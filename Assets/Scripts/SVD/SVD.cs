using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System;
using Unity.Collections;
using System.IO;
using System.Linq;

// �ʿ��� ���� Ŭ���� (DataManager, SaveTrigger, RTResultHolder ��) ���� ����

public class TruncatedSVDRenderFeature : ScriptableRendererFeature
{
    [Header("���̴� �� ����")]
    public ComputeShader truncatedSvdComputeShader; // Truncated SVD�� .compute ���� �Ҵ�
    [Tooltip("A^T A ������ ���� �� Jacobi �ݺ� Ƚ��")]
    public int jacobiIterations = 10; // �ݺ� Ƚ��
    [Tooltip("Ư�̰� ����/�Ӻ��� Ȱ��ȭ ����")]
    public bool modifySingularValues = false;
    [Tooltip("���� �� ����� �Ӱ谪 �Ǵ� �Ӻ��� ����")]
    public float modificationValue = 0.01f;
    [Tooltip("U ��� �� 0���� ������ ������ ���� �� (sigma �Ӱ谪)")]
    public float sigmaThreshold = 1e-6f;

    private TruncatedSVDRenderPass truncatedSvdRenderPass;

    public override void Create()
    {
        if (truncatedSvdComputeShader == null)
        {
            Debug.LogError("Truncated SVD Compute Shader�� �Ҵ���� �ʾҽ��ϴ�.");
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
            // �ʿ�� ������ �غ� ���� Ȯ��
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

        // Ŀ�� ID
        private int rgbToYKernelID, storeCbCrKernelID, computeAtAKernelID, eigenDecompositionKernelID, computeUKernelID, modifySigmaKernelID, reconstructYKernelID, combineYCbCrKernelID;

        // RTHandles
        private RTHandle sourceTextureHandle;   // ���� ����
        private RTHandle sourceYHandle;         // Y ä��
        private RTHandle chromaBufferHandle;    // CbCr ä��
        private RTHandle AtAHandle;             // A^T A ��� (��Ϻ� 8x8)
        // private RTHandle eigenvaluesHandle;     // ������ Lambda (���� �ʿ� ����, �ٷ� sigma ���)
        private RTHandle matrixVHandle;         // V ��� (��Ϻ� 8x8)
        private RTHandle singularValuesHandle;  // Sigma Ư�̰� (��Ϻ� 8��)
        private RTHandle matrixUHandle;         // U ��� (��Ϻ� 8x8)
        private RTHandle reconstructedYHandle;  // �籸���� Y ä��
        private RTHandle finalOutputHandle;     // ���� RGB ���

        private const int BLOCK_SIZE = 8;

        public TruncatedSVDRenderPass(ComputeShader shader, string tag, int iterations, bool modifyActive, float modValue, float sigmaThresh)
        {
            computeShader = shader;
            profilerTag = tag;
            UpdateSettings(iterations, modifyActive, modValue, sigmaThresh);

            // Ŀ�� ã��
            rgbToYKernelID = shader.FindKernel("ConvertRGBToY");
            storeCbCrKernelID = shader.FindKernel("StoreCbCr");
            computeAtAKernelID = shader.FindKernel("ComputeAtA_8x8");
            eigenDecompositionKernelID = shader.FindKernel("Eigendecomposition_AtA_8x8"); // V, Sigma ���
            computeUKernelID = shader.FindKernel("ComputeU_8x8");
            modifySigmaKernelID = shader.FindKernel("ModifySigma");
            reconstructYKernelID = shader.FindKernel("ReconstructY_FromSVD");
            combineYCbCrKernelID = shader.FindKernel("CombineYAndCbCr_ToRGB");

            // Ŀ�� ��ȿ�� �˻� (ModifySigma ����)
            bool kernelsValid = rgbToYKernelID >= 0 && storeCbCrKernelID >= 0 && computeAtAKernelID >= 0 &&
                                eigenDecompositionKernelID >= 0 && computeUKernelID >= 0 &&
                                reconstructYKernelID >= 0 && combineYCbCrKernelID >= 0;
            if (!kernelsValid) { Debug.LogError($"[TruncatedSVDRenderPass] �ϳ� �̻��� �ʼ� Compute Shader Ŀ���� ã�� �� �����ϴ�."); }
            if (modifySigmaActive && modifySigmaKernelID < 0) { Debug.LogWarning("[TruncatedSVDRenderPass] ModifySigma Ŀ���� ã�� �� ������ Ȱ��ȭ��."); }
        }

        public void UpdateSettings(int iterations, bool modifyActive, float modValue, float sigmaThresh)
        {
            jacobiIterations = Mathf.Max(1, iterations);
            modifySigmaActive = modifyActive;
            sigmaModificationValue = modValue;
            sigmaThreshold = Mathf.Max(1e-9f, sigmaThresh); // �ſ� ���� ��� ����
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0; desc.msaaSamples = 1; desc.sRGB = false;

            // ��ũ���� ����
            var yDesc = desc; yDesc.colorFormat = RenderTextureFormat.RFloat; yDesc.enableRandomWrite = true;
            var chromaDesc = desc; chromaDesc.colorFormat = RenderTextureFormat.RGFloat; chromaDesc.enableRandomWrite = true;
            // A^T A (8x8), U(8x8), V(8x8) �����: RFloat �ؽ�ó ��� (�� ����� 8x8 �ȼ� ���� ����)
            var matrixDesc = desc; matrixDesc.colorFormat = RenderTextureFormat.RFloat; matrixDesc.enableRandomWrite = true;
            // Sigma (8��) �����: RFloat �ؽ�ó ��� (�� ����� 8x1 �ȼ� ���� ����)
            var sigmaDesc = desc; sigmaDesc.colorFormat = RenderTextureFormat.RFloat; sigmaDesc.enableRandomWrite = true;
            var outputDesc = desc; outputDesc.enableRandomWrite = true; outputDesc.colorFormat = RenderTextureFormat.ARGBFloat; // HDR ��� ���

            // RTHandle �Ҵ�
            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, name: "_SourceCopyForTSVD");
            RenderingUtils.ReAllocateIfNeeded(ref sourceYHandle, yDesc, FilterMode.Point, name: "_SourceY_TSVD");
            RenderingUtils.ReAllocateIfNeeded(ref chromaBufferHandle, chromaDesc, FilterMode.Point, name: "_ChromaBufferCbCr_TSVD");
            RenderingUtils.ReAllocateIfNeeded(ref AtAHandle, matrixDesc, FilterMode.Point, name: "_AtA_TSVD"); // 8x8 ��� ����
            RenderingUtils.ReAllocateIfNeeded(ref matrixVHandle, matrixDesc, FilterMode.Point, name: "_MatrixV_TSVD"); // 8x8 ��� ����
            RenderingUtils.ReAllocateIfNeeded(ref singularValuesHandle, sigmaDesc, FilterMode.Point, name: "_SingularValues_TSVD"); // 8�� �� ����
            RenderingUtils.ReAllocateIfNeeded(ref matrixUHandle, matrixDesc, FilterMode.Point, name: "_MatrixU_TSVD"); // 8x8 ��� ����
            RenderingUtils.ReAllocateIfNeeded(ref reconstructedYHandle, yDesc, FilterMode.Point, name: "_ReconstructedY_TSVD");
            RenderingUtils.ReAllocateIfNeeded(ref finalOutputHandle, outputDesc, FilterMode.Point, name: "_FinalOutput_TSVD");

            // --- ���̴� �Ķ���� ���� ---
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
            // Ŀ�� ��ȿ�� �˻� �� (����) ...
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
                // 1. ���� ����
                cmd.Blit(cameraTarget, sourceTextureHandle);

                // 2. Y, CbCr �и�
                cmd.DispatchCompute(computeShader, rgbToYKernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, storeCbCrKernelID, threadGroupsX, threadGroupsY, 1);

                // 3. A^T A ���
                cmd.DispatchCompute(computeShader, computeAtAKernelID, threadGroupsX, threadGroupsY, 1);

                // 4. A^T A ������ ���� -> V, Sigma ���
                cmd.DispatchCompute(computeShader, eigenDecompositionKernelID, threadGroupsX, threadGroupsY, 1);

                // 5. U ��� (u_i = A*v_i / sigma_i)
                cmd.DispatchCompute(computeShader, computeUKernelID, threadGroupsX, threadGroupsY, 1);

                // 6. (������) Sigma ����
                if (modifySigmaActive && modifySigmaKernelID >= 0)
                {
                    cmd.DispatchCompute(computeShader, modifySigmaKernelID, threadGroupsX, threadGroupsY, 1);
                }

                // 7. Y �籸�� (Y = U * Sigma * V^T)
                cmd.DispatchCompute(computeShader, reconstructYKernelID, threadGroupsX, threadGroupsY, 1);

                // 8. ���� RGB ����
                cmd.DispatchCompute(computeShader, combineYCbCrKernelID, threadGroupsX, threadGroupsY, 1);

                // 9. ��� ��
                cmd.Blit(finalOutputHandle, cameraTarget);

                // ��� ����� Ÿ�� ����
                RTResultHolder.DedicatedSaveTarget = cameraTarget;
            }

            // ��� ���� ���� (������ ����)
            if (SaveTrigger.SaveRequested /* && !isReadbackPending */)
            {
                // ... AsyncGPUReadback.Request(..., OnCompleteReadback_Static_TSVD); ...
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        // Static �ݹ� �Լ� (�̸� �����Ͽ� ����)
        static void OnCompleteReadback_Static_TSVD(AsyncGPUReadbackRequest request)
        {
            // ���� �ݹ� �Լ� ����� ���� �����ϰ� ��� (���ϸ� � ����)
            // ... (EXR ���� �� ���ϸ� "_TSVD" �߰� ��) ...
            Debug.Log("Truncated SVD Static Async GPU Readback �Ϸ�.");
            // ... isReadbackPending = false ó�� �ʿ� ...
        }

        public override void OnCameraCleanup(CommandBuffer cmd) { }

        public void Cleanup()
        {
            // ��� RTHandle ����
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