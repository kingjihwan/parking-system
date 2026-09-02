using UnityEngine;

/// <summary>
/// 좌측 통합 제어 패널 — 탭 버튼을 눌러 그 내용의 UI만 표시.
/// 각 기능의 데이터/시리얼 로직은 해당 컴포넌트(ParkingManager, PowerMonitor,
/// ParkingSensorBridge, ZAxisChargeBridge)에 그대로 두고, 이 스크립트는 탭 전환과
/// 화면 배치만 담당한다.
/// </summary>
public class ControlPanelUI : MonoBehaviour
{
    enum Tab { Sensor, Rail, ZAxisCharge, Power, Scheduling }
    Tab activeTab = Tab.Sensor;

    ParkingManager      parkingManager;
    PowerMonitor        powerMonitor;
    ParkingSensorBridge parkingSensor;
    ZAxisChargeBridge   zAxisCharge;
    ChargingScheduler   scheduler;

    const float TAB_BAR_W  = 110f;
    const float CONTENT_W  = 260f;
    const float PANEL_H    = 420f;
    const float MARGIN     = 10f;
    const float TOGGLE_H   = 28f;
    const float MIN_PANEL_W = 170f;

    bool minimized = false;

    GUIStyle stPanelBg, stTabBtn, stTabBtnActive, stToggleBtn;
    bool stylesReady = false;

    void Start()
    {
#if UNITY_2023_1_OR_NEWER
        parkingManager = FindFirstObjectByType<ParkingManager>();
        powerMonitor   = FindFirstObjectByType<PowerMonitor>();
        parkingSensor  = FindFirstObjectByType<ParkingSensorBridge>();
        zAxisCharge    = FindFirstObjectByType<ZAxisChargeBridge>();
        scheduler      = FindFirstObjectByType<ChargingScheduler>();
#else
        parkingManager = Object.FindObjectOfType<ParkingManager>();
        powerMonitor   = Object.FindObjectOfType<PowerMonitor>();
        parkingSensor  = Object.FindObjectOfType<ParkingSensorBridge>();
        zAxisCharge    = Object.FindObjectOfType<ZAxisChargeBridge>();
        scheduler      = Object.FindObjectOfType<ChargingScheduler>();
#endif
    }

    void InitStyles()
    {
        if (stylesReady) return;
        stylesReady = true;

        stPanelBg = new GUIStyle();
        stPanelBg.normal.background = Tex(new Color(0.08f, 0.10f, 0.14f, 0.96f));

        stTabBtn = new GUIStyle(GUI.skin.button) { fontSize = 12, alignment = TextAnchor.MiddleLeft };
        stTabBtn.padding = new RectOffset(10, 4, 0, 0);
        stTabBtn.normal.background = Tex(new Color(0.14f, 0.16f, 0.20f));
        stTabBtn.hover.background  = Tex(new Color(0.20f, 0.22f, 0.28f));
        stTabBtn.normal.textColor  = new Color(0.75f, 0.80f, 0.88f);
        stTabBtn.hover.textColor   = Color.white;

        stTabBtnActive = new GUIStyle(stTabBtn) { fontStyle = FontStyle.Bold };
        stTabBtnActive.normal.background = Tex(new Color(0.20f, 0.45f, 0.75f));
        stTabBtnActive.normal.textColor  = Color.white;

        stToggleBtn = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold };
        stToggleBtn.normal.background = Tex(new Color(0.16f, 0.18f, 0.23f));
        stToggleBtn.hover.background  = Tex(new Color(0.22f, 0.25f, 0.32f));
        stToggleBtn.normal.textColor  = new Color(0.75f, 0.80f, 0.88f);
        stToggleBtn.hover.textColor   = Color.white;
    }

    void OnGUI()
    {
        InitStyles();

        float x = MARGIN, y = MARGIN;

        if (minimized)
        {
            GUI.Box(new Rect(x - 4, y - 4, MIN_PANEL_W + 8, TOGGLE_H + 8), GUIContent.none, stPanelBg);
            if (GUI.Button(new Rect(x, y, MIN_PANEL_W, TOGGLE_H), "▲ 제어 패널 펼치기", stToggleBtn))
                minimized = false;
            return;
        }

        float totalW  = TAB_BAR_W + 6f + CONTENT_W;
        float totalH  = PANEL_H + 6f + TOGGLE_H;

        GUI.Box(new Rect(x - 4, y - 4, totalW + 8, totalH + 8), GUIContent.none, stPanelBg);

        // ── 좌측 탭 바 ──
        GUILayout.BeginArea(new Rect(x, y, TAB_BAR_W, PANEL_H));
        GUILayout.BeginVertical();
        TabButton("차량 감지",  Tab.Sensor);
        TabButton("XY 제어",   Tab.Rail);
        TabButton("Z 제어",    Tab.ZAxisCharge);
        TabButton("무선충전",   Tab.Power);
        TabButton("스케줄링",   Tab.Scheduling);
        GUILayout.EndVertical();
        GUILayout.EndArea();

        // ── 우측 선택된 탭의 내용 ──
        Rect contentRect = new Rect(x + TAB_BAR_W + 6f, y, CONTENT_W, PANEL_H);
        switch (activeTab)
        {
            case Tab.Sensor:      parkingSensor?.DrawUI(contentRect);             break;
            case Tab.Rail:        parkingManager?.DrawRailControlUI(contentRect); break;
            case Tab.ZAxisCharge: zAxisCharge?.DrawUI(contentRect);               break;
            case Tab.Power:       powerMonitor?.DrawUI(contentRect);              break;
            case Tab.Scheduling:  scheduler?.DrawSchedulingUI(contentRect);       break;
        }

        // ── 하단 최소화 버튼 ──
        Rect toggleRect = new Rect(x, y + PANEL_H + 6f, totalW, TOGGLE_H);
        if (GUI.Button(toggleRect, "▼ 최소화", stToggleBtn))
            minimized = true;
    }

    void TabButton(string label, Tab tab)
    {
        bool active = activeTab == tab;
        if (GUILayout.Button(label, active ? stTabBtnActive : stTabBtn, GUILayout.Height(34)))
            activeTab = tab;
        GUILayout.Space(2);
    }

    static Texture2D Tex(Color col)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, col);
        t.Apply();
        return t;
    }
}
