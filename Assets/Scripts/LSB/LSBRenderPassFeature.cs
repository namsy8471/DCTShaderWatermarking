using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class LSBRenderFeature : ScriptableRendererFeature
{
    class LSBRenderPass : ScriptableRenderPass
    {
        ComputeShader computeShader;
        int embed;
        int kernelID;
        RTHandle resultHandle;

        ComputeBuffer bitBuffer;
        List<uint> bitData;

        string profilerTag = "LSB Watermark Pass";

        public LSBRenderPass(ComputeShader shader, string profilerTag, bool doEmbed)
        {
            computeShader = shader;
            kernelID = computeShader.FindKernel("LSBComputeShader");
            this.profilerTag = profilerTag;
            embed = doEmbed ? 1 : 0;
        }
        private async void SetBitdata()
        {
            int pixelCount = Screen.width * Screen.height;

            if (bitData != null && bitData.Count == pixelCount)
                return;

            bitData = await OriginBlock.GetBitstreamRuntimeAsync("OriginBlockData");

            bitBuffer?.Release();
            bitBuffer = new ComputeBuffer(bitData.Count, sizeof(uint));

            computeShader.SetBuffer(kernelID, "Bitstream", bitBuffer);
            computeShader.SetInt("BitLength", bitBuffer.count);
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor; ;
            desc.colorFormat = RenderTextureFormat.ARGB32;
            desc.depthBufferBits = 0;
            desc.enableRandomWrite = true;
            desc.msaaSamples = 1; // ComputeShader에서는 MSAA 지원 안 함
            //desc.sRGB = false;

            resultHandle?.Release();

            resultHandle = RTHandles.Alloc(desc, name: "_LSB_Result");

            computeShader.SetTexture(kernelID, "Result", resultHandle);
            var source = renderingData.cameraData.renderer.cameraColorTargetHandle.rt;
            computeShader.SetTexture(kernelID, "Source", source); // RTHandle

            computeShader.SetInt("Width", desc.width);
            computeShader.SetInt("Height", desc.height);
            computeShader.SetInt("Embed", embed);
            
            SetBitdata();
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

            //Debug.Log("현재 Color Format: " + renderingData.cameraData.cameraTargetDescriptor.colorFormat);
            
            cmd.Blit(resultHandle, renderingData.cameraData.renderer.cameraColorTargetHandle);
            //cmd.CopyTexture(resultHandle,0,0, renderingData.cameraData.renderer.cameraColorTargetHandle,0,0);
            //Blitter.BlitCameraTexture(cmd, resultHandle, renderingData.cameraData.renderer.cameraColorTargetHandle);

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            if (resultHandle != null) RTHandles.Release(resultHandle);
        }

        // Feature 비활성화 시 호출될 수 있도록 추가
        public void Cleanup()
        {
            RTHandles.Release(resultHandle);
        }
    }

    public ComputeShader computeShader;

    public bool embedWatermark = true;

    [SerializeField] private string profilerTag = "LSB Watermark Pass";

    private LSBRenderPass pass;

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

    protected override void Dispose(bool disposing)
    {
        pass?.Cleanup();
    }
}
