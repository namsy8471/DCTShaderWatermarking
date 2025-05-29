// --- InspectorExtractorDebug.cs (수정본) ---
using UnityEngine;
using UnityEngine.Rendering; // AsyncGPUReadbackRequest, GraphicsFormat
using System.Collections;
using System.Collections.Generic;
using System;
using System.Text; // StringBuilder
using System.Linq;
using Unity.Collections; // NativeArray
// using Unity.Mathematics; // 현재 코드에서 직접 사용 안 함
// using UnityEngine.Experimental.Rendering; // GraphicsFormatUtility 관련 (최신 버전에서는 UnityEngine.Rendering)
using System.IO; // 파일 입출력
using UnityEngine.UI;
using UnityEngine.Experimental.Rendering; // 만약 UI 요소와 직접 연결한다면 필요 (현재 코드에는 없음)

// OriginBlock.cs가 프로젝트 내에 있고, 해당 클래스 및 DataManager가 올바르게 설정되어 있다고 가정합니다.
// 예: namespace OriginBlockUtil { public class OriginBlock { ... } } 이라면 using OriginBlockUtil; 추가
// 예: public static class DataManager { public static byte[] EncryptedOriginData { get; set; } }

public class InspectorExtractorDebug : MonoBehaviour
{
    public enum ExtractionMode { LSB, DCT_SS, DWT, SVD } // SVD는 현재 미구현

    [Header("추출 설정")]
    public KeyCode startAutomatedAttackKey = KeyCode.F10; // 자동 공격 시작 키
    public ExtractionMode extractionMode = ExtractionMode.DCT_SS;
    public bool logPixelValuesForFirstBlock = false; // 첫 블록 픽셀 값 로깅 (DCT/DWT) 또는 첫 몇 픽셀 (LSB)

    [Header("JPEG 공격 설정")]
    [Tooltip("쉼표로 구분된 JPEG 품질 값 (예: 90,70,50)")]
    public string jpegQualitiesToTest = "90,70,50,30,10";
    [Tooltip("공격 결과 저장 폴더 이름 (Application.persistentDataPath 하위)")]
    public string outputSubFolderName = "RuntimeJpegAttacks";

    [Header("SS 설정 (DCT_SS, DWT 모드 시 사용)")]
    public string ssSecretKey = "default_secret_key_rgb_ss";
    [Range(1, 63)]
    public int ssCoefficientsToUse = 10;

    [Header("타겟 원본 워터마크 이미지")]
    [Tooltip("워터마크가 삽입된 원본 Texture2D (JPEG 공격 전 상태)")]
    public Texture2D sourceWatermarkedTexture; // Inspector에서 할당

    // 내부 상태 변수
    private bool isProcessingAutomatedAttack = false;
    private bool isAsyncReadbackWaiting = false;

    private const int BLOCK_SIZE = 8;
    private const int HALF_BLOCK_SIZE = BLOCK_SIZE / 2; // DWT용
    // OriginBlock.cs에 정의된 상수를 사용하거나 여기서 일치시켜야 함
    // private const int SYNC_PATTERN_LENGTH = OriginBlock.SYNC_PATTERN_LENGTH;
    // private const int LENGTH_FIELD_BITS = OriginBlock.LENGTH_FIELD_BITS;
    // 직접 정의 시:
    private const int SYNC_PATTERN_LENGTH = 64;
    private const int LENGTH_FIELD_BITS = 16;


    // 동기화 패턴 (OriginBlock.syncPattern과 일치해야 함)
    // OriginBlock.syncPattern (List<uint>)을 사용하거나, 아래 int[]를 List<uint>로 변환해서 사용.
    // FindValidatedWatermarkStartIndex 함수 시그니처에 맞춰 int[] 사용.
    private readonly int[] sync_pattern_int_array_for_search = new int[SYNC_PATTERN_LENGTH] {
        1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,1,0,
        0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,1,1,0,0,1,1,0,0,0,1,1,1,0,1,1,0
    };

