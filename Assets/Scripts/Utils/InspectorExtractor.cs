// --- InspectorExtractorDebug.cs (������) ---
using UnityEngine;
using UnityEngine.Rendering; // AsyncGPUReadbackRequest, GraphicsFormat
using System.Collections;
using System.Collections.Generic;
using System;
using System.Text; // StringBuilder
using System.Linq;
using Unity.Collections; // NativeArray
// using Unity.Mathematics; // ���� �ڵ忡�� ���� ��� �� ��
// using UnityEngine.Experimental.Rendering; // GraphicsFormatUtility ���� (�ֽ� ���������� UnityEngine.Rendering)
using System.IO; // ���� �����
using UnityEngine.UI;
using UnityEngine.Experimental.Rendering; // ���� UI ��ҿ� ���� �����Ѵٸ� �ʿ� (���� �ڵ忡�� ����)

// OriginBlock.cs�� ������Ʈ ���� �ְ�, �ش� Ŭ���� �� DataManager�� �ùٸ��� �����Ǿ� �ִٰ� �����մϴ�.
// ��: namespace OriginBlockUtil { public class OriginBlock { ... } } �̶�� using OriginBlockUtil; �߰�
// ��: public static class DataManager { public static byte[] EncryptedOriginData { get; set; } }

public class InspectorExtractorDebug : MonoBehaviour
{
    public enum ExtractionMode { LSB, DCT_SS, DWT, SVD } // SVD�� ���� �̱���

    [Header("���� ����")]
    public KeyCode startAutomatedAttackKey = KeyCode.F10; // �ڵ� ���� ���� Ű
    public ExtractionMode extractionMode = ExtractionMode.DCT_SS;
    public bool logPixelValuesForFirstBlock = false; // ù ��� �ȼ� �� �α� (DCT/DWT) �Ǵ� ù �� �ȼ� (LSB)

    [Header("JPEG ���� ����")]
    [Tooltip("��ǥ�� ���е� JPEG ǰ�� �� (��: 90,70,50)")]
    public string jpegQualitiesToTest = "90,70,50,30,10";
    [Tooltip("���� ��� ���� ���� �̸� (Application.persistentDataPath ����)")]
    public string outputSubFolderName = "RuntimeJpegAttacks";

    [Header("SS ���� (DCT_SS, DWT ��� �� ���)")]
    public string ssSecretKey = "default_secret_key_rgb_ss";
    [Range(1, 63)]
    public int ssCoefficientsToUse = 10;

    [Header("Ÿ�� ���� ���͸�ũ �̹���")]
    [Tooltip("���͸�ũ�� ���Ե� ���� Texture2D (JPEG ���� �� ����)")]
    public Texture2D sourceWatermarkedTexture; // Inspector���� �Ҵ�

    // ���� ���� ����
    private bool isProcessingAutomatedAttack = false;
    private bool isAsyncReadbackWaiting = false;

    private const int BLOCK_SIZE = 8;
    private const int HALF_BLOCK_SIZE = BLOCK_SIZE / 2; // DWT��
    // OriginBlock.cs�� ���ǵ� ����� ����ϰų� ���⼭ ��ġ���Ѿ� ��
    // private const int SYNC_PATTERN_LENGTH = OriginBlock.SYNC_PATTERN_LENGTH;
    // private const int LENGTH_FIELD_BITS = OriginBlock.LENGTH_FIELD_BITS;
    // ���� ���� ��:
    private const int SYNC_PATTERN_LENGTH = 64;
    private const int LENGTH_FIELD_BITS = 16;


    // ����ȭ ���� (OriginBlock.syncPattern�� ��ġ�ؾ� ��)
    // OriginBlock.syncPattern (List<uint>)�� ����ϰų�, �Ʒ� int[]�� List<uint>�� ��ȯ�ؼ� ���.
    // FindValidatedWatermarkStartIndex �Լ� �ñ״�ó�� ���� int[] ���.
    private readonly int[] sync_pattern_int_array_for_search = new int[SYNC_PATTERN_LENGTH] {
        1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,
        0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,1,1,0,0,1,1,0,0,0,1,1,1,0,1,1,0
    };

    // CPU DCT ���� ���
    private const double MATH_PI = 3.141592653589793;
    private readonly double DCT_SQRT1_N = 1.0 / Math.Sqrt(BLOCK_SIZE);
    private readonly double DCT_SQRT2_N = Math.Sqrt(2.0 / BLOCK_SIZE);

    // ���� ���� �������� ���� ������
    private int currentProcessingQualityFactor;
    private Texture2D currentAttackedTempTexture;
    private GraphicsFormat currentReadbackFormatForLSB; // LSB ���� �� ���� ����
    private List<uint> masterExpectedFullPayloadBits; // ��� QF�� ���� ������ ���� ���̷ε�
    private List<string> attackSummaryLogLines;

