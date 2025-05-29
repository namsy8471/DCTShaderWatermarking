using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography; // SHA256 �� �ʿ� ��
using System.Text;

// OriginBlock.cs �� ���� ������Ʈ ���� �ִٰ� �����մϴ�.
// using OriginBlockNamespace; // OriginBlock.cs�� ���ӽ����̽��� �ִٸ� �߰�

public class ImageAttackEditorWindow : EditorWindow
{
    private Texture2D sourceImage;
    private TextAsset originalWatermarkFile; // Base64 ���ڵ��� encryptedData�� ��� ����
    private string jpegQualities = "90,70,50,30,10";
    private string outputDirectory = "Assets/AttackedImages"; // �⺻ ��� ���

    private enum WatermarkTechnique { LSB, DCT_SS, DWT }
    private WatermarkTechnique selectedTechnique = WatermarkTechnique.DWT;

    private string ssSecretKey = "OriginBlockData";
    private int ssCoeffsToUse = 10; // DCT: 1-63, DWT: 1-16 (HH_COEFFS_PER_BLOCK_CS)

    private Vector2 scrollPosition;
    private static List<string> logMessages = new List<string>();

    // OriginBlock.cs �� ���ǵ� ������� �����Ͽ� C# ������ �°� ����
    private const int BLOCK_SIZE = 8; // OriginBlock.BLOCK_SIZE_CS �� �����ϰ�
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
                EditorApplication.delayCall += ProcessAttacks; // UI ������Ʈ�� ���� ���� �����ӿ� ����
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
        Debug.Log(message); // Unity Console���� ���
        // ������ â�� ������ �ٽ� �׸��� �Ϸ��� GetWindow<ImageAttackEditorWindow>().Repaint(); �ʿ�
    }

    private void ProcessAttacks()
    {
        // 1. ���� ���͸�ũ ���̷ε� �籸�� (Base64 ���Ϸκ���)
        List<uint> expectedPayloadBits = null;
        try
        {
            string base64Content = originalWatermarkFile.text;
            byte[] encryptedData = Convert.FromBase64String(base64Content);
            expectedPayloadBits = OriginBlock.ConstructPayloadWithHeader(encryptedData); // OriginBlock.cs�� static �޼ҵ� ���

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


        // ���� Texture2D�� Read/Write Enabled���� Ȯ�� �� ���� (�ʿ��)
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

            // ���ݹ��� �̹����� �ٽ� Texture2D�� �ε� (Read/Write Enabled �ʿ�)
            Texture2D attackedTexture = new Texture2D(sourceImage.width, sourceImage.height, sourceImage.format, false); // Mipmap false
            attackedTexture.LoadImage(jpgBytes); // LoadImage�� �ڵ����� Read/Write �����ϰ� �� (�ӽ� �ؽ�ó)

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
                DestroyImmediate(attackedTexture); // �ӽ� �ؽ�ó ����
                continue;
            }

            double ber = CalculateBER(expectedPayloadBits, extractedBits);

            string resultStr = $"QF {qf}: BER = {ber:F6} (Extracted bits: {extractedBits.Count})";
            AddLog($"  Result: {resultStr}");
            summaryLines.Add(resultStr);

            DestroyImmediate(attackedTexture); // �ӽ� �ؽ�ó ����
        }

        // ���� �̹����� isReadable ���� ���� (�ʿ��)
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
        // GetWindow<ImageAttackEditorWindow>().Repaint(); // �α� ������Ʈ�� ����
    }

    // --- ���͸�ũ ���� ���� (C# ���� �ʿ�) ---
    private List<uint> ExtractWatermark(Texture2D image, WatermarkTechnique technique, string key, int coeffsToUse)
    {
        // Texture2D.isReadable�� true�� �����ؾ� GetPixels/GetPixels32 ��� ����
        // LoadImage�� ������ Texture2D�� �⺻������ readable.
        if (!image.isReadable)
        {
            AddLog("Error: Texture for extraction is not readable!");
            return new List<uint>(); // �Ǵ� null
        }

        List<uint> extractedBits = new List<uint>();
        Color32[] pixels = null; // LSB��
        float[,,] pixelFloatRgb = null; // DCT/DWT�� (0.0 - 1.0 ����ȭ)

        int width = image.width;
        int height = image.height;

        // ��� ��� (Python�� �����ϰ�)
        int numBlocksX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
        int numBlocksY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
        int totalImageBlocks = numBlocksX * numBlocksY;

        if (technique == WatermarkTechnique.LSB)
        {
            pixels = image.GetPixels32(); // GetPixels32()�� ���� �Ʒ����� ����, ����, �� ���� ����������.
                                          // Python�� numpy �迭�� ���� ���� ���ʺ��� ����. �ε��� ����.
                                          // ���⼭�� Python�� �����ϰ� ���������� ó�� ����.
            int maxBitsToExtractLsb = width * height; // B ä�ο����� ���� ����
            for (int i = 0; i < maxBitsToExtractLsb; i++)
            {
                // Python: pixel_y, pixel_x = bit_idx // width, bit_idx % width
                // C# GetPixels32: index = y * width + x
                // (y�� 0���� height-1, x�� 0���� width-1)
                // Python�� ������ ������ �Ϸ��� y�� �������ְų� (height-1-y) �ؾ��� �� ����.
                // ���⼭�� �ܼ� ������ �������� ����. (Python LSB ���� �������� ��Ȯ�� ��ġ �ʿ�)
                int pixelY = i / width; // Python: bit_idx // width
                int pixelX = i % width; // Python: bit_idx % width
                int cSharpIndex = pixelY * width + pixelX; // Python�� (pixel_y, pixel_x) ���ٰ� �����ϰ� �Ϸ��� �� �ε��� Ȯ�� �ʿ�

                // Python �ڵ�� RGB ������ float �迭�� ��������, ���⼭�� Color32 ���
                // Color32.b �� blue ä��
                if (cSharpIndex < pixels.Length)
                {
                    extractedBits.Add((uint)(pixels[cSharpIndex].b & 1));
                }
                else break;
            }
        }
        else // DCT_SS or DWT
        {
            // �ȼ� �����͸� float[height, width, 3] ���·� ��ȯ (0.0-1.0)
            pixelFloatRgb = ConvertTextureToFloatArray(image);

            // ���� ���� ���� (Python�� generate_pattern_buffer_py�� �����ϰ�)
            // ����: key.GetHashCode()�� �÷���/�������� �ٸ� �� ����. �ϰ��� �ִ� �õ� ���� �߿�.
            // Python�� �ܼ� �ջ� ��� �õ�� C#�� GetHashCode()�� �ٸ�.
            // ���⼭�� C# ǥ�� GetHashCode() ��� ����. ���� �ÿ��� ���� ��� ����ؾ� ��.
            int actualCoeffsToUse = coeffsToUse;
            if (technique == WatermarkTechnique.DWT)
            {
                actualCoeffsToUse = Math.Min(coeffsToUse, HH_COEFFS_PER_BLOCK);
            }
            else if (technique == WatermarkTechnique.DCT_SS)
            {
                // DCT�� ���� DC ���� 63 AC ��� ���. Zigzag �ε��� ���̿� ��.
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
                // 1. ���� ��� ������ ���� (R, G, B �� ä�κ� 8x8 float �迭)
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
                        if (k >= zigzagIndices.Count) break; // ����� ������� ��� �ʰ� ����

                        int u = zigzagIndices[k].Item1; // col
                        int v = zigzagIndices[k].Item2; // row
                        float pVal = patternBuffer[patternBaseIdx + k];
                        // DCT ����� (row, col) ����
                        correlationSumR += transformedR[v, u] * pVal;
                        correlationSumG += transformedG[v, u] * pVal;
                        correlationSumB += transformedB[v, u] * pVal;
                    }
                }
                else // DWT
                {
                    transformedR = PerformHaarDWT(blockR); // ����� LL,LH,HL,HH ������ ��ġ�� 8x8 �迭 ����
                    transformedG = PerformHaarDWT(blockG);
                    transformedB = PerformHaarDWT(blockB);

                    for (int k_hh = 0; k_hh < actualCoeffsToUse; k_hh++)
                    {
                        if (patternBaseIdx + k_hh >= patternBuffer.Length) break;
                        // HH �δ뿪 �������� 2D �ε��� (Python: v_hh, u_hh)
                        int v_hh = k_hh / HALF_BLOCK_SIZE;
                        int u_hh = k_hh % HALF_BLOCK_SIZE;
                        // ��ü 8x8 DWT ��� �迭������ HH �δ뿪 ��ǥ (Python: v_global, u_global)
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
                { // Python�� ������ġ ����
                    AddLog($"Warning: Safety break in bit extraction. Extracted {extractedBits.Count} bits.");
                    break;
                }
            }
        }
        AddLog($"Extraction complete for technique {technique}. Total bits extracted: {extractedBits.Count}");
        // Python���� extracted_info_list_int �� ���̰� 32400���� ���Դ� �κп� ���� ����� �α� �߰�
        if ((technique == WatermarkTechnique.DCT_SS || technique == WatermarkTechnique.DWT) && extractedBits.Count != totalImageBlocks && totalImageBlocks > 0)
        {
            AddLog($"Warning: Extracted bit count ({extractedBits.Count}) does not match total_image_blocks ({totalImageBlocks}) for {technique}.");
        }

        return extractedBits;
    }


    // --- BER ��� ���� (C# ���� �ʿ�) ---
    private double CalculateBER(List<uint> expectedBits, List<uint> extractedBits)
    {
        if (extractedBits == null || extractedBits.Count == 0) return 1.0;
        if (expectedBits == null || expectedBits.Count == 0) return 1.0; // ������ ������ �� �Ұ�

        // Python�� find_validated_watermark_start_index_py ����
        int syncStartIndex = FindValidatedWatermarkStartIndex(extractedBits, expectedBits, OriginBlock.syncPattern.ToList()); // OriginBlock.syncPattern�� static readonly List<uint>

        if (syncStartIndex == -1) return 1.0; // ����ȭ ����

        // Python ������ �����ϰ�, expectedBits ������� ���� ������ ���� ����
        int expectedLenFieldStart = OriginBlock.SYNC_PATTERN_LENGTH; // OriginBlock�� ���ǵ� ��� ���
        int expectedLenFieldEnd = expectedLenFieldStart + OriginBlock.LENGTH_FIELD_BITS;

        if (expectedBits.Count < expectedLenFieldEnd) return 1.0; // �ʹ� ª��

        int actualDataLenInExpected = 0;
        for (int bitIdx = 0; bitIdx < OriginBlock.LENGTH_FIELD_BITS; bitIdx++)
        {
            if (expectedBits[expectedLenFieldStart + bitIdx] == 1)
            {
                actualDataLenInExpected |= (1 << (OriginBlock.LENGTH_FIELD_BITS - 1 - bitIdx));
            }
        }

        if (actualDataLenInExpected == 0) return 0.0; // ���� ������ ����

        int originalDataStartIdx = expectedLenFieldEnd;
        int originalDataEndIdx = originalDataStartIdx + actualDataLenInExpected;

        int extractedDataStartIdx = syncStartIndex + OriginBlock.SYNC_PATTERN_LENGTH + OriginBlock.LENGTH_FIELD_BITS;
        int extractedDataEndIdx = extractedDataStartIdx + actualDataLenInExpected;

        if (originalDataEndIdx > expectedBits.Count || extractedDataEndIdx > extractedBits.Count)
        {
            return 1.0; // ���� �ʰ�
        }

        List<uint> originalDataSegment = expectedBits.GetRange(originalDataStartIdx, actualDataLenInExpected);
        List<uint> extractedDataSegment = extractedBits.GetRange(extractedDataStartIdx, actualDataLenInExpected);

        if (originalDataSegment.Count != extractedDataSegment.Count) return 1.0; // ���� ����ġ (�̷л� �߻� �� ��)

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

    // Python�� find_validated_watermark_start_index_py C# ����
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

            // ����ȭ ���� ��ġ, ���� ��ü ���̷ε�(expectedFullPayload�� ���� �κ�)�� ��
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
            // ����ȭ ������ �̹� ����. expectedFullPayload�� ���ۺ��� totalExpectedLengthForThisMatch ��ŭ��
            // extractedBits�� i ��ġ���� ��
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


    // --- �̹��� ó�� �� ��ȯ ���� �Լ��� ---
    private float[,,] ConvertTextureToFloatArray(Texture2D tex)
    {
        // Color[]�� 0.0f-1.0f ������ float ���� ����
        Color[] pixelsColor = tex.GetPixels(); // ���� �Ʒ����� ����
        int width = tex.width;
        int height = tex.height;
        float[,,] floatArray = new float[height, width, 3]; // [y, x, channel]

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // GetPixels�� 1D �迭�̹Ƿ� 2D �ε����� ��ȯ. Unity �ؽ�ó ��ǥ�� ���.
                // (0,0)�� ���� �Ʒ�. Python numpy�� ���� ���� ��.
                // Python�� ������ [y_py, x_py] ������ ���� y��ǥ�� ������ �� ����: int unityY = height - 1 - y;
                // ���⼭�� �ϴ� Unity�� �⺻ ������� ó���ϰ�, ��� ���� �� Python�� ����.
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
        // Python�� extract_block_data_py �� ���� ����
        int blockYCoord = blockIdx / numBlocksX;
        int blockXCoord = blockIdx % numBlocksX;

        for (int yInBlock = 0; yInBlock < BLOCK_SIZE; yInBlock++)
        {
            for (int xInBlock = 0; xInBlock < BLOCK_SIZE; xInBlock++)
            {
                int pixelX = blockXCoord * BLOCK_SIZE + xInBlock;
                // Python pixel_array_rgb_float_norm[pixel_y, pixel_x, c]
                // Python�� y�� ������ �Ʒ�, x�� ���ʿ��� ������.
                // ConvertTextureToFloatArray���� y�� �̹� Python������ (0=top) �������ٰ� �����ϰų�,
                // ���⼭ pixelFloatRgb ���� �� y��ǥ�� (imgHeight - 1 - (blockYCoord * BLOCK_SIZE + yInBlock)) ������ ��ȯ.
                // ���⼭�� pixelFloatRgb�� �̹� [0=top, height-1=bottom]���� ���ĵǾ� �ִٰ� ���� (ConvertTextureToFloatArray ���� �ʿ� ��)
                // �Ǵ�, Python�� pixel_array_rgb_float_norm[pixel_y, pixel_x, 0] ó�� �����ϱ� ����
                // pixelFloatRgb�� y �ε����� Python������ ���:
                int pixelY_python = blockYCoord * BLOCK_SIZE + yInBlock;

                if (pixelX < imgWidth && pixelY_python < imgHeight)
                {
                    // pixelFloatRgb�� [unityY, unityX, c] (unityY�� �Ʒ����� ��) ���,
                    // int unityPixelY = imgHeight - 1 - pixelY_python;
                    // blockR[yInBlock, xInBlock] = pixelFloatRgb[unityPixelY, pixelX, 0];
                    // ...
                    // ���⼭�� pixelFloatRgb�� �̹� [pythonY, pythonX, c]�� �Ǿ� �ִٰ� ����
                    blockR[yInBlock, xInBlock] = pixelFloatRgb[pixelY_python, pixelX, 0];
                    blockG[yInBlock, xInBlock] = pixelFloatRgb[pixelY_python, pixelX, 1];
                    blockB[yInBlock, xInBlock] = pixelFloatRgb[pixelY_python, pixelX, 2];
                }
                else // ����� �̹��� ��踦 ����� ��� 0���� ä�� (�е�)
                {
                    blockR[yInBlock, xInBlock] = 0f;
                    blockG[yInBlock, xInBlock] = 0f;
                    blockB[yInBlock, xInBlock] = 0f;
                }
            }
        }
    }

    // ���� ���� (Python�� generate_pattern_buffer_py �� ����)
    private float[] GeneratePatternBuffer(int totalBlocks, int coeffsPerBlock, string seedKey)
    {
        if (totalBlocks <= 0 || coeffsPerBlock <= 0) return new float[0];

        // C#���� ���ڿ� �õ�κ��� �ϰ��� ���� �õ� ��� (GetHashCode ���)
        // ����: GetHashCode() ����� .NET ����/�÷����� ���� �ٸ� �� �ִٴ� ���� �����ؾ� �ϳ�,
        // ���� ȯ�� ��(Unity ������)������ ���� �ϰ���.
        // ���� �ÿ��� ������ ������� �õ带 �����ؾ� ��.
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

    // ������� �ε��� (Python�� get_zigzag_indices_py �� ���� ����)
    private List<Tuple<int, int>> GetZigzagIndices() // (u,v) �� (col,row)
    {
        return new List<Tuple<int, int>> {
            new Tuple<int,int>(0,1),new Tuple<int,int>(1,0),new Tuple<int,int>(2,0),new Tuple<int,int>(1,1),new Tuple<int,int>(0,2),new Tuple<int,int>(0,3),new Tuple<int,int>(1,2),new Tuple<int,int>(2,1),new Tuple<int,int>(3,0),new Tuple<int,int>(4,0),new Tuple<int,int>(3,1),new Tuple<int,int>(2,2),new Tuple<int,int>(1,3),new Tuple<int,int>(0,4),new Tuple<int,int>(0,5),new Tuple<int,int>(1,4),
            new Tuple<int,int>(2,3),new Tuple<int,int>(3,2),new Tuple<int,int>(4,1),new Tuple<int,int>(5,0),new Tuple<int,int>(6,0),new Tuple<int,int>(5,1),new Tuple<int,int>(4,2),new Tuple<int,int>(3,3),new Tuple<int,int>(2,4),new Tuple<int,int>(1,5),new Tuple<int,int>(0,6),new Tuple<int,int>(0,7),new Tuple<int,int>(1,6),new Tuple<int,int>(2,5),new Tuple<int,int>(3,4),new Tuple<int,int>(4,3),
            new Tuple<int,int>(5,2),new Tuple<int,int>(6,1),new Tuple<int,int>(7,0),new Tuple<int,int>(7,1),new Tuple<int,int>(6,2),new Tuple<int,int>(5,3),new Tuple<int,int>(4,4),new Tuple<int,int>(3,5),new Tuple<int,int>(2,6),new Tuple<int,int>(1,7),new Tuple<int,int>(2,7),new Tuple<int,int>(3,6),new Tuple<int,int>(4,5),new Tuple<int,int>(5,4),new Tuple<int,int>(6,3),new Tuple<int,int>(7,2),
            new Tuple<int,int>(7,3),new Tuple<int,int>(6,4),new Tuple<int,int>(5,5),new Tuple<int,int>(4,6),new Tuple<int,int>(3,7),new Tuple<int,int>(4,7),new Tuple<int,int>(5,6),new Tuple<int,int>(6,5),new Tuple<int,int>(7,4),new Tuple<int,int>(7,5),new Tuple<int,int>(6,6),new Tuple<int,int>(5,7),new Tuple<int,int>(6,7),new Tuple<int,int>(7,6),new Tuple<int,int>(7,7)
        }.Take(BLOCK_SIZE * BLOCK_SIZE - 1).ToList(); // DC ���� �ִ� 63�� AC ���
    }

    // --- DCT �� DWT ���� (8x8 ��� ���) ---
    private float[,] PerformDCT(float[,] blockData)
    {
        // 8x8 DCT-II ���� (Python�� scipy.fft.dctn(..., type=2, norm='ortho')�� �����ϰ�)
        // ���� �����ϰų�, Accord.NET ���� ���̺귯�� ��� ���� (������ ��ũ��Ʈ������ ���� ������ ������ �� ����)
        // �Ʒ��� �⺻���� DCT-II ���Ŀ� ����� ���� ���� ���� (����ȭ ����)
        int N = BLOCK_SIZE;
        float[,] dctCoeffs = new float[N, N];
        float c_u, c_v;
        float sum;

        for (int u = 0; u < N; u++) // u�� ���ļ� (����)
        {
            for (int v = 0; v < N; v++) // v�� ���ļ� (����)
            {
                sum = 0.0f;
                for (int x = 0; x < N; x++) // x�� ���� (����)
                {
                    for (int y = 0; y < N; y++) // y�� ���� (����)
                    {
                        sum += blockData[y, x] *
                               Mathf.Cos((2 * x + 1) * u * Mathf.PI / (2.0f * N)) *
                               Mathf.Cos((2 * y + 1) * v * Mathf.PI / (2.0f * N));
                    }
                }
                c_u = (u == 0) ? (1.0f / Mathf.Sqrt(N)) : (Mathf.Sqrt(2.0f / N));
                c_v = (v == 0) ? (1.0f / Mathf.Sqrt(N)) : (Mathf.Sqrt(2.0f / N));
                // Scipy�� 'ortho' ����ȭ�� ��ü������ 2/N �Ǵ� sqrt(2/N)*sqrt(2/N) ����� ���ϴ� ���.
                // JPEG ǥ�� DCT�� ����ȭ ����� �ణ �ٸ� �� ����. Python scipy�� ortho�� ���߷���
                // ��� c_u, c_v�� ���ϰ�, �߰��� 1/sqrt(N*N) * 2*2 = 4/N �Ǵ� sqrt(1/N) * sqrt(1/N) * 2*2 (u,v!=0)
                // Python Scipy�� ortho�� (2/N) * sum * (C_u_norm_factor * C_v_norm_factor)
                // C_u_norm_factor = 1/sqrt(2) if u=0 or N/2 else 1.
                // �� �����ϰԴ�, Python�� dctn(type=2, norm='ortho') ����� C#���� �����ϰ� ��������
                // ��� �����ϸ��� ���߰ų�, Python���� ����� scipy �ڵ带 C#���� ���� ����.
                // ���⼭�� JPEG ǥ�ؿ� ����� ����ȭ�� ���.
                float alpha_u = (u == 0) ? 1.0f / Mathf.Sqrt(2.0f) : 1.0f;
                float alpha_v = (v == 0) ? 1.0f / Mathf.Sqrt(2.0f) : 1.0f;
                dctCoeffs[v, u] = (2.0f / N) * alpha_u * alpha_v * sum; // JPEG ��Ÿ�� ����ȭ (v,u ���� ����)
            }
        }
        return dctCoeffs;
    }

    private float[,] PerformHaarDWT(float[,] blockData)
    {
        // 8x8 Haar DWT ���� (Python�� pywt.dwt2(..., 'haar', mode='periodization')�� �����ϰ�)
        // LL, LH, HL, HH ������ ��� �迭�� ����
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
        // ���ġ: LL, LH, HL, HH (Python pywt ����� �����ϰ�)
        // ���� dwtCoeffs�� �̹� �� ������ �Ǿ�����.
        // LL: [0..N/2-1, 0..N/2-1]
        // LH: [0..N/2-1, N/2..N-1]
        // HL: [N/2..N-1, 0..N/2-1]
        // HH: [N/2..N-1, N/2..N-1]
        return dwtCoeffs;
    }
}