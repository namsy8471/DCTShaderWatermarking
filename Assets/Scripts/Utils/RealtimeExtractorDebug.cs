using UnityEngine;
using UnityEngine.Rendering; // AsyncGPUReadbackRequest, CommandBuffer 등 + GraphicsFormat 포함!
using UnityEngine.Rendering.Universal; // ScriptableRendererFeature, ScriptableRenderPass 등
using System.IO;
using UnityEngine.Experimental.Rendering;
using Unity.Collections; // NativeArray 사용
using System;
using System.Text; // StringBuilder 사용
using System.Collections.Generic; // List 사용
using System.Linq; // Take, Select 등 Linq 사용 시 (필요 없다면 제거 가능)

// ★★★ 참고: DCTResultHolder 클래스는 프로젝트 내 어딘가에 정의되어 있어야 함 (이전 답변 #62 참고) ★★★

public static class RTResultHolder
{
    public static RTHandle DedicatedSaveTarget;
    public static RenderTextureDescriptor SaveTargetDesc;
}


// 이 스크립트를 씬의 카메라 또는 다른 게임 오브젝트에 추가하세요.
public class RealtimeExtractorDebug : MonoBehaviour
{
    // 추출 모드 선택용 Enum
    public enum ExtractionMode { DCT_QIM, LSB }

    [Header("추출 설정")]
    public KeyCode extractionKey = KeyCode.F12; // 추출 시작 키
    public ExtractionMode extractionMode = ExtractionMode.DCT_QIM; // ★★★ 추출 모드 선택 ★★★

    [Header("DCT(QIM) 설정")]
    [Tooltip("데이터를 추출할 DCT 계수 U 좌표 (DCT 모드 시, 셰이더와 일치)")]
    public int uIndex = 4;
    [Tooltip("데이터를 추출할 DCT 계수 V 좌표 (DCT 모드 시, 셰이더와 일치)")]
    public int vIndex = 4;
    [Tooltip("QIM Delta 값 (DCT 모드 시, 셰이더와 정확히 일치)")]
    public float qimDelta = 0.05f;

    // LSB 설정은 별도 파라미터 없음 (Blue 채널 사용 가정)

    [Header("타겟 렌더 텍스처 (읽기 전용)")]
    [Tooltip("DCT/LSB Pass가 최종 결과를 저장하는 RenderTexture (예: DCTResultHolder.DedicatedSaveTarget)")]
    public RenderTexture sourceRT; // Inspector 할당 또는 아래 로직으로 찾기

    private bool isRequestPending = false; // 중복 요청 방지
    private const int BLOCK_SIZE = 8;
    private const int SYNC_PATTERN_LENGTH = 64; // 동기화 패턴 비트 수

    // Python의 sync_pattern_py 와 동일한 값
    private readonly int[] sync_pattern_cs = {
        1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0,1,0,1,0,1,0,1,0,
        1,0,1,0,1,0,1,0,0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,
        1,1,0,0,1,1,0,0,0,1,1,1,0,1,1,0
    };

    void Update()
    {
        if (Input.GetKeyDown(extractionKey) && !isRequestPending)
        {
            // 소스 RT 확인 및 참조 (DCTResultHolder 사용 권장)
            if (sourceRT == null)
            {
                if (RTResultHolder.DedicatedSaveTarget != null && RTResultHolder.DedicatedSaveTarget.rt != null)
                {
                    sourceRT = RTResultHolder.DedicatedSaveTarget.rt;
                    Debug.Log($"Source RT found via DCTResultHolder: {sourceRT.name}");
                }
                else
                {
                    Debug.LogError("Source RenderTexture를 찾을 수 없습니다! Inspector에 할당하거나 DCTResultHolder를 확인하세요.");
                    return;
                }
            }
            if (sourceRT == null || !sourceRT.IsCreated())
            {
                Debug.LogError("Source RenderTexture가 유효하지 않습니다!");
                return;
            }

            isRequestPending = true;

            // 모드에 따라 요청 포맷 결정
            TextureFormat requestedFormat = (extractionMode == ExtractionMode.LSB)
                                            ? TextureFormat.RGBA32  // LSB는 8비트 정수
                                            : TextureFormat.RGBAFloat; // DCT/QIM은 float

            Debug.Log($"[{this.GetType().Name}] Starting Async GPU Readback for {extractionMode} extraction (Format: {requestedFormat})...");
            AsyncGPUReadback.Request(sourceRT, 0, requestedFormat, this.OnReadbackComplete);
        }
    }