    void Start()
    {
        Application.runInBackground = true; // ��׶��� ���� ���
        Debug.Log("InspectorExtractorDebug �ʱ�ȭ �Ϸ�. �ڵ� ���� ���� Ű: " + startAutomatedAttackKey);
        // �ʿ��� ��� DataManager.EncryptedOriginData �ʱ�ȭ ���� ���⿡ �߰�
        // ��: Ư�� ���Ͽ��� �о�ͼ� DataManager.EncryptedOriginData = ...;
    }

    void Update()
    {
        if (Input.GetKeyDown(startAutomatedAttackKey) && !isProcessingAutomatedAttack)
        {
            if (!ValidateAutomatedAttackInputs()) return;

            isProcessingAutomatedAttack = true;
            attackSummaryLogLines = new List<string>();
            Debug.Log("===== �ڵ� JPEG ���� �� BER ��� ���� =====");
            StartCoroutine(RunAllJpegAttacksAndExtractCoroutine());
        }
    }

    bool ValidateAutomatedAttackInputs()
    {
        if (sourceWatermarkedTexture == null)
        {
            Debug.LogError("Source Watermarked Texture2D�� �Ҵ���� �ʾҽ��ϴ�!"); return false;
        }
        if (!sourceWatermarkedTexture.isReadable)
        {
            Debug.LogError("Source Watermarked Texture2D�� Import Settings���� 'Read/Write Enabled'�� �ݵ�� üũ���ּ���."); return false;
        }
        // DataManager.EncryptedOriginData�� OriginBlock.cs�� ���� ��ȣȭ�� ���� ������ byte[] �̾�� ��.
        if (DataManager.EncryptedOriginData == null || DataManager.EncryptedOriginData.Length == 0)
        {
            Debug.LogError("DataManager.EncryptedOriginData�� ����ְų� null�Դϴ�. ���� ���͸�ũ �����͸� �غ��ؾ� �մϴ�."); return false;
        }
        if (string.IsNullOrWhiteSpace(jpegQualitiesToTest))
        {
            Debug.LogError("JPEG Qualities ���ڿ��� ����ֽ��ϴ�."); return false;
        }
        try
        {
            var qfs = jpegQualitiesToTest.Split(',').Select(q => int.Parse(q.Trim())).ToArray();
            if (qfs.Length == 0) throw new FormatException("No qualities parsed.");
            foreach (var qf in qfs) if (qf < 0 || qf > 100) throw new FormatException("Quality must be 0-100.");
        }
        catch (FormatException e)
        {
            Debug.LogError($"JPEG ǰ�� �� ���ڿ� '{jpegQualitiesToTest}' �Ľ� ����: {e.Message}"); return false;
        }
        return true;
    }

    IEnumerator RunAllJpegAttacksAndExtractCoroutine()
    {
        // 1. ������ ���� ���̷ε� �غ� (��� QF�� �������� ���)
        try
        {
            masterExpectedFullPayloadBits = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);
            if (masterExpectedFullPayloadBits == null || masterExpectedFullPayloadBits.Count == 0)
            {
                Debug.LogError("������ ���� ���̷ε� ���� ����!");
                isProcessingAutomatedAttack = false;
                yield break;
            }
            Debug.Log($"������ ���� ���̷ε� ���� �Ϸ�: {masterExpectedFullPayloadBits.Count} ��Ʈ");
        }
        catch (Exception e)
        {
            Debug.LogError($"������ ���� ���̷ε� ���� �� ����: {e.Message}\n{e.StackTrace}");
            isProcessingAutomatedAttack = false;
            yield break;
        }

        string[] qfStrings = jpegQualitiesToTest.Split(',').Select(q => q.Trim()).Where(q => !string.IsNullOrEmpty(q)).ToArray();
        int[] qfs = Array.ConvertAll(qfStrings, int.Parse);

        string baseOutputDir = Path.Combine(Application.persistentDataPath, outputSubFolderName);
        string techniqueDir = Path.Combine(baseOutputDir, extractionMode.ToString());
        if (!Directory.Exists(techniqueDir)) Directory.CreateDirectory(techniqueDir);

        attackSummaryLogLines.Add($"### JPEG Attack Summary - Mode: {extractionMode} ###");
        attackSummaryLogLines.Add($"Source Image: {sourceWatermarkedTexture.name} ({sourceWatermarkedTexture.width}x{sourceWatermarkedTexture.height}, Format: {sourceWatermarkedTexture.graphicsFormat})");
        attackSummaryLogLines.Add($"Expected Payload Bit Length: {masterExpectedFullPayloadBits.Count}");
        if (extractionMode == ExtractionMode.DCT_SS || extractionMode == ExtractionMode.DWT)
        {
            attackSummaryLogLines.Add($"SS Key: '{ssSecretKey}', SS Coeffs To Use: {ssCoefficientsToUse}");
        }
        attackSummaryLogLines.Add("------------------------------------");

