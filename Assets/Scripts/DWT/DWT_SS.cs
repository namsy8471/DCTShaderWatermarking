// DWTRenderFeature.cs
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.Linq;
using System;
using Unity.Collections;
using System.IO;
using System.Net.NetworkInformation;

// OriginBlock 클래스가 동일 프로젝트 내에 정의되어 있다고 가정
// DataManager 클래스가 동일 프로젝트 내에 정의되어 있다고 가정
// SaveTrigger 클래스가 동일 프로젝트 내에 정의되어 있다고 가정
// RTResultHolder 클래스가 동일 프로젝트 내에 정의되어 있다고 가정

public class DWTRenderFeature_SS : ScriptableRendererFeature
{
    [Header("셰이더 및 설정")]
    public ComputeShader dwtComputeShader; // DWT용 .compute 파일 할당
    [Tooltip("DWT 계수에 비트스트림을 임베딩할지 여부")]
    public bool embedBitstream = true;
    [Tooltip("확산 스펙트럼 임베딩 강도")]
    public float embeddingStrength = 0.05f; // 강도 조절 파라미터 추가 (값 조절 필요)
    [Tooltip("Addressables에서 로드할 암호화된 데이터 키")]
    public string addressableKey = "OriginBlockData";
    [Tooltip("블록당 사용할 확산 스펙트럼 계수 개수 (예: HH 영역 내)")]
    [Range(1,16)]
    public uint coefficientsToUse = 10; // 사용할 계수 개수 추가 (최대 16개 - 8x8블록 HH)


    private DWTRenderPass dwtRenderPass;

