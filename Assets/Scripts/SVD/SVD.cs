using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System;
using Unity.Collections;
using System.IO;
using System.Linq;

// 필요한 헬퍼 클래스 (DataManager, SaveTrigger, RTResultHolder 등) 정의 가정

public class TruncatedSVDRenderFeature : ScriptableRendererFeature
{
    [Header("셰이더 및 설정")]
    public ComputeShader truncatedSvdComputeShader; // Truncated SVD용 .compute 파일 할당
    [Tooltip("A^T A 고유값 분해 시 Jacobi 반복 횟수")]
    public int jacobiIterations = 10; // 반복 횟수
    [Tooltip("특이값 수정/임베딩 활성화 여부")]
    public bool modifySingularValues = false;
    [Tooltip("수정 시 사용할 임계값 또는 임베딩 강도")]
    public float modificationValue = 0.01f;
    [Tooltip("U 계산 시 0으로 나누기 방지용 작은 값 (sigma 임계값)")]
    public float sigmaThreshold = 1e-6f;

    private TruncatedSVDRenderPass truncatedSvdRenderPass;

    public override void Create()
    {
        if (truncatedSvdComputeShader == null)
        {
            Debug.LogError("Truncated SVD Compute Shader가 할당되지 않았습니다.");
            return;
        }

        truncatedSvdRenderPass = new TruncatedSVDRenderPass(truncatedSvdComputeShader, name, jacobiIterations, modifySingularValues, modificationValue, sigmaThreshold);
        truncatedSvdRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var camera = renderingData.cameraData.camera;
        if (camera.cameraType != CameraType.Game) { return; }

        if (truncatedSvdComputeShader != null && truncatedSvdRenderPass != null)
        {
            truncatedSvdRenderPass.UpdateSettings(jacobiIterations, modifySingularValues, modificationValue, sigmaThreshold);
            // 필요시 데이터 준비 상태 확인
            renderer.EnqueuePass(truncatedSvdRenderPass);
        }
    }

    protected override void Dispose(bool disposing)
    {
        truncatedSvdRenderPass?.Cleanup();
    }

    //-------------------------------------------------------------------------
    // Truncated SVD Render Pass
    //-------------------------------------------------------------------------
    class TruncatedSVDRenderPass : ScriptableRenderPass
    {
        private readonly ComputeShader computeShader;
        private readonly string profilerTag;
        private int jacobiIterations;
        private bool modifySigmaActive;
        private float sigmaModificationValue;
        private float sigmaThreshold; // For U calculation stability

        // 커널 ID
        private int rgbToYKernelID, storeCbCrKernelID, computeAtAKernelID, eigenDecompositionKernelID, computeUKernelID, modifySigmaKernelID, reconstructYKernelID, combineYCbCrKernelID;

        // RTHandles
        private RTHandle sourceTextureHandle;   // 원본 복사
        private RTHandle sourceYHandle;         // Y 채널
        private RTHandle chromaBufferHandle;    // CbCr 채널
        private RTHandle AtAHandle;             // A^T A 행렬 (블록별 8x8)
        // private RTHandle eigenvaluesHandle;     // 고유값 Lambda (이제 필요 없음, 바로 sigma 계산)
        private RTHandle matrixVHandle;         // V 행렬 (블록별 8x8)
        private RTHandle singularValuesHandle;  // Sigma 특이값 (블록별 8개)
        private RTHandle matrixUHandle;         // U 행렬 (블록별 8x8)
        private RTHandle reconstructedYHandle;  // 재구성된 Y 채널
        private RTHandle finalOutputHandle;     // 최종 RGB 결과

        private const int BLOCK_SIZE = 8;

