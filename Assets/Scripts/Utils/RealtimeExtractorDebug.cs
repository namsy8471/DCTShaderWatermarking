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


// �� ��ũ��Ʈ�� ���� ī�޶� �Ǵ� �ٸ� ���� ������Ʈ�� �߰��ϼ���.
public class RealtimeExtractorDebug : MonoBehaviour
{
    // ���� ��� ���ÿ� Enum
    public enum ExtractionMode { DCT_SS, LSB }

    [Header("���� ����")]
    public KeyCode extractionKey = KeyCode.F12; // ���� ���� Ű
    public ExtractionMode extractionMode = ExtractionMode.DCT_SS; // ���� ��� ����
    public bool logPixelValues = false; // ������: ù �ȼ� �� �α� ����

    [Header("SS ���� (DCT_SS ��� �� ���)")]
    [Tooltip("���� ������ Secret Key (���� �ÿ� �����ؾ� ��)")]
    public string ssSecretKey = "default_secret_key_rgb_ss"; // ���� �� ����� Ű�� ��ġ �ʿ�
    [Tooltip("1��ϴ� ����ϴ� AC ��� �� (���� �ÿ� �����ؾ� ��)")]
    [Range(1, 63)]
    public int ssCoefficientsToUse = 10; // ���� �� ����� ������ ��ġ �ʿ�

    [Header("Ÿ�� ���� �ؽ�ó (�б� ����)")]
    [Tooltip("���͸�ũ�� ���Ե� ���� ��� RenderTexture ����")]
    public RenderTexture sourceRT; // Inspector �Ҵ� �Ǵ� �Ʒ� �������� ã��

    private bool isRequestPending = false; // �ߺ� ��û ���� �÷���
    private const int BLOCK_SIZE = 8;      // ó�� ��� ũ�� (HLSL�� ��ġ)
    private const int SYNC_PATTERN_LENGTH = 64; // �ڡڡ� ����ȭ ���� ��Ʈ �� (���� ���̿� �°� ���� �ʿ�) �ڡڡ�

    // ����ȭ ���� ���� (���� ����ϴ� ����ȭ �������� ��ü �ʿ�)
    private readonly int[] sync_pattern_cs = new int[SYNC_PATTERN_LENGTH] {
        1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,
        0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,1,1,0,0,1,1,0,0,0,1,1,1,0,1,1,0
    };
    private string expectedSyncPatternString = null; // �񱳿� ���ڿ� (�̸� ����)

    // --- CPU DCT ����� ���� ��� ---
    private const double MATH_PI = 3.141592653589793;
    private readonly double DCT_SQRT1_N = 1.0 / Math.Sqrt(BLOCK_SIZE); // 1/sqrt(8)
    private readonly double DCT_SQRT2_N = Math.Sqrt(2.0 / BLOCK_SIZE); // sqrt(2/8) = 0.5

    void Start()
    {
        // �񱳿� ����ȭ ���� ���ڿ� �̸� ����
        StringBuilder sb = new StringBuilder(SYNC_PATTERN_LENGTH);
        for (int i = 0; i < SYNC_PATTERN_LENGTH; ++i)
        {
            if (i < sync_pattern_cs.Length) sb.Append(sync_pattern_cs[i]); else sb.Append('X');
        }
        expectedSyncPatternString = sb.ToString();
        if (sync_pattern_cs.Length != SYNC_PATTERN_LENGTH)
        {
            Debug.LogWarning($"sync_pattern_cs �迭 ����({sync_pattern_cs.Length})�� SYNC_PATTERN_LENGTH({SYNC_PATTERN_LENGTH})�� ��ġ���� �ʽ��ϴ�!");
        }
        Debug.Log($"���� ����ȭ ���� (ù {SYNC_PATTERN_LENGTH}��Ʈ): {expectedSyncPatternString}");
    }

