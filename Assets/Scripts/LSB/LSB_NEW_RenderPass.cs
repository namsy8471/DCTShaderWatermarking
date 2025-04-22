using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.Linq;
using System;
using Unity.Collections;
using System.IO; // Math.Min ���

// OriginBlock Ŭ������ ���� ������Ʈ ���� ���ǵǾ� �ִٰ� ����

public class LSBRenderFeature : ScriptableRendererFeature
{
    [Header("���̴� �� ����")]
    public ComputeShader lsbComputeShader;
    [Tooltip("Spatial LSB ������� ��Ʈ��Ʈ���� �Ӻ������� ����")]
    public bool embedBitstream = true;
    [Tooltip("Addressables���� �ε��� ��ȣȭ�� ������ Ű")]
    public string addressableKey = "OriginBlockData";

    private LSBRenderPass lsbRenderPass;
    private byte[] cachedEncryptedData = null;

    public override void Create()
    {
        if (lsbComputeShader == null) { /* ���� ó�� */ return; }

        lsbRenderPass = new LSBRenderPass(lsbComputeShader, name, embedBitstream);
        lsbRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var camera = renderingData.cameraData.camera;
        if (camera.cameraType != CameraType.Game) { return; } // ���� ī�޶� �ƴ� ��� �н�

        if (lsbComputeShader != null && lsbRenderPass != null)
        {
            lsbRenderPass.SetEmbedActive(embedBitstream);
            if (DataManager.IsDataReady)
            {
                renderer.EnqueuePass(lsbRenderPass);
            }
        }
    }

    protected override void Dispose(bool disposing) { lsbRenderPass?.Cleanup(); }

    // --- LSB Render Pass ���� Ŭ���� ---
    class LSBRenderPass : ScriptableRenderPass
    {
        private ComputeShader computeShader;
        private int kernelID;
        private RTHandle sourceTextureHandle, outputTextureHandle;
        private string profilerTag;
        private bool embedActive;
        private ComputeBuffer bitstreamBuffer;
        private List<uint> payloadBits; // ��� ����, �е� �� ���� ���̷ε�
        private List<uint> finalBitsToEmbed; // ���� ���Ե� ��Ʈ (�е� �Ϸ�)

        private bool isReadbackPending = false;

        private const int THREAD_GROUP_SIZE_X = 8;
        private const int THREAD_GROUP_SIZE_Y = 8;

        public LSBRenderPass(ComputeShader shader, string tag, bool initialEmbedState)
        {
            computeShader = shader; profilerTag = tag; embedActive = initialEmbedState;
            kernelID = computeShader.FindKernel("LSBEmbedKernel");
            if (kernelID < 0) Debug.LogError("Kernel LSBEmbedKernel ã�� ����");

            // �ʱ� ���̷ε� ���� (���+������)
            payloadBits = new List<uint>(); // �ʱ�ȭ
            finalBitsToEmbed = new List<uint>();
        }

        public void SetEmbedActive(bool isActive) { embedActive = isActive; }

        private void UpdateBitstreamBuffer(List<uint> data)
        { /* ... LSBRenderPass�� ���� ... */
            int count = (data != null) ? data.Count : 0;
            if (count == 0)
            {
                if (bitstreamBuffer != null)
                {
                    bitstreamBuffer.Release();
                    bitstreamBuffer = null;
                }
                return;
            }
            if (bitstreamBuffer == null || bitstreamBuffer.count != count || !bitstreamBuffer.IsValid())
            {
                bitstreamBuffer?.Release();
                try
                {
                    bitstreamBuffer = new ComputeBuffer(count, sizeof(uint), ComputeBufferType.Structured);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LSBRenderPass] ComputeBuffer ���� ����: {ex.Message}");
                    bitstreamBuffer = null;
                    return;
                }
            }

            try
            {
                bitstreamBuffer.SetData(data);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LSBRenderPass] ComputeBuffer SetData ����: {ex.Message}");
                bitstreamBuffer?.Release();
                bitstreamBuffer = null;
            }
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0; 
            desc.msaaSamples = 1;
            desc.sRGB = false; // sRGB ��Ȱ��ȭ

