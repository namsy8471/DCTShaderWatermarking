using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System;
using System.Linq;

/// <summary>
/// Base class for watermark render features with common timing and activation logic
/// 
/// IMPORTANT: Requires GameInitializer MonoBehaviour in the scene to load watermark data.
/// If data isn't loaded, renders will proceed without watermarking.
/// </summary>
public abstract class WatermarkRenderFeatureBase : ScriptableRendererFeature
{
    [Header("General Settings")]
    [Tooltip("Enable watermark embedding")]
    public bool embedBitstream = true;

    [Header("Timing Settings")]
    [Range(0, 1)]
    [Tooltip("Duration to display watermark (0 = always off, 1 = always on)")]
    public float displayDuration = 0.02f;

    private float lastTime;
    private float interval;
    private bool isWatermarkActive = false;

    protected abstract WatermarkRenderPassBase CreateRenderPass();
    protected abstract ComputeShader GetComputeShader();
    protected abstract string GetFeatureName();

    public override void Create()
    {
        if (GetComputeShader() == null)
        {
            Debug.LogError($"[{GetFeatureName()}] Compute Shader가 할당되지 않았습니다.");
            return;
        }

        try
        {
            interval = 1.0f - displayDuration;
            lastTime = Time.time - interval;
            
            var pass = CreateRenderPass();
            if (pass != null)
            {
                pass.renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
                Debug.Log($"[{GetFeatureName()}] Render Pass 생성 완료.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{GetFeatureName()}] Render Pass 생성 실패: {ex.Message}\n{ex.StackTrace}");
        }
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var camera = renderingData.cameraData.camera;
        if (camera.cameraType != CameraType.Game) return;

        var computeShader = GetComputeShader();
        var renderPass = GetRenderPass();
        
        // Early return only if compute shader or render pass is null
        // Don't check DataManager.IsDataReady here - let the render passes handle it internally
        if (computeShader == null || renderPass == null)
            return;

        // ? interval을 매 프레임 계산하여 displayDuration 변경 즉시 반영
        interval = 1.0f - displayDuration;

        // Handle watermark timing
        if (!isWatermarkActive || displayDuration == 0)
        {
            if (Time.time - lastTime >= interval)
            {
                isWatermarkActive = true;
                lastTime = Time.time;
                Debug.Log($"워터마킹 활성화 {displayDuration}초 동안");
            }
            else
            {
                return;
            }
        }
        else
        {
            if (Time.time - lastTime >= displayDuration && displayDuration != 1)
            {
                isWatermarkActive = false;
                lastTime = Time.time;
                Debug.Log($"워터마킹 비활성화 {interval}초 동안");
                return;
            }
        }

        // Update parameters and enqueue pass
        renderPass.SetEmbedActive(embedBitstream);
        UpdateRenderPassParameters(renderPass);
        renderer.EnqueuePass(renderPass);
    }

    protected abstract WatermarkRenderPassBase GetRenderPass();
    protected abstract void UpdateRenderPassParameters(WatermarkRenderPassBase pass);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            GetRenderPass()?.Cleanup();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Base class for watermark render passes with common buffer management
/// 
/// NOTE: Render passes will execute even if DataManager.IsDataReady is false.
/// The Embed flag in shaders controls whether watermark is actually applied.
/// This ensures the scene renders correctly while data is loading.
/// </summary>
public abstract class WatermarkRenderPassBase : ScriptableRenderPass
{
    protected ComputeShader computeShader;
    protected string profilerTag;
    protected ProfilingSampler profilingSampler;
    protected bool embedActive;

    // Common buffers
    protected ComputeBuffer bitstreamBuffer;
    protected List<uint> finalBitsToEmbed;
    
    // ? 추가: 더미 버퍼 (셰이더 오류 방지용)
    private static ComputeBuffer dummyBitstreamBuffer;

    protected const int BLOCK_SIZE = 8;

    protected WatermarkRenderPassBase(ComputeShader shader, string tag, bool initialEmbedState)
    {
        computeShader = shader;
        profilerTag = tag;
        profilingSampler = new ProfilingSampler(tag);
        embedActive = initialEmbedState;
        finalBitsToEmbed = new List<uint>();
        
        // ? 더미 버퍼 초기화 (한 번만)
        EnsureDummyBufferExists();
    }

    // ? 더미 버퍼 생성 (모든 인스턴스가 공유)
    private static void EnsureDummyBufferExists()
    {
        if (dummyBitstreamBuffer == null || !dummyBitstreamBuffer.IsValid())
        {
            dummyBitstreamBuffer = new ComputeBuffer(1, sizeof(uint), ComputeBufferType.Structured);
            dummyBitstreamBuffer.SetData(new uint[] { 0 });
        }
    }

    // ? 더미 버퍼 반환 (null 체크용)
    protected ComputeBuffer GetValidBitstreamBuffer()
    {
        EnsureDummyBufferExists();
        return (bitstreamBuffer != null && bitstreamBuffer.IsValid()) ? bitstreamBuffer : dummyBitstreamBuffer;
    }

    public virtual void SetEmbedActive(bool isActive)
    {
        embedActive = isActive;
    }

    /// <summary>
    /// Prepare bitstream buffer with payload data
    /// </summary>
    protected void PrepareBitstreamBuffer(int totalBlocks)
    {
        if (finalBitsToEmbed.Count == totalBlocks)
            return;

        finalBitsToEmbed.Clear();

        if (!embedActive || !DataManager.IsDataReady || DataManager.EncryptedOriginData == null)
            return;

        try
        {
            List<uint> currentPayload = OriginBlock.ConstructPayloadWithHeader(DataManager.EncryptedOriginData);
            
            if (currentPayload == null || currentPayload.Count == 0)
            {
                Debug.LogWarning($"[{profilerTag}] Payload 구성 실패");
                return;
            }

            // Repeat payload to fill required capacity
            int loops = Mathf.CeilToInt((float)totalBlocks / currentPayload.Count);
            for (int i = 0; i < loops && finalBitsToEmbed.Count < totalBlocks; ++i)
            {
                finalBitsToEmbed.AddRange(currentPayload);
            }

            // Trim to exact size
            if (finalBitsToEmbed.Count > totalBlocks)
            {
                finalBitsToEmbed = finalBitsToEmbed.Take(totalBlocks).ToList();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{profilerTag}] Payload 준비 중 오류: {ex.Message}");
            finalBitsToEmbed.Clear();
        }
    }

    /// <summary>
    /// Update compute buffer with bitstream data
    /// </summary>
    protected void UpdateBitstreamBuffer(List<uint> data)
    {
        int count = data?.Count ?? 0;
        
        if (count == 0)
        {
            ReleaseBitstreamBuffer();
            return;
        }

        if (bitstreamBuffer == null || !bitstreamBuffer.IsValid() || bitstreamBuffer.count != count)
        {
            ReleaseBitstreamBuffer();
            try
            {
                bitstreamBuffer = new ComputeBuffer(count, sizeof(uint), ComputeBufferType.Structured, ComputeBufferMode.Immutable);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{profilerTag}] Bitstream Buffer 생성 실패 (Size:{count}): {ex.Message}");
                bitstreamBuffer = null;
                return;
            }
        }

        if (bitstreamBuffer != null && bitstreamBuffer.IsValid())
        {
            try
            {
                bitstreamBuffer.SetData(data);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{profilerTag}] Bitstream Buffer SetData 실패: {ex.Message}");
                ReleaseBitstreamBuffer();
            }
        }
    }

