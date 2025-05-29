using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography; // SHA256 등 필요 시
using System.Text;

// OriginBlock.cs 가 같은 프로젝트 내에 있다고 가정합니다.
// using OriginBlockNamespace; // OriginBlock.cs에 네임스페이스가 있다면 추가

public class ImageAttackEditorWindow : EditorWindow
{
    private Texture2D sourceImage;
    private TextAsset originalWatermarkFile; // Base64 인코딩된 encryptedData가 담긴 파일
    private string jpegQualities = "90,70,50,30,10";
    private string outputDirectory = "Assets/AttackedImages"; // 기본 출력 경로

    private enum WatermarkTechnique { LSB, DCT_SS, DWT }
    private WatermarkTechnique selectedTechnique = WatermarkTechnique.DWT;

    private string ssSecretKey = "OriginBlockData";
    private int ssCoeffsToUse = 10; // DCT: 1-63, DWT: 1-16 (HH_COEFFS_PER_BLOCK_CS)

    private Vector2 scrollPosition;
    private static List<string> logMessages = new List<string>();

    // OriginBlock.cs 에 정의된 상수들을 참고하여 C# 버전에 맞게 정의
    private const int BLOCK_SIZE = 8; // OriginBlock.BLOCK_SIZE_CS 와 동일하게
    private const int HALF_BLOCK_SIZE = BLOCK_SIZE / 2;
    private const int HH_COEFFS_PER_BLOCK = HALF_BLOCK_SIZE * HALF_BLOCK_SIZE; // 16 for 8x8 block

    [MenuItem("Tools/Image Attack Tool")]
    public static void ShowWindow()
    {
        GetWindow<ImageAttackEditorWindow>("Image Attack Tool");
        logMessages.Clear();
    }

