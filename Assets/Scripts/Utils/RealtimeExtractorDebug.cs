// --- C# Script Code ---
using UnityEngine;
using UnityEngine.Rendering; // AsyncGPUReadbackRequest, GraphicsFormat
using System.Collections.Generic;
using System;
using System.Text; // StringBuilder
using System.Linq;
using Unity.Collections; // NativeArray
using Unity.Mathematics;
using UnityEngine.Experimental.Rendering; // Vector2Int (or UnityEngine.Vector2Int)


// 이 스크립트를 씬의 카메라 또는 다른 게임 오브젝트에 추가하세요.
public class RealtimeExtractorDebug : MonoBehaviour
{
    // 추출 모드 선택용 Enum
    public enum ExtractionMode { DCT_SS, LSB }

    [Header("추출 설정")]
    public KeyCode extractionKey = KeyCode.F12; // 추출 시작 키
    public ExtractionMode extractionMode = ExtractionMode.DCT_SS; // 추출 모드 선택
    public bool logPixelValues = false; // 디버깅용: 첫 픽셀 값 로깅 여부

    [Header("SS 설정 (DCT_SS 모드 시 사용)")]
    [Tooltip("패턴 생성용 Secret Key (삽입 시와 동일해야 함)")]
    public string ssSecretKey = "default_secret_key_rgb_ss"; // 삽입 시 사용한 키와 일치 필요
    [Tooltip("1블록당 사용하는 AC 계수 수 (삽입 시와 동일해야 함)")]
    [Range(1, 63)]
    public int ssCoefficientsToUse = 10; // 삽입 시 사용한 개수와 일치 필요

    [Header("타겟 렌더 텍스처 (읽기 전용)")]
    [Tooltip("워터마크가 포함된 최종 결과 RenderTexture 참조")]
    public RenderTexture sourceRT; // Inspector 할당 또는 아래 로직으로 찾기

    private bool isRequestPending = false; // 중복 요청 방지 플래그
    private const int BLOCK_SIZE = 8;      // 처리 블록 크기 (HLSL과 일치)
    private const int SYNC_PATTERN_LENGTH = 64; // ★★★ 동기화 패턴 비트 수 (실제 길이에 맞게 수정 필요) ★★★

    // 동기화 패턴 정의 (실제 사용하는 동기화 패턴으로 교체 필요)
    private readonly int[] sync_pattern_cs = new int[SYNC_PATTERN_LENGTH] {
        1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,
        0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,1,1,0,0,1,1,0,0,0,1,1,1,0,1,1,0
    };
    private string expectedSyncPatternString = null; // 비교용 문자열 (미리 생성)

    // --- CPU DCT 계산을 위한 상수 ---
    private const double MATH_PI = 3.141592653589793;
    private readonly double DCT_SQRT1_N = 1.0 / Math.Sqrt(BLOCK_SIZE); // 1/sqrt(8)
    private readonly double DCT_SQRT2_N = Math.Sqrt(2.0 / BLOCK_SIZE); // sqrt(2/8) = 0.5

    void Start()
    {
        // 비교용 동기화 패턴 문자열 미리 생성
        StringBuilder sb = new StringBuilder(SYNC_PATTERN_LENGTH);
        for (int i = 0; i < SYNC_PATTERN_LENGTH; ++i)
        {
            if (i < sync_pattern_cs.Length) sb.Append(sync_pattern_cs[i]); else sb.Append('X');
        }
        expectedSyncPatternString = sb.ToString();
        if (sync_pattern_cs.Length != SYNC_PATTERN_LENGTH)
        {
            Debug.LogWarning($"sync_pattern_cs 배열 길이({sync_pattern_cs.Length})와 SYNC_PATTERN_LENGTH({SYNC_PATTERN_LENGTH})가 일치하지 않습니다!");
        }
        Debug.Log($"예상 동기화 패턴 (첫 {SYNC_PATTERN_LENGTH}비트): {expectedSyncPatternString}");
    }

