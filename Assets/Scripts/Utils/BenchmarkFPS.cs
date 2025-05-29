using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.IO;
using System;
using Unity.Profiling; // ProfilerRecorder ����� ���� �߰�

public class BenchmarkFPS : MonoBehaviour
{
    [Header("Benchmark Settings")]
    public string testIdentifier = "DefaultTest"; // �׽�Ʈ �ĺ��� (�ν����Ϳ��� ����)
    public float benchmarkDuration = 180f; // ��ġ��ũ ���� �ð� (��)
    public int frameLimit = -1; // ������ ����(-1�� ��� ����)
    public float warmupDuration = 5f; // ��ġ��ũ ���� �� ����ȭ �ð� (��)
    public bool quitAfterBenchmark = true; // ��ġ��ũ �� �ڵ� ���� ����

    [Header("Realtime Display (Optional)")]
    public bool showRealtimeStats = true;
    public GUISkin guiSkin; // ����� ǥ�ø� ���� GUISkin (���� ����)

    // FPS ���� ����
    private List<float> frameTimesList = new List<float>(); // FPS �� ���� ����Ʈ (1% Low ����)
    private float totalFrameTime = 0f;
    private int frameCount = 0;
    private float currentFPS = 0f; // �ǽð� ǥ�ÿ�

    // Profiler Recorder ���� ����
    private ProfilerRecorder mainThreadTimeRecorder;
    private ProfilerRecorder gpuFrameTimeRecorder;

    // ������ ���� ����Ʈ (ms ����)
    private List<float> mainThreadTimes = new List<float>();
    private List<float> gpuFrameTimes = new List<float>();

    // ���� ����
    private float elapsedTime = 0f;
    private bool isBenchmarking = false;
    private bool isWarmup = false;

    string dataPath = Application.dataPath;
    string appDirectory;
    private string filePath;
    private string realtimeStatText = "";

    // ���� ������ �Ʒ��� �߰�
    private List<float> rawFrameTimeMilliseconds = new List<float>(); // ������ �ð� ���� ����Ʈ (ms ����)

    void Start()
    {
        Application.targetFrameRate = frameLimit; // ������ ���� ����
        QualitySettings.vSyncCount = 0; // VSync ��Ȱ��ȭ

        SetupFilePath();
        StartCoroutine(StartWarmup());
    }