    void OnGUI()
    {
        GUILayout.Label("Image Attack Settings", EditorStyles.boldLabel);

        sourceImage = (Texture2D)EditorGUILayout.ObjectField("Source Image (Watermarked)", sourceImage, typeof(Texture2D), false);
        originalWatermarkFile = (TextAsset)EditorGUILayout.ObjectField("Original WM Data File (Base64 Encoded)", originalWatermarkFile, typeof(TextAsset), false);

        jpegQualities = EditorGUILayout.TextField("JPEG Qualities (comma-sep)", jpegQualities);
        outputDirectory = EditorGUILayout.TextField("Output Directory", outputDirectory);
        EditorGUILayout.HelpBox("Output directory should be relative to project root, e.g., Assets/MyOutput.", MessageType.Info);


        selectedTechnique = (WatermarkTechnique)EditorGUILayout.EnumPopup("Watermark Technique", selectedTechnique);

        if (selectedTechnique == WatermarkTechnique.DCT_SS || selectedTechnique == WatermarkTechnique.DWT)
        {
            ssSecretKey = EditorGUILayout.TextField("Spread Spectrum Key", ssSecretKey);
            ssCoeffsToUse = EditorGUILayout.IntField("SS Coefficients to Use", ssCoeffsToUse);
            if (selectedTechnique == WatermarkTechnique.DCT_SS)
                EditorGUILayout.HelpBox("For DCT_SS, typical AC coeffs are 1-63.", MessageType.Info);
            else if (selectedTechnique == WatermarkTechnique.DWT)
                EditorGUILayout.HelpBox($"For DWT, typical HH coeffs are 1-{HH_COEFFS_PER_BLOCK}.", MessageType.Info);
        }

        if (GUILayout.Button("Start JPEG Attack & BER Calculation"))
        {
            if (ValidateInputs())
            {
                logMessages.Clear();
                AddLog("Starting JPEG Attack Process...");
                EditorApplication.delayCall += ProcessAttacks; // UI 업데이트를 위해 다음 프레임에 실행
            }
        }

        GUILayout.Space(10);
        GUILayout.Label("Log", EditorStyles.boldLabel);
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(200));
        foreach (string log in logMessages)
        {
            GUILayout.Label(log);
        }
        EditorGUILayout.EndScrollView();
    }

    private bool ValidateInputs()
    {
        if (sourceImage == null)
        {
            EditorUtility.DisplayDialog("Error", "Please select a source image.", "OK");
            return false;
        }
        if (originalWatermarkFile == null)
        {
            EditorUtility.DisplayDialog("Error", "Please select the original watermark data file (Base64 encoded).", "OK");
            return false;
        }
        if (string.IsNullOrWhiteSpace(jpegQualities))
        {
            EditorUtility.DisplayDialog("Error", "Please enter JPEG qualities.", "OK");
            return false;
        }
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            EditorUtility.DisplayDialog("Error", "Please specify an output directory.", "OK");
            return false;
        }
        if ((selectedTechnique == WatermarkTechnique.DCT_SS || selectedTechnique == WatermarkTechnique.DWT) && string.IsNullOrWhiteSpace(ssSecretKey))
        {
            EditorUtility.DisplayDialog("Error", "Please enter a Spread Spectrum Key for DCT_SS/DWT.", "OK");
            return false;
        }
        if ((selectedTechnique == WatermarkTechnique.DCT_SS || selectedTechnique == WatermarkTechnique.DWT) && ssCoeffsToUse <= 0)
        {
            EditorUtility.DisplayDialog("Error", "SS Coefficients to Use must be greater than 0.", "OK");
            return false;
        }
        return true;
    }

    private static void AddLog(string message)
    {
        string timestamp = DateTime.Now.ToString("[HH:mm:ss] ");
        logMessages.Add(timestamp + message);
        Debug.Log(message); // Unity Console에도 출력
        // 에디터 창을 강제로 다시 그리게 하려면 GetWindow<ImageAttackEditorWindow>().Repaint(); 필요
    }

    private void ProcessAttacks()
    {
        // 1. 원본 워터마크 페이로드 재구성 (Base64 파일로부터)
        List<uint> expectedPayloadBits = null;
        try
        {
            string base64Content = originalWatermarkFile.text;
            byte[] encryptedData = Convert.FromBase64String(base64Content);
            expectedPayloadBits = OriginBlock.ConstructPayloadWithHeader(encryptedData); // OriginBlock.cs의 static 메소드 사용

            if (expectedPayloadBits == null || expectedPayloadBits.Count == 0)
            {
                AddLog("Error: Failed to reconstruct expected payload from watermark data file. ConstructPayloadWithHeader returned null or empty.");
                EditorUtility.ClearProgressBar();
                return;
            }
            AddLog($"Expected payload reconstructed. Total bits: {expectedPayloadBits.Count}");
        }
        catch (Exception ex)
        {
            AddLog($"Error processing original watermark file: {ex.Message}");
            EditorUtility.ClearProgressBar();
            return;
        }

        string[] qualitiesStr = jpegQualities.Split(',').Select(q => q.Trim()).ToArray();
        int[] qualities = Array.ConvertAll(qualitiesStr, int.Parse);

        if (!Directory.Exists(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }
        string techniqueOutputDir = Path.Combine(outputDirectory, selectedTechnique.ToString());
        if (!Directory.Exists(techniqueOutputDir))
        {
            Directory.CreateDirectory(techniqueOutputDir);
        }

        List<string> summaryLines = new List<string>();
        summaryLines.Add($"JPEG Compression Attack Summary: {selectedTechnique}");
        summaryLines.Add($"Input Watermarked Image: {AssetDatabase.GetAssetPath(sourceImage)}");
        summaryLines.Add($"Original Watermark Base64 File: {AssetDatabase.GetAssetPath(originalWatermarkFile)}");
        if (expectedPayloadBits != null)
        {
            summaryLines.Add($"Original WM Reconstructed Payload (total bits): {expectedPayloadBits.Count}");
        }
        if (selectedTechnique == WatermarkTechnique.DCT_SS || selectedTechnique == WatermarkTechnique.DWT)
        {
            summaryLines.Add($"SS Key: {ssSecretKey}, SS Coeffs: {ssCoeffsToUse}");
        }
        summaryLines.Add("Quality Factors Tested: " + string.Join(", ", qualitiesStr));
        summaryLines.Add("-------------------------------------------------");


        // 원본 Texture2D가 Read/Write Enabled인지 확인 및 설정 (필요시)
        TextureImporter importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(sourceImage)) as TextureImporter;
        bool originalReadableState = false;
        if (importer != null)
        {
            originalReadableState = importer.isReadable;
            if (!originalReadableState)
            {
                importer.isReadable = true;
                importer.SaveAndReimport();
                AddLog("Source image was not readable, re-importing as readable.");
            }
        }
        else
        {
            AddLog("Warning: Could not get TextureImporter for source image. Ensure it's readable.");
        }


        for (int i = 0; i < qualities.Length; i++)
        {
            int qf = qualities[i];
            string progressTitle = $"Processing JPEG QF: {qf}";
            EditorUtility.DisplayProgressBar(progressTitle, $"Attacking with QF {qf}...", (float)i / qualities.Length);
            AddLog($"Processing QF {qf} ({selectedTechnique})...");

            byte[] jpgBytes = sourceImage.EncodeToJPG(qf);
            string attackedFileName = $"{Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(sourceImage))}_jpeg_qf_{qf}.jpg";
            string attackedFilePath = Path.Combine(techniqueOutputDir, attackedFileName);
            File.WriteAllBytes(attackedFilePath, jpgBytes);
            AddLog($"Attacked image saved: {attackedFileName}");

            // 공격받은 이미지를 다시 Texture2D로 로드 (Read/Write Enabled 필요)
            Texture2D attackedTexture = new Texture2D(sourceImage.width, sourceImage.height, sourceImage.format, false); // Mipmap false
            attackedTexture.LoadImage(jpgBytes); // LoadImage는 자동으로 Read/Write 가능하게 함 (임시 텍스처)

            if (attackedTexture == null)
            {
                AddLog($"Error: Could not load attacked image back into Texture2D for QF {qf}.");
                summaryLines.Add($"QF {qf}: Error loading attacked image.");
                continue;
            }

            List<uint> extractedBits = ExtractWatermark(attackedTexture, selectedTechnique, ssSecretKey, ssCoeffsToUse);
            if (extractedBits == null)
            {
                AddLog($"Error: Watermark extraction returned null for QF {qf}.");
                summaryLines.Add($"QF {qf}: Extraction failed or returned null.");
                DestroyImmediate(attackedTexture); // 임시 텍스처 해제
                continue;
            }

            double ber = CalculateBER(expectedPayloadBits, extractedBits);

            string resultStr = $"QF {qf}: BER = {ber:F6} (Extracted bits: {extractedBits.Count})";
            AddLog($"  Result: {resultStr}");
            summaryLines.Add(resultStr);

            DestroyImmediate(attackedTexture); // 임시 텍스처 해제
        }

        // 원본 이미지의 isReadable 상태 복원 (필요시)
        if (importer != null && importer.isReadable != originalReadableState)
        {
            importer.isReadable = originalReadableState;
            importer.SaveAndReimport();
            AddLog("Restored original readable state of source image.");
        }

        EditorUtility.ClearProgressBar();

        string summaryFileName = $"{selectedTechnique}_jpeg_summary_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        string summaryFilePath = Path.Combine(techniqueOutputDir, summaryFileName);
        File.WriteAllLines(summaryFilePath, summaryLines);
        AddLog($"All attacks complete. Summary file: {summaryFilePath}");
        EditorUtility.DisplayDialog("Complete", $"All attacks complete.\nSummary: {summaryFilePath}", "OK");
        // GetWindow<ImageAttackEditorWindow>().Repaint(); // 로그 업데이트를 위해
    }

    // --- 워터마크 추출 로직 (C# 포팅 필요) ---
    private List<uint> ExtractWatermark(Texture2D image, WatermarkTechnique technique, string key, int coeffsToUse)
    {
        // Texture2D.isReadable을 true로 설정해야 GetPixels/GetPixels32 사용 가능
        // LoadImage로 생성된 Texture2D는 기본적으로 readable.
        if (!image.isReadable)
        {
            AddLog("Error: Texture for extraction is not readable!");
            return new List<uint>(); // 또는 null
        }

        List<uint> extractedBits = new List<uint>();
        Color32[] pixels = null; // LSB용
        float[,,] pixelFloatRgb = null; // DCT/DWT용 (0.0 - 1.0 정규화)

        int width = image.width;
        int height = image.height;

        // 블록 계산 (Python과 동일하게)
        int numBlocksX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
        int numBlocksY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
        int totalImageBlocks = numBlocksX * numBlocksY;

        if (technique == WatermarkTechnique.LSB)
        {
            pixels = image.GetPixels32(); // GetPixels32()는 왼쪽 아래부터 시작, 위로, 그 다음 오른쪽으로.
                                          // Python의 numpy 배열은 보통 위쪽 왼쪽부터 시작. 인덱싱 주의.
                                          // 여기서는 Python과 유사하게 순차적으로 처리 가정.
            int maxBitsToExtractLsb = width * height; // B 채널에서만 추출 가정
            for (int i = 0; i < maxBitsToExtractLsb; i++)
            {
                // Python: pixel_y, pixel_x = bit_idx // width, bit_idx % width
                // C# GetPixels32: index = y * width + x
                // (y가 0부터 height-1, x가 0부터 width-1)
                // Python과 동일한 순서로 하려면 y를 뒤집어주거나 (height-1-y) 해야할 수 있음.
                // 여기서는 단순 순차적 접근으로 가정. (Python LSB 추출 로직과의 정확한 일치 필요)
                int pixelY = i / width; // Python: bit_idx // width
                int pixelX = i % width; // Python: bit_idx % width
                int cSharpIndex = pixelY * width + pixelX; // Python의 (pixel_y, pixel_x) 접근과 동일하게 하려면 이 인덱싱 확인 필요

                // Python 코드는 RGB 순서로 float 배열을 만들지만, 여기서는 Color32 사용
                // Color32.b 는 blue 채널
                if (cSharpIndex < pixels.Length)
                {
                    extractedBits.Add((uint)(pixels[cSharpIndex].b & 1));
                }
                else break;
            }
        }
        else // DCT_SS or DWT
        {
            // 픽셀 데이터를 float[height, width, 3] 형태로 변환 (0.0-1.0)
            pixelFloatRgb = ConvertTextureToFloatArray(image);

            // 패턴 버퍼 생성 (Python의 generate_pattern_buffer_py와 유사하게)
            // 주의: key.GetHashCode()는 플랫폼/버전별로 다를 수 있음. 일관성 있는 시드 생성 중요.
            // Python의 단순 합산 방식 시드는 C#의 GetHashCode()와 다름.
            // 여기서는 C# 표준 GetHashCode() 사용 가정. 삽입 시에도 동일 방식 사용해야 함.
            int actualCoeffsToUse = coeffsToUse;
            if (technique == WatermarkTechnique.DWT)
            {
                actualCoeffsToUse = Math.Min(coeffsToUse, HH_COEFFS_PER_BLOCK);
            }
            else if (technique == WatermarkTechnique.DCT_SS)
            {
                // DCT는 보통 DC 제외 63 AC 계수 사용. Zigzag 인덱스 길이와 비교.
                actualCoeffsToUse = Math.Min(coeffsToUse, BLOCK_SIZE * BLOCK_SIZE - 1);
            }
            if (actualCoeffsToUse <= 0)
            {
                AddLog("Error: actualCoeffsToUse is 0 or less.");
                return new List<uint>();
            }

            float[] patternBuffer = GeneratePatternBuffer(totalImageBlocks, actualCoeffsToUse, key);
            if (patternBuffer == null || patternBuffer.Length == 0)
            {
                AddLog("Error: Failed to generate pattern buffer or it's empty.");
                return new List<uint>();
            }


            for (int blockIdx = 0; blockIdx < totalImageBlocks; blockIdx++)
            {
                // 1. 현재 블록 데이터 추출 (R, G, B 각 채널별 8x8 float 배열)
                float[,] blockR = new float[BLOCK_SIZE, BLOCK_SIZE];
                float[,] blockG = new float[BLOCK_SIZE, BLOCK_SIZE];
                float[,] blockB = new float[BLOCK_SIZE, BLOCK_SIZE];
                ExtractBlockData(pixelFloatRgb, width, height, blockIdx, numBlocksX, blockR, blockG, blockB);

                float[,] transformedR, transformedG, transformedB;
                float correlationSumR = 0, correlationSumG = 0, correlationSumB = 0;
                int patternBaseIdx = blockIdx * actualCoeffsToUse;

                if (technique == WatermarkTechnique.DCT_SS)
                {
                    transformedR = PerformDCT(blockR);
                    transformedG = PerformDCT(blockG);
                    transformedB = PerformDCT(blockB);
                    List<Tuple<int, int>> zigzagIndices = GetZigzagIndices(); // (u,v) or (col,row)

                    for (int k = 0; k < actualCoeffsToUse; k++)
                    {
                        if (patternBaseIdx + k >= patternBuffer.Length) break;
                        if (k >= zigzagIndices.Count) break; // 사용할 지그재그 계수 초과 방지

                        int u = zigzagIndices[k].Item1; // col
                        int v = zigzagIndices[k].Item2; // row
                        float pVal = patternBuffer[patternBaseIdx + k];
                        // DCT 계수는 (row, col) 접근
                        correlationSumR += transformedR[v, u] * pVal;
                        correlationSumG += transformedG[v, u] * pVal;
                        correlationSumB += transformedB[v, u] * pVal;
                    }
                }
                else // DWT
                {
                    transformedR = PerformHaarDWT(blockR); // 결과는 LL,LH,HL,HH 순서로 배치된 8x8 배열 가정
                    transformedG = PerformHaarDWT(blockG);
                    transformedB = PerformHaarDWT(blockB);

                    for (int k_hh = 0; k_hh < actualCoeffsToUse; k_hh++)
                    {
                        if (patternBaseIdx + k_hh >= patternBuffer.Length) break;
                        // HH 부대역 내에서의 2D 인덱스 (Python: v_hh, u_hh)
                        int v_hh = k_hh / HALF_BLOCK_SIZE;
                        int u_hh = k_hh % HALF_BLOCK_SIZE;
                        // 전체 8x8 DWT 계수 배열에서의 HH 부대역 좌표 (Python: v_global, u_global)
                        int v_global = v_hh + HALF_BLOCK_SIZE;
                        int u_global = u_hh + HALF_BLOCK_SIZE;

                        float pVal = patternBuffer[patternBaseIdx + k_hh];
                        correlationSumR += transformedR[v_global, u_global] * pVal;
                        correlationSumG += transformedG[v_global, u_global] * pVal;
                        correlationSumB += transformedB[v_global, u_global] * pVal;
                    }
                }
                float finalCorrelation = (correlationSumR + correlationSumG + correlationSumB) / 3.0f;
                extractedBits.Add(finalCorrelation >= 0.0f ? 1u : 0u);

                if (extractedBits.Count >= totalImageBlocks * 2 && totalImageBlocks > 0)
                { // Python의 안전장치 유사
                    AddLog($"Warning: Safety break in bit extraction. Extracted {extractedBits.Count} bits.");
                    break;
                }
            }
        }
        AddLog($"Extraction complete for technique {technique}. Total bits extracted: {extractedBits.Count}");
        // Python에서 extracted_info_list_int 의 길이가 32400으로 나왔던 부분에 대한 디버깅 로그 추가
        if ((technique == WatermarkTechnique.DCT_SS || technique == WatermarkTechnique.DWT) && extractedBits.Count != totalImageBlocks && totalImageBlocks > 0)
        {
            AddLog($"Warning: Extracted bit count ({extractedBits.Count}) does not match total_image_blocks ({totalImageBlocks}) for {technique}.");
        }

        return extractedBits;
    }


    // --- BER 계산 로직 (C# 포팅 필요) ---
    private double CalculateBER(List<uint> expectedBits, List<uint> extractedBits)
    {
        if (extractedBits == null || extractedBits.Count == 0) return 1.0;
        if (expectedBits == null || expectedBits.Count == 0) return 1.0; // 원본이 없으면 비교 불가

        // Python의 find_validated_watermark_start_index_py 포팅
        int syncStartIndex = FindValidatedWatermarkStartIndex(extractedBits, expectedBits, OriginBlock.syncPattern.ToList()); // OriginBlock.syncPattern은 static readonly List<uint>

        if (syncStartIndex == -1) return 1.0; // 동기화 실패

        // Python 로직과 동일하게, expectedBits 헤더에서 실제 데이터 길이 추출
        int expectedLenFieldStart = OriginBlock.SYNC_PATTERN_LENGTH; // OriginBlock에 정의된 상수 사용
        int expectedLenFieldEnd = expectedLenFieldStart + OriginBlock.LENGTH_FIELD_BITS;

        if (expectedBits.Count < expectedLenFieldEnd) return 1.0; // 너무 짧음

        int actualDataLenInExpected = 0;
        for (int bitIdx = 0; bitIdx < OriginBlock.LENGTH_FIELD_BITS; bitIdx++)
        {
            if (expectedBits[expectedLenFieldStart + bitIdx] == 1)
            {
                actualDataLenInExpected |= (1 << (OriginBlock.LENGTH_FIELD_BITS - 1 - bitIdx));
            }
        }

        if (actualDataLenInExpected == 0) return 0.0; // 비교할 데이터 없음

        int originalDataStartIdx = expectedLenFieldEnd;
        int originalDataEndIdx = originalDataStartIdx + actualDataLenInExpected;

        int extractedDataStartIdx = syncStartIndex + OriginBlock.SYNC_PATTERN_LENGTH + OriginBlock.LENGTH_FIELD_BITS;
        int extractedDataEndIdx = extractedDataStartIdx + actualDataLenInExpected;

        if (originalDataEndIdx > expectedBits.Count || extractedDataEndIdx > extractedBits.Count)
        {
            return 1.0; // 범위 초과
        }

        List<uint> originalDataSegment = expectedBits.GetRange(originalDataStartIdx, actualDataLenInExpected);
        List<uint> extractedDataSegment = extractedBits.GetRange(extractedDataStartIdx, actualDataLenInExpected);

        if (originalDataSegment.Count != extractedDataSegment.Count) return 1.0; // 길이 불일치 (이론상 발생 안 함)

        int errors = 0;
        for (int i = 0; i < originalDataSegment.Count; i++)
        {
            if (originalDataSegment[i] != extractedDataSegment[i])
            {
                errors++;
            }
        }
        return (double)errors / originalDataSegment.Count;
    }

    // Python의 find_validated_watermark_start_index_py C# 버전
    private int FindValidatedWatermarkStartIndex(List<uint> extractedBits, List<uint> expectedFullPayload, List<uint> syncPattern)
    {
        int syncPatternLength = syncPattern.Count;
        if (extractedBits == null || expectedFullPayload == null || syncPattern == null ||
            syncPatternLength == 0 || extractedBits.Count < syncPatternLength || expectedFullPayload.Count < syncPatternLength)
        {
            return -1;
        }

        for (int i = 0; i <= extractedBits.Count - syncPatternLength; i++)
        {
            bool syncMatch = true;
            for (int kSync = 0; kSync < syncPatternLength; kSync++)
            {
                if (extractedBits[i + kSync] != syncPattern[kSync])
                {
                    syncMatch = false;
                    break;
                }
            }
            if (!syncMatch) continue;

            // 동기화 패턴 일치, 이제 전체 페이로드(expectedFullPayload의 시작 부분)와 비교
            int payloadStartInExtracted = i + syncPatternLength;
            if (payloadStartInExtracted + OriginBlock.LENGTH_FIELD_BITS > extractedBits.Count) continue;

            int actualDataLenFromExtractedHeader = 0;
            for (int bitIdx = 0; bitIdx < OriginBlock.LENGTH_FIELD_BITS; bitIdx++)
            {
                if (extractedBits[payloadStartInExtracted + bitIdx] == 1)
                {
                    actualDataLenFromExtractedHeader |= (1 << (OriginBlock.LENGTH_FIELD_BITS - 1 - bitIdx));
                }
            }

            int totalExpectedLengthForThisMatch = syncPatternLength + OriginBlock.LENGTH_FIELD_BITS + actualDataLenFromExtractedHeader;

            if (expectedFullPayload.Count < totalExpectedLengthForThisMatch ||
                i + totalExpectedLengthForThisMatch > extractedBits.Count)
            {
                continue;
            }

            bool fullMatch = true;
            // 동기화 패턴은 이미 맞음. expectedFullPayload의 시작부터 totalExpectedLengthForThisMatch 만큼을
            // extractedBits의 i 위치부터 비교
            for (int k = 0; k < totalExpectedLengthForThisMatch; k++)
            {
                if (extractedBits[i + k] != expectedFullPayload[k])
                {
                    fullMatch = false;
                    break;
                }
            }
            if (fullMatch) return i;
        }
        return -1;
    }


    // --- 이미지 처리 및 변환 헬퍼 함수들 ---
    private float[,,] ConvertTextureToFloatArray(Texture2D tex)
    {
        // Color[]는 0.0f-1.0f 범위의 float 값을 가짐
        Color[] pixelsColor = tex.GetPixels(); // 왼쪽 아래부터 시작
        int width = tex.width;
        int height = tex.height;
        float[,,] floatArray = new float[height, width, 3]; // [y, x, channel]

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // GetPixels는 1D 배열이므로 2D 인덱스로 변환. Unity 텍스처 좌표계 고려.
                // (0,0)은 왼쪽 아래. Python numpy는 보통 왼쪽 위.
                // Python과 동일한 [y_py, x_py] 접근을 위해 y좌표를 뒤집을 수 있음: int unityY = height - 1 - y;
                // 여기서는 일단 Unity의 기본 순서대로 처리하고, 블록 추출 시 Python과 맞춤.
                int index = y * width + x;
                floatArray[y, x, 0] = pixelsColor[index].r;
                floatArray[y, x, 1] = pixelsColor[index].g;
                floatArray[y, x, 2] = pixelsColor[index].b;
            }
        }
        return floatArray;
    }

    private void ExtractBlockData(float[,,] pixelFloatRgb, int imgWidth, int imgHeight, int blockIdx, int numBlocksX,
                                  float[,] blockR, float[,] blockG, float[,] blockB)
    {
        // Python의 extract_block_data_py 와 동일 로직
        int blockYCoord = blockIdx / numBlocksX;
        int blockXCoord = blockIdx % numBlocksX;

        for (int yInBlock = 0; yInBlock < BLOCK_SIZE; yInBlock++)
        {
            for (int xInBlock = 0; xInBlock < BLOCK_SIZE; xInBlock++)
            {
                int pixelX = blockXCoord * BLOCK_SIZE + xInBlock;
                // Python pixel_array_rgb_float_norm[pixel_y, pixel_x, c]
                // Python의 y는 위에서 아래, x는 왼쪽에서 오른쪽.
                // ConvertTextureToFloatArray에서 y를 이미 Python식으로 (0=top) 뒤집었다고 가정하거나,
                // 여기서 pixelFloatRgb 접근 시 y좌표를 (imgHeight - 1 - (blockYCoord * BLOCK_SIZE + yInBlock)) 식으로 변환.
                // 여기서는 pixelFloatRgb가 이미 [0=top, height-1=bottom]으로 정렬되어 있다고 가정 (ConvertTextureToFloatArray 수정 필요 시)
                // 또는, Python의 pixel_array_rgb_float_norm[pixel_y, pixel_x, 0] 처럼 접근하기 위해
                // pixelFloatRgb의 y 인덱스를 Python식으로 사용:
                int pixelY_python = blockYCoord * BLOCK_SIZE + yInBlock;

                if (pixelX < imgWidth && pixelY_python < imgHeight)
                {
                    // pixelFloatRgb가 [unityY, unityX, c] (unityY는 아래부터 위) 라면,
                    // int unityPixelY = imgHeight - 1 - pixelY_python;
                    // blockR[yInBlock, xInBlock] = pixelFloatRgb[unityPixelY, pixelX, 0];
                    // ...
                    // 여기서는 pixelFloatRgb가 이미 [pythonY, pythonX, c]로 되어 있다고 가정
                    blockR[yInBlock, xInBlock] = pixelFloatRgb[pixelY_python, pixelX, 0];
                    blockG[yInBlock, xInBlock] = pixelFloatRgb[pixelY_python, pixelX, 1];
                    blockB[yInBlock, xInBlock] = pixelFloatRgb[pixelY_python, pixelX, 2];
                }
                else // 블록이 이미지 경계를 벗어나는 경우 0으로 채움 (패딩)
                {
                    blockR[yInBlock, xInBlock] = 0f;
                    blockG[yInBlock, xInBlock] = 0f;
                    blockB[yInBlock, xInBlock] = 0f;
                }
            }
        }
    }

    // 패턴 생성 (Python의 generate_pattern_buffer_py 와 유사)
    private float[] GeneratePatternBuffer(int totalBlocks, int coeffsPerBlock, string seedKey)
    {
        if (totalBlocks <= 0 || coeffsPerBlock <= 0) return new float[0];

        // C#에서 문자열 시드로부터 일관된 정수 시드 얻기 (GetHashCode 사용)
        // 주의: GetHashCode() 결과는 .NET 버전/플랫폼에 따라 다를 수 있다는 점을 인지해야 하나,
        // 동일 환경 내(Unity 에디터)에서는 보통 일관됨.
        // 삽입 시에도 동일한 방식으로 시드를 생성해야 함.
        int seedIntValue = 0;
        if (!string.IsNullOrEmpty(seedKey))
        {
            seedIntValue = seedKey.GetHashCode();
        }

        System.Random rng = new System.Random(seedIntValue);
        int bufferSize = totalBlocks * coeffsPerBlock;
        float[] buffer = new float[bufferSize];
        for (int i = 0; i < bufferSize; i++)
        {
            buffer[i] = (rng.NextDouble() < 0.5) ? -1.0f : 1.0f;
        }
        return buffer;
    }

    // 지그재그 인덱스 (Python의 get_zigzag_indices_py 와 동일 순서)
    private List<Tuple<int, int>> GetZigzagIndices() // (u,v) 즉 (col,row)
    {
        return new List<Tuple<int, int>> {
            new Tuple<int,int>(0,1),new Tuple<int,int>(1,0),new Tuple<int,int>(2,0),new Tuple<int,int>(1,1),new Tuple<int,int>(0,2),new Tuple<int,int>(0,3),new Tuple<int,int>(1,2),new Tuple<int,int>(2,1),new Tuple<int,int>(3,0),new Tuple<int,int>(4,0),new Tuple<int,int>(3,1),new Tuple<int,int>(2,2),new Tuple<int,int>(1,3),new Tuple<int,int>(0,4),new Tuple<int,int>(0,5),new Tuple<int,int>(1,4),
            new Tuple<int,int>(2,3),new Tuple<int,int>(3,2),new Tuple<int,int>(4,1),new Tuple<int,int>(5,0),new Tuple<int,int>(6,0),new Tuple<int,int>(5,1),new Tuple<int,int>(4,2),new Tuple<int,int>(3,3),new Tuple<int,int>(2,4),new Tuple<int,int>(1,5),new Tuple<int,int>(0,6),new Tuple<int,int>(0,7),new Tuple<int,int>(1,6),new Tuple<int,int>(2,5),new Tuple<int,int>(3,4),new Tuple<int,int>(4,3),
            new Tuple<int,int>(5,2),new Tuple<int,int>(6,1),new Tuple<int,int>(7,0),new Tuple<int,int>(7,1),new Tuple<int,int>(6,2),new Tuple<int,int>(5,3),new Tuple<int,int>(4,4),new Tuple<int,int>(3,5),new Tuple<int,int>(2,6),new Tuple<int,int>(1,7),new Tuple<int,int>(2,7),new Tuple<int,int>(3,6),new Tuple<int,int>(4,5),new Tuple<int,int>(5,4),new Tuple<int,int>(6,3),new Tuple<int,int>(7,2),
            new Tuple<int,int>(7,3),new Tuple<int,int>(6,4),new Tuple<int,int>(5,5),new Tuple<int,int>(4,6),new Tuple<int,int>(3,7),new Tuple<int,int>(4,7),new Tuple<int,int>(5,6),new Tuple<int,int>(6,5),new Tuple<int,int>(7,4),new Tuple<int,int>(7,5),new Tuple<int,int>(6,6),new Tuple<int,int>(5,7),new Tuple<int,int>(6,7),new Tuple<int,int>(7,6),new Tuple<int,int>(7,7)
        }.Take(BLOCK_SIZE * BLOCK_SIZE - 1).ToList(); // DC 제외 최대 63개 AC 계수
    }

    // --- DCT 및 DWT 구현 (8x8 블록 대상) ---
    private float[,] PerformDCT(float[,] blockData)
    {
        // 8x8 DCT-II 구현 (Python의 scipy.fft.dctn(..., type=2, norm='ortho')와 유사하게)
        // 직접 구현하거나, Accord.NET 같은 라이브러리 사용 가능 (에디터 스크립트에서는 직접 구현이 간단할 수 있음)
        // 아래는 기본적인 DCT-II 공식에 기반한 직접 구현 예시 (정규화 포함)
        int N = BLOCK_SIZE;
        float[,] dctCoeffs = new float[N, N];
        float c_u, c_v;
        float sum;

        for (int u = 0; u < N; u++) // u는 주파수 (가로)
        {
            for (int v = 0; v < N; v++) // v는 주파수 (세로)
            {
                sum = 0.0f;
                for (int x = 0; x < N; x++) // x는 공간 (가로)
                {
                    for (int y = 0; y < N; y++) // y는 공간 (세로)
                    {
                        sum += blockData[y, x] *
                               Mathf.Cos((2 * x + 1) * u * Mathf.PI / (2.0f * N)) *
                               Mathf.Cos((2 * y + 1) * v * Mathf.PI / (2.0f * N));
                    }
                }
                c_u = (u == 0) ? (1.0f / Mathf.Sqrt(N)) : (Mathf.Sqrt(2.0f / N));
                c_v = (v == 0) ? (1.0f / Mathf.Sqrt(N)) : (Mathf.Sqrt(2.0f / N));
                // Scipy의 'ortho' 정규화는 전체적으로 2/N 또는 sqrt(2/N)*sqrt(2/N) 계수를 곱하는 방식.
                // JPEG 표준 DCT는 정규화 방식이 약간 다를 수 있음. Python scipy의 ortho와 맞추려면
                // 계수 c_u, c_v를 곱하고, 추가로 1/sqrt(N*N) * 2*2 = 4/N 또는 sqrt(1/N) * sqrt(1/N) * 2*2 (u,v!=0)
                // Python Scipy의 ortho는 (2/N) * sum * (C_u_norm_factor * C_v_norm_factor)
                // C_u_norm_factor = 1/sqrt(2) if u=0 or N/2 else 1.
                // 더 간단하게는, Python의 dctn(type=2, norm='ortho') 결과를 C#에서 동일하게 나오도록
                // 계수 스케일링을 맞추거나, Python에서 사용한 scipy 코드를 C#으로 직접 포팅.
                // 여기서는 JPEG 표준에 가까운 정규화를 사용.
                float alpha_u = (u == 0) ? 1.0f / Mathf.Sqrt(2.0f) : 1.0f;
                float alpha_v = (v == 0) ? 1.0f / Mathf.Sqrt(2.0f) : 1.0f;
                dctCoeffs[v, u] = (2.0f / N) * alpha_u * alpha_v * sum; // JPEG 스타일 정규화 (v,u 순서 주의)
            }
        }
        return dctCoeffs;
    }

    private float[,] PerformHaarDWT(float[,] blockData)
    {
        // 8x8 Haar DWT 구현 (Python의 pywt.dwt2(..., 'haar', mode='periodization')와 유사하게)
        // LL, LH, HL, HH 순서로 결과 배열에 저장
        int N = BLOCK_SIZE;
        float[,] temp = new float[N, N];
        float[,] dwtCoeffs = new float[N, N];

        // Rows
        for (int r = 0; r < N; r++)
        {
            for (int c = 0; c < N / 2; c++)
            {
                temp[r, c] = (blockData[r, 2 * c] + blockData[r, 2 * c + 1]) / Mathf.Sqrt(2.0f); // Approximation (LL, LH)
                temp[r, c + N / 2] = (blockData[r, 2 * c] - blockData[r, 2 * c + 1]) / Mathf.Sqrt(2.0f); // Detail (HL, HH)
            }
        }
        // Columns
        for (int c = 0; c < N; c++)
        {
            for (int r = 0; r < N / 2; r++)
            {
                dwtCoeffs[r, c] = (temp[2 * r, c] + temp[2 * r + 1, c]) / Mathf.Sqrt(2.0f);
                dwtCoeffs[r + N / 2, c] = (temp[2 * r, c] - temp[2 * r + 1, c]) / Mathf.Sqrt(2.0f);
            }
        }
        // 재배치: LL, LH, HL, HH (Python pywt 결과와 동일하게)
        // 현재 dwtCoeffs는 이미 그 순서로 되어있음.
        // LL: [0..N/2-1, 0..N/2-1]
        // LH: [0..N/2-1, N/2..N-1]
        // HL: [N/2..N-1, 0..N/2-1]
        // HH: [N/2..N-1, N/2..N-1]
        return dwtCoeffs;
    }
}