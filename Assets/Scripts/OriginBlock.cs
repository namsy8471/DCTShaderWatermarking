using System;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks; // 비동기 로딩을 위해 필요
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.AddressableAssets; // Addressables 네임스페이스
using UnityEngine.ResourceManagement.AsyncOperations; // Addressables 네임스페이스

#if UNITY_EDITOR
using UnityEditor; // 에디터 전용 네임스페이스
#endif

// ✅ OriginBlock 데이터 구조 및 암/복호화 로직 (런타임에서도 사용 가능)
[Serializable] // 컴포넌트에서 직접 사용할 경우 유용할 수 있음
public class OriginBlock
{
    [JsonProperty] private List<string> mac_addresses = new(); // 개발 기기의 MAC 주소 (생성 시점 기준)
    [JsonProperty] private string creation_time_utc = "";     // 파일 생성 시각 (UTC)
    [JsonProperty] private string project_name = "";          // 프로젝트 이름 (생성 시점 기준)
    [JsonProperty] private string unityProjectID = "";        // Unity 프로젝트 ID (에디터에서만 채워짐)

    // --- 핵심 데이터 및 암호화 메서드 (대부분 변경 없음) ---

    public string ToJson() => JsonConvert.SerializeObject(this);
    public static OriginBlock FromJson(string json) => JsonConvert.DeserializeObject<OriginBlock>(json);

