using UnityEngine;
using System.Threading.Tasks; // Task 사용

public class GameInitializer : MonoBehaviour
{
    // Inspector에서 설정하거나 코드로 찾기
    public string originBlockAddressableKey = "OriginBlockData";

    async void Start()
    {
        // 게임 시작 시 데이터 로딩 시작 (await로 완료 대기 가능)
        await DataManager.LoadOriginDataAsync(originBlockAddressableKey);

        // 로딩 완료 후 필요한 다른 초기화 작업 수행 가능
        if (DataManager.IsDataReady)
        {
            Debug.Log("데이터 준비 완료. 게임 로직 시작 가능.");
        }
        else
        {
            Debug.LogError("데이터 로딩 실패! 관련 기능 사용 불가.");
        }
    }
}