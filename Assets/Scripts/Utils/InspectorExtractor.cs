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
public class InspectorExtractorDebug : MonoBehaviour
{
    // ���� ��� ���ÿ� Enum
    public enum ExtractionMode { LSB, DCT_SS, DWT, SVD }

    [Header("���� ����")]
    public KeyCode extractionKey = KeyCode.F10; // ���� ���� Ű
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
    public Texture2D sourceTexture2d; // Inspector �Ҵ� �Ǵ� �Ʒ� �������� ã��

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
            if (sourceTexture2d == null || sourceTexture2d.width <= 0 || sourceTexture2d.height <= 0)
            {
                Debug.LogError($"Source RenderTexture�� ��ȿ���� �ʽ��ϴ�! Name: {sourceTexture2d?.name}, Size: {sourceTexture2d?.width}x{sourceTexture2d?.height}"); return;
            }

            isRequestPending = true;

            // �ڡڡ� ���� ��Ŀ� ������� ���� �̹����� ������, ������ ������ ��忡 ���� �ٸ��� ��û �ڡڡ�
            GraphicsFormat requestedGraphicsFormat = (extractionMode == ExtractionMode.LSB)
                                                 ? sourceTexture2d.graphicsFormat // LSB�� ���� ���� �״�� �б� �õ�
                                                 : GraphicsFormat.R32G32B32A32_SFloat; // DCT_SS�� float ������ �ʿ�

            Debug.Log($"[{GetType().Name}] GPU Async Readback ��û (Mode: {extractionMode}, Target: {sourceTexture2d.name}, Format: {requestedGraphicsFormat})...");
            AsyncGPUReadback.Request(sourceTexture2d, 0, requestedGraphicsFormat, OnReadbackComplete);
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
                Debug.LogWarning("�̹��� �����Ϳ��� �о���� ��...");

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
                int bytesPerPixel = (int)GraphicsFormatUtility.GetBlockSize(sourceTexture2d.graphicsFormat);
                // ... (�α� �� ��Ʈ �� ���) ...
                extractedBits.Capacity = bitsToExtract;

                int blueChannelByteOffset = 2; // �⺻�� (RGBA32 ����) - ���� ���� ���� �ʿ�!
                var sourceFormat = sourceTexture2d.graphicsFormat;
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

            // 1. �� ����� �� ��ü ���� ��Ʈ��Ʈ�� ���� (��� ����)
            if (expectedFullBits == null || expectedFullBits.Count == 0)
            {
                Debug.LogError("���� ��Ʈ��Ʈ���� ������ �� �����ϴ�!");
            }
            else
            {
                // 2. ����ȭ ���� Ž�� �� ���̷ε� ���� �Լ� ȣ��
                int syncStartIndex = FindValidatedWatermarkStartIndex(extractedBits, expectedFullBits, sync_pattern_cs);

                // 3. ��� ó�� (��: ���� �� ���̷ε� ���)
                if (syncStartIndex != -1)
                {
                    int payloadStartIndex = syncStartIndex + SYNC_PATTERN_LENGTH;
                    int payloadLength = extractedBits.Count - payloadStartIndex;
                    if (payloadLength > 0)
                    {
                        Debug.Log($"���������� ����ȭ��. �ε��� {payloadStartIndex} ���� {payloadLength} ��Ʈ�� ���̷ε� ��� ����.");
                        // TODO: ���⼭ payloadBits�� ���� �����ͷ� ��ȯ�ϴ� ���� �۾� ����
                        // List<int> payloadBits = extractedBits.GetRange(payloadStartIndex, payloadLength);
                    }
                }
                // else: �Լ� ���ο��� �̹� ���� �α� ��µ�
            }

