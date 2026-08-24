using UnityEngine;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;

/// <summary>
/// 아두이노 ↔ Unity 양방향 시리얼 브릿지 + 6점 캘리브레이션
///
/// 축 관계:
///   Arduino X (col모터, A/B/C 방향) → Unity Z
///   Arduino Y (row모터, 1~6 방향)   → Unity X
///
/// 캘리브레이션 순서:
///   A6 → A1 → B6 → B1 → C6 → C1 이동 후 각각 "캡처" 버튼 클릭
///   6점 완료 → "변환 계산" 버튼 클릭
/// </summary>
public class ArduinoBridge : MonoBehaviour
{
    [Header("시리얼 설정")]
    public string portName = "COM13";
    public int    baudRate = 9600;

    [Header("전체 스텝 수 (아두이노 코드와 일치)")]
    public int totalStepsX = 28178;
    public int totalStepsZ = 28926;

    [Header("미세조정 조그 이동 (xy_rail.ino X_TRAVEL_cm과 일치)")]
    public double xTravelCm = 76.8;

    /// <summary>cm를 X축(조그) 스텝 수로 환산. xy_rail.ino의 recomputeZoneSteps() 공식과 동일.</summary>
    public long StepsForCmX(double cm) => (long)System.Math.Round(totalStepsX / xTravelCm * cm);

    // ── 캘리브레이션 기준점 (Unity 로컬 좌표 고정값) ────────────
    static readonly Dictionary<string, Vector2> KNOWN_POS = new Dictionary<string, Vector2>
    {
        { "A1", new Vector2(0.0775f, 0.130f) },
        { "A6", new Vector2(0.8125f, 0.130f) },
        { "B1", new Vector2(0.0775f, 0.430f) },
        { "B6", new Vector2(0.8125f, 0.430f) },
        { "C1", new Vector2(0.0775f, 0.730f) },
        { "C6", new Vector2(0.8125f, 0.730f) },
    };

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
    System.IO.Ports.SerialPort port;
    Thread                     readThread;
    // 단일 volatile string이면 짧은 시간에 두 줄이 연달아 오면(예: "조그 후 좌표"
    // 직후 바로 오는 "POS:") 뒤 줄이 앞 줄을 덮어써서 앞 줄이 통째로 유실된다.
    // 큐로 바꿔 모든 줄을 순서대로 보존한다.
    readonly ConcurrentQueue<string> lineQueue = new ConcurrentQueue<string>();
    volatile bool              running    = false;
#endif

    ParkingRail rail;
    string lastStatus = "연결 안 됨";
    bool   isMoving   = false;

    Vector2Int lastStepPos = Vector2Int.zero;

    Dictionary<string, Vector2Int> capturedSteps = new Dictionary<string, Vector2Int>();
    float xScale, xOffset, zScale, zOffset;
    bool  calibrated = false;

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
    public bool IsConnected => port != null && port.IsOpen;
#else
    public bool IsConnected => false;
#endif
    public string     Status       => lastStatus;
    public bool       IsMoving     => isMoving;
    public bool       IsCalibrated => calibrated;
    public Vector2Int LastStepPos  => lastStepPos;
    public int        CalibCount   => capturedSteps.Count;
    public bool       HasCaptured(string id) => capturedSteps.ContainsKey(id);

