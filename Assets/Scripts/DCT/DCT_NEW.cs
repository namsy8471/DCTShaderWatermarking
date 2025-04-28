using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System;
using System.Linq;

public class DCT_RGB_SS_RenderFeature : ScriptableRendererFeature
{
    [Header("셰이더 및 설정")]
    public ComputeShader ssRgbDctComputeShader; // RGB 처리용 Compute Shader 할당 필요
    [Tooltip("Spread Spectrum 워터마크를 임베딩할지 여부")]
    public bool embedBitstream = true;

    [Header("확산 스펙트럼(Spread Spectrum) 설정")]
    [Tooltip("삽입 강도")]
    public float embeddingStrength = 0.1f; // 값 조절 필요 (RGB에 동시 적용되므로 주의)
    [Tooltip("패턴 생성을 위한 비밀 키")]
    public string secretKey = "default_secret_key_rgb_ss";
    [Tooltip("블록당 패턴을 적용할 AC 계수의 개수 (1~63)")]
    [Range(1, 63)]
    public int coefficientsToUse = 10; // RGB 동시 적용 시 개수 줄이는 것 고려

    private DCT_RGB_SS_RenderPass ssRgbDctRenderPass;

    // HLSL 파일에 정의된 커널 이름들
    private const string PASS1_KERNEL = "DCT_Pass1_Rows_RGB";
    private const string PASS2_KERNEL = "DCT_Pass2_Cols_EmbedSS_RGB";
    private const string PASS3_KERNEL = "IDCT_Pass1_Cols_RGB";
    private const string PASS4_KERNEL = "IDCT_Pass2_Rows_RGB";

    public override void Create()
    {
        if (ssRgbDctComputeShader == null)
        {
            Debug.LogError($"[{name}] Compute Shader가 할당되지 않았습니다.");
            return;
        }

        try
        {
            ssRgbDctRenderPass = new DCT_RGB_SS_RenderPass(
                ssRgbDctComputeShader, name, embedBitstream,
                embeddingStrength, secretKey, coefficientsToUse,
                PASS1_KERNEL, PASS2_KERNEL, PASS3_KERNEL, PASS4_KERNEL
            );
            ssRgbDctRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
            Debug.Log($"[{name}] DCT_RGB_SS_RenderPass 인스턴스 생성 완료.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{name}] DCT_RGB_SS_RenderPass 생성 실패: {ex.Message}\n{ex.StackTrace}");
            ssRgbDctRenderPass = null;
        }
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var camera = renderingData.cameraData.camera;
        if (camera.cameraType != CameraType.Game) return;

        if (ssRgbDctComputeShader != null && ssRgbDctRenderPass != null && DataManager.IsDataReady)
        {
            ssRgbDctRenderPass.SetEmbedActive(embedBitstream);
            ssRgbDctRenderPass.UpdateSSParams(embeddingStrength, secretKey, coefficientsToUse);
            renderer.EnqueuePass(ssRgbDctRenderPass);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ssRgbDctRenderPass?.Cleanup();
            ssRgbDctRenderPass = null;
        }
        base.Dispose(disposing);
    }

    // --- Render Pass 클래스 (RGB 처리용으로 수정) ---
    class DCT_RGB_SS_RenderPass : ScriptableRenderPass
    {
        private ComputeShader computeShader;
        private int kernelPass1, kernelPass2, kernelPass3, kernelPass4;

        // RT Handles (float3 사용으로 일부 변경)
        private RTHandle sourceTextureHandle;       // ARGBFloat (Source Copy)
        private RTHandle intermediateBufferRGBHandle; // ARGBFloat (Intermediate RGB')
        private RTHandle dctOutputRGBHandle;        // ARGBFloat (Final RGB DCT Coeffs)
        private RTHandle finalOutputHandle;         // ARGBFloat (Final RGB Output)

        // Compute Buffers
        private ComputeBuffer bitstreamBuffer;
        private ComputeBuffer patternBuffer;

        // CPU Data & State
        private List<uint> finalBitsToEmbed;
        private float currentEmbeddingStrength;
        private string currentSecretKey;
        private int currentCoefficientsToUse;
        private string lastUsedKeyForPattern = null;
        private int lastPatternBufferSize = 0;

        private string profilerTag;
        private bool embedActive;
        private const int BLOCK_SIZE = 8;

