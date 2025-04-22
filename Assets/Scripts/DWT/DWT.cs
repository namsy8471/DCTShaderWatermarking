using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.Linq;
using System;
using Unity.Collections;
using System.IO;

// OriginBlock Ŭ������ ���� ������Ʈ ���� ���ǵǾ� �ִٰ� ����
// DataManager Ŭ������ ���� ������Ʈ ���� ���ǵǾ� �ִٰ� ����
// SaveTrigger Ŭ������ ���� ������Ʈ ���� ���ǵǾ� �ִٰ� ����
// RTResultHolder Ŭ������ ���� ������Ʈ ���� ���ǵǾ� �ִٰ� ����

public class DWTRenderFeature : ScriptableRendererFeature
{
    [Header("���̴� �� ����")]
    public ComputeShader dwtComputeShader; // DWT�� .compute ���� �Ҵ�
    [Tooltip("DWT ����� ��Ʈ��Ʈ���� �Ӻ������� ����")]
    public bool embedBitstream = true;

    [Tooltip("Addressables���� �ε��� ��ȣȭ�� ������ Ű")]
    public string addressableKey = "OriginBlockData"; // DCT�� �����ϰ� ����ϰų� �ʿ�� ����

    // DWT ���� ������ Compute Shader���� �����ϰų� �ʿ�� �߰� (��: ����)
    // public int dwtLevel = 1; // ���� ���� DWT�� ���� ���

    private DWTRenderPass dwtRenderPass;

