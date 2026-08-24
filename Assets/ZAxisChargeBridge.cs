using UnityEngine;
using System.Threading;
using System.Collections.Concurrent;

/// <summary>
/// Z축 리프트 + 충전 릴레이 아두이노 브릿지 (HC-06 블루투스 COM 포트)
/// arduino/z_rail_with_relay/z_rail_with_relay.ino 와 짝을 이루는 컴포넌트.
/// 명령: 1=상승 80000스텝 / 2=하강 80000스텝 / 3=충전 ON / 4=충전 OFF / s=비상정지
/// 응답: "Z UP/DOWN 80000 START|COMPLETE", "RELAY ON|OFF", "Z STOP"
/// </summary>
public class ZAxisChargeBridge : MonoBehaviour
{
    [Header("시리얼 설정 (HC-06 블루투스 COM 포트)")]
    public string portName = "COM11";
    public int    baudRate = 9600;

    enum LiftState { Idle, MovingUp, MovingDown }

    // z_rail_with_relay.ino 값과 일치 (STEP_DELAY_US=600, MOVE_STEPS=80000)
    const float MOVE_STEPS    = 80000f;
    const float STEP_DELAY_US = 600f;
    const float STEPS_PER_SEC = 1_000_000f / (STEP_DELAY_US * 2f);  // ≈ 833.3 steps/s

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
    System.IO.Ports.SerialPort port;
    Thread                     readThread;
    // 단일 volatile string이면 짧은 시간에 두 줄이 연달아 오면 뒤 줄이 앞 줄을
    // 덮어써서 앞 줄이 통째로 유실된다. 큐로 바꿔 모든 줄을 순서대로 보존한다.
    readonly ConcurrentQueue<string> lineQueue = new ConcurrentQueue<string>();
    volatile bool              running    = false;
#endif

    string    status    = "연결 안 됨";
    LiftState liftState = LiftState.Idle;
    bool      chargeOn  = false;

    // 실행 시작 시점을 0으로 두고, 아두이노 스텝 지연(STEP_DELAY_US)을 기준으로
    // 경과 시간으로부터 현재 스텝 수를 추정한다 (펌웨어가 실시간 스텝을 보내주지 않음).
    float currentStep   = 0f;
    float stepAtMoveStart = 0f;

    public float CurrentStep => currentStep;

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
    public bool IsConnected => port != null && port.IsOpen;
#else
    public bool IsConnected => false;
#endif
    public string Status   => status;
    public bool   IsMoving => liftState != LiftState.Idle;
    public bool   ChargeOn => chargeOn;

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
            Debug.LogError($"[ZAxisChargeBridge] {e.Message}");
        }
#endif
    }

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
    void ReadLoop()
    {
        while (running)
        {
            try { lineQueue.Enqueue(port.ReadLine().Trim()); }
            catch (System.TimeoutException) { }
            catch { break; }
        }
    }
#endif

    void Update()
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        // 매 프레임 이동 중이면 경과 시간 기반으로 현재 스텝 수 추정치를 갱신.
        // "Z UP/DOWN ... COMPLETE" 시리얼 메시지가 안 오거나 늦게 와도(블루투스 유실 등)
        // 80000스텝에 도달한 시점(약 96초)에 스스로 완료 처리한다.
        if (liftState == LiftState.MovingUp)
        {
            currentStep = Mathf.Min(currentStep + STEPS_PER_SEC * Time.deltaTime, stepAtMoveStart + MOVE_STEPS);
            if (currentStep >= stepAtMoveStart + MOVE_STEPS)
            {
                liftState = LiftState.Idle;
                status    = "상승 완료 (스텝 기준)";
                Debug.Log("[ZAxisChargeBridge] 80000스텝 도달 — 상승 완료로 자동 처리");
            }
        }
        else if (liftState == LiftState.MovingDown)
        {
            currentStep = Mathf.Max(currentStep - STEPS_PER_SEC * Time.deltaTime, stepAtMoveStart - MOVE_STEPS);
            if (currentStep <= stepAtMoveStart - MOVE_STEPS)
            {
                liftState = LiftState.Idle;
                status    = "하강 완료 (스텝 기준)";
                Debug.Log("[ZAxisChargeBridge] 80000스텝 도달 — 하강 완료로 자동 처리");
            }
        }

        while (lineQueue.TryDequeue(out string line))
            ProcessLine(line);
#endif
    }

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
    void ProcessLine(string line)
    {
        Debug.Log($"[ZAxisChargeBridge] ← 수신: \"{line}\"");

        if (line.Contains("Z UP") && line.Contains("START"))
        {
            liftState       = LiftState.MovingUp;
            status          = "상승 중...";
            stepAtMoveStart = currentStep;
        }
        else if (line.Contains("Z DOWN") && line.Contains("START"))
        {
            liftState       = LiftState.MovingDown;
            status          = "하강 중...";
            stepAtMoveStart = currentStep;
        }
        else if (line.Contains("Z UP") && line.Contains("COMPLETE"))
        {
            liftState   = LiftState.Idle;
            status      = "상승 완료";
            currentStep = stepAtMoveStart + MOVE_STEPS;   // 추정 오차 보정
        }
        else if (line.Contains("Z DOWN") && line.Contains("COMPLETE"))
        {
            liftState   = LiftState.Idle;
            status      = "하강 완료";
            currentStep = stepAtMoveStart - MOVE_STEPS;   // 추정 오차 보정
        }
        else if (line.Contains("Z STOP"))                              { liftState = LiftState.Idle;       status = "정지됨"; }
        else if (line.Contains("RELAY ON"))                            { chargeOn = true;  }
        else if (line.Contains("RELAY OFF"))                           { chargeOn = false; }
    }
