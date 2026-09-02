using UnityEngine;
using System.Threading;
using System.Collections.Generic;

public class PowerMonitor : MonoBehaviour
{
    [Header("무선충전 모듈 3개 (HC-06 블루투스 COM 포트)")]
    public string portNameA = "COM4";
    public string portNameB = "COM6";
    public string portNameC = "COM8";
    public int    baudRate  = 9600;

    [Header("그래프 설정")]
    public int historySize = 120;  // 표시할 샘플 수 (1초 간격이면 120초)

    [Header("전력 감지 임계값")]
    public float activeThresholdMW = 20f;  // 이 값을 넘는 포트를 "전력 나오는 포트"로 판단

    const int GRAPH_W = 250;
    const int GRAPH_H = 80;

    class ChargePort
    {
        public string portName;
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        public System.IO.Ports.SerialPort port;
        public Thread                     thread;
        public volatile string            latestLine;
        public volatile bool              running;
#endif
        public string status = "연결 안 됨";
        public float  busV, supplyV, currentMA, powerMW;
        public float  maxPowerSeen = 1f;
        public readonly Queue<float> powerHistory = new Queue<float>();
    }

    ChargePort[] ports;
    int activeIndex     = -1;  // 전력이 나오는 포트 (없으면 -1)
    int lastActiveIndex = -1;  // 직전 프레임의 활성 포트 (전환 감지용)

    /// <summary>3개 포트 중 하나라도 연결돼 있으면 true (스케줄링 패널 등 외부에서 조회)</summary>
    public bool AnyConnected
    {
        get
        {
            if (ports == null) return false;
            foreach (var p in ports)
                if (IsConnected(p)) return true;
            return false;
        }
    }

    /// <summary>
    /// 3개 포트 중 순간 최대 전력값(mW). activeIndex의 히스테리시스/임계값과 무관하게
    /// 미세조정 스캔처럼 지점별 상대 비교가 필요한 곳에서 쓴다.
    /// </summary>
    public float MaxPortPowerMW
    {
        get
        {
            if (ports == null) return 0f;
            float max = 0f;
            foreach (var p in ports)
                if (p.powerMW > max) max = p.powerMW;
            return max;
        }
    }

    Texture2D graphTex;
    Color[]   graphPixels;

    // ─────────────────────────────────────────────────────────────
    void Start()
    {
        graphTex    = new Texture2D(GRAPH_W, GRAPH_H, TextureFormat.RGB24, false);
        graphPixels = new Color[GRAPH_W * GRAPH_H];
        ClearGraph();
        graphTex.Apply();

        ports = new ChargePort[]
        {
            new ChargePort { portName = portNameA },
            new ChargePort { portName = portNameB },
            new ChargePort { portName = portNameC },
        };

        foreach (var p in ports) Connect(p);
    }

    void Connect(ChargePort p)
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        try
        {
            p.port = new System.IO.Ports.SerialPort(p.portName, baudRate) { ReadTimeout = 200 };
            p.port.Open();
            p.running = true;
            p.status  = "연결됨 — 측정 시작 중";
            p.thread  = new Thread(() => ReadLoop(p)) { IsBackground = true };
            p.thread.Start();
            p.port.WriteLine("m");  // 연결 즉시 자동으로 측정 시작
        }
        catch (System.Exception e)
        {
            p.status = $"포트 오류: {e.Message}";
            Debug.LogError($"[PowerMonitor] {p.portName}: {e.Message}");
        }
#endif
    }

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
    void ReadLoop(ChargePort p)
    {
        while (p.running)
        {
            try   { p.latestLine = p.port.ReadLine().Trim(); }
            catch (System.TimeoutException) { }
            catch { break; }
        }
    }
