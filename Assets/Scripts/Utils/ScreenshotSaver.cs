using UnityEngine;
using UnityEngine.Rendering;
using System.IO;
using Unity.Collections;
using System;

// 1. 저장 요청 신호를 위한 간단한 static 클래스
public static class SaveTrigger
{
    // volatile 키워드는 멀티스레드 환경에서 값 변경이 즉시 반영되도록 보장 (선택적)
    public static volatile bool SaveRequested = false;
    // 파일 이름 등 다른 정보도 필요 시 static으로 전달 가능
    public static string SaveFileName = "WatermarkedOutput_Async.exr";
}

// 2. 키 입력을 받아 저장 요청 플래그만 설정하는 MonoBehaviour
public class ScreenshotSaver : MonoBehaviour
{
    public KeyCode saveKey = KeyCode.F11;

    void Update()
    {
        // 한 번 요청이 처리될 때까지 다시 요청하지 않도록 함 (선택적)
        if (!SaveTrigger.SaveRequested && Input.GetKeyDown(saveKey))
        {
            Debug.Log($"Save request triggered! Filename: {SaveTrigger.SaveFileName}");
            SaveTrigger.SaveRequested = true;
            // 필요 시 여기서 파일 이름 설정 가능
            // SaveTrigger.SaveFileName = $"Screenshot_{System.DateTime.Now:yyyyMMdd_HHmmss}.exr";
        }
    }
}