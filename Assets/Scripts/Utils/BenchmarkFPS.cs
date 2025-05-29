using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using System.IO;
using System;
using Unity.Profiling; // ProfilerRecorder 사용을 위해 추가

public class BenchmarkFPS : MonoBehaviour
{
    [Header("Benchmark Settings")]
    public string testIdentifier = "DefaultTest"; // 테스트 식별자 (인스펙터에서 수정)
    public float benchmarkDuration = 180f; // 벤치마크 실행 시간 (초)
    public int frameLimit = -1; // 프레임 제한(-1일 경우 무한)
    public float warmupDuration = 5f; // 벤치마크 시작 전 안정화 시간 (초)
    public bool quitAfterBenchmark = true; // 벤치마크 후 자동 종료 여부

    [Header("Realtime Display (Optional)")]
    public bool showRealtimeStats = true;
    public GUISkin guiSkin; // 깔끔한 표시를 위한 GUISkin (선택 사항)

    // FPS 관련 변수
    private List<float> frameTimesList = new List<float>(); // FPS 값 저장 리스트 (1% Low 계산용)
    private float totalFrameTime = 0f;
    private int frameCount = 0;
    private float currentFPS = 0f; // 실시간 표시용

    // Profiler Recorder 관련 변수
    private ProfilerRecorder mainThreadTimeRecorder;
    private ProfilerRecorder gpuFrameTimeRecorder;

    // 측정값 저장 리스트 (ms 단위)
    private List<float> mainThreadTimes = new List<float>();
    private List<float> gpuFrameTimes = new List<float>();

    // 상태 변수
    private float elapsedTime = 0f;
    private bool isBenchmarking = false;
    private bool isWarmup = false;

    string dataPath = Application.dataPath;
    string appDirectory;
    private string filePath;
    private string realtimeStatText = "";

    // 기존 변수들 아래에 추가
    private List<float> rawFrameTimeMilliseconds = new List<float>(); // 프레임 시간 저장 리스트 (ms 단위)

    void Start()
    {
        Application.targetFrameRate = frameLimit; // 프레임 제한 해제
        QualitySettings.vSyncCount = 0; // VSync 비활성화

        SetupFilePath();
        StartCoroutine(StartWarmup());
    }

