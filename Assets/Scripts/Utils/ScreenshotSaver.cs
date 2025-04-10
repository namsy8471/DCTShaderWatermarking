using UnityEngine;
using UnityEngine.Rendering; // AsyncGPUReadbackRequest 사용 위해 필요
using System.IO;
using Unity.Collections; // NativeArray 사용 위해 필요
using System; 

public class SaveTextureAsync : MonoBehaviour
{
    public string saveFileName = "WatermarkedOutput_Async.png";
    public KeyCode saveKey = KeyCode.F11;
    private bool isRequestPending = false; // 중복 요청 방지 플래그

    void Update()
    {
        if (Input.GetKeyDown(saveKey))
        {
            Camera cam = GetComponent<Camera>();
            if (cam == null) return;

            RenderTexture rt = cam.targetTexture; // 또는 Screen Capture 방식 사용
            Texture sourceTexture; // 실제 읽어올 소스 (RenderTexture 또는 Screen)

            if (rt != null)
            {
                sourceTexture = rt;
            }
            else
            {
                // Screen Capture는 약간 다름, 여기서는 RT가 있다고 가정
                // 또는 ScreenCapture.CaptureScreenshotAsTexture 사용 고려
                //Debug.LogError("카메라에 Target Texture가 설정되지 않았습니다. Screen Capture는 다른 방식 필요.");
                // return;
                sourceTexture = ScreenCapture.CaptureScreenshotAsTexture(); // 이 경우 비동기 아님
            }

            if (sourceTexture == null) return;

            isRequestPending = true; // 요청 시작
            Debug.Log("Async GPU Readback 요청 시작...");

            // 비동기 GPU 읽기 요청
            // TextureFormat.RGBA32 등 CPU에서 처리 가능한 포맷 지정
            AsyncGPUReadback.Request(sourceTexture, 0, TextureFormat.RGBA32, OnCompleteReadback);
        }
    }

    void OnCompleteReadback(AsyncGPUReadbackRequest request)
    {
        Debug.Log("Async GPU Readback 완료.");

        if (request.hasError)
        {
            Debug.LogError("GPU Readback 실패!");
            isRequestPending = false;
            return;
        }

        // Texture2D 생성 (요청 시 사용한 포맷과 크기 일치)
        // request.width, request.height 사용
        Texture2D texture = new Texture2D(request.width, request.height, TextureFormat.RGBA32, false);

        // 읽어온 데이터 로드
        // 중요: request.GetData<T>()는 NativeArray를 반환하며, 한 번만 접근해야 할 수 있음
        try
        {
            texture.LoadRawTextureData(request.GetData<byte>());
            texture.Apply(); // Texture2D에 적용

            // PNG로 인코딩
            byte[] bytes = texture.EncodeToPNG();

            // CPU 메모리 정리
            Destroy(texture);

            // 파일 저장
            string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), saveFileName);
            File.WriteAllBytes(savePath, bytes);
            Debug.Log($"이미지 저장 성공 (Async): {savePath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"데이터 처리 또는 저장 실패 (Async): {e.Message}");
        }
        finally
        {
            isRequestPending = false; // 요청 완료 (성공/실패 무관)
        }
    }
}