    // ─────────────────────────────────────────────────────────────
    void Start()
    {
#if UNITY_2023_1_OR_NEWER
        rail = FindFirstObjectByType<ParkingRail>();
#else
        rail = Object.FindObjectOfType<ParkingRail>();
#endif
        LoadCalibration();

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        try
        {
            port = new System.IO.Ports.SerialPort(portName, baudRate)
            {
                ReadTimeout = 200,
                Encoding    = System.Text.Encoding.UTF8   // 기본값(ASCII)이면 "이동 완료" 같은 한글이 ?로 깨져서 절대 매칭 안 됨
            };
            port.Open();
            running = true;
            if (rail != null) rail.arduinoMode = true;
            lastStatus = calibrated ? "연결됨 (캘리브레이션 완료)" : "연결됨 — 캘리브레이션 필요";
            readThread = new Thread(ReadLoop) { IsBackground = true };
            readThread.Start();
        }
        catch (System.Exception e)
        {
            lastStatus = "포트 오류";
            Debug.LogError($"[ArduinoBridge] {e.Message}");
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
        while (lineQueue.TryDequeue(out string line))
            ProcessLine(line);
#endif
    }

#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
    void ProcessLine(string line)
    {
        Debug.Log($"[ArduinoBridge] ← 수신: \"{line}\"");

        if (line.StartsWith("POS:"))
        {
            // 이동 중에도 100ms 간격으로 계속 오는 위치 갱신 — 시각화만 하고
            // isMoving은 여기서 건드리지 않는다 (그래야 도착 전에 다음 단계로
            // 넘어가버리는 오작동이 없다. 도착 판정은 "이동 완료" 문자열로만 한다).
            if (TryParsePos(line.Substring(4), out int sx, out int sz))
            {
                lastStepPos = new Vector2Int(sx, sz);
                var uPos = StepsToUnity(sx, sz);
                rail?.SetRealPosition(uPos.x, uPos.y);
            }
        }
        else if (line.Contains("조그 후 좌표"))
        {
            // jogAxis()는 블로킹 호출이라 이 응답이 왔다는 것 자체가 조그 이동이
            // 물리적으로 완전히 끝났다는 뜻 (중간에 POS:가 여러 번 오지 않음).
            isMoving   = false;
            lastStatus = $"조그 이동 완료  stepX:{lastStepPos.x}  stepY:{lastStepPos.y}";
        }
        else if (line.Contains("이동 완료"))
        {
            isMoving   = false;
            lastStatus = $"완료  stepX:{lastStepPos.x}  stepY:{lastStepPos.y}";
        }
        else if (line.Contains("이동") && !line.Contains("완료")) { isMoving = true; lastStatus = "이동 중..."; }
        else if (line.Contains("정지"))       { isMoving = false; lastStatus = "정지"; }
        else if (line.Contains("원점 복귀 완료"))
        {
            isMoving = false; lastStepPos = Vector2Int.zero;
            lastStatus = "원점 복귀 완료";
            // stepX=0, stepY=0을 Unity 좌표 (0,0)으로 그냥 넣으면 안 됨 —
            // Y축이 반전돼 있어서 실제 원점은 StepsToUnity(0,0) 변환값(≈A6 근처)이다.
            var uPos = StepsToUnity(0, 0);
            rail?.SetRealPosition(uPos.x, uPos.y);
        }
    }
#endif

    // ── 캘리브레이션 ────────────────────────────────────────────

    public void CapturePoint(string spaceId)
    {
        if (!KNOWN_POS.ContainsKey(spaceId)) return;
        capturedSteps[spaceId] = lastStepPos;
        lastStatus = $"캡처: {spaceId}  (stepX:{lastStepPos.x}, stepY:{lastStepPos.y})";
        Debug.Log($"[Calib] {spaceId} → stepX:{lastStepPos.x} stepY:{lastStepPos.y}");
    }

    public void ComputeCalibration()
    {
        var pts = new List<(Vector2Int steps, Vector2 unity)>();
        foreach (var kv in capturedSteps)
            if (KNOWN_POS.TryGetValue(kv.Key, out var u))
                pts.Add((kv.Value, u));

        if (pts.Count < 2) { Debug.LogWarning("[Calib] 최소 2점 필요"); return; }

        float n = pts.Count;

        // Unity X = xScale * stepY + xOffset
        float sumSY = 0, sumUX = 0, sumSYUX = 0, sumSY2 = 0;
        foreach (var p in pts)
        {
            sumSY   += p.steps.y;
            sumUX   += p.unity.x;
            sumSYUX += (float)p.steps.y * p.unity.x;
            sumSY2  += (float)p.steps.y * p.steps.y;
        }
        float dX = n * sumSY2 - sumSY * sumSY;
        xScale  = Mathf.Approximately(dX, 0f) ? 0f : (n * sumSYUX - sumSY * sumUX) / dX;
        xOffset = (sumUX - xScale * sumSY) / n;

        // Unity Z = zScale * stepX + zOffset
        float sumSX = 0, sumUZ = 0, sumSXUZ = 0, sumSX2 = 0;
        foreach (var p in pts)
        {
            sumSX   += p.steps.x;
            sumUZ   += p.unity.y;
            sumSXUZ += (float)p.steps.x * p.unity.y;
            sumSX2  += (float)p.steps.x * p.steps.x;
        }
        float dZ = n * sumSX2 - sumSX * sumSX;
        zScale  = Mathf.Approximately(dZ, 0f) ? 0f : (n * sumSXUZ - sumSX * sumUZ) / dZ;
        zOffset = (sumUZ - zScale * sumSX) / n;

        calibrated = true;
        SaveCalibration();
        lastStatus = $"캘리브레이션 완료 ({(int)n}점)";
        Debug.Log($"[Calib] xScale={xScale:F6} xOff={xOffset:F4} | zScale={zScale:F6} zOff={zOffset:F4}");
    }

    Vector2 StepsToUnity(int stepX, int stepY)
    {
        if (!calibrated)
        {
            float ux = (1f - (float)stepY / totalStepsZ) * MiniatureParkingLot.PW;
            float uz = (float)stepX / totalStepsX * MiniatureParkingLot.PD;
            return new Vector2(ux, uz);
        }
        return new Vector2(xScale * stepY + xOffset, zScale * stepX + zOffset);
    }

    void SaveCalibration()
    {
        PlayerPrefs.SetFloat("calib_xScale",  xScale);
        PlayerPrefs.SetFloat("calib_xOffset", xOffset);
        PlayerPrefs.SetFloat("calib_zScale",  zScale);
        PlayerPrefs.SetFloat("calib_zOffset", zOffset);
        PlayerPrefs.SetInt("calib_done", 1);
        PlayerPrefs.Save();
    }

    void LoadCalibration()
    {
        if (PlayerPrefs.GetInt("calib_done", 0) != 1) return;
        xScale     = PlayerPrefs.GetFloat("calib_xScale");
        xOffset    = PlayerPrefs.GetFloat("calib_xOffset");
        zScale     = PlayerPrefs.GetFloat("calib_zScale");
        zOffset    = PlayerPrefs.GetFloat("calib_zOffset");
        calibrated = true;
    }

    public void ResetCalibration()
    {
        capturedSteps.Clear();
        calibrated = false;
        PlayerPrefs.DeleteKey("calib_done");
        lastStatus = "캘리브레이션 초기화됨";
    }

    // ── 명령 전송 ────────────────────────────────────────────────
    public void SendZoneCommand(string zone)
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        if (!IsConnected || zone.Length < 2)
        {
            Debug.LogWarning($"[ArduinoBridge] → 전송 실패: '{zone}' — IsConnected={IsConnected}, zone.Length={zone?.Length}");
            return;
        }
        string cmd = zone.ToLower().Trim();
        port.WriteLine(cmd);
        Debug.Log($"[ArduinoBridge] → 전송: \"{cmd}\"");
        isMoving   = true;
        lastStatus = $"{zone.ToUpper()} 이동 중...";
#endif
    }

