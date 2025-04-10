using System;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks; // 비동기 로딩을 위해 필요
using Newtonsoft.Json;        // JsonProperty 사용 위해 필요
using UnityEngine;
using UnityEngine.AddressableAssets; // Addressables 네임스페이스
using UnityEngine.ResourceManagement.AsyncOperations; // Addressables 네임스페이스

#if UNITY_EDITOR
using UnityEditor; // 에디터 전용 네임스페이스
#endif

[Serializable]
public class OriginBlock
{
    [JsonProperty] private List<string> mac_addresses = new();
    [JsonProperty] private string creation_time_utc = "";
    [JsonProperty] private string project_name = "";
    [JsonProperty] private string unityProjectID = ""; // 에디터에서만 채워짐

    // --- 동기화 및 프레이밍 상수 ---
    private const int SYNC_PATTERN_LENGTH = 64; // 동기화 패턴 길이 (비트 수)
    private const int LENGTH_FIELD_BITS = 16;  // 데이터 길이를 나타낼 비트 수 (최대 65535 비트 길이 지원)

    // --- 64비트 동기화 패턴 정의 (예시 값, 실제로는 더 랜덤하고 고유한 패턴 사용 권장) ---
    private static readonly List<uint> syncPattern = new List<uint> {
        1, 1, 1, 1, 0, 0, 0, 0, 1, 1, 1, 1, 0, 0, 0, 0, // 16
        1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, // 32
        0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, // 48
        1, 1, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 0  // 64
    }.Take(SYNC_PATTERN_LENGTH).ToList(); // 정의된 길이만큼만 사용


    // --- 핵심 데이터 및 암호화 메서드 ---
    public string ToJson() => JsonConvert.SerializeObject(this);
    public static OriginBlock FromJson(string json) => JsonConvert.DeserializeObject<OriginBlock>(json);

    public byte[] Encrypt(string aesKey)
    {
        using Aes aes = Aes.Create();
        aes.Key = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(aesKey));
        aes.IV = new byte[16]; // 고정 IV는 보안에 취약. 랜덤 IV 사용 및 함께 저장 권장.

