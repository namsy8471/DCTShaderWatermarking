using UnityEngine;
using System.Threading.Tasks;
using System.Collections.Generic; // List<uint> 사용 예시 포함

public static class DataManager
{
    public static byte[] EncryptedOriginData { get; private set; }
    public static bool IsDataReady { get; private set; } = false;
    private static bool isLoading = false;

    // 비동기로 데이터 로드 (앱 시작 시 한 번 호출)
    public static async Task LoadOriginDataAsync(string addressableKey)
    {
        if (IsDataReady || isLoading) return; // 이미 로드했거나 로딩 중이면 실행 안 함

        isLoading = true;
        Debug.Log("[DataManager] OriginBlock 데이터 비동기 로딩 시작...");

        // OriginBlock 클래스의 비동기 로딩 함수 사용
        EncryptedOriginData = await OriginBlock.LoadEncryptedDataBytesASync(addressableKey);

        IsDataReady = (EncryptedOriginData != null && EncryptedOriginData.Length > 0);
        isLoading = false;
        Debug.Log($"[DataManager] OriginBlock 데이터 로딩 완료. 준비 상태: {IsDataReady}");
    }

    // --- 필요 시 다른 데이터 로딩/관리 함수 추가 ---
}