    // GPU Readback 완료 시 호출될 콜백 함수
    void OnReadbackComplete(AsyncGPUReadbackRequest request)
    {
        Debug.Log($"[{this.GetType().Name}] Async Readback Complete.");
        if (request.hasError || !request.done)
        {
            Debug.LogError("GPU Readback Error or not done!"); isRequestPending = false; return;
        }

        int width = request.width;
        int height = request.height;
        List<int> extractedBits = new List<int>();
        // CPU 연산 시간 측정
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            
            if (this.extractionMode == ExtractionMode.DCT_QIM)     // ARGBFloat
            {
                // --- DCT/QIM 처리 로직 ---
                if (extractionMode != ExtractionMode.DCT_QIM)
                {
                    Debug.LogWarning("Data format is Float but mode is not DCT/QIM. Processing as DCT/QIM.");
                }
                Debug.Log("Processing as DCT/QIM (Float data)...");
                NativeArray<float> data = request.GetData<float>();
                int expectedFloatLength = width * height * 4;
                if (data.Length != expectedFloatLength) throw new Exception($"Float data size mismatch! Expected: {expectedFloatLength}, Got: {data.Length}");

                // --- ★★★ (0,0) 픽셀 Raw RGB 값 로깅 ★★★ ---
                if (width > 0 && height > 0 && data.IsCreated && data.Length >= 4)
                {
                    int idx = 0; // (0,0) 픽셀은 인덱스 0부터 시작 (RGBA)
                    Debug.Log($"\n--- C# (RealtimeExtractorDebug): Pixel (0,0) RAW RGB ---");
                    // "R" 포맷 지정자 사용 (float의 전체 정밀도 출력 시도)
                    Debug.Log($"R={data[idx]:R}, G={data[idx + 1]:R}, B={data[idx + 2]:R}, A={data[idx + 3]:R}");
                    Debug.Log("-------------------------------------------------------\n");
                }
                // --- ★★★ 로깅 끝 ★★★ ---

                int numBlocksX = width / BLOCK_SIZE;
                int numBlocksY = height / BLOCK_SIZE;
                int totalBlocks = numBlocksX * numBlocksY;
                int bitsToExtract = Math.Min(totalBlocks, SYNC_PATTERN_LENGTH); // 동기화 패턴까지만 확인
                extractedBits.Capacity = bitsToExtract;

                float[,] yBlock = new float[BLOCK_SIZE, BLOCK_SIZE];
                double[,] dctCoeffs = new double[BLOCK_SIZE, BLOCK_SIZE]; // CPU DCT는 double 사용

                for (int blockIndex = 0; blockIndex < bitsToExtract; ++blockIndex)
                {
                    int blockR = blockIndex / numBlocksX; int blockC = blockIndex % numBlocksX;
                    // Y 채널 추출
                    for (int y = 0; y < BLOCK_SIZE; ++y)
                    {
                        for (int x = 0; x < BLOCK_SIZE; ++x)
                        {
                            int pixelX = blockC * BLOCK_SIZE + x; int pixelY = blockR * BLOCK_SIZE + y;
                            int dataIndex = (pixelY * width + pixelX) * 4; // RGBA 순서
                            if (dataIndex + 2 < data.Length)
                            {
                                yBlock[y, x] = 0.299f * data[dataIndex + 0] + 0.587f * data[dataIndex + 1] + 0.114f * data[dataIndex + 2];
                            }
                            else { yBlock[y, x] = 0f; }
                        }
                    }
                    // 2D DCT 계산
                    DCT2D_CPU(yBlock, dctCoeffs);
                    // QIM 비트 추출
                    double receivedCoeff = dctCoeffs[vIndex, uIndex]; // [행(v), 열(u)]
                    int extractedBit = ExtractQIMBit_CPU(receivedCoeff, (double)qimDelta);
                    if (extractedBit == -1) { Debug.LogError($"QIM 추출 실패 {blockIndex}"); extractedBits.Add(-1); }
                    else { extractedBits.Add(extractedBit); }
                }
            }
            else
            {
                // --- LSB 처리 로직 ---
                if (extractionMode != ExtractionMode.LSB)
                {
                    Debug.LogWarning("Data format is 8bit Integer but mode is not LSB. Processing as LSB.");
                }
                Debug.Log("Processing as LSB (8bit integer data)...");
                NativeArray<byte> data = request.GetData<byte>();
                int expectedByteLength = width * height * 4; // RGBA32 기준
                if (data.Length != expectedByteLength) throw new Exception($"Byte data size mismatch! Expected: {expectedByteLength}, Got: {data.Length}");

                int bitsToExtract = Math.Min(width * height, SYNC_PATTERN_LENGTH); // 동기화 패턴까지만 확인
                extractedBits.Capacity = bitsToExtract;

                // LSB 추출 (Blue 채널)
                for (int i = 0; i < bitsToExtract; ++i)
                {
                    int pixelY = i / width;
                    int pixelX = i % width;
                    int byteIndex = (pixelY * width + pixelX) * 4; // RGBA 순서 가정

                    // Blue 채널 인덱스 = 2 (RGBA 순서일 때)
                    // BGRA 포맷이면 인덱스 = 0
                    // 여기서는 RGBA로 가정
                    byte blueByte = data[byteIndex + 2];
                    extractedBits.Add(blueByte & 1); // 최하위 비트 추출
                }

            }

            stopwatch.Stop();
            Debug.Log($"CPU processing took: {stopwatch.ElapsedMilliseconds} ms for {extractedBits.Count} bits.");

            // --- 결과 비교 및 출력 ---
            if (extractedBits.Count < SYNC_PATTERN_LENGTH)
            {
                Debug.LogError($"추출된 비트 수가 너무 적습니다 ({extractedBits.Count} < {SYNC_PATTERN_LENGTH})");
            }
            else
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < SYNC_PATTERN_LENGTH; ++i) { sb.Append(extractedBits[i]); }
                string extractedPattern = sb.ToString();
                string expectedPattern = "";
                for (int i = 0; i < SYNC_PATTERN_LENGTH; ++i) { expectedPattern += sync_pattern_cs[i]; }

