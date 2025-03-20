using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.IO;

public class BenchmarkFPS : MonoBehaviour
{
    private List<float> frameTimes = new List<float>();
    private float totalFrameTime = 0f;
    private int frameCount = 0;
    private float maxFPS = float.MinValue;
    private float minFPS = float.MaxValue;

    private float benchmarkDuration = 60f; // 벤치마크 실행 시간 (초)
    private float elapsedTime = 0f;
    private bool isBenchmarking = false;
    private string filePath;

    void Start()
    {
        Application.targetFrameRate = -1; // 프레임 제한 해제
        QualitySettings.vSyncCount = 0; // VSync 비활성화 (강제 제한 방지)

        string desktopPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);
        filePath = Path.Combine(desktopPath, "benchmark_results.txt");
        StartBenchmark();
    }

    void Update()
    {
        if (isBenchmarking)
        {
            float deltaTime = Time.unscaledDeltaTime;
            float fps = 1f / deltaTime;

            frameTimes.Add(fps);
            totalFrameTime += deltaTime;
            frameCount++;

            maxFPS = Mathf.Max(maxFPS, fps);
            minFPS = Mathf.Min(minFPS, fps);

            elapsedTime += deltaTime;
            if (elapsedTime >= benchmarkDuration)
            {
                StopBenchmark();
                Application.Quit();
            }
        }
    }

    public void StartBenchmark()
    {
        frameTimes.Clear();
        totalFrameTime = 0f;
        frameCount = 0;
        maxFPS = float.MinValue;
        minFPS = float.MaxValue;
        elapsedTime = 0f;
        isBenchmarking = true;
    }

    public void StopBenchmark()
    {
        isBenchmarking = false;

        if (frameCount == 0) return;

        float averageFPS = frameCount / totalFrameTime;
        float low1PercentFPS = GetLow1PercentFPS();

        string result = $"Benchmark Results:\n" +
                        $"Running Time : {benchmarkDuration}\n" +
                        $"Max FPS: {maxFPS:F2}\n" +
                        $"Min FPS: {minFPS:F2}\n" +
                        $"Avg FPS: {averageFPS:F2}\n" +
                        $"Low 1% FPS: {low1PercentFPS:F2}\n" +
                        $"Saved at: {filePath}";

        Debug.Log(result);
        SaveResultsToFile(result);
    }

    private float GetLow1PercentFPS()
    {
        if (frameTimes.Count == 0) return 0f;

        int count = Mathf.Max(1, Mathf.FloorToInt(frameTimes.Count * 0.01f)); // 최저 1% 프레임 개수
        return frameTimes.OrderBy(f => f).Take(count).Average();
    }

    private void SaveResultsToFile(string content)
    {
        try
        {
            File.WriteAllText(filePath, content);
            Debug.Log($"Benchmark results saved to {filePath}");
        }
        catch (IOException e)
        {
            Debug.LogError($"Error saving benchmark results: {e.Message}");
        }
    }
}