    // CPU DCT 계산용 상수
    private const double MATH_PI = 3.141592653589793;
    private readonly double DCT_SQRT1_N = 1.0 / Math.Sqrt(BLOCK_SIZE);
    private readonly double DCT_SQRT2_N = Math.Sqrt(2.0 / BLOCK_SIZE);

    // 현재 공격 루프에서 사용될 변수들
    private int currentProcessingQualityFactor;
    private Texture2D currentAttackedTempTexture;
    private GraphicsFormat currentReadbackFormatForLSB; // LSB 추출 시 사용된 포맷
    private List<uint> masterExpectedFullPayloadBits; // 모든 QF에 대해 동일한 원본 페이로드
    private List<string> attackSummaryLogLines;

    void Start()
    {
        Application.runInBackground = true; // 백그라운드 실행 허용
        Debug.Log("InspectorExtractorDebug 초기화 완료. 자동 공격 시작 키: " + startAutomatedAttackKey);
        // 필요한 경우 DataManager.EncryptedOriginData 초기화 로직 여기에 추가
        // 예: 특정 파일에서 읽어와서 DataManager.EncryptedOriginData = ...;
    }

    void Update()
    {
        if (Input.GetKeyDown(startAutomatedAttackKey) && !isProcessingAutomatedAttack)
        {
            if (!ValidateAutomatedAttackInputs()) return;

            isProcessingAutomatedAttack = true;
            attackSummaryLogLines = new List<string>();
            Debug.Log("===== 자동 JPEG 공격 및 BER 계산 시작 =====");
            StartCoroutine(RunAllJpegAttacksAndExtractCoroutine());
        }
    }

    bool ValidateAutomatedAttackInputs()
    {
        if (sourceWatermarkedTexture == null)
        {
            Debug.LogError("Source Watermarked Texture2D가 할당되지 않았습니다!"); return false;
        }
        if (!sourceWatermarkedTexture.isReadable)
        {
            Debug.LogError("Source Watermarked Texture2D의 Import Settings에서 'Read/Write Enabled'를 반드시 체크해주세요."); return false;
        }
        // DataManager.EncryptedOriginData는 OriginBlock.cs를 통해 암호화된 원본 데이터 byte[] 이어야 함.
        if (DataManager.EncryptedOriginData == null || DataManager.EncryptedOriginData.Length == 0)
        {
            Debug.LogError("DataManager.EncryptedOriginData가 비어있거나 null입니다. 원본 워터마크 데이터를 준비해야 합니다."); return false;
        }
        if (string.IsNullOrWhiteSpace(jpegQualitiesToTest))
        {
            Debug.LogError("JPEG Qualities 문자열이 비어있습니다."); return false;
        }
        try
        {
            var qfs = jpegQualitiesToTest.Split(',').Select(q => int.Parse(q.Trim())).ToArray();
            if (qfs.Length == 0) throw new FormatException("No qualities parsed.");
            foreach (var qf in qfs) if (qf < 0 || qf > 100) throw new FormatException("Quality must be 0-100.");
        }
        catch (FormatException e)
        {
            Debug.LogError($"JPEG 품질 값 문자열 '{jpegQualitiesToTest}' 파싱 오류: {e.Message}"); return false;
        }
        return true;
    }