#endif

    // ── 명령 전송 ────────────────────────────────────────────────
    // Up/Down은 이미 이동 중일 때 또 보내면 아두이노가 남은 스텝을 무시하고
    // 그 순간 위치에서 80000스텝을 새로 세기 시작해버려 위치가 어긋난다 — 막는다.
    public void SendUp()
    {
        if (IsMoving) { Debug.LogWarning("[ZAxisChargeBridge] SendUp() 무시됨 — 이미 이동 중"); return; }
        Send('1');
    }

    public void SendDown()
    {
        if (IsMoving) { Debug.LogWarning("[ZAxisChargeBridge] SendDown() 무시됨 — 이미 이동 중"); return; }
        Send('2');
    }

    // 충전 ON은 Z가 다 올라가 있을 때만 허용 — 이동 중에 눌리면(수동 클릭 포함)
    // Z가 목표 위치에 도달하기 전에 충전이 시작돼버린다.
    public void SendChargeOn()
    {
        if (IsMoving) { Debug.LogWarning("[ZAxisChargeBridge] SendChargeOn() 무시됨 — Z축 이동 중"); return; }
        Send('3');
    }

    // 충전 OFF는 안전 차원에서 이동 중이어도 항상 허용한다.
    public void SendChargeOff() => Send('4');
    public void SendStop()      => Send('s');

    void Send(char c)
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        if (!IsConnected)
        {
            Debug.LogWarning($"[ZAxisChargeBridge] → 전송 실패: '{c}' — 포트({portName})가 연결되지 않음 (port={(port == null ? "null" : "존재")}, IsOpen={port?.IsOpen})");
            return;
        }
        try
        {
            port.Write(c.ToString());
            Debug.Log($"[ZAxisChargeBridge] → 전송: '{c}'");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[ZAxisChargeBridge] → 전송 예외: '{c}' — {e.Message}");
        }
#endif
    }

    void OnDestroy()
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        running = false;
        readThread?.Join(300);
        if (port?.IsOpen == true) port.Close();
#endif
        if (bulbTex) Destroy(bulbTex);
    }

    // ── UI ───────────────────────────────────────────────────────
    GUIStyle  titleStyle, valueStyle, btnStyle, btnChargeOnStyle, btnStopStyle;
    Texture2D bulbTex;

    void InitGUIStyles()
    {
        titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
        titleStyle.normal.textColor = new Color(0.9f, 0.9f, 1f);

        valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        valueStyle.normal.textColor = Color.white;

        btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 12 };

        btnChargeOnStyle = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold };
        btnChargeOnStyle.normal.textColor = new Color(0.4f, 1f, 0.6f);

        btnStopStyle = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold };
        btnStopStyle.normal.textColor = new Color(1f, 0.55f, 0.55f);

        bulbTex = MakeCircleTexture(20);
    }

    static Texture2D MakeCircleTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px  = new Color[size * size];
        float r = size * 0.5f;
        var   c = new Vector2(r, r);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c);
                px[y * size + x] = d <= r ? Color.white : new Color(1f, 1f, 1f, 0f);
            }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    /// <summary>충전 ON이면 노란 불, OFF면 꺼진 회색 불이 켜지는 전구</summary>
    void DrawChargeBulb()
    {
        Rect r = GUILayoutUtility.GetRect(16, 16, GUILayout.Width(16), GUILayout.Height(16));
        Color prev = GUI.color;
        GUI.color = chargeOn ? new Color(1f, 0.85f, 0.15f) : new Color(0.30f, 0.30f, 0.33f);
        GUI.DrawTexture(r, bulbTex);
        GUI.color = prev;
    }

    /// <summary>지정된 영역 안에만 Z축 패널 UI를 그린다 (ControlPanelUI가 호출)</summary>
    public void DrawUI(Rect area)
    {
        if (titleStyle == null) InitGUIStyles();

        GUILayout.BeginArea(area);
        GUILayout.BeginVertical();

        GUILayout.Label("⬆⬇ Z축 패널", titleStyle, GUILayout.Height(20));

        GUILayout.BeginHorizontal(GUILayout.Height(18));
        GUILayout.Label($"{(IsConnected ? "● 연결됨" : "○ 연결 안 됨")} ({portName})", valueStyle);
        GUILayout.FlexibleSpace();
        DrawChargeBulb();
        GUILayout.EndHorizontal();

        GUILayout.Label(status, valueStyle, GUILayout.Height(16));
        GUILayout.Label($"현재 스텝: {currentStep:F0}  (시작 기준 0)", valueStyle, GUILayout.Height(16));
        GUILayout.Space(6);

        GUILayout.BeginHorizontal();
        GUI.enabled = !IsMoving;
        if (GUILayout.Button("▲ 올라가기", btnStyle, GUILayout.Height(30))) SendUp();
        if (GUILayout.Button("▼ 내려가기", btnStyle, GUILayout.Height(30))) SendDown();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        GUI.enabled = !IsMoving;   // Z가 다 올라가 있을 때만 충전 ON 허용
        if (GUILayout.Button("충전 ON",  btnChargeOnStyle, GUILayout.Height(30))) SendChargeOn();
        GUI.enabled = true;        // 충전 OFF는 안전 차원에서 항상 허용
        if (GUILayout.Button("충전 OFF", btnStyle,         GUILayout.Height(30))) SendChargeOff();
        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        if (GUILayout.Button("■  비상정지", btnStopStyle, GUILayout.Height(26))) SendStop();

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }
}