    // ── 미세조정 조그 이동 (xy_rail.ino 'j' 모드) ─────────────────
    // 'j' 진입 후에는 아두이노가 전용 조그 모드로 들어가서 x+<스텝>/y+<스텝> 같은
    // 상대 이동 명령만 받는다. 'q'로 빠져나오기 전까진 구역 이동("a1" 등)이 안 먹는다.
    public void EnterJogMode()
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        if (!IsConnected) return;
        port.WriteLine("j");
        lastStatus = "조그 모드 진입";
        Debug.Log("[ArduinoBridge] → 전송: \"j\" (조그 모드 진입)");
#endif
    }

    public void ExitJogMode()
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        if (!IsConnected) return;
        port.WriteLine("q");
        lastStatus = "조그 모드 종료";
        Debug.Log("[ArduinoBridge] → 전송: \"q\" (조그 모드 종료)");
#endif
    }

    /// <summary>조그 모드 진입 상태에서만 유효. X축(A→C 방향)으로 steps만큼 상대 이동.</summary>
    public void JogMoveX(long steps, bool positive)
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        if (!IsConnected || steps <= 0) return;
        string cmd = $"x{(positive ? "+" : "-")}{steps}";
        isMoving   = true;
        lastStatus = $"조그 이동 중... ({cmd})";
        port.WriteLine(cmd);
        Debug.Log($"[ArduinoBridge] → 전송: \"{cmd}\" (조그 X 이동)");
#endif
    }

    public void SendHome()
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        if (!IsConnected) return;
        port.WriteLine("h");
        Debug.Log("[ArduinoBridge] → 전송: \"h\"");
        isMoving = true; lastStatus = "원점 복귀 중...";
#endif
    }

    public void SendStop()
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        if (!IsConnected) return;
        port.WriteLine("s");
        isMoving = false; lastStatus = "정지";
#endif
    }

    static bool TryParsePos(string data, out int x, out int z)
    {
        x = z = 0;
        try
        {
            var parts = data.Split(',');
            x = int.Parse(parts[0].Trim());
            z = int.Parse(parts[1].Trim());
            return true;
        }
        catch { return false; }
    }

    void OnDestroy()
    {
#if !UNITY_WEBGL && !UNITY_IOS && !UNITY_ANDROID
        running = false;
        readThread?.Join(300);
        if (port?.IsOpen == true) port.Close();
#endif
        if (rail != null) rail.arduinoMode = false;
    }
}