    IEnumerator RunAllJpegAttacksAndExtractCoroutine()
    {
        // 1. 마스터 예상 페이로드 준비 (모든 QF에 공통으로 사용)
        try
        {
            masterExpectedFullPayloadBits = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);
            if (masterExpectedFullPayloadBits == null || masterExpectedFullPayloadBits.Count == 0)
            {
                Debug.LogError("마스터 예상 페이로드 생성 실패!");
                isProcessingAutomatedAttack = false;
                yield break;
            }
            Debug.Log($"마스터 예상 페이로드 생성 완료: {masterExpectedFullPayloadBits.Count} 비트");
        }
        catch (Exception e)
        {
            Debug.LogError($"마스터 예상 페이로드 생성 중 예외: {e.Message}\n{e.StackTrace}");
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
            Debug.Log($"--- 현재 처리 중인 JPEG 품질(QF): {currentProcessingQualityFactor} ---");

            byte[] jpegBytes = sourceWatermarkedTexture.EncodeToJPG(currentProcessingQualityFactor);
            string attackedImageFileName = $"{sourceWatermarkedTexture.name}_QF{currentProcessingQualityFactor}.jpg";
            string attackedImageFilePath = Path.Combine(techniqueDir, attackedImageFileName);
            File.WriteAllBytes(attackedImageFilePath, jpegBytes);
            Debug.Log($"공격된 이미지 저장됨: {attackedImageFilePath} ({jpegBytes.Length / 1024.0f:F2} KB)");

            // JPEG으로 압축된 데이터를 새 Texture2D로 로드
            currentAttackedTempTexture = new Texture2D(2, 2, (TextureFormat)sourceWatermarkedTexture.graphicsFormat, false, false); // mipChain=false, linear=false (색 공간은 원본 따름)
            if (!currentAttackedTempTexture.LoadImage(jpegBytes))
            {
                Debug.LogError($"QF {currentProcessingQualityFactor}: JPEG 바이트를 Texture2D로 로드 실패.");
                attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: Error loading attacked image into Texture2D.");
                Destroy(currentAttackedTempTexture); currentAttackedTempTexture = null;
                continue;
            }
            // LoadImage 후 텍스처 크기가 원본과 다를 수 있으므로 확인 (보통은 맞춰줌)
            // Debug.Log($"Loaded attacked texture: {currentAttackedTempTexture.width}x{currentAttackedTempTexture.height}, Format: {currentAttackedTempTexture.graphicsFormat}");


            // AsyncGPUReadback에 사용할 그래픽 포맷 결정
            if (extractionMode == ExtractionMode.LSB)
            {
                currentReadbackFormatForLSB = currentAttackedTempTexture.graphicsFormat; // LSB는 현재 텍스처 포맷 그대로 읽기 시도
            }
            else
            { // DCT_SS, DWT
                currentReadbackFormatForLSB = GraphicsFormat.R32G32B32A32_SFloat; // float 데이터 필요
            }

            isAsyncReadbackWaiting = true;
            Debug.Log($"AsyncGPUReadback 요청 (Target: Attacked QF{currentProcessingQualityFactor} Texture, Requested Format: {currentReadbackFormatForLSB})...");
            AsyncGPUReadback.Request(currentAttackedTempTexture, 0, currentReadbackFormatForLSB, OnAttackedImageReadbackComplete);

            yield return new WaitUntil(() => !isAsyncReadbackWaiting); // 콜백 완료 대기

            if (currentAttackedTempTexture != null)
            {
                Destroy(currentAttackedTempTexture);
                currentAttackedTempTexture = null;
            }
            Debug.Log($"--- QF: {currentProcessingQualityFactor} 처리 완료 ---");
            yield return null;
        }

        string summaryFileName = $"Summary_{extractionMode}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        string summaryFilePath = Path.Combine(techniqueDir, summaryFileName);
        File.WriteAllLines(summaryFilePath, attackSummaryLogLines);
        Debug.Log($"===== 모든 JPEG 공격 처리 완료. 요약 파일 저장: {summaryFilePath} =====");
        Debug.Log($"결과 저장 경로: {techniqueDir}");


