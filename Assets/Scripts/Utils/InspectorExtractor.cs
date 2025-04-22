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
public class InspectorExtractorDebug : MonoBehaviour
{
    // 추출 모드 선택용 Enum
    public enum ExtractionMode { LSB, DCT_SS, DWT, SVD }

    [Header("추출 설정")]
    public KeyCode extractionKey = KeyCode.F10; // 추출 시작 키
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
    public Texture2D sourceTexture2d; // Inspector 할당 또는 아래 로직으로 찾기

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
            if (sourceTexture2d == null || sourceTexture2d.width <= 0 || sourceTexture2d.height <= 0)
            {
                Debug.LogError($"Source RenderTexture가 유효하지 않습니다! Name: {sourceTexture2d?.name}, Size: {sourceTexture2d?.width}x{sourceTexture2d?.height}"); return;
            }

            isRequestPending = true;

            // ★★★ 추출 방식에 관계없이 최종 이미지를 읽지만, 데이터 형식은 모드에 따라 다르게 요청 ★★★
            GraphicsFormat requestedGraphicsFormat = (extractionMode == ExtractionMode.LSB)
                                                 ? sourceTexture2d.graphicsFormat // LSB는 원본 포맷 그대로 읽기 시도
                                                 : GraphicsFormat.R32G32B32A32_SFloat; // DCT_SS는 float 데이터 필요

            Debug.Log($"[{GetType().Name}] GPU Async Readback 요청 (Mode: {extractionMode}, Target: {sourceTexture2d.name}, Format: {requestedGraphicsFormat})...");
            AsyncGPUReadback.Request(sourceTexture2d, 0, requestedGraphicsFormat, OnReadbackComplete);
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
                Debug.LogWarning("이미지 데이터에서 읽어오는 중...");

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
                int bytesPerPixel = (int)GraphicsFormatUtility.GetBlockSize(sourceTexture2d.graphicsFormat);
                // ... (로그 및 비트 수 계산) ...
                extractedBits.Capacity = bitsToExtract;

                int blueChannelByteOffset = 2; // 기본값 (RGBA32 가정) - 포맷 따라 조정 필요!
                var sourceFormat = sourceTexture2d.graphicsFormat;
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

            // 1. 비교 대상이 될 전체 예상 비트스트림 생성 (헤더 포함)
            if (expectedFullBits == null || expectedFullBits.Count == 0)
            {
                Debug.LogError("예상 비트스트림을 생성할 수 없습니다!");
            }
            else
            {
                // 2. 동기화 패턴 탐색 및 페이로드 검증 함수 호출
                int syncStartIndex = FindValidatedWatermarkStartIndex(extractedBits, expectedFullBits, sync_pattern_cs);

                // 3. 결과 처리 (예: 성공 시 페이로드 사용)
                if (syncStartIndex != -1)
                {
                    int payloadStartIndex = syncStartIndex + SYNC_PATTERN_LENGTH;
                    int payloadLength = extractedBits.Count - payloadStartIndex;
                    if (payloadLength > 0)
                    {
                        Debug.Log($"성공적으로 동기화됨. 인덱스 {payloadStartIndex} 부터 {payloadLength} 비트의 페이로드 사용 가능.");
                        // TODO: 여기서 payloadBits를 실제 데이터로 변환하는 등의 작업 수행
                        // List<int> payloadBits = extractedBits.GetRange(payloadStartIndex, payloadLength);
                    }
                }
                // else: 함수 내부에서 이미 실패 로그 출력됨
            }

