using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic; // List 사용 위해 추가
using System.IO; // Path 사용 위해 추가 (OriginBlock 내부 로직 때문)

public class DCTRenderFeature : ScriptableRendererFeature
{
    [Header("셰이더 및 설정")]
    public ComputeShader dctComputeShader; // Inspector에서 할당

    [Tooltip("DCT 계수에 OriginBlock 비트스트림을 임베딩할지 여부")]
    public bool embedBitstream = true; // Inspector에서 제어 가능

    private DCTRenderPass dctRenderPass;

    public override void Create()
    {
        // Feature 생성 시 OriginBlock 파일 생성 시도
        // 중요: GenerateAndSave가 에디터 전용 API를 사용한다면 빌드 시 오류 발생 가능
        //       또는 런타임에 호출해도 안전하게 OriginBlock 코드가 작성되었는지 확인 필요.
        try
        {

        }
        catch (System.Exception ex)
        {
            // GenerateAndSave가 실패할 경우 (예: 권한 문제, 에디터 전용 API 런타임 호출 등)
            Debug.LogError($"OriginBlock.GenerateAndSave() 호출 중 오류 발생: {ex.Message}");
            // 필요 시 여기서 기능 비활성화 또는 다른 오류 처리 수행
        }


        if (dctComputeShader == null)
        {
            Debug.LogError("DCT Compute Shader가 할당되지 않았습니다.");
            return;
        }

        // DCTRenderPass 인스턴스 생성 및 설정
        dctRenderPass = new DCTRenderPass(dctComputeShader, name, embedBitstream);
        dctRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    // 렌더러에 패스 추가
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (dctComputeShader != null && dctRenderPass != null)
        {
            // Pass 실행 전에 최신 embedBitstream 설정 전달
            dctRenderPass.SetEmbedActive(embedBitstream);
            renderer.EnqueuePass(dctRenderPass);
        }
    }

    // Feature 비활성화 또는 제거 시 리소스 정리
    protected override void Dispose(bool disposing)
    {
        // ScriptableRenderPass의 Cleanup 메서드를 호출하여 내부 리소스(ComputeBuffer 등) 해제
        dctRenderPass?.Cleanup();
    }

    // --- DCTRenderPass 내부 클래스 ---
    class DCTRenderPass : ScriptableRenderPass
    {
        private ComputeShader computeShader;
        private int dctKernelID;
        private int idctKernelID; // <<< IDCT 커널 ID 추가

        private RTHandle sourceTextureHandle;
        private RTHandle dctOutputHandle;
        private RTHandle idctOutputHandle; // <<< IDCT 결과 핸들 추가

        private string profilerTag;
        private bool embedActive; // 현재 임베딩 활성화 상태

        // Bitstream 관련 멤버 변수
        private ComputeBuffer bitstreamBuffer;
        // private List<uint> bitstreamData; // SetData 직접 사용 시 불필요할 수 있음

        private const int BLOCK_SIZE = 8;

        public DCTRenderPass(ComputeShader shader, string tag, bool initialEmbedState)
        {
            computeShader = shader;
            profilerTag = tag;
            embedActive = initialEmbedState;
            dctKernelID = computeShader.FindKernel("DCTKernel");
            idctKernelID = computeShader.FindKernel("IDCTKernel"); // <<< IDCT 커널 찾기

            if (dctKernelID < 0) Debug.LogError("DCTKernel을 찾을 수 없습니다.");
            if (idctKernelID < 0) Debug.LogError("IDCTKernel을 찾을 수 없습니다."); // <<< IDCT 커널 확인
        }

        public void SetEmbedActive(bool isActive)
        {
            embedActive = isActive;
        }

