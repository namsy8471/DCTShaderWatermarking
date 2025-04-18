using UnityEngine;
using System.Collections.Generic;
using System.Text;
using System; // Math 사용
using System.IO;
using Unity.Collections; // NativeArray 사용

// 이 스크립트를 씬의 아무 게임오브젝트에 추가하세요.
public class InspectorLsbExtractor : MonoBehaviour
{
    // 추출 방식 선택 옵션
    public enum ExtractionMethod
    {
        LSB_From_Bytes,      // PNG/TGA 등 정수 기반 LSB (Color32 사용)
        DCT_QIM_From_Floats // EXR 등 Float 기반 DCT/QIM (float[] 또는 NativeArray<float> 사용)
    }

    [Header("공통 설정")]
    [Tooltip("LSB 또는 DCT/QIM 데이터가 숨겨진 이미지 파일을 Project 창에서 여기로 드래그하세요 (PNG, TGA, EXR 등).")]
    public Texture2D inputTexture;

    [Tooltip("사용할 추출 방식을 선택하세요.")]
    public ExtractionMethod methodToUse = ExtractionMethod.DCT_QIM_From_Floats; // 기본값을 DCT로 변경

    public KeyCode extractKey = KeyCode.F10;

    [Header("LSB 설정 (참고용)")]
    private const int SYNC_PATTERN_LENGTH = 1532;
    private readonly int[] sync_pattern_cs = { /* ... 이전과 동일 ... */
        1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0,1,0,1,0,1,0,1,0,
        1,0,1,0,1,0,1,0,0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,
        1,1,0,0,1,1,0,0,0,1,1,1,0,1,1,0
    };

    [Header("DCT/QIM 설정 (입력)")] // DCT 관련 설정만 보이도록 조정
    [Tooltip("QIM 추출에 사용할 Delta 값 (삽입 시 사용한 값과 동일해야 함)")]
    public float qimDelta = 0.05f;
    [Tooltip("비트가 삽입된 DCT 계수의 U 좌표 (가로, 0~7)")]
    public int dctU = 4; // 쉐이더 코드 예시와 맞춤 (4, 4)
    [Tooltip("비트가 삽입된 DCT 계수의 V 좌표 (세로, 0~7)")]
    public int dctV = 4; // 쉐이더 코드 예시와 맞춤 (4, 4)
    public const int BLOCK_SIZE = 8; // DCT 블록 크기

    // --- DCT 상수 (C# 버전) ---
    private const float PI_CS = Mathf.PI;
    private const float BLOCK_SIZE_FLOAT_CS = (float)BLOCK_SIZE;
    private static readonly float SQRT1_N_CS = 1.0f / Mathf.Sqrt(BLOCK_SIZE_FLOAT_CS);
    private static readonly float SQRT2_N_CS = Mathf.Sqrt(2.0f / BLOCK_SIZE_FLOAT_CS);


    void Update()
    {
        if (Input.GetKeyDown(extractKey))
        {
            if (inputTexture == null)
            {
                Debug.LogError("Input Texture가 Inspector에 할당되지 않았습니다!");
                return;
            }
            if (!inputTexture.isReadable)
            {
                Debug.LogError($"'{inputTexture.name}' 텍스처의 Import Settings에서 'Read/Write Enabled' 옵션을 체크해주세요!", inputTexture);
                return;
            }

            Debug.Log($"[{this.GetType().Name}] Starting extraction from Texture: {inputTexture.name} ({inputTexture.width}x{inputTexture.height}), Format: {inputTexture.format}, Method: {methodToUse}");

            switch (methodToUse)
            {
                case ExtractionMethod.LSB_From_Bytes:
                    ExtractLsbFromTextureBytes(inputTexture);
                    break;
                case ExtractionMethod.DCT_QIM_From_Floats:
                    // 입력 파라미터 유효성 검사
                    if (qimDelta <= 0) { Debug.LogError("QIM Delta 값은 0보다 커야 합니다."); return; }
                    if (dctU < 0 || dctU >= BLOCK_SIZE || dctV < 0 || dctV >= BLOCK_SIZE) { Debug.LogError($"DCT U, V 좌표는 0과 {BLOCK_SIZE - 1} 사이여야 합니다."); return; }
                    ExtractDctQimFromTextureFloats(inputTexture, qimDelta, dctU, dctV);
                    break;
                default:
                    Debug.LogError($"지원하지 않는 추출 방식입니다: {methodToUse}");
                    break;
            }
        }
    }

