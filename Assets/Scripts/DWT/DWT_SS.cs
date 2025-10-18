// DWTRenderFeature.cs
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.Linq;
using System;

public class DWTRenderFeature_SS : WatermarkRenderFeatureBase
{
    [Header("셰이더 및 설정")]
    public ComputeShader dwtComputeShader;

    [Header("확산 스펙트럼 설정")]
    [Tooltip("확산 스펙트럼 임베딩 강도")]
    public float embeddingStrength = 0.05f;
    
    [Tooltip("Addressables에서 로드할 암호화된 데이터 키")]
    public string addressableKey = "OriginBlockData";
    
    [Tooltip("블록당 사용할 확산 스펙트럼 계수 개수 (예: HH 영역 내)")]
    [Range(1, 16)]
    public uint coefficientsToUse = 10;

    private DWTRenderPass dwtRenderPass;

    protected override ComputeShader GetComputeShader() => dwtComputeShader;
    protected override string GetFeatureName() => name;
    protected override WatermarkRenderPassBase GetRenderPass() => dwtRenderPass;

    protected override WatermarkRenderPassBase CreateRenderPass()
    {
        dwtRenderPass = new DWTRenderPass(
            dwtComputeShader, name, embedBitstream, 
            embeddingStrength, coefficientsToUse, addressableKey
        );
        return dwtRenderPass;
    }

    protected override void UpdateRenderPassParameters(WatermarkRenderPassBase pass)
    {
        if (pass is DWTRenderPass dwtPass)
        {
            dwtPass.SetParameters(embeddingStrength, coefficientsToUse);
            dwtPass.UpdatePatternBufferIfNeeded();
        }
    }

    // --- DWT Render Pass ---
    class DWTRenderPass : WatermarkRenderPassBase
    {
        private int dwtRowsKernelID, dwtColsKernelID, idwtColsKernelID, idwtRowsKernelID;
        private RTHandle sourceTextureHandle, intermediateHandle, dwtOutputHandle, idwtOutputHandle;

        private float currentEmbeddingStrength;
        private uint currentCoefficientsToUse;
        private string secretKey;

        private ComputeBuffer patternBuffer;
        private static ComputeBuffer dummyPatternBuffer; // ✅ 추가: 더미 패턴 버퍼
        private List<float> currentPatternData;

        private const int HALF_BLOCK_SIZE = BLOCK_SIZE / 2;
        private const int HH_COEFFS_PER_BLOCK = HALF_BLOCK_SIZE * HALF_BLOCK_SIZE; // 4x4 = 16

        private int lastWidth = 0;
        private int lastHeight = 0;
        private bool wasEmbedActiveLastFrame = false;
        private uint lastCoefficientsToUse = 0;