    public override void Create()
    {
        if (dwtComputeShader == null)
        {
            Debug.LogError("DWT Compute Shader가 할당되지 않았습니다.");
            return;
        }

        // Pass 생성 시 파라미터 전달
        dwtRenderPass = new DWTRenderPass(dwtComputeShader, name, embedBitstream, embeddingStrength, coefficientsToUse, addressableKey);
        dwtRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var camera = renderingData.cameraData.camera;
        if (camera.cameraType != CameraType.Game) { return; }

        if (dwtComputeShader != null && dwtRenderPass != null)
        {
            // 매 프레임 설정 업데이트 (Inspector 변경 사항 반영)
            dwtRenderPass.SetEmbedActive(embedBitstream);
            dwtRenderPass.SetParameters(embeddingStrength, coefficientsToUse);

            if (DataManager.IsDataReady)
            {
                // 패턴 버퍼 생성 및 업데이트 로직 추가 (매번 할 필요는 없을 수 있음)
                dwtRenderPass.UpdatePatternBufferIfNeeded(renderingData.cameraData.cameraTargetDescriptor);
                renderer.EnqueuePass(dwtRenderPass);
            }
            else if (embedBitstream)
            {
                Debug.LogWarning("[DWTRenderFeature] 데이터 미준비. 임베딩 패스 건너뜀.");
            }
            // 임베딩 비활성시 그냥 통과시키는 로직은 제거 (원본만 필요하면 패스 추가 안함)
            // else
            // {
            //     // 임베딩 안 할 때는 패스 추가 불필요 (원본 화면이 그냥 나옴)
            // }
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
        private RTHandle sourceTextureHandle, intermediateHandle, dwtOutputHandle, idwtOutputHandle;

        private string profilerTag;
        private bool embedActive;
        private float currentEmbeddingStrength; // 현재 강도 저장
        private uint currentCoefficientsToUse; // 현재 사용할 계수 개수 저장
        private string secretKey;

        private ComputeBuffer bitstreamBuffer;
        private ComputeBuffer patternBuffer; // 확산 패턴 버퍼 추가

        private List<uint> finalBitsToEmbed;
        private List<float> currentPatternData; // 패턴 데이터 저장용 리스트

        private const int BLOCK_SIZE = 8;
        private const int HALF_BLOCK_SIZE = BLOCK_SIZE / 2;
        private const int HH_COEFFS_PER_BLOCK = HALF_BLOCK_SIZE * HALF_BLOCK_SIZE; // 4x4 = 16

        public DWTRenderPass(ComputeShader shader, string tag, bool initialEmbedState, float initialStrength, uint initialCoeffs, string secretKey)
        {
            computeShader = shader;
            profilerTag = tag;
            embedActive = initialEmbedState;
            currentEmbeddingStrength = initialStrength;
            currentCoefficientsToUse = initialCoeffs;
            this.secretKey = secretKey; // Addressables에서 로드할 키 저장

            dwtRowsKernelID = shader.FindKernel("DWT_Pass1_Rows");
            dwtColsKernelID = shader.FindKernel("DWT_Pass2_Cols_EmbedSS"); // 커널 이름 변경 제안 (SS 명시)
            idwtColsKernelID = shader.FindKernel("IDWT_Pass1_Cols");
            idwtRowsKernelID = shader.FindKernel("IDWT_Pass2_Rows");

            finalBitsToEmbed = new List<uint>();
            currentPatternData = new List<float>();

            if (dwtRowsKernelID < 0 || dwtColsKernelID < 0 || idwtColsKernelID < 0 || idwtRowsKernelID < 0)
            {
                Debug.LogError($"[DWTRenderPass] 하나 이상의 DWT Compute Shader 커널을 찾을 수 없습니다. 커널 이름을 확인하세요: DWT_Pass1_Rows, DWT_Pass2_Cols_EmbedSS, IDWT_Pass1_Cols, IDWT_Pass2_Rows");
            }
        }

        public void SetEmbedActive(bool isActive) { embedActive = isActive; }
        public void SetParameters(float strength, uint coeffs)
        {
            currentEmbeddingStrength = strength;
            // 사용할 계수 개수가 HH 영역 최대 개수(16)를 넘지 않도록 제한
            currentCoefficientsToUse = Math.Min(coeffs, (uint)HH_COEFFS_PER_BLOCK);
        }

        // 필요할 때만 패턴 버퍼 업데이트 (예: 게임 시작 시, 또는 설정 변경 시)
        public void UpdatePatternBufferIfNeeded(RenderTextureDescriptor desc)
        {
            if (!embedActive || currentCoefficientsToUse == 0)
            {
                ReleasePatternBuffer(); // 사용 안하면 해제
                return;
            }

            int width = desc.width;
            int height = desc.height;
            int numBlocksX = Mathf.Max(1, (width + BLOCK_SIZE - 1) / BLOCK_SIZE);
            int numBlocksY = Mathf.Max(1, (height + BLOCK_SIZE - 1) / BLOCK_SIZE);
            int totalBlocks = numBlocksX * numBlocksY;
            int requiredPatternSize = totalBlocks * (int)currentCoefficientsToUse;

            if (requiredPatternSize == 0)
            {
                ReleasePatternBuffer();
                return;
            }

            // 패턴 데이터가 없거나 크기가 다르면 새로 생성
            if (currentPatternData == null || currentPatternData.Count != requiredPatternSize)
            {
                Debug.Log($"[DWTRenderPass] Pattern Buffer 생성/업데이트 필요. 요구 크기: {requiredPatternSize}");
                GeneratePatternData(requiredPatternSize, secretKey);
                UpdatePatternComputeBuffer();
            }
            // 이미 있다면 업데이트 불필요 (매 프레임 생성 방지)
        }

        private void GeneratePatternData(int size, string secretKey)
        {
            currentPatternData = new List<float>(size);
            System.Random random = new System.Random(secretKey.GetHashCode());
            for (int i = 0; i < size; i++)
            {
                // +1 또는 -1 랜덤 생성
                currentPatternData.Add((random.NextDouble() < 0.5) ? -1.0f : 1.0f);

            }
            // 첫 64개 패턴 로그 출력 (디버깅용)
            int logLength = Math.Min(currentPatternData.Count, 64);
            string firstPatterns = string.Join(", ", currentPatternData.Take(logLength).Select(p => p.ToString("F1")));
            Debug.Log($"[DWTRenderPass] 생성된 패턴 데이터 (처음 {logLength}개): [{firstPatterns}]");
        }

        private void UpdatePatternComputeBuffer()
        {
            int count = (currentPatternData != null) ? currentPatternData.Count : 0;
            if (count == 0)
            {
                ReleasePatternBuffer();
                return;
            }

            if (patternBuffer == null || patternBuffer.count != count || !patternBuffer.IsValid())
            {
                ReleasePatternBuffer();
                try
                {
                    patternBuffer = new ComputeBuffer(count, sizeof(float), ComputeBufferType.Structured);
                    // Debug.Log($"[DWTRenderPass] Pattern ComputeBuffer 생성됨 (Count: {count})");
                }
                catch (Exception ex) { /* ... 에러 처리 ... */ return; }
            }

            try
            {
                patternBuffer.SetData(currentPatternData);
                // Debug.Log($"[DWTRenderPass] Pattern ComputeBuffer 데이터 설정 완료 (Count: {count})");
            }
            catch (Exception ex) { /* ... 에러 처리 ... */ ReleasePatternBuffer(); }
        }


        private void ReleaseBitstreamBuffer()
        {
            if (bitstreamBuffer != null) { bitstreamBuffer.Release(); bitstreamBuffer = null; }
        }
        private void ReleasePatternBuffer()
        {
            if (patternBuffer != null) { patternBuffer.Release(); patternBuffer = null; }
        }


        private void UpdateBitstreamBuffer(List<uint> data) // 기존 함수 재사용
        {
            int count = (data != null) ? data.Count : 0;
            if (count == 0)
            {
                ReleaseBitstreamBuffer();
                return;
            }
            if (bitstreamBuffer == null || bitstreamBuffer.count != count || !bitstreamBuffer.IsValid())
            {
                ReleaseBitstreamBuffer();
                try
                {
                    bitstreamBuffer = new ComputeBuffer(count, sizeof(uint), ComputeBufferType.Structured);
                }
                catch (Exception ex) { /* ... 에러 처리 ... */ return; }
            }
            try
            {
                bitstreamBuffer.SetData(data);
            }
            catch (Exception ex) { /* ... 에러 처리 ... */ ReleaseBitstreamBuffer(); }
        }

        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0;
            desc.msaaSamples = 1;
            desc.sRGB = false;

            var bufferDesc = desc;
            bufferDesc.colorFormat = RenderTextureFormat.ARGBFloat;
            bufferDesc.enableRandomWrite = true;

            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, name: "_SourceCopyForDWT");
            RenderingUtils.ReAllocateIfNeeded(ref intermediateHandle, bufferDesc, FilterMode.Point, name: "_IntermediateDWT_IDWT");
            RenderingUtils.ReAllocateIfNeeded(ref dwtOutputHandle, bufferDesc, FilterMode.Point, name: "_DWTOutput");
            RenderingUtils.ReAllocateIfNeeded(ref idwtOutputHandle, bufferDesc, FilterMode.Point, name: "_IDWTOutput");

            // --- 비트스트림 준비 (기존 로직 재사용) ---

            int width = desc.width;
            int height = desc.height;
            int numBlocksX = Mathf.Max(1, (width + BLOCK_SIZE - 1) / BLOCK_SIZE); // 올림 계산
            int numBlocksY = Mathf.Max(1, (height + BLOCK_SIZE - 1) / BLOCK_SIZE); // 올림 계산
            int availableCapacity = numBlocksX * numBlocksY;

            if (finalBitsToEmbed.Count != availableCapacity)
            {
                finalBitsToEmbed = finalBitsToEmbed ?? new List<uint>();
                finalBitsToEmbed.Clear();

                if (embedActive && DataManager.IsDataReady && DataManager.EncryptedOriginData != null)
                {
                    try
                    {
                        List<uint> currentPayload = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);
                        if (currentPayload != null && currentPayload.Count > 0)
                        {

                            int totalPayloadLength = currentPayload.Count;

                            // Debug.Log($"[DWTRenderPass] 이미지 크기: {width}x{height}, 블록 크기: {BLOCK_SIZE}, 총 블록 수: {availableCapacity}, 원본 페이로드 길이: {totalPayloadLength}");

                            if (availableCapacity > 0 && totalPayloadLength > 0)
                            {
                                finalBitsToEmbed.Capacity = availableCapacity;
                                int currentPosition = 0;
                                while (currentPosition < availableCapacity)
                                {
                                    int remainingSpace = availableCapacity - currentPosition;
                                    int countToAdd = Math.Min(totalPayloadLength, remainingSpace);
                                    if (countToAdd <= 0) break;
                                    finalBitsToEmbed.AddRange(currentPayload.GetRange(0, countToAdd));
                                    currentPosition += countToAdd;
                                }
                                // Debug.Log($"[DWTRenderPass] 패딩/절단 완료. 최종 비트 수: {finalBitsToEmbed.Count} (용량: {availableCapacity})");
                            }
                        }
                    }
                    catch (Exception ex) { /* ... 에러 처리 ... */ finalBitsToEmbed.Clear(); }
                }

                UpdateBitstreamBuffer(finalBitsToEmbed); // 비트스트림 버퍼 업데이트
            }
            // --- 셰이더 파라미터 설정 ---
            int currentBitLength = finalBitsToEmbed.Count;
            bool bitstreamBufferValid = bitstreamBuffer != null && bitstreamBuffer.IsValid() && bitstreamBuffer.count == currentBitLength;
            bool patternBufferValid = patternBuffer != null && patternBuffer.IsValid(); // 패턴 버퍼 유효성 검사 추가