    // --- LSB 추출 함수 (기존 유지) ---
    void ExtractLsbFromTextureBytes(Texture2D sourceTex)
    {
        // ... (이전 답변의 ExtractLsbFromTextureBytes 함수 내용 그대로) ...
        if (IsFloatFormat(sourceTex.format)) { /* 경고 */ }
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        List<int> extractedBits = new List<int>();
        try
        {
            Color32[] pixels = sourceTex.GetPixels32();
            int totalPixels = pixels.Length;
            int bitsToExtract = Math.Min(totalPixels, SYNC_PATTERN_LENGTH);
            Debug.Log($"[LSB] 총 {totalPixels} 픽셀 데이터(Color32) 읽음. 처음 {bitsToExtract}개 LSB(Blue 채널) 추출 시도...");
            extractedBits.Capacity = bitsToExtract;
            for (int i = 0; i < bitsToExtract; ++i)
            {
                extractedBits.Add(pixels[i].b & 1);
            }
            stopwatch.Stop();
            Debug.Log($"[LSB] 추출 완료 ({extractedBits.Count} 비트). 소요 시간: {stopwatch.ElapsedMilliseconds} ms.");
            ValidateAndPrintSyncPattern(extractedBits);
        }
        catch (Exception e) { Debug.LogError($"[LSB] 추출 중 오류 발생: {e.Message}\n{e.StackTrace}"); }
    }