                Debug.Log($"추출된 동기화 패턴 ({SYNC_PATTERN_LENGTH} 비트): {extractedPattern}");
                Debug.Log($"예상 동기화 패턴 ({SYNC_PATTERN_LENGTH} 비트): {expectedPattern}");
                if (extractedPattern == expectedPattern) Debug.Log("<color=green>동기화 패턴 일치!</color>");
                else
                {
                    Debug.LogError("<color=red>동기화 패턴 불일치!</color>");
                    string diffMarker = "";
                    for (int i = 0; i < SYNC_PATTERN_LENGTH; ++i) { diffMarker += (extractedBits[i] == sync_pattern_cs[i]) ? " " : "^"; }
                    Debug.Log($"불일치 위치            : {diffMarker}");
                }
            }

        }
        catch (Exception e) { Debug.LogError($"추출/처리 중 오류 발생: {e.Message}\n{e.StackTrace}"); }
        finally { isRequestPending = false; }
    }


    // --- CPU 기반 DCT/QIM 함수 (double precision) ---
    // 이전 답변 #88에서 가져옴
    private const double MATH_PI = 3.141592653589793;
    private readonly double DCT_SQRT1_N = 1.0 / Math.Sqrt(BLOCK_SIZE);
    private readonly double DCT_SQRT2_N = Math.Sqrt(2.0 / BLOCK_SIZE);

    // 1D DCT (CPU, double precision)
    private void DCT1D_CPU(double[] input, double[] output)
    {
        int N = input.Length;
        if (N != BLOCK_SIZE) return; // Basic check
        double pi_n = MATH_PI / (2.0 * N);
        for (int k = 0; k < N; ++k)
        {
            double sum = 0.0;
            double Ck = (k == 0) ? DCT_SQRT1_N : DCT_SQRT2_N;
            for (int n = 0; n < N; ++n)
            {
                sum += input[n] * Math.Cos(k * (2.0 * n + 1.0) * pi_n);
            }
            output[k] = Ck * sum;
        }
    }

    // 2D DCT (CPU, double precision)
    // 입력을 float[,] 받고 내부에서 double 사용
    private void DCT2D_CPU(float[,] blockY, double[,] dctCoeffs)
    {
        int rows = blockY.GetLength(0);
        int cols = blockY.GetLength(1);
        if (rows != BLOCK_SIZE || cols != BLOCK_SIZE) return;

        double[] rowInput = new double[BLOCK_SIZE];
        double[] rowOutput = new double[BLOCK_SIZE];
        double[,] temp = new double[BLOCK_SIZE, BLOCK_SIZE];

        // Row-wise DCT
        for (int i = 0; i < BLOCK_SIZE; ++i)
        {
            for (int j = 0; j < BLOCK_SIZE; ++j) { rowInput[j] = (double)blockY[i, j]; } // float -> double
            DCT1D_CPU(rowInput, rowOutput);
            for (int j = 0; j < BLOCK_SIZE; ++j) { temp[i, j] = rowOutput[j]; }
        }

        // Column-wise DCT
        double[] colInput = new double[BLOCK_SIZE];
        double[] colOutput = new double[BLOCK_SIZE];
        for (int j = 0; j < BLOCK_SIZE; ++j)
        {
            for (int i = 0; i < BLOCK_SIZE; ++i) { colInput[i] = temp[i, j]; }
            DCT1D_CPU(colInput, colOutput);
            // ★★★ 수정: 결과를 dctCoeffs[v, u] 즉 [i, j] 순서로 저장 ★★★
            for (int i = 0; i < BLOCK_SIZE; ++i) { dctCoeffs[i, j] = colOutput[i]; }
        }
    }

    // QIM 비트 추출 (CPU, double precision)
    private int ExtractQIMBit_CPU(double receivedCoeff, double delta)
    {
        if (delta <= 0) return -1; // 오류

        // Shader 로직과 일치 (round는 AwayFromZero가 기본일 수 있음)
        double n0 = Math.Round(receivedCoeff / delta, MidpointRounding.AwayFromZero); // Tie-breaking 명시
        double level0 = n0 * delta;
        double dist0 = Math.Abs(receivedCoeff - level0);

        double n1 = Math.Round((receivedCoeff - (delta / 2.0)) / delta, MidpointRounding.AwayFromZero);
        double level1 = n1 * delta + (delta / 2.0);
        double dist1 = Math.Abs(receivedCoeff - level1);

        return (dist0 <= dist1) ? 0 : 1;
    }
}