    public byte[] Encrypt(string aesKey)
    {
        using Aes aes = Aes.Create();
        // 필요하다면 강력하고 일관된 키 유도 방식 사용 (SHA256 괜찮음)
        aes.Key = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes(aesKey));
        // 고정된 IV(0으로 채워진) 사용은 보안상 일반적으로 권장되지 않습니다.
        // 무작위 IV를 생성하여 암호화된 데이터 앞에 붙여 저장하는 것을 고려하세요.
        aes.IV = new byte[16];

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
            // 복호화 실패 시 null 반환 또는 예외 처리
            Debug.LogError($"[OriginBlock] 복호화 실패. AES 키 또는 데이터 무결성을 확인하세요. 오류: {ex.Message}");
            return null;
        }
    }

    public static List<uint> ToBitstream(byte[] encryptedData)
    {
        if (encryptedData == null) return new List<uint>();

        List<uint> bits = new((encryptedData.Length * 8)); // 용량 미리 할당
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
        if (bits == null || bits.Count % 8 != 0) return Array.Empty<byte>(); // 또는 예외 발생

        int byteCount = bits.Count / 8;
        byte[] data = new byte[byteCount];
        for (int i = 0; i < byteCount; i++)
        {
            byte b = 0;
            int baseIndex = i * 8;
            for (int j = 0; j < 8; j++)
            {
                // 시프트 전에 비트 값이 0 또는 1인지 확인
                uint bit = bits[baseIndex + j] & 1;
                b |= (byte)(bit << (7 - j));
            }
            data[i] = b;
        }
        return data;
    }

    // --- Addressables를 사용한 런타임 로딩 로직 ---

    // !! 보안 경고 !!
    // 코드에 직접 키를 저장하는 것은 안전하지 않습니다. 안전한 키 관리 전략으로 교체해야 합니다.
    private const string RUNTIME_AES_KEY = "OriginBlockData"; // <<< --- 반드시 교체하세요!!!

    // Addressables를 통해 암호화된 데이터 TextAsset을 로드합니다. (내부 헬퍼)
    private static async Task<TextAsset> LoadEncryptedDataAssetAsync(string addressableKey)
    {
        AsyncOperationHandle<TextAsset> handle = Addressables.LoadAssetAsync<TextAsset>(addressableKey);
        await handle.Task; // 로딩 완료까지 기다림

        if (handle.Status == AsyncOperationStatus.Succeeded)
        {
            return handle.Result;
        }
        else
        {
            Debug.LogError($"[OriginBlock] Addressable 에셋 로드 실패: {addressableKey}. 오류: {handle.OperationException}");
            Addressables.Release(handle); // 실패 시 핸들 해제
            return null;
        }
        // 참고: 에셋 사용이 끝나면 나중에 핸들을 해제해야 합니다: Addressables.Release(handle);
        // 가능하면 로드, 처리, 해제를 같은 범위 내에서 수행하는 것이 좋습니다.
    }

    // 런타임에 OriginBlock을 로드하고 복호화합니다.
    public static async Task<OriginBlock> LoadAndDecryptRuntimeAsync(string addressableKey)
    {
        AsyncOperationHandle<TextAsset> handle = Addressables.LoadAssetAsync<TextAsset>(addressableKey);
        await handle.Task;

        OriginBlock block = null;
        if (handle.Status == AsyncOperationStatus.Succeeded)
        {
            try
            {
                string encoded = handle.Result.text; // Base64 인코딩된 문자열 가져오기
                byte[] encryptedData = Convert.FromBase64String(encoded); // Base64 디코딩
                block = Decrypt(encryptedData, RUNTIME_AES_KEY); // 안전하게 관리되는 런타임 키 사용
            }
            catch (FormatException ex) // Base64 디코딩 오류 처리
            {
                Debug.LogError($"[OriginBlock] Base64 디코딩 실패. 데이터가 손상되었거나 잘못된 형식일 수 있습니다. 오류: {ex.Message}");
                block = null;
            }
            catch (Exception ex) // 기타 오류 처리
            {
                Debug.LogError($"[OriginBlock] 로드된 OriginBlock 데이터 처리 중 오류 발생: {ex.Message}");
                block = null; // 오류 발생 시 null 반환 보장
            }
            finally
            {
                // 성공 여부에 관계없이 핸들을 해제해야 메모리 누수를 방지합니다!
                Addressables.Release(handle);
            }
        }
        else
        {
            Debug.LogError($"[OriginBlock] OriginBlock을 위한 Addressable 에셋 로드 실패: {addressableKey}. 오류: {handle.OperationException}");
            // 로드 실패 시에도 핸들 해제 (LoadAssetAsync가 처리할 수도 있지만 명시적으로)
            if (handle.IsValid()) Addressables.Release(handle);
        }
        return block;
    }

    // 런타임에 암호화된 데이터를 비트스트림으로 가져옵니다.
    public static async Task<List<uint>> GetBitstreamRuntimeAsync(string addressableKey)
    {
        List<uint> bits = null;
        AsyncOperationHandle<TextAsset> handle = Addressables.LoadAssetAsync<TextAsset>(addressableKey);
        await handle.Task;

        if (handle.Status == AsyncOperationStatus.Succeeded)
        {
            try
            {
                string encoded = handle.Result.text;
                byte[] encryptedData = Convert.FromBase64String(encoded); // 암호화된 바이트 얻기
                bits = ToBitstream(encryptedData); // 바이트를 비트스트림으로 변환

                // 선택 사항: 비트스트림을 화면 크기에 맞게 패딩 (Screen API 호출 시점 주의)
                // 화면 크기가 필요한 정확한 시점에 패딩을 수행하는 것을 고려하세요.
                if (bits != null && bits.Count > 0)
                {
                    int targetBitLength = Screen.width * Screen.height; // 화면 초기화 전에 호출될 경우 주의
                    if (targetBitLength <= 0)
                    {
                        Debug.LogWarning("[OriginBlock] 화면 크기가 유효하지 않아 비트스트림 패딩을 건너<0xEB><0x9C><0x84>니다.");
                    }
                    else if (bits.Count < targetBitLength)
                    {
                        int originalCount = bits.Count;
                        while (bits.Count < targetBitLength)
                        {
                            // 원본 비트스트림을 반복해서 추가하여 길이를 늘립니다.
                            bits.AddRange(bits.GetRange(0, Math.Min(originalCount, targetBitLength - bits.Count)));
                        }
                        // 정확한 길이로 자릅니다.
                        bits = bits.Take(targetBitLength).ToList();
                    }
                    else if (bits.Count > targetBitLength)
                    {
                        // 길이가 길 경우, 필요한 만큼만 잘라냅니다.
                        bits = bits.Take(targetBitLength).ToList();
                    }
                }
            }
            catch (FormatException ex)
            {
                Debug.LogError($"[OriginBlock] Base64 디코딩 실패 (Bitstream): {ex.Message}");
                bits = null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OriginBlock] OriginBlock 데이터에서 비트스트림 처리 중 오류 발생: {ex.Message}");
                bits = null; // 오류 시 null 반환 보장
            }
            finally
            {
                Addressables.Release(handle); // 핸들 해제!
            }
        }
        else
        {
            Debug.LogError($"[OriginBlock] Bitstream을 위한 Addressable 에셋 로드 실패: {addressableKey}. 오류: {handle.OperationException}");
            if (handle.IsValid()) Addressables.Release(handle);
        }

        // 실패 시 빈 리스트 반환
        return bits ?? new List<uint>();
    }


    // --- 에디터 전용 생성 로직 ---