        isProcessingAutomatedAttack = false;
    }

    // JPEG 공격 후 생성된 이미지에 대한 Readback 완료 콜백
    void OnAttackedImageReadbackComplete(AsyncGPUReadbackRequest request)
    {
        Debug.Log($"Async Readback 완료 (QF {currentProcessingQualityFactor}).");
        if (!request.done || request.hasError || request.layerCount <= 0)
        {
            Debug.LogError($"GPU Readback 오류! QF: {currentProcessingQualityFactor}, HasError: {request.hasError}, LayerCount: {request.layerCount}, Done: {request.done}");
            attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: GPU Readback Error.");
            isAsyncReadbackWaiting = false; return;
        }
        int width = request.width; int height = request.height;
        if (width <= 0 || height <= 0)
        {
            Debug.LogError($"GPU Readback 유효하지 않은 크기 QF {currentProcessingQualityFactor}: {width}x{height}");
            attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: GPU Readback invalid dimensions.");
            isAsyncReadbackWaiting = false; return;
        }

        List<int> extractedBits = new List<int>(); // 추출된 비트는 0 또는 1의 int
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            int numBlocksX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int numBlocksY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int actualTotalBlocksOnCurrentImage = numBlocksX * numBlocksY;

            int maxPossibleBitsFromPayload = masterExpectedFullPayloadBits.Count;


            if (extractionMode == ExtractionMode.DCT_SS)
            {
                Debug.Log($"[QF {currentProcessingQualityFactor}] DCT_SS 모드로 비트 추출 (CPU)...");
                //if (request.format != GraphicsFormat.R32G32B32A32_SFloat)
                //{
                //    Debug.LogError($"DCT_SS Error: Readback format is {request.format}, but R32G32B32A32_SFloat was expected for float data.");
                //}
                NativeArray<float> pixelDataFloat = request.GetData<float>();
                if (logPixelValuesForFirstBlock && pixelDataFloat.Length >= 4) Debug.Log($"[QF {currentProcessingQualityFactor} Pixel(0,0)] R={pixelDataFloat[0]:G9}, G={pixelDataFloat[1]:G9}, B={pixelDataFloat[2]:G9}, A={pixelDataFloat[3]:G9}");

                float[] patternBuffer = GeneratePatternBufferCPU(actualTotalBlocksOnCurrentImage, ssCoefficientsToUse, ssSecretKey);
                Vector2Int[] zigzag = GetZigzagIndices(); // 사용자 기존 코드의 zigzag 배열 사용
                int actualCoeffsToProcess = Math.Min(ssCoefficientsToUse, zigzag.Length); // 사용할 실제 계수 수

                float[,] blockR = new float[BLOCK_SIZE, BLOCK_SIZE];
                float[,] blockG = new float[BLOCK_SIZE, BLOCK_SIZE];
                float[,] blockB = new float[BLOCK_SIZE, BLOCK_SIZE];
                double[,] dctCoeffsR = new double[BLOCK_SIZE, BLOCK_SIZE];
                double[,] dctCoeffsG = new double[BLOCK_SIZE, BLOCK_SIZE];
                double[,] dctCoeffsB = new double[BLOCK_SIZE, BLOCK_SIZE];

                // DCT/DWT는 블록당 1비트 추출하므로, 실제 추출 루프는 actualTotalBlocksOnCurrentImage 만큼 돌아야 함.
                // BER 비교는 masterExpectedFullPayloadBits 길이만큼만 의미가 있음.
                // 추출은 이미지 전체에서 하고, 비교 시 동기화로 찾아냄.
                for (int blockIdx = 0; blockIdx < actualTotalBlocksOnCurrentImage; ++blockIdx)
                {
                    int blkY = blockIdx / numBlocksX; int blkX = blockIdx % numBlocksX;
                    ExtractPixelBlockFloat_CPU(pixelDataFloat, width, height, blkX, blkY, blockR, blockG, blockB);

                    DCT2D_CPU(blockR, dctCoeffsR); DCT2D_CPU(blockG, dctCoeffsG); DCT2D_CPU(blockB, dctCoeffsB);

                    double correlationSumR = 0.0, correlationSumG = 0.0, correlationSumB = 0.0;
                    int patternBaseIndex = blockIdx * actualCoeffsToProcess; // 실제 사용할 계수 수 기준
                    for (int k = 0; k < actualCoeffsToProcess; ++k)
                    {
                        Vector2Int uv = zigzag[k]; // 0부터 actualCoeffsToProcess-1 까지
                        if (patternBaseIndex + k < patternBuffer.Length)
                        {
                            correlationSumR += dctCoeffsR[uv.y, uv.x] * patternBuffer[patternBaseIndex + k];
                            correlationSumG += dctCoeffsG[uv.y, uv.x] * patternBuffer[patternBaseIndex + k];
                            correlationSumB += dctCoeffsB[uv.y, uv.x] * patternBuffer[patternBaseIndex + k];
                        }
                        else { break; } // 패턴 버퍼 범위 초과 시 중단
                    }
                    double finalCorrelation = (correlationSumR + correlationSumG + correlationSumB) / 3.0;
                    extractedBits.Add(finalCorrelation >= 0.0 ? 1 : 0);
                }
            }
            else if (extractionMode == ExtractionMode.LSB)
            {
                Debug.Log($"[QF {currentProcessingQualityFactor}] LSB 모드로 비트 추출 (CPU)...");
                NativeArray<byte> byteData = request.GetData<byte>();
                int bytesPerPixel = GetFormatBytesPerPixel(currentReadbackFormatForLSB); // 이전에 결정된 포맷 사용

                if (bytesPerPixel == 0)
                {
                    Debug.LogError($"[QF {currentProcessingQualityFactor}] LSB: Bytes per pixel for format {currentReadbackFormatForLSB} is 0. Cannot extract.");
                    // 이 경우 extractedBits는 비어있게 됨. BER=1.0 예상.
                }
                else
                {
                    int blueChannelByteOffset = 2; // RGBA32 기본 (R=0, G=1, B=2, A=3)
                    if (currentReadbackFormatForLSB == GraphicsFormat.B8G8R8A8_SRGB || currentReadbackFormatForLSB == GraphicsFormat.B8G8R8A8_UNorm ||
                        currentReadbackFormatForLSB == GraphicsFormat.B8G8R8_SRGB || currentReadbackFormatForLSB == GraphicsFormat.B8G8R8_UNorm)
                        blueChannelByteOffset = 0; // B가 첫 바이트 (B=0, G=1, R=2, A=3)
                    // 예시: R8G8B8_UNorm (RGB24)는 B가 2. (R=0, G=1, B=2)
                    // 더 많은 포맷에 대한 정확한 채널 오프셋을 확인하고 적용해야 합니다.

                    int totalPixels = width * height;
                    if (logPixelValuesForFirstBlock && byteData.Length >= bytesPerPixel * 5)
                    { // 첫 5픽셀 로깅 시도
                        for (int p = 0; p < 5; ++p)
                        {
                            Debug.Log($"[QF {currentProcessingQualityFactor} LSB Pixel {p}] Byte0={byteData[p * bytesPerPixel + 0]}, Byte1={byteData[p * bytesPerPixel + 1]}, Byte2={byteData[p * bytesPerPixel + 2]}" + (bytesPerPixel > 3 ? $", Byte3={byteData[p * bytesPerPixel + 3]}" : ""));
                        }
                    }

                    for (int i = 0; i < totalPixels; ++i) // 이미지 전체 픽셀에서 추출 시도
                    {
                        int pixelY = i / width; int pixelX = i % width;
                        int byteBaseIndex = (pixelY * width + pixelX) * bytesPerPixel;
                        int targetByteIndex = byteBaseIndex + blueChannelByteOffset;
                        if (targetByteIndex < byteData.Length) extractedBits.Add(byteData[targetByteIndex] & 1);
                        else { break; } // 데이터 배열 범위 벗어나면 중단
                    }
                }
            }
            else if (extractionMode == ExtractionMode.DWT)
            {
                Debug.LogWarning($"[QF {currentProcessingQualityFactor}] DWT 추출 모드는 이 코드에서 아직 상세히 구현되지 않았습니다.");
                // 여기에 DWT 추출 로직 (CPU 또는 GPU 호출)
            }

            Debug.Log($"[QF {currentProcessingQualityFactor}] 총 {extractedBits.Count} 비트 추출 완료.");

            // BER 계산
            if (masterExpectedFullPayloadBits == null || masterExpectedFullPayloadBits.Count == 0)
            {
                Debug.LogError($"[QF {currentProcessingQualityFactor}] 기준 페이로드가 없어 BER 계산 불가!");
                attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: BER = N/A (No expected bits) (Extracted: {extractedBits.Count})");
            }
            else
            {
                // FindValidatedWatermarkStartIndex는 List<uint> expected, int[] syncPattern을 받음
                // OriginBlock.syncPattern (List<uint>)을 int[]로 변환하여 전달하거나,
                // 또는 FindValidatedWatermarkStartIndex가 List<uint> syncPattern을 받도록 수정.
                // 여기서는 사용자 코드의 sync_pattern_int_array_for_search (int[])를 사용.
                // 이 int[]는 OriginBlock.syncPattern과 내용이 동일해야 함.
                int syncStartIndex = FindValidatedWatermarkStartIndex_Internal(extractedBits, masterExpectedFullPayloadBits, sync_pattern_int_array_for_search);

                if (syncStartIndex != -1)
                {
                    int expectedDataLenFieldStart = SYNC_PATTERN_LENGTH; // 전역 const 또는 OriginBlock.SYNC_PATTERN_LENGTH
                    int expectedDataLenFieldEnd = expectedDataLenFieldStart + LENGTH_FIELD_BITS; // 전역 const 또는 OriginBlock.LENGTH_FIELD_BITS

                    if (masterExpectedFullPayloadBits.Count < expectedDataLenFieldEnd)
                    {
                        Debug.LogError($"[QF {currentProcessingQualityFactor}] 기준 페이로드가 길이 필드를 포함하기에 너무 짧습니다.");
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
                            Debug.Log($"[QF {currentProcessingQualityFactor}] 기준 페이로드의 데이터 길이가 0입니다. BER = 0.0");
                            attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: BER = 0.000000 (No data to compare) (Extracted: {extractedBits.Count})");
                        }
                        else
                        {
                            int originalDataStartIndexInExpected = expectedDataLenFieldEnd;
                            int extractedDataStartIndexFromSync = syncStartIndex + SYNC_PATTERN_LENGTH + LENGTH_FIELD_BITS;

                            if ((originalDataStartIndexInExpected + actualDataLengthInPayload) > masterExpectedFullPayloadBits.Count ||
                                (extractedDataStartIndexFromSync + actualDataLengthInPayload) > extractedBits.Count)
                            {
                                Debug.LogWarning($"[QF {currentProcessingQualityFactor}] BER 계산 시 데이터 세그먼트가 범위를 벗어납니다. BER = 1.0. ExpectedPayloadLen={masterExpectedFullPayloadBits.Count}, ExtractedLen={extractedBits.Count}, SyncStart={syncStartIndex}, ActualDataLen={actualDataLengthInPayload}");
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
                                Debug.Log($"[QF {currentProcessingQualityFactor}] BER 계산됨: {ber:F6} ({errors} errors / {actualDataLengthInPayload} bits)");
                                attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: BER = {ber:F6} (Extracted: {extractedBits.Count})");
                            }
                        }
                    }
                }
                else
                { // 동기화 실패
                    Debug.LogWarning($"[QF {currentProcessingQualityFactor}] 추출된 비트에서 동기화 패턴을 찾지 못했습니다. BER = 1.0");
                    attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: BER = 1.000000 (Sync failed) (Extracted: {extractedBits.Count})");
                }
            }
            stopwatch.Stop();
            Debug.Log($"[QF {currentProcessingQualityFactor}] CPU 처리 시간 (추출+BER): {stopwatch.Elapsed.TotalMilliseconds:F2} ms.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[QF {currentProcessingQualityFactor}] OnReadbackComplete 처리 중 예외 발생: {e.Message}\n{e.StackTrace}");
            attackSummaryLogLines.Add($"QF {currentProcessingQualityFactor}: Exception - {e.Message}");
        }
        finally
        {
            isAsyncReadbackWaiting = false;
        }
    }

    // 픽셀 블록 추출 (NativeArray<float>에서 float[,,]으로)
    private void ExtractPixelBlockFloat_CPU(NativeArray<float> pixelData, int imgWidth, int imgHeight, int blockXId, int blockYId,
                                       float[,] blockR, float[,] blockG, float[,] blockB)
    {
        for (int y = 0; y < BLOCK_SIZE; ++y)
        {
            for (int x = 0; x < BLOCK_SIZE; ++x)
            {
                int px = blockXId * BLOCK_SIZE + x;
                int py = blockYId * BLOCK_SIZE + y; // (0,0) is top-left (NativeArray는 보통 1D로 쭉 이어짐)
                if (px < imgWidth && py < imgHeight)
                {
                    int baseIdx = (py * imgWidth + px) * 4; // RGBA 순서로 4 float 가정
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

    // 지그재그 인덱스 반환 (사용자 기존 코드와 동일)
    private Vector2Int[] GetZigzagIndices()
    {
        return new Vector2Int[] {
            new Vector2Int(0,1),new Vector2Int(1,0),new Vector2Int(2,0),new Vector2Int(1,1),new Vector2Int(0,2),new Vector2Int(0,3),new Vector2Int(1,2),new Vector2Int(2,1),new Vector2Int(3,0),new Vector2Int(4,0),new Vector2Int(3,1),new Vector2Int(2,2),new Vector2Int(1,3),new Vector2Int(0,4),new Vector2Int(0,5),new Vector2Int(1,4),
            new Vector2Int(2,3),new Vector2Int(3,2),new Vector2Int(4,1),new Vector2Int(5,0),new Vector2Int(6,0),new Vector2Int(5,1),new Vector2Int(4,2),new Vector2Int(3,3),new Vector2Int(2,4),new Vector2Int(1,5),new Vector2Int(0,6),new Vector2Int(0,7),new Vector2Int(1,6),new Vector2Int(2,5),new Vector2Int(3,4),new Vector2Int(4,3),
            new Vector2Int(5,2),new Vector2Int(6,1),new Vector2Int(7,0),new Vector2Int(7,1),new Vector2Int(6,2),new Vector2Int(5,3),new Vector2Int(4,4),new Vector2Int(3,5),new Vector2Int(2,6),new Vector2Int(1,7),new Vector2Int(2,7),new Vector2Int(3,6),new Vector2Int(4,5),new Vector2Int(5,4),new Vector2Int(6,3),new Vector2Int(7,2),
            new Vector2Int(7,3),new Vector2Int(6,4),new Vector2Int(5,5),new Vector2Int(4,6),new Vector2Int(3,7),new Vector2Int(4,7),new Vector2Int(5,6),new Vector2Int(6,5),new Vector2Int(7,4),new Vector2Int(7,5),new Vector2Int(6,6),new Vector2Int(5,7),new Vector2Int(6,7),new Vector2Int(7,6),new Vector2Int(7,7)
        };
    }

    // CPU 기반 1D DCT (사용자 기존 코드와 동일)
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
    // CPU 기반 2D DCT (사용자 기존 코드와 동일)
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

    // CPU에서 패턴 버퍼 생성 (이전 답변의 C# 버전과 동일)
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

    // GraphicsFormat의 픽셀당 바이트 수 결정 (수동 switch)
    private int GetFormatBytesPerPixel(GraphicsFormat format)
    {
        switch (format)
        { // 주요 포맷들, 필요시 추가
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

    // 동기화 패턴 탐색 (사용자 기존 함수와 거의 동일, List<uint> expected, int[] syncPattern 사용)
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

            // 동기화 패턴 일치, 이제 전체 페이로드(expectedFullPayload의 시작 부분)와 비교
            int payloadStartInExtracted = i + syncPatternLength;
            if (payloadStartInExtracted + LENGTH_FIELD_BITS > extractedBits.Count) continue; // 길이 필드 읽을 수 있는지 확인

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
                continue; // 비교할 만큼 충분한 비트가 없는 경우
            }

            bool fullMatch = true;
            // expectedFullPayload의 앞부분(동기화패턴+길이필드+데이터)과 extractedBits의 현재 위치부터를 비교
            for (int k = 0; k < totalLengthToCompareInExpected; k++)
            {
                if (extractedBits[i + k] != (int)expectedFullPayload[k])
                { // expected는 uint, extracted는 int
                    fullMatch = false; break;
                }
            }
            if (fullMatch)
            {
                Debug.Log($"<color=lime>검증된 워터마크 시작 인덱스 {i} 발견 (추출된 비트열 기준).</color>");
                return i;
            }
        }
        // Debug.LogWarning("<color=orange>검증된 워터마크를 찾지 못했습니다.</color>");
        return -1;
    }
}

// 사용자 프로젝트의 DataManager 클래스 (실제 구현 필요)
// 예시: public static class DataManager { public static byte[] EncryptedOriginData { get; set; } }