using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System;
using System.Linq;

// 필요한 클래스 정의 가정 (DataManager, OriginBlock 등)
// using static DataManager; // 필요 시 가정
// using static OriginBlock;  // 필요 시 가정
// --- 예시용 가상 클래스 ---


public class DCT_LSB_RenderFeature : ScriptableRendererFeature
{
    [Header("셰이더 및 설정")]
    public ComputeShader lsbDctComputeShader; // DCT, 양자화, LSB 임베딩, IDCT를 수행할 Compute Shader
    [Tooltip("LSB 워터마크를 임베딩할지 여부")]
    public bool embedBitstream = true; // 워터마크 삽입 기능 활성화/비활성화

    [Header("LSB Watermarking 설정")]
    [Tooltip("LSB를 삽입할 AC 계수의 지그재그 순서 인덱스 (0~62)")]
    [Range(0, 62)]
    public int lsbEmbedIndex = 0; // 기본값: 첫 번째 AC 계수 (1,0 또는 0,1)

    [Header("양자화 테이블 (JPEG 표준 예시 - Luminance)")]
    [Tooltip("8x8 양자화 테이블 값 (64개 필요)")]
    public int[] quantizationTableValues = new int[64] {
        // 표준 JPEG 휘도 양자화 테이블
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99
    };

    private DCT_LSB_RenderPass lsbDctRenderPass;

    // 커널 이름 정의 (셰이더 파일과 일치해야 함)
    private const string PASS1_KERNEL = "DCT_Pass1_Rows";
    private const string PASS2_KERNEL = "DCT_Pass2_Cols_Quant_EmbedLSB";
    private const string PASS3_KERNEL = "IDCT_Pass1_Dequant_Cols";
    private const string PASS4_KERNEL = "IDCT_Pass2_Rows_Combine";

    /// <summary>
    /// Render Feature가 처음 생성되거나 활성화될 때 호출됩니다.
    /// Render Pass를 초기화하고 설정합니다.
    /// </summary>
    public override void Create()
    {
        // 1. Compute Shader 유효성 검사
        if (lsbDctComputeShader == null)
        {
            Debug.LogError($"[{name}] Compute Shader가 할당되지 않았습니다. Render Feature 비활성화됨.");
            return;
        }
        Debug.Log($"[{name}] Compute Shader '{lsbDctComputeShader.name}' 확인됨.");

        // 2. 양자화 테이블 크기 검사 및 기본값 설정
        if (quantizationTableValues == null || quantizationTableValues.Length != 64)
        {
            Debug.LogError($"[{name}] 양자화 테이블 크기가 64가 아닙니다. 표준 8x8 테이블로 재설정합니다.");
            quantizationTableValues = new int[64] { // 기본 테이블로 강제 설정
                16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
                14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
                18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
                49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99
            };
        }
        Debug.Log($"[{name}] 양자화 테이블 (크기: {quantizationTableValues.Length}) 확인됨.");

        // 3. RenderPass 인스턴스 생성
        try
        {
            lsbDctRenderPass = new DCT_LSB_RenderPass(
                lsbDctComputeShader,
                name, // 프로파일러 태그용 이름
                embedBitstream,
                lsbEmbedIndex,
                quantizationTableValues,
                PASS1_KERNEL,
                PASS2_KERNEL,
                PASS3_KERNEL,
                PASS4_KERNEL
            );
            Debug.Log($"[{name}] DCT_LSB_RenderPass 인스턴스 생성 완료.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{name}] DCT_LSB_RenderPass 생성 실패: {ex.Message}\n{ex.StackTrace}");
            lsbDctRenderPass = null; // 실패 시 null 처리
            return;
        }

        // 4. Render Pass 실행 시점 설정 (후처리 이후)
        lsbDctRenderPass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
        Debug.Log($"[{name}] Render Pass Event 설정: {lsbDctRenderPass.renderPassEvent}");
    }

    /// <summary>
    /// 매 프레임 렌더링 파이프라인에 Render Pass를 추가합니다.
    /// </summary>
    /// <param name="renderer">현재 사용 중인 ScriptableRenderer</param>
    /// <param name="renderingData">현재 프레임의 렌더링 관련 데이터</param>
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // 게임 카메라에만 적용 (씬 뷰 등 제외)
        var camera = renderingData.cameraData.camera;
        if (camera.cameraType != CameraType.Game) return;

