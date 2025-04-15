using UnityEngine;
using UnityEngine.Rendering; // AsyncGPUReadbackRequest, CommandBuffer �� + GraphicsFormat ����!
using UnityEngine.Rendering.Universal; // ScriptableRendererFeature, ScriptableRenderPass ��
using System.IO;
using UnityEngine.Experimental.Rendering;
using Unity.Collections; // NativeArray ���
using System;
using System.Text; // StringBuilder ���
using System.Collections.Generic; // List ���
using System.Linq; // Take, Select �� Linq ��� �� (�ʿ� ���ٸ� ���� ����)

// �ڡڡ� ����: DCTResultHolder Ŭ������ ������Ʈ �� ��򰡿� ���ǵǾ� �־�� �� (���� �亯 #62 ����) �ڡڡ�

public static class RTResultHolder
{
    public static RTHandle DedicatedSaveTarget;
    public static RenderTextureDescriptor SaveTargetDesc;
}


// �� ��ũ��Ʈ�� ���� ī�޶� �Ǵ� �ٸ� ���� ������Ʈ�� �߰��ϼ���.
public class RealtimeExtractorDebug : MonoBehaviour
{
    // ���� ��� ���ÿ� Enum
    public enum ExtractionMode { DCT_QIM, LSB }

    [Header("���� ����")]
    public KeyCode extractionKey = KeyCode.F12; // ���� ���� Ű
    public ExtractionMode extractionMode = ExtractionMode.DCT_QIM; // �ڡڡ� ���� ��� ���� �ڡڡ�

    [Header("DCT(QIM) ����")]
    [Tooltip("�����͸� ������ DCT ��� U ��ǥ (DCT ��� ��, ���̴��� ��ġ)")]
    public int uIndex = 4;
    [Tooltip("�����͸� ������ DCT ��� V ��ǥ (DCT ��� ��, ���̴��� ��ġ)")]
    public int vIndex = 4;
    [Tooltip("QIM Delta �� (DCT ��� ��, ���̴��� ��Ȯ�� ��ġ)")]
    public float qimDelta = 0.05f;

    // LSB ������ ���� �Ķ���� ���� (Blue ä�� ��� ����)

    [Header("Ÿ�� ���� �ؽ�ó (�б� ����)")]
    [Tooltip("DCT/LSB Pass�� ���� ����� �����ϴ� RenderTexture (��: DCTResultHolder.DedicatedSaveTarget)")]
    public RenderTexture sourceRT; // Inspector �Ҵ� �Ǵ� �Ʒ� �������� ã��

    private bool isRequestPending = false; // �ߺ� ��û ����
    private const int BLOCK_SIZE = 8;
    private const int SYNC_PATTERN_LENGTH = 64; // ����ȭ ���� ��Ʈ ��

    // Python�� sync_pattern_py �� ������ ��
    private readonly int[] sync_pattern_cs = {
        1,1,1,1,0,0,0,0,1,1,1,1,0,0,0,0,1,0,1,0,1,0,1,0,
        1,0,1,0,1,0,1,0,0,0,1,1,0,0,1,1,0,0,1,1,0,0,1,1,
        1,1,0,0,1,1,0,0,0,1,1,1,0,1,1,0
    };

    void Update()
    {
        if (Input.GetKeyDown(extractionKey) && !isRequestPending)
        {
            // �ҽ� RT Ȯ�� �� ���� (DCTResultHolder ��� ����)
            if (sourceRT == null)
            {
                if (RTResultHolder.DedicatedSaveTarget != null && RTResultHolder.DedicatedSaveTarget.rt != null)
                {
                    sourceRT = RTResultHolder.DedicatedSaveTarget.rt;
                    Debug.Log($"Source RT found via DCTResultHolder: {sourceRT.name}");
                }
                else
                {
                    Debug.LogError("Source RenderTexture�� ã�� �� �����ϴ�! Inspector�� �Ҵ��ϰų� DCTResultHolder�� Ȯ���ϼ���.");
                    return;
                }
            }
            if (sourceRT == null || !sourceRT.IsCreated())
            {
                Debug.LogError("Source RenderTexture�� ��ȿ���� �ʽ��ϴ�!");
                return;
            }

            isRequestPending = true;

            // ��忡 ���� ��û ���� ����
            TextureFormat requestedFormat = (extractionMode == ExtractionMode.LSB)
                                            ? TextureFormat.RGBA32  // LSB�� 8��Ʈ ����
                                            : TextureFormat.RGBAFloat; // DCT/QIM�� float

            Debug.Log($"[{this.GetType().Name}] Starting Async GPU Readback for {extractionMode} extraction (Format: {requestedFormat})...");
            AsyncGPUReadback.Request(sourceRT, 0, requestedFormat, this.OnReadbackComplete);
        }
    }

