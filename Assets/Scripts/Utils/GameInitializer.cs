using UnityEngine;
using System.Threading.Tasks; // Task ���

public class GameInitializer : MonoBehaviour
{
    // Inspector���� �����ϰų� �ڵ�� ã��
    public string originBlockAddressableKey = "OriginBlockData";

    async void Start()
    {
        // ���� ���� �� ������ �ε� ���� (await�� �Ϸ� ��� ����)
        await DataManager.LoadOriginDataAsync(originBlockAddressableKey);

        // �ε� �Ϸ� �� �ʿ��� �ٸ� �ʱ�ȭ �۾� ���� ����
        if (DataManager.IsDataReady)
        {
            Debug.Log("������ �غ� �Ϸ�. ���� ���� ���� ����.");
        }
        else
        {
            Debug.LogError("������ �ε� ����! ���� ��� ��� �Ұ�.");
        }
    }
}