    void Update()
    {
        if (Input.GetKeyDown(extractionKey) && !isRequestPending)
        {
            if (sourceRT == null)
            { /* ... sourceRT 찾기 로직 ... */
                if (RTResultHolder.DedicatedSaveTarget != null && RTResultHolder.DedicatedSaveTarget.rt != null)
                {
                    sourceRT = RTResultHolder.DedicatedSaveTarget.rt; Debug.Log($"Source RT found via DCTResultHolder: {sourceRT.name}");
                }
                else { Debug.LogError("Source RenderTexture를 찾을 수 없습니다!"); return; }
            }
            if (sourceRT == null || !sourceRT.IsCreated() || sourceRT.width <= 0 || sourceRT.height <= 0)
            {
                Debug.LogError($"Source RenderTexture가 유효하지 않습니다! Name: {sourceRT?.name}, Created: {sourceRT?.IsCreated()}, Size: {sourceRT?.width}x{sourceRT?.height}"); return;
            }

            isRequestPending = true;

            // ★★★ 추출 방식에 관계없이 최종 이미지를 읽지만, 데이터 형식은 모드에 따라 다르게 요청 ★★★
            GraphicsFormat requestedGraphicsFormat = (extractionMode == ExtractionMode.LSB)
                                                 ? sourceRT.graphicsFormat // LSB는 원본 포맷 그대로 읽기 시도
                                                 : GraphicsFormat.R32G32B32A32_SFloat; // DCT_SS는 float 데이터 필요

            Debug.Log($"[{GetType().Name}] GPU Async Readback 요청 (Mode: {extractionMode}, Target: {sourceRT.name}, Format: {requestedGraphicsFormat})...");
            AsyncGPUReadback.Request(sourceRT, 0, requestedGraphicsFormat, OnReadbackComplete);
        }
    }

    // GPU Readback 완료 콜백
    void OnReadbackComplete(AsyncGPUReadbackRequest request)
    {
        Debug.Log($"[{GetType().Name}] Async Readback Complete.");
        if (request.hasError || request.layerCount <= 0)
        {
            Debug.LogError($"GPU Readback Error! HasError: {request.hasError}, LayerCount: {request.layerCount}");
            isRequestPending = false; return;
        }
        int width = request.width; int height = request.height;
        if (width <= 0 || height <= 0)
        {
            Debug.LogError($"GPU Readback invalid dimensions: {width}x{height}");
            isRequestPending = false; return;
        }

        List<int> extractedBits = new List<int>();
        List<uint> expectedFullBits = new List<uint>();
        int totalBlocks = 0; // 실제 처리/비교할 블록 수

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            totalBlocks = ((width + BLOCK_SIZE - 1) / BLOCK_SIZE) * ((height + BLOCK_SIZE - 1) / BLOCK_SIZE);
            if (totalBlocks > 0 && DataManager.EncryptedOriginData != null)
            {
                // 실제 임베딩 시 사용했을 페이로드 생성
                List<uint> fullPayload = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);
                if (fullPayload != null && fullPayload.Count > 0)
                {
                    // GPU에 전달되었을 비트 수 계산 (totalBlocks 와 페이로드 길이 중 작은 값)
                    int bitsSentToGpu = Math.Min(fullPayload.Count, totalBlocks);
                    // 비교 대상은 GPU에 전달된 비트열
                    expectedFullBits.AddRange(fullPayload.Take(bitsSentToGpu));
                    Debug.Log($"비교 대상 비트 생성 완료: {expectedFullBits.Count} 비트 (Max Blocks: {totalBlocks}, Payload Size: {fullPayload.Count})");
                }
                else { Debug.LogWarning("예상 페이로드 생성 실패 또는 비어 있음."); }
            }
            else { Debug.LogWarning("예상 페이로드 생성 조건 미충족 (totalBlocks 또는 데이터 없음)."); }

            int bitsToExtract = expectedFullBits.Count; // 추출/비교할 길이는 예상 비트 길이 기준
            if (bitsToExtract == 0) throw new Exception("비교할 예상 비트가 없습니다.");
            extractedBits.Capacity = bitsToExtract;