    // GPU Readback �Ϸ� �� ȣ��� �ݹ� �Լ�
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
        // CPU ���� �ð� ����
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            
            if (this.extractionMode == ExtractionMode.DCT_QIM)     // ARGBFloat
            {
                // --- DCT/QIM ó�� ���� ---
                if (extractionMode != ExtractionMode.DCT_QIM)
                {
                    Debug.LogWarning("Data format is Float but mode is not DCT/QIM. Processing as DCT/QIM.");
                }
                Debug.Log("Processing as DCT/QIM (Float data)...");
                NativeArray<float> data = request.GetData<float>();
                int expectedFloatLength = width * height * 4;
                if (data.Length != expectedFloatLength) throw new Exception($"Float data size mismatch! Expected: {expectedFloatLength}, Got: {data.Length}");

                // --- �ڡڡ� (0,0) �ȼ� Raw RGB �� �α� �ڡڡ� ---
                if (width > 0 && height > 0 && data.IsCreated && data.Length >= 4)
                {
                    int idx = 0; // (0,0) �ȼ��� �ε��� 0���� ���� (RGBA)
                    Debug.Log($"\n--- C# (RealtimeExtractorDebug): Pixel (0,0) RAW RGB ---");
                    // "R" ���� ������ ��� (float�� ��ü ���е� ��� �õ�)
                    Debug.Log($"R={data[idx]:R}, G={data[idx + 1]:R}, B={data[idx + 2]:R}, A={data[idx + 3]:R}");
                    Debug.Log("-------------------------------------------------------\n");
                }
                // --- �ڡڡ� �α� �� �ڡڡ� ---

                int numBlocksX = width / BLOCK_SIZE;
                int numBlocksY = height / BLOCK_SIZE;
                int totalBlocks = numBlocksX * numBlocksY;
                int bitsToExtract = Math.Min(totalBlocks, SYNC_PATTERN_LENGTH); // ����ȭ ���ϱ����� Ȯ��
                extractedBits.Capacity = bitsToExtract;

                float[,] yBlock = new float[BLOCK_SIZE, BLOCK_SIZE];
                double[,] dctCoeffs = new double[BLOCK_SIZE, BLOCK_SIZE]; // CPU DCT�� double ���

                for (int blockIndex = 0; blockIndex < bitsToExtract; ++blockIndex)
                {
                    int blockR = blockIndex / numBlocksX; int blockC = blockIndex % numBlocksX;
                    // Y ä�� ����
                    for (int y = 0; y < BLOCK_SIZE; ++y)
                    {
                        for (int x = 0; x < BLOCK_SIZE; ++x)
                        {
                            int pixelX = blockC * BLOCK_SIZE + x; int pixelY = blockR * BLOCK_SIZE + y;
                            int dataIndex = (pixelY * width + pixelX) * 4; // RGBA ����
                            if (dataIndex + 2 < data.Length)
                            {
                                yBlock[y, x] = 0.299f * data[dataIndex + 0] + 0.587f * data[dataIndex + 1] + 0.114f * data[dataIndex + 2];
                            }
                            else { yBlock[y, x] = 0f; }
                        }
                    }
                    // 2D DCT ���
                    DCT2D_CPU(yBlock, dctCoeffs);
                    // QIM ��Ʈ ����
                    double receivedCoeff = dctCoeffs[vIndex, uIndex]; // [��(v), ��(u)]
                    int extractedBit = ExtractQIMBit_CPU(receivedCoeff, (double)qimDelta);
                    if (extractedBit == -1) { Debug.LogError($"QIM ���� ���� {blockIndex}"); extractedBits.Add(-1); }
                    else { extractedBits.Add(extractedBit); }
                }
            }
            else
            {
                // --- LSB ó�� ���� ---
                if (extractionMode != ExtractionMode.LSB)
                {
                    Debug.LogWarning("Data format is 8bit Integer but mode is not LSB. Processing as LSB.");
                }
                Debug.Log("Processing as LSB (8bit integer data)...");
                NativeArray<byte> data = request.GetData<byte>();
                int expectedByteLength = width * height * 4; // RGBA32 ����
                if (data.Length != expectedByteLength) throw new Exception($"Byte data size mismatch! Expected: {expectedByteLength}, Got: {data.Length}");

                int bitsToExtract = Math.Min(width * height, SYNC_PATTERN_LENGTH); // ����ȭ ���ϱ����� Ȯ��
                extractedBits.Capacity = bitsToExtract;

                // LSB ���� (Blue ä��)
                for (int i = 0; i < bitsToExtract; ++i)
                {
                    int pixelY = i / width;
                    int pixelX = i % width;
                    int byteIndex = (pixelY * width + pixelX) * 4; // RGBA ���� ����

                    // Blue ä�� �ε��� = 2 (RGBA ������ ��)
                    // BGRA �����̸� �ε��� = 0
                    // ���⼭�� RGBA�� ����
                    byte blueByte = data[byteIndex + 2];
                    extractedBits.Add(blueByte & 1); // ������ ��Ʈ ����
                }

            }

