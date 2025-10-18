using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System;
using System.Linq;

public class DCT_RGB_SS_RenderFeature : WatermarkRenderFeatureBase
{
    [Header("셰이더 및 설정")]
    public ComputeShader ssRgbDctComputeShader;

    [Header("확산 스펙트럼(Spread Spectrum) 설정")]
    [Tooltip("삽입 강도")]
    public float embeddingStrength = 0.1f;
    
    [Tooltip("패턴 생성을 위한 비밀 키")]
    public string secretKey = "default_secret_key_rgb_ss";
    
    [Tooltip("블록당 패턴을 적용할 AC 계수의 개수 (1~63)")]
    [Range(1, 63)]
    public int coefficientsToUse = 10;

    private DCT_RGB_SS_RenderPass ssRgbDctRenderPass;

    private const string PASS1_KERNEL = "DCT_Pass1_Rows_RGB";
    private const string PASS2_KERNEL = "DCT_Pass2_Cols_EmbedSS_RGB";
    private const string PASS3_KERNEL = "IDCT_Pass1_Cols_RGB";
    private const string PASS4_KERNEL = "IDCT_Pass2_Rows_RGB";

    protected override ComputeShader GetComputeShader() => ssRgbDctComputeShader;
    protected override string GetFeatureName() => name;
    protected override WatermarkRenderPassBase GetRenderPass() => ssRgbDctRenderPass;

    protected override WatermarkRenderPassBase CreateRenderPass()
    {
        ssRgbDctRenderPass = new DCT_RGB_SS_RenderPass(
            ssRgbDctComputeShader, name, embedBitstream,
            embeddingStrength, secretKey, coefficientsToUse,
            PASS1_KERNEL, PASS2_KERNEL, PASS3_KERNEL, PASS4_KERNEL
        );
        return ssRgbDctRenderPass;
    }

    protected override void UpdateRenderPassParameters(WatermarkRenderPassBase pass)
    {
        if (pass is DCT_RGB_SS_RenderPass dctPass)
        {
            dctPass.UpdateSSParams(embeddingStrength, secretKey, coefficientsToUse);
        }
    }

    // --- Render Pass Class ---
    class DCT_RGB_SS_RenderPass : WatermarkRenderPassBase
    {
        private int kernelPass1, kernelPass2, kernelPass3, kernelPass4;

        // RT Handles
        private RTHandle sourceTextureHandle;
        private RTHandle intermediateBufferRGBHandle;
        private RTHandle dctOutputRGBHandle;
        private RTHandle finalOutputHandle;

        // Pattern buffer for spread spectrum
        private ComputeBuffer patternBuffer;
        private static ComputeBuffer dummyPatternBuffer; // ✅ 추가: 더미 패턴 버퍼
        
        // SS Parameters
        private float currentEmbeddingStrength;
        private string currentSecretKey;
        private int currentCoefficientsToUse;
        private string lastUsedKeyForPattern = null;
        private int lastPatternBufferSize = 0;
        
        // ✅ 추가: 변경 감지를 위한 추적 변수들
        private int lastCoefficientsToUse = 0;
        private int lastTotalBlocks = 0;