            // --- DCT Spread Spectrum 추출 로직 ---
            if (extractionMode == ExtractionMode.DCT_SS)
            {
                Debug.Log("DCT Spread Spectrum 모드로 비트 추출 시도...");
                Debug.LogWarning("CPU에서 DCT를 수행합니다. 성능이 매우 느릴 수 있습니다!");

                NativeArray<float> pixelData = request.GetData<float>();
                Debug.Log($"Readback data as float array (Length: {pixelData.Length}). Expecting {width * height * 4}.");
                if (logPixelValues && pixelData.Length >= 4) Debug.Log($"[Pixel(0,0)] R={pixelData[0]:G9}, G={pixelData[1]:G9}, B={pixelData[2]:G9}, A={pixelData[3]:G9}");

                // ★★★ 수정: 올림 나눗셈으로 블록 수 계산 ★★★
                int numBlocksX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
                int numBlocksY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
                Debug.Log($"Total Blocks: {totalBlocks} ({numBlocksX}x{numBlocksY}), Bits to Extract: {bitsToExtract}");

                // 패턴 버퍼 생성
                float[] patternBuffer = new float[totalBlocks * ssCoefficientsToUse];
                System.Random prng = new System.Random(ssSecretKey.GetHashCode());
                for (int i = 0; i < patternBuffer.Length; ++i) patternBuffer[i] = (prng.NextDouble() < 0.5) ? -1f : 1f;

                // 지그재그 인덱스
                Vector2Int[] zigzag = { /* ... 이전과 동일 ... */
                    new Vector2Int(0, 1), new Vector2Int(1, 0), new Vector2Int(2, 0), new Vector2Int(1, 1), new Vector2Int(0, 2), new Vector2Int(0, 3), new Vector2Int(1, 2), new Vector2Int(2, 1),
                    new Vector2Int(3, 0), new Vector2Int(4, 0), new Vector2Int(3, 1), new Vector2Int(2, 2), new Vector2Int(1, 3), new Vector2Int(0, 4), new Vector2Int(0, 5), new Vector2Int(1, 4),
                    new Vector2Int(2, 3), new Vector2Int(3, 2), new Vector2Int(4, 1), new Vector2Int(5, 0), new Vector2Int(6, 0), new Vector2Int(5, 1), new Vector2Int(4, 2), new Vector2Int(3, 3),
                    new Vector2Int(2, 4), new Vector2Int(1, 5), new Vector2Int(0, 6), new Vector2Int(0, 7), new Vector2Int(1, 6), new Vector2Int(2, 5), new Vector2Int(3, 4), new Vector2Int(4, 3),
                    new Vector2Int(5, 2), new Vector2Int(6, 1), new Vector2Int(7, 0), new Vector2Int(7, 1), new Vector2Int(6, 2), new Vector2Int(5, 3), new Vector2Int(4, 4), new Vector2Int(3, 5),
                    new Vector2Int(2, 6), new Vector2Int(1, 7), new Vector2Int(2, 7), new Vector2Int(3, 6), new Vector2Int(4, 5), new Vector2Int(5, 4), new Vector2Int(6, 3), new Vector2Int(7, 2),
                    new Vector2Int(7, 3), new Vector2Int(6, 4), new Vector2Int(5, 5), new Vector2Int(4, 6), new Vector2Int(3, 7), new Vector2Int(4, 7), new Vector2Int(5, 6), new Vector2Int(6, 5),
                    new Vector2Int(7, 4), new Vector2Int(7, 5), new Vector2Int(6, 6), new Vector2Int(5, 7), new Vector2Int(6, 7), new Vector2Int(7, 6), new Vector2Int(7, 7)
                    };
                if (ssCoefficientsToUse > zigzag.Length) ssCoefficientsToUse = zigzag.Length;

                // 임시 배열 선언 (CPU DCT용)
                float[,] blockR = new float[BLOCK_SIZE, BLOCK_SIZE];
                float[,] blockG = new float[BLOCK_SIZE, BLOCK_SIZE]; // 필요시 G, B도 처리
                float[,] blockB = new float[BLOCK_SIZE, BLOCK_SIZE];
                
                double[,] dctCoeffsR = new double[BLOCK_SIZE, BLOCK_SIZE]; // DCT 결과 저장용 (double)
                double[,] dctCoeffsG = new double[BLOCK_SIZE, BLOCK_SIZE]; // DCT 결과 저장용 (double)
                double[,] dctCoeffsB = new double[BLOCK_SIZE, BLOCK_SIZE]; // DCT 결과 저장용 (double)

                // 각 블록 처리
                for (int blockIdx = 0; blockIdx < bitsToExtract; ++blockIdx)
                {
                    int blockY = blockIdx / numBlocksX; int blockX = blockIdx % numBlocksX;

                    // ★★★ 1. 현재 블록의 픽셀 데이터 추출 (R 채널만) ★★★
                    for (int y = 0; y < BLOCK_SIZE; ++y)
                    {
                        for (int x = 0; x < BLOCK_SIZE; ++x)
                        {
                            int pixelX = blockX * BLOCK_SIZE + x;
                            int pixelY = blockY * BLOCK_SIZE + y;
                            if (pixelX < width && pixelY < height)
                            {
                                int dataIndex = (pixelY * width + pixelX) * 4; // R 채널 인덱스
                                if (dataIndex < pixelData.Length) blockR[y, x] = pixelData[dataIndex];
                                else blockR[y, x] = 0f;
                            }
                            else { blockR[y, x] = 0f; }
                        }
                    }

                    // ★★★ 2. 추출된 블록에 대해 CPU에서 2D DCT 수행 ★★★
                    DCT2D_CPU(blockR, dctCoeffsR); // R 채널 DCT 수행
                    DCT2D_CPU(blockG, dctCoeffsG); // R 채널 DCT 수행
                    DCT2D_CPU(blockB, dctCoeffsB); // R 채널 DCT 수행

                    // ★★★ 3. 계산된 DCT 계수와 패턴으로 상관관계 계산 ★★★
                    double correlationSumR = 0.0; // double로 계산하여 정밀도 확보
                    double correlationSumG = 0.0;
                    double correlationSumB = 0.0;

                    int patternBaseIndex = blockIdx * ssCoefficientsToUse;
                    for (int i = 0; i < ssCoefficientsToUse; ++i)
                    {
                        Vector2Int uv = zigzag[i]; // 사용할 AC 계수 좌표
                                                   // DCT2D_CPU 결과는 [v, u] 순서로 저장됨
                                                   // DCT2D_CPU 결과는 [v, u] 순서로 저장됨
                        double coeffValueR = dctCoeffsR[uv.y, uv.x];
                        double coeffValueG = dctCoeffsG[uv.y, uv.x];
                        double coeffValueB = dctCoeffsB[uv.y, uv.x];

                        if (patternBaseIndex + i < patternBuffer.Length)
                        {
                            float patternValue = patternBuffer[patternBaseIndex + i];
                            correlationSumR += coeffValueR * patternValue;
                            correlationSumG += coeffValueG * patternValue;
                            correlationSumB += coeffValueB * patternValue;
                        }
                    }

                    // ★★★ 4. 비트 판정 (평균 상관관계 사용) ★★★
                    double finalCorrelation = (correlationSumR + correlationSumG + correlationSumB) / 3.0;
                    extractedBits.Add(finalCorrelation >= 0.0 ? 1 : 0); // double 비교
                    // end for blockIdx

                }

            }
            // --- LSB 추출 로직 ---
            else
            { // LSB
                Debug.Log("LSB 모드로 비트 추출 시도...");
                NativeArray<byte> byteData = request.GetData<byte>();
                 /* ... 이전 LSB 로직과 거의 동일 ... */
                    int bytesPerPixel = (int)GraphicsFormatUtility.GetBlockSize(sourceRT.graphicsFormat);
                    // ... (로그 및 비트 수 계산) ...
                    extractedBits.Capacity = bitsToExtract;

                    int blueChannelByteOffset = 2; // 기본값 (RGBA32 가정) - 포맷 따라 조정 필요!
                    var sourceFormat = sourceRT.graphicsFormat;
                    if (sourceFormat == GraphicsFormat.B8G8R8A8_SRGB || sourceFormat == GraphicsFormat.B8G8R8A8_UNorm) blueChannelByteOffset = 0;
                    // ... 다른 포맷 검사 ...

                    for (int i = 0; i < bitsToExtract; ++i)
                    {
                        int pixelY = i / width; int pixelX = i % width;
                        int byteBaseIndex = (pixelY * width + pixelX) * bytesPerPixel;
                        int blueByteIndex = byteBaseIndex + blueChannelByteOffset;
                        if (blueByteIndex < byteData.Length) extractedBits.Add(byteData[blueByteIndex] & 1);
                        else extractedBits.Add(0);
                    }
                
            }

