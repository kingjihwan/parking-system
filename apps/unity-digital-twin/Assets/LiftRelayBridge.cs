using UnityEngine;
using System.Threading;

/// <summary>
/// Z축 리프트 + 릴레이 아두이노 브릿지 (HC-06 블루투스 시리얼)
/// 명령: 1=상승 80000스텝 / 2=하강 80000스텝 / 3=릴레이 ON / 4=릴레이 OFF / s=비상정지
/// 응답: "Z UP/DOWN 80000 START|COMPLETE", "RELAY ON|OFF", "Z STOP"
/// </summary>
public class LiftRelayBridge : MonoBehaviour
{
    [Header("시리얼 설정 (HC-06 블루투스 COM 포트)")]
    public string portName = "COM6";
    public int    baudRate = 9600;

    enum LiftState { Idle, MovingUp, MovingDown }

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
    System.IO.Ports.SerialPort port;
    Thread                     readThread;
    volatile string            latestLine = null;
    volatile bool              running    = false;
#endif

    string    status    = "연결 안 됨";
    LiftState liftState = LiftState.Idle;
    bool      relayOn   = false;

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
    public bool IsConnected => port != null && port.IsOpen;
#else
    public bool IsConnected => false;
#endif
    public string Status   => status;
    public bool   IsMoving => liftState != LiftState.Idle;
    public bool   RelayOn  => relayOn;

    // ─────────────────────────────────────────────────────────────
    void Start()
    {
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
            Debug.LogError($"[LiftRelayBridge] {e.Message}");
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

        if      (line.Contains("Z UP")   && line.Contains("START"))    { liftState = LiftState.MovingUp;   status = "상승 중..."; }
        else if (line.Contains("Z DOWN") && line.Contains("START"))    { liftState = LiftState.MovingDown; status = "하강 중..."; }
        else if (line.Contains("Z UP")   && line.Contains("COMPLETE")) { liftState = LiftState.Idle;       status = "상승 완료"; }
        else if (line.Contains("Z DOWN") && line.Contains("COMPLETE")) { liftState = LiftState.Idle;       status = "하강 완료"; }
        else if (line.Contains("Z STOP"))                              { liftState = LiftState.Idle;       status = "정지됨"; }
        else if (line.Contains("RELAY ON"))                            { relayOn = true;  status = "릴레이 ON"; }
        else if (line.Contains("RELAY OFF"))                           { relayOn = false; status = "릴레이 OFF"; }
#endif
    }

    // ── 명령 전송 ────────────────────────────────────────────────
    public void SendUp()       => Send('1');
    public void SendDown()     => Send('2');
    public void SendRelayOn()  => Send('3');
    public void SendRelayOff() => Send('4');
    public void SendStop()     => Send('s');

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

    // ── UI ───────────────────────────────────────────────────────
    GUIStyle titleStyle, valueStyle, btnStyle, btnStopStyle;

    void InitGUIStyles()
    {
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
        titleStyle.normal.textColor = new Color(0.9f, 0.9f, 1f);

        valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        valueStyle.normal.textColor = Color.white;

        btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 12 };

        btnStopStyle = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold };
        btnStopStyle.normal.textColor = new Color(1f, 0.55f, 0.55f);
    }

    /// <summary>지정된 영역 안에만 리프트+릴레이 UI를 그린다 (ControlPanelUI가 호출)</summary>
    public void DrawUI(Rect area)
    {
        if (titleStyle == null) InitGUIStyles();

        GUILayout.BeginArea(area);
        GUILayout.BeginVertical();

        GUILayout.Label("⬆⬇ 리프트 + 릴레이", titleStyle, GUILayout.Height(20));
        GUILayout.Label($"{(IsConnected ? "● 연결됨" : "○ 연결 안 됨")} ({portName})   {status}", valueStyle, GUILayout.Height(16));
        GUILayout.Label($"릴레이(충전): {(relayOn ? "ON" : "OFF")}  —  {portName}", valueStyle, GUILayout.Height(16));
        GUILayout.Space(6);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("▲ 상승", btnStyle, GUILayout.Height(30))) SendUp();
        if (GUILayout.Button("▼ 하강", btnStyle, GUILayout.Height(30))) SendDown();
        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("릴레이 ON",  btnStyle, GUILayout.Height(30))) SendRelayOn();
        if (GUILayout.Button("릴레이 OFF", btnStyle, GUILayout.Height(30))) SendRelayOff();
        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        if (GUILayout.Button("■  비상정지", btnStopStyle, GUILayout.Height(26))) SendStop();

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
}