        public DCT_RGB_SS_RenderPass(
            ComputeShader shader, string tag, bool initialEmbedState,
            float strength, string key, int numCoeffs,
            string kernel1Name, string kernel2Name, string kernel3Name, string kernel4Name)
            : base(shader, tag, initialEmbedState)
        {
            UpdateSSParams(strength, key, numCoeffs);
            lastCoefficientsToUse = numCoeffs;
            
            // ✅ 더미 패턴 버퍼 초기화
            EnsureDummyPatternBufferExists();

            try
            {
                kernelPass1 = shader.FindKernel(kernel1Name);
                kernelPass2 = shader.FindKernel(kernel2Name);
                kernelPass3 = shader.FindKernel(kernel3Name);
                kernelPass4 = shader.FindKernel(kernel4Name);
                
                if (kernelPass1 < 0 || kernelPass2 < 0 || kernelPass3 < 0 || kernelPass4 < 0)
                {
                    throw new Exception($"하나 이상의 필수 커널을 찾을 수 없습니다.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{profilerTag}] Compute Shader 커널 초기화 실패: {ex.Message}");
                throw;
            }
        }

        // ✅ 더미 패턴 버퍼 생성
        private static void EnsureDummyPatternBufferExists()
        {
            if (dummyPatternBuffer == null || !dummyPatternBuffer.IsValid())
            {
                dummyPatternBuffer = new ComputeBuffer(1, sizeof(float), ComputeBufferType.Structured);
                dummyPatternBuffer.SetData(new float[] { 0f });
            }
        }

        // ✅ 유효한 패턴 버퍼 반환
        private ComputeBuffer GetValidPatternBuffer()
        {
            EnsureDummyPatternBufferExists();
            return (patternBuffer != null && patternBuffer.IsValid()) ? patternBuffer : dummyPatternBuffer;
        }

        public void UpdateSSParams(float strength, string key, int numCoeffs)
        {
            currentEmbeddingStrength = strength;
            currentSecretKey = key;
            currentCoefficientsToUse = Mathf.Clamp(numCoeffs, 1, 63);
        }

        private void UpdatePatternBuffer(int numBlocks)
        {
            if (numBlocks <= 0 || currentCoefficientsToUse <= 0)
            {
                ReleasePatternBuffer();
                return;
            }

            int requiredSize = numBlocks * currentCoefficientsToUse;
            bool needsUpdate = patternBuffer == null || 
                             !patternBuffer.IsValid() ||
                             lastPatternBufferSize != requiredSize || 
                             lastUsedKeyForPattern != currentSecretKey;

            if (needsUpdate)
            {
                ReleasePatternBuffer();
                float[] patternData = new float[requiredSize];
                System.Random prng = new System.Random(currentSecretKey.GetHashCode());
                
                for (int i = 0; i < requiredSize; ++i)
                {
                    patternData[i] = (prng.NextDouble() < 0.5) ? -1.0f : 1.0f;
                }

                try
                {
                    patternBuffer = new ComputeBuffer(requiredSize, sizeof(float), ComputeBufferType.Structured, ComputeBufferMode.Immutable);
                    patternBuffer.SetData(patternData);
                    lastUsedKeyForPattern = currentSecretKey;
                    lastPatternBufferSize = requiredSize;
                    Debug.Log($"[{profilerTag}] 패턴 버퍼 생성: {requiredSize}개 (블록: {numBlocks}, 계수: {currentCoefficientsToUse})");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{profilerTag}] Pattern Buffer 생성 실패 (Size:{requiredSize}): {ex.Message}");
                    ReleasePatternBuffer();
                }
            }
        }

        private void ReleasePatternBuffer()
        {
            patternBuffer?.Release();
            patternBuffer = null;
            lastUsedKeyForPattern = null;
            lastPatternBufferSize = 0;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            if (kernelPass1 < 0 || kernelPass2 < 0 || kernelPass3 < 0 || kernelPass4 < 0 || computeShader == null)
                return;

            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.sRGB = false;

            int width = desc.width;
            int height = desc.height;

            if (width <= 0 || height <= 0) return;

            // Allocate RT Handles
            var bufferDesc = desc;
            bufferDesc.colorFormat = RenderTextureFormat.ARGBFloat;
            bufferDesc.enableRandomWrite = true;

            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, name: "_RGB_SourceCopy");
            RenderingUtils.ReAllocateIfNeeded(ref intermediateBufferRGBHandle, bufferDesc, FilterMode.Point, name: "_RGB_Intermediate");
            RenderingUtils.ReAllocateIfNeeded(ref dctOutputRGBHandle, bufferDesc, FilterMode.Point, name: "_RGB_DCTOutput");
            RenderingUtils.ReAllocateIfNeeded(ref finalOutputHandle, bufferDesc, FilterMode.Point, name: "_RGB_FinalOutput");

            // Calculate blocks
            int numBlocksX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int numBlocksY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int totalBlocks = numBlocksX * numBlocksY;

            if (totalBlocks <= 0) return;

            // ✅ 변경 감지: 블록 수 또는 계수 변경
            bool blocksChanged = (totalBlocks != lastTotalBlocks);
            bool coefficientsChanged = (currentCoefficientsToUse != lastCoefficientsToUse);
            
            lastTotalBlocks = totalBlocks;
            lastCoefficientsToUse = currentCoefficientsToUse;

            // Prepare bitstream and pattern buffers - 블록 수, 계수, 또는 비트스트림 크기 변경 시 재생성
            if (finalBitsToEmbed.Count != totalBlocks || blocksChanged || coefficientsChanged)
            {
                PrepareBitstreamBuffer(totalBlocks);
                UpdateBitstreamBuffer(finalBitsToEmbed);
                UpdatePatternBuffer(totalBlocks); // ✅ 계수 변경 시에도 패턴 버퍼 재생성
            }