            stopwatch.Stop();
            Debug.Log($"CPU 처리 시간: {stopwatch.Elapsed.TotalMilliseconds:F2} ms ({extractedBits.Count} 비트 처리).");

            // --- 결과 비교 및 출력 ---
            CompareAgainstFullExpected(extractedBits, expectedFullBits);

        }
        catch (Exception e) { Debug.LogError($"추출/처리 중 오류 발생: {e.Message}\n{e.StackTrace}"); }
        finally { isRequestPending = false; }
    }

    // 동기화 패턴 비교 및 로깅 함수
    private void CompareAndLogSyncPattern(List<int> bits)
    { /* ... 이전과 동일 ... */
        if (bits == null || bits.Count == 0) { Debug.LogError("추출된 비트가 없습니다!"); return; }
        int compareLength = Math.Min(bits.Count, 1532);
        if (compareLength == 0) { Debug.LogError("비교할 비트가 없습니다!"); return; }

        StringBuilder extractedSb = new StringBuilder(compareLength);
        bool match = true; int mismatchCount = 0; StringBuilder diffMarker = new StringBuilder(compareLength);
        for (int i = 0; i < compareLength; ++i)
        {
            extractedSb.Append(bits[i]);
            if (i < sync_pattern_cs.Length && bits[i] != sync_pattern_cs[i]) { match = false; mismatchCount++; diffMarker.Append("^"); }
            else { diffMarker.Append(" "); }
        }
        string extractedPattern = extractedSb.ToString();
        string expectedSubPattern = "111100001111000010101010101010100011001100110011110011000111011000000110000000000110000100011110000101011011000110111001111000010101101011010010110111001100111111011101010101111001010111100100110000101010101011001110111111011100000101000011011001011110000111100110000001100111111010011110010100111001111001111001100001011101101110111001100000000000101011100101111101110011001110011000000010000010100101001110111110001100010111001000001110001110101001000101011011010100111110000111100110110001000101111011101110011010111111010011011011000000110100110111011100111011011111010100010001011110010111100111011101001000011111010100011010110100011101001101010110011110010110100000000001101111010011000110000101111011010110001111110101110111010011010101111010101101111110100110111001110001100101101010101100100011011100101011001010010001010001101111111110100010011111110011001100100001000101111110111100001001010100011011100001111000101100001001001101100001110100011101001101100011000010001100110011101000011100111100101101111001100101011011101011100110001110100100010011010111011101100010111011010011001110000111111111011111010000001110110101000101000101101000001000100110111100111001001000001001100000001100000101101000000001010111110100101010011000111011100001100110110100100010100010001011101010110001010100001011001100001000001110100001001010111011001110000100101011110001011110000010100110100101000010001010000001101010111011001011000111011100101101100001000111100000110100000001100010010001001010111101001101101000110100100010001011011011";


        Debug.Log($"추출된 패턴 ({compareLength} 비트): {extractedPattern}");
        Debug.Log($"예상 패턴 ({compareLength} 비트): {expectedSubPattern}");
        if (match && compareLength == SYNC_PATTERN_LENGTH) { Debug.Log($"<color=green>동기화 패턴 {compareLength} 비트 일치!</color>"); }
        else
        {
            Debug.LogError($"<color=red>동기화 패턴 불일치! ({mismatchCount} / {compareLength} 비트 다름)</color>");
            int markerLength = Math.Min(diffMarker.Length, 1532);
            Debug.Log($"불일치 위치: {diffMarker.ToString().Substring(0, markerLength)}{(diffMarker.Length > markerLength ? "..." : "")}");
        }
    }

    private void CompareAgainstFullExpected(List<int> extracted, List<uint> expected)
    {
        if (extracted == null || extracted.Count == 0) { Debug.LogError("추출된 비트가 없습니다!"); return; }
        if (expected == null || expected.Count == 0) { Debug.LogError("비교할 예상 비트가 없습니다!"); return; }

        int compareLength = Math.Min(extracted.Count, expected.Count); // 실제 비교 가능한 길이
        Debug.Log($"Comparing {compareLength} bits...");

        StringBuilder extractedSb = new StringBuilder(compareLength);
        StringBuilder expectedSb = new StringBuilder(compareLength);
        StringBuilder diffMarker = new StringBuilder(compareLength);
        bool match = true;
        int mismatchCount = 0;

        for (int i = 0; i < compareLength; ++i)
        {
            extractedSb.Append(extracted[i]);
            expectedSb.Append(expected[i]);
            if (extracted[i] != expected[i])
            {
                match = false;
                mismatchCount++;
                diffMarker.Append("^");
            }
            else
            {
                diffMarker.Append(" ");
            }
        }

        int displayLength = Math.Min(compareLength, 120); // 콘솔 출력 길이 제한
        Debug.Log($"추출된 비트 (처음 {displayLength}개): {extractedSb.ToString().Substring(0, displayLength)}{(compareLength > displayLength ? "..." : "")}");
        Debug.Log($"예상 비트 (처음 {displayLength}개): {expectedSb.ToString().Substring(0, displayLength)}{(compareLength > displayLength ? "..." : "")}");

        if (match)
        {
            Debug.Log($"<color=green>전체 {compareLength} 비트 일치!</color>");
        }
        else
        {
            Debug.LogError($"<color=red>비트 불일치! ({mismatchCount} / {compareLength} 비트 다름)</color>");
            int markerLength = Math.Min(diffMarker.Length, 120);
            Debug.Log($"불일치 위치: {diffMarker.ToString().Substring(0, markerLength)}{(diffMarker.Length > markerLength ? "..." : "")}");
        }
    }

    // =============================================================
    // --- CPU 기반 2D DCT 함수 ---
    // =============================================================

    // 1D DCT (CPU, double precision) - HLSL의 DCT_1D_Single과 동일 로직
    private void DCT1D_CPU(double[] input, double[] output)
    {
        int N = input.Length;
        if (N != BLOCK_SIZE || output.Length != BLOCK_SIZE)
        {
            Debug.LogError("DCT1D_CPU: Input/Output array size must be BLOCK_SIZE.");
            return;
        }
        double pi_div_2N = MATH_PI / (2.0 * N); // 상수 미리 계산

        for (int k = 0; k < N; ++k)
        {
            double sum = 0.0;
            double Ck = (k == 0) ? DCT_SQRT1_N : DCT_SQRT2_N; // 스케일링 상수

            for (int n = 0; n < N; ++n)
            {
                // Math.Cos의 인자는 라디안
                sum += input[n] * Math.Cos((2.0 * n + 1.0) * k * pi_div_2N);
            }
            output[k] = Ck * sum;
        }
    }

    // 2D DCT (CPU, double precision) - 입력은 float[,] 사용
    // blockData: 입력 8x8 픽셀 블록 (float)
    // dctCoeffs: 출력 8x8 DCT 계수 (double)
    private void DCT2D_CPU(float[,] blockData, double[,] dctCoeffs)
    {
        int rows = blockData.GetLength(0);
        int cols = blockData.GetLength(1);
        if (rows != BLOCK_SIZE || cols != BLOCK_SIZE || dctCoeffs.GetLength(0) != BLOCK_SIZE || dctCoeffs.GetLength(1) != BLOCK_SIZE)
        {
            Debug.LogError("DCT2D_CPU: Input/Output array dimensions must be BLOCK_SIZE x BLOCK_SIZE.");
            return;
        }

        // 임시 배열 (double 사용)
        double[] tempRowInput = new double[BLOCK_SIZE];
        double[] tempRowOutput = new double[BLOCK_SIZE];
        double[,] tempBuffer = new double[BLOCK_SIZE, BLOCK_SIZE]; // 행 DCT 결과 저장용

        // 1단계: 행(Row) 방향 1D DCT 수행
        for (int i = 0; i < BLOCK_SIZE; ++i) // 각 행에 대해
        {
            // 현재 행 데이터를 double 배열로 복사
            for (int j = 0; j < BLOCK_SIZE; ++j) { tempRowInput[j] = (double)blockData[i, j]; }
            // 1D DCT 수행
            DCT1D_CPU(tempRowInput, tempRowOutput);
            // 결과를 임시 버퍼에 저장
            for (int j = 0; j < BLOCK_SIZE; ++j) { tempBuffer[i, j] = tempRowOutput[j]; }
        }

        // 임시 배열 (double 사용)
        double[] tempColInput = new double[BLOCK_SIZE];
        double[] tempColOutput = new double[BLOCK_SIZE];

        // 2단계: 열(Column) 방향 1D DCT 수행
        for (int j = 0; j < BLOCK_SIZE; ++j) // 각 열에 대해
        {
            // 임시 버퍼에서 현재 열 데이터를 double 배열로 복사
            for (int i = 0; i < BLOCK_SIZE; ++i) { tempColInput[i] = tempBuffer[i, j]; }
            // 1D DCT 수행
            DCT1D_CPU(tempColInput, tempColOutput);
            // 최종 결과를 출력 배열 dctCoeffs에 저장 (v, u 순서 = i, j 순서)
            for (int i = 0; i < BLOCK_SIZE; ++i) { dctCoeffs[i, j] = tempColOutput[i]; }
        }
    }

    // 클래스 종료
}