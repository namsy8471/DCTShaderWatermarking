# Watermark Renderer Feature - Developer Guide

## Quick Start

### Creating a New Watermark Feature

1. **Create a new feature class**:
```csharp
using UnityEngine;
using UnityEngine.Rendering.Universal;

public class MyWatermarkFeature : WatermarkRenderFeatureBase
{
    [Header("¼ÎÀÌ´õ")]
    public ComputeShader myComputeShader;
    
    [Header("¼³Á¤")]
    public float myParameter = 1.0f;
    
    private MyRenderPass myRenderPass;
    
    protected override ComputeShader GetComputeShader() => myComputeShader;
    protected override string GetFeatureName() => name;
    protected override WatermarkRenderPassBase GetRenderPass() => myRenderPass;
    
    protected override WatermarkRenderPassBase CreateRenderPass()
    {
        myRenderPass = new MyRenderPass(myComputeShader, name, embedBitstream, myParameter);
        return myRenderPass;
    }
    
    protected override void UpdateRenderPassParameters(WatermarkRenderPassBase pass)
    {
        if (pass is MyRenderPass myPass)
        {
            myPass.UpdateParameter(myParameter);
        }
    }
    
    // Inner render pass class
    class MyRenderPass : WatermarkRenderPassBase
    {
        // Your implementation here
    }
}
```

2. **Implement the render pass**:
```csharp
class MyRenderPass : WatermarkRenderPassBase
{
    private int kernelID;
    private RTHandle inputHandle, outputHandle;
    private float parameter;
    
    public MyRenderPass(ComputeShader shader, string tag, bool embedState, float param)
        : base(shader, tag, embedState)
    {
        parameter = param;
        kernelID = shader.FindKernel("MyKernel");
    }
    
    public void UpdateParameter(float param)
    {
        parameter = param;
    }
    
    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
    {
        // Setup RT handles
        var desc = renderingData.cameraData.cameraTargetDescriptor;
        RenderingUtils.ReAllocateIfNeeded(ref inputHandle, desc, FilterMode.Point, name: "_Input");
        RenderingUtils.ReAllocateIfNeeded(ref outputHandle, desc, FilterMode.Point, name: "_Output");
        
        // Prepare bitstream
        int totalPixels = desc.width * desc.height;
        if (finalBitsToEmbed.Count != totalPixels)
        {
            PrepareBitstreamBuffer(totalPixels);
            UpdateBitstreamBuffer(finalBitsToEmbed);
        }
        
        // Set shader parameters
        bool shouldEmbed = ShouldEmbedOnGPU(finalBitsToEmbed.Count);
        cmd.SetComputeFloatParam(computeShader, "MyParameter", parameter);
        cmd.SetComputeIntParam(computeShader, "Embed", shouldEmbed ? 1 : 0);
        // ... more parameters
    }
    
    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        CommandBuffer cmd = CommandBufferPool.Get(profilerTag);
        var cameraTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
        
        using (new ProfilingScope(cmd, profilingSampler))
        {
            var (threadsX, threadsY) = CalculateThreadGroups(width, height);
            cmd.DispatchCompute(computeShader, kernelID, threadsX, threadsY, 1);
        }
        
        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }
    
    public override void Cleanup()
    {
        base.Cleanup();
        RTHandles.Release(inputHandle); inputHandle = null;
        RTHandles.Release(outputHandle); outputHandle = null;
    }
}
```

## Base Class Reference

### WatermarkRenderFeatureBase

**Purpose**: Handles timing and activation logic

**Public Properties**:
- `bool embedBitstream` - Enable/disable embedding
- `float displayDuration` - Duration to show watermark (0-1)

**Abstract Methods You Must Implement**:
- `CreateRenderPass()` - Create your render pass instance
- `GetComputeShader()` - Return your compute shader
- `GetFeatureName()` - Return feature name for logging
- `GetRenderPass()` - Return your render pass instance
- `UpdateRenderPassParameters()` - Update parameters each frame