            // Set shader parameters
            int currentBitLength = finalBitsToEmbed.Count;
            bool patternValid = patternBuffer != null && 
                              patternBuffer.IsValid() && 
                              patternBuffer.count >= totalBlocks * currentCoefficientsToUse;
            bool shouldEmbedOnGPU = ShouldEmbedOnGPU(currentBitLength) && patternValid;

            try
            {
                computeShader.SetInt("Width", width);
                computeShader.SetInt("Height", height);
                computeShader.SetFloat("EmbeddingStrength", currentEmbeddingStrength);
                computeShader.SetInt("CoefficientsToUse", currentCoefficientsToUse);
                computeShader.SetInt("Embed", shouldEmbedOnGPU ? 1 : 0);
                computeShader.SetInt("BitLength", shouldEmbedOnGPU ? currentBitLength : 0);
                
                // ✅ 항상 유효한 버퍼 바인딩 (더미 버퍼 사용)
                computeShader.SetBuffer(kernelPass2, "Bitstream", GetValidBitstreamBuffer());
                computeShader.SetBuffer(kernelPass2, "PatternBuffer", GetValidPatternBuffer()); // ✅ 패턴 버퍼도 항상 바인딩
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{profilerTag}] 셰이더 파라미터 설정 실패: {ex.Message}");
            }

            // Bind textures
            try
            {
                if (kernelPass1 >= 0)
                {
                    computeShader.SetTexture(kernelPass1, "Source", sourceTextureHandle);
                    computeShader.SetTexture(kernelPass1, "IntermediateBufferRGB", intermediateBufferRGBHandle);
                }
                if (kernelPass2 >= 0)
                {
                    computeShader.SetTexture(kernelPass2, "IntermediateBufferRGB", intermediateBufferRGBHandle);
                    computeShader.SetTexture(kernelPass2, "DCTOutputRGB", dctOutputRGBHandle);
                }
                if (kernelPass3 >= 0)
                {
                    computeShader.SetTexture(kernelPass3, "DCTOutputRGB", dctOutputRGBHandle);
                    computeShader.SetTexture(kernelPass3, "IntermediateBufferRGB", intermediateBufferRGBHandle);
                }
                if (kernelPass4 >= 0)
                {
                    computeShader.SetTexture(kernelPass4, "IntermediateBufferRGB", intermediateBufferRGBHandle);
                    computeShader.SetTexture(kernelPass4, "FinalOutput", finalOutputHandle);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{profilerTag}] 텍스처 바인딩 실패: {ex.Message}");
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (kernelPass1 < 0 || kernelPass2 < 0 || kernelPass3 < 0 || kernelPass4 < 0 || computeShader == null)
                return;

            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
            
            if (cameraTarget.rt == null)
            {
                CommandBufferPool.Release(cmd);
                return;
            }

            int width = cameraTarget.rt.width;
            int height = cameraTarget.rt.height;
            
            if (width <= 0 || height <= 0)
            {
                CommandBufferPool.Release(cmd);
                return;
            }

            var (threadGroupsX, threadGroupsY) = CalculateThreadGroups(width, height);
            
            if (threadGroupsX <= 0 || threadGroupsY <= 0)
            {
                CommandBufferPool.Release(cmd);
                return;
            }

            cmd.Blit(cameraTarget, sourceTextureHandle);
            RTResultHolder.DedicatedSaveTargetBeforeEmbedding = sourceTextureHandle;

            using (new ProfilingScope(cmd, profilingSampler))
            {
                cmd.DispatchCompute(computeShader, kernelPass1, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, kernelPass2, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, kernelPass3, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, kernelPass4, threadGroupsX, threadGroupsY, 1);

                cmd.Blit(finalOutputHandle, cameraTarget);
                RTResultHolder.DedicatedSaveTarget = finalOutputHandle;
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) { }

        public override void Cleanup()
        {
            base.Cleanup();
            ReleasePatternBuffer();

            RTHandles.Release(sourceTextureHandle); sourceTextureHandle = null;
            RTHandles.Release(intermediateBufferRGBHandle); intermediateBufferRGBHandle = null;
            RTHandles.Release(dctOutputRGBHandle); dctOutputRGBHandle = null;
            RTHandles.Release(finalOutputHandle); finalOutputHandle = null;
            
            Debug.Log($"[{profilerTag}] Cleaned up RGB SS Render Pass resources.");
        }
        
        // ✅ 정적 리소스 정리
        public static void CleanupStaticResources()
        {
            dummyPatternBuffer?.Release();
            dummyPatternBuffer = null;
        }
    }
}