    void OnEnable()
    {
        // Profiler Recorder 초기화 및 시작
        // 주의: 마커 이름은 유니티 버전이나 렌더 파이프라인에 따라 다를 수 있습니다.
        // 만약 데이터가 기록되지 않으면 Profiler 창에서 정확한 이름을 확인하세요.
        mainThreadTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 15);
        gpuFrameTimeRecorder = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GPU Frame Time", 15); // SRP에서는 이 이름이 아닐 수도 있음
    }

    void OnDisable()
    {
        // Profiler Recorder 해제
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
        Debug.Log($"워밍업 시작 ({warmupDuration}초)...");
        float warmupTimer = 0f;
        while (warmupTimer < warmupDuration)
        {
            // 워밍업 중에도 실시간 값 표시 가능
            float deltaTime = Time.unscaledDeltaTime;
            currentFPS = 1f / deltaTime;
            UpdateRealtimeStatText();
            warmupTimer += deltaTime;
            yield return null;
        }
        isWarmup = false;
        Debug.Log("워밍업 종료. 벤치마크 시작!");
        StartBenchmark();
    }


    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape)) // Esc 키로 벤치마크 중단 및 종료
        {
            if (isBenchmarking)
            {
                StopBenchmark();
            }
            if (quitAfterBenchmark) Application.Quit();
        }

        // 프레임 시간 및 FPS 계산 (매 프레임)
        float frameDeltaTime = Time.unscaledDeltaTime;
        currentFPS = 1f / frameDeltaTime;

        if (isBenchmarking)
        {
            // FPS 데이터 기록
            frameTimesList.Add(currentFPS);
            // 프레임 시간 데이터 기록 (추가할 코드)
            rawFrameTimeMilliseconds.Add(frameDeltaTime * 1000f); // ms 단위로 저장
            totalFrameTime += frameDeltaTime;
            frameCount++;

            // Profiler 데이터 기록 (ms 단위)
            if (mainThreadTimeRecorder.Valid)
                mainThreadTimes.Add(mainThreadTimeRecorder.LastValue / 1_000_000f);
            if (gpuFrameTimeRecorder.Valid)
                gpuFrameTimes.Add(gpuFrameTimeRecorder.LastValue / 1_000_000f);

            // 경과 시간 체크 및 종료
            elapsedTime += frameDeltaTime;
            if (elapsedTime >= benchmarkDuration)
            {
                StopBenchmark();
                if (quitAfterBenchmark) Application.Quit();
            }
        }

        // 실시간 상태 업데이트
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
        // 데이터 리스트 초기화
        frameTimesList.Clear();
        mainThreadTimes.Clear();
        gpuFrameTimes.Clear();

        // 변수 초기화
        totalFrameTime = 0f;
        frameCount = 0;
        elapsedTime = 0f;
        isBenchmarking = true;
    }

    public void StopBenchmark()
    {
        if (!isBenchmarking) return; // 중복 호출 방지
        isBenchmarking = false;

        if (frameCount == 0) return;

        // ----- 통계 계산 -----
        // FPS
        float averageFPS = frameCount / totalFrameTime;
        float minFPS = frameTimesList.Any() ? frameTimesList.Min() : 0f;
        float maxFPS = frameTimesList.Any() ? frameTimesList.Max() : 0f;
        float low1PercentFPS = GetPercentileFPS(frameTimesList, 0.01f); // 1% Low FPS
        // float low01PercentFPS = GetPercentileFPS(frameTimesList, 0.001f); // 필요하다면 0.1% Low

        // 99th Percentile Frame Time 계산 (핵심)
        float frameTime99thPercentile = GetPercentileValue(rawFrameTimeMilliseconds, 0.99f); // 99% 백분위수


        // CPU/GPU Times (ms) - 평균, 최대값
        float avgMainThreadTime = mainThreadTimes.Any() ? mainThreadTimes.Average() : 0f;
        float maxMainThreadTime = mainThreadTimes.Any() ? mainThreadTimes.Max() : 0f;
        float avgGpuFrameTime = gpuFrameTimes.Any() ? gpuFrameTimes.Average() : 0f;
        float maxGpuFrameTime = gpuFrameTimes.Any() ? gpuFrameTimes.Max() : 0f;

        // ----- 결과 문자열 생성 -----
        string result = $"--- Benchmark Results ({testIdentifier}) ---\n";
        result += $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
        result += $"Duration: {elapsedTime:F2}s (Target: {benchmarkDuration}s)\n";
        result += $"Total Frames: {frameCount}\n\n";
        result += "[FPS Metrics]\n";
        result += $"  Average: {averageFPS:F2} FPS\n";
        result += $"  Minimum: {minFPS:F2} FPS\n";
        result += $"  Maximum: {maxFPS:F2} FPS\n";
        result += $"  1% Low:  {low1PercentFPS:F2} FPS\n\n";
        // result += $"  0.1% Low: {low01PercentFPS:F2} FPS\n\n"; // 필요시 추가
        result += $"  99th Percentile: {frameTime99thPercentile:F2} ms\n"; // 99th 백분위수 추가
        // result += $"  95th Percentile: {frameTime95thPercentile:F2} ms\n"; // 필요시 추가
        // result += $"  99.9th Percentile: {frameTime99_9thPercentile:F2} ms\n\n"; // 필요시 추가
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

    // 퍼센타일 FPS 계산 함수 (예: 1% Low = 하위 1% 프레임들의 평균 FPS)
    private float GetPercentileFPS(List<float> fpsList, float percentile)
    {
        if (fpsList == null || fpsList.Count == 0) return 0f;

        int count = Mathf.Max(1, Mathf.CeilToInt(fpsList.Count * percentile)); // 올림 처리하여 최소 1개 보장
        return fpsList.OrderBy(f => f).Take(count).Average();
    }

    // 백분위수 값 계산 함수 (수정 및 추가)
    // Nth Percentile에 해당하는 "값"을 반환합니다.
    private float GetPercentileValue(List<float> dataList, float percentile)
    {
        if (dataList == null || dataList.Count == 0) return 0f;

        // 데이터를 오름차순으로 정렬 (프레임 시간은 짧은 것부터 긴 것으로)
        var sortedList = dataList.OrderBy(d => d).ToList();

        // 백분위수에 해당하는 인덱스 계산
        // (N * Count) - 1 이 일반적인 인덱스 계산법이며, 올림/내림 등 다양한 정의가 있으나 이 방식이 흔함.
        // 리스트는 0부터 시작하므로 -1
        float index = percentile * (sortedList.Count - 1);

        if (index < 0) return sortedList[0]; // 데이터가 1개인 경우 등
        if (index >= sortedList.Count - 1) return sortedList[sortedList.Count - 1]; // 데이터가 1개인 경우 등

        // 인덱스가 정수가 아닌 경우, 보간법 사용 (일반적으로 선형 보간)
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
            float weight = index - floorIndex; // 소수점 부분

            return lowerValue * (1 - weight) + upperValue * weight;
        }
    }

    private void SaveResultsToFile(string content)
    {
        try
        {
            File.WriteAllText(filePath, content);
            Debug.Log($"벤치마크 결과 저장 완료: {filePath}");
        }
        catch (IOException e)
        {
            Debug.LogError($"벤치마크 결과 저장 실패: {e.Message}");
        }
    }

    // 실시간 통계 표시용 OnGUI (선택 사항)
    void OnGUI()
    {
        if (!showRealtimeStats) return;

        if (guiSkin != null)
        {
            GUI.skin = guiSkin;
        }

        // 화면 좌측 상단에 표시
        GUI.Box(new Rect(10, 10, 250, 100), "Benchmark Stats");
        GUI.Label(new Rect(20, 35, 230, 80), realtimeStatText);
    }
}