### WatermarkRenderPassBase

**Purpose**: Handles common buffer and embedding logic

**Protected Members**:
- `ComputeShader computeShader` - Your compute shader
- `string profilerTag` - Tag for profiling
- `ProfilingSampler profilingSampler` - Unity profiler sampler
- `bool embedActive` - Current embed state
- `ComputeBuffer bitstreamBuffer` - Bitstream data buffer
- `List<uint> finalBitsToEmbed` - Final bitstream to embed
- `const int BLOCK_SIZE = 8` - Standard block size

**Protected Methods You Can Use**:
- `PrepareBitstreamBuffer(int totalBlocks)` - Prepare payload data
- `UpdateBitstreamBuffer(List<uint> data)` - Update compute buffer
- `ReleaseBitstreamBuffer()` - Clean up buffer
- `CalculateThreadGroups(int width, int height, int groupSize = 8)` - Calculate dispatch groups
- `ShouldEmbedOnGPU(int currentBitLength)` - Check if embedding should proceed

**Methods You Must Implement**:
- `OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)` - Setup resources
- `Execute(ScriptableRenderContext context, ref RenderingData renderingData)` - Execute rendering
- `OnCameraCleanup(CommandBuffer cmd)` - Per-frame cleanup
- `Cleanup()` - Full cleanup (remember to call `base.Cleanup()`)

## Utility Classes

### SpreadSpectrumPatternGenerator

**Purpose**: Generate patterns for spread spectrum embedding

**Static Methods**:

```csharp
// Binary pattern (-1 or +1)
float[] pattern = SpreadSpectrumPatternGenerator.GenerateBinaryPattern(size, secretKey);

// Gaussian pattern
float[] pattern = SpreadSpectrumPatternGenerator.GenerateGaussianPattern(size, secretKey);

// Uniform pattern in range [min, max]
float[] pattern = SpreadSpectrumPatternGenerator.GenerateUniformPattern(size, secretKey, -1f, 1f);

// Create ComputeBuffer from pattern
ComputeBuffer buffer = SpreadSpectrumPatternGenerator.CreatePatternBuffer(pattern);

// Validate buffer
bool isValid = SpreadSpectrumPatternGenerator.ValidatePatternBuffer(buffer, expectedSize);

// Calculate required pattern size
int size = SpreadSpectrumPatternGenerator.CalculatePatternSize(width, height, blockSize, coeffsPerBlock);

// Debug logging
SpreadSpectrumPatternGenerator.LogPattern(pattern, 64, "MyPattern");
```

## Common Patterns

### Pattern 1: Simple Single-Pass Embedding

```csharp
public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
{
    var desc = renderingData.cameraData.cameraTargetDescriptor;
    RenderingUtils.ReAllocateIfNeeded(ref outputHandle, desc, FilterMode.Point);
    
    int capacity = desc.width * desc.height;
    PrepareBitstreamBuffer(capacity);
    UpdateBitstreamBuffer(finalBitsToEmbed);
    
    bool shouldEmbed = ShouldEmbedOnGPU(finalBitsToEmbed.Count);
    cmd.SetComputeIntParam(computeShader, "Embed", shouldEmbed ? 1 : 0);
    cmd.SetComputeBufferParam(computeShader, kernelID, "Bitstream", bitstreamBuffer);
}
```

### Pattern 2: Multi-Pass with Pattern Buffer

```csharp
private ComputeBuffer patternBuffer;
private string lastSecretKey;

public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
{
    // Prepare bitstream
    int totalBlocks = CalculateTotalBlocks(desc.width, desc.height);
    PrepareBitstreamBuffer(totalBlocks);
    UpdateBitstreamBuffer(finalBitsToEmbed);
    
    // Prepare pattern buffer
    if (patternBuffer == null || lastSecretKey != currentSecretKey)
    {
        int patternSize = totalBlocks * coefficientsToUse;
        float[] pattern = SpreadSpectrumPatternGenerator.GenerateBinaryPattern(patternSize, currentSecretKey);
        patternBuffer?.Release();
        patternBuffer = SpreadSpectrumPatternGenerator.CreatePatternBuffer(pattern);
        lastSecretKey = currentSecretKey;
    }
    
    // Set parameters
    cmd.SetComputeBufferParam(computeShader, kernelID, "Bitstream", bitstreamBuffer);
    cmd.SetComputeBufferParam(computeShader, kernelID, "PatternBuffer", patternBuffer);
}

public override void Cleanup()
{
    base.Cleanup();
    patternBuffer?.Release();
    patternBuffer = null;
}
```

