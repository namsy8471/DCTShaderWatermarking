// TriangleRenderFeature.cs
//using UnityEditor.Search;
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
            renderPassEvent = RenderPassEvent.AfterRendering;

            overlayMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/BlitOverlay"));

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

            // 삼각형 정점 좌표를 한 번만 계산하고 ComputeShader로 전달
            v0 = new Vector2(0.5f * width, (1.0f - 0.4f) * height);
            v1 = new Vector2(0.55f * width, (1.0f - 0.6f) * height);
            v2 = new Vector2(0.45f * width, (1.0f - 0.6f) * height);

            edge0 = v1 - v0;
            edge1 = v2 - v1;
            edge2 = v0 - v2;

            computeShader.SetVector("v0", new Vector4(v0.x, v0.y, 0, 0));
            computeShader.SetVector("v1", new Vector4(v1.x, v1.y, 0, 0));
            computeShader.SetVector("v2", new Vector4(v2.x, v2.y, 0, 0));

            computeShader.SetVector("edge0", new Vector4(edge0.x, edge0.y, 0, 0));
            computeShader.SetVector("edge1", new Vector4(edge1.x, edge1.y, 0, 0));
            computeShader.SetVector("edge2", new Vector4(edge2.x, edge2.y, 0, 0));

            computeShader.SetVector("triangleColor", new Vector4(0, 1, 0, 0.1f));

            computeShader.SetTexture(kernelID, "Result", overlayRT.rt);
            computeShader.SetInt("width", width);
            computeShader.SetInt("height", height);

            overlayMaterial.SetTexture("_MainTex", overlayRT);

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

                // 🎯 시간 기반으로 색상 변경
                //time += Time.deltaTime;
                //float r = Mathf.Abs(Mathf.Sin(time * 2.0f));
                //float g = Mathf.Abs(Mathf.Cos(time * 2.0f));
                //float b = Mathf.Abs(Mathf.Sin(time * 1.5f));
                //Vector4 newColor = new Vector4(r, g, b, 1.0f);

                //computeShader.SetVector("triangleColor", newColor);

                computeShader.Dispatch(kernelID, dispatchX, dispatchY, 1);


                //Blitter.BlitCameraTexture(cmd, overlayRT, renderingData.cameraData.renderer.cameraColorTargetHandle, overlayMaterial, 0);
                //Blitter.BlitCameraTexture(cmd, overlayRT, renderingData.cameraData.renderer.cameraColorTargetHandle);
                //cmd.SetViewport(new Rect(0, 0, Screen.width, Screen.height));
                cmd.Blit(overlayRT, renderingData.cameraData.renderer.cameraColorTargetHandle, overlayMaterial);
                //cmd.Blit(overlayRT, renderingData.cameraData.renderer.cameraColorTargetHandle);

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
    private float lastTime;
    private float interval;
    private readonly float displayDuration = 0.1f;
    private bool isWatermarkActive = false;

    public override void Create()
    {
        //이미 renderPass가 있다면 Dispose()를 호출하여 확실하게 해제.

        renderPass?.Dispose();

        interval = 1.0f - displayDuration;
        lastTime = Time.time - interval;
        renderPass = new TriangleRenderPass(computeShader);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!isWatermarkActive)
        {
            Debug.Log("워터마킹 비작동" + interval + " 초 동안");

            if (Time.time - lastTime >= interval)
            {
                isWatermarkActive = true;  // ✅ 워터마킹 활성화
                lastTime = Time.time;

                return;
            }
        }

        else
        {
            if(Time.time - lastTime >= displayDuration)
            {
                isWatermarkActive = false;
                lastTime = Time.time;
                return;
            }

            Debug.Log("워터마킹 작동" + displayDuration + " 초 동안");

            int width = renderingData.cameraData.camera.pixelWidth;
            int height = renderingData.cameraData.camera.pixelHeight;
            RenderTextureDescriptor cameraDescriptor = renderingData.cameraData.cameraTargetDescriptor;

            renderPass.Setup(width, height, cameraDescriptor);
            renderer.EnqueuePass(renderPass);
        }
    }


    // ScriptableRendererFeature가 제거될 때 호출되는 메서드를 추가합니다.
    protected override void Dispose(bool disposing)
    {
        renderPass?.Dispose();
    }
}