        public TruncatedSVDRenderPass(ComputeShader shader, string tag, int iterations, bool modifyActive, float modValue, float sigmaThresh)
        {
            computeShader = shader;
            profilerTag = tag;
            UpdateSettings(iterations, modifyActive, modValue, sigmaThresh);

            // 커널 찾기
            rgbToYKernelID = shader.FindKernel("ConvertRGBToY");
            storeCbCrKernelID = shader.FindKernel("StoreCbCr");
            computeAtAKernelID = shader.FindKernel("ComputeAtA_8x8");
            eigenDecompositionKernelID = shader.FindKernel("Eigendecomposition_AtA_8x8"); // V, Sigma 출력
            computeUKernelID = shader.FindKernel("ComputeU_8x8");
            modifySigmaKernelID = shader.FindKernel("ModifySigma");
            reconstructYKernelID = shader.FindKernel("ReconstructY_FromSVD");
            combineYCbCrKernelID = shader.FindKernel("CombineYAndCbCr_ToRGB");

            // 커널 유효성 검사 (ModifySigma 제외)
            bool kernelsValid = rgbToYKernelID >= 0 && storeCbCrKernelID >= 0 && computeAtAKernelID >= 0 &&
                                eigenDecompositionKernelID >= 0 && computeUKernelID >= 0 &&
                                reconstructYKernelID >= 0 && combineYCbCrKernelID >= 0;
            if (!kernelsValid) { Debug.LogError($"[TruncatedSVDRenderPass] 하나 이상의 필수 Compute Shader 커널을 찾을 수 없습니다."); }
            if (modifySigmaActive && modifySigmaKernelID < 0) { Debug.LogWarning("[TruncatedSVDRenderPass] ModifySigma 커널을 찾을 수 없지만 활성화됨."); }
        }

        public void UpdateSettings(int iterations, bool modifyActive, float modValue, float sigmaThresh)
        {
            jacobiIterations = Mathf.Max(1, iterations);
            modifySigmaActive = modifyActive;
            sigmaModificationValue = modValue;
            sigmaThreshold = Mathf.Max(1e-9f, sigmaThresh); // 매우 작은 양수 보장
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0; desc.msaaSamples = 1; desc.sRGB = false;

            // 디스크립터 설정
            var yDesc = desc; yDesc.colorFormat = RenderTextureFormat.RFloat; yDesc.enableRandomWrite = true;
            var chromaDesc = desc; chromaDesc.colorFormat = RenderTextureFormat.RGFloat; chromaDesc.enableRandomWrite = true;
            // A^T A (8x8), U(8x8), V(8x8) 저장용: RFloat 텍스처 사용 (각 블록이 8x8 픽셀 영역 차지)
            var matrixDesc = desc; matrixDesc.colorFormat = RenderTextureFormat.RFloat; matrixDesc.enableRandomWrite = true;
            // Sigma (8개) 저장용: RFloat 텍스처 사용 (각 블록이 8x1 픽셀 영역 차지)
            var sigmaDesc = desc; sigmaDesc.colorFormat = RenderTextureFormat.RFloat; sigmaDesc.enableRandomWrite = true;
            var outputDesc = desc; outputDesc.enableRandomWrite = true; outputDesc.colorFormat = RenderTextureFormat.ARGBFloat; // HDR 결과 고려

            // RTHandle 할당
            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, name: "_SourceCopyForTSVD");
            RenderingUtils.ReAllocateIfNeeded(ref sourceYHandle, yDesc, FilterMode.Point, name: "_SourceY_TSVD");
            RenderingUtils.ReAllocateIfNeeded(ref chromaBufferHandle, chromaDesc, FilterMode.Point, name: "_ChromaBufferCbCr_TSVD");
            RenderingUtils.ReAllocateIfNeeded(ref AtAHandle, matrixDesc, FilterMode.Point, name: "_AtA_TSVD"); // 8x8 행렬 저장
            RenderingUtils.ReAllocateIfNeeded(ref matrixVHandle, matrixDesc, FilterMode.Point, name: "_MatrixV_TSVD"); // 8x8 행렬 저장
            RenderingUtils.ReAllocateIfNeeded(ref singularValuesHandle, sigmaDesc, FilterMode.Point, name: "_SingularValues_TSVD"); // 8개 값 저장
            RenderingUtils.ReAllocateIfNeeded(ref matrixUHandle, matrixDesc, FilterMode.Point, name: "_MatrixU_TSVD"); // 8x8 행렬 저장
            RenderingUtils.ReAllocateIfNeeded(ref reconstructedYHandle, yDesc, FilterMode.Point, name: "_ReconstructedY_TSVD");
            RenderingUtils.ReAllocateIfNeeded(ref finalOutputHandle, outputDesc, FilterMode.Point, name: "_FinalOutput_TSVD");