    void OnEnable()
    {
        // Profiler Recorder �ʱ�ȭ �� ����
        // ����: ��Ŀ �̸��� ����Ƽ �����̳� ���� ���������ο� ���� �ٸ� �� �ֽ��ϴ�.
        // ���� �����Ͱ� ��ϵ��� ������ Profiler â���� ��Ȯ�� �̸��� Ȯ���ϼ���.
        mainThreadTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 15);
        gpuFrameTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time", 15); // SRP������ �� �̸��� �ƴ� ���� ����
    }

    void OnDisable()
    {
        // Profiler Recorder ����
        mainThreadTimeRecorder.Dispose();
        gpuFrameTimeRecorder.Dispose();
    }

    void SetupFilePath()
    {
        appDirectory = Directory.GetParent(dataPath).FullName;

        string fileName = $"Benchmark_{testIdentifier}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
        filePath = Path.Combine(appDirectory, fileName);
    }

    IEnumerator StartWarmup()
    {
        isWarmup = true;
        Debug.Log($"���־� ���� ({warmupDuration}��)...");
        float warmupTimer = 0f;
        while (warmupTimer < warmupDuration)
        {
            // ���־� �߿��� �ǽð� �� ǥ�� ����
            float deltaTime = Time.unscaledDeltaTime;
            currentFPS = 1f / deltaTime;
            UpdateRealtimeStatText();
            warmupTimer += deltaTime;
            yield return null;
        }
        isWarmup = false;
        Debug.Log("���־� ����. ��ġ��ũ ����!");
        StartBenchmark();
    }


    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape)) // Esc Ű�� ��ġ��ũ �ߴ� �� ����
        {
            if (isBenchmarking)
            {
                StopBenchmark();
            }
            if (quitAfterBenchmark) Application.Quit();
        }

        // ������ �ð� �� FPS ��� (�� ������)
        float frameDeltaTime = Time.unscaledDeltaTime;
        currentFPS = 1f / frameDeltaTime;

        if (isBenchmarking)
        {
            // FPS ������ ���
            frameTimesList.Add(currentFPS);
            // ������ �ð� ������ ��� (�߰��� �ڵ�)
            rawFrameTimeMilliseconds.Add(frameDeltaTime * 1000f); // ms ������ ����
            totalFrameTime += frameDeltaTime;
            frameCount++;

            // Profiler ������ ��� (ms ����)
            if (mainThreadTimeRecorder.Valid)
                mainThreadTimes.Add(mainThreadTimeRecorder.LastValue / 1_000_000f);
            if (gpuFrameTimeRecorder.Valid)
                gpuFrameTimes.Add(gpuFrameTimeRecorder.LastValue / 1_000_000f);

            // ��� �ð� üũ �� ����
            elapsedTime += frameDeltaTime;
            if (elapsedTime >= benchmarkDuration)
            {
                StopBenchmark();
                if (quitAfterBenchmark) Application.Quit();
            }
        }

        // �ǽð� ���� ������Ʈ
        UpdateRealtimeStatText();
    }

    void UpdateRealtimeStatText()
    {
        if (!showRealtimeStats) return;

        string status = isWarmup ? $"Warmup ({warmupDuration - elapsedTime:F1}s left)" : (isBenchmarking ? $"Benchmarking ({benchmarkDuration - elapsedTime:F1}s left)" : "Finished");
        string fpsText = $"FPS: {currentFPS:F1}";
        string mainThreadText = mainThreadTimeRecorder.Valid ? $"Main: {(mainThreadTimeRecorder.LastValue / 1_000_000f):F2} ms" : "Main: N/A";
        string gpuText = gpuFrameTimeRecorder.Valid ? $"GPU: {(gpuFrameTimeRecorder.LastValue / 1_000_000f):F2} ms" : "GPU: N/A";

        realtimeStatText = $"{status}\n{fpsText}\n{mainThreadText}\n{gpuText}";
    }


    public void StartBenchmark()
    {
        // ������ ����Ʈ �ʱ�ȭ
        frameTimesList.Clear();
        mainThreadTimes.Clear();
        gpuFrameTimes.Clear();

        // ���� �ʱ�ȭ
        totalFrameTime = 0f;
        frameCount = 0;
        elapsedTime = 0f;
        isBenchmarking = true;
    }

    public void StopBenchmark()
    {
        if (!isBenchmarking) return; // �ߺ� ȣ�� ����
        isBenchmarking = false;

        if (frameCount == 0) return;

        // ----- ��� ��� -----
        // FPS
        float averageFPS = frameCount / totalFrameTime;
        float minFPS = frameTimesList.Any() ? frameTimesList.Min() : 0f;
        float maxFPS = frameTimesList.Any() ? frameTimesList.Max() : 0f;
        float low1PercentFPS = GetPercentileFPS(frameTimesList, 0.01f); // 1% Low FPS
        // float low01PercentFPS = GetPercentileFPS(frameTimesList, 0.001f); // �ʿ��ϴٸ� 0.1% Low

        // 99th Percentile Frame Time ��� (�ٽ�)
        float frameTime99thPercentile = GetPercentileValue(rawFrameTimeMilliseconds, 0.99f); // 99% �������


        // CPU/GPU Times (ms) - ���, �ִ밪
        float avgMainThreadTime = mainThreadTimes.Any() ? mainThreadTimes.Average() : 0f;
        float maxMainThreadTime = mainThreadTimes.Any() ? mainThreadTimes.Max() : 0f;
        float avgGpuFrameTime = gpuFrameTimes.Any() ? gpuFrameTimes.Average() : 0f;
        float maxGpuFrameTime = gpuFrameTimes.Any() ? gpuFrameTimes.Max() : 0f;

        // ----- ��� ���ڿ� ���� -----
        string result = $"--- Benchmark Results ({testIdentifier}) ---\n";
        result += $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
        result += $"Duration: {elapsedTime:F2}s (Target: {benchmarkDuration}s)\n";
        result += $"Total Frames: {frameCount}\n\n";
        result += "[FPS Metrics]\n";
        result += $"  Average: {averageFPS:F2} FPS\n";
        result += $"  Minimum: {minFPS:F2} FPS\n";
        result += $"  Maximum: {maxFPS:F2} FPS\n";
        result += $"  1% Low:  {low1PercentFPS:F2} FPS\n\n";
        // result += $"  0.1% Low: {low01PercentFPS:F2} FPS\n\n"; // �ʿ�� �߰�
        result += $"  99th Percentile: {frameTime99thPercentile:F2} ms\n"; // 99th ������� �߰�
        // result += $"  95th Percentile: {frameTime95thPercentile:F2} ms\n"; // �ʿ�� �߰�
        // result += $"  99.9th Percentile: {frameTime99_9thPercentile:F2} ms\n\n"; // �ʿ�� �߰�
        result += "[CPU Time (ms)]\n";
        result += $"  Main Thread Avg: {avgMainThreadTime:F2} ms\n";
        result += $"  Main Thread Max: {maxMainThreadTime:F2} ms\n";
        result += "[GPU Time (ms)]\n";
        result += $"  GPU Frame Time Avg: {avgGpuFrameTime:F2} ms\n";
        result += $"  GPU Frame Time Max: {maxGpuFrameTime:F2} ms\n\n";
        result += $"Results saved to: {filePath}\n";
        result += "----------------------------------------";


        Debug.Log(result);
        SaveResultsToFile(result);
    }

    // �ۼ�Ÿ�� FPS ��� �Լ� (��: 1% Low = ���� 1% �����ӵ��� ��� FPS)
    private float GetPercentileFPS(List<float> fpsList, float percentile)
    {
        if (fpsList == null || fpsList.Count == 0) return 0f;

        int count = Mathf.Max(1, Mathf.CeilToInt(fpsList.Count * percentile)); // �ø� ó���Ͽ� �ּ� 1�� ����
        return fpsList.OrderBy(f => f).Take(count).Average();
    }

    // ������� �� ��� �Լ� (���� �� �߰�)
    // Nth Percentile�� �ش��ϴ� "��"�� ��ȯ�մϴ�.
    private float GetPercentileValue(List<float> dataList, float percentile)
    {
        if (dataList == null || dataList.Count == 0) return 0f;

        // �����͸� ������������ ���� (������ �ð��� ª�� �ͺ��� �� ������)
        var sortedList = dataList.OrderBy(d => d).ToList();

        // ��������� �ش��ϴ� �ε��� ���
        // (N * Count) - 1 �� �Ϲ����� �ε��� �����̸�, �ø�/���� �� �پ��� ���ǰ� ������ �� ����� ����.
        // ����Ʈ�� 0���� �����ϹǷ� -1
        float index = percentile * (sortedList.Count - 1);

        if (index < 0) return sortedList[0]; // �����Ͱ� 1���� ��� ��
        if (index >= sortedList.Count - 1) return sortedList[sortedList.Count - 1]; // �����Ͱ� 1���� ��� ��

        // �ε����� ������ �ƴ� ���, ������ ��� (�Ϲ������� ���� ����)
        int floorIndex = Mathf.FloorToInt(index);
        int ceilIndex = Mathf.CeilToInt(index);

        if (floorIndex == ceilIndex)
        {
            return sortedList[floorIndex];
        }
        else
        {
            float lowerValue = sortedList[floorIndex];
            float upperValue = sortedList[ceilIndex];
            float weight = index - floorIndex; // �Ҽ��� �κ�

            return lowerValue * (1 - weight) + upperValue * weight;
        }
    }

    private void SaveResultsToFile(string content)
    {
        try
        {
            File.WriteAllText(filePath, content);
            Debug.Log($"��ġ��ũ ��� ���� �Ϸ�: {filePath}");
        }
        catch (IOException e)
        {
            Debug.LogError($"��ġ��ũ ��� ���� ����: {e.Message}");
        }
    }

    // �ǽð� ��� ǥ�ÿ� OnGUI (���� ����)
    void OnGUI()
    {
        if (!showRealtimeStats) return;

        if (guiSkin != null)
        {
            GUI.skin = guiSkin;
        }

        // ȭ�� ���� ��ܿ� ǥ��
        GUI.Box(new Rect(10, 10, 250, 100), "Benchmark Stats");
        GUI.Label(new Rect(20, 35, 230, 80), realtimeStatText);
    }
}