        public DWTRenderPass(
            ComputeShader shader, string tag, bool initialEmbedState, 
            float initialStrength, uint initialCoeffs, string secretKey)
            : base(shader, tag, initialEmbedState)
        {
            currentEmbeddingStrength = initialStrength;
            currentCoefficientsToUse = initialCoeffs;
            this.secretKey = secretKey;
            currentPatternData = new List<float>();
            wasEmbedActiveLastFrame = initialEmbedState;
            lastCoefficientsToUse = initialCoeffs;
            
            // ✅ 더미 패턴 버퍼 초기화
            EnsureDummyPatternBufferExists();

            dwtRowsKernelID = shader.FindKernel("DWT_Pass1_Rows");
            dwtColsKernelID = shader.FindKernel("DWT_Pass2_Cols_EmbedSS");
            idwtColsKernelID = shader.FindKernel("IDWT_Pass1_Cols");
            idwtRowsKernelID = shader.FindKernel("IDWT_Pass2_Rows");

            if (dwtRowsKernelID < 0 || dwtColsKernelID < 0 || idwtColsKernelID < 0 || idwtRowsKernelID < 0)
            {
                Debug.LogError($"[{profilerTag}] 하나 이상의 DWT Compute Shader 커널을 찾을 수 없습니다.");
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

        public void SetParameters(float strength, uint coeffs)
        {
            currentEmbeddingStrength = strength;
            currentCoefficientsToUse = Math.Min(coeffs, (uint)HH_COEFFS_PER_BLOCK);
        }

        public void UpdatePatternBufferIfNeeded()
        {
            if (!embedActive || currentCoefficientsToUse == 0)
            {
                ReleasePatternBuffer();
                return;
            }

            // Pattern buffer will be updated in OnCameraSetup with actual dimensions
        }

        private void UpdatePatternBufferWithSize(int totalBlocks)
        {
            if (!embedActive || currentCoefficientsToUse == 0)
            {
                ReleasePatternBuffer();
                return;
            }

            int requiredPatternSize = totalBlocks * (int)currentCoefficientsToUse;

            if (requiredPatternSize == 0)
            {
                ReleasePatternBuffer();
                return;
            }

            // ✅ 개선: 패턴 데이터가 없거나, 크기가 다르거나, 버퍼가 유효하지 않으면 재생성
            bool needsPatternDataRegeneration = (currentPatternData == null || currentPatternData.Count != requiredPatternSize);
            bool needsBufferRecreation = (patternBuffer == null || !patternBuffer.IsValid());

            // 패턴 데이터 재생성이 필요한 경우
            if (needsPatternDataRegeneration)
            {
                Debug.Log($"[{profilerTag}] 패턴 데이터 재생성: {requiredPatternSize}개 (블록: {totalBlocks}, 계수: {currentCoefficientsToUse})");
                GeneratePatternData(requiredPatternSize, secretKey);
            }

            // 버퍼 재생성이 필요한 경우 (패턴 데이터가 있어도 버퍼가 없으면 생성)
            if (needsBufferRecreation || needsPatternDataRegeneration)
            {
                UpdatePatternComputeBuffer();
            }
        }

        private void GeneratePatternData(int size, string secretKey)
        {
            currentPatternData = new List<float>(size);
            System.Random random = new System.Random(secretKey.GetHashCode());
            
            for (int i = 0; i < size; i++)
            {
                currentPatternData.Add((random.NextDouble() < 0.5) ? -1.0f : 1.0f);
            }
        }

        private void UpdatePatternComputeBuffer()
        {
            int count = currentPatternData?.Count ?? 0;
            
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
                    Debug.Log($"[{profilerTag}] 패턴 버퍼 생성: {count}개");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{profilerTag}] Pattern Buffer 생성 실패: {ex.Message}");
                    return;
                }
            }

