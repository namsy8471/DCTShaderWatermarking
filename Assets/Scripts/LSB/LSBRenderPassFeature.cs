using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class LSBRenderFeature : ScriptableRendererFeature
{
    class LSBRenderPass : ScriptableRenderPass
    {
        ComputeShader computeShader;
        int embed;
        int kernelID;

        RTHandle sourceHandle;
        RTHandle resultHandle;

        string profilerTag = "LSB Watermark Pass";

        public LSBRenderPass(ComputeShader shader, string profilerTag, bool doEmbed)
        {
            computeShader = shader;
            kernelID = computeShader.FindKernel("LSBComputeShader");
            this.profilerTag = profilerTag;
            embed = doEmbed ? 1 : 0;
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;

            desc.graphicsFormat = GraphicsFormat.R8G8B8A8_UInt;
            desc.colorFormat = RenderTextureFormat.ARGB32;
            desc.depthBufferBits = 0;
            desc.enableRandomWrite = true;
            desc.msaaSamples = 1; // ComputeShader에서는 MSAA 지원 안 함
            desc.sRGB = false;

            resultHandle?.Release();
            sourceHandle?.Release();

            resultHandle = RTHandles.Alloc(desc, name: "_LSB_Result");
            sourceHandle = RTHandles.Alloc(desc, name: "_LSB_Source");

            computeShader.SetTexture(kernelID, "Result", resultHandle);

            var source = renderingData.cameraData.renderer.cameraColorTargetHandle;
            computeShader.SetTexture(kernelID, "Source", source); // RTHandle

            computeShader.SetInt("Width", desc.width);
            computeShader.SetInt("Height", desc.height);
            computeShader.SetInt("Embed", embed);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);

            // 카메라 컬러 타겟을 RTHandle로 복사
            
            var width = renderingData.cameraData.camera.pixelWidth;
            var height = renderingData.cameraData.camera.pixelHeight;
            int threadGroupsX = Mathf.CeilToInt(width / 8f);
            int threadGroupsY = Mathf.CeilToInt(height / 8f);

            computeShader.Dispatch(kernelID, threadGroupsX, threadGroupsY, 1);

            // 결과를 다시 카메라 컬러 타겟에 적용
            cmd.Blit(resultHandle, renderingData.cameraData.renderer.cameraColorTargetHandle);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            if (sourceHandle != null) RTHandles.Release(sourceHandle);
            if (resultHandle != null) RTHandles.Release(resultHandle);
        }
    }

    public ComputeShader computeShader;
    public bool embedWatermark = true;
    [SerializeField] private string profilerTag = "LSB Watermark Pass";

    LSBRenderPass pass;

    public override void Create()
    {
        pass = new LSBRenderPass(computeShader, profilerTag, embedWatermark)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(pass);
    }
}
