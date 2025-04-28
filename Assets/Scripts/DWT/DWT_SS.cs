// DWTRenderFeature.cs
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.Linq;
using System;
using Unity.Collections;
using System.IO;
using System.Net.NetworkInformation;

// OriginBlock Ŭ������ ���� ������Ʈ ���� ���ǵǾ� �ִٰ� ����
// DataManager Ŭ������ ���� ������Ʈ ���� ���ǵǾ� �ִٰ� ����
// SaveTrigger Ŭ������ ���� ������Ʈ ���� ���ǵǾ� �ִٰ� ����
// RTResultHolder Ŭ������ ���� ������Ʈ ���� ���ǵǾ� �ִٰ� ����

public class DWTRenderFeature_SS : ScriptableRendererFeature
{
    [Header("���̴� �� ����")]
    public ComputeShader dwtComputeShader; // DWT�� .compute ���� �Ҵ�
    [Tooltip("DWT ����� ��Ʈ��Ʈ���� �Ӻ������� ����")]
    public bool embedBitstream = true;
    [Tooltip("Ȯ�� ����Ʈ�� �Ӻ��� ����")]
    public float embeddingStrength = 0.05f; // ���� ���� �Ķ���� �߰� (�� ���� �ʿ�)
    [Tooltip("Addressables���� �ε��� ��ȣȭ�� ������ Ű")]
    public string addressableKey = "OriginBlockData";
    [Tooltip("��ϴ� ����� Ȯ�� ����Ʈ�� ��� ���� (��: HH ���� ��)")]
    [Range(1,16)]
    public uint coefficientsToUse = 10; // ����� ��� ���� �߰� (�ִ� 16�� - 8x8��� HH)


    private DWTRenderPass dwtRenderPass;