        public DCT_RGB_SS_RenderPass(ComputeShader shader, string tag, bool initialEmbedState,
                                 float strength, string key, int numCoeffs,
                                 string kernel1Name, string kernel2Name, string kernel3Name, string kernel4Name)
        {
            computeShader = shader;
            profilerTag = tag;
            this.profilingSampler = new ProfilingSampler(tag);
            embedActive = initialEmbedState;
            UpdateSSParams(strength, key, numCoeffs);

            try
            {
                kernelPass1 = shader.FindKernel(kernel1Name);
                kernelPass2 = shader.FindKernel(kernel2Name);
                kernelPass3 = shader.FindKernel(kernel3Name);
                kernelPass4 = shader.FindKernel(kernel4Name);
                if (kernelPass1 < 0 || kernelPass2 < 0 || kernelPass3 < 0 || kernelPass4 < 0)
                {
                    throw new Exception($"하나 이상의 필수 커널을 찾을 수 없습니다. ({kernel1Name}, {kernel2Name}, {kernel3Name}, {kernel4Name})");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{profilerTag}] Compute Shader 커널 초기화 실패: {ex.Message}");
                kernelPass1 = kernelPass2 = kernelPass3 = kernelPass4 = -1; throw;
            }
            finalBitsToEmbed = new List<uint>();
        }

        public void SetEmbedActive(bool isActive) { embedActive = isActive; }
        public void UpdateSSParams(float strength, string key, int numCoeffs)
        {
            this.currentEmbeddingStrength = strength;
            this.currentSecretKey = key;
            this.currentCoefficientsToUse = Mathf.Clamp(numCoeffs, 1, 63);
        }

        // --- Buffer 관리 (이전 SS 버전과 유사) ---
        private void UpdateBitstreamBuffer(List<uint> data)
        { 
            int count = (data != null) ? data.Count : 0;
            if (count == 0) { ReleaseBitstreamBuffer(); return; }
            if (bitstreamBuffer == null || !bitstreamBuffer.IsValid() || bitstreamBuffer.count != count)
            {
                ReleaseBitstreamBuffer();
                try { bitstreamBuffer = new ComputeBuffer(count, sizeof(uint), ComputeBufferType.Structured, ComputeBufferMode.Immutable); }
                catch (Exception ex) { Debug.LogError($"[{profilerTag}] Bitstream CB Create Failed (Size:{count}): {ex.Message}"); bitstreamBuffer = null; return; }
            }
            if (bitstreamBuffer != null && bitstreamBuffer.IsValid())
            {
                try { bitstreamBuffer.SetData(data); }
                catch (Exception ex) { Debug.LogError($"[{profilerTag}] Bitstream CB SetData Failed (Size:{count}): {ex.Message}"); ReleaseBitstreamBuffer(); }
            }
        }
        private void ReleaseBitstreamBuffer() { bitstreamBuffer?.Release(); bitstreamBuffer = null; }

        private void UpdatePatternBuffer(int numBlocks)
        {
            if (numBlocks <= 0 || currentCoefficientsToUse <= 0) { ReleasePatternBuffer(); return; }
            int requiredSize = numBlocks * currentCoefficientsToUse;
            bool needsUpdate = patternBuffer == null || !patternBuffer.IsValid() ||
                               lastPatternBufferSize != requiredSize || lastUsedKeyForPattern != currentSecretKey;
            if (needsUpdate)
            {
                ReleasePatternBuffer();
                float[] patternData = new float[requiredSize];
                System.Random prng = new System.Random(currentSecretKey.GetHashCode());
                for (int i = 0; i < requiredSize; ++i) { patternData[i] = (prng.NextDouble() < 0.5) ? -1.0f : 1.0f; }
                try
                {
                    patternBuffer = new ComputeBuffer(requiredSize, sizeof(float), ComputeBufferType.Structured, ComputeBufferMode.Immutable);
                    patternBuffer.SetData(patternData);
                    lastUsedKeyForPattern = currentSecretKey;
                    lastPatternBufferSize = requiredSize;
                }
                catch (Exception ex) { Debug.LogError($"[{profilerTag}] Pattern CB Create/Set Failed (Size:{requiredSize}): {ex.Message}"); ReleasePatternBuffer(); }
            }
        }
        private void ReleasePatternBuffer()
        {
            patternBuffer?.Release(); patternBuffer = null;
            lastUsedKeyForPattern = null; lastPatternBufferSize = 0;
        }

