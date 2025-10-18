using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.Linq;
using System;

public class LSBRenderFeature : WatermarkRenderFeatureBase
{
    [Header("셰이더 및 설정")]
    public ComputeShader lsbComputeShader;
    
    [Tooltip("Addressables에서 로드할 암호화된 데이터 키")]
    public string addressableKey = "OriginBlockData";

    private LSBRenderPass lsbRenderPass;

    protected override ComputeShader GetComputeShader() => lsbComputeShader;
    protected override string GetFeatureName() => name;
    protected override WatermarkRenderPassBase GetRenderPass() => lsbRenderPass;

    protected override WatermarkRenderPassBase CreateRenderPass()
    {
        lsbRenderPass = new LSBRenderPass(lsbComputeShader, name, embedBitstream);
        return lsbRenderPass;
    }

    protected override void UpdateRenderPassParameters(WatermarkRenderPassBase pass)
    {
        // LSB has no additional parameters to update
    }

    // --- LSB Render Pass ---
    class LSBRenderPass : WatermarkRenderPassBase
    {
        private int kernelID;
        private RTHandle sourceTextureHandle, outputTextureHandle;

        private const int THREAD_GROUP_SIZE_X = 8;
        private const int THREAD_GROUP_SIZE_Y = 8;

        private int lastWidth = 0;
        private int lastHeight = 0;

        public LSBRenderPass(ComputeShader shader, string tag, bool initialEmbedState)
            : base(shader, tag, initialEmbedState)
        {
            kernelID = computeShader.FindKernel("LSBEmbedKernel");
            
            if (kernelID < 0)
            {
                Debug.LogError($"[{profilerTag}] Kernel LSBEmbedKernel 찾기 실패");
            }
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.colorFormat = RenderTextureFormat.ARGB32;
            desc.sRGB = false;

            var outputDesc = desc;
            outputDesc.enableRandomWrite = true;

            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SourceCopyForLSB");
            RenderingUtils.ReAllocateIfNeeded(ref outputTextureHandle, outputDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_LSBOutput");

            int width = desc.width;
            int height = desc.height;
            int availableCapacity = width * height;

            // Prepare bitstream
            if (finalBitsToEmbed.Count != availableCapacity || width != lastWidth || height != lastHeight)
            {
                PrepareBitstreamBuffer(availableCapacity);
                UpdateBitstreamBuffer(finalBitsToEmbed);
                
                lastWidth = width;
                lastHeight = height;
            }

            // Set shader parameters
            if (kernelID >= 0)
            {
                int currentBitLength = finalBitsToEmbed.Count;
                bool shouldEmbed = ShouldEmbedOnGPU(currentBitLength);

                cmd.SetComputeTextureParam(computeShader, kernelID, "Source", sourceTextureHandle);
                cmd.SetComputeTextureParam(computeShader, kernelID, "Output", outputTextureHandle);
                cmd.SetComputeIntParam(computeShader, "Width", width);
                cmd.SetComputeIntParam(computeShader, "Height", height);

                // ✅ 항상 유효한 버퍼 바인딩 (더미 버퍼 사용)
                cmd.SetComputeBufferParam(computeShader, kernelID, "Bitstream", GetValidBitstreamBuffer());

                cmd.SetComputeIntParam(computeShader, "BitLength", shouldEmbed ? currentBitLength : 0);
                cmd.SetComputeIntParam(computeShader, "Embed", shouldEmbed ? 1 : 0);
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (kernelID < 0) return;

            // Log once if embedding is requested but data isn't ready
            if (embedActive && (bitstreamBuffer == null || !bitstreamBuffer.IsValid() || !DataManager.IsDataReady))
            {
                if (!DataManager.IsDataReady)
                {
                    Debug.LogWarning($"[{profilerTag}] 데이터가 아직 로드되지 않아 워터마크 없이 렌더링합니다. GameInitializer가 씬에 있는지 확인하세요.");
                }
                // Continue rendering without embedding instead of returning
            }

            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
            
            int width = cameraTarget.rt.width;
            int height = cameraTarget.rt.height;

            cmd.CopyTexture(cameraTarget, sourceTextureHandle);
            RTResultHolder.DedicatedSaveTargetBeforeEmbedding = sourceTextureHandle;

            using (new ProfilingScope(cmd, profilingSampler))
            {
                int threadGroupsX = Mathf.CeilToInt((float)width / THREAD_GROUP_SIZE_X);
                int threadGroupsY = Mathf.CeilToInt((float)height / THREAD_GROUP_SIZE_Y);

                // Execute the compute shader - it will check Embed flag internally
                cmd.DispatchCompute(computeShader, kernelID, threadGroupsX, threadGroupsY, 1);
                cmd.CopyTexture(outputTextureHandle, cameraTarget);

                RTResultHolder.DedicatedSaveTarget = outputTextureHandle;
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) { }

        public override void Cleanup()
        {
            base.Cleanup();
            
            RTHandles.Release(sourceTextureHandle); sourceTextureHandle = null;
            RTHandles.Release(outputTextureHandle); outputTextureHandle = null;
        }
    }
}