    void Update()
    {
        if (Input.GetKeyDown(extractionKey) && !isRequestPending)
        {
            if (sourceRT == null)
            { /* ... sourceRT ã�� ���� ... */
                if (RTResultHolder.DedicatedSaveTarget != null && RTResultHolder.DedicatedSaveTarget.rt != null)
                {
                    sourceRT = RTResultHolder.DedicatedSaveTarget.rt; Debug.Log($"Source RT found via DCTResultHolder: {sourceRT.name}");
                }
                else { Debug.LogError("Source RenderTexture�� ã�� �� �����ϴ�!"); return; }
            }
            if (sourceRT == null || !sourceRT.IsCreated() || sourceRT.width <= 0 || sourceRT.height <= 0)
            {
                Debug.LogError($"Source RenderTexture�� ��ȿ���� �ʽ��ϴ�! Name: {sourceRT?.name}, Created: {sourceRT?.IsCreated()}, Size: {sourceRT?.width}x{sourceRT?.height}"); return;
            }

            isRequestPending = true;

            // �ڡڡ� ���� ��Ŀ� ������� ���� �̹����� ������, ������ ������ ��忡 ���� �ٸ��� ��û �ڡڡ�
            GraphicsFormat requestedGraphicsFormat = (extractionMode == ExtractionMode.LSB)
                                                 ? sourceRT.graphicsFormat // LSB�� ���� ���� �״�� �б� �õ�
                                                 : GraphicsFormat.R32G32B32A32_SFloat; // DCT_SS�� float ������ �ʿ�

            Debug.Log($"[{GetType().Name}] GPU Async Readback ��û (Mode: {extractionMode}, Target: {sourceRT.name}, Format: {requestedGraphicsFormat})...");
            AsyncGPUReadback.Request(sourceRT, 0, requestedGraphicsFormat, OnReadbackComplete);
        }
    }