    public override void Create()
    {
        if (dwtComputeShader == null)
        {
            Debug.LogError("DWT Compute Shader�� �Ҵ���� �ʾҽ��ϴ�.");
            return;
        }

        // Pass ���� �� �Ķ���� ����
        dwtRenderPass = new DWTRenderPass(dwtComputeShader, name, embedBitstream, embeddingStrength, coefficientsToUse, addressableKey);
        dwtRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var camera = renderingData.cameraData.camera;
        if (camera.cameraType != CameraType.Game) { return; }

        if (dwtComputeShader != null && dwtRenderPass != null)
        {
            // �� ������ ���� ������Ʈ (Inspector ���� ���� �ݿ�)
            dwtRenderPass.SetEmbedActive(embedBitstream);
            dwtRenderPass.SetParameters(embeddingStrength, coefficientsToUse);

            if (DataManager.IsDataReady)
            {
                // ���� ���� ���� �� ������Ʈ ���� �߰� (�Ź� �� �ʿ�� ���� �� ����)
                dwtRenderPass.UpdatePatternBufferIfNeeded(renderingData.cameraData.cameraTargetDescriptor);
                renderer.EnqueuePass(dwtRenderPass);
            }
            else if (embedBitstream)
            {
                Debug.LogWarning("[DWTRenderFeature] ������ ���غ�. �Ӻ��� �н� �ǳʶ�.");
            }
            // �Ӻ��� ��Ȱ���� �׳� �����Ű�� ������ ���� (������ �ʿ��ϸ� �н� �߰� ����)
            // else
            // {
            //     // �Ӻ��� �� �� ���� �н� �߰� ���ʿ� (���� ȭ���� �׳� ����)
            // }
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
        private RTHandle sourceTextureHandle, intermediateHandle, dwtOutputHandle, idwtOutputHandle;

        private string profilerTag;
        private bool embedActive;
        private float currentEmbeddingStrength; // ���� ���� ����
        private uint currentCoefficientsToUse; // ���� ����� ��� ���� ����
        private string secretKey;

        private ComputeBuffer bitstreamBuffer;
        private ComputeBuffer patternBuffer; // Ȯ�� ���� ���� �߰�

        private List<uint> finalBitsToEmbed;
        private List<float> currentPatternData; // ���� ������ ����� ����Ʈ

        private const int BLOCK_SIZE = 8;
        private const int HALF_BLOCK_SIZE = BLOCK_SIZE / 2;
        private const int HH_COEFFS_PER_BLOCK = HALF_BLOCK_SIZE * HALF_BLOCK_SIZE; // 4x4 = 16

        public DWTRenderPass(ComputeShader shader, string tag, bool initialEmbedState, float initialStrength, uint initialCoeffs, string secretKey)
        {
            computeShader = shader;
            profilerTag = tag;
            embedActive = initialEmbedState;
            currentEmbeddingStrength = initialStrength;
            currentCoefficientsToUse = initialCoeffs;
            this.secretKey = secretKey; // Addressables���� �ε��� Ű ����

            dwtRowsKernelID = shader.FindKernel("DWT_Pass1_Rows");
            dwtColsKernelID = shader.FindKernel("DWT_Pass2_Cols_EmbedSS"); // Ŀ�� �̸� ���� ���� (SS ���)
            idwtColsKernelID = shader.FindKernel("IDWT_Pass1_Cols");
            idwtRowsKernelID = shader.FindKernel("IDWT_Pass2_Rows");

            finalBitsToEmbed = new List<uint>();
            currentPatternData = new List<float>();

            if (dwtRowsKernelID < 0 || dwtColsKernelID < 0 || idwtColsKernelID < 0 || idwtRowsKernelID < 0)
            {
                Debug.LogError($"[DWTRenderPass] �ϳ� �̻��� DWT Compute Shader Ŀ���� ã�� �� �����ϴ�. Ŀ�� �̸��� Ȯ���ϼ���: DWT_Pass1_Rows, DWT_Pass2_Cols_EmbedSS, IDWT_Pass1_Cols, IDWT_Pass2_Rows");
            }
        }

        public void SetEmbedActive(bool isActive) { embedActive = isActive; }
        public void SetParameters(float strength, uint coeffs)
        {
            currentEmbeddingStrength = strength;
            // ����� ��� ������ HH ���� �ִ� ����(16)�� ���� �ʵ��� ����
            currentCoefficientsToUse = Math.Min(coeffs, (uint)HH_COEFFS_PER_BLOCK);
        }

        // �ʿ��� ���� ���� ���� ������Ʈ (��: ���� ���� ��, �Ǵ� ���� ���� ��)
        public void UpdatePatternBufferIfNeeded(RenderTextureDescriptor desc)
        {
            if (!embedActive || currentCoefficientsToUse == 0)
            {
                ReleasePatternBuffer(); // ��� ���ϸ� ����
                return;
            }

            int width = desc.width;
            int height = desc.height;
            int numBlocksX = Mathf.Max(1, (width + BLOCK_SIZE - 1) / BLOCK_SIZE);
            int numBlocksY = Mathf.Max(1, (height + BLOCK_SIZE - 1) / BLOCK_SIZE);
            int totalBlocks = numBlocksX * numBlocksY;
            int requiredPatternSize = totalBlocks * (int)currentCoefficientsToUse;

            if (requiredPatternSize == 0)
            {
                ReleasePatternBuffer();
                return;
            }

            // ���� �����Ͱ� ���ų� ũ�Ⱑ �ٸ��� ���� ����
            if (currentPatternData == null || currentPatternData.Count != requiredPatternSize)
            {
                Debug.Log($"[DWTRenderPass] Pattern Buffer ����/������Ʈ �ʿ�. �䱸 ũ��: {requiredPatternSize}");
                GeneratePatternData(requiredPatternSize, secretKey);
                UpdatePatternComputeBuffer();
            }
            // �̹� �ִٸ� ������Ʈ ���ʿ� (�� ������ ���� ����)
        }

        private void GeneratePatternData(int size, string secretKey)
        {
            currentPatternData = new List<float>(size);
            System.Random random = new System.Random(secretKey.GetHashCode());
            for (int i = 0; i < size; i++)
            {
                // +1 �Ǵ� -1 ���� ����
                currentPatternData.Add((random.NextDouble() < 0.5) ? -1.0f : 1.0f);

            }
            // ù 64�� ���� �α� ��� (������)
            int logLength = Math.Min(currentPatternData.Count, 64);
            string firstPatterns = string.Join(", ", currentPatternData.Take(logLength).Select(p => p.ToString("F1")));
            Debug.Log($"[DWTRenderPass] ������ ���� ������ (ó�� {logLength}��): [{firstPatterns}]");
        }

        private void UpdatePatternComputeBuffer()
        {
            int count = (currentPatternData != null) ? currentPatternData.Count : 0;
            if (count == 0)
            {
                ReleasePatternBuffer();
                return;
            }

            if (patternBuffer == null || patternBuffer.count != count || !patternBuffer.IsValid())
            {
                ReleasePatternBuffer();
                try
                {
                    patternBuffer = new ComputeBuffer(count, sizeof(float), ComputeBufferType.Structured);
                    // Debug.Log($"[DWTRenderPass] Pattern ComputeBuffer ������ (Count: {count})");
                }
                catch (Exception ex) { /* ... ���� ó�� ... */ return; }
            }

            try
            {
                patternBuffer.SetData(currentPatternData);
                // Debug.Log($"[DWTRenderPass] Pattern ComputeBuffer ������ ���� �Ϸ� (Count: {count})");
            }
            catch (Exception ex) { /* ... ���� ó�� ... */ ReleasePatternBuffer(); }
        }


        private void ReleaseBitstreamBuffer()
        {
            if (bitstreamBuffer != null) { bitstreamBuffer.Release(); bitstreamBuffer = null; }
        }
        private void ReleasePatternBuffer()
        {
            if (patternBuffer != null) { patternBuffer.Release(); patternBuffer = null; }
        }


        private void UpdateBitstreamBuffer(List<uint> data) // ���� �Լ� ����
        {
            int count = (data != null) ? data.Count : 0;
            if (count == 0)
            {
                ReleaseBitstreamBuffer();
                return;
            }
            if (bitstreamBuffer == null || bitstreamBuffer.count != count || !bitstreamBuffer.IsValid())
            {
                ReleaseBitstreamBuffer();
                try
                {
                    bitstreamBuffer = new ComputeBuffer(count, sizeof(uint), ComputeBufferType.Structured);
                }
                catch (Exception ex) { /* ... ���� ó�� ... */ return; }
            }
            try
            {
                bitstreamBuffer.SetData(data);
            }
            catch (Exception ex) { /* ... ���� ó�� ... */ ReleaseBitstreamBuffer(); }
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.sRGB = false;

            var bufferDesc = desc;
            bufferDesc.colorFormat = RenderTextureFormat.ARGBFloat;
            bufferDesc.enableRandomWrite = true;

            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, name: "_SourceCopyForDWT");
            RenderingUtils.ReAllocateIfNeeded(ref intermediateHandle, bufferDesc, FilterMode.Point, name: "_IntermediateDWT_IDWT");
            RenderingUtils.ReAllocateIfNeeded(ref dwtOutputHandle, bufferDesc, FilterMode.Point, name: "_DWTOutput");
            RenderingUtils.ReAllocateIfNeeded(ref idwtOutputHandle, bufferDesc, FilterMode.Point, name: "_IDWTOutput");

            // --- ��Ʈ��Ʈ�� �غ� (���� ���� ����) ---

            int width = desc.width;
            int height = desc.height;
            int numBlocksX = Mathf.Max(1, (width + BLOCK_SIZE - 1) / BLOCK_SIZE); // �ø� ���
            int numBlocksY = Mathf.Max(1, (height + BLOCK_SIZE - 1) / BLOCK_SIZE); // �ø� ���
            int availableCapacity = numBlocksX * numBlocksY;

            if (finalBitsToEmbed.Count != availableCapacity)
            {
                finalBitsToEmbed = finalBitsToEmbed ?? new List<uint>();
                finalBitsToEmbed.Clear();

                if (embedActive && DataManager.IsDataReady && DataManager.EncryptedOriginData != null)
                {
                    try
                    {
                        List<uint> currentPayload = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);
                        if (currentPayload != null && currentPayload.Count > 0)
                        {

                            int totalPayloadLength = currentPayload.Count;

                            // Debug.Log($"[DWTRenderPass] �̹��� ũ��: {width}x{height}, ��� ũ��: {BLOCK_SIZE}, �� ��� ��: {availableCapacity}, ���� ���̷ε� ����: {totalPayloadLength}");

                            if (availableCapacity > 0 && totalPayloadLength > 0)
                            {
                                finalBitsToEmbed.Capacity = availableCapacity;
                                int currentPosition = 0;
                                while (currentPosition < availableCapacity)
                                {
                                    int remainingSpace = availableCapacity - currentPosition;
                                    int countToAdd = Math.Min(totalPayloadLength, remainingSpace);
                                    if (countToAdd <= 0) break;
                                    finalBitsToEmbed.AddRange(currentPayload.GetRange(0, countToAdd));
                                    currentPosition += countToAdd;
                                }
                                // Debug.Log($"[DWTRenderPass] �е�/���� �Ϸ�. ���� ��Ʈ ��: {finalBitsToEmbed.Count} (�뷮: {availableCapacity})");
                            }
                        }
                    }
                    catch (Exception ex) { /* ... ���� ó�� ... */ finalBitsToEmbed.Clear(); }
                }

                UpdateBitstreamBuffer(finalBitsToEmbed); // ��Ʈ��Ʈ�� ���� ������Ʈ
            }
            // --- ���̴� �Ķ���� ���� ---
            int currentBitLength = finalBitsToEmbed.Count;
            bool bitstreamBufferValid = bitstreamBuffer != null && bitstreamBuffer.IsValid() && bitstreamBuffer.count == currentBitLength;
            bool patternBufferValid = patternBuffer != null && patternBuffer.IsValid(); // ���� ���� ��ȿ�� �˻� �߰�

            // ���� �Ӻ��� ���� ����: ���� ���� ��ȿ�� �� CoefficientsToUse > 0 ���� �߰�
            bool shouldEmbed = embedActive && DataManager.IsDataReady && bitstreamBufferValid && patternBufferValid && currentBitLength > 0 && currentCoefficientsToUse > 0;

            // Debug.Log($"[DWTRenderPass] ���� ��Ʈ ����: {currentBitLength} / BitBuffer:{bitstreamBufferValid} / PatternBuffer:{patternBufferValid} / Embed:{embedActive} / DataReady:{DataManager.IsDataReady} / Coeffs>0:{currentCoefficientsToUse > 0} => ���� Embed:{shouldEmbed}");

            cmd.SetComputeIntParam(computeShader, "Width", width);
            cmd.SetComputeIntParam(computeShader, "Height", height);
            cmd.SetComputeFloatParam(computeShader, "EmbeddingStrength", currentEmbeddingStrength); // ���� ����
            cmd.SetComputeIntParam(computeShader, "CoefficientsToUse", (int)currentCoefficientsToUse); // ����� ��� ���� ����

            // --- Ŀ�ο� �Ķ���� ���ε� ---
            // DWT Pass 1
            if (dwtRowsKernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, dwtRowsKernelID, "Source", sourceTextureHandle);
                cmd.SetComputeTextureParam(computeShader, dwtRowsKernelID, "IntermediateBuffer", intermediateHandle);
            }
            // DWT Pass 2 + Embed SS
            if (dwtColsKernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, dwtColsKernelID, "IntermediateBuffer", intermediateHandle);
                cmd.SetComputeTextureParam(computeShader, dwtColsKernelID, "DWTOutput", dwtOutputHandle);

