using UnityEngine;
using System.Collections.Generic;
using System.Text;
using System; // Math ���
using System.IO;
using Unity.Collections; // NativeArray ���

// �� ��ũ��Ʈ�� ���� �ƹ� ���ӿ�����Ʈ�� �߰��ϼ���.
public class InspectorLsbExtractor : MonoBehaviour
{
    // ���� ��� ���� �ɼ�
    public enum ExtractionMethod
    {
        LSB_From_Bytes,      // PNG/TGA �� ���� ��� LSB (Color32 ���)
        DCT_QIM_From_Floats // EXR �� Float ��� DCT/QIM (float[] �Ǵ� NativeArray<float> ���)
    }

    [Header("���� ����")]
    [Tooltip("LSB �Ǵ� DCT/QIM �����Ͱ� ������ �̹��� ������ Project â���� ����� �巡���ϼ��� (PNG, TGA, EXR ��).")]
    public Texture2D inputTexture;

    [Tooltip("����� ���� ����� �����ϼ���.")]
    public ExtractionMethod methodToUse = ExtractionMethod.DCT_QIM_From_Floats; // �⺻���� DCT�� ����

    public KeyCode extractKey = KeyCode.F10;

    [Header("LSB ���� (�����)")]
    private const int SYNC_PATTERN_LENGTH = 1532;
    private readonly int[] sync_pattern_cs = { /* ... ������ ���� ... */
        1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0,1,0,1,0,1,0,1,0,
        1,0,1,0,1,0,1,0,0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,
        1,1,0,0,1,1,0,0,0,1,1,1,0,1,1,0
    };

    [Header("DCT/QIM ���� (�Է�)")] // DCT ���� ������ ���̵��� ����
    [Tooltip("QIM ���⿡ ����� Delta �� (���� �� ����� ���� �����ؾ� ��)")]
    public float qimDelta = 0.05f;
    [Tooltip("��Ʈ�� ���Ե� DCT ����� U ��ǥ (����, 0~7)")]
    public int dctU = 4; // ���̴� �ڵ� ���ÿ� ���� (4, 4)
    [Tooltip("��Ʈ�� ���Ե� DCT ����� V ��ǥ (����, 0~7)")]
    public int dctV = 4; // ���̴� �ڵ� ���ÿ� ���� (4, 4)
    public const int BLOCK_SIZE = 8; // DCT ��� ũ��

