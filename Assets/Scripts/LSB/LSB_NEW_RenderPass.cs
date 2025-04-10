using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.Linq;
using System; // Math.Min 사용

// OriginBlock 클래스가 동일 프로젝트 내에 정의되어 있다고 가정

public class LSBRenderFeature : ScriptableRendererFeature
{
    [Header("셰이더 및 설정")]
    public ComputeShader lsbComputeShader;
    [Tooltip("Spatial LSB 방식으로 비트스트림을 임베딩할지 여부")]
    public bool embedBitstream = true;
    [Tooltip("Addressables에서 로드할 암호화된 데이터 키")]
    public string addressableKey = "OriginBlockData";

    private LSBRenderPass lsbRenderPass;
    private byte[] cachedEncryptedData = null;
    private bool dataLoaded = false;

    public override void Create()
    {
        if (lsbComputeShader == null) { /* 오류 처리 */ return; }

        // 데이터 로딩 (동기 예시)
        //try
        //{
        //    cachedEncryptedData = OriginBlock.LoadEncryptedDataBytesSync(addressableKey); // 동기 로딩 사용
        //    dataLoaded = (cachedEncryptedData != null && cachedEncryptedData.Length > 0);
        //    if (!dataLoaded) Debug.LogWarning("[LSBRenderFeature] 암호화된 데이터 로드 실패 또는 없음.");
        //    else Debug.Log($"[LSBRenderFeature] 암호화된 데이터 로딩 완료: {cachedEncryptedData.Length} bytes");
        //}
        //catch (Exception ex)
        //{
        //    Debug.LogError($"[LSBRenderFeature] 데이터 로딩 중 오류: {ex.Message}");
        //    cachedEncryptedData = null; dataLoaded = false;
        //}

        lsbRenderPass = new LSBRenderPass(lsbComputeShader, name, embedBitstream, cachedEncryptedData);
        lsbRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (lsbComputeShader != null && lsbRenderPass != null)
        {
            lsbRenderPass.SetEmbedActive(embedBitstream && dataLoaded);
            renderer.EnqueuePass(lsbRenderPass);
        }
    }

    protected override void Dispose(bool disposing) { lsbRenderPass?.Cleanup(); }

    // --- LSB Render Pass 내부 클래스 ---
    class LSBRenderPass : ScriptableRenderPass
    {
        private ComputeShader computeShader;
        private int kernelID;
        private RTHandle sourceTextureHandle, outputTextureHandle;
        private string profilerTag;
        private bool embedActive;
        private ComputeBuffer bitstreamBuffer;
        private byte[] initialEncryptedData;
        private List<uint> payloadBits; // 헤더 포함, 패딩 전 원본 페이로드
        private List<uint> finalBitsToEmbed; // 최종 삽입될 비트 (패딩 완료)

        private const int THREAD_GROUP_SIZE_X = 8;
        private const int THREAD_GROUP_SIZE_Y = 8;

        public LSBRenderPass(ComputeShader shader, string tag, bool initialEmbedState, byte[] encryptedData)
        {
            computeShader = shader; profilerTag = tag; embedActive = initialEmbedState;
            initialEncryptedData = encryptedData;
            kernelID = computeShader.FindKernel("LSBEmbedKernel");
            if (kernelID < 0) Debug.LogError("Kernel LSBEmbedKernel 찾기 실패");

            // 초기 페이로드 구성 (헤더+데이터)
            payloadBits = new List<uint>(); // 초기화
            if (initialEncryptedData != null && initialEncryptedData.Length > 0)
            {
                try
                {
                    // OriginBlock 클래스의 함수 사용 (프로젝트 내 OriginBlock.cs 필요)
                    payloadBits = OriginBlock.ConstructPayloadWithHeader(initialEncryptedData);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LSBRenderPass] 초기 페이로드 구성 오류: {ex.Message}");
                }
            }
            finalBitsToEmbed = new List<uint>();
        }

        public void SetEmbedActive(bool isActive) { embedActive = isActive; }