            // --- 셰이더 파라미터 설정 ---
            cmd.SetComputeIntParam(computeShader, "Width", desc.width);
            cmd.SetComputeIntParam(computeShader, "Height", desc.height);
            cmd.SetComputeIntParam(computeShader, "BLOCK_SIZE", BLOCK_SIZE);

            // RGB -> Y / Store CbCr
            cmd.SetComputeTextureParam(computeShader, rgbToYKernelID, "SourceReader", sourceTextureHandle);
            cmd.SetComputeTextureParam(computeShader, rgbToYKernelID, "SourceYWriter", sourceYHandle);
            cmd.SetComputeTextureParam(computeShader, storeCbCrKernelID, "SourceReader", sourceTextureHandle);
            cmd.SetComputeTextureParam(computeShader, storeCbCrKernelID, "ChromaBufferWriter", chromaBufferHandle);

            // Compute AtA
            cmd.SetComputeTextureParam(computeShader, computeAtAKernelID, "SourceYReader_AtA", sourceYHandle);
            cmd.SetComputeTextureParam(computeShader, computeAtAKernelID, "AtAWriter", AtAHandle);

            // Eigendecomposition (AtA -> V, Sigma)
            cmd.SetComputeIntParam(computeShader, "JacobiIterations", jacobiIterations);
            cmd.SetComputeTextureParam(computeShader, eigenDecompositionKernelID, "AtAReader", AtAHandle);
            cmd.SetComputeTextureParam(computeShader, eigenDecompositionKernelID, "MatrixVWriter", matrixVHandle);
            cmd.SetComputeTextureParam(computeShader, eigenDecompositionKernelID, "SingularValuesWriter", singularValuesHandle);

            // Compute U (Y, V, Sigma -> U)
            cmd.SetComputeFloatParam(computeShader, "SigmaThreshold", sigmaThreshold);
            cmd.SetComputeTextureParam(computeShader, computeUKernelID, "SourceYReader_U", sourceYHandle);
            cmd.SetComputeTextureParam(computeShader, computeUKernelID, "MatrixVReader_U", matrixVHandle);
            cmd.SetComputeTextureParam(computeShader, computeUKernelID, "SingularValuesReader_U", singularValuesHandle);
            cmd.SetComputeTextureParam(computeShader, computeUKernelID, "MatrixUWriter", matrixUHandle);

            
            cmd.SetComputeFloatParam(computeShader, "ModificationValue", sigmaModificationValue);
            cmd.SetComputeTextureParam(computeShader, modifySigmaKernelID, "SingularValues", singularValuesHandle);
            

            // Reconstruct Y (U, Sigma, V -> Y)
            cmd.SetComputeTextureParam(computeShader, reconstructYKernelID, "MatrixUReader", matrixUHandle);
            cmd.SetComputeTextureParam(computeShader, reconstructYKernelID, "SingularValuesReader", singularValuesHandle);
            cmd.SetComputeTextureParam(computeShader, reconstructYKernelID, "MatrixVReader", matrixVHandle);
            cmd.SetComputeTextureParam(computeShader, reconstructYKernelID, "ReconstructedYWriter", reconstructedYHandle);