        // --- Render Pass 설정 및 실행 ---
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            if (kernelPass1 < 0 || kernelPass2 < 0 || kernelPass3 < 0 || kernelPass4 < 0 || computeShader == null) return;

            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;

            int width = desc.width;
            int height = desc.height;

            if (width <= 0 || height <= 0) return;

            // RT Handles 할당 (RGB 처리용 - ARGBFloat 사용)
            var bufferDesc = desc;
            bufferDesc.colorFormat = RenderTextureFormat.ARGBFloat;
            bufferDesc.enableRandomWrite = true;

            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, name: "_RGB_SourceCopy");
            RenderingUtils.ReAllocateIfNeeded(ref intermediateBufferRGBHandle, bufferDesc, FilterMode.Point, name: "_RGB_Intermediate");
            RenderingUtils.ReAllocateIfNeeded(ref dctOutputRGBHandle, bufferDesc, FilterMode.Point, name: "_RGB_DCTOutput");
            RenderingUtils.ReAllocateIfNeeded(ref finalOutputHandle, bufferDesc, FilterMode.Point, name: "_RGB_FinalOutput");



            // 블록 수 계산
            int numBlocksX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int numBlocksY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int totalBlocks = numBlocksX * numBlocksY;
            if (totalBlocks <= 0) return;

            if (finalBitsToEmbed.Count != totalBlocks)
            {
                // 비트스트림 준비
                finalBitsToEmbed = finalBitsToEmbed ?? new List<uint>(); finalBitsToEmbed.Clear();
                if (DataManager.IsDataReady && DataManager.EncryptedOriginData != null)
                {
                    try
                    {
                        List<uint> currentPayload = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);
                        if (currentPayload != null && currentPayload.Count > 0)
                        {
                            int requiredBits = totalBlocks; // 전체 8x8 블록 수 (Width * Height / 64)

                            // Payload 반복해서 필요한 만큼 채우기
                            int loops = Mathf.CeilToInt((float)requiredBits / currentPayload.Count);
                            for (int i = 0; i < loops; ++i)
                            {
                                finalBitsToEmbed.AddRange(currentPayload);
                                if (finalBitsToEmbed.Count >= requiredBits) break;
                            }

                            // 정확히 맞춰 자르기
                            if (finalBitsToEmbed.Count > requiredBits)
                                finalBitsToEmbed = finalBitsToEmbed.Take(requiredBits).ToList();
                        }
                    }
                    catch (Exception ex) { Debug.LogError($"[{profilerTag}] RGB Payload Prep Error: {ex.Message}"); finalBitsToEmbed.Clear(); }
                }

