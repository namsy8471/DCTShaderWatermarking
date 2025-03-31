// ✅ C# 코드: 문자열을 AES로 암호화한 뒤 bitstream으로 변환
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

public static class WatermarkEncoder
{
    // AES 암호화
    public static byte[] EncryptStringToBytes(string plainText, string keyString)
    {
        using Aes aes = Aes.Create();
        aes.Key = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(keyString));
        aes.IV = new byte[16]; // zero IV for simplicity (not secure for real apps)

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        return encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
    }

    // bitstream으로 변환
    public static List<uint> ConvertToBitstream(byte[] data)
    {
        List<uint> bits = new();
        foreach (byte b in data)
        {
            for (int i = 7; i >= 0; i--)
            {
                bits.Add((uint)((b >> i) & 1));
            }
        }
        return bits;
    }

    // 최종 통합 함수: 문자열을 암호화해서 bitstream으로 반환
    public static List<uint> EncodeWatermark(string input, string key)
    {
        byte[] encrypted = EncryptStringToBytes(input, key);
        return ConvertToBitstream(encrypted);
    }
}