        for (int i = 0; i < qfs.Length; i++)
        {
            currentProcessingQualityFactor = qfs[i];
            Debug.Log($"--- ���� ó�� ���� JPEG ǰ��(QF): {currentProcessingQualityFactor} ---");

            byte[] jpegBytes = sourceWatermarkedTexture.EncodeToJPG(currentProcessingQualityFactor);
            string attackedImageFileName = $"{sourceWatermarkedTexture.name}_QF{currentProcessingQualityFactor}.jpg";
            string attackedImageFilePath = Path.Combine(techniqueDir, attackedImageFileName);
            File.WriteAllBytes(attackedImageFilePath, jpegBytes);
            Debug.Log($"���ݵ� �̹��� �����: {attackedImageFilePath} ({jpegBytes.Length / 1024.0f:F2} KB)");

            // JPEG���� ����� �����͸� �� Texture2D�� �ε�
            currentAttackedTempTexture = new Texture2D(2, 2, (TextureFormat)sourceWatermarkedTexture.graphicsFormat, false, false); // mipChain=false, linear=false (�� ������ ���� ����)
            if (!currentAttackedTempTexture.LoadImage(jpegBytes))
            {
                Debug.LogError($"QF {currentProcessingQualityFactor}: JPEG ����Ʈ�� Texture2D�� �ε� ����.");
                attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: Error loading attacked image into Texture2D.");
                Destroy(currentAttackedTempTexture); currentAttackedTempTexture = null;
                continue;
            }
            // LoadImage �� �ؽ�ó ũ�Ⱑ ������ �ٸ� �� �����Ƿ� Ȯ�� (������ ������)
            // Debug.Log($"Loaded attacked texture: {currentAttackedTempTexture.width}x{currentAttackedTempTexture.height}, Format: {currentAttackedTempTexture.graphicsFormat}");


            // AsyncGPUReadback�� ����� �׷��� ���� ����
            if (extractionMode == ExtractionMode.LSB)
            {
                currentReadbackFormatForLSB = currentAttackedTempTexture.graphicsFormat; // LSB�� ���� �ؽ�ó ���� �״�� �б� �õ�
            }
            else
            { // DCT_SS, DWT
                currentReadbackFormatForLSB = GraphicsFormat.R32G32B32A32_SFloat; // float ������ �ʿ�
            }

            isAsyncReadbackWaiting = true;
            Debug.Log($"AsyncGPUReadback ��û (Target: Attacked QF{currentProcessingQualityFactor} Texture, Requested Format: {currentReadbackFormatForLSB})...");
            AsyncGPUReadback.Request(currentAttackedTempTexture, 0, currentReadbackFormatForLSB, OnAttackedImageReadbackComplete);

            yield return new WaitUntil(() => !isAsyncReadbackWaiting); // �ݹ� �Ϸ� ���

            if (currentAttackedTempTexture != null)
            {
                Destroy(currentAttackedTempTexture);
                currentAttackedTempTexture = null;
            }
            Debug.Log($"--- QF: {currentProcessingQualityFactor} ó�� �Ϸ� ---");
            yield return null;
        }

        string summaryFileName = $"Summary_{extractionMode}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        string summaryFilePath = Path.Combine(techniqueDir, summaryFileName);
        File.WriteAllLines(summaryFilePath, attackSummaryLogLines);
        Debug.Log($"===== ��� JPEG ���� ó�� �Ϸ�. ��� ���� ����: {summaryFilePath} =====");
        Debug.Log($"��� ���� ���: {techniqueDir}");