    // --- DCT ��� (C# ����) ---
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
                Debug.LogError("Input Texture�� Inspector�� �Ҵ���� �ʾҽ��ϴ�!");
                return;
            }
            if (!inputTexture.isReadable)
            {
                Debug.LogError($"'{inputTexture.name}' �ؽ�ó�� Import Settings���� 'Read/Write Enabled' �ɼ��� üũ���ּ���!", inputTexture);
                return;
            }

            Debug.Log($"[{this.GetType().Name}] Starting extraction from Texture: {inputTexture.name} ({inputTexture.width}x{inputTexture.height}), Format: {inputTexture.format}, Method: {methodToUse}");

            switch (methodToUse)
            {
                case ExtractionMethod.LSB_From_Bytes:
                    ExtractLsbFromTextureBytes(inputTexture);
                    break;
                case ExtractionMethod.DCT_QIM_From_Floats:
                    // �Է� �Ķ���� ��ȿ�� �˻�
                    if (qimDelta <= 0) { Debug.LogError("QIM Delta ���� 0���� Ŀ�� �մϴ�."); return; }
                    if (dctU < 0 || dctU >= BLOCK_SIZE || dctV < 0 || dctV >= BLOCK_SIZE) { Debug.LogError($"DCT U, V ��ǥ�� 0�� {BLOCK_SIZE - 1} ���̿��� �մϴ�."); return; }
                    ExtractDctQimFromTextureFloats(inputTexture, qimDelta, dctU, dctV);
                    break;
                default:
                    Debug.LogError($"�������� �ʴ� ���� ����Դϴ�: {methodToUse}");
                    break;
            }
        }
    }

    // --- LSB ���� �Լ� (���� ����) ---
    void ExtractLsbFromTextureBytes(Texture2D sourceTex)
    {
        // ... (���� �亯�� ExtractLsbFromTextureBytes �Լ� ���� �״��) ...
        if (IsFloatFormat(sourceTex.format)) { /* ��� */ }
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        List<int> extractedBits = new List<int>();
        try
        {
            Color32[] pixels = sourceTex.GetPixels32();
            int totalPixels = pixels.Length;
            int bitsToExtract = Math.Min(totalPixels, SYNC_PATTERN_LENGTH);
            Debug.Log($"[LSB] �� {totalPixels} �ȼ� ������(Color32) ����. ó�� {bitsToExtract}�� LSB(Blue ä��) ���� �õ�...");
            extractedBits.Capacity = bitsToExtract;
            for (int i = 0; i < bitsToExtract; ++i)
            {
                extractedBits.Add(pixels[i].b & 1);
            }
            stopwatch.Stop();
            Debug.Log($"[LSB] ���� �Ϸ� ({extractedBits.Count} ��Ʈ). �ҿ� �ð�: {stopwatch.ElapsedMilliseconds} ms.");
            ValidateAndPrintSyncPattern(extractedBits);
        }
        catch (Exception e) { Debug.LogError($"[LSB] ���� �� ���� �߻�: {e.Message}\n{e.StackTrace}"); }
    }

    // --- DCT/QIM ���� �Լ� (Float ������ ��� + DCT/QIM ���� ����) ---
    void ExtractDctQimFromTextureFloats(Texture2D sourceTex, float delta, int u, int v)
    {
        if (!IsFloatFormat(sourceTex.format))
        {
            Debug.LogWarning($"���: DCT/QIM ���� ����� ���õǾ�����, �Է� �ؽ�ó({sourceTex.format})�� Float ������ �ƴմϴ�. GetPixelData<float> ȣ���� �����ϰų� ����Ȯ�� ����� ���� �� �ֽ��ϴ�.");
            // �ʿ� �� ���⼭ return ó���� ����
        }

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        List<int> extractedBits = new List<int>();

        try
        {
            int width = sourceTex.width;
            int height = sourceTex.height;
            int pixelCount = width * height;

            // 1. GetPixelData<float>�� Float �ȼ� ������ �б� (RGBA ����)
            NativeArray<float> pixelData = sourceTex.GetPixelData<float>(0);
//            int pixelCount = width * height;
            int expectedLength = pixelCount * 4; // RGBAFloat ����

            // ������ ���� ���� (�ּ� RGB, �� 3ä���� �ʿ�)
            if (pixelData.Length < pixelCount * 3)
            {
                throw new Exception($"GetPixelData<float> returned unexpected data length: {pixelData.Length}. Expected at least {pixelCount * 3}");
            }
            Debug.Log($"[DCT/QIM] �� {pixelData.Length}�� float �ȼ� ������(NativeArray<float>) ����.");


            // 2. ��� ���� ó��
            int numBlocksX = width / BLOCK_SIZE;
            int numBlocksY = height / BLOCK_SIZE;
            int totalBlocks = numBlocksX * numBlocksY;
            extractedBits.Capacity = totalBlocks;

            float[,] blockYData = new float[BLOCK_SIZE, BLOCK_SIZE]; // ���� ����� Y ������ �����
            float[,] dctCoeffs = new float[BLOCK_SIZE, BLOCK_SIZE];  // DCT ��� �����

            Debug.Log($"[DCT/QIM] �̹��� ��� ó�� ���� ({numBlocksX} x {numBlocksY} = {totalBlocks} ���)");

            for (int blockY = 0; blockY < numBlocksY; ++blockY)
            {
                for (int blockX = 0; blockX < numBlocksX; ++blockX)
                {
                    // 2-1. ���� ����� Y(Luminance) ������ ����
                    for (int y = 0; y < BLOCK_SIZE; y++)
                    {
                        for (int x = 0; x < BLOCK_SIZE; x++)
                        {
                            int pixelIndex = ((blockY * BLOCK_SIZE) + y) * width + ((blockX * BLOCK_SIZE) + x);
                            int dataIndex = pixelIndex * 4; // RGBA ����

                            // ��� üũ (NativeArray ���� �� �߿�)
                            if (dataIndex + 2 < pixelData.Length) // R, G, B �ε��� ��ȿ���� Ȯ��
                            {
                                float R = pixelData[dataIndex + 0];
                                float G = pixelData[dataIndex + 1];
                                float B = pixelData[dataIndex + 2];
                                // RGB to Y ��ȯ (���̴��� ������ ��� ���)
                                blockYData[y, x] = 0.299f * R + 0.587f * G + 0.114f * B;

                                // --- �ڡڡ� ù ��° �ȼ�(0,0)�� Raw RGB �� �α� �߰� �ڡڡ� ---
                                if (blockX == 0 && blockY == 0 && x == 0 && y == 0)
                                {
                                    Debug.Log($"\n--- C#: Pixel (0,0) RAW RGB ---");
                                    Debug.Log($"R={R:R}, G={G:R}, B={B:R}"); // "R" ����: ��ü ���е�
                                    Debug.Log("-------------------------------\n");
                                }
                                // --- �ڡڡ� �α� �߰� �� �ڡڡ� ---
                            }
                            else
                            {
                                blockYData[y, x] = 0f; // ���� ����� 0���� ó��
                            }

                        }
                    }

                    // --- �ڡڡ� ù ��° ��� Y ������ �α� �߰� �ڡڡ� ---
                    if (blockX == 0 && blockY == 0)
                    {
                        Debug.Log("\n--- C#: First Block Y Data (Input to DCT) ---");
                        StringBuilder sbY = new StringBuilder();
                        // ����: ��� ��ü �Ǵ� �Ϻ� �� ��� (�Ҽ��� ����)
                        for (int r = 0; r < BLOCK_SIZE; r++)
                        {
                            for (int c = 0; c < BLOCK_SIZE; c++)
                            {
                                sbY.AppendFormat("{0:F9} ", blockYData[r, c]); // F9: �Ҽ��� 9�ڸ�
                            }
                            sbY.AppendLine(); // �ٹٲ�
                        }
                        Debug.Log(sbY.ToString());
                        Debug.Log("--------------------------------------------\n");
                    }
                    // --- �ڡڡ� �α� �߰� �� �ڡڡ� ---

                    // 2-2. 2D DCT ���� (C# ����)
                    PerformDCT2D_CS(blockYData, ref dctCoeffs); // ref�� ��� �迭 ����

                    // 2-3. ��ǥ ��ġ (u, v)�� DCT ��� ��������
                    // �ڡڡ� C# �迭 �ε����� [row, column] -> [v, u] ��� �ڡڡ�
                    float targetCoeff = dctCoeffs[v, u];

                    if (blockX == 0 && blockY == 0)
                    {
                        Debug.Log($"�ڡڡ� C# DCT Coeff ({v},{u}): {targetCoeff:R}"); // "R" ���� ������ ��� (��ü ���е�)
                    }

                    // --- �ڡڡ� ���� ����� DCT ��� �� ����� ��� �ڡڡ� ---
                    int midBlockY = numBlocksY / 2; // �߰� ��� Y �ε��� (���� ������)
                    int midBlockX = numBlocksX / 2; // �߰� ��� X �ε��� (���� ������)

                    bool isFirstBlock = (blockX == 0 && blockY == 0);
                    bool isSecondBlockRow = (blockX == 1 && blockY == 0); // (0, 1) ���
                    bool isSecondBlockCol = (blockX == 0 && blockY == 1); // (1, 0) ���
                    bool isMiddleBlock = (blockX == midBlockX && blockY == midBlockY); // �߰� ���

                    // Ư�� ��ϵ��� ��� ���� �α׷� ���
                    if (isFirstBlock || isSecondBlockRow || isSecondBlockCol || isMiddleBlock)
                    {
                        Debug.Log($"\n--- C# DCT Coeff Debug ---");
                        // blockX, blockY �� ���� ����, vIndex, uIndex �� Inspector �Է°�
                        Debug.Log($"Block ({blockY}, {blockX}) - Target Coeff ({v},{u}): {targetCoeff:R}"); // "R" ����: double�� ��ü ���е� ��� �õ�
                        Debug.Log("---------------------------\n");
                    }
                    // --- �ڡڡ� ����� ��� �� �ڡڡ� ---


                    // 2-4. QIM ��Ʈ ���� (C# ����)
                    int extractedBit = ExtractBitQIM_CS(targetCoeff, delta);
                    extractedBits.Add(extractedBit);
                } // end for blockX
            } // end for blockY

            stopwatch.Stop();
            Debug.Log($"[DCT/QIM] ��Ʈ ���� �Ϸ� ({extractedBits.Count} ��Ʈ). �ҿ� �ð�: {stopwatch.ElapsedMilliseconds} ms.");

            // 3. ��� �� �� ���
            ValidateAndPrintSyncPattern(extractedBits);

        }
        catch (UnityException uex) // GetPixelData�� ���� �� ������ UnityException �߻� ����
        {
            Debug.LogError($"[DCT/QIM] ���� �� Unity ���� �߻� (�ؽ�ó ���� Ȯ��): {uex.Message}\n{uex.StackTrace}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[DCT/QIM] ���� �� ���� �߻�: {e.Message}\n{e.StackTrace}");
        }
        // pixelData NativeArray�� ���� Dispose ���ʿ�
    }

    // --- ����ȭ ���� ���� �� ��� �Լ� (���� ���, ������ ����) ---
    void ValidateAndPrintSyncPattern(List<int> bits)
    {
        // ... (���� �亯�� ����) ...
        if (bits == null || bits.Count < SYNC_PATTERN_LENGTH) { /*...*/ return; }
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < SYNC_PATTERN_LENGTH; ++i) { sb.Append(bits[i]); }
        string extractedPattern = sb.ToString();
        string expectedPattern = "111100001111000010101010101010100011001100110011110011000111011000000110000000000110000100011110000101011011000110111001111000010101101011010010110111001100111111011101010101111001010111100100110000101010101011001110111111011100000101000011011001011110000111100110000001100111111010011110010100111001111001111001100001011101101110111001100000000000101011100101111101110011001110011000000010000010100101001110111110001100010111001000001110001110101001000101011011010100111110000111100110110001000101111011101110011010111111010011011011000000110100110111011100111011011111010100010001011110010111100111011101001000011111010100011010110100011101001101010110011110010110100000000001101111010011000110000101111011010110001111110101110111010011010101111010101101111110100110111001110001100101101010101100100011011100101011001010010001010001101111111110100010011111110011001100100001000101111110111100001001010100011011100001111000101100001001001101100001110100011101001101100011000010001100110011101000011100111100101101111001100101011011101011100110001110100100010011010111011101100010111011010011001110000111111111011111010000001110110101000101000101101000001000100110111100111001001000001001100000001100000101101000000001010111110100101010011000111011100001100110110100100010100010001011101010110001010100001011001100001000001110100001001010111011001110000100101011110001011110000010100110100101000010001010000001101010111011001011000111011100101101100001000111100000110100000001100010010001001010111101001101101000110100100010001011011011";
        //for (int i = 0; i < SYNC_PATTERN_LENGTH; ++i) { expectedPattern += sync_pattern_cs[i]; }
        Debug.Log($"����� ����ȭ ���� ({SYNC_PATTERN_LENGTH} ��Ʈ): {extractedPattern}");
        Debug.Log($"���� ����ȭ ���� ({SYNC_PATTERN_LENGTH} ��Ʈ): {expectedPattern}");
        if (extractedPattern == expectedPattern) Debug.Log("<color=green>����ȭ ���� ��ġ!</color>");
        else { Debug.Log("<color=red>����ȭ ���� ����ġ!</color>"); }
    }

    // --- �ؽ�ó ���� Ȯ�� ���� �Լ� (������ ����) ---
    bool IsFloatFormat(TextureFormat format)
    {
        return format == TextureFormat.RGBAFloat || format == TextureFormat.RGBAHalf ||
               format == TextureFormat.RFloat || format == TextureFormat.RGFloat ||
               format == TextureFormat.RHalf || format == TextureFormat.RGHalf;
    }


    // ============================================================
    // === C# DCT / QIM ���� ===
    // ============================================================

    // --- 1D DCT (C#) ---
    // �Է� float[8], ��� float[8]
    private void PerformDCT1D_CS(float[] inputData, ref float[] outputData)
    {
        if (inputData == null || outputData == null || inputData.Length != BLOCK_SIZE || outputData.Length != BLOCK_SIZE)
        {
            Debug.LogError("[DCT] 1D DCT �迭 ũ�� ����!");
            return;
        }

        for (uint k = 0; k < BLOCK_SIZE; ++k)
        {
            float K = (float)k;
            float Ck = (K == 0.0f) ? SQRT1_N_CS : SQRT2_N_CS;
            float sum = 0.0f;
            for (uint n = 0; n < BLOCK_SIZE; ++n)
            {
                // Mathf.Cos ���ڴ� ���� ����
                float cosVal = Mathf.Cos(PI_CS * K * (2.0f * n + 1.0f) / (2.0f * BLOCK_SIZE_FLOAT_CS));
                sum += inputData[n] * cosVal;
            }
            outputData[k] = Ck * sum;
        }
    }

    // --- 2D DCT (C#) ---
    // �Է� float[8,8], ��� float[8,8] (ref�� ���޹޾� ����)
    private void PerformDCT2D_CS(float[,] inputBlock, ref float[,] outputCoeffs)
    {
        if (inputBlock == null || outputCoeffs == null ||
            inputBlock.GetLength(0) != BLOCK_SIZE || inputBlock.GetLength(1) != BLOCK_SIZE ||
            outputCoeffs.GetLength(0) != BLOCK_SIZE || outputCoeffs.GetLength(1) != BLOCK_SIZE)
        {
            Debug.LogError("[DCT] 2D DCT �迭 ũ�� ����!");
            return;
        }

        // �ӽ� �迭 (�� DCT ��� �����)
        float[,] tempRowResult = new float[BLOCK_SIZE, BLOCK_SIZE];
        float[] rowInput = new float[BLOCK_SIZE];
        float[] rowOutput = new float[BLOCK_SIZE];

        // 1. �� ��(Row)�� ���� 1D DCT ����
        for (int r = 0; r < BLOCK_SIZE; r++)
        {
            // ���� �� ������ ����
            for (int c = 0; c < BLOCK_SIZE; c++)
            {
                rowInput[c] = inputBlock[r, c];
            }
            // 1D DCT ����
            PerformDCT1D_CS(rowInput, ref rowOutput);
            // ��� ����
            for (int c = 0; c < BLOCK_SIZE; c++)
            {
                tempRowResult[r, c] = rowOutput[c];
            }
        }

        // 2. �� ��(Column)�� ���� 1D DCT ���� (tempRowResult ���)
        float[] colInput = new float[BLOCK_SIZE];
        float[] colOutput = new float[BLOCK_SIZE];
        for (int c = 0; c < BLOCK_SIZE; c++)
        {
            // ���� �� ������ ����
            for (int r = 0; r < BLOCK_SIZE; r++)
            {
                colInput[r] = tempRowResult[r, c];
            }
            // 1D DCT ����
            PerformDCT1D_CS(colInput, ref colOutput);
            // ���� ��� ���� (outputCoeffs �迭��)
            for (int r = 0; r < BLOCK_SIZE; r++)
            {
                // �ڡڡ� C# �迭 �ε��� [��(v), ��(u)] ���� �ڡڡ�
                outputCoeffs[r, c] = colOutput[r];
            }
        }
    }


    // --- QIM ��Ʈ ���� (C#) ---
    // �ڡڡ� �ݿø� ����� HLSL round()�� �����ϰ� ���� �ڡڡ�
    private int ExtractBitQIM_CS(float receivedCoeff, float delta)
    {
        if (delta <= 0f)
        {
            Debug.LogError($"[QIM] ��ȿ���� ���� Delta ��: {delta}");
            return 0;
        }

        // HLSL round() ���� (0.5�� 0���� �־����� �������� �ݿø�)�� �䳻���� �Լ�
        Func<float, float> RoundHalfAwayFromZero = (val) =>
        {
            // Mathf.Round�� .5�� ¦���� �ݿø��ϹǷ� ���� ����
            // return Mathf.Floor(val + 0.5f); // ��������� ������ �������� �ٸ�
            // �Ǵ� System.Math.Round ��� (MidpointRounding �ɼ� Ȱ��)
            // MidpointRounding.AwayFromZero�� HLSL round�� ���� ������
            return (float)System.Math.Round(value: val, digits: 0, mode: MidpointRounding.AwayFromZero);
        };


        // ��Ʈ 0�� ����� ���� �� �Ÿ� ���
        // float n0 = Mathf.Round(receivedCoeff / delta); // ���� ��� �ּ� ó��
        float n0 = RoundHalfAwayFromZero(receivedCoeff / delta); // �� ������ �ݿø� ��� ��
        float level0 = n0 * delta;
        float dist0 = Mathf.Abs(receivedCoeff - level0);

        // ��Ʈ 1�� ����� ���� �� �Ÿ� ���
        // float n1 = Mathf.Round((receivedCoeff - (delta / 2.0f)) / delta); // ���� ��� �ּ� ó��
        float n1 = RoundHalfAwayFromZero((receivedCoeff - (delta / 2.0f)) / delta); // �� ������ �ݿø� ��� ��
        float level1 = n1 * delta + (delta / 2.0f);
        float dist1 = Mathf.Abs(receivedCoeff - level1);

        // �Ÿ��� �� ����� ������ ��Ʈ ���� (������ 0)
        int extractedBit = (dist0 <= dist1) ? 0 : 1;

        // --- ������ �α� �߰� (���� ����) ---
        if (Mathf.Abs(dist0 - dist1) < 1e-6f) // �� �Ÿ��� �ſ� ����� �� �α� ���
        {
            Debug.LogWarning($"[QIM Debug] Coeff={receivedCoeff:F8}, Delta={delta}, dist0={dist0:F8}, dist1={dist1:F8} -> Bit={extractedBit} (Distances are very close!)");
        }
        // ---����� �α� �� ---

        return extractedBit;
    }

} // Ŭ���� ��