    // GPU Readback �Ϸ� �ݹ�
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
        int totalBlocks = 0; // ���� ó��/���� ��� ��

        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            totalBlocks = ((width + BLOCK_SIZE - 1) / BLOCK_SIZE) * ((height + BLOCK_SIZE - 1) / BLOCK_SIZE);
            if (totalBlocks > 0 && DataManager.EncryptedOriginData != null)
            {
                // ���� �Ӻ��� �� ������� ���̷ε� ����
                List<uint> fullPayload = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);
                if (fullPayload != null && fullPayload.Count > 0)
                {
                    // GPU�� ���޵Ǿ��� ��Ʈ �� ��� (totalBlocks �� ���̷ε� ���� �� ���� ��)
                    int bitsSentToGpu = Math.Min(fullPayload.Count, totalBlocks);
                    // �� ����� GPU�� ���޵� ��Ʈ��
                    expectedFullBits.AddRange(fullPayload.Take(bitsSentToGpu));
                    Debug.Log($"�� ��� ��Ʈ ���� �Ϸ�: {expectedFullBits.Count} ��Ʈ (Max Blocks: {totalBlocks}, Payload Size: {fullPayload.Count})");
                }
                else { Debug.LogWarning("���� ���̷ε� ���� ���� �Ǵ� ��� ����."); }
            }
            else { Debug.LogWarning("���� ���̷ε� ���� ���� ������ (totalBlocks �Ǵ� ������ ����)."); }

            int bitsToExtract = expectedFullBits.Count; // ����/���� ���̴� ���� ��Ʈ ���� ����
            if (bitsToExtract == 0) throw new Exception("���� ���� ��Ʈ�� �����ϴ�.");
            extractedBits.Capacity = bitsToExtract;

            // --- DCT Spread Spectrum ���� ���� ---
            if (extractionMode == ExtractionMode.DCT_SS)
            {
                Debug.Log("DCT Spread Spectrum ���� ��Ʈ ���� �õ�...");
                Debug.LogWarning("CPU���� DCT�� �����մϴ�. ������ �ſ� ���� �� �ֽ��ϴ�!");

                NativeArray<float> pixelData = request.GetData<float>();
                Debug.Log($"Readback data as float array (Length: {pixelData.Length}). Expecting {width * height * 4}.");
                if (logPixelValues && pixelData.Length >= 4) Debug.Log($"[Pixel(0,0)] R={pixelData[0]:G9}, G={pixelData[1]:G9}, B={pixelData[2]:G9}, A={pixelData[3]:G9}");

                // �ڡڡ� ����: �ø� ���������� ��� �� ��� �ڡڡ�
                int numBlocksX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
                int numBlocksY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
                Debug.Log($"Total Blocks: {totalBlocks} ({numBlocksX}x{numBlocksY}), Bits to Extract: {bitsToExtract}");

                // ���� ���� ����
                float[] patternBuffer = new float[totalBlocks * ssCoefficientsToUse];
                System.Random prng = new System.Random(ssSecretKey.GetHashCode());
                for (int i = 0; i < patternBuffer.Length; ++i) patternBuffer[i] = (prng.NextDouble() < 0.5) ? -1f : 1f;

                // ������� �ε���
                Vector2Int[] zigzag = { /* ... ������ ���� ... */
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

                // �ӽ� �迭 ���� (CPU DCT��)
                float[,] blockR = new float[BLOCK_SIZE, BLOCK_SIZE];
                float[,] blockG = new float[BLOCK_SIZE, BLOCK_SIZE]; // �ʿ�� G, B�� ó��
                float[,] blockB = new float[BLOCK_SIZE, BLOCK_SIZE];
                
                double[,] dctCoeffsR = new double[BLOCK_SIZE, BLOCK_SIZE]; // DCT ��� ����� (double)
                double[,] dctCoeffsG = new double[BLOCK_SIZE, BLOCK_SIZE]; // DCT ��� ����� (double)
                double[,] dctCoeffsB = new double[BLOCK_SIZE, BLOCK_SIZE]; // DCT ��� ����� (double)

                // �� ��� ó��
                for (int blockIdx = 0; blockIdx < bitsToExtract; ++blockIdx)
                {
                    int blockY = blockIdx / numBlocksX; int blockX = blockIdx % numBlocksX;

                    // �ڡڡ� 1. ���� ����� �ȼ� ������ ���� (R ä�θ�) �ڡڡ�
                    for (int y = 0; y < BLOCK_SIZE; ++y)
                    {
                        for (int x = 0; x < BLOCK_SIZE; ++x)
                        {
                            int pixelX = blockX * BLOCK_SIZE + x;
                            int pixelY = blockY * BLOCK_SIZE + y;
                            if (pixelX < width && pixelY < height)
                            {
                                int dataIndex = (pixelY * width + pixelX) * 4; // R ä�� �ε���
                                if (dataIndex < pixelData.Length) blockR[y, x] = pixelData[dataIndex];
                                else blockR[y, x] = 0f;
                            }
                            else { blockR[y, x] = 0f; }
                        }
                    }

                    // �ڡڡ� 2. ����� ��Ͽ� ���� CPU���� 2D DCT ���� �ڡڡ�
                    DCT2D_CPU(blockR, dctCoeffsR); // R ä�� DCT ����
                    DCT2D_CPU(blockG, dctCoeffsG); // R ä�� DCT ����
                    DCT2D_CPU(blockB, dctCoeffsB); // R ä�� DCT ����

                    // �ڡڡ� 3. ���� DCT ����� �������� ������� ��� �ڡڡ�
                    double correlationSumR = 0.0; // double�� ����Ͽ� ���е� Ȯ��
                    double correlationSumG = 0.0;
                    double correlationSumB = 0.0;

                    int patternBaseIndex = blockIdx * ssCoefficientsToUse;
                    for (int i = 0; i < ssCoefficientsToUse; ++i)
                    {
                        Vector2Int uv = zigzag[i]; // ����� AC ��� ��ǥ
                                                   // DCT2D_CPU ����� [v, u] ������ �����
                                                   // DCT2D_CPU ����� [v, u] ������ �����
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

                    // �ڡڡ� 4. ��Ʈ ���� (��� ������� ���) �ڡڡ�
                    double finalCorrelation = (correlationSumR + correlationSumG + correlationSumB) / 3.0;
                    extractedBits.Add(finalCorrelation >= 0.0 ? 1 : 0); // double ��
                    // end for blockIdx

                }

            }
            // --- LSB ���� ���� ---
            else
            { // LSB
                Debug.Log("LSB ���� ��Ʈ ���� �õ�...");
                NativeArray<byte> byteData = request.GetData<byte>();
                 /* ... ���� LSB ������ ���� ���� ... */
                    int bytesPerPixel = (int)GraphicsFormatUtility.GetBlockSize(sourceRT.graphicsFormat);
                    // ... (�α� �� ��Ʈ �� ���) ...
                    extractedBits.Capacity = bitsToExtract;

                    int blueChannelByteOffset = 2; // �⺻�� (RGBA32 ����) - ���� ���� ���� �ʿ�!
                    var sourceFormat = sourceRT.graphicsFormat;
                    if (sourceFormat == GraphicsFormat.B8G8R8A8_SRGB || sourceFormat == GraphicsFormat.B8G8R8A8_UNorm) blueChannelByteOffset = 0;
                    // ... �ٸ� ���� �˻� ...

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
            Debug.Log($"CPU ó�� �ð�: {stopwatch.Elapsed.TotalMilliseconds:F2} ms ({extractedBits.Count} ��Ʈ ó��).");

            // --- ��� �� �� ��� ---
            CompareAgainstFullExpected(extractedBits, expectedFullBits);

        }
        catch (Exception e) { Debug.LogError($"����/ó�� �� ���� �߻�: {e.Message}\n{e.StackTrace}"); }
        finally { isRequestPending = false; }
    }

    // ����ȭ ���� �� �� �α� �Լ�
    private void CompareAndLogSyncPattern(List<int> bits)
    { /* ... ������ ���� ... */
        if (bits == null || bits.Count == 0) { Debug.LogError("����� ��Ʈ�� �����ϴ�!"); return; }
        int compareLength = Math.Min(bits.Count, 1532);
        if (compareLength == 0) { Debug.LogError("���� ��Ʈ�� �����ϴ�!"); return; }

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


        Debug.Log($"����� ���� ({compareLength} ��Ʈ): {extractedPattern}");
        Debug.Log($"���� ���� ({compareLength} ��Ʈ): {expectedSubPattern}");
        if (match && compareLength == SYNC_PATTERN_LENGTH) { Debug.Log($"<color=green>����ȭ ���� {compareLength} ��Ʈ ��ġ!</color>"); }
        else
        {
            Debug.LogError($"<color=red>����ȭ ���� ����ġ! ({mismatchCount} / {compareLength} ��Ʈ �ٸ�)</color>");
            int markerLength = Math.Min(diffMarker.Length, 1532);
            Debug.Log($"����ġ ��ġ: {diffMarker.ToString().Substring(0, markerLength)}{(diffMarker.Length > markerLength ? "..." : "")}");
        }
    }

    private void CompareAgainstFullExpected(List<int> extracted, List<uint> expected)
    {
        if (extracted == null || extracted.Count == 0) { Debug.LogError("����� ��Ʈ�� �����ϴ�!"); return; }
        if (expected == null || expected.Count == 0) { Debug.LogError("���� ���� ��Ʈ�� �����ϴ�!"); return; }

        int compareLength = Math.Min(extracted.Count, expected.Count); // ���� �� ������ ����
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

        int displayLength = Math.Min(compareLength, 120); // �ܼ� ��� ���� ����
        Debug.Log($"����� ��Ʈ (ó�� {displayLength}��): {extractedSb.ToString().Substring(0, displayLength)}{(compareLength > displayLength ? "..." : "")}");
        Debug.Log($"���� ��Ʈ (ó�� {displayLength}��): {expectedSb.ToString().Substring(0, displayLength)}{(compareLength > displayLength ? "..." : "")}");

        if (match)
        {
            Debug.Log($"<color=green>��ü {compareLength} ��Ʈ ��ġ!</color>");
        }
        else
        {
            Debug.LogError($"<color=red>��Ʈ ����ġ! ({mismatchCount} / {compareLength} ��Ʈ �ٸ�)</color>");
            int markerLength = Math.Min(diffMarker.Length, 120);
            Debug.Log($"����ġ ��ġ: {diffMarker.ToString().Substring(0, markerLength)}{(diffMarker.Length > markerLength ? "..." : "")}");
        }
    }

    // =============================================================
    // --- CPU ��� 2D DCT �Լ� ---
    // =============================================================

    // 1D DCT (CPU, double precision) - HLSL�� DCT_1D_Single�� ���� ����
    private void DCT1D_CPU(double[] input, double[] output)
    {
        int N = input.Length;
        if (N != BLOCK_SIZE || output.Length != BLOCK_SIZE)
        {
            Debug.LogError("DCT1D_CPU: Input/Output array size must be BLOCK_SIZE.");
            return;
        }
        double pi_div_2N = MATH_PI / (2.0 * N); // ��� �̸� ���

        for (int k = 0; k < N; ++k)
        {
            double sum = 0.0;
            double Ck = (k == 0) ? DCT_SQRT1_N : DCT_SQRT2_N; // �����ϸ� ���

            for (int n = 0; n < N; ++n)
            {
                // Math.Cos�� ���ڴ� ����
                sum += input[n] * Math.Cos((2.0 * n + 1.0) * k * pi_div_2N);
            }
            output[k] = Ck * sum;
        }
    }

    // 2D DCT (CPU, double precision) - �Է��� float[,] ���
    // blockData: �Է� 8x8 �ȼ� ��� (float)
    // dctCoeffs: ��� 8x8 DCT ��� (double)
    private void DCT2D_CPU(float[,] blockData, double[,] dctCoeffs)
    {
        int rows = blockData.GetLength(0);
        int cols = blockData.GetLength(1);
        if (rows != BLOCK_SIZE || cols != BLOCK_SIZE || dctCoeffs.GetLength(0) != BLOCK_SIZE || dctCoeffs.GetLength(1) != BLOCK_SIZE)
        {
            Debug.LogError("DCT2D_CPU: Input/Output array dimensions must be BLOCK_SIZE x BLOCK_SIZE.");
            return;
        }

        // �ӽ� �迭 (double ���)
        double[] tempRowInput = new double[BLOCK_SIZE];
        double[] tempRowOutput = new double[BLOCK_SIZE];
        double[,] tempBuffer = new double[BLOCK_SIZE, BLOCK_SIZE]; // �� DCT ��� �����

        // 1�ܰ�: ��(Row) ���� 1D DCT ����
        for (int i = 0; i < BLOCK_SIZE; ++i) // �� �࿡ ����
        {
            // ���� �� �����͸� double �迭�� ����
            for (int j = 0; j < BLOCK_SIZE; ++j) { tempRowInput[j] = (double)blockData[i, j]; }
            // 1D DCT ����
            DCT1D_CPU(tempRowInput, tempRowOutput);
            // ����� �ӽ� ���ۿ� ����
            for (int j = 0; j < BLOCK_SIZE; ++j) { tempBuffer[i, j] = tempRowOutput[j]; }
        }

        // �ӽ� �迭 (double ���)
        double[] tempColInput = new double[BLOCK_SIZE];
        double[] tempColOutput = new double[BLOCK_SIZE];

        // 2�ܰ�: ��(Column) ���� 1D DCT ����
        for (int j = 0; j < BLOCK_SIZE; ++j) // �� ���� ����
        {
            // �ӽ� ���ۿ��� ���� �� �����͸� double �迭�� ����
            for (int i = 0; i < BLOCK_SIZE; ++i) { tempColInput[i] = tempBuffer[i, j]; }
            // 1D DCT ����
            DCT1D_CPU(tempColInput, tempColOutput);
            // ���� ����� ��� �迭 dctCoeffs�� ���� (v, u ���� = i, j ����)
            for (int i = 0; i < BLOCK_SIZE; ++i) { dctCoeffs[i, j] = tempColOutput[i]; }
        }
    }

    // Ŭ���� ����
}