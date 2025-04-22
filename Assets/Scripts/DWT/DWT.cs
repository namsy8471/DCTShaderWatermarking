using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.Linq;
using System;
using Unity.Collections;
using System.IO;

// OriginBlock 클래스가 동일 프로젝트 내에 정의되어 있다고 가정
// DataManager 클래스가 동일 프로젝트 내에 정의되어 있다고 가정
// SaveTrigger 클래스가 동일 프로젝트 내에 정의되어 있다고 가정
// RTResultHolder 클래스가 동일 프로젝트 내에 정의되어 있다고 가정

public class DWTRenderFeature : ScriptableRendererFeature
{
    [Header("셰이더 및 설정")]
    public ComputeShader dwtComputeShader; // DWT용 .compute 파일 할당
    [Tooltip("DWT 계수에 비트스트림을 임베딩할지 여부")]
    public bool embedBitstream = true;

    [Tooltip("Addressables에서 로드할 암호화된 데이터 키")]
    public string addressableKey = "OriginBlockData"; // DCT와 동일하게 사용하거나 필요시 변경

    // DWT 관련 설정은 Compute Shader에서 고정하거나 필요시 추가 (예: 레벨)
    // public int dwtLevel = 1; // 다중 레벨 DWT를 원할 경우

    private DWTRenderPass dwtRenderPass;

