using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.Linq;
using System; // Math.Min 사용

// OriginBlock 클래스가 동일 프로젝트 내에 정의되어 있다고 가정

public class DCTRenderFeature_Optimized : ScriptableRendererFeature
{
    [Header("셰이더 및 설정")]
    public ComputeShader dctComputeShader; // DCT용 최적화된 .compute 파일 할당
    [Tooltip("DCT 계수에 비트스트림을 임베딩할지 여부")]
    public bool embedBitstream = true;
    [Tooltip("QIM 스텝 크기")]
    public float qimDelta = 10.0f;
    [Tooltip("Addressables에서 로드할 암호화된 데이터 키")]
    public string addressableKey = "OriginBlockData";

    private DCTRenderPass_Optimized dctRenderPass;


    public override void Create()
    {
        if (dctComputeShader == null) { /* 오류 처리 */ return; }

        dctRenderPass = new DCTRenderPass_Optimized(dctComputeShader, name, embedBitstream, qimDelta);
        dctRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (dctComputeShader != null && dctRenderPass != null )
        {
            dctRenderPass.SetEmbedActive(embedBitstream);
            if(DataManager.IsDataReady)
            {
                renderer.EnqueuePass(dctRenderPass);
            }
        }
    }

    protected override void Dispose(bool disposing) { dctRenderPass?.Cleanup(); }


    // --- DCTRenderPass 내부 클래스 (최적화 버전) ---
    class DCTRenderPass_Optimized : ScriptableRenderPass
    {
        private ComputeShader computeShader;
        private int dctPass1KernelID, dctPass2KernelID, idctPass1KernelID, idctPass2KernelID;
        private RTHandle sourceTextureHandle, intermediateHandle, dctOutputHandle, idctOutputHandle;
        private string profilerTag;
        private bool embedActive;
        private ComputeBuffer bitstreamBuffer;
        private Material overlayMaterial;

        private List<uint> payloadBits; // 헤더 포함, 패딩 전 원본 페이로드
        private List<uint> finalBitsToEmbed; // 최종 삽입될 비트 (패딩 완료)

        private float qimDelta; // QIM 스텝 크기 (셰이더에서 사용됨)
        private const int BLOCK_SIZE = 8; // DCT 블록 크기

        public DCTRenderPass_Optimized(ComputeShader shader, string tag, bool initialEmbedState, float qimDelta)
        {
            computeShader = shader; profilerTag = tag; embedActive = initialEmbedState;

            dctPass1KernelID = shader.FindKernel("DCT_Pass1_Rows");
            dctPass2KernelID = shader.FindKernel("DCT_Pass2_Cols");
            idctPass1KernelID = shader.FindKernel("IDCT_Pass1_Cols");
            idctPass2KernelID = shader.FindKernel("IDCT_Pass2_Rows");

            this.qimDelta = qimDelta; // QIM 스텝 크기
            payloadBits = new List<uint>();
            finalBitsToEmbed = new List<uint>();

            overlayMaterial = CoreUtils.CreateEngineMaterial(Shader.Find("Hidden/BlitOverlay"));
        }

        public void SetEmbedActive(bool isActive) { embedActive = isActive; }

        // UpdateBitstreamBuffer 함수는 LSBRenderPass와 동일하게 사용
        private void UpdateBitstreamBuffer(List<uint> data)
        { /* ... LSBRenderPass와 동일 ... */
            int count = (data != null) ? data.Count : 0;
            if (count == 0) 
            { 
                if (bitstreamBuffer != null) 
                { 
                    bitstreamBuffer.Release(); 
                    bitstreamBuffer = null;
                } 
                return; 
            }
            if (bitstreamBuffer == null || bitstreamBuffer.count != count || !bitstreamBuffer.IsValid())
            {
                bitstreamBuffer?.Release();
                try 
                { 
                    bitstreamBuffer = new ComputeBuffer(count, sizeof(uint), ComputeBufferType.Structured); 
                }
                catch (Exception ex) 
                { 
                    Debug.LogError($"[DCTRenderPass] ComputeBuffer 생성 실패: {ex.Message}");
                    bitstreamBuffer = null;
                    return;
                }
            }

            try 
            {
                bitstreamBuffer.SetData(data);
            }
            catch (Exception ex) 
            { 
                Debug.LogError($"[DCTRenderPass] ComputeBuffer SetData 오류: {ex.Message}"); 
                bitstreamBuffer?.Release(); 
                bitstreamBuffer = null; 
            }
        }


        // DCTRenderPass_Optimized 클래스 내부

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0; desc.msaaSamples = 1;

