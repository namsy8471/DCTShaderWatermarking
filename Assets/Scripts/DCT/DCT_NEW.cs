using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.IO; // Path 사용 위해 추가
using System.Threading.Tasks; // Task 사용 위해 추가 (선택적)

public class DCTRenderFeature_Optimized : ScriptableRendererFeature
{
    [Header("셰이더 및 설정")]
    public ComputeShader dctComputeShader; // Inspector에서 할당 (최적화된 .compute 파일)

    [Tooltip("DCT 계수에 OriginBlock 비트스트림을 임베딩할지 여부")]
    public bool embedBitstream = true;

    private DCTRenderPass_Optimized dctRenderPass;
    private List<uint> cachedBitstreamData = null; // 비트스트림 데이터 캐시
    private bool bitstreamLoaded = false;

    public override void Create()
    {
        if (dctComputeShader == null)
        {
            Debug.LogError("Optimized DCT Compute Shader가 할당되지 않았습니다.");
            return;
        }

        // --- Bitstream 데이터 로딩 (동기 또는 비동기 후 캐싱) ---
        // 예시: 동기 로딩 (OriginBlock.GetBitstreamRuntimeSync 가 있다고 가정)
        // 또는 앱 시작 시 다른 곳에서 로드 후 여기에 전달
        try
        {
            // 중요: 실제 프로젝트에서는 OriginBlock.GetBitstreamRuntimeAsync를 호출하고
            // await 하거나, 콜백 또는 다른 동기화 메커니즘을 사용해야 합니다.
            // 여기서는 간단하게 동기 메서드 호출 또는 직접 데이터 생성을 가정합니다.

            // --- 예시 1: 동기 메서드가 있다고 가정 ---
            cachedBitstreamData = OriginBlock.GetBitstreamRuntimeSync("OriginBlockData");

            // --- 예시 2: 임시 데이터 생성 (테스트용) ---
            // cachedBitstreamData = GenerateTempBitstream(256 * 144); // 예: 2048x1152 / 8x8 = 256 * 144 블록

            bitstreamLoaded = (cachedBitstreamData != null && cachedBitstreamData.Count > 0);
            if (!bitstreamLoaded)
            {
                Debug.LogWarning("OriginBlock 비트스트림 데이터를 로드하지 못했거나 데이터가 없습니다.");
            }
            else
            {
                Debug.Log($"Bitstream 데이터 로드 완료: {cachedBitstreamData.Count} bits");
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"비트스트림 데이터 로딩 중 오류 발생: {ex.Message}");
            cachedBitstreamData = null;
            bitstreamLoaded = false;
        }
        // --- 로딩 끝 ---


        dctRenderPass = new DCTRenderPass_Optimized(dctComputeShader, name, embedBitstream, cachedBitstreamData);
        dctRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }


    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (dctComputeShader != null && dctRenderPass != null)
        {
            // Pass 실행 전에 최신 embedBitstream 설정 전달
            dctRenderPass.SetEmbedActive(embedBitstream && bitstreamLoaded); // 데이터 로드 실패 시 비활성화
            renderer.EnqueuePass(dctRenderPass);
        }
    }

    protected override void Dispose(bool disposing)
    {
        dctRenderPass?.Cleanup();
    }

    // --- DCTRenderPass 내부 클래스 (최적화 버전) ---
    class DCTRenderPass_Optimized : ScriptableRenderPass
    {
        private ComputeShader computeShader;
        private int dctPass1KernelID; // DCT Rows
        private int dctPass2KernelID; // DCT Cols + Embed
        private int idctPass1KernelID; // IDCT Cols
        private int idctPass2KernelID; // IDCT Rows

        private RTHandle sourceTextureHandle;    // 원본 복사본
        private RTHandle intermediateHandle;     // 중간 결과 (DCT 1단계 출력, DCT 2단계 입력 / IDCT 1단계 출력, IDCT 2단계 입력)
        private RTHandle dctOutputHandle;        // 최종 DCT 계수 (DCT 2단계 출력, IDCT 1단계 입력)
        private RTHandle idctOutputHandle;       // 최종 복원 이미지 (IDCT 2단계 출력)

        private string profilerTag;
        private bool embedActive;

        // Bitstream 관련
        private ComputeBuffer bitstreamBuffer;
        private List<uint> initialBitstreamData; // Create에서 전달받은 캐시된 데이터

        private const int BLOCK_SIZE = 8;

        public DCTRenderPass_Optimized(ComputeShader shader, string tag, bool initialEmbedState, List<uint> bitstreamData)
        {
            computeShader = shader;
            profilerTag = tag;
            embedActive = initialEmbedState;
            initialBitstreamData = bitstreamData; // 캐시된 데이터 저장

            dctPass1KernelID = computeShader.FindKernel("DCT_Pass1_Rows");
            dctPass2KernelID = computeShader.FindKernel("DCT_Pass2_Cols");
            idctPass1KernelID = computeShader.FindKernel("IDCT_Pass1_Cols");
            idctPass2KernelID = computeShader.FindKernel("IDCT_Pass2_Rows");

            if (dctPass1KernelID < 0) Debug.LogError("Kernel DCT_Pass1_Rows 를 찾을 수 없습니다.");
            if (dctPass2KernelID < 0) Debug.LogError("Kernel DCT_Pass2_Cols 를 찾을 수 없습니다.");
            if (idctPass1KernelID < 0) Debug.LogError("Kernel IDCT_Pass1_Cols 를 찾을 수 없습니다.");
            if (idctPass2KernelID < 0) Debug.LogError("Kernel IDCT_Pass2_Rows 를 찾을 수 없습니다.");

            // 초기 Bitstream 버퍼 생성 (데이터가 있다면)
            UpdateBitstreamBuffer(initialBitstreamData);
        }

        public void SetEmbedActive(bool isActive)
        {
            embedActive = isActive;
        }

        // ComputeBuffer 생성/갱신 로직
        private void UpdateBitstreamBuffer(List<uint> data)
        {
            int count = (data != null) ? data.Count : 0;

            if (count == 0)
            {
                bitstreamBuffer?.Release();
                bitstreamBuffer = null;
                // Debug.Log("Bitstream 데이터가 없어 ComputeBuffer 해제됨.");
                return;
            }

            // 버퍼가 없거나, 크기가 다르거나, 유효하지 않으면 새로 생성
            if (bitstreamBuffer == null || bitstreamBuffer.count != count || !bitstreamBuffer.IsValid())
            {
                bitstreamBuffer?.Release();
                bitstreamBuffer = new ComputeBuffer(count, sizeof(uint), ComputeBufferType.Structured);
                Debug.Log($"Bitstream ComputeBuffer 생성/갱신: Count={count}");
            }

            // 데이터 설정
            try
            {
                bitstreamBuffer.SetData(data);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Bitstream ComputeBuffer 데이터 설정 오류: {ex.Message}");
                bitstreamBuffer?.Release();
                bitstreamBuffer = null;
            }
        }


        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var cameraTargetDescriptor = renderingData.cameraData.cameraTargetDescriptor;
            cameraTargetDescriptor.depthBufferBits = 0;
            cameraTargetDescriptor.msaaSamples = 1; // MSAA 비활성화

            // 1. Source Copy 용 핸들
            var sourceDesc = cameraTargetDescriptor;
            // sourceDesc.enableRandomWrite = false; // 읽기만 함
            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, sourceDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_SourceCopyForDCT");

            // 2. Intermediate Buffer 핸들 (RFloat 또는 RHalf)
            var intermediateDesc = cameraTargetDescriptor;
            intermediateDesc.colorFormat = RenderTextureFormat.RFloat; // 또는 RHalf
            intermediateDesc.sRGB = false;
            intermediateDesc.enableRandomWrite = true;
            RenderingUtils.ReAllocateIfNeeded(ref intermediateHandle, intermediateDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_IntermediateDCT_IDCT");

            // 3. DCT Output 핸들 (RFloat 또는 RHalf)
            var dctDesc = intermediateDesc; // Intermediate와 동일 포맷 사용
            RenderingUtils.ReAllocateIfNeeded(ref dctOutputHandle, dctDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_DCTOutput");

            // 4. IDCT Output 핸들 (최종 결과, 카메라 타겟과 유사)
            var idctDesc = cameraTargetDescriptor;
            idctDesc.enableRandomWrite = true; // IDCT Pass 2가 쓰기용
            // idctDesc.colorFormat = RenderTextureFormat.ARGB32; // 필요시 명시
            RenderingUtils.ReAllocateIfNeeded(ref idctOutputHandle, idctDesc, FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_IDCTOutput");

            // --- 셰이더 파라미터 설정 ---
            int width = cameraTargetDescriptor.width;
            int height = cameraTargetDescriptor.height;

            // 모든 커널에 공통 파라미터 설정
            computeShader.SetInt("Width", width);
            computeShader.SetInt("Height", height);

            // DCT Pass 1
            if (dctPass1KernelID >= 0)
            {
                computeShader.SetTexture(dctPass1KernelID, "Source", sourceTextureHandle);
                computeShader.SetTexture(dctPass1KernelID, "IntermediateBuffer", intermediateHandle); // 출력
            }
            // DCT Pass 2
            if (dctPass2KernelID >= 0)
            {
                computeShader.SetTexture(dctPass2KernelID, "IntermediateBuffer", intermediateHandle); // 입력
                computeShader.SetTexture(dctPass2KernelID, "DCTOutput", dctOutputHandle);       // 출력
                // Bitstream 설정
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
                computeShader.SetTexture(idctPass1KernelID, "DCTOutput", dctOutputHandle);       // 입력
                computeShader.SetTexture(idctPass1KernelID, "IntermediateBuffer", intermediateHandle); // 출력 (재사용)
            }
            // IDCT Pass 2
            if (idctPass2KernelID >= 0)
            {
                computeShader.SetTexture(idctPass2KernelID, "IntermediateBuffer", intermediateHandle); // 입력 (재사용)
                computeShader.SetTexture(idctPass2KernelID, "IDCTOutput", idctOutputHandle);       // 최종 출력
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            bool canExecute = dctPass1KernelID >= 0 && dctPass2KernelID >= 0 && idctPass1KernelID >= 0 && idctPass2KernelID >= 0;
            if (embedActive && (bitstreamBuffer == null || !bitstreamBuffer.IsValid()))
            {
                // 임베딩 활성화 시 버퍼 유효성 재확인 (OnCameraSetup 이후 상태 변경 가능성 대비)
                canExecute = false;
                // Debug.LogWarning("Embed 활성화 상태이나 Bitstream 버퍼가 유효하지 않아 Pass 실행 건너뜁니다.");
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
                // 1. 원본 -> 임시 핸들 복사
                cmd.CopyTexture(cameraTarget, sourceTextureHandle);

                // 2. DCT Pass 1 (Rows) 실행: Source -> IntermediateBuffer
                cmd.DispatchCompute(computeShader, dctPass1KernelID, threadGroupsX, threadGroupsY, 1);

                // 3. DCT Pass 2 (Cols + Embed) 실행: IntermediateBuffer -> DCTOutput
                //    (중요: Pass 2 실행 전에 Bitstream 파라미터 설정이 OnCameraSetup에서 완료되어야 함)
                cmd.DispatchCompute(computeShader, dctPass2KernelID, threadGroupsX, threadGroupsY, 1);

                // 4. IDCT Pass 1 (Cols) 실행: DCTOutput -> IntermediateBuffer (재사용)
                cmd.DispatchCompute(computeShader, idctPass1KernelID, threadGroupsX, threadGroupsY, 1);

                // 5. IDCT Pass 2 (Rows) 실행: IntermediateBuffer -> IDCTOutput
                cmd.DispatchCompute(computeShader, idctPass2KernelID, threadGroupsX, threadGroupsY, 1);

                // 6. 최종 결과 (IDCTOutput) -> 카메라 타겟으로 복사
                cmd.CopyTexture(idctOutputHandle, cameraTarget);
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) { }

        public void Cleanup()
        {
            Debug.Log("Optimized DCTRenderPass Cleanup 호출됨");
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