            // Combine (Y, CbCr -> RGB)
            cmd.SetComputeTextureParam(computeShader, combineYCbCrKernelID, "ReconstructedYReader", reconstructedYHandle);
            cmd.SetComputeTextureParam(computeShader, combineYCbCrKernelID, "ChromaBufferReader", chromaBufferHandle);
            cmd.SetComputeTextureParam(computeShader, combineYCbCrKernelID, "FinalRGBWriter", finalOutputHandle);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // 커널 유효성 검사 등 (생략) ...
            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
            int width = cameraTarget.rt?.width ?? renderingData.cameraData.cameraTargetDescriptor.width;
            int height = cameraTarget.rt?.height ?? renderingData.cameraData.cameraTargetDescriptor.height;
            if (width <= 0 || height <= 0) { CommandBufferPool.Release(cmd); return; }
            int threadGroupsX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int threadGroupsY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
            if (threadGroupsX <= 0 || threadGroupsY <= 0) { CommandBufferPool.Release(cmd); return; }

            using (new ProfilingScope(cmd, new ProfilingSampler(profilerTag)))
            {
                // 1. 원본 복사
                cmd.Blit(cameraTarget, sourceTextureHandle);

                // 2. Y, CbCr 분리
                cmd.DispatchCompute(computeShader, rgbToYKernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, storeCbCrKernelID, threadGroupsX, threadGroupsY, 1);

                // 3. A^T A 계산
                cmd.DispatchCompute(computeShader, computeAtAKernelID, threadGroupsX, threadGroupsY, 1);

                // 4. A^T A 고유값 분해 -> V, Sigma 계산
                cmd.DispatchCompute(computeShader, eigenDecompositionKernelID, threadGroupsX, threadGroupsY, 1);

                // 5. U 계산 (u_i = A*v_i / sigma_i)
                cmd.DispatchCompute(computeShader, computeUKernelID, threadGroupsX, threadGroupsY, 1);

                // 6. (선택적) Sigma 수정
                if (modifySigmaActive && modifySigmaKernelID >= 0)
                {
                    cmd.DispatchCompute(computeShader, modifySigmaKernelID, threadGroupsX, threadGroupsY, 1);
                }

                // 7. Y 재구성 (Y = U * Sigma * V^T)
                cmd.DispatchCompute(computeShader, reconstructYKernelID, threadGroupsX, threadGroupsY, 1);

                // 8. 최종 RGB 결합
                cmd.DispatchCompute(computeShader, combineYCbCrKernelID, threadGroupsX, threadGroupsY, 1);

                // 9. 결과 블릿
                cmd.Blit(finalOutputHandle, cameraTarget);

                // 결과 저장용 타겟 설정
                RTResultHolder.DedicatedSaveTarget = cameraTarget;
            }

            // 결과 저장 로직 (기존과 유사)
            if (SaveTrigger.SaveRequested /* && !isReadbackPending */)
            {
                // ... AsyncGPUReadback.Request(..., OnCompleteReadback_Static_TSVD); ...
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        // Static 콜백 함수 (이름 변경하여 구분)
        static void OnCompleteReadback_Static_TSVD(AsyncGPUReadbackRequest request)
        {
            // 기존 콜백 함수 내용과 거의 동일하게 사용 (파일명 등만 수정)
            // ... (EXR 저장 시 파일명에 "_TSVD" 추가 등) ...
            Debug.Log("Truncated SVD Static Async GPU Readback 완료.");
            // ... isReadbackPending = false 처리 필요 ...
        }

        public override void OnCameraCleanup(CommandBuffer cmd) { }

        public void Cleanup()
        {
            // 모든 RTHandle 해제
            RTHandles.Release(sourceTextureHandle); sourceTextureHandle = null;
            RTHandles.Release(sourceYHandle); sourceYHandle = null;
            RTHandles.Release(chromaBufferHandle); chromaBufferHandle = null;
            RTHandles.Release(AtAHandle); AtAHandle = null;
            RTHandles.Release(matrixVHandle); matrixVHandle = null;
            RTHandles.Release(singularValuesHandle); singularValuesHandle = null;
            RTHandles.Release(matrixUHandle); matrixUHandle = null;
            RTHandles.Release(reconstructedYHandle); reconstructedYHandle = null;
            RTHandles.Release(finalOutputHandle); finalOutputHandle = null;
        }
    }
}