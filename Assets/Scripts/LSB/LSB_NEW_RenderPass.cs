using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.Linq;
using System;
using Unity.Collections;
using System.IO; // Math.Min 사용

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

    public override void Create()
    {
        if (lsbComputeShader == null) { /* 오류 처리 */ return; }

        lsbRenderPass = new LSBRenderPass(lsbComputeShader, name, embedBitstream);
        lsbRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var camera = renderingData.cameraData.camera;
        if (camera.cameraType != CameraType.Game) { return; } // 게임 카메라가 아닐 경우 패스

        if (lsbComputeShader != null && lsbRenderPass != null)
        {
            lsbRenderPass.SetEmbedActive(embedBitstream);
            if (DataManager.IsDataReady)
            {
                renderer.EnqueuePass(lsbRenderPass);
            }
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
        private List<uint> payloadBits; // 헤더 포함, 패딩 전 원본 페이로드
        private List<uint> finalBitsToEmbed; // 최종 삽입될 비트 (패딩 완료)

        private bool isReadbackPending = false;

        private const int THREAD_GROUP_SIZE_X = 8;
        private const int THREAD_GROUP_SIZE_Y = 8;

        public LSBRenderPass(ComputeShader shader, string tag, bool initialEmbedState)
        {
            computeShader = shader; profilerTag = tag; embedActive = initialEmbedState;
            kernelID = computeShader.FindKernel("LSBEmbedKernel");
            if (kernelID < 0) Debug.LogError("Kernel LSBEmbedKernel 찾기 실패");

            // 초기 페이로드 구성 (헤더+데이터)
            payloadBits = new List<uint>(); // 초기화
            finalBitsToEmbed = new List<uint>();
        }

        public void SetEmbedActive(bool isActive) { embedActive = isActive; }

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

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0; 
            desc.msaaSamples = 1;
            desc.sRGB = false; // sRGB 비활성화

            var outputDesc = desc;
            outputDesc.enableRandomWrite = true;

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
                        int availableCapacity = width * height;
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
                    Debug.LogWarning("[LSBRenderPass] 데이터가 아직 준비되지 않아 임베딩을 건너<0xEB><0x91>니다.");
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

                // Bitstream 버퍼 설정은 유효할 때만 (shouldEmbed 조건 대신 bufferValid 사용)
                if (bufferValid && currentBitLength > 0) // 버퍼가 실제로 유효할 때만 바인딩 시도
                {
                    cmd.SetComputeBufferParam(computeShader, kernelID, "Bitstream", bitstreamBuffer);
                }
                // Embed 관련 파라미터는 항상 설정 (셰이더가 Embed 값 보고 처리하도록)
                cmd.SetComputeIntParam(computeShader, "BitLength", shouldEmbed ? currentBitLength : 0);
                cmd.SetComputeIntParam(computeShader, "Embed", shouldEmbed ? 1 : 0);
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (kernelID < 0) return;
            // Optional: Check buffer validity again if needed
            if (embedActive && (bitstreamBuffer == null || !bitstreamBuffer.IsValid()) || !DataManager.IsDataReady)
            {
                Debug.LogWarning("[LSBRenderPass] Embed 활성화 상태이나 ComputeBuffer 유효하지 않음. 혹은 데이터가 로딩 되지 않음");
                return; // 선택적: 실행 중단
            }


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

                RTResultHolder.DedicatedSaveTarget = cameraTarget;
            }

            // Execute 메서드 내 AsyncGPUReadback 요청 부분 수정
            if (SaveTrigger.SaveRequested && !isReadbackPending)
            {
                isReadbackPending = true;
                SaveTrigger.SaveRequested = false;

                Debug.Log("[LSBRenderPass] Starting AsyncGPUReadback Request (Requesting RGBA32)..."); // 로그 수정

                var targetToRead = RTResultHolder.DedicatedSaveTarget.rt;

                if (targetToRead != null && targetToRead.IsCreated())
                {
                    // ★ 요청 포맷을 TextureFormat.RGBA32 로 변경 ★
                    // ★ 콜백 함수도 RGBA32 처리용으로 변경 ★
                    AsyncGPUReadback.Request(targetToRead, 0, TextureFormat.RGBA32, OnCompleteReadback_RGBA32_Static);
                }
                else
                {
                    Debug.LogError("[LSBRenderPass] Async Readback source texture is invalid!");
                    isReadbackPending = false;
                }
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private static bool staticIsCallbackProcessing_TGA = false; // TGA 처리용 락 변수
        // --- ★★★ RGBA32 Readback 후 TGA 저장 콜백 함수 ★★★ ---
        static void OnCompleteReadback_RGBA32_Static(AsyncGPUReadbackRequest request)
        {
            // 락 처리
            if (staticIsCallbackProcessing_TGA)
            {
                Debug.LogWarning("[LSBRenderPass] Previous TGA readback callback still processing.");
                return;
            }
            staticIsCallbackProcessing_TGA = true;

            Debug.Log("[LSBRenderPass] Static Async GPU Readback (RGBA32 for TGA) 완료. TGA 저장 시도..."); // 로그 수정

            // 요청 상태 확인 (에러, 완료 여부)
            if (request.hasError || !request.done)
            {
                Debug.LogError($"[LSBRenderPass] GPU Readback (RGBA32 for TGA) 실패! HasError={request.hasError}, IsDone={request.done}");
                staticIsCallbackProcessing_TGA = false;
                return;
            }

            // --- 데이터 읽기 (Byte) ---
            NativeArray<byte> byteData = request.GetData<byte>();
            int width = request.width;
            int height = request.height;
            int expectedByteLength = width * height * 4; // RGBA32 = 4 bytes per pixel

            if (byteData.Length != expectedByteLength)
            {
                Debug.LogError($"[LSBRenderPass] Readback RGBA32 data size mismatch! Expected: {expectedByteLength}, Got: {byteData.Length}");
                staticIsCallbackProcessing_TGA = false;
                return;
            }
            Debug.Log($"[LSBRenderPass] RGBA32 data read successfully ({byteData.Length} bytes) for TGA saving.");

            // --- TGA 저장 로직 ---
            Texture2D texForTga = null; // try-finally 위해 미리 선언
            try
            {
                // 1. Texture2D 생성 (RGBA32 포맷)
                texForTga = new Texture2D(width, height, TextureFormat.RGBA32, false);

                // 2. 읽어온 바이트 데이터 로드
                texForTga.LoadRawTextureData(byteData);
                texForTga.Apply(false); // 변경사항 적용 (mipmap 생성 안 함)

                // 3. TGA 바이트 배열로 인코딩
                byte[] tgaBytes = texForTga.EncodeToTGA();
                if (tgaBytes == null)
                {
                    // EncodeToTGA는 실패 시 null 반환 가능성 있음 (문서상 명확하진 않으나 방어 코드)
                    throw new Exception("Texture2D.EncodeToTGA() failed, returned null.");
                }

                // 4. 파일 경로 설정 및 저장
                // SaveTrigger.SaveFileName 은 파일명.확장자 형태라고 가정 (예: "MyImage.png")
                string baseName = Path.GetFileNameWithoutExtension(SaveTrigger.SaveFileName); // 예: "MyImage"
                string tgaFileName = baseName + "_LSB.tga"; // ★★★ 파일명 및 확장자 변경 ★★★
                string tgaSavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), tgaFileName); // 바탕화면에 저장 예시

                File.WriteAllBytes(tgaSavePath, tgaBytes);
                Debug.Log($"<color=lime>[LSBRenderPass] TGA 이미지 저장 성공:</color> {tgaSavePath}"); // 성공 로그 색상 변경

            }
            catch (Exception e)
            {
                Debug.LogError($"[LSBRenderPass] TGA 저장 실패: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                // 사용한 Texture2D 객체 메모리 정리
                if (texForTga != null)
                {
                    // 콜백 함수는 메인 스레드에서 실행될 가능성이 높으므로 Destroy 사용 가능
                    // (만약 다른 스레드라면 DestroyImmediate 사용 필요할 수도 있으나, AsyncGPUReadback 콜백은 보통 메인 스레드)
                    UnityEngine.Object.Destroy(texForTga);
                }
                // 락 해제
                staticIsCallbackProcessing_TGA = false;
            }
        } // --- 콜백 함수 끝 ---

        public override void OnCameraCleanup(CommandBuffer cmd) { }
        public void Cleanup()
        { /* 이전과 동일 */
            bitstreamBuffer?.Release(); bitstreamBuffer = null;
            RTHandles.Release(sourceTextureHandle); sourceTextureHandle = null;
            RTHandles.Release(outputTextureHandle); outputTextureHandle = null;
        }
    }
}