            try
            {
                patternBuffer.SetData(currentPatternData);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{profilerTag}] Pattern Buffer SetData 실패: {ex.Message}");
                ReleasePatternBuffer();
            }
        }

        private void ReleasePatternBuffer()
        {
            patternBuffer?.Release();
            patternBuffer = null;
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

            int width = desc.width;
            int height = desc.height;
            int numBlocksX = Mathf.Max(1, (width + BLOCK_SIZE - 1) / BLOCK_SIZE);
            int numBlocksY = Mathf.Max(1, (height + BLOCK_SIZE - 1) / BLOCK_SIZE);
            int totalBlocks = numBlocksX * numBlocksY;

            // ✅ 변경 감지: embedActive 상태 및 계수 변경
            bool embedActiveStateChanged = (embedActive != wasEmbedActiveLastFrame);
            bool coefficientsChanged = (currentCoefficientsToUse != lastCoefficientsToUse);
            
            wasEmbedActiveLastFrame = embedActive;
            lastCoefficientsToUse = currentCoefficientsToUse;

            // Prepare bitstream - 해상도, embedActive 상태, 또는 계수 변경 시 재생성
            if (finalBitsToEmbed.Count != totalBlocks || 
                width != lastWidth || 
                height != lastHeight || 
                embedActiveStateChanged || 
                coefficientsChanged) // ✅ 계수 변경 시에도 재생성
            {
                PrepareBitstreamBuffer(totalBlocks);
                UpdateBitstreamBuffer(finalBitsToEmbed);
                UpdatePatternBufferWithSize(totalBlocks); // 패턴 버퍼 재생성
                
                lastWidth = width;
                lastHeight = height;
            }

            // Set shader parameters
            int currentBitLength = finalBitsToEmbed.Count;
            bool patternBufferValid = patternBuffer != null && patternBuffer.IsValid();
            bool shouldEmbed = ShouldEmbedOnGPU(currentBitLength) && 
                             patternBufferValid && 
                             currentCoefficientsToUse > 0;

            computeShader.SetInt("Width", width);
            computeShader.SetInt("Height", height);
            computeShader.SetFloat("EmbeddingStrength", currentEmbeddingStrength);
            computeShader.SetInt("CoefficientsToUse", (int)currentCoefficientsToUse);

            // Bind textures
            if (dwtRowsKernelID >= 0)
            {
                computeShader.SetTexture(dwtRowsKernelID, "Source", sourceTextureHandle);
                computeShader.SetTexture(dwtRowsKernelID, "IntermediateBuffer", intermediateHandle);
            }
            
            if (dwtColsKernelID >= 0)
            {
                computeShader.SetTexture(dwtColsKernelID, "IntermediateBuffer", intermediateHandle);
                computeShader.SetTexture(dwtColsKernelID, "DWTOutput", dwtOutputHandle);
                computeShader.SetInt("Embed", shouldEmbed ? 1 : 0);
                computeShader.SetInt("BitLength", shouldEmbed ? currentBitLength : 0);
                
                // ✅ 항상 유효한 버퍼 바인딩 (더미 버퍼 사용)
                computeShader.SetBuffer(dwtColsKernelID, "Bitstream", GetValidBitstreamBuffer());
                computeShader.SetBuffer(dwtColsKernelID, "PatternBuffer", GetValidPatternBuffer()); // ✅ 패턴 버퍼도 항상 바인딩
            }
            
            if (idwtColsKernelID >= 0)
            {
                computeShader.SetTexture(idwtColsKernelID, "DWTOutput", dwtOutputHandle);
                computeShader.SetTexture(idwtColsKernelID, "IntermediateBuffer", intermediateHandle);
            }
            
            if (idwtRowsKernelID >= 0)
            {
                computeShader.SetTexture(idwtRowsKernelID, "IntermediateBuffer", intermediateHandle);
                computeShader.SetTexture(idwtRowsKernelID, "IDWTOutput", idwtOutputHandle);
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            bool kernelsValid = dwtRowsKernelID >= 0 && dwtColsKernelID >= 0 && 
                              idwtColsKernelID >= 0 && idwtRowsKernelID >= 0;
            
            if (!kernelsValid) return;

            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;

            if (sourceTextureHandle == null || intermediateHandle == null || 
                dwtOutputHandle == null || idwtOutputHandle == null || cameraTarget == null)
            {
                Debug.LogError($"[{profilerTag}] 하나 이상의 RTHandle이 유효하지 않습니다.");
                CommandBufferPool.Release(cmd);
                return;
            }

            int width = cameraTarget.rt?.width ?? renderingData.cameraData.cameraTargetDescriptor.width;
            int height = cameraTarget.rt?.height ?? renderingData.cameraData.cameraTargetDescriptor.height;
            
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

            // Check if embedding should proceed - but don't skip rendering!
            bool shouldEmbed = embedActive && DataManager.IsDataReady && 
                             bitstreamBuffer != null && bitstreamBuffer.IsValid() && 
                             patternBuffer != null && patternBuffer.IsValid() && 
                             finalBitsToEmbed.Count > 0 && currentCoefficientsToUse > 0;

            // Log once if embedding is requested but conditions aren't met
            if (embedActive && !shouldEmbed && !DataManager.IsDataReady)
            {
                Debug.LogWarning($"[{profilerTag}] 데이터가 아직 로드되지 않아 워터마크 없이 렌더링합니다. GameInitializer가 씬에 있는지 확인하세요.");
            }

            cmd.Blit(cameraTarget, sourceTextureHandle);
            RTResultHolder.DedicatedSaveTargetBeforeEmbedding = sourceTextureHandle;

            using (new ProfilingScope(cmd, profilingSampler))
            {
                // Always execute DWT/IDWT transform, even if not embedding
                // The Embed flag in shader will control whether watermark is actually applied
                cmd.DispatchCompute(computeShader, dwtRowsKernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, dwtColsKernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, idwtColsKernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, idwtRowsKernelID, threadGroupsX, threadGroupsY, 1);

                cmd.Blit(idwtOutputHandle, cameraTarget);
                RTResultHolder.DedicatedSaveTarget = idwtOutputHandle;
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
            RTHandles.Release(intermediateHandle); intermediateHandle = null;
            RTHandles.Release(dwtOutputHandle); dwtOutputHandle = null;
            RTHandles.Release(idwtOutputHandle); idwtOutputHandle = null;
        }
        
        // ✅ 정적 리소스 정리
        public static void CleanupStaticResources()
        {
            dummyPatternBuffer?.Release();
            dummyPatternBuffer = null;
        }
    }
}