            // 최종 임베딩 조건 수정: 패턴 버퍼 유효성 및 CoefficientsToUse > 0 조건 추가
            bool shouldEmbed = embedActive && DataManager.IsDataReady && bitstreamBufferValid && patternBufferValid && currentBitLength > 0 && currentCoefficientsToUse > 0;

            // Debug.Log($"[DWTRenderPass] 최종 비트 길이: {currentBitLength} / BitBuffer:{bitstreamBufferValid} / PatternBuffer:{patternBufferValid} / Embed:{embedActive} / DataReady:{DataManager.IsDataReady} / Coeffs>0:{currentCoefficientsToUse > 0} => 최종 Embed:{shouldEmbed}");

            cmd.SetComputeIntParam(computeShader, "Width", width);
            cmd.SetComputeIntParam(computeShader, "Height", height);
            cmd.SetComputeFloatParam(computeShader, "EmbeddingStrength", currentEmbeddingStrength); // 강도 전달
            cmd.SetComputeIntParam(computeShader, "CoefficientsToUse", (int)currentCoefficientsToUse); // 사용할 계수 개수 전달

            // --- 커널에 파라미터 바인딩 ---
            // DWT Pass 1
            if (dwtRowsKernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, dwtRowsKernelID, "Source", sourceTextureHandle);
                cmd.SetComputeTextureParam(computeShader, dwtRowsKernelID, "IntermediateBuffer", intermediateHandle);
            }
            // DWT Pass 2 + Embed SS
            if (dwtColsKernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, dwtColsKernelID, "IntermediateBuffer", intermediateHandle);
                cmd.SetComputeTextureParam(computeShader, dwtColsKernelID, "DWTOutput", dwtOutputHandle);

                cmd.SetComputeIntParam(computeShader, "Embed", shouldEmbed ? 1 : 0);

                if (shouldEmbed)
                {
                    cmd.SetComputeIntParam(computeShader, "BitLength", currentBitLength);
                    cmd.SetComputeBufferParam(computeShader, dwtColsKernelID, "Bitstream", bitstreamBuffer);
                    cmd.SetComputeBufferParam(computeShader, dwtColsKernelID, "PatternBuffer", patternBuffer);
                }

                else
                {
                    cmd.SetComputeIntParam(computeShader, "BitLength", 0);
                }
                // 패턴 버퍼 바인딩
                // Debug.Log($"[DWTRenderPass] DWT Pass 2: Bitstream({currentBitLength}), PatternBuffer({patternBuffer.count}) 바인딩됨.");

            }
            // IDWT Pass 1
            if (idwtColsKernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, idwtColsKernelID, "DWTOutput", dwtOutputHandle);
                cmd.SetComputeTextureParam(computeShader, idwtColsKernelID, "IntermediateBuffer", intermediateHandle);
            }
            // IDWT Pass 2
            if (idwtRowsKernelID >= 0)
            {
                cmd.SetComputeTextureParam(computeShader, idwtRowsKernelID, "IntermediateBuffer", intermediateHandle);
                cmd.SetComputeTextureParam(computeShader, idwtRowsKernelID, "IDWTOutput", idwtOutputHandle);
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // ... (기존 커널 유효성 검사, RTHandle 유효성 검사, 스레드 그룹 계산 등은 동일하게 유지) ...
            bool kernelsValid = dwtRowsKernelID >= 0 && dwtColsKernelID >= 0 && idwtColsKernelID >= 0 && idwtRowsKernelID >= 0;
            if (!kernelsValid) { /* ... 에러 처리 ... */ return; }

            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;

            // RTHandle 유효성 검사 추가
            if (sourceTextureHandle == null || intermediateHandle == null || dwtOutputHandle == null || idwtOutputHandle == null || cameraTarget == null)
            {
                Debug.LogError("[DWTRenderPass] 하나 이상의 RTHandle이 유효하지 않습니다. Execute 중단.");
                CommandBufferPool.Release(cmd);
                return;
            }
            int width = cameraTarget.rt?.width ?? renderingData.cameraData.cameraTargetDescriptor.width;
            int height = cameraTarget.rt?.height ?? renderingData.cameraData.cameraTargetDescriptor.height;
            if (width <= 0 || height <= 0) { /* ... 에러 처리 ... */ CommandBufferPool.Release(cmd); return; }

            int threadGroupsX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int threadGroupsY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
            if (threadGroupsX <= 0 || threadGroupsY <= 0) { /* ... 에러 처리 ... */ CommandBufferPool.Release(cmd); return; }


            // 임베딩 활성화인데 필요한 버퍼가 준비 안됐으면 실행하지 않음 (오류 방지)
            bool shouldEmbed = embedActive && DataManager.IsDataReady && bitstreamBuffer != null && bitstreamBuffer.IsValid() && patternBuffer != null && patternBuffer.IsValid() && finalBitsToEmbed.Count > 0 && currentCoefficientsToUse > 0;
            if (embedActive && !shouldEmbed)
            {
                Debug.LogWarning("[DWTRenderPass Execute] 임베딩 조건 미충족, 패스 실행 건너뜀.");
                // 이 경우, 원본 화면을 유지해야 하므로 아무 작업도 하지 않거나 Blit(source, target)만 수행
                // 여기서는 그냥 리턴하여 이전 프레임 유지 (또는 Blit 추가)
                CommandBufferPool.Release(cmd);
                return;
            }


            cmd.Blit(cameraTarget, sourceTextureHandle); // 원본 복사
            RTResultHolder.DedicatedSaveTargetBeforeEmbedding = sourceTextureHandle;


            using (new ProfilingScope(cmd, new ProfilingSampler(profilerTag)))
            {
                // DWT
                cmd.DispatchCompute(computeShader, dwtRowsKernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, dwtColsKernelID, threadGroupsX, threadGroupsY, 1); // Embed SS 포함 커널

                // IDWT
                cmd.DispatchCompute(computeShader, idwtColsKernelID, threadGroupsX, threadGroupsY, 1);
                cmd.DispatchCompute(computeShader, idwtRowsKernelID, threadGroupsX, threadGroupsY, 1);

                // 최종 결과 복사
                cmd.Blit(idwtOutputHandle, cameraTarget);

                // 결과 저장용 설정 (필요시)
                RTResultHolder.DedicatedSaveTarget = idwtOutputHandle;
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }


        public override void OnCameraCleanup(CommandBuffer cmd) { }

        public void Cleanup()
        {
            ReleaseBitstreamBuffer(); // 비트스트림 버퍼 해제
            ReleasePatternBuffer(); // 패턴 버퍼 해제
            RTHandles.Release(sourceTextureHandle); sourceTextureHandle = null;
            RTHandles.Release(intermediateHandle); intermediateHandle = null;
            RTHandles.Release(dwtOutputHandle); dwtOutputHandle = null;
            RTHandles.Release(idwtOutputHandle); idwtOutputHandle = null;
        }
    }
}