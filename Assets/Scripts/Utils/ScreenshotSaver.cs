using UnityEngine;
using UnityEngine.Rendering;
using System.IO;
using Unity.Collections;
using System;

// RenderTexture를 저장하기 위한 간단한 스크립트
public static class RTResultHolder
{
    public static RTHandle DedicatedSaveTarget;
    public static RenderTextureDescriptor SaveTargetDesc;
}

// 1. 저장 요청 신호를 위한 간단한 static 클래스
public static class SaveTrigger
{
    // volatile 키워드는 멀티스레드 환경에서 값 변경이 즉시 반영되도록 보장 (선택적)
    public static volatile bool SaveRequested = false;
    // 파일 이름 등 다른 정보도 필요 시 static으로 전달 가능
    public static string SaveFileName = "";
}


// 2. 키 입력을 받아 저장 요청 플래그만 설정하는 MonoBehaviour
public class ScreenshotSaver : MonoBehaviour
{
    public KeyCode saveKey = KeyCode.F11;
    private bool isReadbackPending = false; // Readback 진행 중 플래그 (MonoBehaviour 인스턴스 변수)

    void Update()
    {
        // 한 번 요청이 처리될 때까지 다시 요청하지 않도록 함 (선택적)
        if (!SaveTrigger.SaveRequested && Input.GetKeyDown(saveKey))
        {
            Debug.Log($"Save request triggered! Filename: {SaveTrigger.SaveFileName}");
            SaveTrigger.SaveRequested = true;
            RequestSave();
        }
    }
    public void RequestSave()
    {
        if (!isReadbackPending)
        {
            SaveTrigger.SaveRequested = true;
            // 파일 이름 설정 등 추가 로직 가능
            SaveTrigger.SaveFileName = $"Result_{DateTime.Now:yyyyMMdd_HHmmss}";
            Debug.Log($"Save Requested! Filename: {SaveTrigger.SaveFileName}");
        }
        else
        {
            Debug.LogWarning("Previous save request is still pending.");
        }
    }

    // LateUpdate는 모든 렌더링 작업이 끝난 후 호출되는 경향이 있음
    void LateUpdate()
    {
        // 저장 요청이 있고, 현재 진행 중인 Readback이 없을 때
        if (SaveTrigger.SaveRequested && !isReadbackPending)
        {
            var targetHandle = RTResultHolder.DedicatedSaveTarget;
            RenderTexture targetToRead = targetHandle?.rt; // RTHandle에서 RenderTexture 가져오기

            if (targetToRead != null && targetToRead.IsCreated())
            {
                Debug.Log($"[{Time.frameCount}] Initiating AsyncGPUReadback request...");
                isReadbackPending = true; // 요청 시작, 플래그 설정
                SaveTrigger.SaveRequested = false; // 요청 플래그 리셋

                // *** 중요: 콜백으로 이 MonoBehaviour의 인스턴스 메서드(OnCompleteReadback) 전달 ***
                AsyncGPUReadback.Request(targetToRead, 0, TextureFormat.RGBAFloat, AsyncSaveFile);
            }
            else
            {
                Debug.LogError("Async Readback source texture is invalid or not ready!");
                SaveTrigger.SaveRequested = false; // 실패 시 요청 플래그 리셋
            }
        }
    }

    // AsyncGPUReadback 콜백 함수 (인스턴스 메서드)
    void AsyncSaveFile(AsyncGPUReadbackRequest request)
    {
        Debug.Log($"[{Time.frameCount}] AsyncGPUReadback Complete. Attempting to save files...");

        // --- 기본 오류 체크 ---
        if (request.hasError)
        {
            Debug.LogError("GPU Readback failed!");
            isReadbackPending = false;
            return;
        }
        if (!request.done)
        {
            Debug.LogWarning("GPU Readback not done?");
            isReadbackPending = false;
            return;
        }

        // --- 데이터 가져오기 ---
        NativeArray<float> floatData = request.GetData<float>();
        int width = request.width;
        int height = request.height;
        Debug.Log($"Readback data size: {width}x{height}, format: RGBAFloat, length: {floatData.Length}");

        // 데이터 길이 검증 (선택적)
        int expectedFloatLength = width * height * 4; // RGBAFloat는 채널당 float, 총 4채널
        if (floatData.Length != expectedFloatLength)
        {
            Debug.LogError($"Readback data size mismatch! Expected: {expectedFloatLength}, Got: {floatData.Length}");
            isReadbackPending = false;
            return;
        }
        if (!floatData.IsCreated || floatData.Length == 0)
        {
            Debug.LogError("Readback data is invalid or empty!");
            isReadbackPending = false;
            return;
        }


        // --- 파일 저장을 위한 기본 정보 설정 ---
        string baseFileName = SaveTrigger.SaveFileName; // 저장 요청 시 설정된 기본 이름
        string saveDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop); // 예: 바탕화면

