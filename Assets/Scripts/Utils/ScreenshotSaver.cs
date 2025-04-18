using UnityEngine;
using UnityEngine.Rendering;
using System.IO;
using Unity.Collections;
using System;

// RenderTexture�� �����ϱ� ���� ������ ��ũ��Ʈ
public static class RTResultHolder
{
    public static RTHandle DedicatedSaveTarget;
    public static RenderTextureDescriptor SaveTargetDesc;
}

// 1. ���� ��û ��ȣ�� ���� ������ static Ŭ����
public static class SaveTrigger
{
    // volatile Ű����� ��Ƽ������ ȯ�濡�� �� ������ ��� �ݿ��ǵ��� ���� (������)
    public static volatile bool SaveRequested = false;
    // ���� �̸� �� �ٸ� ������ �ʿ� �� static���� ���� ����
    public static string SaveFileName = "";
}


// 2. Ű �Է��� �޾� ���� ��û �÷��׸� �����ϴ� MonoBehaviour
public class ScreenshotSaver : MonoBehaviour
{
    public KeyCode saveKey = KeyCode.F11;
    private bool isReadbackPending = false; // Readback ���� �� �÷��� (MonoBehaviour �ν��Ͻ� ����)

    void Update()
    {
        // �� �� ��û�� ó���� ������ �ٽ� ��û���� �ʵ��� �� (������)
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
            // ���� �̸� ���� �� �߰� ���� ����
            SaveTrigger.SaveFileName = $"Result_{DateTime.Now:yyyyMMdd_HHmmss}";
            Debug.Log($"Save Requested! Filename: {SaveTrigger.SaveFileName}");
        }
        else
        {
            Debug.LogWarning("Previous save request is still pending.");
        }
    }

    // LateUpdate�� ��� ������ �۾��� ���� �� ȣ��Ǵ� ������ ����
    void LateUpdate()
    {
        // ���� ��û�� �ְ�, ���� ���� ���� Readback�� ���� ��
        if (SaveTrigger.SaveRequested && !isReadbackPending)
        {
            var targetHandle = RTResultHolder.DedicatedSaveTarget;
            RenderTexture targetToRead = targetHandle?.rt; // RTHandle���� RenderTexture ��������

            if (targetToRead != null && targetToRead.IsCreated())
            {
                Debug.Log($"[{Time.frameCount}] Initiating AsyncGPUReadback request...");
                isReadbackPending = true; // ��û ����, �÷��� ����
                SaveTrigger.SaveRequested = false; // ��û �÷��� ����

                // *** �߿�: �ݹ����� �� MonoBehaviour�� �ν��Ͻ� �޼���(OnCompleteReadback) ���� ***
                AsyncGPUReadback.Request(targetToRead, 0, TextureFormat.RGBAFloat, AsyncSaveFile);
            }
            else
            {
                Debug.LogError("Async Readback source texture is invalid or not ready!");
                SaveTrigger.SaveRequested = false; // ���� �� ��û �÷��� ����
            }
        }
    }

    // AsyncGPUReadback �ݹ� �Լ� (�ν��Ͻ� �޼���)
    void AsyncSaveFile(AsyncGPUReadbackRequest request)
    {
        Debug.Log($"[{Time.frameCount}] AsyncGPUReadback Complete. Attempting to save files...");

        // --- �⺻ ���� üũ ---
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

        // --- ������ �������� ---
        NativeArray<float> floatData = request.GetData<float>();
        int width = request.width;
        int height = request.height;
        Debug.Log($"Readback data size: {width}x{height}, format: RGBAFloat, length: {floatData.Length}");

        // ������ ���� ���� (������)
        int expectedFloatLength = width * height * 4; // RGBAFloat�� ä�δ� float, �� 4ä��
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


        // --- ���� ������ ���� �⺻ ���� ���� ---
        string baseFileName = SaveTrigger.SaveFileName; // ���� ��û �� ������ �⺻ �̸�
        string saveDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop); // ��: ����ȭ��

        // --- ��� ���� �۾��� �õ��ϰ�, �ӽ� Texture2D�� finally���� ���� ---
        Texture2D texFloat = null; // EXR, PNG ���忡 ���� �ӽ� �ؽ�ó
        bool rawSaved = false;
        bool exrSaved = false;
        bool pngSaved = false;

        try
        {
            // --- 1. Raw Binary (.bin) ���� ---
            try
            {
                // NativeArray<float>�� managed float[]�� ��ȯ (������ �߻� ����)
                float[] floatArray = floatData.ToArray();
                // float[]�� byte[]�� ��ȯ
                byte[] rawBytes = new byte[floatArray.Length * sizeof(float)];
                Buffer.BlockCopy(floatArray, 0, rawBytes, 0, rawBytes.Length);

                // ���ϸ� ���� (���� ���� ����)
                string binFileName = $"{baseFileName}_{width}x{height}x4_float32.bin";
                string binSavePath = Path.Combine(saveDirectory, binFileName);

                File.WriteAllBytes(binSavePath, rawBytes);
                Debug.Log($"<color=orange>Raw Binary saved successfully:</color> {binSavePath}");
                rawSaved = true;

                // �� �̻� floatArray, rawBytes �ʿ� ������ null ó���Ͽ� GC ���� ����
                floatArray = null;
                rawBytes = null;
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save Raw Binary: {e.Message}");
            }


            // --- 2. EXR (.exr) ���� (HDR/Float ������ ����) ---
            try
            {
                // �ӽ� Texture2D ���� (RGBAFloat ����)
                texFloat = new Texture2D(width, height, TextureFormat.RGBAFloat, false);
                // NativeArray ������ ���� �ε� (������ �ּ�ȭ)
                texFloat.SetPixelData(floatData, 0);
                texFloat.Apply(false); // Apply �ʿ�

                // EXR�� ���ڵ� (Float �÷��� ���)
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


            // --- 3. PNG (.png) ���� (SDR/8bit ��ȯ, ���� �ս� ���ɼ� ����) ---
            try
            {
                // EXR ������ ���� �����ߴ� texFloat ����
                if (texFloat != null)
                {
                    // PNG�� ���ڵ� (���������� RGBA32�� ��ȯ�Ǹ� [0,1] ���� Ŭ���� �� ����ȭ �߻�)
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
        finally // ��� �۾� �Ϸ� �Ǵ� ���� �߻� �� �ݵ�� ����
        {
            // �ӽ� Texture2D �ı� (�޸� ���� ����)
            if (texFloat != null)
            {
                if (Application.isEditor) DestroyImmediate(texFloat);
                else Destroy(texFloat);
                texFloat = null; // ���� ����
            }

            // --- �߿�: Readback ó�� �Ϸ� �˸� �� �÷��� ���� ---
            isReadbackPending = false;
            Debug.Log($"Readback processing finished. Save status: RAW({rawSaved}), EXR({exrSaved}), PNG({pngSaved})");
        }
    }

}