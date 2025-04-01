// ✅ 불변 OriginBlock 생성기: 최초 1회 생성
using System;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using Newtonsoft.Json;
using UnityEngine;
using UnityEditor;

public class OriginBlock
{
    [JsonProperty] private List<string> mac_addresses = new();  // 기기의 모든 MAC 주소
    [JsonProperty] private string creation_time_utc = "";    // 파일 생성 시각
    [JsonProperty] private string project_name = "";         // 대시보드에 저장된 프로젝트 이름
    [JsonProperty] private string unityProjectID = "";         // 대시보드에 저장된 프로젝트 ID

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

    public static OriginBlock Create(string originAssetPath)
    {
        var macs = GetMacAddresses();

        // 애셋 폴더 생성 시간(에셋 폴더는 유니티 생성 시 같이 생성되기 때문)
        string creationUtc = File.GetCreationTimeUtc("Assets").ToString("o");

        return new OriginBlock
        {
            mac_addresses = macs,
            creation_time_utc = creationUtc,
            project_name = Application.productName,
            unityProjectID = CloudProjectSettings.projectId
        };
    }

    private static List<string> GetMacAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
        .Where(nic =>
            nic.OperationalStatus == OperationalStatus.Up &&
            nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(nic => nic.GetPhysicalAddress().ToString())
        .Where(mac => !string.IsNullOrEmpty(mac))
        .Distinct().ToList();
    }

    // ✅ 최초 1회 .bytes 위장 저장 함수
    public static void GenerateAndSave()
    {
        string fileName = Application.productName;
        string aesKey = Environment.UserName;

        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string path = Path.Combine(projectRoot, fileName);

        if (File.Exists(path)) return;

        var block = Create(path);
        byte[] encrypted = block.Encrypt(aesKey);
        string encodedInBase64 = Convert.ToBase64String(encrypted);
        File.WriteAllText(path, encodedInBase64);

        var attr = File.GetAttributes(path);
        File.SetAttributes(path, attr | FileAttributes.Hidden | FileAttributes.ReadOnly);

        Debug.Log("✅ OriginBlock 저장 완료 (숨김+읽기전용): " + path);
    }

    // ✅ .bytes 파일을 로드하고 복호화
    public static void LoadAndDecrypt()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string decodingFileName = Path.Combine(projectRoot, Application.productName);

        string aesKey = Environment.UserName;

        Debug.Log("🔍 OriginBlock 복호화 시작...");

        if (!File.Exists(decodingFileName))
        {
            Debug.LogError("❌ " + decodingFileName + "에서 파일을 찾을 수 없습니다.");
            return;
        }

        string encoded = File.ReadAllText(decodingFileName);

        try
        {
            byte[] decodedFromBase64 = Convert.FromBase64String(encoded);
            OriginBlock block = Decrypt(decodedFromBase64, aesKey);

            string json = block.ToJson();
            string absPath = Path.Combine(Application.dataPath, "Scripts/LSB/OriginBlockRecovery.json");

            string dir = Path.GetDirectoryName(absPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(absPath, json);
            Debug.Log("✅ 복호화 완료! JSON 저장 위치: " + absPath);
            AssetDatabase.Refresh();
        }
        catch (Exception ex)
        {
            Debug.LogError("⚠️ 복호화 중 오류 발생: " + ex.Message);
        }
    }

    public static List<uint> GetBitstream()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string decodingFileName = Path.Combine(projectRoot, Application.productName);

        if (!File.Exists(decodingFileName))
        {
            Debug.LogError("❌ OriginBlock 파일이 존재하지 않습니다: " + decodingFileName);
            return null;
        }

        string encoded = File.ReadAllText(decodingFileName);
        byte[] encrypted = Convert.FromBase64String(encoded);
        
        List<uint> bits = ToBitstream(encrypted);

        int targetBitLength = Screen.width * Screen.height;
        
        while(bits.Count < targetBitLength)
        {
            Debug.LogError("❌ OriginBlock 비트스트림 길이 부족: " + bits.Count + " < " + targetBitLength);
            bits.AddRange(bits);
            Debug.LogError("❌ OriginBlock 비트스트림 길이 증량, 현재 비트스트림 길이 " + bits.Count);
        }

        bits = bits.Take(targetBitLength).ToList(); // 딱 맞게 자름

        return bits;
    }
}