### Pattern 3: Resolution Change Detection

```csharp
private int lastWidth, lastHeight;

public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
{
    int width = renderingData.cameraData.cameraTargetDescriptor.width;
    int height = renderingData.cameraData.cameraTargetDescriptor.height;
    
    if (width != lastWidth || height != lastHeight)
    {
        // Re-prepare everything
        int capacity = CalculateCapacity(width, height);
        PrepareBitstreamBuffer(capacity);
        UpdateBitstreamBuffer(finalBitsToEmbed);
        
        lastWidth = width;
        lastHeight = height;
    }
}
```

## Best Practices

### 1. Resource Management
- Always call `base.Cleanup()` in your Cleanup() method
- Release all RTHandles and ComputeBuffers in Cleanup()
- Use `RenderingUtils.ReAllocateIfNeeded()` for RTHandles
- Check buffer validity before use: `buffer != null && buffer.IsValid()`

### 2. Error Handling
```csharp
try
{
    computeBuffer = new ComputeBuffer(size, sizeof(float));
    computeBuffer.SetData(data);
}
catch (Exception ex)
{
    Debug.LogError($"[{profilerTag}] Buffer creation failed: {ex.Message}");
    computeBuffer?.Release();
    computeBuffer = null;
}
```

### 3. Performance
- Cache kernel IDs in constructor
- Avoid allocating new objects in Execute()
- Use `ProfilingScope` for profiling
- Only update buffers when data changes

### 4. Debugging
```csharp
// Use Input keys for debug visualization (in Execute)
if (Input.GetKey(KeyCode.F1))
{
    cmd.Blit(intermediateHandle, cameraTarget);
    // Display intermediate results
}
```

### 5. Shader Parameters
- Set global parameters (Width, Height) once
- Set kernel-specific parameters per kernel
- Always check if embedding should occur before setting buffers

## Testing Checklist

- [ ] Feature appears in URP Renderer settings
- [ ] Inspector shows all parameters correctly
- [ ] Watermark displays when enabled
- [ ] Watermark timing works (displayDuration)
- [ ] Resolution changes handled correctly
- [ ] No memory leaks (check Profiler)
- [ ] No errors in Console
- [ ] Build succeeds
- [ ] Performance is acceptable

## Troubleshooting

### "ComputeBuffer is null or invalid"
- Check if DataManager.IsDataReady is true
- Verify PrepareBitstreamBuffer() is called
- Check buffer creation in try-catch

### "Kernel not found"
- Verify kernel name matches compute shader
- Check compute shader is assigned in Inspector
- Look for typos in kernel names

### "No visual effect"
- Check if embedBitstream is enabled
- Verify displayDuration > 0
- Check if DataManager has loaded data
- Use debug keys to verify shader is running

### "Performance issues"
- Profile with Unity Profiler
- Check if buffers are recreated every frame
- Verify thread group calculations
- Consider reducing coefficientsToUse

## Examples

See existing implementations:
- **DCT_NEW.cs** - 4-pass DCT/IDCT with spread spectrum
- **DWT_SS.cs** - 4-pass DWT/IDWT with HH coefficient embedding
- **LSB_NEW_RenderPass.cs** - Simple single-pass pixel embedding

## Support

For questions or issues:
1. Check REFACTORING_SUMMARY.md
2. Review existing implementations
3. Check Unity Console for errors
4. Use Unity Profiler for performance analysis