            var outputDesc = desc;
            outputDesc.enableRandomWrite = true;

            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SourceCopyForLSB");
            RenderingUtils.ReAllocateIfNeeded(ref outputTextureHandle, outputDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_LSBOutput");

            // ���� ���Ե� ��Ʈ ����Ʈ �ʱ�ȭ
            finalBitsToEmbed = finalBitsToEmbed ?? new List<uint>(); // Null�̸� ���� ����
            finalBitsToEmbed.Clear();

            // embedActive �÷��װ� Ȱ��ȭ �Ǿ� �ְ�, DataManager�� ���� ������ �ε��� �Ϸ�Ǿ����� Ȯ��
            // <<< ����/�߰��� �κ� ���� >>>
            if (embedActive && DataManager.IsDataReady && DataManager.EncryptedOriginData != null)
            {
                // �����Ͱ� �غ�Ǿ����Ƿ� ���̷ε� ���� �� �е� �õ�
                try
                {
                    // 1. DataManager���� ���� ���� ���̷ε� ����
                    List<uint> currentPayload = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);

                    // 2. ���� ��� Ȯ��
                    if (currentPayload == null || currentPayload.Count == 0)
                    {
                        Debug.LogWarning("[DCTRenderPass] ���� ���̷ε� ���� ���� �Ǵ� ������ ����.");
                        // finalBitsToEmbed�� �̹� Clear()�� �����̹Ƿ� �� �� �� ����
                    }
                    else
                    {
                        // 3. �е� ���� ���� (currentPayload�� �������� ���)
                        int width = desc.width;
                        int height = desc.height;
                        int availableCapacity = width * height;
                        int totalPayloadLength = currentPayload.Count; // <- currentPayload ���

                        if (availableCapacity == 0)
                        {
                            Debug.LogWarning("[DCTRenderPass] �̹��� ũ�Ⱑ �۾� ��� ���� �Ұ�.");
                        }
                        else
                        {
                            finalBitsToEmbed.Clear(); // �е� ���� Ȯ���� ����
                            finalBitsToEmbed.Capacity = availableCapacity;
                            int currentPosition = 0;
                            while (currentPosition < availableCapacity)
                            {
                                int remainingSpace = availableCapacity - currentPosition;
                                int countToAdd = Math.Min(totalPayloadLength, remainingSpace);
                                if (countToAdd <= 0) break; // totalPayloadLength�� 0�� ��� ����
                                finalBitsToEmbed.AddRange(currentPayload.GetRange(0, countToAdd)); // <- currentPayload ���
                                currentPosition += countToAdd;
                            }
                            // Debug.Log($"[DCTRenderPass] �е� �Ϸ�. ���� ũ��: {finalBitsToEmbed.Count}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{profilerTag}] ���̷ε� ���� �Ǵ� �е� �� ����: {ex.Message}");
                    finalBitsToEmbed.Clear(); // ���� �� ���
                }
            }
            // <<< ����/�߰��� �κ� �� >>>
            else
            {
                // �Ӻ��� ��Ȱ��ȭ �Ǵ� ������ ���غ� ����
                // finalBitsToEmbed�� �̹� ��������Ƿ� �߰� �۾� ����
                if (!DataManager.IsDataReady && embedActive)
                {
                    // ������ �ε��� ���� �� ������ �� ���� (��� �α��� ���û���)
                    Debug.LogWarning("[LSBRenderPass] �����Ͱ� ���� �غ���� �ʾ� �Ӻ����� �ǳ�<0xEB><0x91>�ϴ�.");
                }
            }

            // ���� ��Ʈ��Ʈ������ ComputeBuffer ������Ʈ (������ ������ ���� ������)
            UpdateBitstreamBuffer(finalBitsToEmbed);

            // --- ���̴� �Ķ���� ���� ---
            if (kernelID >= 0)
            {
                int currentBitLength = finalBitsToEmbed.Count; // ���� ���ۿ� ��/�� ��Ʈ ��
                                                               // ComputeBuffer�� ������Ʈ �� �� ��ȿ�� ��Ȯ��
                bool bufferValid = bitstreamBuffer != null && bitstreamBuffer.IsValid() && bitstreamBuffer.count == currentBitLength;
                // ���������� ���̴����� �Ӻ����� �������� ���� ����
                bool shouldEmbed = embedActive && DataManager.IsDataReady && bufferValid && currentBitLength > 0;

                cmd.SetComputeTextureParam(computeShader, kernelID, "Source", sourceTextureHandle);
                cmd.SetComputeTextureParam(computeShader, kernelID, "Output", outputTextureHandle);
                cmd.SetComputeIntParam(computeShader, "Width", desc.width);
                cmd.SetComputeIntParam(computeShader, "Height", desc.height);

                // Bitstream ���� ������ ��ȿ�� ���� (shouldEmbed ���� ��� bufferValid ���)
                if (bufferValid && currentBitLength > 0) // ���۰� ������ ��ȿ�� ���� ���ε� �õ�
                {
                    cmd.SetComputeBufferParam(computeShader, kernelID, "Bitstream", bitstreamBuffer);
                }
                // Embed ���� �Ķ���ʹ� �׻� ���� (���̴��� Embed �� ���� ó���ϵ���)
                cmd.SetComputeIntParam(computeShader, "BitLength", shouldEmbed ? currentBitLength : 0);
                cmd.SetComputeIntParam(computeShader, "Embed", shouldEmbed ? 1 : 0);
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (kernelID < 0) return;
            // Optional: Check buffer validity again if needed
            if (embedActive && (bitstreamBuffer == null || !bitstreamBuffer.IsValid()) || !DataManager.IsDataReady)
            {
                Debug.LogWarning("[LSBRenderPass] Embed Ȱ��ȭ �����̳� ComputeBuffer ��ȿ���� ����. Ȥ�� �����Ͱ� �ε� ���� ����");
                return; // ������: ���� �ߴ�
            }


            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
            int width = cameraTarget.rt.width;
            int height = cameraTarget.rt.height;

            cmd.CopyTexture(cameraTarget, sourceTextureHandle); // Copy source

            using (new ProfilingScope(cmd, new ProfilingSampler(profilerTag)))
            {
                int threadGroupsX = Mathf.CeilToInt((float)width / THREAD_GROUP_SIZE_X);
                int threadGroupsY = Mathf.CeilToInt((float)height / THREAD_GROUP_SIZE_Y);
                cmd.DispatchCompute(computeShader, kernelID, threadGroupsX, threadGroupsY, 1); // Dispatch
                cmd.CopyTexture(outputTextureHandle, cameraTarget); // Copy result back

                RTResultHolder.DedicatedSaveTarget = outputTextureHandle;
            }


            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

       
        public override void OnCameraCleanup(CommandBuffer cmd) { }
        public void Cleanup()
        { /* ������ ���� */
            bitstreamBuffer?.Release(); bitstreamBuffer = null;
            RTHandles.Release(sourceTextureHandle); sourceTextureHandle = null;
            RTHandles.Release(outputTextureHandle); outputTextureHandle = null;
        }
    }
}