        using var encryptor = aes.CreateEncryptor();
        byte[] plainBytes = Encoding.UTF8.GetBytes(this.ToJson());
        return encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
    }

    public static OriginBlock Decrypt(byte[] encryptedData, string aesKey)
    {
        using Aes aes = Aes.Create();
        aes.Key = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(aesKey));
        aes.IV = new byte[16]; // 암호화 시 사용한 IV와 일치해야 함

        using var decryptor = aes.CreateDecryptor();
        try
        {
            byte[] plainBytes = decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
            string json = Encoding.UTF8.GetString(plainBytes);
            return FromJson(json);
        }
        catch (CryptographicException ex)
        {
            Debug.LogError($"[OriginBlock] 복호화 실패. AES 키 또는 데이터 무결성을 확인하세요. 오류: {ex.Message}");
            return null;
        }
    }

    // --- 비트스트림 변환 유틸리티 ---
    public static List<uint> ToBitstream(byte[] data)
    {
        if (data == null) return new List<uint>();
        List<uint> bits = new List<uint>(data.Length * 8);
        foreach (byte b in data)
        {
            for (int i = 7; i >= 0; i--) // MSB first
            {
                bits.Add((uint)((b >> i) & 1));
            }
        }
        return bits;
    }

    public static byte[] BitstreamToBytes(List<uint> bits)
    {
        if (bits == null || bits.Count % 8 != 0)
        {
            Debug.LogError($"[OriginBlock] BitstreamToBytes: 비트 수가 8의 배수가 아닙니다 ({bits?.Count ?? 0}).");
            return Array.Empty<byte>();
        }
        int byteCount = bits.Count / 8;
        byte[] data = new byte[byteCount];
        for (int i = 0; i < byteCount; i++)
        {
            byte b = 0;
            int baseIndex = i * 8;
            for (int j = 0; j < 8; j++)
            {
                uint bit = bits[baseIndex + j] & 1;
                b |= (byte)(bit << (7 - j)); // MSB first reconstruction
            }
            data[i] = b;
        }
        return data;
    }

    // --- Helper: 정수를 지정된 비트 수의 List<uint>로 변환 ---
    private static List<uint> IntToBits(int value, int numBits)
    {
        if (value < 0 || value >= (1L << numBits)) // Use long (1L) for shift comparison to avoid overflow issue with numBits=32
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"값 {value}는 {numBits} 비트로 표현할 수 없습니다.");
        }
        List<uint> bits = new List<uint>(numBits);
        for (int i = numBits - 1; i >= 0; i--)
        {
            bits.Add((uint)((value >> i) & 1));
        }
        return bits;
    }

    // --- 새 함수: 암호화된 byte[] 데이터를 받아 헤더(동기화+길이)가 포함된 페이로드 비트 리스트 생성 ---
    /// <summary>
    /// 암호화된 데이터를 받아 동기화 패턴과 데이터 길이를 포함하는 페이로드 비트 리스트를 생성합니다.
    /// 최종 패딩/잘라내기는 이 함수 결과와 실제 이미지 용량을 비교하여 외부에서 수행해야 합니다.
    /// </summary>
    /// <param name="encryptedData">암호화된 데이터 바이트 배열</param>
    /// <returns> [동기화 패턴(64)] + [길이(16)] + [암호화 데이터 비트열] 형태의 List<uint> </returns>
    public static List<uint> ConstructPayloadWithHeader(byte[] encryptedData)
    {
        if (encryptedData == null)
        {
            Debug.LogError("[OriginBlock] ConstructPayloadWithHeader: 암호화된 데이터가 null입니다.");
            return new List<uint>();
        }

        // 1. 암호화된 데이터를 비트스트림으로 변환
        List<uint> encryptedBits = ToBitstream(encryptedData);
        int actualDataLength = encryptedBits.Count;

        // 1.1 데이터 길이가 길이 필드에 표현 가능한지 확인
        int maxLength = (1 << LENGTH_FIELD_BITS) - 1;
        if (actualDataLength > maxLength)
        {
            Debug.LogError($"[OriginBlock] 암호화된 데이터 길이({actualDataLength} bits)가 길이 필드({LENGTH_FIELD_BITS} bits)로 표현 가능한 최대치({maxLength})를 초과합니다.");
            return new List<uint>(); // 처리 불가
        }
        Debug.Log($"[OriginBlock] 원본 암호화 비트 길이: {actualDataLength} bits");

        // 2. 길이 정보를 비트스트림으로 변환
        List<uint> lengthBits = IntToBits(actualDataLength, LENGTH_FIELD_BITS);

        // 3. 최종 페이로드 구성: [동기화 패턴] + [길이] + [암호화 데이터]
        List<uint> payloadBits = new List<uint>(syncPattern); // 동기화 패턴 복사
        payloadBits.AddRange(lengthBits);                   // 길이 정보 추가
        payloadBits.AddRange(encryptedBits);                // 암호화 데이터 추가

        int totalPayloadLength = payloadBits.Count;
        Debug.Log($"[OriginBlock] 헤더 포함 페이로드 구성 완료: Sync({SYNC_PATTERN_LENGTH}) + Length({LENGTH_FIELD_BITS}) + Data({actualDataLength}) = {totalPayloadLength} bits");

        // 4. 최종 패딩/잘라내기는 이 함수를 호출한 외부(RenderPass 등)에서
        //    실제 이미지 용량(availableCapacity)과 이 totalPayloadLength를 비교하여 수행합니다.
        return payloadBits;
    }


    // --- Addressables를 사용한 런타임 로딩 로직 ---

    // !! 보안 경고 !!
    private const string RUNTIME_AES_KEY = "OriginBlockData"; // <<< --- 반드시 안전한 방법으로 교체하세요!!!

    // (동기) Addressables를 통해 암호화된 데이터 에셋(Base64)을 로드하고 원본 byte[]를 반환합니다.
    public static async Task<byte[]> LoadEncryptedDataBytesASync(string addressableKey)
    {
        byte[] encryptedData = null;
        AsyncOperationHandle<TextAsset> handle = Addressables.LoadAssetAsync<TextAsset>(addressableKey); ; // finally에서 사용하기 위해

        try
        {
            await handle.Task;

            if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
            {
                try
                {
                    string encoded = handle.Result.text;
                    encryptedData = Convert.FromBase64String(encoded);
                }
                catch (FormatException ex)
                {
                    Debug.LogError($"[OriginBlock] Base64 디코딩 실패 (Sync): {ex.Message}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[OriginBlock] 로드된 데이터 처리 중 오류 발생 (Sync): {ex.Message}");
                }
            }
            else
            {
                Debug.LogError($"[OriginBlock] Addressable 에셋 로드 실패 (Sync): {addressableKey}. Status: {handle.Status}, Error: {handle.OperationException}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[OriginBlock] LoadEncryptedDataBytesSync 중 예외 발생: {ex.Message}");
        }
        finally
        {
            if (handle.IsValid())
            {
                Addressables.Release(handle); // 핸들 해제!
            }
        }
        return encryptedData; // 성공 시 byte[], 실패 시 null 반환
    }

    // --- 이전에 있던 GetBitstreamRuntimeSync 함수는 이제 역할이 변경/축소됨 ---
    // 필요하다면 이 함수는 LoadEncryptedDataBytesSync 와 ConstructPayloadWithHeader 를
    // 조합하여 호출하는 형태로 유지할 수도 있으나, 패딩 로직은 분리하는 것이 좋습니다.
    // 여기서는 일단 LoadEncryptedDataBytesSync/Async 를 직접 사용하는 것을 권장하며
    // 기존 GetBitstreamRuntimeSync 는 삭제하거나 주석 처리합니다.
    /*
    public static List<uint> GetBitstreamRuntimeSync(string addressableKey)
    {
        // ... 이 함수는 이제 사용하지 않거나 역할 변경 ...
        // byte[] encryptedData = LoadEncryptedDataBytesSync(addressableKey);
        // // 패딩 로직은 여기서 하지 않음!
        // // List<uint> bits = ToBitstream(encryptedData);
        // // return bits;
        return null; // 또는 다른 역할로 변경
    }
    */


    // --- 에디터 전용 생성 로직 ---
#if UNITY_EDITOR

    // --- 에디터 생성용 상수 ---
    private const string EDITOR_SAVE_PATH = "Assets/GameData/OriginBlockData.bytes"; // .bytes 또는 .txt 권장
    public const string ADDRESSABLE_KEY = "OriginBlockData"; // 런타임 로딩 시 사용할 키

    // 에디터에서 OriginBlock 데이터를 생성합니다.
    private static OriginBlock CreateEditorData()
    {
        var macs = GetMacAddresses();
        string creationUtc;
        try
        {
            creationUtc = File.GetCreationTimeUtc("Assets").ToString("o");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[OriginBlock] Assets 폴더 생성 시간을 가져올 수 없습니다: {ex.Message}");
            creationUtc = DateTime.UtcNow.ToString("o");
        }
        string projectID = UnityEditor.CloudProjectSettings.projectId;
        if (string.IsNullOrEmpty(projectID))
        {
            Debug.LogWarning("[OriginBlock] Unity Cloud Project ID를 찾을 수 없습니다.");
        }
        return new OriginBlock
        {
            mac_addresses = macs,
            creation_time_utc = creationUtc,
            project_name = Application.productName,
            unityProjectID = projectID
        };
    }

    // MAC 주소 가져오기 (에디터 전용 헬퍼)
    private static List<string> GetMacAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
               .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                             nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                             nic.GetPhysicalAddress() != null)
               .Select(nic => nic.GetPhysicalAddress().ToString())
               .Where(mac => !string.IsNullOrEmpty(mac) && mac.Length >= 12)
               .Distinct().ToList();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[OriginBlock] MAC 주소를 가져오는 중 오류 발생: {ex.Message}");
            return new List<string>();
        }
    }

    // Addressable 에셋으로 OriginBlock 데이터를 생성하고 저장합니다 (에디터 전용)
    [MenuItem("Tools/OriginBlock/OriginBlock 데이터 생성")]
    public static void GenerateAndSaveDataEditor()
    {
        // !! 보안 경고 !! 에디터용 키도 안전하게 관리해야 합니다.
        string editorAesKey = "OriginBlockData"; // <<< --- 에디터용으로 교체 권장
        Debug.LogWarning("임시 에디터 AES 키를 사용 중입니다. 교체하세요.");

        Debug.Log("[OriginBlock] OriginBlock 데이터 생성 중...");
        var block = CreateEditorData();
        if (block == null) { /* 오류 처리 */ return; }

        byte[] encrypted = block.Encrypt(editorAesKey);
        string encodedInBase64 = Convert.ToBase64String(encrypted); // Base64로 인코딩

        string directory = Path.GetDirectoryName(EDITOR_SAVE_PATH);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            // .bytes 확장자를 사용해도 Unity는 TextAsset으로 인식 가능
            File.WriteAllText(EDITOR_SAVE_PATH, encodedInBase64);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[OriginBlock] 파일 쓰기 실패: {EDITOR_SAVE_PATH}. 오류: {ex.Message}");
            return;
        }

        AssetDatabase.ImportAsset(EDITOR_SAVE_PATH);
        Debug.Log($"[OriginBlock] ✅ OriginBlock 데이터 생성 완료: {EDITOR_SAVE_PATH}");
        Debug.Log($"[OriginBlock] ➡️ 중요: Addressables Groups 창에서 '{Path.GetFileName(EDITOR_SAVE_PATH)}'을 추가하고 Address를 '{ADDRESSABLE_KEY}'로 설정하세요.");
    }

#endif // UNITY_EDITOR
}