        // RenderPass가 유효하고 Compute Shader가 할당된 경우에만 추가
        if (lsbDctComputeShader != null && lsbDctRenderPass != null)
        {
            // 매 프레임 동적으로 변경될 수 있는 설정 업데이트
            lsbDctRenderPass.SetEmbedActive(embedBitstream);
            lsbDctRenderPass.SetLsbEmbedIndex(lsbEmbedIndex);
            // 양자화 테이블은 변경 시 업데이트 가능 (선택적 최적화: 실제 변경되었을 때만 호출)
            lsbDctRenderPass.UpdateQuantizationTable(quantizationTableValues);

            // RenderPass를 렌더러의 실행 큐에 추가
            renderer.EnqueuePass(lsbDctRenderPass);
            // Debug.Log($"[{name}] Render Pass Enqueued for frame."); // 너무 자주 로깅되므로 주석 처리
        }
    }

    /// <summary>
    /// Render Feature가 비활성화되거나 제거될 때 호출됩니다.
    /// 할당된 리소스를 정리합니다.
    /// </summary>
    /// <param name="disposing">관리되는 리소스와 관리되지 않는 리소스 모두 해제해야 하는지 여부</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // RenderPass의 Cleanup 함수 호출하여 내부 리소스 해제
            lsbDctRenderPass?.Cleanup();
            lsbDctRenderPass = null;
            Debug.Log($"[{name}] Disposed Render Pass resources.");
        }
        base.Dispose(disposing);
    }

    // --- Render Pass 클래스 ---
    class DCT_LSB_RenderPass : ScriptableRenderPass
    {
        private ComputeShader computeShader; // 사용할 Compute Shader
        private int kernelPass1, kernelPass2, kernelPass3, kernelPass4; // 각 패스 커널 ID

        // Render Target 핸들 (임시 텍스처 관리)
        private RTHandle sourceTextureHandle;      // 원본 화면 복사본 (RGB)
        private RTHandle intermediateHandle;       // 중간 결과 저장 (Pass 1 출력 Y', Pass 3 출력 Y') - RFloat
        private RTHandle quantizedDctHandle;       // 양자화된 DCT 계수 저장 (Pass 2 출력) - RInt
        private RTHandle chromaBufferHandle;       // 원본 CbCr 값 저장 (Pass 1 출력) - RGFloat
        private RTHandle finalOutputHandle;        // 최종 워터마크된 결과 저장 (Pass 4 출력) - ARGBFloat

        // Compute Buffer 핸들 (GPU 데이터 버퍼)
        private ComputeBuffer bitstreamBuffer;       // 삽입할 비트 데이터 (uint)
        private ComputeBuffer quantizationTableBuffer; // 양자화 테이블 데이터 (int)

        // CPU 측 데이터
        private List<uint> finalBitsToEmbed;       // GPU로 전송할 최종 비트 리스트
        private int[] currentQuantizationTable;    // 현재 사용 중인 양자화 테이블 (CPU 복사본)

        // 설정 및 상태 변수
        private string profilerTag;                 // 프로파일러에서 식별하기 위한 태그
        private bool embedActive;                   // 현재 프레임에서 임베딩 활성화 여부
        private int currentLsbEmbedIndex;          // 현재 LSB 삽입 위치 인덱스

        private const int BLOCK_SIZE = 8;          // 처리 블록 크기 (셰이더와 일치)

        /// <summary>
        /// Render Pass 생성자.
        /// </summary>
        public DCT_LSB_RenderPass(ComputeShader shader, string tag, bool initialEmbedState,
                                  int initialLsbIndex, int[] initialQuantTable,
                                  string kernel1Name, string kernel2Name, string kernel3Name, string kernel4Name)
        {
            computeShader = shader;
            profilerTag = tag; // Profiling Sampler 이름 설정
            this.profilingSampler = new ProfilingSampler(tag); // 내부 Profiling Sampler 초기화

            embedActive = initialEmbedState;
            currentLsbEmbedIndex = initialLsbIndex;
            currentQuantizationTable = (int[])initialQuantTable.Clone(); // 배열 복사본 저장

            // 필수 커널 ID 찾기 및 유효성 검사
            try
            {
                kernelPass1 = shader.FindKernel(kernel1Name);
                kernelPass2 = shader.FindKernel(kernel2Name);
                kernelPass3 = shader.FindKernel(kernel3Name);
                kernelPass4 = shader.FindKernel(kernel4Name);

                if (kernelPass1 < 0 || kernelPass2 < 0 || kernelPass3 < 0 || kernelPass4 < 0)
                {
                    throw new Exception($"하나 이상의 필수 커널을 찾을 수 없습니다. ({kernel1Name}:{kernelPass1}, {kernel2Name}:{kernelPass2}, {kernel3Name}:{kernelPass3}, {kernel4Name}:{kernelPass4})");
                }
                Debug.Log($"[{profilerTag}] Compute Shader Kernels found: Pass1({kernelPass1}), Pass2({kernelPass2}), Pass3({kernelPass3}), Pass4({kernelPass4})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{profilerTag}] Compute Shader 커널 초기화 실패: {ex.Message}");
                // 커널 ID를 유효하지 않음(-1)으로 설정하여 Execute 단계에서 실행 방지
                kernelPass1 = kernelPass2 = kernelPass3 = kernelPass4 = -1;
                throw; // 생성자에서 예외를 다시 던져 Render Feature 생성 중단 유도
            }

            finalBitsToEmbed = new List<uint>();
        }

        // --- 설정 업데이트 함수 ---
        public void SetEmbedActive(bool isActive) { embedActive = isActive; }
        public void SetLsbEmbedIndex(int index) { currentLsbEmbedIndex = Mathf.Clamp(index, 0, 62); } // 0~62 범위 강제

        /// <summary>
        /// 양자화 테이블을 업데이트합니다. 변경된 경우 GPU 버퍼 재생성을 유도합니다.
        /// </summary>
        public void UpdateQuantizationTable(int[] newTable)
        {
            if (newTable != null && newTable.Length == 64)
            {
                bool changed = false;
                if (currentQuantizationTable == null || currentQuantizationTable.Length != 64)
                {
                    currentQuantizationTable = new int[64];
                    changed = true; // 이전 테이블이 없었으면 변경된 것
                }
                // 값 비교
                for (int i = 0; i < 64; ++i)
                {
                    if (currentQuantizationTable[i] != newTable[i])
                    {
                        currentQuantizationTable[i] = newTable[i];
                        changed = true;
                    }
                }

                // 테이블이 변경되었고, 기존 버퍼가 존재하면 해제하여 OnCameraSetup에서 다시 만들도록 함
                if (changed && quantizationTableBuffer != null)
                {
                    ReleaseQuantizationTableBuffer();
                    // Debug.Log($"[{profilerTag}] Quantization table changed, buffer will be recreated.");
                }
            }
            else
            {
                Debug.LogError($"[{profilerTag}] 잘못된 양자화 테이블 데이터입니다. 크기는 64여야 합니다.");
            }
        }

        // --- Compute Buffer 관리 ---

        /// <summary>
        /// 삽입할 비트스트림 데이터로 ComputeBuffer를 업데이트합니다.
        /// </summary>
        private void UpdateBitstreamBuffer(List<uint> data)
        {
            int count = (data != null) ? data.Count : 0;
            if (count == 0) // 데이터 없으면 버퍼 해제
            {
                if (bitstreamBuffer != null)
                {
                    // Debug.Log($"[{profilerTag}] No bitstream data, releasing existing buffer.");
                    ReleaseBitstreamBuffer();
                }
                return;
            }

            // 버퍼가 없거나, 유효하지 않거나, 크기가 다르면 새로 생성
            if (bitstreamBuffer == null || !bitstreamBuffer.IsValid() || bitstreamBuffer.count != count)
            {
                ReleaseBitstreamBuffer(); // 기존 것 해제
                try
                {
                    bitstreamBuffer = new ComputeBuffer(count, sizeof(uint), ComputeBufferType.Structured, ComputeBufferMode.Immutable); // 데이터 한번 설정 후 안 바뀔 것이므로 Immutable
                    Debug.Log($"[{profilerTag}] Created Bitstream ComputeBuffer (Count: {count}).");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{profilerTag}] Bitstream ComputeBuffer 생성 실패 (Size: {count}): {ex.Message}\n{ex.StackTrace}");
                    bitstreamBuffer = null;
                    return; // 버퍼 생성 실패 시 중단
                }
            }

            // 유효한 버퍼에 데이터 설정
            if (bitstreamBuffer != null && bitstreamBuffer.IsValid())
            {
                try
                {
                    bitstreamBuffer.SetData(data);
                    // Debug.Log($"[{profilerTag}] Set data for Bitstream ComputeBuffer (Count: {count}).");
                }
                catch (Exception ex)
                {
                    // SetData 실패 시 버퍼 해제
                    Debug.LogError($"[{profilerTag}] Bitstream ComputeBuffer SetData 오류 (Size: {count}): {ex.Message}\n{ex.StackTrace}");
                    ReleaseBitstreamBuffer();
                }
            }
        }

        /// <summary>
        /// Bitstream ComputeBuffer를 안전하게 해제합니다.
        /// </summary>
        private void ReleaseBitstreamBuffer()
        {
            bitstreamBuffer?.Release();
            bitstreamBuffer = null;
        }

        /// <summary>
        /// 양자화 테이블 데이터로 ComputeBuffer를 업데이트합니다.
        /// </summary>
        private void UpdateQuantizationTableBuffer()
        {
            if (currentQuantizationTable == null || currentQuantizationTable.Length != 64)
            {
                Debug.LogError($"[{profilerTag}] 양자화 테이블 CPU 데이터가 유효하지 않아 GPU 버퍼를 생성할 수 없습니다.");
                ReleaseQuantizationTableBuffer(); // 혹시 버퍼가 있었다면 해제
                return;
            }

            // 버퍼가 없거나 유효하지 않으면 새로 생성
            if (quantizationTableBuffer == null || !quantizationTableBuffer.IsValid())
            {
                ReleaseQuantizationTableBuffer(); // 안전하게 이전 버퍼 해제
                try
                {
                    // 양자화 테이블은 자주 바뀌지 않으므로 Immutable 모드 사용 가능
                    quantizationTableBuffer = new ComputeBuffer(64, sizeof(int), ComputeBufferType.Structured, ComputeBufferMode.Immutable);
                    quantizationTableBuffer.SetData(currentQuantizationTable);
                    Debug.Log($"[{profilerTag}] Created and set data for Quantization Table ComputeBuffer.");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{profilerTag}] Quantization Table ComputeBuffer 생성/설정 실패: {ex.Message}\n{ex.StackTrace}");
                    quantizationTableBuffer = null; // 실패 시 null 처리
                }
            }
            // 이미 버퍼가 존재하고 유효하다면, UpdateQuantizationTable 함수에서 변경 시 재생성하므로 여기서는 추가 작업 없음
        }

        /// <summary>
        /// Quantization Table ComputeBuffer를 안전하게 해제합니다.
        /// </summary>
        private void ReleaseQuantizationTableBuffer()
        {
            quantizationTableBuffer?.Release();
            quantizationTableBuffer = null;
        }

        /// <summary>
        /// 카메라 렌더링 시작 전에 호출됩니다.
        /// 필요한 Render Target과 Compute Buffer를 설정하고 셰이더 파라미터를 업데이트합니다.
        /// </summary>
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            // 필수 커널 유효성 확인
            if (kernelPass1 < 0 || kernelPass2 < 0 || kernelPass3 < 0 || kernelPass4 < 0)
            {
                Debug.LogError($"[{profilerTag}] 하나 이상의 Compute Shader 커널이 유효하지 않습니다. Pass 실행 중단.");
                return;
            }

            // 렌더 타겟 디스크립터 가져오기 (카메라 설정 기반)
            var desc = renderingData.cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0; // 깊이 버퍼 불필요
            desc.msaaSamples = 1; // MSAA 사용 안 함
            // sRGB 변환은 최종 단계에서만 고려하거나, 색 공간 처리에 따라 조정 필요. 보통 Compute Shader 내에서 선형 공간 처리.
            desc.sRGB = false; // 선형 공간에서 작업

            int width = desc.width;
            int height = desc.height;
            // 해상도 유효성 검사
            if (width <= 0 || height <= 0)
            {
                Debug.LogWarning($"[{profilerTag}] 카메라 해상도({width}x{height})가 유효하지 않아 중단합니다.");
                return;
            }

            // --- RTHandle 할당 (필요한 경우에만 재할당) ---
            // 이름은 디버깅에 유용
            // FilterMode.Point는 픽셀 간 보간이 필요 없는 경우 사용
            RenderingUtils.ReAllocateIfNeeded(ref sourceTextureHandle, desc, FilterMode.Point, TextureWrapMode.Clamp, name: "_SourceCopyForDCT");
            if (sourceTextureHandle != null) Debug.Log($"[{profilerTag}] Allocated RT: _SourceCopyForDCT ({desc.width}x{desc.height}, {desc.colorFormat})");

            // 중간 결과 (Pass 1 Y', Pass 3 Y') - 단일 채널 실수형
            var intermediateDesc = desc;
            intermediateDesc.colorFormat = RenderTextureFormat.RFloat; // Y 채널만 저장
            intermediateDesc.enableRandomWrite = true; // Compute Shader 쓰기 활성화
            RenderingUtils.ReAllocateIfNeeded(ref intermediateHandle, intermediateDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_Intermediate_Y_Prime");
            if (intermediateHandle != null) Debug.Log($"[{profilerTag}] Allocated RT: _Intermediate_Y_Prime ({intermediateDesc.width}x{intermediateDesc.height}, {intermediateDesc.colorFormat})");

            // 양자화된 DCT 계수 (Pass 2 출력) - 단일 채널 정수형
            var quantizedDesc = desc;
            quantizedDesc.colorFormat = RenderTextureFormat.RInt; // 정수 DCT 계수 저장
            quantizedDesc.enableRandomWrite = true;
            RenderingUtils.ReAllocateIfNeeded(ref quantizedDctHandle, quantizedDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_QuantizedDCTOutput_Y");
            if (quantizedDctHandle != null) Debug.Log($"[{profilerTag}] Allocated RT: _QuantizedDCTOutput_Y ({quantizedDesc.width}x{quantizedDesc.height}, {quantizedDesc.colorFormat})");

            // CbCr 저장용 (Pass 1 출력) - 2 채널 실수형
            var chromaDesc = desc;
            chromaDesc.colorFormat = RenderTextureFormat.RGFloat; // Cb, Cr 저장
            chromaDesc.enableRandomWrite = true;
            RenderingUtils.ReAllocateIfNeeded(ref chromaBufferHandle, chromaDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_ChromaBufferCbCr");
            if (chromaBufferHandle != null) Debug.Log($"[{profilerTag}] Allocated RT: _ChromaBufferCbCr ({chromaDesc.width}x{chromaDesc.height}, {chromaDesc.colorFormat})");

            // 최종 결과 (Pass 4 출력) - 4 채널 실수형 (알파 포함)
            var finalDesc = desc;
            finalDesc.colorFormat = RenderTextureFormat.ARGBFloat; // 최종 RGB 결과 (HDR)
            finalDesc.enableRandomWrite = true;
            RenderingUtils.ReAllocateIfNeeded(ref finalOutputHandle, finalDesc, FilterMode.Point, TextureWrapMode.Clamp, name: "_FinalWatermarkedOutput");
            if (finalOutputHandle != null) Debug.Log($"[{profilerTag}] Allocated RT: _FinalWatermarkedOutput ({finalDesc.width}x{finalDesc.height}, {finalDesc.colorFormat})");

            // --- 올바른 블록 수 계산 ---
            int numBlocksX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int numBlocksY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
            int totalBlocks = numBlocksX * numBlocksY;
            if (totalBlocks <= 0) return; // 처리할 블록 없음

            // --- 비트스트림 준비 ---
            finalBitsToEmbed = finalBitsToEmbed ?? new List<uint>(); // 리스트가 null이면 새로 생성
            finalBitsToEmbed.Clear(); // 이전 프레임 데이터 지우기
            // 임베딩 활성화 상태이고, 데이터 소스가 준비되었으며, 암호화된 원본 데이터가 있고, 처리할 블록이 있을 때
            if (embedActive && DataManager.IsDataReady && DataManager.EncryptedOriginData != null && totalBlocks > 0)
            {
                try
                {
                    // DataManager와 OriginBlock을 통해 삽입할 페이로드(비트 리스트) 생성
                    // LSB 방식은 보통 블록당 1비트를 삽입하므로, 최대 totalBlocks개의 비트가 필요
                    List<uint> currentPayload = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);

                    if (currentPayload != null && currentPayload.Count > 0)
                    {
                        // 필요한 비트 수(totalBlocks)만큼만 사용. 데이터가 부족하면 있는 만큼만 사용.
                        int bitsToTake = Mathf.Min(currentPayload.Count, totalBlocks);
                        finalBitsToEmbed.AddRange(currentPayload.Take(bitsToTake));
                        Debug.Log($"[{profilerTag}] Prepared {finalBitsToEmbed.Count} bits for embedding (requested max: {totalBlocks}).");
                    }
                    else
                    {
                        Debug.LogWarning($"[{profilerTag}] Payload construction returned empty or null. No bits to embed.");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[{profilerTag}] 비트스트림 페이로드 준비 중 오류 발생: {ex.Message}\n{ex.StackTrace}");
                    finalBitsToEmbed.Clear(); // 오류 시 비트 리스트 초기화
                }
            }

            // --- Compute Buffer 업데이트 ---
            UpdateBitstreamBuffer(finalBitsToEmbed);      // 비트스트림 데이터로 GPU 버퍼 업데이트
            UpdateQuantizationTableBuffer();            // 양자화 테이블 데이터로 GPU 버퍼 업데이트

            // --- 셰이더 파라미터 설정 ---
            int currentBitLength = finalBitsToEmbed.Count;
            // 버퍼 유효성 검사 (셰이더에 설정하기 전)
            bool bitstreamBufferValid = bitstreamBuffer != null && bitstreamBuffer.IsValid() && bitstreamBuffer.count >= currentBitLength;
            bool quantTableBufferValid = quantizationTableBuffer != null && quantizationTableBuffer.IsValid();

            // 최종적으로 GPU에서 임베딩을 실행할지 여부 결정
            // (CPU 플래그 활성, 데이터 준비 완료, 필요한 GPU 버퍼들 유효, 삽입할 비트 존재)
            bool shouldEmbedOnGPU = embedActive && DataManager.IsDataReady &&
                                   bitstreamBufferValid && quantTableBufferValid &&
                                   currentBitLength > 0;

            // Debug.Log($"[{profilerTag}] Should Embed on GPU: {shouldEmbedOnGPU} (embedActive:{embedActive}, dataReady:{DataManager.IsDataReady}, bitstreamValid:{bitstreamBufferValid}, quantTableValid:{quantTableBufferValid}, bitLength:{currentBitLength})");


            // Compute Shader에 전역 변수 설정
            if (computeShader == null) return;
            try
            {
                computeShader.SetInt("Width", width);
                computeShader.SetInt("Height", height);
                computeShader.SetInt("Embed", shouldEmbedOnGPU ? 1 : 0); // GPU 임베딩 실행 여부 플래그
                computeShader.SetInt("LsbEmbedIndex", currentLsbEmbedIndex); // LSB 삽입 위치 (지그재그 인덱스)

                // --- Pass 2 (양자화 및 임베딩)에 필요한 버퍼 설정 ---
                if (kernelPass2 >= 0)
                {
                    // 양자화 테이블 버퍼 바인딩 (유효성 확인 후)
                    if (quantTableBufferValid)
                        computeShader.SetBuffer(kernelPass2, "QuantizationTable", quantizationTableBuffer);
                    else if (shouldEmbedOnGPU) // 임베딩 하려는데 테이블 버퍼가 없으면 문제
                        Debug.LogError($"[{profilerTag}] QuantizationTable buffer is invalid, but embedding was requested for Pass 2!");

                    // 임베딩 실행 시 비트스트림 버퍼 바인딩
                    if (shouldEmbedOnGPU)
                    {
                        computeShader.SetInt("BitLength", currentBitLength); // 실제 삽입할 비트 수 전달
                        if (bitstreamBufferValid)
                            computeShader.SetBuffer(kernelPass2, "Bitstream", bitstreamBuffer);
                        else // 임베딩 하려는데 비트 버퍼가 없으면 문제
                            Debug.LogError($"[{profilerTag}] Bitstream buffer is invalid, but embedding was requested for Pass 2!");
                    }
                }

                // --- Pass 3 (역양자화)에 필요한 버퍼 설정 ---
                if (kernelPass3 >= 0)
                {
                    // 양자화 테이블 버퍼 바인딩 (유효성 확인 후)
                    if (quantTableBufferValid)
                        computeShader.SetBuffer(kernelPass3, "QuantizationTable", quantizationTableBuffer);
                    else
                        Debug.LogError($"[{profilerTag}] QuantizationTable buffer is invalid for Pass 3!");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{profilerTag}] Compute Shader 파라미터 설정 실패: {ex.Message}\n{ex.StackTrace}");
                // 파라미터 설정 실패 시 이후 Execute 방지 위해 커널 ID 무효화? (선택적)
                // kernelPass1 = kernelPass2 = kernelPass3 = kernelPass4 = -1;
            }

            // --- 텍스처 바인딩 (각 커널에 필요한 입출력 텍스처 설정) ---
            // 각 커널에서 사용하는 이름과 일치해야 함
            try
            {
                // Pass 1: Source(읽기) -> IntermediateBuffer(쓰기), ChromaBuffer(쓰기)
                if (kernelPass1 >= 0)
                {
                    // SetTexture의 첫번째 인자는 커널 인덱스, 두번째는 HLSL의 변수 이름, 세번째는 RTHandle
                    computeShader.SetTexture(kernelPass1, "Source", sourceTextureHandle);
                    computeShader.SetTexture(kernelPass1, "IntermediateBuffer", intermediateHandle); // RWTexture2D
                    computeShader.SetTexture(kernelPass1, "ChromaBuffer", chromaBufferHandle);         // RWTexture2D
                }
                // Pass 2: IntermediateBuffer(읽기) -> QuantizedDCTOutput(쓰기)
                if (kernelPass2 >= 0)
                {
                    computeShader.SetTexture(kernelPass2, "IntermediateBuffer", intermediateHandle); // Texture2D (읽기용)
                    computeShader.SetTexture(kernelPass2, "QuantizedDCTOutput", quantizedDctHandle); // RWTexture2D
                }
                // Pass 3: QuantizedDCTOutput(읽기) -> IntermediateBuffer(쓰기)
                if (kernelPass3 >= 0)
                {
                    computeShader.SetTexture(kernelPass3, "QuantizedDCTOutput", quantizedDctHandle); // Texture2D<int> (읽기용)
                    computeShader.SetTexture(kernelPass3, "IntermediateBuffer", intermediateHandle);   // RWTexture2D
                }
                // Pass 4: IntermediateBuffer(읽기), ChromaBuffer(읽기) -> FinalOutput(쓰기)
                if (kernelPass4 >= 0)
                {
                    computeShader.SetTexture(kernelPass4, "IntermediateBuffer", intermediateHandle); // Texture2D (읽기용)
                    computeShader.SetTexture(kernelPass4, "ChromaBuffer", chromaBufferHandle);         // Texture2D (읽기용)
                    computeShader.SetTexture(kernelPass4, "FinalOutput", finalOutputHandle);           // RWTexture2D
                }
                // Debug.Log($"[{profilerTag}] All textures bound to compute shader kernels.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{profilerTag}] Compute Shader 텍스처 바인딩 실패: {ex.Message}\n{ex.StackTrace}");
                // 텍스처 바인딩 실패 시 이후 Execute 방지?
                // kernelPass1 = kernelPass2 = kernelPass3 = kernelPass4 = -1;
            }

            // 이 Pass가 카메라 타겟을 직접 쓴다고 명시 (Configure에서 타겟 설정하는 방식 대신 사용 가능)
            // ConfigureTarget(renderingData.cameraData.renderer.cameraColorTargetHandle);
        }

        /// <summary>
        /// 실제 렌더링 로직을 실행합니다. Compute Shader 커널을 순서대로 Dispatch합니다.
        /// </summary>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // 유효성 검사: 커널 ID, Compute Shader, Render Target 등
            if (kernelPass1 < 0 || kernelPass2 < 0 || kernelPass3 < 0 || kernelPass4 < 0 || computeShader == null)
            {
                Debug.LogError($"[{profilerTag}] Cannot execute pass due to invalid kernel or shader.");
                return;
            }

            var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
            // 필요한 RTHandle들이 유효한지 추가 확인
            if (sourceTextureHandle == null || intermediateHandle == null || quantizedDctHandle == null ||
               chromaBufferHandle == null || finalOutputHandle == null)
            {
                Debug.LogError($"[{profilerTag}] Cannot execute pass due to invalid RTHandles.");
                return;
            }

            // CommandBuffer 가져오기 (풀 사용)
            CommandBuffer cmd = CommandBufferPool.Get(profilerTag);

            // 프로파일링 스코프 시작 (using 구문 사용)
            using (new ProfilingScope(cmd, profilingSampler)) // profilingSampler 사용
            {
                int width = cameraTarget.rt.width;
                int height = cameraTarget.rt.height;
                if (width <= 0 || height <= 0)
                {
                    Debug.LogWarning($"[{profilerTag}] Invalid target size ({width}x{height}) in Execute.");
                    CommandBufferPool.Release(cmd);
                    return;
                }

                // 스레드 그룹 수 계산 (셰이더의 numthreads와 BLOCK_SIZE 기준)
                int threadGroupsX = (width + BLOCK_SIZE - 1) / BLOCK_SIZE;
                int threadGroupsY = (height + BLOCK_SIZE - 1) / BLOCK_SIZE;
                if (threadGroupsX <= 0 || threadGroupsY <= 0)
                {
                    Debug.LogWarning($"[{profilerTag}] Invalid thread group count ({threadGroupsX}x{threadGroupsY}).");
                    CommandBufferPool.Release(cmd);
                    return;
                }

                // 0. 원본 카메라 타겟을 작업용 텍스처로 복사
                cmd.Blit(cameraTarget, sourceTextureHandle);
                // Debug.Log($"[{profilerTag}] Blitted camera target to sourceTextureHandle.");

                // --- Compute Shader 커널 디스패치 ---
                // 1. Pass 1: Row DCT + CbCr Save
                cmd.DispatchCompute(computeShader, kernelPass1, threadGroupsX, threadGroupsY, 1);
                Debug.Log($"[{profilerTag}] Dispatched Pass 1 ({threadGroupsX}x{threadGroupsY} groups).");

                if (Input.GetKey(KeyCode.Keypad1)) cmd.Blit(intermediateHandle, cameraTarget); // Pass 1/3 결과 (Y')

                // 2. Pass 2: Column DCT + Quantization + LSB Embed
                cmd.DispatchCompute(computeShader, kernelPass2, threadGroupsX, threadGroupsY, 1);
                Debug.Log($"[{profilerTag}] Dispatched Pass 2 ({threadGroupsX}x{threadGroupsY} groups).");

                if (Input.GetKey(KeyCode.Keypad2)) cmd.Blit(quantizedDctHandle, cameraTarget); // Pass 2 결과 (Quantized - 시각화 어려움)


                // 3. Pass 3: Dequantization + Column IDCT
                cmd.DispatchCompute(computeShader, kernelPass3, threadGroupsX, threadGroupsY, 1);
                Debug.Log($"[{profilerTag}] Dispatched Pass 3 ({threadGroupsX}x{threadGroupsY} groups).");

                if (Input.GetKey(KeyCode.Keypad3)) cmd.Blit(intermediateHandle, cameraTarget);   // Pass 1 결과 (CbCr - 시각화 어려움)


                // 4. Pass 4: Row IDCT + Combine + RGB Conversion
                cmd.DispatchCompute(computeShader, kernelPass4, threadGroupsX, threadGroupsY, 1);
                Debug.Log($"[{profilerTag}] Dispatched Pass 4 ({threadGroupsX}x{threadGroupsY} groups).");

                if (Input.GetKey(KeyCode.Keypad4)) cmd.Blit(finalOutputHandle, cameraTarget);    // Pass 4 결과 (최종) - 위 최종 Blit과 동일


                // 5. 최종 결과 (FinalOutput) 를 카메라 타겟으로 Blit
                // cmd.Blit(finalOutputHandle, cameraTarget);
                Debug.Log($"[{profilerTag}] Blitted final result from finalOutputHandle to camera target.");

                // --- 중간 결과 확인용 디버그 코드 (Input 키 사용) ---
                // 주의: 매 프레임 Input 체크는 성능에 영향 줄 수 있음. 디버깅 시에만 사용 권장.
                //       RInt 포맷(Quantized)은 직접 Blit 시 이상하게 보일 수 있음. 시각화용 셰이더 필요.
            }

            // CommandBuffer 실행 및 반환
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        /// <summary>
        /// 카메라 렌더링 끝난 후 호출됩니다. (여기서는 특별한 작업 없음)
        /// </summary>
        public override void OnCameraCleanup(CommandBuffer cmd) { }

        /// <summary>
        /// Render Pass가 더 이상 필요 없을 때 호출되어 할당된 모든 리소스를 해제합니다.
        /// </summary>
        public void Cleanup()
        {
            Debug.Log($"[{profilerTag}] Cleaning up Render Pass resources...");
            // Compute Buffer 해제
            ReleaseBitstreamBuffer();
            ReleaseQuantizationTableBuffer();

            // RTHandle 해제
            RTHandles.Release(sourceTextureHandle); sourceTextureHandle = null;
            RTHandles.Release(intermediateHandle); intermediateHandle = null;
            RTHandles.Release(quantizedDctHandle); quantizedDctHandle = null;
            RTHandles.Release(chromaBufferHandle); chromaBufferHandle = null;
            RTHandles.Release(finalOutputHandle); finalOutputHandle = null;
            Debug.Log($"[{profilerTag}] All RTHandles and Compute Buffers released.");
        }
    }
}