            stopwatch.Stop();
            Debug.Log($"CPU 처리 시간: {stopwatch.Elapsed.TotalMilliseconds:F2} ms ({extractedBits.Count} 비트 처리).");

        }
        catch (Exception e) { Debug.LogError($"추출/처리 중 오류 발생: {e.Message}\n{e.StackTrace}"); }
        finally { isRequestPending = false; }
    }


    /// <summary>
    /// 추출된 비트열 내에서 동기화 패턴을 탐색하고, 찾으면 페이로드 부분까지 비교하여
    /// 동기화 패턴과 페이로드가 모두 일치하는 첫 번째 시작 인덱스를 반환합니다.
    /// </summary>
    /// <param name="extractedBits">GPU에서 추출된 전체 비트 리스트</param>
    /// <param name="expectedFullPayloadWithHeader">예상되는 전체 비트 리스트 (동기화 패턴 포함)</param>
    /// <param name="syncPattern">동기화 패턴 int 배열</param>
    /// <returns>검증된 동기화 패턴의 시작 인덱스 (0부터 시작). 유효한 패턴을 찾지 못하면 -1 반환.</returns>
    private int FindValidatedWatermarkStartIndex(List<int> extractedBits, List<uint> expectedFullPayloadWithHeader, int[] syncPattern)
    {
        int syncPatternLength = syncPattern.Length;

        // --- 입력 유효성 검사 ---
        if (extractedBits == null || expectedFullPayloadWithHeader == null || syncPattern == null || syncPatternLength == 0)
        {
            Debug.LogError("[FindValidatedWatermarkStartIndex] 입력 데이터가 유효하지 않습니다.");
            return -1; // 오류 코드 -1
        }
        // 최소한 동기화 패턴 길이만큼의 비트는 추출되어야 함
        if (extractedBits.Count < syncPatternLength)
        {
            Debug.LogWarning($"추출된 비트 수({extractedBits.Count})가 동기화 패턴 길이({syncPatternLength})보다 짧아 탐색이 불가능합니다.");
            return -1;
        }
        // 예상 페이로드에도 최소한 동기화 패턴은 있어야 함
        if (expectedFullPayloadWithHeader.Count < syncPatternLength)
        {
            Debug.LogWarning($"예상 페이로드 길이({expectedFullPayloadWithHeader.Count})가 동기화 패턴 길이({syncPatternLength})보다 짧습니다.");
            // 이 경우 동기화 패턴만 비교할 수도 있으나, 현재 로직은 페이로드 검증까지 하므로 실패 처리
            return -1;
        }

        // --- 동기화 패턴 탐색 루프 ---
        // extractedBits 리스트에서 syncPattern이 시작될 수 있는 마지막 위치까지만 탐색
        int maxSearchStart = extractedBits.Count - syncPatternLength;
        Debug.Log($"동기화 패턴 탐색 시작 (추출된 비트 수: {extractedBits.Count}, 최대 {maxSearchStart + 1} 위치 확인)...");
        System.Diagnostics.Stopwatch searchStopwatch = System.Diagnostics.Stopwatch.StartNew(); // 탐색 시간 측정

        for (int i = 0; i <= maxSearchStart; ++i) // i는 현재 탐색 중인 동기화 패턴의 시작 인덱스
        {
            // 1. 현재 위치(i)에서 시작하는 부분이 동기화 패턴과 일치하는지 확인
            bool isSyncMatch = true;
            for (int j = 0; j < syncPatternLength; ++j)
            {
                if (extractedBits[i + j] != syncPattern[j])
                {
                    isSyncMatch = false; // 하나라도 다르면 불일치
                    break; // 내부 루프 탈출
                }
            }

            // 2. 동기화 패턴이 일치했다면, 뒤따르는 페이로드도 검증
            if (isSyncMatch)
            {
                Debug.Log($"인덱스 {i}에서 동기화 패턴 후보 발견! 페이로드 검증 시작...");

                int payloadStartIndexInExtracted = i + syncPatternLength; // 추출된 비트에서 페이로드 시작 위치
                int payloadStartIndexInExpected = syncPatternLength;    // 예상 비트열에서 페이로드 시작 위치
                int availableExtractedPayload = extractedBits.Count - payloadStartIndexInExtracted; // 추출된 비트 중 남은 페이로드 길이
                int expectedPayloadLength = expectedFullPayloadWithHeader.Count - payloadStartIndexInExpected; // 예상 페이로드 길이
                int comparePayloadLength = Math.Min(availableExtractedPayload, expectedPayloadLength); // 비교할 실제 페이로드 길이

                // 비교할 페이로드가 있는지 확인
                if (comparePayloadLength <= 0)
                {
                    Debug.LogWarning($"인덱스 {i}에서 동기화 패턴은 일치했으나 비교할 페이로드가 없습니다. (추출된 페이로드 길이: {availableExtractedPayload}, 예상 페이로드 길이: {expectedPayloadLength})");
                    // 동기화 패턴만 맞으면 성공으로 간주할지, 아니면 실패로 간주하고 계속 탐색할지 정책 결정 필요
                    // 여기서는 페이로드 검증 실패로 보고 계속 탐색 (continue)
                    continue; // 다음 i 로 넘어감
                }

                // 페이로드 비교 시작
                bool isPayloadMatch = true;
                int payloadMismatchCount = 0;
                StringBuilder diffMarker = new StringBuilder(comparePayloadLength); // 오류 위치 표시용

                for (int j = 0; j < comparePayloadLength; ++j)
                {
                    int extractedBit = extractedBits[payloadStartIndexInExtracted + j];
                    // expectedFullPayloadWithHeader는 uint 리스트이므로 int로 캐스팅 필요
                    uint expectedBitUint = expectedFullPayloadWithHeader[payloadStartIndexInExpected + j];
                    int expectedBit = (int)expectedBitUint;

                    if (extractedBit != expectedBit)
                    {
                        isPayloadMatch = false;
                        payloadMismatchCount++;
                        diffMarker.Append("^");
                        // 많은 오류가 예상되면 여기서 break 하여 성능 향상 가능
                        // if (payloadMismatchCount > 10) break; // 예: 10개 이상 틀리면 더 비교 안 함
                    }
                    else
                    {
                        diffMarker.Append(" ");
                    }
                }

                // 페이로드 검증 결과 확인
                if (isPayloadMatch) // 페이로드까지 모두 일치!
                {
                    searchStopwatch.Stop(); // 탐색 시간 측정 종료
                    Debug.Log($"<color=lime>인덱스 {i}에서 동기화 패턴 및 페이로드 ({comparePayloadLength}비트) 완전 일치 확인! 최종 성공.</color> (탐색 시간: {searchStopwatch.Elapsed.TotalMilliseconds:F2} ms)");
                    return i; // 성공! 동기화 패턴이 시작된 인덱스(i) 반환
                }
                else // 동기화 패턴은 맞았으나 페이로드가 틀림 (False Positive)
                {
                    Debug.LogWarning($"인덱스 {i}에서 동기화 패턴은 일치했으나 페이로드가 불일치합니다 ({payloadMismatchCount}/{comparePayloadLength} 비트 다름). 계속 탐색...");
                    int displayLength = Math.Min(comparePayloadLength, 120);
                    Debug.Log($"페이로드 불일치 위치: {diffMarker.ToString().Substring(0, displayLength)}{(comparePayloadLength > displayLength ? "..." : "")}");
                    // isSyncMatch는 여전히 true 상태이므로, 외부 루프는 다음 i로 진행됨
                }
            }
            // else: isSyncMatch가 false이면 다음 i로 넘어감
        } // end for i (탐색 루프)

        // 루프를 다 돌았는데도 검증된 패턴을 찾지 못함
        searchStopwatch.Stop();
        Debug.LogError($"<color=orange>추출된 비트열 전체에서 유효한 동기화 패턴 + 페이로드를 찾지 못했습니다.</color> (탐색 시간: {searchStopwatch.Elapsed.TotalMilliseconds:F2} ms)");
        return -1; // 최종 실패
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