            stopwatch.Stop();
            Debug.Log($"CPU ó�� �ð�: {stopwatch.Elapsed.TotalMilliseconds:F2} ms ({extractedBits.Count} ��Ʈ ó��).");

        }
        catch (Exception e) { Debug.LogError($"����/ó�� �� ���� �߻�: {e.Message}\n{e.StackTrace}"); }
        finally { isRequestPending = false; }
    }


    /// <summary>
    /// ����� ��Ʈ�� ������ ����ȭ ������ Ž���ϰ�, ã���� ���̷ε� �κб��� ���Ͽ�
    /// ����ȭ ���ϰ� ���̷ε尡 ��� ��ġ�ϴ� ù ��° ���� �ε����� ��ȯ�մϴ�.
    /// </summary>
    /// <param name="extractedBits">GPU���� ����� ��ü ��Ʈ ����Ʈ</param>
    /// <param name="expectedFullPayloadWithHeader">����Ǵ� ��ü ��Ʈ ����Ʈ (����ȭ ���� ����)</param>
    /// <param name="syncPattern">����ȭ ���� int �迭</param>
    /// <returns>������ ����ȭ ������ ���� �ε��� (0���� ����). ��ȿ�� ������ ã�� ���ϸ� -1 ��ȯ.</returns>
    private int FindValidatedWatermarkStartIndex(List<int> extractedBits, List<uint> expectedFullPayloadWithHeader, int[] syncPattern)
    {
        int syncPatternLength = syncPattern.Length;

        // --- �Է� ��ȿ�� �˻� ---
        if (extractedBits == null || expectedFullPayloadWithHeader == null || syncPattern == null || syncPatternLength == 0)
        {
            Debug.LogError("[FindValidatedWatermarkStartIndex] �Է� �����Ͱ� ��ȿ���� �ʽ��ϴ�.");
            return -1; // ���� �ڵ� -1
        }
        // �ּ��� ����ȭ ���� ���̸�ŭ�� ��Ʈ�� ����Ǿ�� ��
        if (extractedBits.Count < syncPatternLength)
        {
            Debug.LogWarning($"����� ��Ʈ ��({extractedBits.Count})�� ����ȭ ���� ����({syncPatternLength})���� ª�� Ž���� �Ұ����մϴ�.");
            return -1;
        }
        // ���� ���̷ε忡�� �ּ��� ����ȭ ������ �־�� ��
        if (expectedFullPayloadWithHeader.Count < syncPatternLength)
        {
            Debug.LogWarning($"���� ���̷ε� ����({expectedFullPayloadWithHeader.Count})�� ����ȭ ���� ����({syncPatternLength})���� ª���ϴ�.");
            // �� ��� ����ȭ ���ϸ� ���� ���� ������, ���� ������ ���̷ε� �������� �ϹǷ� ���� ó��
            return -1;
        }

        // --- ����ȭ ���� Ž�� ���� ---
        // extractedBits ����Ʈ���� syncPattern�� ���۵� �� �ִ� ������ ��ġ������ Ž��
        int maxSearchStart = extractedBits.Count - syncPatternLength;
        Debug.Log($"����ȭ ���� Ž�� ���� (����� ��Ʈ ��: {extractedBits.Count}, �ִ� {maxSearchStart + 1} ��ġ Ȯ��)...");
        System.Diagnostics.Stopwatch searchStopwatch = System.Diagnostics.Stopwatch.StartNew(); // Ž�� �ð� ����

        for (int i = 0; i <= maxSearchStart; ++i) // i�� ���� Ž�� ���� ����ȭ ������ ���� �ε���
        {
            // 1. ���� ��ġ(i)���� �����ϴ� �κ��� ����ȭ ���ϰ� ��ġ�ϴ��� Ȯ��
            bool isSyncMatch = true;
            for (int j = 0; j < syncPatternLength; ++j)
            {
                if (extractedBits[i + j] != syncPattern[j])
                {
                    isSyncMatch = false; // �ϳ��� �ٸ��� ����ġ
                    break; // ���� ���� Ż��
                }
            }

            // 2. ����ȭ ������ ��ġ�ߴٸ�, �ڵ����� ���̷ε嵵 ����
            if (isSyncMatch)
            {
                Debug.Log($"�ε��� {i}���� ����ȭ ���� �ĺ� �߰�! ���̷ε� ���� ����...");

                int payloadStartIndexInExtracted = i + syncPatternLength; // ����� ��Ʈ���� ���̷ε� ���� ��ġ
                int payloadStartIndexInExpected = syncPatternLength;    // ���� ��Ʈ������ ���̷ε� ���� ��ġ
                int availableExtractedPayload = extractedBits.Count - payloadStartIndexInExtracted; // ����� ��Ʈ �� ���� ���̷ε� ����
                int expectedPayloadLength = expectedFullPayloadWithHeader.Count - payloadStartIndexInExpected; // ���� ���̷ε� ����
                int comparePayloadLength = Math.Min(availableExtractedPayload, expectedPayloadLength); // ���� ���� ���̷ε� ����

                // ���� ���̷ε尡 �ִ��� Ȯ��
                if (comparePayloadLength <= 0)
                {
                    Debug.LogWarning($"�ε��� {i}���� ����ȭ ������ ��ġ������ ���� ���̷ε尡 �����ϴ�. (����� ���̷ε� ����: {availableExtractedPayload}, ���� ���̷ε� ����: {expectedPayloadLength})");
                    // ����ȭ ���ϸ� ������ �������� ��������, �ƴϸ� ���з� �����ϰ� ��� Ž������ ��å ���� �ʿ�
                    // ���⼭�� ���̷ε� ���� ���з� ���� ��� Ž�� (continue)
                    continue; // ���� i �� �Ѿ
                }

                // ���̷ε� �� ����
                bool isPayloadMatch = true;
                int payloadMismatchCount = 0;
                StringBuilder diffMarker = new StringBuilder(comparePayloadLength); // ���� ��ġ ǥ�ÿ�

                for (int j = 0; j < comparePayloadLength; ++j)
                {
                    int extractedBit = extractedBits[payloadStartIndexInExtracted + j];
                    // expectedFullPayloadWithHeader�� uint ����Ʈ�̹Ƿ� int�� ĳ���� �ʿ�
                    uint expectedBitUint = expectedFullPayloadWithHeader[payloadStartIndexInExpected + j];
                    int expectedBit = (int)expectedBitUint;

                    if (extractedBit != expectedBit)
                    {
                        isPayloadMatch = false;
                        payloadMismatchCount++;
                        diffMarker.Append("^");
                        // ���� ������ ����Ǹ� ���⼭ break �Ͽ� ���� ��� ����
                        // if (payloadMismatchCount > 10) break; // ��: 10�� �̻� Ʋ���� �� �� �� ��
                    }
                    else
                    {
                        diffMarker.Append(" ");
                    }
                }

                // ���̷ε� ���� ��� Ȯ��
                if (isPayloadMatch) // ���̷ε���� ��� ��ġ!
                {
                    searchStopwatch.Stop(); // Ž�� �ð� ���� ����
                    Debug.Log($"<color=lime>�ε��� {i}���� ����ȭ ���� �� ���̷ε� ({comparePayloadLength}��Ʈ) ���� ��ġ Ȯ��! ���� ����.</color> (Ž�� �ð�: {searchStopwatch.Elapsed.TotalMilliseconds:F2} ms)");
                    return i; // ����! ����ȭ ������ ���۵� �ε���(i) ��ȯ
                }
                else // ����ȭ ������ �¾����� ���̷ε尡 Ʋ�� (False Positive)
                {
                    Debug.LogWarning($"�ε��� {i}���� ����ȭ ������ ��ġ������ ���̷ε尡 ����ġ�մϴ� ({payloadMismatchCount}/{comparePayloadLength} ��Ʈ �ٸ�). ��� Ž��...");
                    int displayLength = Math.Min(comparePayloadLength, 120);
                    Debug.Log($"���̷ε� ����ġ ��ġ: {diffMarker.ToString().Substring(0, displayLength)}{(comparePayloadLength > displayLength ? "..." : "")}");
                    // isSyncMatch�� ������ true �����̹Ƿ�, �ܺ� ������ ���� i�� �����
                }
            }
            // else: isSyncMatch�� false�̸� ���� i�� �Ѿ
        } // end for i (Ž�� ����)

        // ������ �� ���Ҵµ��� ������ ������ ã�� ����
        searchStopwatch.Stop();
        Debug.LogError($"<color=orange>����� ��Ʈ�� ��ü���� ��ȿ�� ����ȭ ���� + ���̷ε带 ã�� ���߽��ϴ�.</color> (Ž�� �ð�: {searchStopwatch.Elapsed.TotalMilliseconds:F2} ms)");
        return -1; // ���� ����
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