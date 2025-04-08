using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.IO; // Path ��� ���� �߰�
using System.Threading.Tasks; // Task ��� ���� �߰� (������)

public class DCTRenderFeature_Optimized : ScriptableRendererFeature
{
    [Header("���̴� �� ����")]
    public ComputeShader dctComputeShader; // Inspector���� �Ҵ� (����ȭ�� .compute ����)

    [Tooltip("DCT ����� OriginBlock ��Ʈ��Ʈ���� �Ӻ������� ����")]
    public bool embedBitstream = true;

    private DCTRenderPass_Optimized dctRenderPass;
    private List<uint> cachedBitstreamData = null; // ��Ʈ��Ʈ�� ������ ĳ��
    private bool bitstreamLoaded = false;

    public override void Create()
    {
        if (dctComputeShader == null)
        {
            Debug.LogError("Optimized DCT Compute Shader�� �Ҵ���� �ʾҽ��ϴ�.");
            return;
        }

        // --- Bitstream ������ �ε� (���� �Ǵ� �񵿱� �� ĳ��) ---
        // ����: ���� �ε� (OriginBlock.GetBitstreamRuntimeSync �� �ִٰ� ����)
        // �Ǵ� �� ���� �� �ٸ� ������ �ε� �� ���⿡ ����
        try
        {
            // �߿�: ���� ������Ʈ������ OriginBlock.GetBitstreamRuntimeAsync�� ȣ���ϰ�
            // await �ϰų�, �ݹ� �Ǵ� �ٸ� ����ȭ ��Ŀ������ ����ؾ� �մϴ�.
            // ���⼭�� �����ϰ� ���� �޼��� ȣ�� �Ǵ� ���� ������ ������ �����մϴ�.

            // --- ���� 1: ���� �޼��尡 �ִٰ� ���� ---
            cachedBitstreamData = OriginBlock.GetBitstreamRuntimeSync("OriginBlockData");

            // --- ���� 2: �ӽ� ������ ���� (�׽�Ʈ��) ---
            // cachedBitstreamData = GenerateTempBitstream(256 * 144); // ��: 2048x1152 / 8x8 = 256 * 144 ���

            bitstreamLoaded = (cachedBitstreamData != null && cachedBitstreamData.Count > 0);
            if (!bitstreamLoaded)
            {
                Debug.LogWarning("OriginBlock ��Ʈ��Ʈ�� �����͸� �ε����� ���߰ų� �����Ͱ� �����ϴ�.");
            }
            else
            {
                Debug.Log($"Bitstream ������ �ε� �Ϸ�: {cachedBitstreamData.Count} bits");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"��Ʈ��Ʈ�� ������ �ε� �� ���� �߻�: {ex.Message}");
            cachedBitstreamData = null;
            bitstreamLoaded = false;
        }
        // --- �ε� �� ---


        dctRenderPass = new DCTRenderPass_Optimized(dctComputeShader, name, embedBitstream, cachedBitstreamData);
        dctRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }


    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (dctComputeShader != null && dctRenderPass != null)
        {
            // Pass ���� ���� �ֽ� embedBitstream ���� ����
            dctRenderPass.SetEmbedActive(embedBitstream && bitstreamLoaded); // ������ �ε� ���� �� ��Ȱ��ȭ
            renderer.EnqueuePass(dctRenderPass);
        }
    }

    protected override void Dispose(bool disposing)
    {
        dctRenderPass?.Cleanup();
    }

    // --- DCTRenderPass ���� Ŭ���� (����ȭ ����) ---
    class DCTRenderPass_Optimized : ScriptableRenderPass
    {
        private ComputeShader computeShader;
        private int dctPass1KernelID; // DCT Rows
        private int dctPass2KernelID; // DCT Cols + Embed
        private int idctPass1KernelID; // IDCT Cols
        private int idctPass2KernelID; // IDCT Rows

        private RTHandle sourceTextureHandle;    // ���� ���纻
        private RTHandle intermediateHandle;     // �߰� ��� (DCT 1�ܰ� ���, DCT 2�ܰ� �Է� / IDCT 1�ܰ� ���, IDCT 2�ܰ� �Է�)
        private RTHandle dctOutputHandle;        // ���� DCT ��� (DCT 2�ܰ� ���, IDCT 1�ܰ� �Է�)
        private RTHandle idctOutputHandle;       // ���� ���� �̹��� (IDCT 2�ܰ� ���)

        private string profilerTag;
        private bool embedActive;

        // Bitstream ����
        private ComputeBuffer bitstreamBuffer;
        private List<uint> initialBitstreamData; // Create���� ���޹��� ĳ�õ� ������

        private const int BLOCK_SIZE = 8;

        public DCTRenderPass_Optimized(ComputeShader shader, string tag, bool initialEmbedState, List<uint> bitstreamData)
        {
            computeShader = shader;
            profilerTag = tag;
            embedActive = initialEmbedState;
            initialBitstreamData = bitstreamData; // ĳ�õ� ������ ����

            dctPass1KernelID = computeShader.FindKernel("DCT_Pass1_Rows");
            dctPass2KernelID = computeShader.FindKernel("DCT_Pass2_Cols");
            idctPass1KernelID = computeShader.FindKernel("IDCT_Pass1_Cols");
            idctPass2KernelID = computeShader.FindKernel("IDCT_Pass2_Rows");

            if (dctPass1KernelID < 0) Debug.LogError("Kernel DCT_Pass1_Rows �� ã�� �� �����ϴ�.");
            if (dctPass2KernelID < 0) Debug.LogError("Kernel DCT_Pass2_Cols �� ã�� �� �����ϴ�.");
            if (idctPass1KernelID < 0) Debug.LogError("Kernel IDCT_Pass1_Cols �� ã�� �� �����ϴ�.");
            if (idctPass2KernelID < 0) Debug.LogError("Kernel IDCT_Pass2_Rows �� ã�� �� �����ϴ�.");

            // �ʱ� Bitstream ���� ���� (�����Ͱ� �ִٸ�)
            UpdateBitstreamBuffer(initialBitstreamData);
        }

        public void SetEmbedActive(bool isActive)
        {
            embedActive = isActive;
        }

        // ComputeBuffer ����/���� ����
        private void UpdateBitstreamBuffer(List<uint> data)
        {
            int count = (data != null) ? data.Count : 0;

            if (count == 0)
            {
                bitstreamBuffer?.Release();
                bitstreamBuffer = null;
                // Debug.Log("Bitstream �����Ͱ� ���� ComputeBuffer ������.");
                return;
            }

            // ���۰� ���ų�, ũ�Ⱑ �ٸ��ų�, ��ȿ���� ������ ���� ����
            if (bitstreamBuffer == null || bitstreamBuffer.count != count || !bitstreamBuffer.IsValid())
            {
                bitstreamBuffer?.Release();
                bitstreamBuffer = new ComputeBuffer(count, sizeof(uint), ComputeBufferType.Structured);
                Debug.Log($"Bitstream ComputeBuffer ����/����: Count={count}");
            }

            // ������ ����
            try
            {
                bitstreamBuffer.SetData(data);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Bitstream ComputeBuffer ������ ���� ����: {ex.Message}");
                bitstreamBuffer?.Release();
                bitstreamBuffer = null;
            }
        }


        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var cameraTargetDescriptor = renderingData.cameraData.cameraTargetDescriptor;
            cameraTargetDescriptor.depthBufferBits = 0;
            cameraTargetDescriptor.msaaSamples = 1; // MSAA ��Ȱ��ȭ

            // 1. Source Copy �� �ڵ�
            var sourceDesc = cameraTargetDescriptor;
            // sourceDesc.enableRandomWrite = false; // �б⸸ ��
            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, sourceDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_SourceCopyForDCT");

            // 2. Intermediate Buffer �ڵ� (RFloat �Ǵ� RHalf)
            var intermediateDesc = cameraTargetDescriptor;
            intermediateDesc.colorFormat = RenderTextureFormat.RFloat; // �Ǵ� RHalf
            intermediateDesc.sRGB = false;
            intermediateDesc.enableRandomWrite = true;
            RenderingUtils.ReAllocateIfNeeded(ref intermediateHandle, intermediateDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_IntermediateDCT_IDCT");

            // 3. DCT Output �ڵ� (RFloat �Ǵ� RHalf)
            var dctDesc = intermediateDesc; // Intermediate�� ���� ���� ���
            RenderingUtils.ReAllocateIfNeeded(ref dctOutputHandle, dctDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_DCTOutput");

            // 4. IDCT Output �ڵ� (���� ���, ī�޶� Ÿ�ٰ� ����)
            var idctDesc = cameraTargetDescriptor;
            idctDesc.enableRandomWrite = true; // IDCT Pass 2�� �����
            // idctDesc.colorFormat = RenderTextureFormat.ARGB32; // �ʿ�� ���
            RenderingUtils.ReAllocateIfNeeded(ref idctOutputHandle, idctDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_IDCTOutput");

            // --- ���̴� �Ķ���� ���� ---
            int width = cameraTargetDescriptor.width;
            int height = cameraTargetDescriptor.height;

            // ��� Ŀ�ο� ���� �Ķ���� ����
            computeShader.SetInt("Width", width);
            computeShader.SetInt("Height", height);

            // DCT Pass 1
            if (dctPass1KernelID >= 0)
            {
                computeShader.SetTexture(dctPass1KernelID, "Source", sourceTextureHandle);
                computeShader.SetTexture(dctPass1KernelID, "IntermediateBuffer", intermediateHandle); // ���
            }
            // DCT Pass 2
            if (dctPass2KernelID >= 0)
            {
                computeShader.SetTexture(dctPass2KernelID, "IntermediateBuffer", intermediateHandle); // �Է�
                computeShader.SetTexture(dctPass2KernelID, "DCTOutput", dctOutputHandle);       // ���
                // Bitstream ����
                if (embedActive && bitstreamBuffer != null && bitstreamBuffer.IsValid())
                {
                    cmd.SetComputeBufferParam(computeShader, dctPass2KernelID, "Bitstream", bitstreamBuffer);
                    cmd.SetComputeIntParam(computeShader, "BitLength", bitstreamBuffer.count);
                    cmd.SetComputeIntParam(computeShader, "Embed", 1);
                }
                else
                {
                    cmd.SetComputeIntParam(computeShader, "BitLength", 0);
                    cmd.SetComputeIntParam(computeShader, "Embed", 0);
                }
            }
            // IDCT Pass 1
            if (idctPass1KernelID >= 0)
            {
                computeShader.SetTexture(idctPass1KernelID, "DCTOutput", dctOutputHandle);       // �Է�
                computeShader.SetTexture(idctPass1KernelID, "IntermediateBuffer", intermediateHandle); // ��� (����)
            }
            // IDCT Pass 2
            if (idctPass2KernelID >= 0)
            {
                computeShader.SetTexture(idctPass2KernelID, "IntermediateBuffer", intermediateHandle); // �Է� (����)
                computeShader.SetTexture(idctPass2KernelID, "IDCTOutput", idctOutputHandle);       // ���� ���
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            bool canExecute = dctPass1KernelID >= 0 && dctPass2KernelID >= 0 && idctPass1KernelID >= 0 && idctPass2KernelID >= 0;
            if (embedActive && (bitstreamBuffer == null || !bitstreamBuffer.IsValid()))
            {
                // �Ӻ��� Ȱ��ȭ �� ���� ��ȿ�� ��Ȯ�� (OnCameraSetup ���� ���� ���� ���ɼ� ���)
                canExecute = false;
                // Debug.LogWarning("Embed Ȱ��ȭ �����̳� Bitstream ���۰� ��ȿ���� �ʾ� Pass ���� �ǳʶݴϴ�.");
            }
            if (!canExecute) return;


            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;

            int width = cameraTarget.rt.width;
            int height = cameraTarget.rt.height;
            int threadGroupsX = Mathf.CeilToInt((float)width / BLOCK_SIZE);
            int threadGroupsY = Mathf.CeilToInt((float)height / BLOCK_SIZE);

            using (new ProfilingScope(cmd, new ProfilingSampler(profilerTag)))
            {
                // 1. ���� -> �ӽ� �ڵ� ����
                cmd.CopyTexture(cameraTarget, sourceTextureHandle);

                // 2. DCT Pass 1 (Rows) ����: Source -> IntermediateBuffer
                cmd.DispatchCompute(computeShader, dctPass1KernelID, threadGroupsX, threadGroupsY, 1);

                // 3. DCT Pass 2 (Cols + Embed) ����: IntermediateBuffer -> DCTOutput
                //    (�߿�: Pass 2 ���� ���� Bitstream �Ķ���� ������ OnCameraSetup���� �Ϸ�Ǿ�� ��)
                cmd.DispatchCompute(computeShader, dctPass2KernelID, threadGroupsX, threadGroupsY, 1);

                // 4. IDCT Pass 1 (Cols) ����: DCTOutput -> IntermediateBuffer (����)
                cmd.DispatchCompute(computeShader, idctPass1KernelID, threadGroupsX, threadGroupsY, 1);

                // 5. IDCT Pass 2 (Rows) ����: IntermediateBuffer -> IDCTOutput
                cmd.DispatchCompute(computeShader, idctPass2KernelID, threadGroupsX, threadGroupsY, 1);

                // 6. ���� ��� (IDCTOutput) -> ī�޶� Ÿ������ ����
                cmd.CopyTexture(idctOutputHandle, cameraTarget);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) { }

        public void Cleanup()
        {
            Debug.Log("Optimized DCTRenderPass Cleanup ȣ���");
            bitstreamBuffer?.Release();
            bitstreamBuffer = null;

            RTHandles.Release(sourceTextureHandle);
            RTHandles.Release(intermediateHandle);
            RTHandles.Release(dctOutputHandle);
            RTHandles.Release(idctOutputHandle);

            sourceTextureHandle = null;
            intermediateHandle = null;
            dctOutputHandle = null;
            idctOutputHandle = null;
        }
    }
}