    public override void Create()
    {
        if (dwtComputeShader == null)
        {
            Debug.LogError("DWT Compute Shader가 할당되지 않았습니다.");
            return;
        }

        dwtRenderPass = new DWTRenderPass(dwtComputeShader, name, embedBitstream);
        // 렌더링 파이프라인에서 적절한 시점 선택 (AfterRenderingPostProcessing 등)
        dwtRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var camera = renderingData.cameraData.camera;
        if (camera.cameraType != CameraType.Game) { return; } // 게임 카메라만 처리

        if (dwtComputeShader != null && dwtRenderPass != null)
        {
            dwtRenderPass.SetEmbedActive(embedBitstream);
            // DataManager.IsDataReady와 같은 데이터 준비 상태 확인 로직은 DCT와 동일하게 유지
            if (DataManager.IsDataReady) // 데이터 로딩 완료 여부 확인
            {
                renderer.EnqueuePass(dwtRenderPass);
            }
            else if (embedBitstream) // 임베딩은 활성인데 데이터 준비 안됐으면 경고
            {
                Debug.LogWarning("[DWTRenderFeature] 데이터 미준비. 임베딩 패스 건너뜀.");
            }
            else // 임베딩 비활성이면 데이터 없어도 패스 추가 가능 (선택사항)
            {
                renderer.EnqueuePass(dwtRenderPass); // 임베딩 안 할 때는 그냥 실행
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        dwtRenderPass?.Cleanup();
    }

    //-------------------------------------------------------------------------
    // DWT Render Pass
    //-------------------------------------------------------------------------
    class DWTRenderPass : ScriptableRenderPass
    {
        private ComputeShader computeShader;
        private int dwtRowsKernelID, dwtColsKernelID, idwtColsKernelID, idwtRowsKernelID;
        private RTHandle sourceTextureHandle, intermediateHandle, dwtOutputHandle, idwtOutputHandle, chromaBufferHandle;
        private string profilerTag;
        private bool embedActive;
        private ComputeBuffer bitstreamBuffer;

        private List<uint> finalBitsToEmbed; // 최종 삽입될 비트 (패딩 완료)

        // DWT는 QIM Delta, u/v Index 불필요
        // 필요시 DWT 레벨 등의 파라미터 추가 가능

        private const int BLOCK_SIZE = 8; // DWT 블록 크기 (Haar는 2의 거듭제곱 필요, 8x8 유지)

        public DWTRenderPass(ComputeShader shader, string tag, bool initialEmbedState)
        {
            computeShader = shader;
            profilerTag = tag;
            embedActive = initialEmbedState;

            // 커널 이름 변경
            dwtRowsKernelID = shader.FindKernel("DWT_Pass1_Rows");
            dwtColsKernelID = shader.FindKernel("DWT_Pass2_Cols_Embed"); // 임베딩 포함 커널
            idwtColsKernelID = shader.FindKernel("IDWT_Pass1_Cols");
            idwtRowsKernelID = shader.FindKernel("IDWT_Pass2_Rows");

            finalBitsToEmbed = new List<uint>();

            // 커널 유효성 검사
            if (dwtRowsKernelID < 0 || dwtColsKernelID < 0 || idwtColsKernelID < 0 || idwtRowsKernelID < 0)
            {
                Debug.LogError($"[DWTRenderPass] 하나 이상의 DWT Compute Shader 커널을 찾을 수 없습니다. 커널 이름을 확인하세요: DWT_Pass1_Rows, DWT_Pass2_Cols_Embed, IDWT_Pass1_Cols, IDWT_Pass2_Rows");
            }
        }

        public void SetEmbedActive(bool isActive) { embedActive = isActive; }

        // UpdateBitstreamBuffer는 DCT와 동일하게 사용 가능
        private void UpdateBitstreamBuffer(List<uint> data)
        {
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
                    // DWT 임베딩은 보통 StructuredBuffer 사용
                    bitstreamBuffer = new ComputeBuffer(count, sizeof(uint), ComputeBufferType.Structured);
                    Debug.Log($"[DWTRenderPass] ComputeBuffer 생성됨 (Count: {count})");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[DWTRenderPass] ComputeBuffer 생성 실패: {ex.Message}");
                    bitstreamBuffer = null;
                    return;
                }
            }

            try
            {
                bitstreamBuffer.SetData(data);
                Debug.Log($"[DWTRenderPass] ComputeBuffer 데이터 설정 완료 (Count: {count})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DWTRenderPass] ComputeBuffer SetData 오류: {ex.Message}");
                bitstreamBuffer?.Release();
                bitstreamBuffer = null;
            }
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.sRGB = false; // Linear 작업 공간 가정

            // RTHandle 할당 (DCT와 유사하게, 포맷 확인)
            var intermediateDesc = desc;
            intermediateDesc.colorFormat = RenderTextureFormat.RFloat; // Y 채널용 Float
            intermediateDesc.enableRandomWrite = true;

            var dwtDesc = desc;
            dwtDesc.colorFormat = RenderTextureFormat.RFloat; // DWT 계수용 Float
            dwtDesc.enableRandomWrite = true;


            var chromaDesc = desc; // CbCr 저장용
            chromaDesc.colorFormat = RenderTextureFormat.RGFloat; // CbCr용 float2
            chromaDesc.enableRandomWrite = true;

            var idwtDesc = desc; // 최종 결과용
            idwtDesc.enableRandomWrite = true;
            // idwtDesc.colorFormat = RenderTextureFormat.ARGBFloat; // 최종 결과 포맷 확인


            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, name: "_SourceCopyForDWT");
            RenderingUtils.ReAllocateIfNeeded(ref intermediateHandle, intermediateDesc, FilterMode.Point, name: "_IntermediateDWT_IDWT"); // 중간 Y'
            RenderingUtils.ReAllocateIfNeeded(ref dwtOutputHandle, dwtDesc, FilterMode.Point, name: "_DWTOutput"); // 최종 Y DWT 계수
            RenderingUtils.ReAllocateIfNeeded(ref chromaBufferHandle, chromaDesc, FilterMode.Point, name: "_ChromaBufferCbCr"); // CbCr 저장
            RenderingUtils.ReAllocateIfNeeded(ref idwtOutputHandle, idwtDesc, FilterMode.Point, name: "_IDWTOutput"); // 최종 RGB 결과

            // --- 비트스트림 준비 (DCT와 동일 로직 사용) ---
            finalBitsToEmbed = finalBitsToEmbed ?? new List<uint>();
            finalBitsToEmbed.Clear();

            if (embedActive && DataManager.IsDataReady && DataManager.EncryptedOriginData != null)
            {
                try
                {
                    List<uint> currentPayload = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);

                    if (currentPayload == null || currentPayload.Count == 0)
                    {
                        Debug.LogWarning("[DWTRenderPass] 원본 페이로드 구성 실패 또는 데이터 없음.");
                    }
                    else
                    {
                        // 패딩 로직 (DCT와 동일: 블록 개수만큼 용량 계산)
                        int width = desc.width;
                        int height = desc.height;
                        // 정수 나눗셈 확인: width/BLOCK_SIZE
                        int numBlocksX = Mathf.Max(1, width / BLOCK_SIZE); // 최소 1개 블록
                        int numBlocksY = Mathf.Max(1, height / BLOCK_SIZE); // 최소 1개 블록
                        int availableCapacity = numBlocksX * numBlocksY; // 각 블록당 1비트 가정
                        int totalPayloadLength = currentPayload.Count;

                        Debug.Log($"[DWTRenderPass] 이미지 크기: {width}x{height}, 블록 크기: {BLOCK_SIZE}, 총 블록 수: {availableCapacity}, 원본 페이로드 길이: {totalPayloadLength}");


                        if (availableCapacity == 0)
                        {
                            Debug.LogWarning("[DWTRenderPass] 이미지 크기가 작아 블록 생성 불가.");
                        }
                        else if (totalPayloadLength == 0)
                        {
                            Debug.LogWarning("[DWTRenderPass] 원본 페이로드 길이가 0입니다.");
                        }
                        else
                        {
                            finalBitsToEmbed.Clear();
                            finalBitsToEmbed.Capacity = availableCapacity; // 필요한 용량만큼 확보 시도
                            int currentPosition = 0;
                            while (currentPosition < availableCapacity)
                            {
                                int remainingSpace = availableCapacity - currentPosition;
                                int countToAdd = Math.Min(totalPayloadLength, remainingSpace);
                                if (countToAdd <= 0) break;

                                // currentPayload에서 필요한 만큼 가져오기
                                finalBitsToEmbed.AddRange(currentPayload.GetRange(0, countToAdd));
                                currentPosition += countToAdd;

                                // 만약 페이로드를 반복해서 채워야 한다면, GetRange 인덱스 조절 필요
                                // 여기서는 페이로드가 용량보다 작으면 그대로 두고, 크면 잘라서 넣음
                            }
                            // 용량보다 페이로드가 작으면 나머지는 0으로 채울 수도 있음 (선택 사항)
                            // while (finalBitsToEmbed.Count < availableCapacity) { finalBitsToEmbed.Add(0); }

                            Debug.Log($"[DWTRenderPass] 패딩/절단 완료. 최종 비트 수: {finalBitsToEmbed.Count} (용량: {availableCapacity})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{profilerTag}] 페이로드 구성 또는 패딩 중 오류: {ex.Message}\n{ex.StackTrace}");
                    finalBitsToEmbed.Clear();
                }
            }
            else
            {
                if (!DataManager.IsDataReady && embedActive)
                {
                    // Debug.LogWarning("[DWTRenderPass] 데이터 미준비 또는 임베딩 비활성화. 페이로드 비어 있음.");
                }
                // finalBitsToEmbed는 이미 비어있는 상태
            }


            // --- 디버깅 로그 (DCT와 동일) ---
            if (finalBitsToEmbed != null && finalBitsToEmbed.Count > 0)
            {
                int logLength = Math.Min(finalBitsToEmbed.Count, 64); // 로그 출력 길이 조절
                string firstBits = string.Join("", finalBitsToEmbed.Take(logLength).Select(b => b.ToString()));
                Debug.Log($"[DWTRenderPass] Shader로 전달될 최종 비트 (처음 {logLength}개): {firstBits}");
                Debug.Log($"[DWTRenderPass] 최종 비트 총 길이 (BitLength): {finalBitsToEmbed.Count}");
            }
            else
            {
                Debug.LogWarning("[DWTRenderPass] finalBitsToEmbed가 비어있음. 셰이더로 전달될 페이로드 없음.");
            }

            // 최종 비트스트림으로 ComputeBuffer 업데이트
            UpdateBitstreamBuffer(finalBitsToEmbed);

            // --- 셰이더 파라미터 설정 ---
            int currentBitLength = finalBitsToEmbed.Count;
            bool bufferValid = bitstreamBuffer != null && bitstreamBuffer.IsValid() && bitstreamBuffer.count == currentBitLength;

            // 최종 임베딩 조건: 활성화 상태, 데이터 준비 완료, 버퍼 유효, 비트 길이 > 0
            bool shouldEmbed = embedActive && DataManager.IsDataReady && bufferValid && currentBitLength > 0;

            // Debug.Log($"[DWTRenderPass] 최종 비트스트림 길이: {currentBitLength} / 버퍼 유효성: {bufferValid} / 임베딩 활성: {embedActive} / 데이터 준비: {DataManager.IsDataReady} => 최종 임베딩 여부: {shouldEmbed}");


            // 공통 파라미터 설정
            cmd.SetComputeIntParam(computeShader, "Width", desc.width);
            cmd.SetComputeIntParam(computeShader, "Height", desc.height);

            // 각 커널에 필요한 텍스처 및 버퍼 바인딩
            if (dwtRowsKernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, dwtRowsKernelID, "Source", sourceTextureHandle);
                cmd.SetComputeTextureParam(computeShader, dwtRowsKernelID, "IntermediateYBuffer", intermediateHandle); // Pass 1 출력 Y'
                cmd.SetComputeTextureParam(computeShader, dwtRowsKernelID, "ChromaBuffer", chromaBufferHandle);      // Pass 1 출력 CbCr
            }
            if (dwtColsKernelID >= 0) // DWT Pass 2 + Embed
            {
                cmd.SetComputeTextureParam(computeShader, dwtColsKernelID, "IntermediateYBuffer", intermediateHandle); // Pass 2 입력 Y'
                cmd.SetComputeTextureParam(computeShader, dwtColsKernelID, "DWTOutputY", dwtOutputHandle);          // Pass 2 출력 Y DWT 계수

                // 임베딩 관련 파라미터 설정
                cmd.SetComputeIntParam(computeShader, "Embed", shouldEmbed ? 1 : 0);
                if (shouldEmbed) // 실제 임베딩할 때만 버퍼 설정
                {
                    cmd.SetComputeIntParam(computeShader, "BitLength", currentBitLength);
                    // 버퍼가 유효할 때만 바인딩 시도
                    if (bufferValid)
                    {
                        cmd.SetComputeBufferParam(computeShader, dwtColsKernelID, "Bitstream", bitstreamBuffer);
                        Debug.Log($"[DWTRenderPass] DWT Pass 2: Bitstream Buffer 설정 (Count: {currentBitLength})");
                    }
                    else
                    {
                        Debug.LogWarning($"[DWTRenderPass] DWT Pass 2: Bitstream Buffer 유효하지 않음 (Count: {currentBitLength}, IsValid: {bufferValid}). 임베딩 건너뛸 수 있음.");
                        // 만약을 위해 Embed 플래그를 0으로 설정할 수도 있음
                        // cmd.SetComputeIntParam(computeShader, "Embed", 0);
                    }
                }
                else
                {
                    cmd.SetComputeIntParam(computeShader, "BitLength", 0); // 임베딩 안 할 시 길이 0
                    // Debug.LogWarning($"[DWTRenderPass] DWT Pass 2: 임베딩 비활성화 또는 조건 미충족. Bitstream 설정 건너뜀.");
                }
            }
            if (idwtColsKernelID >= 0) // IDWT Pass 1
            {
                cmd.SetComputeTextureParam(computeShader, idwtColsKernelID, "DWTOutputY", dwtOutputHandle);          // 입력: Y DWT 계수
                cmd.SetComputeTextureParam(computeShader, idwtColsKernelID, "IntermediateYBuffer", intermediateHandle); // 출력: 중간 Y'
            }
            if (idwtRowsKernelID >= 0) // IDWT Pass 2
            {
                cmd.SetComputeTextureParam(computeShader, idwtRowsKernelID, "IntermediateYBuffer", intermediateHandle); // 입력: 중간 Y'
                cmd.SetComputeTextureParam(computeShader, idwtRowsKernelID, "ChromaBuffer", chromaBufferHandle);      // 입력: 원본 CbCr
                cmd.SetComputeTextureParam(computeShader, idwtRowsKernelID, "IDWTOutput", idwtOutputHandle);        // 출력: 최종 RGB
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            bool kernelsValid = dwtRowsKernelID >= 0 && dwtColsKernelID >= 0 && idwtColsKernelID >= 0 && idwtRowsKernelID >= 0;
            if (!kernelsValid)
            {
                Debug.LogError("[DWTRenderPass] 유효하지 않은 커널 ID로 인해 Execute 중단.");
                return;
            }

            // 임베딩 활성 상태인데 필요한 데이터/버퍼가 없으면 실행 전 경고 또는 중단 (선택적)
            bool canProceed = true;
            if (embedActive)
            {
                bool isDataReady = DataManager.IsDataReady;
                bool isBufferReady = bitstreamBuffer != null && bitstreamBuffer.IsValid() && finalBitsToEmbed.Count > 0 && bitstreamBuffer.count == finalBitsToEmbed.Count;

                if (!isDataReady)
                {
                    // Debug.LogWarning("[DWTRenderPass Execute] 임베딩 활성 상태이나 DataManager 미준비.");
                    // canProceed = false; // 데이터 없으면 아예 실행 안 함
                }
                if (!isBufferReady)
                {
                    Debug.LogWarning($"[DWTRenderPass Execute] 임베딩 활성 상태이나 ComputeBuffer 준비 안됨 (Buffer: {bitstreamBuffer != null}, Valid: {bitstreamBuffer?.IsValid()}, Count Match: {bitstreamBuffer?.count == finalBitsToEmbed.Count}).");
                    // canProceed = false; // 버퍼 문제 시 실행 안 함 (셰이더에서 Embed=0 처리하므로 계속 진행해도 될 수 있음)
                }
            }

            if (!canProceed)
            {
                // Debug.LogWarning("[DWTRenderPass] 필수 조건 미충족으로 Execute 건너<0xEB><0x91>.");
                // 만약 그냥 원본을 출력하고 싶다면 Blit만 수행
                // CommandBuffer cmdSkip = CommandBufferPool.Get(profilerTag + "_Skip");
                // Blit(renderingData.cameraData.renderer.cameraColorTargetHandle, renderingData.cameraData.renderer.cameraColorTargetHandle);
                // context.ExecuteCommandBuffer(cmdSkip);
                // CommandBufferPool.Release(cmdSkip);
                return;
            }


            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;

            // RTHandle 유효성 검사 추가
            if (sourceTextureHandle == null || intermediateHandle == null || dwtOutputHandle == null || idwtOutputHandle == null || chromaBufferHandle == null || cameraTarget == null)
            {
                Debug.LogError("[DWTRenderPass] 하나 이상의 RTHandle이 유효하지 않습니다. Execute 중단.");
                CommandBufferPool.Release(cmd);
                return;
            }
            // 렌더 타겟 크기 확인 (0이면 Dispatch 불가)
            int width = cameraTarget.rt?.width ?? renderingData.cameraData.cameraTargetDescriptor.width;
            int height = cameraTarget.rt?.height ?? renderingData.cameraData.cameraTargetDescriptor.height;

            if (width <= 0 || height <= 0)
            {
                Debug.LogError($"[DWTRenderPass] 유효하지 않은 렌더 타겟 크기 ({width}x{height}). Execute 중단.");
                CommandBufferPool.Release(cmd);
                return;
            }


            // 스레드 그룹 계산 (DCT와 동일)
            // Mathf.CeilToInt 대신 정수 나눗셈 올림 사용: (N + M - 1) / M
            int threadGroupsX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int threadGroupsY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;

            // Check for zero thread groups
            if (threadGroupsX <= 0 || threadGroupsY <= 0)
            {
                Debug.LogError($"[DWTRenderPass] 계산된 스레드 그룹 수가 0 이하입니다. (X: {threadGroupsX}, Y: {threadGroupsY}). Dispatch 불가.");
                CommandBufferPool.Release(cmd);
                return;
            }


            // 1. 원본 텍스처 복사
            cmd.Blit(cameraTarget, sourceTextureHandle);

            using (new ProfilingScope(cmd, new ProfilingSampler(profilerTag)))
            {
                // 2. DWT 수행 (Rows -> Cols + Embed)
                cmd.DispatchCompute(computeShader, dwtRowsKernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, dwtColsKernelID, threadGroupsX, threadGroupsY, 1);

                // 3. IDWT 수행 (Cols -> Rows + Combine)
                cmd.DispatchCompute(computeShader, idwtColsKernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, idwtRowsKernelID, threadGroupsX, threadGroupsY, 1);

                // 4. 최종 결과를 카메라 타겟으로 복사
                cmd.Blit(idwtOutputHandle, cameraTarget);

                // 스크린샷 결과 저장을 위한 타겟 설정
                RTResultHolder.DedicatedSaveTarget = cameraTarget; // 최종 결과가 쓰인 타겟
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }


        public override void OnCameraCleanup(CommandBuffer cmd) { }

        public void Cleanup()
        {
            bitstreamBuffer?.Release(); bitstreamBuffer = null;
            RTHandles.Release(sourceTextureHandle); sourceTextureHandle = null;
            RTHandles.Release(intermediateHandle); intermediateHandle = null;
            RTHandles.Release(dwtOutputHandle); dwtOutputHandle = null;
            RTHandles.Release(idwtOutputHandle); idwtOutputHandle = null;
            RTHandles.Release(chromaBufferHandle); chromaBufferHandle = null;
        }
    }
}