#endif

    void Update()
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        if (ports == null) return;

        bool anyUpdated = false;
        foreach (var p in ports)
        {
            if (p.latestLine == null) continue;
            string line = p.latestLine;
            p.latestLine = null;

            if (!line.StartsWith("PWR:")) continue;

            var parts = line.Substring(4).Split(',');
            if (parts.Length < 4) continue;

            float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out p.busV);
            float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out p.supplyV);
            float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out p.currentMA);
            float.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out p.powerMW);

            if (p.powerMW > p.maxPowerSeen) p.maxPowerSeen = p.powerMW;

            p.powerHistory.Enqueue(p.powerMW);
            if (p.powerHistory.Count > historySize) p.powerHistory.Dequeue();

            p.status    = "수신 중";
            anyUpdated  = true;
        }

        if (anyUpdated)
        {
            UpdateActivePort();
            UpdateGraph();
        }
#endif
    }

    // ── 3개 포트 중 실제로 전력이 나오는 포트를 고른다 ────────────────
    void UpdateActivePort()
    {
        int   best      = -1;
        float bestPower = activeThresholdMW;
        for (int i = 0; i < ports.Length; i++)
        {
            if (ports[i].powerMW > bestPower)
            {
                bestPower = ports[i].powerMW;
                best      = i;
            }
        }

        // 충전이 새로 인식됨(꺼져 있다가 켜짐 / 다른 포트로 전환) → 그래프를 이번 연결 구간만 남도록 초기화
        if (best >= 0 && best != lastActiveIndex)
        {
            var p = ports[best];
            p.powerHistory.Clear();
            p.powerHistory.Enqueue(p.powerMW);
            p.maxPowerSeen = Mathf.Max(p.powerMW, 1f);
        }

        activeIndex     = best;
        lastActiveIndex = best;
    }

    // ── 그래프 업데이트 (전력이 나오는 포트만 그린다) ──────────────
    void UpdateGraph()
    {
        ClearGraph();

        if (activeIndex >= 0)
        {
            var active = ports[activeIndex];
            var arr    = active.powerHistory.ToArray();
            int n      = arr.Length;

            if (n >= 2)
            {
                float top = active.maxPowerSeen * 1.1f;
                if (top < 1f) top = 1f;

                for (int i = 0; i < n - 1; i++)
                {
                    int x0 = i       * (GRAPH_W - 1) / (historySize - 1);
                    int x1 = (i + 1) * (GRAPH_W - 1) / (historySize - 1);
                    int y0 = Mathf.Clamp((int)(arr[i]     / top * (GRAPH_H - 2)), 0, GRAPH_H - 1);
                    int y1 = Mathf.Clamp((int)(arr[i + 1] / top * (GRAPH_H - 2)), 0, GRAPH_H - 1);
                    DrawLine(x0, y0, x1, y1, new Color(0.2f, 0.9f, 0.4f));
                }
            }
        }

        graphTex.SetPixels(graphPixels);
        graphTex.Apply();
    }

    void ClearGraph()
    {
        var bg = new Color(0.08f, 0.08f, 0.10f);
        for (int i = 0; i < graphPixels.Length; i++) graphPixels[i] = bg;

        // 기준선 (y=0)
        for (int x = 0; x < GRAPH_W; x++) graphPixels[x] = new Color(0.25f, 0.25f, 0.25f);
    }

    void DrawLine(int x0, int y0, int x1, int y1, Color col)
    {
        int dx = Mathf.Abs(x1 - x0), dy = Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        while (true)
        {
            if (x0 >= 0 && x0 < GRAPH_W && y0 >= 0 && y0 < GRAPH_H)
                graphPixels[y0 * GRAPH_W + x0] = col;
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 <  dx) { err += dx; y0 += sy; }
        }
    }

    // ── UI ───────────────────────────────────────────────────────
    GUIStyle titleStyle, valueStyle, smallStyle, btnStyle, activeStyle, inactiveStyle;

    void InitGUIStyles()
    {
        titleStyle = new GUIStyle(GUI.skin.label)
            { fontSize = 13, fontStyle = FontStyle.Bold };
        titleStyle.normal.textColor = new Color(0.9f, 0.9f, 1f);

        valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        valueStyle.normal.textColor = Color.white;

        smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 10 };
        smallStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);

        activeStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
        activeStyle.normal.textColor = new Color(0.3f, 1f, 0.5f);

        inactiveStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
        inactiveStyle.normal.textColor = new Color(0.55f, 0.55f, 0.6f);

        btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 11 };
    }

    /// <summary>지정된 영역 안에만 전력 모니터 UI를 그린다 (ControlPanelUI가 호출)</summary>
    public void DrawUI(Rect area)
    {
        if (titleStyle == null) InitGUIStyles();
        if (ports == null) return;

        GUILayout.BeginArea(area);
        GUILayout.BeginVertical();

        GUILayout.Label("⚡ 무선충전 전력 모니터 (3포트)", titleStyle, GUILayout.Height(20));
        GUILayout.Space(2);

        GUILayout.Label($"연결된 포트: {ConnectedPortsSummary()}", smallStyle, GUILayout.Height(14));
        GUILayout.Space(2);

        for (int i = 0; i < ports.Length; i++)
        {
            var p      = ports[i];
            bool isOn  = i == activeIndex;
            string dot = IsConnected(p) ? "✔" : "✘";
            string txt = $"{(isOn ? "● " : "○ ")}{p.portName} [{dot}]   {p.powerMW:F1} mW   ({p.status})";
            GUILayout.Label(txt, isOn ? activeStyle : inactiveStyle, GUILayout.Height(16));
        }
        GUILayout.Space(4);

        if (activeIndex >= 0)
        {
            var a = ports[activeIndex];
            GUILayout.Label($"▶ 전력 출력 포트: {a.portName}", titleStyle, GUILayout.Height(18));
            GUILayout.Label($"Bus Voltage    {a.busV:F2} V",       valueStyle, GUILayout.Height(16));
            GUILayout.Label($"Supply Voltage {a.supplyV:F2} V",    valueStyle, GUILayout.Height(16));
            GUILayout.Label($"Current        {a.currentMA:F1} mA", valueStyle, GUILayout.Height(16));
            GUILayout.Label($"Power          {a.powerMW:F1} mW",   valueStyle, GUILayout.Height(16));
            GUILayout.Space(4);
            GUILayout.Label($"max: {a.maxPowerSeen:F1} mW  [{a.powerHistory.Count}/{historySize}s]",
                smallStyle, GUILayout.Height(14));
        }
        else
        {
            GUILayout.Label("▶ 전력 출력 포트: 감지 안 됨", titleStyle, GUILayout.Height(18));
            GUILayout.Space(4);
            GUILayout.Label(" ", smallStyle, GUILayout.Height(14));
        }

        Rect graphRect = GUILayoutUtility.GetRect(GRAPH_W, GRAPH_H);
        GUI.DrawTexture(graphRect, graphTex);
        GUILayout.Space(6);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("측정 시작", btnStyle, GUILayout.Height(26))) SendAll("m");
        if (GUILayout.Button("측정 중지", btnStyle, GUILayout.Height(26))) SendAll("s");
        GUILayout.EndHorizontal();
        if (GUILayout.Button("그래프 초기화", btnStyle, GUILayout.Height(24))) ResetGraph();

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    bool IsConnected(ChargePort p)
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        return p.port != null && p.port.IsOpen;
#else
        return false;
#endif
    }

    string ConnectedPortsSummary()
    {
        if (ports == null) return "-";
        var names = new List<string>();
        foreach (var p in ports)
            if (IsConnected(p)) names.Add(p.portName);
        return names.Count > 0 ? string.Join(", ", names) : "없음";
    }

    void SendAll(string cmd)
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        if (ports == null) return;
        foreach (var p in ports)
            if (p.port != null && p.port.IsOpen) p.port.WriteLine(cmd);
#endif
    }

    void ResetGraph()
    {
        if (ports != null)
        {
            foreach (var p in ports)
            {
                p.powerHistory.Clear();
                p.maxPowerSeen = 1f;
            }
        }
        ClearGraph();
        graphTex.SetPixels(graphPixels);
        graphTex.Apply();
    }

    void OnDestroy()
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        if (ports != null)
        {
            foreach (var p in ports)
            {
                p.running = false;
                p.thread?.Join(300);
                if (p.port?.IsOpen == true) p.port.Close();
            }
        }
#endif
        if (graphTex) Destroy(graphTex);
    }
}
