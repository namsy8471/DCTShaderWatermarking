using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.Linq;
using System;
using Unity.Collections;
using System.IO; // Math.Min 사용

// OriginBlock 클래스가 동일 프로젝트 내에 정의되어 있다고 가정

public class DCTRenderFeature_Optimized : ScriptableRendererFeature
{
    [Header("셰이더 및 설정")]
    public ComputeShader dctComputeShader; // DCT용 최적화된 .compute 파일 할당
    [Tooltip("DCT 계수에 비트스트림을 임베딩할지 여부")]
    public bool embedBitstream = true;

    [Tooltip("Addressables에서 로드할 암호화된 데이터 키")]
    public string addressableKey = "OriginBlockData";
    [Header("DCT 설정")]
    [Tooltip("QIM 스텝 크기")]
    public float qimDelta = 0.05f;
    [Tooltip("UV")]
    public int uIndex = 4;
    public int vIndex = 4;


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
            dctRenderPass.SetuvIndex(uIndex, vIndex);
            if (DataManager.IsDataReady)
            {
                renderer.EnqueuePass(dctRenderPass);
            }
        }
    }

    protected override void Dispose(bool disposing) { dctRenderPass?.Cleanup(); }

    class DCTRenderPass_Optimized : ScriptableRenderPass
    { 

        private ComputeShader computeShader;
        private int dctPass1KernelID, dctPass2KernelID, idctPass1KernelID, idctPass2KernelID;
        private RTHandle sourceTextureHandle, intermediateHandle, dctOutputHandle, idctOutputHandle, chromaBufferHandle;
        private string profilerTag;
        private bool embedActive;
        private ComputeBuffer bitstreamBuffer;

        private List<uint> payloadBits; // 헤더 포함, 패딩 전 원본 페이로드
        private List<uint> finalBitsToEmbed; // 최종 삽입될 비트 (패딩 완료)

        private bool isReadbackPending = false;

        private float qimDelta; // QIM 스텝 크기 (셰이더에서 사용됨)
        private int uIndex; // u 인덱스
        private int vIndex; // v 인덱스

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

        }

        public void SetEmbedActive(bool isActive) { embedActive = isActive; }
        public void SetuvIndex(int u, int v) { uIndex = u; vIndex = v; }

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
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;

            // RTHandle 할당 (기존과 동일)
            var intermediateDesc = desc; 
            intermediateDesc.colorFormat = RenderTextureFormat.RFloat;
            intermediateDesc.sRGB = false; 
            intermediateDesc.enableRandomWrite = true;

            var chromaDesc = desc; // CbCr 저장용
            chromaDesc.colorFormat = RenderTextureFormat.RGFloat; // ★★★ CbCr은 float2 (RG32_SFloat) ★★★
            chromaDesc.sRGB = false;
            chromaDesc.enableRandomWrite = true; // Pass1에서 쓰기 위함

            var idctDesc = desc;
            idctDesc.enableRandomWrite = true;

            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, name: "_SourceCopyForDCT");
            RenderingUtils.ReAllocateIfNeeded(ref intermediateHandle, intermediateDesc, FilterMode.Point, name: "_IntermediateDCT_IDCT");
            RenderingUtils.ReAllocateIfNeeded(ref dctOutputHandle, intermediateDesc, FilterMode.Point, name: "_DCTOutput");
            RenderingUtils.ReAllocateIfNeeded(ref chromaBufferHandle, chromaDesc, FilterMode.Point, name: "_ChromaBufferCbCr"); // ★★★ ChromaBuffer 할당 ★★★
            RenderingUtils.ReAllocateIfNeeded(ref idctOutputHandle, idctDesc, FilterMode.Point, name: "_IDCTOutput");

            // --- 비트스트림 준비 (비동기 로딩 확인 및 타일링 패딩 적용) ---

            // 최종 삽입될 비트 리스트 초기화
            finalBitsToEmbed = finalBitsToEmbed ?? new List<uint>(); // Null이면 새로 생성
            finalBitsToEmbed.Clear();

            // embedActive 플래그가 활성화 되어 있고, DataManager를 통해 데이터 로딩이 완료되었는지 확인
            // <<< 변경/추가된 부분 시작 >>>
            if (embedActive && DataManager.IsDataReady && DataManager.EncryptedOriginData != null)
            {
                // OnCameraSetup 내부, if (embedActive && DataManager.IsDataReady...) 블록 안
                try
                {
                    // 1. DataManager에서 직접 원본 페이로드 구성
                    List<uint> currentPayload = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);

                    // 2. 구성 결과 확인
                    if (currentPayload == null || currentPayload.Count == 0)
                    {
                        Debug.LogWarning("[DCTRenderPass] 원본 페이로드 구성 실패 또는 데이터 없음.");
                        // finalBitsToEmbed는 이미 Clear()된 상태이므로 더 할 것 없음
                    }
                    else
                    {
                        // 3. 패딩 로직 수행 (currentPayload를 원본으로 사용)
                        int width = desc.width;
                        int height = desc.height;
                        int numBlocksX = width / BLOCK_SIZE;
                        int numBlocksY = height / BLOCK_SIZE;
                        int availableCapacity = numBlocksX * numBlocksY;
                        int totalPayloadLength = currentPayload.Count; // <- currentPayload 사용

                        if (availableCapacity == 0)
                        {
                            Debug.LogWarning("[DCTRenderPass] 이미지 크기가 작아 블록 생성 불가.");
                        }
                        else
                        {
                            finalBitsToEmbed.Clear(); // 패딩 전에 확실히 비우기
                            finalBitsToEmbed.Capacity = availableCapacity;
                            int currentPosition = 0;
                            while (currentPosition < availableCapacity)
                            {
                                int remainingSpace = availableCapacity - currentPosition;
                                int countToAdd = Math.Min(totalPayloadLength, remainingSpace);
                                if (countToAdd <= 0) break; // totalPayloadLength가 0인 경우 포함
                                finalBitsToEmbed.AddRange(currentPayload.GetRange(0, countToAdd)); // <- currentPayload 사용
                                currentPosition += countToAdd;
                            }
                            // Debug.Log($"[DCTRenderPass] 패딩 완료. 최종 크기: {finalBitsToEmbed.Count}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{profilerTag}] 페이로드 구성 또는 패딩 중 오류: {ex.Message}");
                    finalBitsToEmbed.Clear(); // 오류 시 확실히 비움
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

            // --- ★★★ 디버깅 로그 추가: 최종 페이로드 앞부분 출력 ★★★ ---
            if (finalBitsToEmbed != null && finalBitsToEmbed.Count > 0)
            {
                int logLength = Math.Min(finalBitsToEmbed.Count, 60); // 최대 60개 비트 출력
                // Linq 사용 (파일 상단에 using System.Linq; 추가 필요)
                string first50Bits = string.Join("", finalBitsToEmbed.Take(logLength).Select(b => b.ToString()));
                // 콘솔에 처음 50개 비트와 총 길이 출력
                Debug.Log($"[DCTRenderPass] Shader로 전달될 최종 비트 (처음 {logLength}개): {first50Bits}");
                Debug.Log($"[DCTRenderPass] 최종 비트 총 길이 (currentBitLength): {finalBitsToEmbed.Count}");
            }
            else
            {
                // finalBitsToEmbed가 비어있는 경우 로그
                Debug.LogWarning("[DCTRenderPass] finalBitsToEmbed가 비어있음. 셰이더로 전달될 페이로드 없음.");
            }
            // --- ★★★ 디버깅 로그 끝 ★★★ ---

            // 최종 비트스트림으로 ComputeBuffer 업데이트 (데이터 없으면 버퍼 해제됨)
            UpdateBitstreamBuffer(finalBitsToEmbed);

            // --- 셰이더 파라미터 설정 ---
            int currentBitLength = finalBitsToEmbed.Count;
            bool bufferValid = bitstreamBuffer != null && bitstreamBuffer.IsValid() && bitstreamBuffer.count == currentBitLength;
            // 최종 임베딩 조건 확인
            bool shouldEmbed = embedActive && DataManager.IsDataReady && bufferValid && currentBitLength > 0;
            Debug.Log($"[DCTRenderPass] 최종 비트스트림 길이: {currentBitLength} / 유효성: {bufferValid}");

            // 공통 파라미터 (모든 커널에 설정 필요할 수 있음 - 확인 필요)
            cmd.SetComputeIntParam(computeShader, "Width", desc.width);
            cmd.SetComputeIntParam(computeShader, "Height", desc.height);
            cmd.SetComputeIntParam(computeShader, "uIndex", uIndex);
            cmd.SetComputeIntParam(computeShader, "vIndex", vIndex);
            cmd.SetComputeFloatParam(computeShader, "QIM_DELTA", qimDelta);

            // 각 커널 텍스처 바인딩 (기존과 동일)
            if (dctPass1KernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, dctPass1KernelID, "Source", sourceTextureHandle);
                cmd.SetComputeTextureParam(computeShader, dctPass1KernelID, "IntermediateBuffer", intermediateHandle);
                cmd.SetComputeTextureParam(computeShader, dctPass1KernelID, "ChromaBuffer", chromaBufferHandle);     // CbCr 출력
            }
            if (dctPass2KernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, dctPass2KernelID, "IntermediateBuffer", intermediateHandle);
                cmd.SetComputeTextureParam(computeShader, dctPass2KernelID, "DCTOutput", dctOutputHandle);

                // Bitstream 버퍼 설정은 유효할 때만 (shouldEmbed 조건 대신 bufferValid 사용)
                if (bufferValid && currentBitLength > 0) // 버퍼가 실제로 유효할 때만 바인딩 시도
                {
                    cmd.SetComputeBufferParam(computeShader, dctPass2KernelID, "Bitstream", bitstreamBuffer);
                }
                // Embed 관련 파라미터는 항상 설정 (셰이더가 Embed 값 보고 처리하도록)
                cmd.SetComputeIntParam(computeShader, "BitLength", shouldEmbed ? currentBitLength : 0);
                cmd.SetComputeIntParam(computeShader, "Embed", shouldEmbed ? 1 : 0);

                if (shouldEmbed) Debug.Log($"[DCTRenderPass] Setting Bitstream Buffer (Count: {currentBitLength})");
                else Debug.LogWarning($"[DCTRenderPass] SKIPPING Bitstream Buffer setting (shouldEmbed: {shouldEmbed})");
            }
            if (idctPass1KernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, idctPass1KernelID, "DCTOutput", dctOutputHandle);
                cmd.SetComputeTextureParam(computeShader, idctPass1KernelID, "IntermediateBuffer", intermediateHandle);
            }
            if (idctPass2KernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, idctPass2KernelID, "IntermediateBuffer", intermediateHandle);
                cmd.SetComputeTextureParam(computeShader, idctPass2KernelID, "ChromaBuffer", chromaBufferHandle);     // 원본 CbCr 입력
                cmd.SetComputeTextureParam(computeShader, idctPass2KernelID, "IDCTOutput", idctOutputHandle);
            }


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

            cmd.Blit(cameraTarget, sourceTextureHandle); // Copy source

            using (new ProfilingScope(cmd, new ProfilingSampler(profilerTag)))
            {
                cmd.DispatchCompute(computeShader, dctPass1KernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, dctPass2KernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, idctPass1KernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, idctPass2KernelID, threadGroupsX, threadGroupsY, 1);
                cmd.Blit(idctOutputHandle,  cameraTarget);
            }

            if (SaveTrigger.SaveRequested && !isReadbackPending)
            {
                isReadbackPending = true; // 중복 요청 방지 (이 패스 인스턴스 내에서)
                SaveTrigger.SaveRequested = false; // 요청 플래그 즉시 리셋 (다른 프레임에서 처리 못하게)

                Debug.Log("RenderPass starting AsyncGPUReadback Request...");

                // ★ 최종 결과가 담긴 cameraTarget 또는 idctOutputHandle 사용 ★
                // 어떤 것을 읽을지는 최종 쓰기 작업에 따라 결정
                // 여기서는 cameraTarget을 읽는다고 가정 (CopyTexture/Blit 이후)
                var targetToRead = cameraTarget.rt;
                // 또는 var targetToRead = idctOutputHandle.rt; (Copy/Blit 하기 전 핸들)

                if (targetToRead != null && targetToRead.IsCreated())
                {
                    // 요청 시 포맷은 반드시 Float 계열로!
                    // ★ 콜백 함수는 static 또는 싱글턴 객체의 메서드여야 함 ★
                    AsyncGPUReadback.Request(targetToRead, 0, TextureFormat.RGBAFloat, OnCompleteReadback_Static);
                }
                else
                {
                    Debug.LogError("Async Readback source texture is invalid!");
                    isReadbackPending = false; // 실패 시 플래그 리셋
                }
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        // --- ★★★ Static 콜백 함수 (이전과 동일, 클래스 내부에 static으로 선언) ★★★ ---
        // (주의: 이 함수는 Render Pass 인스턴스 변수에 직접 접근 불가)
        private static bool staticIsCallbackProcessing = false; // 콜백 동시 처리 방지
        static void OnCompleteReadback_Static(AsyncGPUReadbackRequest request)
        {
            // 간단한 락으로 동시 처리 방지
            if (staticIsCallbackProcessing)
            {
                Debug.LogWarning("Previous readback callback still processing.");
                return;
            }
            staticIsCallbackProcessing = true;

            Debug.Log("Static Async GPU Readback 완료.");
            if (request.hasError) { Debug.LogError("GPU Readback 실패!"); }
            else if (request.done) // 완료되었는지 다시 확인
            {
                // Texture2D 생성 (RGBAFloat)
                Texture2D texture = new Texture2D(request.width, request.height, TextureFormat.RGBAFloat, false);
                try
                {
                    NativeArray<float> data = request.GetData<float>();
                    if (data.Length > 0 && data.Length == request.width * request.height * 4)
                    {
                        texture.SetPixelData(data, 0);
                        texture.Apply(false);

                        byte[] bytes = texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat);
                        // 파일 이름은 static 변수에서 가져옴
                        string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), SaveTrigger.SaveFileName);
                        File.WriteAllBytes(savePath, bytes);
                        Debug.Log($"이미지 저장 성공 (Static CB): {savePath}");
                    }
                    else { Debug.LogError($"Readback data size mismatch! Got {data.Length}"); }
                }
                catch (Exception e) { Debug.LogError($"데이터 처리/저장 실패 (Static CB): {e.Message}\n{e.StackTrace}"); }
                finally
                {
                    if (texture != null) Destroy(texture); // Texture2D 정리
                }
            }
            // 이 패스 인스턴스의 isReadbackPending을 여기서 false로 바꿀 수 없음 (static이므로)
            // 따라서 InputController에서 SaveRequested를 다시 true로 만들기 전에 약간의 딜레이가 필요할 수 있음
            // 혹은 콜백 완료 이벤트 같은 것을 구현
            staticIsCallbackProcessing = false; // 처리 완료
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