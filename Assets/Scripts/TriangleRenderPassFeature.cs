// TriangleRenderFeature.cs
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class TriangleRenderFeature : ScriptableRendererFeature
{
    class TriangleRenderPass : ScriptableRenderPass
    {
        private ComputeShader computeShader;
        private RTHandle overlayRT;
        private int kernelID;
        private ProfilingSampler sampler;
        private Material overlayMaterial;

        // Compute Shader 최적화를 위한 변수
        private Vector2 edge0, edge1, edge2;
        private Vector2 v0, v1, v2;

        public TriangleRenderPass(ComputeShader shader)
        {
            computeShader = shader;
            kernelID = computeShader.FindKernel("CSMain");
            sampler = new ProfilingSampler("TriangleRenderPass");
            renderPassEvent = RenderPassEvent.AfterRendering; // 렌더링 순서
            overlayMaterial = CoreUtils.CreateEngineMaterial("Hidden/TriangleOverlay");
            // 생성자에서는 RTHandle을 할당하지 않습니다.
        }

        public void Setup(int width, int height, RenderTextureDescriptor cameraDescriptor)
        {
            var desc = cameraDescriptor;
            desc.enableRandomWrite = true;
            desc.colorFormat = RenderTextureFormat.ARGB32;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;

            // RTHandle이 null이거나, 크기가 다를 때만 할당/재할당합니다.
            if (overlayRT == null || overlayRT.rt == null || overlayRT.rt.width != width || overlayRT.rt.height != height)
            {
                // 기존 RTHandle이 있으면 해제합니다.
                if (overlayRT != null && overlayRT.rt != null)
                {
                    RTHandles.Release(overlayRT);
                }
                overlayRT = RTHandles.Alloc(desc);
            }

            // 삼각형의 정점 (UV 공간) - Compute Shader와 동일하게 유지
            v0 = new Vector2(0.3f, 0.2f);
            v1 = new Vector2(0.7f, 0.2f);
            v2 = new Vector2(0.5f, 0.8f);

            edge0 = v1 - v0;
            edge1 = v2 - v1;
            edge2 = v0 - v2;

            computeShader.SetVector("edge0", edge0);
            computeShader.SetVector("edge1", edge1);
            computeShader.SetVector("edge2", edge2);
            computeShader.SetVector("v0", v0);
            computeShader.SetVector("v1", v1);
            computeShader.SetVector("v2", v2);
            computeShader.SetTexture(kernelID, "Result", overlayRT);
            computeShader.SetInt("width", width);
            computeShader.SetInt("height", height);
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get("TriangleRenderPass");
            using (new ProfilingScope(cmd, sampler))
            {
                int width = renderingData.cameraData.camera.pixelWidth;
                int height = renderingData.cameraData.camera.pixelHeight;
                int dispatchX = Mathf.CeilToInt(width / 8f);
                int dispatchY = Mathf.CeilToInt(height / 8f);

                computeShader.Dispatch(kernelID, dispatchX, dispatchY, 1);

                Debug.Log("Blit 호출됨!"); // 이 로그가 출력되는지 확인
                Blit(cmd, overlayRT, renderingData.cameraData.renderer.cameraColorTargetHandle, overlayMaterial);
                //Blit(cmd, overlayRT, renderingData.cameraData.renderer.cameraColorTargetHandle);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            // OnCameraCleanup에서는 아무것도 하지 않습니다.
        }


        // ScriptableRendererFeature가 제거될 때 RTHandle을 해제합니다.
        public void Dispose()
        {
            if (overlayRT != null)
            {
                RTHandles.Release(overlayRT);
                overlayRT = null;
            }
        }
    }

    [SerializeField] private ComputeShader computeShader;

    private TriangleRenderPass renderPass;

    public override void Create()
    {
        //이미 renderPass가 있다면 Dispose()를 호출하여 확실하게 해제.
        if (renderPass != null)
        {
            renderPass.Dispose();
        }
        renderPass = new TriangleRenderPass(computeShader);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        int width = renderingData.cameraData.camera.pixelWidth;
        int height = renderingData.cameraData.camera.pixelHeight;
        RenderTextureDescriptor cameraDescriptor = renderingData.cameraData.cameraTargetDescriptor;

        renderPass.Setup(width, height, cameraDescriptor);
        renderer.EnqueuePass(renderPass);
    }

    // ScriptableRendererFeature가 제거될 때 호출되는 메서드를 추가합니다.
    protected override void Dispose(bool disposing)
    {
        renderPass?.Dispose();
    }
}