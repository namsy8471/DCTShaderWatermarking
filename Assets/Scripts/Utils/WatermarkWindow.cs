// WatermarkWindow.cs
// 붙이는 곳: 네가 말한 "utils" 오브젝트(빈 MonoBehaviour 교체)
// 실행 중 백틱(`) 키로 창 토글

using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

[DefaultExecutionOrder(-1000)]
public class WatermarkWindow : MonoBehaviour
{
    [Header("Window")]
    public KeyCode toggleKey = KeyCode.BackQuote; // `
    public bool startVisible = true;
    private Rect win = new Rect(20, 20, 420, 520);
    private bool show;

    // 찾은 Feature들 캐시
    private DCT_RGB_SS_RenderFeature dct;
    private DWTRenderFeature_SS dwt;
    private LSBRenderFeature lsb;

    // Foldout 상태
    bool fDct = true, fDwt = false, fLsb = false, fSys = false;

    void Awake()
    {
        show = startVisible;
        RescanFeatures();
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey)) show = !show;
        if (Input.GetKeyDown(KeyCode.Escape)) Application.Quit();
    }

    void OnGUI()
    {
        if (!show) return;
        win = GUILayout.Window(GetInstanceID(), win, DrawWin, "Watermark Runtime Controls");
    }

    void DrawWin(int id)
    {
        TopBar();

        // URP 체크(없어도 동작은 하지만 경고만 표시)
        var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (urp == null)
            LabelRich("<color=orange>Current Render Pipeline is not URP (계속 사용 가능, 참고만)</color>");

        // 섹션
        DrawDCT();
        DrawDWT();
        DrawLSB();
        DrawSystem();

        GUILayout.Space(6);
        GUILayout.Label("Tip: 백틱(`)으로 창 토글. 값은 즉시 반영됩니다.", Mini());

        GUI.DragWindow(new Rect(0, 0, 10000, 20));
    }

    void TopBar()
    {
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Re-scan", GUILayout.Height(22))) RescanFeatures();
        if (GUILayout.Button("Hide ( ` )", GUILayout.Height(22))) show = false;
        GUILayout.EndHorizontal();

        GUILayout.Space(2);

        if (dct == null && dwt == null && lsb == null)
            LabelRich("<color=orange>ScriptableRendererFeature (DCT/DWT/LSB)를 찾지 못했습니다.</color>\n" +
                      "URP Renderer Asset에 추가되어 있어야 합니다.");
        GUILayout.Space(4);
    }

    // =============== DCT =================
    void DrawDCT()
    {
        if (dct == null) return;
        fDct = Foldout("DCT (RGB Spread Spectrum)", fDct);
        if (!fDct) return;
        GUILayout.BeginVertical("box");

        using (new ScopedEnable(true))
        {
            bool embed = ToggleRow("Embed Bitstream", dct.embedBitstream);
            if (embed != dct.embedBitstream) dct.embedBitstream = embed;

            float strength = SliderRow("Embedding Strength", dct.embeddingStrength, 0f, 1f);
            if (!Mathf.Approximately(strength, dct.embeddingStrength)) dct.embeddingStrength = strength;

            int coeff = SliderIntRow("CoefficientsToUse (AC)", dct.coefficientsToUse, 1, 63);
            if (coeff != dct.coefficientsToUse) dct.coefficientsToUse = coeff;

            string key = TextRow("Secret Key", dct.secretKey);
            if (key != dct.secretKey) dct.secretKey = key;

            float dur = SliderRow("Display Duration (0~1s frac)", dct.displayDuration, 0f, 1f);
            if (!Mathf.Approximately(dur, dct.displayDuration)) dct.displayDuration = dur;

            Help("DCT 패스는 AddRenderPasses()에서 매 프레임 최신 값을 반영합니다.");
        }

        GUILayout.EndVertical();
    }

    // =============== DWT =================
    void DrawDWT()
    {
        if (dwt == null) return;
        fDwt = Foldout("DWT (Spread Spectrum)", fDwt);
        if (!fDwt) return;
        GUILayout.BeginVertical("box");

        using (new ScopedEnable(true))
        {
            bool embed = ToggleRow("Embed Bitstream", dwt.embedBitstream);
            if (embed != dwt.embedBitstream) dwt.embedBitstream = embed;

            float strength = SliderRow("Embedding Strength", dwt.embeddingStrength, 0f, 1f);
            if (!Mathf.Approximately(strength, dwt.embeddingStrength)) dwt.embeddingStrength = strength;

            int coeff = SliderIntRow("CoefficientsToUse (HH<=16)", (int)dwt.coefficientsToUse, 1, 16);
            if (coeff != (int)dwt.coefficientsToUse) dwt.coefficientsToUse = (uint)coeff;

            string addrKey = TextRow("Addressable Key", dwt.addressableKey);
            if (addrKey != dwt.addressableKey) dwt.addressableKey = addrKey;

            float dur = SliderRow("Display Duration (0~1s frac)", dwt.displayDuration, 0f, 1f);
            if (!Mathf.Approximately(dur, dwt.displayDuration)) dwt.displayDuration = dur;

            Help("해상도/계수 변경 시 패턴 버퍼는 내부에서 필요 시 갱신됩니다.");
        }
        GUILayout.EndVertical();
    }

    // =============== LSB =================
    void DrawLSB()
    {
        if (lsb == null) return;
        fLsb = Foldout("Spatial LSB", fLsb);
        if (!fLsb) return;
        GUILayout.BeginVertical("box");

        bool embed = ToggleRow("Embed Bitstream", lsb.embedBitstream);
        if (embed != lsb.embedBitstream) lsb.embedBitstream = embed;

        string addrKey = TextRow("Addressable Key", lsb.addressableKey);
        if (addrKey != lsb.addressableKey) lsb.addressableKey = addrKey;

        float dur = SliderRow("Display Duration (0~1s frac)", lsb.displayDuration, 0f, 1f);
        if (!Mathf.Approximately(dur, lsb.displayDuration)) lsb.displayDuration = dur;

        Help("화면 용량(Width*Height)에 맞춰 자동 패딩됩니다.");
        GUILayout.EndVertical();
    }

    // =============== SYSTEM =============
    void DrawSystem()
    {
        fSys = Foldout("System / Data Status", fSys);
        if (!fSys) return;
        GUILayout.BeginVertical("box");

        LabelRow("DataManager.IsDataReady", SafeBoolText(DataManager_IsReady()));
        LabelRow("Main Camera", Camera.main ? Camera.main.name : "(null)");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Log Values", GUILayout.Height(22))) LogSnapshot();
        if (GUILayout.Button("Re-scan", GUILayout.Height(22))) RescanFeatures();
        GUILayout.EndHorizontal();

        Help("느리면 Display Duration↓, Coefficients↓, Strength↓ 부터 낮춰보세요.");
        GUILayout.EndVertical();
    }

    // =============== FEATURE FIND =========
    void RescanFeatures()
    {
        dct = FindFeature<DCT_RGB_SS_RenderFeature>();
        dwt = FindFeature<DWTRenderFeature_SS>();
        lsb = FindFeature<LSBRenderFeature>();

        Debug.Log($"[WatermarkWindow] Bound -> DCT:{NameOf(dct)}, DWT:{NameOf(dwt)}, LSB:{NameOf(lsb)}");
    }

    T FindFeature<T>() where T : ScriptableRendererFeature
    {
        // 보호된 rendererFeatures에 접근하지 않고, 메모리에 로드된 Feature를 전역 검색
        var all = Resources.FindObjectsOfTypeAll<T>();
        // 가장 먼저 발견된 것을 사용 (필요시 UI로 선택 목록 만들 수 있음)
        return all != null ? all.FirstOrDefault() : null;
    }

    // =============== HELPERS =============
    static string NameOf(ScriptableRendererFeature f)
        => f == null ? "null" : (string.IsNullOrEmpty(f.name) ? f.GetType().Name : f.name);

    static bool DataManager_IsReady()
    {
        try { return DataManager.IsDataReady; } catch { return false; }
    }

    void LogSnapshot()
    {
        Debug.Log($"[WatermarkWindow] Snapshot" +
                  $"\n DCT: embed={dct?.embedBitstream}, strength={dct?.embeddingStrength}, coeff={dct?.coefficientsToUse}, key={dct?.secretKey}, dur={dct?.displayDuration}" +
                  $"\n DWT: embed={dwt?.embedBitstream}, strength={dwt?.embeddingStrength}, coeff={dwt?.coefficientsToUse}, key={dwt?.addressableKey}, dur={dwt?.displayDuration}" +
                  $"\n LSB: embed={lsb?.embedBitstream}, key={lsb?.addressableKey}, dur={lsb?.displayDuration}" +
                  $"\n DataReady={DataManager_IsReady()}");
    }

    // ---- UI bits (런타임 안전, 에디터 의존 X) ----
    static bool Foldout(string title, bool open)
    {
        GUILayout.BeginHorizontal();
        string arrow = open ? "▼" : "▶";
        if (GUILayout.Button($"{arrow} {title}", GUILayout.Height(20)))
            open = !open;
        GUILayout.EndHorizontal();
        return open;
    }

    static bool ToggleRow(string label, bool v)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(220));
        v = GUILayout.Toggle(v, v ? "ON" : "OFF", GUILayout.Width(80));
        GUILayout.EndHorizontal();
        return v;
    }

    static float SliderRow(string label, float v, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label($"{label}  [{v:0.###}]", GUILayout.Width(260));
        v = GUILayout.HorizontalSlider(v, min, max, GUILayout.MinWidth(120));
        GUILayout.EndHorizontal();
        return Mathf.Clamp(v, min, max);
    }

    static int SliderIntRow(string label, int v, int min, int max)
    {
        return Mathf.Clamp(Mathf.RoundToInt(SliderRow(label, v, min, max)), min, max);
    }

    static string TextRow(string label, string text)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(140));
        text = GUILayout.TextField(text ?? string.Empty);
        GUILayout.EndHorizontal();
        return text;
    }

    static void LabelRow(string label, object value)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(200));
        GUILayout.Label(value != null ? value.ToString() : "(null)");
        GUILayout.EndHorizontal();
    }

    static void Help(string msg)
    {
        var s = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, wordWrap = true };
        GUILayout.Box(msg, s);
    }

    static void LabelRich(string rich)
    {
        var s = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
        GUILayout.Label(rich, s);
    }

    static GUIStyle Mini()
    {
        return new GUIStyle(GUI.skin.label) { fontSize = 10, normal = { textColor = Color.grey } };
    }

    static string SafeBoolText(bool v) => v ? "True" : "False";

    // GUI.enabled 스코프 헬퍼
    struct ScopedEnable : IDisposable
    {
        bool prev;
        public ScopedEnable(bool enable)
        {
            prev = GUI.enabled;
            GUI.enabled = enable;
        }
        public void Dispose() => GUI.enabled = prev;
    }
}