            stopwatch.Stop();
            Debug.Log($"CPU processing took: {stopwatch.ElapsedMilliseconds} ms for {extractedBits.Count} bits.");

            // --- ��� �� �� ��� ---
            if (extractedBits.Count < SYNC_PATTERN_LENGTH)
            {
                Debug.LogError($"����� ��Ʈ ���� �ʹ� �����ϴ� ({extractedBits.Count} < {SYNC_PATTERN_LENGTH})");
            }
            else
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < SYNC_PATTERN_LENGTH; ++i) { sb.Append(extractedBits[i]); }
                string extractedPattern = sb.ToString();
                string expectedPattern = "";
                for (int i = 0; i < SYNC_PATTERN_LENGTH; ++i) { expectedPattern += sync_pattern_cs[i]; }

                Debug.Log($"����� ����ȭ ���� ({SYNC_PATTERN_LENGTH} ��Ʈ): {extractedPattern}");
                Debug.Log($"���� ����ȭ ���� ({SYNC_PATTERN_LENGTH} ��Ʈ): {expectedPattern}");
                if (extractedPattern == expectedPattern) Debug.Log("<color=green>����ȭ ���� ��ġ!</color>");
                else
                {
                    Debug.LogError("<color=red>����ȭ ���� ����ġ!</color>");
                    string diffMarker = "";
                    for (int i = 0; i < SYNC_PATTERN_LENGTH; ++i) { diffMarker += (extractedBits[i] == sync_pattern_cs[i]) ? " " : "^"; }
                    Debug.Log($"����ġ ��ġ            : {diffMarker}");
                }
            }

        }
        catch (Exception e) { Debug.LogError($"����/ó�� �� ���� �߻�: {e.Message}\n{e.StackTrace}"); }
        finally { isRequestPending = false; }
    }


    // --- CPU ��� DCT/QIM �Լ� (double precision) ---
    // ���� �亯 #88���� ������
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
    // �Է��� float[,] �ް� ���ο��� double ���
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
            // �ڡڡ� ����: ����� dctCoeffs[v, u] �� [i, j] ������ ���� �ڡڡ�
            for (int i = 0; i < BLOCK_SIZE; ++i) { dctCoeffs[i, j] = colOutput[i]; }
        }
    }

    // QIM ��Ʈ ���� (CPU, double precision)
    private int ExtractQIMBit_CPU(double receivedCoeff, double delta)
    {
        if (delta <= 0) return -1; // ����

        // Shader ������ ��ġ (round�� AwayFromZero�� �⺻�� �� ����)
        double n0 = Math.Round(receivedCoeff / delta, MidpointRounding.AwayFromZero); // Tie-breaking ���
        double level0 = n0 * delta;
        double dist0 = Math.Abs(receivedCoeff - level0);

        double n1 = Math.Round((receivedCoeff - (delta / 2.0)) / delta, MidpointRounding.AwayFromZero);
        double level1 = n1 * delta + (delta / 2.0);
        double dist1 = Math.Abs(receivedCoeff - level1);

        return (dist0 <= dist1) ? 0 : 1;
    }
}