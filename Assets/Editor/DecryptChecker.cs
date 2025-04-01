#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System;
using System.IO;

public static class OriginBlockEditorChecker
{
    [MenuItem("Tools/OriginBlock/복호화 및 JSON 저장")]
    public static void DecryptAndSaveOriginBlock()
    {
        OriginBlock.LoadAndDecrypt();
    }
}
#endif