                // 버퍼 업데이트
                UpdateBitstreamBuffer(finalBitsToEmbed);
                UpdatePatternBuffer(totalBlocks);
            }

            // 셰이더 파라미터 설정
            int currentBitLength = finalBitsToEmbed.Count;
            bool bitstreamValid = bitstreamBuffer != null && bitstreamBuffer.IsValid() && bitstreamBuffer.count >= currentBitLength;
            bool patternValid = patternBuffer != null && patternBuffer.IsValid() && patternBuffer.count >= totalBlocks * currentCoefficientsToUse;
            bool shouldEmbedOnGPU = embedActive && DataManager.IsDataReady && bitstreamValid && patternValid && currentBitLength > 0;

            try
            {
                computeShader.SetInt("Width", width);
                computeShader.SetInt("Height", height);
                computeShader.SetFloat("EmbeddingStrength", currentEmbeddingStrength);
                computeShader.SetInt("CoefficientsToUse", currentCoefficientsToUse);
                computeShader.SetInt("Embed", shouldEmbedOnGPU ? 1 : 0);

                computeShader.SetInt("BitLength", currentBitLength);
                computeShader.SetBuffer(kernelPass2, "Bitstream", bitstreamBuffer);
                computeShader.SetBuffer(kernelPass2, "PatternBuffer", patternBuffer);
                
            }
            catch (Exception ex) { Debug.LogError($"[{profilerTag}] RGB Set Params Failed: {ex.Message}"); }

            // 텍스처 바인딩
            try
            {
                // Pass 1: Source -> IntermediateBufferRGB
                if (kernelPass1 >= 0) { 
                    computeShader.SetTexture(kernelPass1, "Source", sourceTextureHandle); 
                    computeShader.SetTexture(kernelPass1, "IntermediateBufferRGB", intermediateBufferRGBHandle); }
                // Pass 2: IntermediateBufferRGB -> DCTOutputRGB
                if (kernelPass2 >= 0) { 
                    computeShader.SetTexture(kernelPass2, "IntermediateBufferRGB", intermediateBufferRGBHandle); 
                    computeShader.SetTexture(kernelPass2, "DCTOutputRGB", dctOutputRGBHandle); }
                // Pass 3: DCTOutputRGB -> IntermediateBufferRGB
                if (kernelPass3 >= 0) { 
                    computeShader.SetTexture(kernelPass3, "DCTOutputRGB", dctOutputRGBHandle); 
                    computeShader.SetTexture(kernelPass3, "IntermediateBufferRGB", intermediateBufferRGBHandle); }
                // Pass 4: IntermediateBufferRGB -> FinalOutput
                if (kernelPass4 >= 0) { 
                    computeShader.SetTexture(kernelPass4, "IntermediateBufferRGB", intermediateBufferRGBHandle); 
                    computeShader.SetTexture(kernelPass4, "FinalOutput", finalOutputHandle); }
            }
            catch (Exception ex) { Debug.LogError($"[{profilerTag}] RGB Texture Bind Failed: {ex.Message}"); }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (kernelPass1 < 0 || kernelPass2 < 0 || kernelPass3 < 0 || kernelPass4 < 0 || computeShader == null) return;

            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
            if (cameraTarget.rt == null) { CommandBufferPool.Release(cmd); return; }

            int width = cameraTarget.rt.width; int height = cameraTarget.rt.height;
            if (width <= 0 || height <= 0) { CommandBufferPool.Release(cmd); return; }

            int threadGroupsX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int threadGroupsY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
            if (threadGroupsX <= 0 || threadGroupsY <= 0) { CommandBufferPool.Release(cmd); return; }

            cmd.Blit(cameraTarget, sourceTextureHandle); // 원본 복사
            RTResultHolder.DedicatedSaveTargetBeforeEmbedding = sourceTextureHandle; // 원본 복사본 저장;

            using (new ProfilingScope(cmd, profilingSampler))
            {
                cmd.DispatchCompute(computeShader, kernelPass1, threadGroupsX, threadGroupsY, 1);
                // if (Input.GetKey(KeyCode.Keypad1)) cmd.Blit(intermediateBufferRGBHandle, cameraTarget); // Debug Pass 1 RGB'

                cmd.DispatchCompute(computeShader, kernelPass2, threadGroupsX, threadGroupsY, 1);
                // if (Input.GetKey(KeyCode.Keypad2)) cmd.Blit(dctOutputRGBHandle, cameraTarget); // Debug Pass 2 RGB DCT Coeffs

                cmd.DispatchCompute(computeShader, kernelPass3, threadGroupsX, threadGroupsY, 1);
                // if (Input.GetKey(KeyCode.Keypad3)) cmd.Blit(intermediateBufferRGBHandle, cameraTarget); // Debug Pass 3 RGB'

                cmd.DispatchCompute(computeShader, kernelPass4, threadGroupsX, threadGroupsY, 1);

                cmd.Blit(finalOutputHandle, cameraTarget); // 최종 결과 출력
                //if (Input.GetKey(KeyCode.Keypad4)) cmd.Blit(finalOutputHandle, cameraTarget);
                RTResultHolder.DedicatedSaveTarget = finalOutputHandle;
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) { }

        public void Cleanup()
        {
            ReleaseBitstreamBuffer();
            ReleasePatternBuffer();

            RTHandles.Release(sourceTextureHandle); sourceTextureHandle = null;
            RTHandles.Release(intermediateBufferRGBHandle); intermediateBufferRGBHandle = null;
            RTHandles.Release(dctOutputRGBHandle); dctOutputRGBHandle = null;
            RTHandles.Release(finalOutputHandle); finalOutputHandle = null;
            Debug.Log($"[{profilerTag}] Cleaned up RGB SS Render Pass resources.");
        }
    }
}