    // --- DCT/QIM 추출 함수 (Float 데이터 사용 + DCT/QIM 로직 구현) ---
    void ExtractDctQimFromTextureFloats(Texture2D sourceTex, float delta, int u, int v)
    {
        if (!IsFloatFormat(sourceTex.format))
        {
            Debug.LogWarning($"경고: DCT/QIM 추출 방식이 선택되었으나, 입력 텍스처({sourceTex.format})는 Float 포맷이 아닙니다. GetPixelData<float> 호출이 실패하거나 부정확한 결과가 나올 수 있습니다.");
            // 필요 시 여기서 return 처리도 가능
        }

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        List<int> extractedBits = new List<int>();

        try
        {
            int width = sourceTex.width;
            int height = sourceTex.height;
            int pixelCount = width * height;

            // 1. GetPixelData<float>로 Float 픽셀 데이터 읽기 (RGBA 순서)
            NativeArray<float> pixelData = sourceTex.GetPixelData<float>(0);
//            int pixelCount = width * height;
            int expectedLength = pixelCount * 4; // RGBAFloat 가정

            // 데이터 길이 검증 (최소 RGB, 즉 3채널은 필요)
            if (pixelData.Length < pixelCount * 3)
            {
                throw new Exception($"GetPixelData<float> returned unexpected data length: {pixelData.Length}. Expected at least {pixelCount * 3}");
            }
            Debug.Log($"[DCT/QIM] 총 {pixelData.Length}개 float 픽셀 데이터(NativeArray<float>) 읽음.");


            // 2. 블록 단위 처리
            int numBlocksX = width / BLOCK_SIZE;
            int numBlocksY = height / BLOCK_SIZE;
            int totalBlocks = numBlocksX * numBlocksY;
            extractedBits.Capacity = totalBlocks;

            float[,] blockYData = new float[BLOCK_SIZE, BLOCK_SIZE]; // 현재 블록의 Y 데이터 저장용
            float[,] dctCoeffs = new float[BLOCK_SIZE, BLOCK_SIZE];  // DCT 계수 저장용

            Debug.Log($"[DCT/QIM] 이미지 블록 처리 시작 ({numBlocksX} x {numBlocksY} = {totalBlocks} 블록)");

            for (int blockY = 0; blockY < numBlocksY; ++blockY)
            {
                for (int blockX = 0; blockX < numBlocksX; ++blockX)
                {
                    // 2-1. 현재 블록의 Y(Luminance) 데이터 추출
                    for (int y = 0; y < BLOCK_SIZE; y++)
                    {
                        for (int x = 0; x < BLOCK_SIZE; x++)
                        {
                            int pixelIndex = ((blockY * BLOCK_SIZE) + y) * width + ((blockX * BLOCK_SIZE) + x);
                            int dataIndex = pixelIndex * 4; // RGBA 순서

                            // 경계 체크 (NativeArray 접근 시 중요)
                            if (dataIndex + 2 < pixelData.Length) // R, G, B 인덱스 유효한지 확인
                            {
                                float R = pixelData[dataIndex + 0];
                                float G = pixelData[dataIndex + 1];
                                float B = pixelData[dataIndex + 2];
                                // RGB to Y 변환 (쉐이더와 동일한 계수 사용)
                                blockYData[y, x] = 0.299f * R + 0.587f * G + 0.114f * B;

                                // --- ★★★ 첫 번째 픽셀(0,0)의 Raw RGB 값 로깅 추가 ★★★ ---
                                if (blockX == 0 && blockY == 0 && x == 0 && y == 0)
                                {
                                    Debug.Log($"\n--- C#: Pixel (0,0) RAW RGB ---");
                                    Debug.Log($"R={R:R}, G={G:R}, B={B:R}"); // "R" 포맷: 전체 정밀도
                                    Debug.Log("-------------------------------\n");
                                }
                                // --- ★★★ 로깅 추가 끝 ★★★ ---
                            }
                            else
                            {
                                blockYData[y, x] = 0f; // 범위 벗어나면 0으로 처리
                            }

                        }
                    }

                    // --- ★★★ 첫 번째 블록 Y 데이터 로깅 추가 ★★★ ---
                    if (blockX == 0 && blockY == 0)
                    {
                        Debug.Log("\n--- C#: First Block Y Data (Input to DCT) ---");
                        StringBuilder sbY = new StringBuilder();
                        // 예시: 블록 전체 또는 일부 값 출력 (소수점 많이)
                        for (int r = 0; r < BLOCK_SIZE; r++)
                        {
                            for (int c = 0; c < BLOCK_SIZE; c++)
                            {
                                sbY.AppendFormat("{0:F9} ", blockYData[r, c]); // F9: 소수점 9자리
                            }
                            sbY.AppendLine(); // 줄바꿈
                        }
                        Debug.Log(sbY.ToString());
                        Debug.Log("--------------------------------------------\n");
                    }
                    // --- ★★★ 로깅 추가 끝 ★★★ ---

                    // 2-2. 2D DCT 수행 (C# 구현)
                    PerformDCT2D_CS(blockYData, ref dctCoeffs); // ref로 결과 배열 전달

                    // 2-3. 목표 위치 (u, v)의 DCT 계수 가져오기
                    // ★★★ C# 배열 인덱스는 [row, column] -> [v, u] 사용 ★★★
                    float targetCoeff = dctCoeffs[v, u];

                    if (blockX == 0 && blockY == 0)
                    {
                        Debug.Log($"★★★ C# DCT Coeff ({v},{u}): {targetCoeff:R}"); // "R" 포맷 지정자 사용 (전체 정밀도)
                    }

                    // --- ★★★ 여러 블록의 DCT 계수 값 디버깅 출력 ★★★ ---
                    int midBlockY = numBlocksY / 2; // 중간 블록 Y 인덱스 (정수 나눗셈)
                    int midBlockX = numBlocksX / 2; // 중간 블록 X 인덱스 (정수 나눗셈)

                    bool isFirstBlock = (blockX == 0 && blockY == 0);
                    bool isSecondBlockRow = (blockX == 1 && blockY == 0); // (0, 1) 블록
                    bool isSecondBlockCol = (blockX == 0 && blockY == 1); // (1, 0) 블록
                    bool isMiddleBlock = (blockX == midBlockX && blockY == midBlockY); // 중간 블록

                    // 특정 블록들의 계수 값만 로그로 출력
                    if (isFirstBlock || isSecondBlockRow || isSecondBlockCol || isMiddleBlock)
                    {
                        Debug.Log($"\n--- C# DCT Coeff Debug ---");
                        // blockX, blockY 는 루프 변수, vIndex, uIndex 는 Inspector 입력값
                        Debug.Log($"Block ({blockY}, {blockX}) - Target Coeff ({v},{u}): {targetCoeff:R}"); // "R" 포맷: double의 전체 정밀도 출력 시도
                        Debug.Log("---------------------------\n");
                    }
                    // --- ★★★ 디버깅 출력 끝 ★★★ ---


                    // 2-4. QIM 비트 추출 (C# 구현)
                    int extractedBit = ExtractBitQIM_CS(targetCoeff, delta);
                    extractedBits.Add(extractedBit);
                } // end for blockX
            } // end for blockY

            stopwatch.Stop();
            Debug.Log($"[DCT/QIM] 비트 추출 완료 ({extractedBits.Count} 비트). 소요 시간: {stopwatch.ElapsedMilliseconds} ms.");

            // 3. 결과 비교 및 출력
            ValidateAndPrintSyncPattern(extractedBits);

        }
        catch (UnityException uex) // GetPixelData는 포맷 안 맞으면 UnityException 발생 가능
        {
            Debug.LogError($"[DCT/QIM] 추출 중 Unity 오류 발생 (텍스처 포맷 확인): {uex.Message}\n{uex.StackTrace}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[DCT/QIM] 추출 중 오류 발생: {e.Message}\n{e.StackTrace}");
        }
        // pixelData NativeArray는 별도 Dispose 불필요
    }

