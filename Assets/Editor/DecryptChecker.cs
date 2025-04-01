#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System;
using System.IO;

public static class OriginBlockEditorChecker
{
    private static readonly string ProjectRoot =
    Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

    private const string AES_KEY = "LSB"; // 키는 고정 또는 수정 가능
    private static string DecodingFileName = Path.Combine(ProjectRoot, "LSB.bytes"); // Resources/LSB.bytes
    private const string SAVE_PATH = "Assets/Scripts/LSB/OriginBlockRecovery.json";

    [MenuItem("Tools/OriginBlock/복호화 및 JSON 저장")]
    public static void DecryptAndSaveOriginBlock()
    {
        Debug.Log("🔍 OriginBlock 복호화 시작...");

        if (!File.Exists(DecodingFileName))
        {
            Debug.LogError("❌ " + DecodingFileName  + "에서 LSB.bytes를 찾을 수 없습니다.");
            return;
        }

        string encoded = File.ReadAllText(DecodingFileName);

        try
        {
            byte[] encrypted = Convert.FromBase64String(encoded);
            OriginBlock block = OriginBlock.Decrypt(encrypted, AES_KEY);

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
}
#endif
