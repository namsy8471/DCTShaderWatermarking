using UnityEngine;
using System;
using System.Collections.Generic;

/// <summary>
/// Utility class for generating spread spectrum patterns used in watermarking
/// </summary>
public static class SpreadSpectrumPatternGenerator
{
    /// <summary>
    /// Generate a binary spread spectrum pattern (-1 or +1) based on a secret key
    /// </summary>
    /// <param name="size">Total number of pattern values to generate</param>
    /// <param name="secretKey">Secret key for PRNG seeding</param>
    /// <returns>Array of pattern values (-1.0f or +1.0f)</returns>
    public static float[] GenerateBinaryPattern(int size, string secretKey)
    {
        if (size <= 0)
        {
            Debug.LogWarning("[SpreadSpectrumPatternGenerator] Invalid size: " + size);
            return Array.Empty<float>();
        }

        if (string.IsNullOrEmpty(secretKey))
        {
            Debug.LogWarning("[SpreadSpectrumPatternGenerator] Secret key is null or empty, using default");
            secretKey = "default_key";
        }

        float[] pattern = new float[size];
        System.Random prng = new System.Random(secretKey.GetHashCode());

        for (int i = 0; i < size; i++)
        {
            pattern[i] = (prng.NextDouble() < 0.5) ? -1.0f : 1.0f;
        }

        return pattern;
    }

    /// <summary>
    /// Generate a Gaussian spread spectrum pattern
    /// </summary>
    /// <param name="size">Total number of pattern values to generate</param>
    /// <param name="secretKey">Secret key for PRNG seeding</param>
    /// <param name="mean">Mean of the Gaussian distribution (default: 0)</param>
    /// <param name="stdDev">Standard deviation of the Gaussian distribution (default: 1)</param>
    /// <returns>Array of Gaussian-distributed pattern values</returns>
    public static float[] GenerateGaussianPattern(int size, string secretKey, float mean = 0f, float stdDev = 1f)
    {
        if (size <= 0)
        {
            Debug.LogWarning("[SpreadSpectrumPatternGenerator] Invalid size: " + size);
            return Array.Empty<float>();
        }

        if (string.IsNullOrEmpty(secretKey))
        {
            Debug.LogWarning("[SpreadSpectrumPatternGenerator] Secret key is null or empty, using default");
            secretKey = "default_key";
        }

        float[] pattern = new float[size];
        System.Random prng = new System.Random(secretKey.GetHashCode());

        for (int i = 0; i < size; i++)
        {
            // Box-Muller transform for Gaussian distribution
            double u1 = prng.NextDouble();
            double u2 = prng.NextDouble();
            double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            pattern[i] = mean + stdDev * (float)randStdNormal;
        }

        return pattern;
    }

    /// <summary>
    /// Generate a normalized pattern in range [min, max]
    /// </summary>
    /// <param name="size">Total number of pattern values to generate</param>
    /// <param name="secretKey">Secret key for PRNG seeding</param>
    /// <param name="min">Minimum value (default: -1)</param>
    /// <param name="max">Maximum value (default: 1)</param>
    /// <returns>Array of uniformly distributed pattern values in [min, max]</returns>
    public static float[] GenerateUniformPattern(int size, string secretKey, float min = -1f, float max = 1f)
    {
        if (size <= 0)
        {
            Debug.LogWarning("[SpreadSpectrumPatternGenerator] Invalid size: " + size);
            return Array.Empty<float>();
        }

        if (string.IsNullOrEmpty(secretKey))
        {
            Debug.LogWarning("[SpreadSpectrumPatternGenerator] Secret key is null or empty, using default");
            secretKey = "default_key";
        }

        float[] pattern = new float[size];
        System.Random prng = new System.Random(secretKey.GetHashCode());
        float range = max - min;

        for (int i = 0; i < size; i++)
        {
            pattern[i] = min + (float)prng.NextDouble() * range;
        }

        return pattern;
    }

    /// <summary>
    /// Create a ComputeBuffer from a pattern array
    /// </summary>
    /// <param name="pattern">Pattern data array</param>
    /// <returns>ComputeBuffer containing the pattern data, or null on error</returns>
    public static ComputeBuffer CreatePatternBuffer(float[] pattern)
    {
        if (pattern == null || pattern.Length == 0)
        {
            Debug.LogWarning("[SpreadSpectrumPatternGenerator] Cannot create buffer from null or empty pattern");
            return null;
        }

        try
        {
            ComputeBuffer buffer = new ComputeBuffer(pattern.Length, sizeof(float), ComputeBufferType.Structured, ComputeBufferMode.Immutable);
            buffer.SetData(pattern);
            return buffer;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[SpreadSpectrumPatternGenerator] Failed to create pattern buffer: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Validate pattern buffer state
    /// </summary>
    /// <param name="buffer">Buffer to validate</param>
    /// <param name="expectedSize">Expected buffer size (optional, -1 to skip check)</param>
    /// <returns>True if buffer is valid</returns>
    public static bool ValidatePatternBuffer(ComputeBuffer buffer, int expectedSize = -1)
    {
        if (buffer == null)
            return false;

        if (!buffer.IsValid())
            return false;

        if (expectedSize > 0 && buffer.count != expectedSize)
        {
            Debug.LogWarning($"[SpreadSpectrumPatternGenerator] Buffer size mismatch: expected {expectedSize}, got {buffer.count}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Calculate required pattern size for block-based embedding
    /// </summary>
    /// <param name="width">Image width</param>
    /// <param name="height">Image height</param>
    /// <param name="blockSize">Block size (default: 8)</param>
    /// <param name="coefficientsPerBlock">Number of coefficients to embed per block</param>
    /// <returns>Total pattern size required</returns>
    public static int CalculatePatternSize(int width, int height, int blockSize, int coefficientsPerBlock)
    {
        int numBlocksX = Mathf.CeilToInt((float)width / blockSize);
        int numBlocksY = Mathf.CeilToInt((float)height / blockSize);
        int totalBlocks = numBlocksX * numBlocksY;
        return totalBlocks * coefficientsPerBlock;
    }

    /// <summary>
    /// Debug log first N values of a pattern
    /// </summary>
    /// <param name="pattern">Pattern to log</param>
    /// <param name="count">Number of values to log (default: 64)</param>
    /// <param name="tag">Tag for log message</param>
    public static void LogPattern(float[] pattern, int count = 64, string tag = "Pattern")
    {
        if (pattern == null || pattern.Length == 0)
        {
            Debug.Log($"[{tag}] Pattern is null or empty");
            return;
        }

        int logLength = Mathf.Min(count, pattern.Length);
        var values = new List<string>(logLength);
        
        for (int i = 0; i < logLength; i++)
        {
            values.Add(pattern[i].ToString("F2"));
        }

        Debug.Log($"[{tag}] First {logLength}/{pattern.Length} values: [{string.Join(", ", values)}]");
    }
}
