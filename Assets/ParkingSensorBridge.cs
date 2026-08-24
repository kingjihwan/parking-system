using UnityEngine;
using System.Threading;

/// <summary>
/// 초음파센서 6채널 주차 감지 아두이노 브릿지 (USB 시리얼)
/// 명령: + = 측정 시작 / - = 측정 정지
/// 응답: "N : O" (10cm 이내 = 감지) / "N : X" (없음)  (N = 1~6)
/// 감지 시작(X→O) → ParkingManager.Park() / 감지 종료(O→X) → ParkingManager.Depart()
/// </summary>
public class ParkingSensorBridge : MonoBehaviour
{
    [Header("시리얼 설정 (USB COM 포트)")]
    public string portName = "COM12";
    public int    baudRate = 9600;

    // 센서 번호(1~6) → 실제 주차 구역
    static readonly string[] ZONE_MAP = { "A2", "A4", "B3", "B6", "C1", "C5" };

    ParkingManager parkingManager;

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
    System.IO.Ports.SerialPort port;
    Thread                     readThread;
    volatile string            latestLine = null;
    volatile bool              running    = false;
#endif

    string status   = "연결 안 됨";
    bool   scanning = false;
    readonly bool[] occupied = new bool[6];

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
    public bool IsConnected => port != null && port.IsOpen;
#else
    public bool IsConnected => false;
#endif
    public string Status     => status;
    public bool   IsScanning => scanning;
    public bool   IsOccupied(int index1to6) =>
        index1to6 >= 1 && index1to6 <= 6 && occupied[index1to6 - 1];

    // ─────────────────────────────────────────────────────────────
    void Start()
    {
#if UNITY_2023_1_OR_NEWER
        parkingManager = FindFirstObjectByType<ParkingManager>();
#else
        parkingManager = Object.FindObjectOfType<ParkingManager>();
#endif

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        try
        {
            port = new System.IO.Ports.SerialPort(portName, baudRate) { ReadTimeout = 200 };
            port.Open();
            running = true;
            status  = "연결됨";
            readThread = new Thread(ReadLoop) { IsBackground = true };
            readThread.Start();
        }
        catch (System.Exception e)
        {
            status = "포트 오류";
            Debug.LogError($"[ParkingSensorBridge] {e.Message}");
        }
#endif
    }

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
    void ReadLoop()
    {
        while (running)
        {
            try { latestLine = port.ReadLine().Trim(); }
            catch (System.TimeoutException) { }
            catch { break; }
        }
    }
#endif

    void Update()
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        if (latestLine == null) return;
        string line = latestLine;
        latestLine = null;

        // "N : O" / "N : X" 형식만 처리
        var parts = line.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0].Trim(), out int idx) &&
            idx >= 1 && idx <= 6)
        {
            bool nowOccupied = parts[1].Trim() == "O";
            if (nowOccupied != occupied[idx - 1])
            {
                occupied[idx - 1] = nowOccupied;
                string zoneId = ZONE_MAP[idx - 1];
                if (nowOccupied) parkingManager?.Park(zoneId);
                else              parkingManager?.Depart(zoneId);
            }
        }
#endif
    }

    // ── 명령 전송 ────────────────────────────────────────────────
    public void SendStart() { Send('+'); scanning = true;  status = "측정 중"; }
    public void SendStop()  { Send('-'); scanning = false; status = "정지됨"; }

    void Send(char c)
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        if (!IsConnected) return;
        port.Write(c.ToString());
#endif
    }

    void OnDestroy()
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        running = false;
        readThread?.Join(300);
        if (port?.IsOpen == true) port.Close();
#endif
    }

    // ── 수동 입/출차 (센서 없이 테스트용) ───────────────────────
    string manualZone = "";

    // ── UI ───────────────────────────────────────────────────────
    GUIStyle titleStyle, valueStyle, cellStyle, btnStyle;
    GUIStyle stManualInput, stManualPark, stManualDepart;

    void InitGUIStyles()
    {
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
        titleStyle.normal.textColor = new Color(0.9f, 0.9f, 1f);

        valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        valueStyle.normal.textColor = Color.white;

        cellStyle = new GUIStyle(GUI.skin.box) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        cellStyle.normal.textColor = Color.white;

        btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 12 };

        stManualInput = new GUIStyle(GUI.skin.textField)
        {
            fontSize = 16, fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter, fixedHeight = 30
        };

        stManualPark   = new GUIStyle(GUI.skin.button) { fontSize = 12 };
        stManualPark.normal.textColor = new Color(0.5f, 1f, 0.6f);

        stManualDepart = new GUIStyle(GUI.skin.button) { fontSize = 12 };
        stManualDepart.normal.textColor = new Color(1f, 0.55f, 0.55f);
    }

    /// <summary>지정된 영역 안에만 초음파 감지 UI를 그린다 (ControlPanelUI가 호출)</summary>
    public void DrawUI(Rect area)
    {
        if (titleStyle == null) InitGUIStyles();

        GUILayout.BeginArea(area);
        GUILayout.BeginVertical();

        GUILayout.Label("📡 초음파 주차 감지", titleStyle, GUILayout.Height(20));
        GUILayout.Label($"{(IsConnected ? "● 연결됨" : "○ 연결 안 됨")}   {status}", valueStyle, GUILayout.Height(16));
        GUILayout.Space(6);

        GUILayout.BeginHorizontal();
        Color prevBg = GUI.backgroundColor;
        for (int i = 0; i < 6; i++)
        {
            // 배경은 고정 톤으로 두고, 감지 상태에 따라 글자색만 바꾼다.
            // 연결 안 됨(미감지) = 빨강, 연결됨(감지됨) = 파랑
            GUI.backgroundColor = new Color(0.16f, 0.16f, 0.19f);
            cellStyle.normal.textColor = occupied[i]
                ? new Color(0.35f, 0.6f, 1f)   // 연결됨 → 파랑
                : new Color(1f, 0.3f, 0.3f);   // 연결 안 됨 → 빨강
            GUILayout.Box(ZONE_MAP[i], cellStyle, GUILayout.Height(30));
        }
        GUI.backgroundColor = prevBg;
        GUILayout.EndHorizontal();
        GUILayout.Space(6);

        if (!scanning)
        {
            if (GUILayout.Button("▶  측정 시작", btnStyle, GUILayout.Height(28))) SendStart();
        }
        else
        {
            if (GUILayout.Button("■  측정 정지", btnStyle, GUILayout.Height(28))) SendStop();
        }

        // ── 수동 입/출차 (센서 없이도 테스트) ────────────────────
        GUILayout.Space(8);
        GUILayout.Label("수동 입/출차 (예: A2)", valueStyle, GUILayout.Height(16));
        GUILayout.Space(2);
        string prev = manualZone;
        manualZone = GUILayout.TextField(manualZone, 3, stManualInput).ToUpper().Trim();
        if (manualZone != prev)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in manualZone)
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            manualZone = sb.ToString();
        }

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("입차", stManualPark, GUILayout.Height(28)))
            parkingManager?.Park(manualZone);
        if (GUILayout.Button("출차", stManualDepart, GUILayout.Height(28)))
            parkingManager?.Depart(manualZone);
        GUILayout.EndHorizontal();

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
}