        private async void SetBitstreamData(CommandBuffer cmd)
        {
            List<uint> bitstreamData = await OriginBlock.GetBitstreamRuntimeAsync("OriginBlockData");

            if (bitstreamData == null || bitstreamData.Count == 0)
            {
                Debug.LogError("OriginBlock에서 유효한 비트스트림을 가져오지 못했습니다.");
                bitstreamBuffer?.Release();
                bitstreamBuffer = null;
                cmd.SetComputeIntParam(computeShader, "BitLength", 0);
                cmd.SetComputeIntParam(computeShader, "Embed", 0);
                return;
            }

            if (bitstreamBuffer == null || bitstreamBuffer.count != bitstreamData.Count || !bitstreamBuffer.IsValid())
            {
                bitstreamBuffer?.Release();
                // ComputeBuffer 생성 시 0개 데이터 예외 처리
                if (bitstreamData.Count > 0)
                {
                    bitstreamBuffer = new ComputeBuffer(bitstreamData.Count, sizeof(uint), ComputeBufferType.Structured);
                    Debug.Log($"Bitstream ComputeBuffer 생성/갱신: Count={bitstreamData.Count}");
                }
                else
                {
                    bitstreamBuffer = null; // 0개면 null로 유지
                }
            }

            // bitstreamBuffer가 유효할 때만 데이터 설정 및 바인딩
            if (bitstreamBuffer != null)
            {
                try
                {
                    bitstreamBuffer.SetData(bitstreamData);
                    cmd.SetComputeBufferParam(computeShader, dctKernelID, "Bitstream", bitstreamBuffer);
                    cmd.SetComputeIntParam(computeShader, "BitLength", bitstreamBuffer.count);
                    cmd.SetComputeIntParam(computeShader, "Embed", embedActive ? 1 : 0);
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"Bitstream ComputeBuffer 설정 오류: {ex.Message}");
                    cmd.SetComputeIntParam(computeShader, "BitLength", 0);
                    cmd.SetComputeIntParam(computeShader, "Embed", 0);
                    if (bitstreamBuffer != null && !bitstreamBuffer.IsValid())
                    {
                        bitstreamBuffer.Release();
                        bitstreamBuffer = null;
                    }
                }
            }
            else // bitstreamBuffer가 null인 경우 (데이터가 0개인 경우 등)
            {
                cmd.SetComputeIntParam(computeShader, "BitLength", 0);
                cmd.SetComputeIntParam(computeShader, "Embed", 0);
            }
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var cameraTargetDescriptor = renderingData.cameraData.cameraTargetDescriptor;
            var sourceDesc = cameraTargetDescriptor;
            sourceDesc.depthBufferBits = 0;
            sourceDesc.msaaSamples = 1;

            var dctDesc = cameraTargetDescriptor;
            dctDesc.colorFormat = RenderTextureFormat.RFloat;
            dctDesc.depthBufferBits = 0;
            dctDesc.sRGB = false;
            dctDesc.enableRandomWrite = true;
            dctDesc.msaaSamples = 1;