        // --- 모든 저장 작업을 시도하고, 임시 Texture2D는 finally에서 정리 ---
        Texture2D texFloat = null; // EXR, PNG 저장에 사용될 임시 텍스처
        bool rawSaved = false;
        bool exrSaved = false;
        bool pngSaved = false;

        try
        {
            // --- 1. Raw Binary (.bin) 저장 ---
            try
            {
                // NativeArray<float>를 managed float[]로 변환 (가비지 발생 가능)
                float[] floatArray = floatData.ToArray();
                // float[]를 byte[]로 변환
                byte[] rawBytes = new byte[floatArray.Length * sizeof(float)];
                Buffer.BlockCopy(floatArray, 0, rawBytes, 0, rawBytes.Length);

                // 파일명 조합 (정보 포함 권장)
                string binFileName = $"{baseFileName}_{width}x{height}x4_float32.bin";
                string binSavePath = Path.Combine(saveDirectory, binFileName);

                File.WriteAllBytes(binSavePath, rawBytes);
                Debug.Log($"<color=orange>Raw Binary saved successfully:</color> {binSavePath}");
                rawSaved = true;

                // 더 이상 floatArray, rawBytes 필요 없으면 null 처리하여 GC 유도 가능
                floatArray = null;
                rawBytes = null;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save Raw Binary: {e.Message}");
            }


            // --- 2. EXR (.exr) 저장 (HDR/Float 데이터 보존) ---
            try
            {
                // 임시 Texture2D 생성 (RGBAFloat 포맷)
                texFloat = new Texture2D(width, height, TextureFormat.RGBAFloat, false);
                // NativeArray 데이터 직접 로드 (가비지 최소화)
                texFloat.SetPixelData(floatData, 0);
                texFloat.Apply(false); // Apply 필요

                // EXR로 인코딩 (Float 플래그 사용)
                byte[] exrBytes = texFloat.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat);

                string exrFileName = baseFileName + ".exr";
                string exrSavePath = Path.Combine(saveDirectory, exrFileName);

                File.WriteAllBytes(exrSavePath, exrBytes);
                Debug.Log($"<color=cyan>EXR image saved successfully:</color> {exrSavePath}");
                exrSaved = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save EXR: {e.Message}\n{e.StackTrace}");
            }


            // --- 3. PNG (.png) 저장 (SDR/8bit 변환, 정보 손실 가능성 있음) ---
            try
            {
                // EXR 저장을 위해 생성했던 texFloat 재사용
                if (texFloat != null)
                {
                    // PNG로 인코딩 (내부적으로 RGBA32로 변환되며 [0,1] 범위 클램핑 및 양자화 발생)
                    byte[] pngBytes = texFloat.EncodeToPNG();

                    string pngFileName = baseFileName + ".png";
                    string pngSavePath = Path.Combine(saveDirectory, pngFileName);

                    File.WriteAllBytes(pngSavePath, pngBytes);
                    Debug.Log($"<color=lime>PNG image saved successfully:</color> {pngSavePath} (Note: HDR data clamped/quantized)");
                    pngSaved = true;
                }
                else
                {
                    Debug.LogWarning("Cannot save PNG because temporary texture (texFloat) was not created (EXR save might have failed).");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save PNG: {e.Message}\n{e.StackTrace}");
            }

        }
        finally // 모든 작업 완료 또는 예외 발생 후 반드시 실행
        {
            // 임시 Texture2D 파괴 (메모리 누수 방지)
            if (texFloat != null)
            {
                if (Application.isEditor) DestroyImmediate(texFloat);
                else Destroy(texFloat);
                texFloat = null; // 참조 제거
            }

            // --- 중요: Readback 처리 완료 알림 및 플래그 리셋 ---
            isReadbackPending = false;
            Debug.Log($"Readback processing finished. Save status: RAW({rawSaved}), EXR({exrSaved}), PNG({pngSaved})");
        }
    }

}