        isProcessingAutomatedAttack = false;
    }

    // JPEG ���� �� ������ �̹����� ���� Readback �Ϸ� �ݹ�
    void OnAttackedImageReadbackComplete(AsyncGPUReadbackRequest request)
    {
        Debug.Log($"Async Readback �Ϸ� (QF {currentProcessingQualityFactor}).");
        if (!request.done || request.hasError || request.layerCount <= 0)
        {
            Debug.LogError($"GPU Readback ����! QF: {currentProcessingQualityFactor}, HasError: {request.hasError}, LayerCount: {request.layerCount}, Done: {request.done}");
            attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: GPU Readback Error.");
            isAsyncReadbackWaiting = false; return;
        }
        int width = request.width; int height = request.height;
        if (width <= 0 || height <= 0)
        {
            Debug.LogError($"GPU Readback ��ȿ���� ���� ũ�� QF {currentProcessingQualityFactor}: {width}x{height}");
            attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: GPU Readback invalid dimensions.");
            isAsyncReadbackWaiting = false; return;
        }

        List<int> extractedBits = new List<int>(); // ����� ��Ʈ�� 0 �Ǵ� 1�� int
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            int numBlocksX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int numBlocksY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int actualTotalBlocksOnCurrentImage = numBlocksX * numBlocksY;

            int maxPossibleBitsFromPayload = masterExpectedFullPayloadBits.Count;


            if (extractionMode == ExtractionMode.DCT_SS)
            {
                Debug.Log($"[QF {currentProcessingQualityFactor}] DCT_SS ���� ��Ʈ ���� (CPU)...");
                //if (request.format != GraphicsFormat.R32G32B32A32_SFloat)
                //{
                //    Debug.LogError($"DCT_SS Error: Readback format is {request.format}, but R32G32B32A32_SFloat was expected for float data.");
                //}
                NativeArray<float> pixelDataFloat = request.GetData<float>();
                if (logPixelValuesForFirstBlock && pixelDataFloat.Length >= 4) Debug.Log($"[QF {currentProcessingQualityFactor} Pixel(0,0)] R={pixelDataFloat[0]:G9}, G={pixelDataFloat[1]:G9}, B={pixelDataFloat[2]:G9}, A={pixelDataFloat[3]:G9}");

                float[] patternBuffer = GeneratePatternBufferCPU(actualTotalBlocksOnCurrentImage, ssCoefficientsToUse, ssSecretKey);
                Vector2Int[] zigzag = GetZigzagIndices(); // ����� ���� �ڵ��� zigzag �迭 ���
                int actualCoeffsToProcess = Math.Min(ssCoefficientsToUse, zigzag.Length); // ����� ���� ��� ��

                float[,] blockR = new float[BLOCK_SIZE, BLOCK_SIZE];
                float[,] blockG = new float[BLOCK_SIZE, BLOCK_SIZE];
                float[,] blockB = new float[BLOCK_SIZE, BLOCK_SIZE];
                double[,] dctCoeffsR = new double[BLOCK_SIZE, BLOCK_SIZE];
                double[,] dctCoeffsG = new double[BLOCK_SIZE, BLOCK_SIZE];
                double[,] dctCoeffsB = new double[BLOCK_SIZE, BLOCK_SIZE];

                // DCT/DWT�� ��ϴ� 1��Ʈ �����ϹǷ�, ���� ���� ������ actualTotalBlocksOnCurrentImage ��ŭ ���ƾ� ��.
                // BER �񱳴� masterExpectedFullPayloadBits ���̸�ŭ�� �ǹ̰� ����.
                // ������ �̹��� ��ü���� �ϰ�, �� �� ����ȭ�� ã�Ƴ�.
                for (int blockIdx = 0; blockIdx < actualTotalBlocksOnCurrentImage; ++blockIdx)
                {
                    int blkY = blockIdx / numBlocksX; int blkX = blockIdx % numBlocksX;
                    ExtractPixelBlockFloat_CPU(pixelDataFloat, width, height, blkX, blkY, blockR, blockG, blockB);

                    DCT2D_CPU(blockR, dctCoeffsR); DCT2D_CPU(blockG, dctCoeffsG); DCT2D_CPU(blockB, dctCoeffsB);

                    double correlationSumR = 0.0, correlationSumG = 0.0, correlationSumB = 0.0;
                    int patternBaseIndex = blockIdx * actualCoeffsToProcess; // ���� ����� ��� �� ����
                    for (int k = 0; k < actualCoeffsToProcess; ++k)
                    {
                        Vector2Int uv = zigzag[k]; // 0���� actualCoeffsToProcess-1 ����
                        if (patternBaseIndex + k < patternBuffer.Length)
                        {
                            correlationSumR += dctCoeffsR[uv.y, uv.x] * patternBuffer[patternBaseIndex + k];
                            correlationSumG += dctCoeffsG[uv.y, uv.x] * patternBuffer[patternBaseIndex + k];
                            correlationSumB += dctCoeffsB[uv.y, uv.x] * patternBuffer[patternBaseIndex + k];
                        }
                        else { break; } // ���� ���� ���� �ʰ� �� �ߴ�
                    }
                    double finalCorrelation = (correlationSumR + correlationSumG + correlationSumB) / 3.0;
                    extractedBits.Add(finalCorrelation >= 0.0 ? 1 : 0);
                }
            }
            else if (extractionMode == ExtractionMode.LSB)
            {
                Debug.Log($"[QF {currentProcessingQualityFactor}] LSB ���� ��Ʈ ���� (CPU)...");
                NativeArray<byte> byteData = request.GetData<byte>();
                int bytesPerPixel = GetFormatBytesPerPixel(currentReadbackFormatForLSB); // ������ ������ ���� ���

                if (bytesPerPixel == 0)
                {
                    Debug.LogError($"[QF {currentProcessingQualityFactor}] LSB: Bytes per pixel for format {currentReadbackFormatForLSB} is 0. Cannot extract.");
                    // �� ��� extractedBits�� ����ְ� ��. BER=1.0 ����.
                }
                else
                {
                    int blueChannelByteOffset = 2; // RGBA32 �⺻ (R=0, G=1, B=2, A=3)
                    if (currentReadbackFormatForLSB == GraphicsFormat.B8G8R8A8_SRGB || currentReadbackFormatForLSB == GraphicsFormat.B8G8R8A8_UNorm ||
                        currentReadbackFormatForLSB == GraphicsFormat.B8G8R8_SRGB || currentReadbackFormatForLSB == GraphicsFormat.B8G8R8_UNorm)
                        blueChannelByteOffset = 0; // B�� ù ����Ʈ (B=0, G=1, R=2, A=3)
                    // ����: R8G8B8_UNorm (RGB24)�� B�� 2. (R=0, G=1, B=2)
                    // �� ���� ���˿� ���� ��Ȯ�� ä�� �������� Ȯ���ϰ� �����ؾ� �մϴ�.

                    int totalPixels = width * height;
                    if (logPixelValuesForFirstBlock && byteData.Length >= bytesPerPixel * 5)
                    { // ù 5�ȼ� �α� �õ�
                        for (int p = 0; p < 5; ++p)
                        {
                            Debug.Log($"[QF {currentProcessingQualityFactor} LSB Pixel {p}] Byte0={byteData[p * bytesPerPixel + 0]}, Byte1={byteData[p * bytesPerPixel + 1]}, Byte2={byteData[p * bytesPerPixel + 2]}" + (bytesPerPixel > 3 ? $", Byte3={byteData[p * bytesPerPixel + 3]}" : ""));
                        }
                    }

                    for (int i = 0; i < totalPixels; ++i) // �̹��� ��ü �ȼ����� ���� �õ�
                    {
                        int pixelY = i / width; int pixelX = i % width;
                        int byteBaseIndex = (pixelY * width + pixelX) * bytesPerPixel;
                        int targetByteIndex = byteBaseIndex + blueChannelByteOffset;
                        if (targetByteIndex < byteData.Length) extractedBits.Add(byteData[targetByteIndex] & 1);
                        else { break; } // ������ �迭 ���� ����� �ߴ�
                    }
                }
            }
            else if (extractionMode == ExtractionMode.DWT)
            {
                Debug.LogWarning($"[QF {currentProcessingQualityFactor}] DWT ���� ���� �� �ڵ忡�� ���� ���� �������� �ʾҽ��ϴ�.");
                // ���⿡ DWT ���� ���� (CPU �Ǵ� GPU ȣ��)
            }

            Debug.Log($"[QF {currentProcessingQualityFactor}] �� {extractedBits.Count} ��Ʈ ���� �Ϸ�.");

            // BER ���
            if (masterExpectedFullPayloadBits == null || masterExpectedFullPayloadBits.Count == 0)
            {
                Debug.LogError($"[QF {currentProcessingQualityFactor}] ���� ���̷ε尡 ���� BER ��� �Ұ�!");
                attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: BER = N/A (No expected bits) (Extracted: {extractedBits.Count})");
            }
            else
            {
                // FindValidatedWatermarkStartIndex�� List<uint> expected, int[] syncPattern�� ����
                // OriginBlock.syncPattern (List<uint>)�� int[]�� ��ȯ�Ͽ� �����ϰų�,
                // �Ǵ� FindValidatedWatermarkStartIndex�� List<uint> syncPattern�� �޵��� ����.
                // ���⼭�� ����� �ڵ��� sync_pattern_int_array_for_search (int[])�� ���.
                // �� int[]�� OriginBlock.syncPattern�� ������ �����ؾ� ��.
                int syncStartIndex = FindValidatedWatermarkStartIndex_Internal(extractedBits, masterExpectedFullPayloadBits, sync_pattern_int_array_for_search);

                if (syncStartIndex != -1)
                {
                    int expectedDataLenFieldStart = SYNC_PATTERN_LENGTH; // ���� const �Ǵ� OriginBlock.SYNC_PATTERN_LENGTH
                    int expectedDataLenFieldEnd = expectedDataLenFieldStart + LENGTH_FIELD_BITS; // ���� const �Ǵ� OriginBlock.LENGTH_FIELD_BITS

                    if (masterExpectedFullPayloadBits.Count < expectedDataLenFieldEnd)
                    {
                        Debug.LogError($"[QF {currentProcessingQualityFactor}] ���� ���̷ε尡 ���� �ʵ带 �����ϱ⿡ �ʹ� ª���ϴ�.");
                        attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: BER = 1.000000 (Expected payload too short) (Extracted: {extractedBits.Count})");
                    }
                    else
                    {
                        int actualDataLengthInPayload = 0;
                        for (int bitIdx = 0; bitIdx < LENGTH_FIELD_BITS; bitIdx++)
                        {
                            if (masterExpectedFullPayloadBits[expectedDataLenFieldStart + bitIdx] == 1)
                            {
                                actualDataLengthInPayload |= (1 << (LENGTH_FIELD_BITS - 1 - bitIdx));
                            }
                        }

                        if (actualDataLengthInPayload == 0)
                        {
                            Debug.Log($"[QF {currentProcessingQualityFactor}] ���� ���̷ε��� ������ ���̰� 0�Դϴ�. BER = 0.0");
                            attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: BER = 0.000000 (No data to compare) (Extracted: {extractedBits.Count})");
                        }
                        else
                        {
                            int originalDataStartIndexInExpected = expectedDataLenFieldEnd;
                            int extractedDataStartIndexFromSync = syncStartIndex + SYNC_PATTERN_LENGTH + LENGTH_FIELD_BITS;

                            if ((originalDataStartIndexInExpected + actualDataLengthInPayload) > masterExpectedFullPayloadBits.Count ||
                                (extractedDataStartIndexFromSync + actualDataLengthInPayload) > extractedBits.Count)
                            {
                                Debug.LogWarning($"[QF {currentProcessingQualityFactor}] BER ��� �� ������ ���׸�Ʈ�� ������ ����ϴ�. BER = 1.0. ExpectedPayloadLen={masterExpectedFullPayloadBits.Count}, ExtractedLen={extractedBits.Count}, SyncStart={syncStartIndex}, ActualDataLen={actualDataLengthInPayload}");
                                attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: BER = 1.000000 (Data bounds error) (Extracted: {extractedBits.Count})");
                            }
                            else
                            {
                                int errors = 0;
                                for (int k = 0; k < actualDataLengthInPayload; k++)
                                {
                                    if ((uint)extractedBits[extractedDataStartIndexFromSync + k] != masterExpectedFullPayloadBits[originalDataStartIndexInExpected + k])
                                    {
                                        errors++;
                                    }
                                }
                                double ber = (actualDataLengthInPayload == 0) ? 0.0 : (double)errors / actualDataLengthInPayload;
                                Debug.Log($"[QF {currentProcessingQualityFactor}] BER ����: {ber:F6} ({errors} errors / {actualDataLengthInPayload} bits)");
                                attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: BER = {ber:F6} (Extracted: {extractedBits.Count})");
                            }
                        }
                    }
                }
                else
                { // ����ȭ ����
                    Debug.LogWarning($"[QF {currentProcessingQualityFactor}] ����� ��Ʈ���� ����ȭ ������ ã�� ���߽��ϴ�. BER = 1.0");
                    attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: BER = 1.000000 (Sync failed) (Extracted: {extractedBits.Count})");
                }
            }
            stopwatch.Stop();
            Debug.Log($"[QF {currentProcessingQualityFactor}] CPU ó�� �ð� (����+BER): {stopwatch.Elapsed.TotalMilliseconds:F2} ms.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[QF {currentProcessingQualityFactor}] OnReadbackComplete ó�� �� ���� �߻�: {e.Message}\n{e.StackTrace}");
            attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: Exception - {e.Message}");
        }
        finally
        {
            isAsyncReadbackWaiting = false;
        }
    }

    // �ȼ� ��� ���� (NativeArray<float>���� float[,,]����)
    private void ExtractPixelBlockFloat_CPU(NativeArray<float> pixelData, int imgWidth, int imgHeight, int blockXId, int blockYId,
                                       float[,] blockR, float[,] blockG, float[,] blockB)
    {
        for (int y = 0; y < BLOCK_SIZE; ++y)
        {
            for (int x = 0; x < BLOCK_SIZE; ++x)
            {
                int px = blockXId * BLOCK_SIZE + x;
                int py = blockYId * BLOCK_SIZE + y; // (0,0) is top-left (NativeArray�� ���� 1D�� �� �̾���)
                if (px < imgWidth && py < imgHeight)
                {
                    int baseIdx = (py * imgWidth + px) * 4; // RGBA ������ 4 float ����
                    if (baseIdx + 2 < pixelData.Length)
                    { // Ensure R, G, B are accessible
                        blockR[y, x] = pixelData[baseIdx + 0];
                        blockG[y, x] = pixelData[baseIdx + 1];
                        blockB[y, x] = pixelData[baseIdx + 2];
                    }
                    else { blockR[y, x] = 0f; blockG[y, x] = 0f; blockB[y, x] = 0f; }
                }
                else { blockR[y, x] = 0f; blockG[y, x] = 0f; blockB[y, x] = 0f; }
            }
        }
    }

    // ������� �ε��� ��ȯ (����� ���� �ڵ�� ����)
    private Vector2Int[] GetZigzagIndices()
    {
        return new Vector2Int[] {
            new Vector2Int(0,1),new Vector2Int(1,0),new Vector2Int(2,0),new Vector2Int(1,1),new Vector2Int(0,2),new Vector2Int(0,3),new Vector2Int(1,2),new Vector2Int(2,1),new Vector2Int(3,0),new Vector2Int(4,0),new Vector2Int(3,1),new Vector2Int(2,2),new Vector2Int(1,3),new Vector2Int(0,4),new Vector2Int(0,5),new Vector2Int(1,4),
            new Vector2Int(2,3),new Vector2Int(3,2),new Vector2Int(4,1),new Vector2Int(5,0),new Vector2Int(6,0),new Vector2Int(5,1),new Vector2Int(4,2),new Vector2Int(3,3),new Vector2Int(2,4),new Vector2Int(1,5),new Vector2Int(0,6),new Vector2Int(0,7),new Vector2Int(1,6),new Vector2Int(2,5),new Vector2Int(3,4),new Vector2Int(4,3),
            new Vector2Int(5,2),new Vector2Int(6,1),new Vector2Int(7,0),new Vector2Int(7,1),new Vector2Int(6,2),new Vector2Int(5,3),new Vector2Int(4,4),new Vector2Int(3,5),new Vector2Int(2,6),new Vector2Int(1,7),new Vector2Int(2,7),new Vector2Int(3,6),new Vector2Int(4,5),new Vector2Int(5,4),new Vector2Int(6,3),new Vector2Int(7,2),
            new Vector2Int(7,3),new Vector2Int(6,4),new Vector2Int(5,5),new Vector2Int(4,6),new Vector2Int(3,7),new Vector2Int(4,7),new Vector2Int(5,6),new Vector2Int(6,5),new Vector2Int(7,4),new Vector2Int(7,5),new Vector2Int(6,6),new Vector2Int(5,7),new Vector2Int(6,7),new Vector2Int(7,6),new Vector2Int(7,7)
        };
    }

    // CPU ��� 1D DCT (����� ���� �ڵ�� ����)
    private void DCT1D_CPU(double[] input, double[] output)
    {
        int N = input.Length; double pi_div_2N = MATH_PI / (2.0 * N);
        for (int k = 0; k < N; ++k)
        {
            double sum = 0.0; double Ck = (k == 0) ? DCT_SQRT1_N : DCT_SQRT2_N;
            for (int n = 0; n < N; ++n) { sum += input[n] * Math.Cos((2.0 * n + 1.0) * k * pi_div_2N); }
            output[k] = Ck * sum;
        }
    }
    // CPU ��� 2D DCT (����� ���� �ڵ�� ����)
    private void DCT2D_CPU(float[,] blockData, double[,] dctCoeffs)
    {
        double[] tempRowInput = new double[BLOCK_SIZE]; double[] tempRowOutput = new double[BLOCK_SIZE];
        double[,] tempBuffer = new double[BLOCK_SIZE, BLOCK_SIZE];
        for (int i = 0; i < BLOCK_SIZE; ++i)
        {
            for (int j = 0; j < BLOCK_SIZE; ++j) { tempRowInput[j] = (double)blockData[i, j]; }
            DCT1D_CPU(tempRowInput, tempRowOutput);
            for (int j = 0; j < BLOCK_SIZE; ++j) { tempBuffer[i, j] = tempRowOutput[j]; }
        }
        double[] tempColInput = new double[BLOCK_SIZE]; double[] tempColOutput = new double[BLOCK_SIZE];
        for (int j = 0; j < BLOCK_SIZE; ++j)
        {
            for (int i = 0; i < BLOCK_SIZE; ++i) { tempColInput[i] = tempBuffer[i, j]; }
            DCT1D_CPU(tempColInput, tempColOutput);
            for (int i = 0; i < BLOCK_SIZE; ++i) { dctCoeffs[i, j] = tempColOutput[i]; }
        }
    }

    // CPU���� ���� ���� ���� (���� �亯�� C# ������ ����)
    private float[] GeneratePatternBufferCPU(int totalBlocks, int coeffsPerBlock, string seedKey)
    {
        if (totalBlocks <= 0 || coeffsPerBlock <= 0) return new float[0];
        int seedIntValue = 0; if (!string.IsNullOrEmpty(seedKey)) seedIntValue = seedKey.GetHashCode();
        System.Random rng = new System.Random(seedIntValue);
        int bufferSize = totalBlocks * coeffsPerBlock;
        float[] buffer = new float[bufferSize];
        for (int i = 0; i < bufferSize; i++) { buffer[i] = (rng.NextDouble() < 0.5) ? -1.0f : 1.0f; }
        return buffer;
    }

    // GraphicsFormat�� �ȼ��� ����Ʈ �� ���� (���� switch)
    private int GetFormatBytesPerPixel(GraphicsFormat format)
    {
        switch (format)
        { // �ֿ� ���˵�, �ʿ�� �߰�
            case GraphicsFormat.R8_UNorm: case GraphicsFormat.R8_SNorm: case GraphicsFormat.R8_UInt: case GraphicsFormat.R8_SInt: return 1;
            case GraphicsFormat.R8G8_UNorm: case GraphicsFormat.R8G8_SNorm: case GraphicsFormat.R8G8_UInt: case GraphicsFormat.R8G8_SInt: case GraphicsFormat.R16_UNorm: case GraphicsFormat.R16_SNorm: case GraphicsFormat.R16_UInt: case GraphicsFormat.R16_SInt: case GraphicsFormat.R16_SFloat: return 2;
            case GraphicsFormat.R8G8B8_UNorm: case GraphicsFormat.R8G8B8_SRGB: case GraphicsFormat.B8G8R8_UNorm: case GraphicsFormat.B8G8R8_SRGB: return 3; // 24bpp
            case GraphicsFormat.R8G8B8A8_UNorm: case GraphicsFormat.R8G8B8A8_SRGB: case GraphicsFormat.B8G8R8A8_UNorm: case GraphicsFormat.B8G8R8A8_SRGB: case GraphicsFormat.R16G16_UNorm: case GraphicsFormat.R16G16_SFloat: case GraphicsFormat.R32_UInt: case GraphicsFormat.R32_SInt: case GraphicsFormat.R32_SFloat: return 4;
            case GraphicsFormat.R16G16B16A16_UNorm: case GraphicsFormat.R16G16B16A16_SFloat: case GraphicsFormat.R32G32_UInt: case GraphicsFormat.R32G32_SInt: case GraphicsFormat.R32G32_SFloat: return 8;
            case GraphicsFormat.R32G32B32_UInt: case GraphicsFormat.R32G32B32_SInt: case GraphicsFormat.R32G32B32_SFloat: return 12;
            case GraphicsFormat.R32G32B32A32_UInt: case GraphicsFormat.R32G32B32A32_SInt: case GraphicsFormat.R32G32B32A32_SFloat: return 16;
            default: Debug.LogWarning($"GetFormatBytesPerPixel: Unhandled GraphicsFormat {format}. Returning 0."); return 0;
        }
    }

    // ����ȭ ���� Ž�� (����� ���� �Լ��� ���� ����, List<uint> expected, int[] syncPattern ���)
    private int FindValidatedWatermarkStartIndex_Internal(List<int> extractedBits, List<uint> expectedFullPayload, int[] syncPattern)
    {
        int syncPatternLength = syncPattern.Length;
        if (extractedBits == null || expectedFullPayload == null || syncPattern == null || syncPatternLength == 0 || extractedBits.Count < syncPatternLength || expectedFullPayload.Count < syncPatternLength)
        {
            // Debug.LogWarning("[FindValidatedWatermarkStartIndex_Internal] Invalid input data or lengths.");
            return -1;
        }

        for (int i = 0; i <= extractedBits.Count - syncPatternLength; i++)
        {
            bool syncMatch = true;
            for (int kSync = 0; kSync < syncPatternLength; kSync++)
            {
                if (extractedBits[i + kSync] != syncPattern[kSync])
                {
                    syncMatch = false; break;
                }
            }
            if (!syncMatch) continue;

            // ����ȭ ���� ��ġ, ���� ��ü ���̷ε�(expectedFullPayload�� ���� �κ�)�� ��
            int payloadStartInExtracted = i + syncPatternLength;
            if (payloadStartInExtracted + LENGTH_FIELD_BITS > extractedBits.Count) continue; // ���� �ʵ� ���� �� �ִ��� Ȯ��

            int actualDataLenFromExtractedHeader = 0;
            for (int bitIdx = 0; bitIdx < LENGTH_FIELD_BITS; bitIdx++)
            {
                if (extractedBits[payloadStartInExtracted + bitIdx] == 1)
                {
                    actualDataLenFromExtractedHeader |= (1 << (LENGTH_FIELD_BITS - 1 - bitIdx));
                }
            }

            int totalLengthToCompareInExpected = syncPatternLength + LENGTH_FIELD_BITS + actualDataLenFromExtractedHeader;

            if (expectedFullPayload.Count < totalLengthToCompareInExpected || i + totalLengthToCompareInExpected > extractedBits.Count)
            {
                continue; // ���� ��ŭ ����� ��Ʈ�� ���� ���
            }

            bool fullMatch = true;
            // expectedFullPayload�� �պκ�(����ȭ����+�����ʵ�+������)�� extractedBits�� ���� ��ġ���͸� ��
            for (int k = 0; k < totalLengthToCompareInExpected; k++)
            {
                if (extractedBits[i + k] != (int)expectedFullPayload[k])
                { // expected�� uint, extracted�� int
                    fullMatch = false; break;
                }
            }
            if (fullMatch)
            {
                Debug.Log($"<color=lime>������ ���͸�ũ ���� �ε��� {i} �߰� (����� ��Ʈ�� ����).</color>");
                return i;
            }
        }
        // Debug.LogWarning("<color=orange>������ ���͸�ũ�� ã�� ���߽��ϴ�.</color>");
        return -1;
    }
}

// ����� ������Ʈ�� DataManager Ŭ���� (���� ���� �ʿ�)
// ����: public static class DataManager { public static byte[] EncryptedOriginData { get; set; } }