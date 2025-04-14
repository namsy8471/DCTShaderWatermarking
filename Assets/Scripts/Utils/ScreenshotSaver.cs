using UnityEngine;
using UnityEngine.Rendering;
using System.IO;
using Unity.Collections;
using System;

// 1. ���� ��û ��ȣ�� ���� ������ static Ŭ����
public static class SaveTrigger
{
    // volatile Ű����� ��Ƽ������ ȯ�濡�� �� ������ ��� �ݿ��ǵ��� ���� (������)
    public static volatile bool SaveRequested = false;
    // ���� �̸� �� �ٸ� ������ �ʿ� �� static���� ���� ����
    public static string SaveFileName = "WatermarkedOutput_Async.exr";
}

// 2. Ű �Է��� �޾� ���� ��û �÷��׸� �����ϴ� MonoBehaviour
public class ScreenshotSaver : MonoBehaviour
{
    public KeyCode saveKey = KeyCode.F11;

    void Update()
    {
        // �� �� ��û�� ó���� ������ �ٽ� ��û���� �ʵ��� �� (������)
        if (!SaveTrigger.SaveRequested && Input.GetKeyDown(saveKey))
        {
            Debug.Log($"Save request triggered! Filename: {SaveTrigger.SaveFileName}");
            SaveTrigger.SaveRequested = true;
            // �ʿ� �� ���⼭ ���� �̸� ���� ����
            // SaveTrigger.SaveFileName = $"Screenshot_{System.DateTime.Now:yyyyMMdd_HHmmss}.exr";
        }
    }
}