            // RTHandle 할당 (기존과 동일)
            var intermediateDesc = desc; intermediateDesc.colorFormat = RenderTextureFormat.RFloat; intermediateDesc.sRGB = false; intermediateDesc.enableRandomWrite = true;
            var idctDesc = desc; idctDesc.enableRandomWrite = true;
            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, name: "_SourceCopyForDCT");
            RenderingUtils.ReAllocateIfNeeded(ref intermediateHandle, intermediateDesc, FilterMode.Point, name: "_IntermediateDCT_IDCT");
            RenderingUtils.ReAllocateIfNeeded(ref dctOutputHandle, intermediateDesc, FilterMode.Point, name: "_DCTOutput");
            RenderingUtils.ReAllocateIfNeeded(ref idctOutputHandle, idctDesc, FilterMode.Point, name: "_IDCTOutput");

            // --- 비트스트림 준비 (비동기 로딩 확인 및 타일링 패딩 적용) ---

            // 최종 삽입될 비트 리스트 초기화
            finalBitsToEmbed = finalBitsToEmbed ?? new List<uint>(); // Null이면 새로 생성
            finalBitsToEmbed.Clear();

            // embedActive 플래그가 활성화 되어 있고, DataManager를 통해 데이터 로딩이 완료되었는지 확인
            // <<< 변경/추가된 부분 시작 >>>
            if (embedActive && DataManager.IsDataReady && DataManager.EncryptedOriginData != null)
            {
                // 데이터가 준비되었으므로 페이로드 구성 및 패딩 시도
                try
                {
                    finalBitsToEmbed = payloadBits;
                    // 초기화 및 복사
                    // 1. 헤더 포함 페이로드 구성 (OriginBlock 클래스 함수 호출)
                    // payloadBits는 생성자 등에서 미리 구성해 두었거나 여기서 호출
                    // 여기서는 생성자에서 payloadBits를 구성했다고 가정
                    if (payloadBits == null || payloadBits.Count == 0)
                    {
                        payloadBits = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);
                        finalBitsToEmbed = payloadBits;
                        // 만약 생성자에서 로딩 실패 등으로 payloadBits가 준비 안됐다면 여기서 재시도 또는 오류 처리
                        // 예시: payloadBits = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);
                        // 아래는 payloadBits가 유효하다고 가정하고 진행
                        if (payloadBits == null || payloadBits.Count == 0)
                        {
                            finalBitsToEmbed = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);
                            Debug.LogError("[DCTRenderPass] 페이로드 비트가 준비되지 않았습니다.");
                            throw new InvalidOperationException("Payload bits are not ready.");
                        }
                    }

                    int width = desc.width;
                    int height = desc.height;
                    // DCT 용량 = 총 블록 수
                    int numBlocksX = width / BLOCK_SIZE;
                    int numBlocksY = height / BLOCK_SIZE;
                    int availableCapacity = numBlocksX * numBlocksY;
                    int totalPayloadLength = payloadBits.Count; // L = Sync+Len+Data 길이

