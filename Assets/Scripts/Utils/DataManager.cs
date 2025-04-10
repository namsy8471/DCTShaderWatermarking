using UnityEngine;
using System.Threading.Tasks;
using System.Collections.Generic; // List<uint> ��� ���� ����

public static class DataManager
{
    public static byte[] EncryptedOriginData { get; private set; }
    public static bool IsDataReady { get; private set; } = false;
    private static bool isLoading = false;

    // �񵿱�� ������ �ε� (�� ���� �� �� �� ȣ��)
    public static async Task LoadOriginDataAsync(string addressableKey)
    {
        if (IsDataReady || isLoading) return; // �̹� �ε��߰ų� �ε� ���̸� ���� �� ��

        isLoading = true;
        Debug.Log("[DataManager] OriginBlock ������ �񵿱� �ε� ����...");

        // OriginBlock Ŭ������ �񵿱� �ε� �Լ� ���
        EncryptedOriginData = await OriginBlock.LoadEncryptedDataBytesASync(addressableKey);

        IsDataReady = (EncryptedOriginData != null && EncryptedOriginData.Length > 0);
        isLoading = false;
        Debug.Log($"[DataManager] OriginBlock ������ �ε� �Ϸ�. �غ� ����: {IsDataReady}");
    }

    // --- �ʿ� �� �ٸ� ������ �ε�/���� �Լ� �߰� ---
}