using UnityEngine;
using UnityEngine.Rendering; // AsyncGPUReadbackRequest ��� ���� �ʿ�
using System.IO;
using Unity.Collections; // NativeArray ��� ���� �ʿ�
using System; 

public class SaveTextureAsync : MonoBehaviour
{
    public string saveFileName = "WatermarkedOutput_Async.png";
    public KeyCode saveKey = KeyCode.F11;
    private bool isRequestPending = false; // �ߺ� ��û ���� �÷���

    void Update()
    {
        if (Input.GetKeyDown(saveKey))
        {
            Camera cam = GetComponent<Camera>();
            if (cam == null) return;

            RenderTexture rt = cam.targetTexture; // �Ǵ� Screen Capture ��� ���
            Texture sourceTexture; // ���� �о�� �ҽ� (RenderTexture �Ǵ� Screen)

            if (rt != null)
            {
                sourceTexture = rt;
            }
            else
            {
                // Screen Capture�� �ణ �ٸ�, ���⼭�� RT�� �ִٰ� ����
                // �Ǵ� ScreenCapture.CaptureScreenshotAsTexture ��� ���
                //Debug.LogError("ī�޶� Target Texture�� �������� �ʾҽ��ϴ�. Screen Capture�� �ٸ� ��� �ʿ�.");
                // return;
                sourceTexture = ScreenCapture.CaptureScreenshotAsTexture(); // �� ��� �񵿱� �ƴ�
            }

            if (sourceTexture == null) return;

            isRequestPending = true; // ��û ����
            Debug.Log("Async GPU Readback ��û ����...");

            // �񵿱� GPU �б� ��û
            // TextureFormat.RGBA32 �� CPU���� ó�� ������ ���� ����
            AsyncGPUReadback.Request(sourceTexture, 0, TextureFormat.RGBA32, OnCompleteReadback);
        }
    }

    void OnCompleteReadback(AsyncGPUReadbackRequest request)
    {
        Debug.Log("Async GPU Readback �Ϸ�.");

        if (request.hasError)
        {
            Debug.LogError("GPU Readback ����!");
            isRequestPending = false;
            return;
        }

        // Texture2D ���� (��û �� ����� ���˰� ũ�� ��ġ)
        // request.width, request.height ���
        Texture2D texture = new Texture2D(request.width, request.height, TextureFormat.RGBA32, false);

        // �о�� ������ �ε�
        // �߿�: request.GetData<T>()�� NativeArray�� ��ȯ�ϸ�, �� ���� �����ؾ� �� �� ����
        try
        {
            texture.LoadRawTextureData(request.GetData<byte>());
            texture.Apply(); // Texture2D�� ����

            // PNG�� ���ڵ�
            byte[] bytes = texture.EncodeToPNG();

            // CPU �޸� ����
            Destroy(texture);

            // ���� ����
            string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), saveFileName);
            File.WriteAllBytes(savePath, bytes);
            Debug.Log($"�̹��� ���� ���� (Async): {savePath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"������ ó�� �Ǵ� ���� ���� (Async): {e.Message}");
        }
        finally
        {
            isRequestPending = false; // ��û �Ϸ� (����/���� ����)
        }
    }
}