        private void UpdateBitstreamBuffer(List<uint> data)
        { /* 이전 답변과 동일한 로직 */
            int count = (data != null) ? data.Count : 0;
            if (count == 0)
            {
                if (bitstreamBuffer != null) { bitstreamBuffer.Release(); bitstreamBuffer = null; }
                return;
            }
            if (bitstreamBuffer == null || bitstreamBuffer.count != count || !bitstreamBuffer.IsValid())
            {
                bitstreamBuffer?.Release();
                try
                {
                    bitstreamBuffer = new ComputeBuffer(count, sizeof(uint), ComputeBufferType.Structured);
                }
                catch (Exception ex) { Debug.LogError($"[LSBRenderPass] ComputeBuffer 생성 실패: {ex.Message}"); bitstreamBuffer = null; return; }
            }
            try { bitstreamBuffer.SetData(data); }
            catch (Exception ex) { Debug.LogError($"[LSBRenderPass] ComputeBuffer SetData 오류: {ex.Message}"); bitstreamBuffer?.Release(); bitstreamBuffer = null; }
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0; desc.msaaSamples = 1;
            var outputDesc = desc; outputDesc.enableRandomWrite = true;

            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SourceCopyForLSB");
            RenderingUtils.ReAllocateIfNeeded(ref outputTextureHandle, outputDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_LSBOutput");

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
                    // 1. 헤더 포함 페이로드 구성 (OriginBlock 클래스 함수 호출)
                    List<uint> payloadBits = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);

                    if (payloadBits != null && payloadBits.Count > 0)
                    {
                        int width = desc.width;
                        int height = desc.height;
                        int availableCapacity = width * height; // LSB 용량 = 총 픽셀 수
                        int totalPayloadLength = payloadBits.Count; // L = Sync+Len+Data 길이

                        if (availableCapacity == 0)
                        {
                            Debug.LogWarning("[LSBRenderPass] 이미지 크기가 0이라 비트스트림 준비 불가.");
                            // finalBitsToEmbed는 이미 비어있음
                        }
                        else
                        {
                            // 2. 자가 복제(타일링) 패딩 수행
                            finalBitsToEmbed.Capacity = availableCapacity; // 메모리 미리 할당
                            int currentPosition = 0;
                            while (currentPosition < availableCapacity)
                            {
                                int remainingSpace = availableCapacity - currentPosition;
                                int countToAdd = Math.Min(totalPayloadLength, remainingSpace);
                                if (countToAdd <= 0 || totalPayloadLength == 0) break;
                                finalBitsToEmbed.AddRange(payloadBits.GetRange(0, countToAdd));
                                currentPosition += countToAdd;
                            }
                            // Debug.Log($"[LSBRenderPass] 자가 복제 패딩 완료. 최종 크기: {finalBitsToEmbed.Count} / 용량: {availableCapacity}");
                        }
                    }
                    else
                    {
                        Debug.LogError("[LSBRenderPass] 페이로드 구성 실패 (ConstructPayloadWithHeader 결과 없음).");
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
                // finalBitsToEmbed는 이미 비어있으므로 추가 작업 없음
                if (!DataManager.IsDataReady && embedActive)
                {
                    // 데이터 로딩이 아직 안 끝났을 수 있음 (경고 로깅은 선택사항)
                    // Debug.LogWarning("[LSBRenderPass] 데이터가 아직 준비되지 않아 임베딩을 건너<0xEB><0x91>니다.");
                }
            }

            // 최종 비트스트림으로 ComputeBuffer 업데이트 (데이터 없으면 버퍼 해제됨)
            UpdateBitstreamBuffer(finalBitsToEmbed);

            // --- 셰이더 파라미터 설정 ---
            if (kernelID >= 0)
            {
                int currentBitLength = finalBitsToEmbed.Count; // 실제 버퍼에 들어갈/들어간 비트 수
                                                               // ComputeBuffer가 업데이트 된 후 유효성 재확인
                bool bufferValid = bitstreamBuffer != null && bitstreamBuffer.IsValid() && bitstreamBuffer.count == currentBitLength;
                // 최종적으로 셰이더에서 임베딩을 수행할지 여부 결정
                bool shouldEmbed = embedActive && DataManager.IsDataReady && bufferValid && currentBitLength > 0;

                cmd.SetComputeTextureParam(computeShader, kernelID, "Source", sourceTextureHandle);
                cmd.SetComputeTextureParam(computeShader, kernelID, "Output", outputTextureHandle);
                cmd.SetComputeIntParam(computeShader, "Width", desc.width);
                cmd.SetComputeIntParam(computeShader, "Height", desc.height);

                if (shouldEmbed)
                {
                    cmd.SetComputeBufferParam(computeShader, kernelID, "Bitstream", bitstreamBuffer);
                    cmd.SetComputeIntParam(computeShader, "BitLength", currentBitLength);
                    cmd.SetComputeIntParam(computeShader, "Embed", 1);
                }
                else
                {
                    // 데이터 미준비, 버퍼 오류, 임베딩 비활성화 등 모든 경우 Embed=0
                    cmd.SetComputeIntParam(computeShader, "BitLength", 0);
                    cmd.SetComputeIntParam(computeShader, "Embed", 0);
                    // UpdateBitstreamBuffer(null) 이 호출되어 bitstreamBuffer가 null일 수 있음
                    // 이 경우 SetComputeBufferParam을 호출하지 않는 것이 더 안전할 수 있으나,
                    // Embed=0 이면 셰이더 내에서 접근하지 않으므로 일반적으로는 문제 없음.
                    // 만약을 위해 null 체크 후 바인딩 해제 고려 가능:
                    // if (bitstreamBuffer == null) { /* 필요시 이전 바인딩 해제 로직 */ }
                }
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (kernelID < 0) return;
            // Optional: Check buffer validity again if needed

            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
            int width = cameraTarget.rt.width;
            int height = cameraTarget.rt.height;

            cmd.CopyTexture(cameraTarget, sourceTextureHandle); // Copy source

            using (new ProfilingScope(cmd, new ProfilingSampler(profilerTag)))
            {
                int threadGroupsX = Mathf.CeilToInt((float)width / THREAD_GROUP_SIZE_X);
                int threadGroupsY = Mathf.CeilToInt((float)height / THREAD_GROUP_SIZE_Y);
                cmd.DispatchCompute(computeShader, kernelID, threadGroupsX, threadGroupsY, 1); // Dispatch
                cmd.CopyTexture(outputTextureHandle, cameraTarget); // Copy result back
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        public override void OnCameraCleanup(CommandBuffer cmd) { }
        public void Cleanup()
        { /* 이전과 동일 */
            bitstreamBuffer?.Release(); bitstreamBuffer = null;
            RTHandles.Release(sourceTextureHandle); sourceTextureHandle = null;
            RTHandles.Release(outputTextureHandle); outputTextureHandle = null;
        }
    }
}