    protected void ReleaseBitstreamBuffer()
    {
        bitstreamBuffer?.Release();
        bitstreamBuffer = null;
    }

    /// <summary>
    /// Calculate thread groups for compute shader dispatch
    /// </summary>
    protected (int x, int y) CalculateThreadGroups(int width, int height, int groupSize = BLOCK_SIZE)
    {
        int threadGroupsX = (width + groupSize - 1) / groupSize;
        int threadGroupsY = (height + groupSize - 1) / groupSize;
        return (threadGroupsX, threadGroupsY);
    }

    /// <summary>
    /// Check if embedding should be performed on GPU
    /// </summary>
    protected bool ShouldEmbedOnGPU(int currentBitLength)
    {
        bool bitstreamValid = bitstreamBuffer != null && 
                             bitstreamBuffer.IsValid() && 
                             bitstreamBuffer.count >= currentBitLength;
        
        return embedActive && 
               DataManager.IsDataReady && 
               bitstreamValid && 
               currentBitLength > 0;
    }

    public virtual void Cleanup()
    {
        ReleaseBitstreamBuffer();
    }
    
    // ? 정적 더미 버퍼 정리 (애플리케이션 종료 시)
    public static void CleanupStaticResources()
    {
        dummyBitstreamBuffer?.Release();
        dummyBitstreamBuffer = null;
    }
}