                cmd.SetComputeIntParam(computeShader, "Embed", shouldEmbed ? 1 : 0);

                if (shouldEmbed)
                {
                    cmd.SetComputeIntParam(computeShader, "BitLength", currentBitLength);
                    cmd.SetComputeBufferParam(computeShader, dwtColsKernelID, "Bitstream", bitstreamBuffer);
                    cmd.SetComputeBufferParam(computeShader, dwtColsKernelID, "PatternBuffer", patternBuffer);
                }

                else
                {
                    cmd.SetComputeIntParam(computeShader, "BitLength", 0);
                }
                // ���� ���� ���ε�
                // Debug.Log($"[DWTRenderPass] DWT Pass 2: Bitstream({currentBitLength}), PatternBuffer({patternBuffer.count}) ���ε���.");

            }
            // IDWT Pass 1
            if (idwtColsKernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, idwtColsKernelID, "DWTOutput", dwtOutputHandle);
                cmd.SetComputeTextureParam(computeShader, idwtColsKernelID, "IntermediateBuffer", intermediateHandle);
            }
            // IDWT Pass 2
            if (idwtRowsKernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, idwtRowsKernelID, "IntermediateBuffer", intermediateHandle);
                cmd.SetComputeTextureParam(computeShader, idwtRowsKernelID, "IDWTOutput", idwtOutputHandle);
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // ... (���� Ŀ�� ��ȿ�� �˻�, RTHandle ��ȿ�� �˻�, ������ �׷� ��� ���� �����ϰ� ����) ...
            bool kernelsValid = dwtRowsKernelID >= 0 && dwtColsKernelID >= 0 && idwtColsKernelID >= 0 && idwtRowsKernelID >= 0;
            if (!kernelsValid) { /* ... ���� ó�� ... */ return; }

            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;

            // RTHandle ��ȿ�� �˻� �߰�
            if (sourceTextureHandle == null || intermediateHandle == null || dwtOutputHandle == null || idwtOutputHandle == null || cameraTarget == null)
            {
                Debug.LogError("[DWTRenderPass] �ϳ� �̻��� RTHandle�� ��ȿ���� �ʽ��ϴ�. Execute �ߴ�.");
                CommandBufferPool.Release(cmd);
                return;
            }
            int width = cameraTarget.rt?.width ?? renderingData.cameraData.cameraTargetDescriptor.width;
            int height = cameraTarget.rt?.height ?? renderingData.cameraData.cameraTargetDescriptor.height;
            if (width <= 0 || height <= 0) { /* ... ���� ó�� ... */ CommandBufferPool.Release(cmd); return; }

            int threadGroupsX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int threadGroupsY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
            if (threadGroupsX <= 0 || threadGroupsY <= 0) { /* ... ���� ó�� ... */ CommandBufferPool.Release(cmd); return; }


            // �Ӻ��� Ȱ��ȭ�ε� �ʿ��� ���۰� �غ� �ȵ����� �������� ���� (���� ����)
            bool shouldEmbed = embedActive && DataManager.IsDataReady && bitstreamBuffer != null && bitstreamBuffer.IsValid() && patternBuffer != null && patternBuffer.IsValid() && finalBitsToEmbed.Count > 0 && currentCoefficientsToUse > 0;
            if (embedActive && !shouldEmbed)
            {
                Debug.LogWarning("[DWTRenderPass Execute] �Ӻ��� ���� ������, �н� ���� �ǳʶ�.");
                // �� ���, ���� ȭ���� �����ؾ� �ϹǷ� �ƹ� �۾��� ���� �ʰų� Blit(source, target)�� ����
                // ���⼭�� �׳� �����Ͽ� ���� ������ ���� (�Ǵ� Blit �߰�)
                CommandBufferPool.Release(cmd);
                return;
            }


            cmd.Blit(cameraTarget, sourceTextureHandle); // ���� ����
            RTResultHolder.DedicatedSaveTargetBeforeEmbedding = sourceTextureHandle;


            using (new ProfilingScope(cmd, new ProfilingSampler(profilerTag)))
            {
                // DWT
                cmd.DispatchCompute(computeShader, dwtRowsKernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, dwtColsKernelID, threadGroupsX, threadGroupsY, 1); // Embed SS ���� Ŀ��

                // IDWT
                cmd.DispatchCompute(computeShader, idwtColsKernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, idwtRowsKernelID, threadGroupsX, threadGroupsY, 1);

                // ���� ��� ����
                cmd.Blit(idwtOutputHandle, cameraTarget);

                // ��� ����� ���� (�ʿ��)
                RTResultHolder.DedicatedSaveTarget = idwtOutputHandle;
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }


        public override void OnCameraCleanup(CommandBuffer cmd) { }

        public void Cleanup()
        {
            ReleaseBitstreamBuffer(); // ��Ʈ��Ʈ�� ���� ����
            ReleasePatternBuffer(); // ���� ���� ����
            RTHandles.Release(sourceTextureHandle); sourceTextureHandle = null;
            RTHandles.Release(intermediateHandle); intermediateHandle = null;
            RTHandles.Release(dwtOutputHandle); dwtOutputHandle = null;
            RTHandles.Release(idwtOutputHandle); idwtOutputHandle = null;
        }
    }
}