    // --- 동기화 패턴 검증 및 출력 함수 (공통 사용, 이전과 동일) ---
    void ValidateAndPrintSyncPattern(List<int> bits)
    {
        // ... (이전 답변과 동일) ...
        if (bits == null || bits.Count < SYNC_PATTERN_LENGTH) { /*...*/ return; }
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < SYNC_PATTERN_LENGTH; ++i) { sb.Append(bits[i]); }
        string extractedPattern = sb.ToString();
        string expectedPattern = "111100001111000010101010101010100011001100110011110011000111011000000110000000000110000100011110000101011011000110111001111000010101101011010010110111001100111111011101010101111001010111100100110000101010101011001110111111011100000101000011011001011110000111100110000001100111111010011110010100111001111001111001100001011101101110111001100000000000101011100101111101110011001110011000000010000010100101001110111110001100010111001000001110001110101001000101011011010100111110000111100110110001000101111011101110011010111111010011011011000000110100110111011100111011011111010100010001011110010111100111011101001000011111010100011010110100011101001101010110011110010110100000000001101111010011000110000101111011010110001111110101110111010011010101111010101101111110100110111001110001100101101010101100100011011100101011001010010001010001101111111110100010011111110011001100100001000101111110111100001001010100011011100001111000101100001001001101100001110100011101001101100011000010001100110011101000011100111100101101111001100101011011101011100110001110100100010011010111011101100010111011010011001110000111111111011111010000001110110101000101000101101000001000100110111100111001001000001001100000001100000101101000000001010111110100101010011000111011100001100110110100100010100010001011101010110001010100001011001100001000001110100001001010111011001110000100101011110001011110000010100110100101000010001010000001101010111011001011000111011100101101100001000111100000110100000001100010010001001010111101001101101000110100100010001011011011";
        //for (int i = 0; i < SYNC_PATTERN_LENGTH; ++i) { expectedPattern += sync_pattern_cs[i]; }
        Debug.Log($"추출된 동기화 패턴 ({SYNC_PATTERN_LENGTH} 비트): {extractedPattern}");
        Debug.Log($"예상 동기화 패턴 ({SYNC_PATTERN_LENGTH} 비트): {expectedPattern}");
        if (extractedPattern == expectedPattern) Debug.Log("<color=green>동기화 패턴 일치!</color>");
        else { Debug.Log("<color=red>동기화 패턴 불일치!</color>"); }
    }

    // --- 텍스처 포맷 확인 헬퍼 함수 (이전과 동일) ---
    bool IsFloatFormat(TextureFormat format)
    {
        return format == TextureFormat.RGBAFloat || format == TextureFormat.RGBAHalf ||
               format == TextureFormat.RFloat || format == TextureFormat.RGFloat ||
               format == TextureFormat.RHalf || format == TextureFormat.RGHalf;
    }


    // ============================================================
    // === C# DCT / QIM 구현 ===
    // ============================================================

    // --- 1D DCT (C#) ---
    // 입력 float[8], 출력 float[8]
    private void PerformDCT1D_CS(float[] inputData, ref float[] outputData)
    {
        if (inputData == null || outputData == null || inputData.Length != BLOCK_SIZE || outputData.Length != BLOCK_SIZE)
        {
            Debug.LogError("[DCT] 1D DCT 배열 크기 오류!");
            return;
        }

        for (uint k = 0; k < BLOCK_SIZE; ++k)
        {
            float K = (float)k;
            float Ck = (K == 0.0f) ? SQRT1_N_CS : SQRT2_N_CS;
            float sum = 0.0f;
            for (uint n = 0; n < BLOCK_SIZE; ++n)
            {
                // Mathf.Cos 인자는 라디안 단위
                float cosVal = Mathf.Cos(PI_CS * K * (2.0f * n + 1.0f) / (2.0f * BLOCK_SIZE_FLOAT_CS));
                sum += inputData[n] * cosVal;
            }
            outputData[k] = Ck * sum;
        }
    }

    // --- 2D DCT (C#) ---
    // 입력 float[8,8], 결과 float[8,8] (ref로 전달받아 수정)
    private void PerformDCT2D_CS(float[,] inputBlock, ref float[,] outputCoeffs)
    {
        if (inputBlock == null || outputCoeffs == null ||
            inputBlock.GetLength(0) != BLOCK_SIZE || inputBlock.GetLength(1) != BLOCK_SIZE ||
            outputCoeffs.GetLength(0) != BLOCK_SIZE || outputCoeffs.GetLength(1) != BLOCK_SIZE)
        {
            Debug.LogError("[DCT] 2D DCT 배열 크기 오류!");
            return;
        }

        // 임시 배열 (행 DCT 결과 저장용)
        float[,] tempRowResult = new float[BLOCK_SIZE, BLOCK_SIZE];
        float[] rowInput = new float[BLOCK_SIZE];
        float[] rowOutput = new float[BLOCK_SIZE];

        // 1. 각 행(Row)에 대해 1D DCT 수행
        for (int r = 0; r < BLOCK_SIZE; r++)
        {
            // 현재 행 데이터 복사
            for (int c = 0; c < BLOCK_SIZE; c++)
            {
                rowInput[c] = inputBlock[r, c];
            }
            // 1D DCT 수행
            PerformDCT1D_CS(rowInput, ref rowOutput);
            // 결과 저장
            for (int c = 0; c < BLOCK_SIZE; c++)
            {
                tempRowResult[r, c] = rowOutput[c];
            }
        }

        // 2. 각 열(Column)에 대해 1D DCT 수행 (tempRowResult 사용)
        float[] colInput = new float[BLOCK_SIZE];
        float[] colOutput = new float[BLOCK_SIZE];
        for (int c = 0; c < BLOCK_SIZE; c++)
        {
            // 현재 열 데이터 복사
            for (int r = 0; r < BLOCK_SIZE; r++)
            {
                colInput[r] = tempRowResult[r, c];
            }
            // 1D DCT 수행
            PerformDCT1D_CS(colInput, ref colOutput);
            // 최종 결과 저장 (outputCoeffs 배열에)
            for (int r = 0; r < BLOCK_SIZE; r++)
            {
                // ★★★ C# 배열 인덱스 [행(v), 열(u)] 주의 ★★★
                outputCoeffs[r, c] = colOutput[r];
            }
        }
    }


    // --- QIM 비트 추출 (C#) ---
    // ★★★ 반올림 방식을 HLSL round()와 유사하게 수정 ★★★
    private int ExtractBitQIM_CS(float receivedCoeff, float delta)
    {
        if (delta <= 0f)
        {
            Debug.LogError($"[QIM] 유효하지 않은 Delta 값: {delta}");
            return 0;
        }

        // HLSL round() 동작 (0.5를 0에서 멀어지는 방향으로 반올림)을 흉내내는 함수
        Func<float, float> RoundHalfAwayFromZero = (val) =>
        {
            // Mathf.Round는 .5를 짝수로 반올림하므로 직접 구현
            // return Mathf.Floor(val + 0.5f); // 양수에서는 맞지만 음수에서 다름
            // 또는 System.Math.Round 사용 (MidpointRounding 옵션 활용)
            // MidpointRounding.AwayFromZero가 HLSL round와 가장 유사함
            return (float)System.Math.Round(value: val, digits: 0, mode: MidpointRounding.AwayFromZero);
        };


        // 비트 0일 경우의 레벨 및 거리 계산
        // float n0 = Mathf.Round(receivedCoeff / delta); // 기존 방식 주석 처리
        float n0 = RoundHalfAwayFromZero(receivedCoeff / delta); // ★ 수정된 반올림 사용 ★
        float level0 = n0 * delta;
        float dist0 = Mathf.Abs(receivedCoeff - level0);

        // 비트 1일 경우의 레벨 및 거리 계산
        // float n1 = Mathf.Round((receivedCoeff - (delta / 2.0f)) / delta); // 기존 방식 주석 처리
        float n1 = RoundHalfAwayFromZero((receivedCoeff - (delta / 2.0f)) / delta); // ★ 수정된 반올림 사용 ★
        float level1 = n1 * delta + (delta / 2.0f);
        float dist1 = Mathf.Abs(receivedCoeff - level1);

        // 거리가 더 가까운 쪽으로 비트 결정 (같으면 0)
        int extractedBit = (dist0 <= dist1) ? 0 : 1;

        // --- 디버깅용 로그 추가 (선택 사항) ---
        if (Mathf.Abs(dist0 - dist1) < 1e-6f) // 두 거리가 매우 비슷할 때 로그 출력
        {
            Debug.LogWarning($"[QIM Debug] Coeff={receivedCoeff:F8}, Delta={delta}, dist0={dist0:F8}, dist1={dist1:F8} -> Bit={extractedBit} (Distances are very close!)");
        }
        // ---디버깅 로그 끝 ---

        return extractedBit;
    }

} // 클래스 끝