#if UNITY_EDITOR

    // --- 에디터 생성용 상수 ---
    // 암호화된 데이터 에셋을 저장할 경로 정의
    private const string EDITOR_SAVE_PATH = "Assets/GameData/OriginBlockData.bytes"; // .asset 또는 .bytes 사용 권장
    // Addressable 키 정의 (런타임 로딩 시 사용할 키와 일치해야 함)
    public const string ADDRESSABLE_KEY = "OriginBlockData";


    // 에디터에서 OriginBlock 데이터를 생성합니다.
    private static OriginBlock CreateEditorData()
    {
        // MAC 주소 수집은 개발 머신에 특화될 수 있습니다. 이것이 의도된 동작인가요?
        var macs = GetMacAddresses();

        // Assets 폴더 생성 시간 사용 (기존과 동일)
        string creationUtc;
        try
        {
            creationUtc = File.GetCreationTimeUtc("Assets").ToString("o"); // "o"는 라운드트립 형식
        }
        catch (Exception ex)
        {
            Debug.LogError($"[OriginBlock] Assets 폴더 생성 시간을 가져올 수 없습니다: {ex.Message}");
            creationUtc = DateTime.UtcNow.ToString("o"); // 현재 시간으로 대체
        }


        // 에디터 전용 API를 사용하여 프로젝트 ID 가져오기
        string projectID = UnityEditor.CloudProjectSettings.projectId;
        if (string.IsNullOrEmpty(projectID))
        {
            Debug.LogWarning("[OriginBlock] Unity Cloud Project ID를 찾을 수 없습니다. Services 창에서 프로젝트를 연결했는지 확인하세요.");
        }

        return new OriginBlock
        {
            mac_addresses = macs,
            creation_time_utc = creationUtc,
            project_name = Application.productName,
            unityProjectID = projectID // 에디터에서만 사용 가능
        };
    }

    // MAC 주소 가져오기 (에디터 전용 헬퍼)
    private static List<string> GetMacAddresses()
    {
        // 권한 문제나 플랫폼 제약으로 실패할 수 있으므로 예외 처리 추가
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
               .Where(nic =>
                   nic.OperationalStatus == OperationalStatus.Up && // 활성화된 인터페이스만
                   nic.NetworkInterfaceType != NetworkInterfaceType.Loopback && // 루프백 제외
                   nic.GetPhysicalAddress() != null) // 물리 주소가 있는 경우만
               .Select(nic => nic.GetPhysicalAddress().ToString()) // 주소를 문자열로 변환
                                                                   // 기본적인 MAC 주소 형식 검사 (예: 12자리 16진수)
               .Where(mac => !string.IsNullOrEmpty(mac) && mac.Length >= 12)
               .Distinct() // 중복 제거
               .ToList();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[OriginBlock] MAC 주소를 가져오는 중 오류 발생: {ex.Message}");
            return new List<string>(); // 오류 시 빈 리스트 반환
        }
    }

    // ✅ Addressable 에셋으로 OriginBlock 데이터를 생성하고 저장합니다 (에디터 전용)
    // Unity 에디터 메뉴에서 쉽게 실행할 수 있도록 메뉴 아이템 추가
    [MenuItem("Tools/OriginBlock/OriginBlock 데이터 생성")]
    public static void GenerateAndSaveDataEditor()
    {
        // !! 보안 경고 !! Environment.UserName을 키로 사용하는 것은 안전하지 않습니다.
        // 에디터 생성 프로세스만을 위한 일관되고 안전한 키로 교체하세요.
        // 이 키는 RUNTIME_AES_KEY와 같을 필요는 없습니다.
        string editorAesKey = "OriginBlockData"; // <<< --- 에디터용으로 교체하세요
        Debug.LogWarning("임시 에디터 AES 키를 사용 중입니다. '에디터_전용_비밀_키'를 교체하세요.");


        Debug.Log("[OriginBlock] OriginBlock 데이터 생성 중...");
        var block = CreateEditorData();
        if (block == null)
        {
            Debug.LogError("[OriginBlock] OriginBlock 데이터 생성 실패.");
            return;
        }

        byte[] encrypted = block.Encrypt(editorAesKey);
        string encodedInBase64 = Convert.ToBase64String(encrypted); // Base64로 인코딩

        // 저장 경로의 디렉토리가 존재하는지 확인 및 생성
        string directory = Path.GetDirectoryName(EDITOR_SAVE_PATH);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Base64 문자열을 TextAsset으로 저장 (Unity는 .asset 파일을 텍스트로 인식 가능)
        try
        {
            File.WriteAllText(EDITOR_SAVE_PATH, encodedInBase64);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[OriginBlock] 파일 쓰기 실패: {EDITOR_SAVE_PATH}. 오류: {ex.Message}");
            return;
        }


        // 중요: Unity가 새/업데이트된 에셋을 인식하도록 합니다.
        AssetDatabase.ImportAsset(EDITOR_SAVE_PATH);
        // AssetDatabase.Refresh(); // ImportAsset 이후 필요 없을 수 있음

        Debug.Log($"[OriginBlock] ✅ OriginBlock 데이터 생성 완료 및 저장 위치: {EDITOR_SAVE_PATH}");
        Debug.Log($"[OriginBlock] ➡️ 중요: Addressables Groups 창 (Window > Asset Management > Addressables > Groups)에서 " +
            $"'{EDITOR_SAVE_PATH}' 파일을 찾아서 Addressable로 지정하고, 키(Address)를 '{ADDRESSABLE_KEY}'로 설정하세요.");
    }

    #endif // UNITY_EDITOR
}