            // IDCT 결과(복원 이미지)용 Desc
            var idctDesc = cameraTargetDescriptor; // 최종 결과는 원본과 유사한 포맷
            idctDesc.depthBufferBits = 0;
            idctDesc.enableRandomWrite = true; // IDCTKernel이 쓰므로 true
            idctDesc.msaaSamples = 1;
            // idctDesc.sRGB = cameraTargetDescriptor.sRGB; // 원본 sRGB 설정 따르도록

            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, sourceDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_SourceCopyForDCT");
            RenderingUtils.ReAllocateIfNeeded(ref dctOutputHandle, dctDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_DCTOutput");
            RenderingUtils.ReAllocateIfNeeded(ref idctOutputHandle, idctDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_IDCTOutput"); // <<< IDCT 핸들 할당

            SetBitstreamData(cmd); // 비트스트림 설정 호출

            if (dctKernelID >= 0)
            {
                if (sourceTextureHandle != null && dctOutputHandle != null)
                {
                    // CommandBuffer를 사용하여 파라미터 설정 (OnCameraSetup에서 권장)
                    computeShader.SetTexture(dctKernelID, "Source", sourceTextureHandle);
                    computeShader.SetTexture(dctKernelID, "DCTOutput", dctOutputHandle);
                    computeShader.SetInt("Width", cameraTargetDescriptor.width);
                    computeShader.SetInt("Height", cameraTargetDescriptor.height);
                }
                else Debug.LogError("DCT Pass: RTHandle 할당 실패!");
            }

            if (idctKernelID >= 0) // <<< IDCT 커널 파라미터 설정
            {
                if (dctOutputHandle != null && idctOutputHandle != null)
                {
                    // 중요: IDCTKernel의 입력 이름을 "DCTInput", 출력을 "IDCTOutput"으로 가정
                    computeShader.SetTexture(idctKernelID, "DCTInput", dctOutputHandle); // DCT 결과를 IDCT 입력으로
                    computeShader.SetTexture(idctKernelID, "IDCTOutput", idctOutputHandle); // IDCT 결과를 새 핸들에
                    computeShader.SetInt("Width", cameraTargetDescriptor.width);
                    computeShader.SetInt("Height", cameraTargetDescriptor.height);
                }
                else Debug.LogError("DCT Pass: IDCT용 RTHandle 할당 실패!");
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // 실행 조건 강화: 커널 ID 유효하고, 임베딩 시 buffer도 유효해야 함
            bool canExecute = dctKernelID >= 0;
            if (embedActive && (bitstreamBuffer == null || !bitstreamBuffer.IsValid()))
            {
                // 임베딩이 활성화되었는데 버퍼가 준비 안되면 실행 안 함 (오류 방지)
                // 또는 embedActive = false; 로 강제 비활성화 할 수도 있음
                canExecute = false;
                // Debug.LogWarning("Embed 활성화 상태이나 Bitstream 버퍼가 유효하지 않아 DCT Pass 실행 건너<0xEB><0x91>니다.");
            }

            if (!canExecute) return;

            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;

            using (new ProfilingScope(cmd, new ProfilingSampler(profilerTag)))
            {
                // 1. 원본 -> 임시 핸들 복사
                //Blitter.BlitCameraTexture(cmd, cameraTarget, sourceTextureHandle);
                cmd.CopyTexture(cameraTarget, sourceTextureHandle);

                // 2. DCT 커널 실행 (Source 읽기 -> DCTOutput 쓰기)
                int threadGroupsX = Mathf.CeilToInt((float)cameraTarget.rt.width / BLOCK_SIZE);
                int threadGroupsY = Mathf.CeilToInt((float)cameraTarget.rt.height / BLOCK_SIZE);
                computeShader.Dispatch(dctKernelID, threadGroupsX, threadGroupsY, 1);

                //Blitter.BlitCameraTexture(cmd, dctOutputHandle, cameraTarget);

                // --- !!! IDCT 커널 실행 추가 !!! ---
                // 3. IDCT 커널 실행 (DCTOutput 읽기 -> IDCTOutput 쓰기)
                computeShader.Dispatch(idctKernelID, threadGroupsX, threadGroupsY, 1);
                // ----------------------------------
                
                // --- !!! 최종 Blit 대상 변경 !!! ---
                // 4. IDCT 결과(idctOutputHandle)를 최종 타겟으로 Blit
                //Blitter.BlitCameraTexture(cmd, idctOutputHandle, cameraTarget);
                cmd.CopyTexture(idctOutputHandle, cameraTarget); // 최종 결과를 카메라 타겟으로 복사
                // ----------------------------------
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) { }

        public void Cleanup()
        {
            Debug.Log("DCTRenderPass Cleanup 호출됨");
            bitstreamBuffer?.Release();
            bitstreamBuffer = null;

            RTHandles.Release(sourceTextureHandle);
            RTHandles.Release(dctOutputHandle);
            RTHandles.Release(idctOutputHandle); // <<< IDCT 핸들 해제 추가

            sourceTextureHandle = null;
            dctOutputHandle = null;
            idctOutputHandle = null; // <<< IDCT 핸들 null 설정 추가
        }
    }
}