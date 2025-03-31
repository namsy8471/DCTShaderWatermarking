// ✅ Newtonsoft.Json 기반 OriginBlock (private 필드 + JSON 직렬화/복호화 지원)
using System;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using Newtonsoft.Json;

public class OriginBlock
{
    [JsonProperty] private List<string> mac_addresses = new();
    [JsonProperty] private List<string> ip_addresses = new();
    [JsonProperty] private List<string> user_domains = new();

    public string ToJson() => JsonConvert.SerializeObject(this);

    public static OriginBlock FromJson(string json) => JsonConvert.DeserializeObject<OriginBlock>(json);

    public byte[] Encrypt(string aesKey)
    {
        using Aes aes = Aes.Create();
        aes.Key = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(aesKey));
        aes.IV = new byte[16];

        using var encryptor = aes.CreateEncryptor();
        byte[] plainBytes = Encoding.UTF8.GetBytes(this.ToJson());
        return encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
    }

    public static OriginBlock Decrypt(byte[] encryptedData, string aesKey)
    {
        using Aes aes = Aes.Create();
        aes.Key = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(aesKey));
        aes.IV = new byte[16];

        using var decryptor = aes.CreateDecryptor();
        byte[] plainBytes = decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
        string json = Encoding.UTF8.GetString(plainBytes);
        return FromJson(json);
    }

    public static List<uint> ToBitstream(byte[] encryptedData)
    {
        List<uint> bits = new();
        foreach (byte b in encryptedData)
        {
            for (int i = 7; i >= 0; i--)
            {
                bits.Add((uint)((b >> i) & 1));
            }
        }
        return bits;
    }

    public static byte[] BitstreamToBytes(List<uint> bits)
    {
        int byteCount = bits.Count / 8;
        byte[] data = new byte[byteCount];
        for (int i = 0; i < byteCount; i++)
        {
            byte b = 0;
            for (int j = 0; j < 8; j++)
            {
                b |= (byte)(bits[i * 8 + j] << (7 - j));
            }
            data[i] = b;
        }
        return data;
    }

    public static OriginBlock Create()
    {
        var macs = GetMacAddresses();
        var ips = GetLocalIPAddresses();
        var domain = Environment.UserDomainName;

        return new OriginBlock
        {
            mac_addresses = macs,
            ip_addresses = ips,
            user_domains = new List<string> { domain }
        };
    }

    private static List<string> GetMacAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(nic => nic.GetPhysicalAddress().ToString())
            .Where(mac => !string.IsNullOrEmpty(mac))
            .Distinct().ToList();
    }

    private static List<string> GetLocalIPAddresses()
    {
        return Dns.GetHostAddresses(Dns.GetHostName())
            .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(ip => ip.ToString())
            .ToList();
    }
}