    public override void Create()
    {
        if (dwtComputeShader == null)
        {
            Debug.LogError("DWT Compute Shader�� �Ҵ���� �ʾҽ��ϴ�.");
            return;
        }

        dwtRenderPass = new DWTRenderPass(dwtComputeShader, name, embedBitstream);
        // ������ ���������ο��� ������ ���� ���� (AfterRenderingPostProcessing ��)
        dwtRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var camera = renderingData.cameraData.camera;
        if (camera.cameraType != CameraType.Game) { return; } // ���� ī�޶� ó��

        if (dwtComputeShader != null && dwtRenderPass != null)
        {
            dwtRenderPass.SetEmbedActive(embedBitstream);
            // DataManager.IsDataReady�� ���� ������ �غ� ���� Ȯ�� ������ DCT�� �����ϰ� ����
            if (DataManager.IsDataReady) // ������ �ε� �Ϸ� ���� Ȯ��
            {
                renderer.EnqueuePass(dwtRenderPass);
            }
            else if (embedBitstream) // �Ӻ����� Ȱ���ε� ������ �غ� �ȵ����� ���
            {
                Debug.LogWarning("[DWTRenderFeature] ������ ���غ�. �Ӻ��� �н� �ǳʶ�.");
            }
            else // �Ӻ��� ��Ȱ���̸� ������ ��� �н� �߰� ���� (���û���)
            {
                renderer.EnqueuePass(dwtRenderPass); // �Ӻ��� �� �� ���� �׳� ����
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        dwtRenderPass?.Cleanup();
    }

    //-------------------------------------------------------------------------
    // DWT Render Pass
    //-------------------------------------------------------------------------
    class DWTRenderPass : ScriptableRenderPass
    {
        private ComputeShader computeShader;
        private int dwtRowsKernelID, dwtColsKernelID, idwtColsKernelID, idwtRowsKernelID;
        private RTHandle sourceTextureHandle, intermediateHandle, dwtOutputHandle, idwtOutputHandle, chromaBufferHandle;
        private string profilerTag;
        private bool embedActive;
        private ComputeBuffer bitstreamBuffer;

        private List<uint> finalBitsToEmbed; // ���� ���Ե� ��Ʈ (�е� �Ϸ�)

        // DWT�� QIM Delta, u/v Index ���ʿ�
        // �ʿ�� DWT ���� ���� �Ķ���� �߰� ����

        private const int BLOCK_SIZE = 8; // DWT ��� ũ�� (Haar�� 2�� �ŵ����� �ʿ�, 8x8 ����)

        public DWTRenderPass(ComputeShader shader, string tag, bool initialEmbedState)
        {
            computeShader = shader;
            profilerTag = tag;
            embedActive = initialEmbedState;

            // Ŀ�� �̸� ����
            dwtRowsKernelID = shader.FindKernel("DWT_Pass1_Rows");
            dwtColsKernelID = shader.FindKernel("DWT_Pass2_Cols_Embed"); // �Ӻ��� ���� Ŀ��
            idwtColsKernelID = shader.FindKernel("IDWT_Pass1_Cols");
            idwtRowsKernelID = shader.FindKernel("IDWT_Pass2_Rows");

            finalBitsToEmbed = new List<uint>();

            // Ŀ�� ��ȿ�� �˻�
            if (dwtRowsKernelID < 0 || dwtColsKernelID < 0 || idwtColsKernelID < 0 || idwtRowsKernelID < 0)
            {
                Debug.LogError($"[DWTRenderPass] �ϳ� �̻��� DWT Compute Shader Ŀ���� ã�� �� �����ϴ�. Ŀ�� �̸��� Ȯ���ϼ���: DWT_Pass1_Rows, DWT_Pass2_Cols_Embed, IDWT_Pass1_Cols, IDWT_Pass2_Rows");
            }
        }

        public void SetEmbedActive(bool isActive) { embedActive = isActive; }

        // UpdateBitstreamBuffer�� DCT�� �����ϰ� ��� ����
        private void UpdateBitstreamBuffer(List<uint> data)
        {
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
                    // DWT �Ӻ����� ���� StructuredBuffer ���
                    bitstreamBuffer = new ComputeBuffer(count, sizeof(uint), ComputeBufferType.Structured);
                    Debug.Log($"[DWTRenderPass] ComputeBuffer ������ (Count: {count})");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[DWTRenderPass] ComputeBuffer ���� ����: {ex.Message}");
                    bitstreamBuffer = null;
                    return;
                }
            }

            try
            {
                bitstreamBuffer.SetData(data);
                Debug.Log($"[DWTRenderPass] ComputeBuffer ������ ���� �Ϸ� (Count: {count})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DWTRenderPass] ComputeBuffer SetData ����: {ex.Message}");
                bitstreamBuffer?.Release();
                bitstreamBuffer = null;
            }
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.sRGB = false; // Linear �۾� ���� ����

            // RTHandle �Ҵ� (DCT�� �����ϰ�, ���� Ȯ��)
            var intermediateDesc = desc;
            intermediateDesc.colorFormat = RenderTextureFormat.RFloat; // Y ä�ο� Float
            intermediateDesc.enableRandomWrite = true;

            var dwtDesc = desc;
            dwtDesc.colorFormat = RenderTextureFormat.RFloat; // DWT ����� Float
            dwtDesc.enableRandomWrite = true;


            var chromaDesc = desc; // CbCr �����
            chromaDesc.colorFormat = RenderTextureFormat.RGFloat; // CbCr�� float2
            chromaDesc.enableRandomWrite = true;

            var idwtDesc = desc; // ���� �����
            idwtDesc.enableRandomWrite = true;
            // idwtDesc.colorFormat = RenderTextureFormat.ARGBFloat; // ���� ��� ���� Ȯ��


            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, name: "_SourceCopyForDWT");
            RenderingUtils.ReAllocateIfNeeded(ref intermediateHandle, intermediateDesc, FilterMode.Point, name: "_IntermediateDWT_IDWT"); // �߰� Y'
            RenderingUtils.ReAllocateIfNeeded(ref dwtOutputHandle, dwtDesc, FilterMode.Point, name: "_DWTOutput"); // ���� Y DWT ���
            RenderingUtils.ReAllocateIfNeeded(ref chromaBufferHandle, chromaDesc, FilterMode.Point, name: "_ChromaBufferCbCr"); // CbCr ����
            RenderingUtils.ReAllocateIfNeeded(ref idwtOutputHandle, idwtDesc, FilterMode.Point, name: "_IDWTOutput"); // ���� RGB ���

            // --- ��Ʈ��Ʈ�� �غ� (DCT�� ���� ���� ���) ---
            finalBitsToEmbed = finalBitsToEmbed ?? new List<uint>();
            finalBitsToEmbed.Clear();

            if (embedActive && DataManager.IsDataReady && DataManager.EncryptedOriginData != null)
            {
                try
                {
                    List<uint> currentPayload = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);

                    if (currentPayload == null || currentPayload.Count == 0)
                    {
                        Debug.LogWarning("[DWTRenderPass] ���� ���̷ε� ���� ���� �Ǵ� ������ ����.");
                    }
                    else
                    {
                        // �е� ���� (DCT�� ����: ��� ������ŭ �뷮 ���)
                        int width = desc.width;
                        int height = desc.height;
                        // ���� ������ Ȯ��: width/BLOCK_SIZE
                        int numBlocksX = Mathf.Max(1, width / BLOCK_SIZE); // �ּ� 1�� ���
                        int numBlocksY = Mathf.Max(1, height / BLOCK_SIZE); // �ּ� 1�� ���
                        int availableCapacity = numBlocksX * numBlocksY; // �� ��ϴ� 1��Ʈ ����
                        int totalPayloadLength = currentPayload.Count;

                        Debug.Log($"[DWTRenderPass] �̹��� ũ��: {width}x{height}, ��� ũ��: {BLOCK_SIZE}, �� ��� ��: {availableCapacity}, ���� ���̷ε� ����: {totalPayloadLength}");


                        if (availableCapacity == 0)
                        {
                            Debug.LogWarning("[DWTRenderPass] �̹��� ũ�Ⱑ �۾� ��� ���� �Ұ�.");
                        }
                        else if (totalPayloadLength == 0)
                        {
                            Debug.LogWarning("[DWTRenderPass] ���� ���̷ε� ���̰� 0�Դϴ�.");
                        }
                        else
                        {
                            finalBitsToEmbed.Clear();
                            finalBitsToEmbed.Capacity = availableCapacity; // �ʿ��� �뷮��ŭ Ȯ�� �õ�
                            int currentPosition = 0;
                            while (currentPosition < availableCapacity)
                            {
                                int remainingSpace = availableCapacity - currentPosition;
                                int countToAdd = Math.Min(totalPayloadLength, remainingSpace);
                                if (countToAdd <= 0) break;

                                // currentPayload���� �ʿ��� ��ŭ ��������
                                finalBitsToEmbed.AddRange(currentPayload.GetRange(0, countToAdd));
                                currentPosition += countToAdd;

                                // ���� ���̷ε带 �ݺ��ؼ� ä���� �Ѵٸ�, GetRange �ε��� ���� �ʿ�
                                // ���⼭�� ���̷ε尡 �뷮���� ������ �״�� �ΰ�, ũ�� �߶� ����
                            }
                            // �뷮���� ���̷ε尡 ������ �������� 0���� ä�� ���� ���� (���� ����)
                            // while (finalBitsToEmbed.Count < availableCapacity) { finalBitsToEmbed.Add(0); }

                            Debug.Log($"[DWTRenderPass] �е�/���� �Ϸ�. ���� ��Ʈ ��: {finalBitsToEmbed.Count} (�뷮: {availableCapacity})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{profilerTag}] ���̷ε� ���� �Ǵ� �е� �� ����: {ex.Message}\n{ex.StackTrace}");
                    finalBitsToEmbed.Clear();
                }
            }
            else
            {
                if (!DataManager.IsDataReady && embedActive)
                {
                    // Debug.LogWarning("[DWTRenderPass] ������ ���غ� �Ǵ� �Ӻ��� ��Ȱ��ȭ. ���̷ε� ��� ����.");
                }
                // finalBitsToEmbed�� �̹� ����ִ� ����
            }


            // --- ����� �α� (DCT�� ����) ---
            if (finalBitsToEmbed != null && finalBitsToEmbed.Count > 0)
            {
                int logLength = Math.Min(finalBitsToEmbed.Count, 64); // �α� ��� ���� ����
                string firstBits = string.Join("", finalBitsToEmbed.Take(logLength).Select(b => b.ToString()));
                Debug.Log($"[DWTRenderPass] Shader�� ���޵� ���� ��Ʈ (ó�� {logLength}��): {firstBits}");
                Debug.Log($"[DWTRenderPass] ���� ��Ʈ �� ���� (BitLength): {finalBitsToEmbed.Count}");
            }
            else
            {
                Debug.LogWarning("[DWTRenderPass] finalBitsToEmbed�� �������. ���̴��� ���޵� ���̷ε� ����.");
            }

            // ���� ��Ʈ��Ʈ������ ComputeBuffer ������Ʈ
            UpdateBitstreamBuffer(finalBitsToEmbed);

            // --- ���̴� �Ķ���� ���� ---
            int currentBitLength = finalBitsToEmbed.Count;
            bool bufferValid = bitstreamBuffer != null && bitstreamBuffer.IsValid() && bitstreamBuffer.count == currentBitLength;

            // ���� �Ӻ��� ����: Ȱ��ȭ ����, ������ �غ� �Ϸ�, ���� ��ȿ, ��Ʈ ���� > 0
            bool shouldEmbed = embedActive && DataManager.IsDataReady && bufferValid && currentBitLength > 0;

            // Debug.Log($"[DWTRenderPass] ���� ��Ʈ��Ʈ�� ����: {currentBitLength} / ���� ��ȿ��: {bufferValid} / �Ӻ��� Ȱ��: {embedActive} / ������ �غ�: {DataManager.IsDataReady} => ���� �Ӻ��� ����: {shouldEmbed}");


            // ���� �Ķ���� ����
            cmd.SetComputeIntParam(computeShader, "Width", desc.width);
            cmd.SetComputeIntParam(computeShader, "Height", desc.height);

            // �� Ŀ�ο� �ʿ��� �ؽ�ó �� ���� ���ε�
            if (dwtRowsKernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, dwtRowsKernelID, "Source", sourceTextureHandle);
                cmd.SetComputeTextureParam(computeShader, dwtRowsKernelID, "IntermediateYBuffer", intermediateHandle); // Pass 1 ��� Y'
                cmd.SetComputeTextureParam(computeShader, dwtRowsKernelID, "ChromaBuffer", chromaBufferHandle);      // Pass 1 ��� CbCr
            }
            if (dwtColsKernelID >= 0) // DWT Pass 2 + Embed
            {
                cmd.SetComputeTextureParam(computeShader, dwtColsKernelID, "IntermediateYBuffer", intermediateHandle); // Pass 2 �Է� Y'
                cmd.SetComputeTextureParam(computeShader, dwtColsKernelID, "DWTOutputY", dwtOutputHandle);          // Pass 2 ��� Y DWT ���

                // �Ӻ��� ���� �Ķ���� ����
                cmd.SetComputeIntParam(computeShader, "Embed", shouldEmbed ? 1 : 0);
                if (shouldEmbed) // ���� �Ӻ����� ���� ���� ����
                {
                    cmd.SetComputeIntParam(computeShader, "BitLength", currentBitLength);
                    // ���۰� ��ȿ�� ���� ���ε� �õ�
                    if (bufferValid)
                    {
                        cmd.SetComputeBufferParam(computeShader, dwtColsKernelID, "Bitstream", bitstreamBuffer);
                        Debug.Log($"[DWTRenderPass] DWT Pass 2: Bitstream Buffer ���� (Count: {currentBitLength})");
                    }
                    else
                    {
                        Debug.LogWarning($"[DWTRenderPass] DWT Pass 2: Bitstream Buffer ��ȿ���� ���� (Count: {currentBitLength}, IsValid: {bufferValid}). �Ӻ��� �ǳʶ� �� ����.");
                        // ������ ���� Embed �÷��׸� 0���� ������ ���� ����
                        // cmd.SetComputeIntParam(computeShader, "Embed", 0);
                    }
                }
                else
                {
                    cmd.SetComputeIntParam(computeShader, "BitLength", 0); // �Ӻ��� �� �� �� ���� 0
                    // Debug.LogWarning($"[DWTRenderPass] DWT Pass 2: �Ӻ��� ��Ȱ��ȭ �Ǵ� ���� ������. Bitstream ���� �ǳʶ�.");
                }
            }
            if (idwtColsKernelID >= 0) // IDWT Pass 1
            {
                cmd.SetComputeTextureParam(computeShader, idwtColsKernelID, "DWTOutputY", dwtOutputHandle);          // �Է�: Y DWT ���
                cmd.SetComputeTextureParam(computeShader, idwtColsKernelID, "IntermediateYBuffer", intermediateHandle); // ���: �߰� Y'
            }
            if (idwtRowsKernelID >= 0) // IDWT Pass 2
            {
                cmd.SetComputeTextureParam(computeShader, idwtRowsKernelID, "IntermediateYBuffer", intermediateHandle); // �Է�: �߰� Y'
                cmd.SetComputeTextureParam(computeShader, idwtRowsKernelID, "ChromaBuffer", chromaBufferHandle);      // �Է�: ���� CbCr
                cmd.SetComputeTextureParam(computeShader, idwtRowsKernelID, "IDWTOutput", idwtOutputHandle);        // ���: ���� RGB
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            bool kernelsValid = dwtRowsKernelID >= 0 && dwtColsKernelID >= 0 && idwtColsKernelID >= 0 && idwtRowsKernelID >= 0;
            if (!kernelsValid)
            {
                Debug.LogError("[DWTRenderPass] ��ȿ���� ���� Ŀ�� ID�� ���� Execute �ߴ�.");
                return;
            }

            // �Ӻ��� Ȱ�� �����ε� �ʿ��� ������/���۰� ������ ���� �� ��� �Ǵ� �ߴ� (������)
            bool canProceed = true;
            if (embedActive)
            {
                bool isDataReady = DataManager.IsDataReady;
                bool isBufferReady = bitstreamBuffer != null && bitstreamBuffer.IsValid() && finalBitsToEmbed.Count > 0 && bitstreamBuffer.count == finalBitsToEmbed.Count;

                if (!isDataReady)
                {
                    // Debug.LogWarning("[DWTRenderPass Execute] �Ӻ��� Ȱ�� �����̳� DataManager ���غ�.");
                    // canProceed = false; // ������ ������ �ƿ� ���� �� ��
                }
                if (!isBufferReady)
                {
                    Debug.LogWarning($"[DWTRenderPass Execute] �Ӻ��� Ȱ�� �����̳� ComputeBuffer �غ� �ȵ� (Buffer: {bitstreamBuffer != null}, Valid: {bitstreamBuffer?.IsValid()}, Count Match: {bitstreamBuffer?.count == finalBitsToEmbed.Count}).");
                    // canProceed = false; // ���� ���� �� ���� �� �� (���̴����� Embed=0 ó���ϹǷ� ��� �����ص� �� �� ����)
                }
            }

            if (!canProceed)
            {
                // Debug.LogWarning("[DWTRenderPass] �ʼ� ���� ���������� Execute �ǳ�<0xEB><0x91>.");
                // ���� �׳� ������ ����ϰ� �ʹٸ� Blit�� ����
                // CommandBuffer cmdSkip = CommandBufferPool.Get(profilerTag + "_Skip");
                // Blit(renderingData.cameraData.renderer.cameraColorTargetHandle, renderingData.cameraData.renderer.cameraColorTargetHandle);
                // context.ExecuteCommandBuffer(cmdSkip);
                // CommandBufferPool.Release(cmdSkip);
                return;
            }


            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;

            // RTHandle ��ȿ�� �˻� �߰�
            if (sourceTextureHandle == null || intermediateHandle == null || dwtOutputHandle == null || idwtOutputHandle == null || chromaBufferHandle == null || cameraTarget == null)
            {
                Debug.LogError("[DWTRenderPass] �ϳ� �̻��� RTHandle�� ��ȿ���� �ʽ��ϴ�. Execute �ߴ�.");
                CommandBufferPool.Release(cmd);
                return;
            }
            // ���� Ÿ�� ũ�� Ȯ�� (0�̸� Dispatch �Ұ�)
            int width = cameraTarget.rt?.width ?? renderingData.cameraData.cameraTargetDescriptor.width;
            int height = cameraTarget.rt?.height ?? renderingData.cameraData.cameraTargetDescriptor.height;

            if (width <= 0 || height <= 0)
            {
                Debug.LogError($"[DWTRenderPass] ��ȿ���� ���� ���� Ÿ�� ũ�� ({width}x{height}). Execute �ߴ�.");
                CommandBufferPool.Release(cmd);
                return;
            }


            // ������ �׷� ��� (DCT�� ����)
            // Mathf.CeilToInt ��� ���� ������ �ø� ���: (N + M - 1) / M
            int threadGroupsX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int threadGroupsY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;

            // Check for zero thread groups
            if (threadGroupsX <= 0 || threadGroupsY <= 0)
            {
                Debug.LogError($"[DWTRenderPass] ���� ������ �׷� ���� 0 �����Դϴ�. (X: {threadGroupsX}, Y: {threadGroupsY}). Dispatch �Ұ�.");
                CommandBufferPool.Release(cmd);
                return;
            }


            // 1. ���� �ؽ�ó ����
            cmd.Blit(cameraTarget, sourceTextureHandle);

            using (new ProfilingScope(cmd, new ProfilingSampler(profilerTag)))
            {
                // 2. DWT ���� (Rows -> Cols + Embed)
                cmd.DispatchCompute(computeShader, dwtRowsKernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, dwtColsKernelID, threadGroupsX, threadGroupsY, 1);

                // 3. IDWT ���� (Cols -> Rows + Combine)
                cmd.DispatchCompute(computeShader, idwtColsKernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, idwtRowsKernelID, threadGroupsX, threadGroupsY, 1);

                // 4. ���� ����� ī�޶� Ÿ������ ����
                cmd.Blit(idwtOutputHandle, cameraTarget);

                // ��ũ���� ��� ������ ���� Ÿ�� ����
                RTResultHolder.DedicatedSaveTarget = cameraTarget; // ���� ����� ���� Ÿ��
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }


        public override void OnCameraCleanup(CommandBuffer cmd) { }

        public void Cleanup()
        {
            bitstreamBuffer?.Release(); bitstreamBuffer = null;
            RTHandles.Release(sourceTextureHandle); sourceTextureHandle = null;
            RTHandles.Release(intermediateHandle); intermediateHandle = null;
            RTHandles.Release(dwtOutputHandle); dwtOutputHandle = null;
            RTHandles.Release(idwtOutputHandle); idwtOutputHandle = null;
            RTHandles.Release(chromaBufferHandle); chromaBufferHandle = null;
        }
    }
}