                    if (availableCapacity == 0)
                    {
                        Debug.LogWarning("[DCTRenderPass] 이미지 크기가 작아 블록 생성 불가.");
                        // finalBitsToEmbed는 이미 비어있음
                    }
                    else
                    {
                        // 2. 자가 복제(타일링) 패딩 수행
                        finalBitsToEmbed.Capacity = availableCapacity;
                        int currentPosition = 0;
                        while (currentPosition < availableCapacity)
                        {
                            int remainingSpace = availableCapacity - currentPosition;
                            int countToAdd = Math.Min(totalPayloadLength, remainingSpace);
                            if (countToAdd <= 0 || totalPayloadLength == 0) break;
                            finalBitsToEmbed.AddRange(payloadBits.GetRange(0, countToAdd));
                            currentPosition += countToAdd;
                        }
                        // Debug.Log($"[DCTRenderPass] 자가 복제 패딩 완료. 최종 크기: {finalBitsToEmbed.Count} / 용량: {availableCapacity}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{profilerTag}] 페이로드 구성 또는 패딩 중 오류: {ex.Message}");
                    finalBitsToEmbed.Clear(); // 오류 시 비움
                }
            }
            // <<< 변경/추가된 부분 끝 >>>
            else
            {
                // 임베딩 비활성화 또는 데이터 미준비 상태
                // finalBitsToEmbed는 이미 비어있음
                if (!DataManager.IsDataReady && embedActive)
                {
                    Debug.LogWarning("[DCTRenderPass] 데이터 미준비. 임베딩 건너<0xEB><0x91>.");
                }
            }

            // 최종 비트스트림으로 ComputeBuffer 업데이트 (데이터 없으면 버퍼 해제됨)
            UpdateBitstreamBuffer(finalBitsToEmbed);

            // --- 셰이더 파라미터 설정 ---
            int currentBitLength = finalBitsToEmbed.Count;
            bool bufferValid = bitstreamBuffer != null && bitstreamBuffer.IsValid() && bitstreamBuffer.count == currentBitLength;
            // 최종 임베딩 조건 확인
            bool shouldEmbed = embedActive && DataManager.IsDataReady && bufferValid && currentBitLength > 0;
            Debug.Log($"[DCTRenderPass] 최종 비트스트림 길이: {currentBitLength} / 유효성: {bufferValid}");

            // 공통 파라미터 (모든 커널에 설정 필요할 수 있음 - 확인 필요)
            computeShader.SetInt("Width", desc.width);
            computeShader.SetInt("Height", desc.height);

            // 각 커널 텍스처 바인딩 (기존과 동일)
            if (dctPass1KernelID >= 0)
            {
                computeShader.SetTexture(dctPass1KernelID, "Source", sourceTextureHandle);
                computeShader.SetTexture(dctPass1KernelID, "IntermediateBuffer", intermediateHandle);
            }
            if (dctPass2KernelID >= 0)
            {
                computeShader.SetTexture(dctPass2KernelID, "IntermediateBuffer", intermediateHandle);
                computeShader.SetTexture(dctPass2KernelID, "DCTOutput", dctOutputHandle);
                cmd.SetComputeBufferParam(computeShader, dctPass2KernelID, "Bitstream", bitstreamBuffer);
                cmd.SetComputeFloatParam(computeShader, "QIM_DELTA", qimDelta);
                // Bitstream 파라미터 (DCT Pass 2 커널에만 필요)
                if (shouldEmbed)
                {
                    Debug.Log($"[DCTRenderPass] shouldEmbed True!");

                    cmd.SetComputeIntParam(computeShader, "BitLength", currentBitLength);
                    cmd.SetComputeIntParam(computeShader, "Embed", 1);
                }
                else
                {
                    Debug.Log($"[DCTRenderPass] shouldEmbed false!");

                    cmd.SetComputeIntParam(computeShader, "BitLength", 0);
                    cmd.SetComputeIntParam(computeShader, "Embed", 0);
                }
            }

            if (idctPass1KernelID >= 0)
            {
                computeShader.SetTexture(idctPass1KernelID, "DCTOutput", dctOutputHandle);
                computeShader.SetTexture(idctPass1KernelID, "IntermediateBuffer", intermediateHandle);
            }
            if (idctPass2KernelID >= 0)
            {
                computeShader.SetTexture(idctPass2KernelID, "IntermediateBuffer", intermediateHandle);
                computeShader.SetTexture(idctPass2KernelID, "IDCTOutput", idctOutputHandle);
            }

            overlayMaterial.SetTexture("_MainTex", idctOutputHandle);

        }


        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            bool kernelsValid = dctPass1KernelID >= 0 && dctPass2KernelID >= 0 && idctPass1KernelID >= 0 && idctPass2KernelID >= 0;
            if (!kernelsValid) return;
            if (embedActive && (bitstreamBuffer == null || !bitstreamBuffer.IsValid()) || !DataManager.IsDataReady)
            {
                Debug.LogWarning("[DCTRenderPass] Embed 활성화 상태이나 ComputeBuffer 유효하지 않음. 혹은 데이터가 로딩 되지 않음");
                return; // 선택적: 실행 중단
            }

            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
            int width = cameraTarget.rt.width;
            int height = cameraTarget.rt.height;
            int threadGroupsX = Mathf.CeilToInt((float)width / BLOCK_SIZE);
            int threadGroupsY = Mathf.CeilToInt((float)height / BLOCK_SIZE);

            cmd.CopyTexture(cameraTarget, sourceTextureHandle); // Copy source

            using (new ProfilingScope(cmd, new ProfilingSampler(profilerTag)))
            {
                cmd.DispatchCompute(computeShader, dctPass1KernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, dctPass2KernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, idctPass1KernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, idctPass2KernelID, threadGroupsX, threadGroupsY, 1);
                //cmd.CopyTexture(idctOutputHandle, cameraTarget); // Copy result back
                cmd.Blit(idctOutputHandle, cameraTarget, overlayMaterial); // Optional: Blit to camera target
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) { }
        public void Cleanup()
        { /* 이전과 동일 */
            bitstreamBuffer?.Release(); bitstreamBuffer = null;
            RTHandles.Release(sourceTextureHandle); sourceTextureHandle = null;
            RTHandles.Release(intermediateHandle); intermediateHandle = null;
            RTHandles.Release(dctOutputHandle); dctOutputHandle = null;
            RTHandles.Release(idctOutputHandle); idctOutputHandle = null;
        }
    }
}