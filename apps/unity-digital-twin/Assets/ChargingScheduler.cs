using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 정규화 가중합 기반 동적 우선순위 큐 충전 스케줄러
///
/// 좌측 하단 : 차량 현황 (배터리 바, SOC, 속도, 출차)
/// 우측 하단 : 충전 큐 패널 (1행: 현재 충전 중, 이하: 대기 순서) + 가동 버튼
///
/// 점수 = W_DIST*(1-norm_d) + W_TIME*(1-norm_r) + W_BATT*norm_b
///   - 거리 가까울수록, 출차 임박할수록, 충전량 많을수록 높은 점수
///   - 가중치 균등 (각 1/3)
///
/// 규칙
///   - 충전 중에는 새 차량이 들어와도 중단하지 않음
///   - 레일이 목표 구역에 도착한 후에만 배터리 게이지 증가
///   - 충전 완료 후 레일 위치 갱신 → 큐 재정렬 → 다음 차량으로 이동
/// </summary>
public class ChargingScheduler : MonoBehaviour
{
    // ── 차량 데이터 ──────────────────────────────────────────────
    public class CarData
    {
        public string spaceId;
        public float  entryHour;
        public float  exitHour;
        public float  speedKW;
        public float  currentKWh;
        public float  maxKWh;
        public bool   doneCharging;
        public string carNumber     = "";  // 앱에서 등록한 차량 번호 (VehicleRecord.car_number)
        public string exitTimeLabel = "";  // 앱에서 등록한 실제 출차 시각 표시용 (예: "14:30")
        public string vehicleId     = "";  // Supabase vehicles.id — PushState()로 실시간 반영할 때 사용

        public float SocPct      => currentKWh / maxKWh * 100f;
        public float NeedKWh     => Mathf.Max(0, maxKWh - currentKWh);
        public float HoursToFull => speedKW > 0 ? NeedKWh / speedKW : 0f;
        public bool  NeedsCharge => NeedKWh > 0.5f && !doneCharging;
    }

    // ── 시뮬레이션 ───────────────────────────────────────────────
    [Header("시뮬레이션")]
    public float simSpeed  = 1200f;   // 1실초 = simSpeed 시뮬초
    public float startHour = 0.0f;

    // ── 정규화 가중합 계수 (균등) ────────────────────────────────
    const float W_DIST = 1f / 3f;
    const float W_TIME = 1f / 3f;
    const float W_BATT = 1f / 3f;

    // ── 구역 하이라이트 색상 (앱 등록 대기 = 빨강, 충전 중 = 초록) ──
    static readonly Color HL_WAITING  = new Color(0.90f, 0.20f, 0.20f);
    static readonly Color HL_CHARGING = new Color(0.20f, 0.85f, 0.35f);

    // ── 참조 ─────────────────────────────────────────────────────
    ParkingRail         rail;
    ParkingManager      parkMgr;
    MiniatureParkingLot lot;
    ArduinoBridge       arduinoBridge;
    ZAxisChargeBridge   zAxisBridge;
    ParkingSensorBridge parkingSensor;
    PowerMonitor        powerMonitor;
    SupabaseBridge      supabaseBridge;

    // ── 상태 ─────────────────────────────────────────────────────
    bool   running = false;
    float  simHour;
    string currentlyCharging;   // 현재 충전(이동 포함) 중인 구역 ID

    public readonly List<CarData> Cars        = new List<CarData>();
    List<CarData>                 chargeQueue = new List<CarData>();

    // ParkingManager UI / OnPark에서 접근하는 프로퍼티
    public bool             IsRunning          => running;
    public string           CurrentlyCharging  => currentlyCharging;
    public List<CarData>    ChargeQueue        => chargeQueue;
    public ParkingRail      Rail               => rail;
    public float            SimHour            => simHour;

    static readonly Dictionary<Color, Texture2D> s_texCache =
        new Dictionary<Color, Texture2D>();

    // ─────────────────────────────────────────────────────────────
    void Start()
    {
#if UNITY_2023_1_OR_NEWER
        rail          = FindFirstObjectByType<ParkingRail>();
        parkMgr       = FindFirstObjectByType<ParkingManager>();
        lot           = FindFirstObjectByType<MiniatureParkingLot>();
        arduinoBridge = FindFirstObjectByType<ArduinoBridge>();
        zAxisBridge   = FindFirstObjectByType<ZAxisChargeBridge>();
        parkingSensor = FindFirstObjectByType<ParkingSensorBridge>();
        powerMonitor  = FindFirstObjectByType<PowerMonitor>();
        supabaseBridge = FindFirstObjectByType<SupabaseBridge>();
#else
        rail          = Object.FindObjectOfType<ParkingRail>();
        parkMgr       = Object.FindObjectOfType<ParkingManager>();
        lot           = Object.FindObjectOfType<MiniatureParkingLot>();
        arduinoBridge = Object.FindObjectOfType<ArduinoBridge>();
        zAxisBridge   = Object.FindObjectOfType<ZAxisChargeBridge>();
        parkingSensor = Object.FindObjectOfType<ParkingSensorBridge>();
        powerMonitor  = Object.FindObjectOfType<PowerMonitor>();
        supabaseBridge = Object.FindObjectOfType<SupabaseBridge>();
#endif
        if (rail != null) rail.onChargingDone = OnChargingDone;
        simHour = startHour;
        // 시작 시 주차장은 항상 빈 상태 — 데모 차량 자동 스폰 없음
    }

    // ── Update ───────────────────────────────────────────────────
    void Update()
    {
        // 구역 하이라이트는 매 프레임 현재 상태에서 다시 계산한다(단일 정답 소스).
        // 여러 지점에서 개별적으로 켜고 끄면 특정 경로를 놓쳤을 때 색이 꺼지지 않고
        // 남는 버그가 생기기 쉬워서, 대신 Cars/충전 상태를 그대로 반영만 한다.
        RefreshHighlights();

        // 자동 하드웨어 시퀀스 없이도(가상 이동 + Z 충전 ON만으로) 실측 전력만큼
        // 배터리량이 올라가고 Supabase에도 반영되게 한다.
        ApplyManualCharging();

        if (!running) return;

        // 레일이 실제 충전 중일 때만 시간 진행 (이동 중·대기 중은 정지)
        if (rail != null && rail.IsCharging)
            simHour += Time.deltaTime * simSpeed / 3600f;

        // 배터리 증가: 레일이 목표 지점에 도착해 충전 중일 때만
        if (currentlyCharging != null && rail != null && rail.IsCharging)
        {
            var car = Cars.Find(c => c.spaceId == currentlyCharging);
            if (car != null && !car.doneCharging)
            {
                car.currentKWh += car.speedKW * Time.deltaTime * simSpeed / 3600f;
                if (car.currentKWh >= car.maxKWh)
                {
                    car.currentKWh   = car.maxKWh;
                    car.doneCharging = true;
                }
            }
        }

        // 출차 시각이 지난 대기 차량 자동 출차 (충전 중인 차는 OnChargingDone에서 처리)
        for (int i = Cars.Count - 1; i >= 0; i--)
        {
            var c = Cars[i];
            if (c.spaceId == currentlyCharging) continue;
            if (simHour >= c.exitHour)
            {
                string sid = c.spaceId;
                Cars.RemoveAt(i);
                chargeQueue.RemoveAll(x => x.spaceId == sid);
                parkMgr?.DepartCar(sid);
            }
        }

        // 레일 대기 중 + 충전 대상 없음 → 다음 차량 시작
        if (rail != null && rail.IsIdle && currentlyCharging == null)
            ChargeNext();
    }

    // ── 가동 시작 ────────────────────────────────────────────────
    public void StartOperation()
    {
        if (running) return;
        running = true;
        simHour = startHour;
        foreach (var c in Cars) c.doneCharging = false;
        currentlyCharging = null;
        RebuildQueue();
        ChargeNext();
    }

    public void StopOperation()
    {
        running           = false;
        currentlyCharging = null;
        chargeQueue.Clear();
    }

    // ── 정규화 가중합으로 큐 재정렬 ──────────────────────────────
    // 호출 시점: 가동 시작 / 새 차량 입차 / 충전 완료 후
    void RebuildQueue()
    {
        chargeQueue = ComputeChargeOrder(currentlyCharging);
    }

    /// <summary>
    /// 충전이 필요한 차량들을 우선순위 점수 순으로 정렬해 반환한다 (읽기 전용, 부작용 없음).
    /// excludeId: 이미 충전 중인 구역(시뮬레이션 큐/실물 하드웨어 시퀀스 공용)은 후보에서 제외.
    /// </summary>
    public List<CarData> ComputeChargeOrder(string excludeId = null)
    {
        if (lot == null) return new List<CarData>();

        Vector2 railPos    = rail != null ? rail.CurrentRailPos : Vector2.zero;
        var     candidates = Cars
            .Where(c => c.NeedsCharge && c.spaceId != excludeId)
            .ToList();

        if (candidates.Count == 0) return candidates;

        int n      = candidates.Count;
        var dists  = new float[n];
        var rems   = new float[n];
        var batts  = new float[n];

        for (int i = 0; i < n; i++)
        {
            var c = candidates[i];
            if (lot.SpaceMap.TryGetValue(c.spaceId, out var sp))
                dists[i] = Mathf.Abs(sp.localPos.x - railPos.x)
                          + Mathf.Abs(sp.localPos.z - railPos.y);
            rems[i]  = Mathf.Max(0.01f, c.exitHour - simHour);
            batts[i] = Mathf.Max(0.01f, c.HoursToFull);
        }

        float maxD = Mathf.Max(0.0001f, dists.Max());
        float maxR = Mathf.Max(0.0001f, rems.Max());
        float maxB = Mathf.Max(0.0001f, batts.Max());

        var scored = new List<(CarData car, float score)>();
        for (int i = 0; i < n; i++)
        {
            float nd    = dists[i] / maxD;
            float nr    = rems[i]  / maxR;
            float nb    = batts[i] / maxB;
            float score = W_DIST*(1-nd) + W_TIME*(1-nr) + W_BATT*nb;
            scored.Add((candidates[i], score));
        }

        scored.Sort((a, b) => b.score.CompareTo(a.score));
        return scored.Select(x => x.car).ToList();
    }

    // ── 큐 앞 차량 꺼내 충전 시작 ───────────────────────────────
    void ChargeNext()
    {
        if (chargeQueue.Count == 0)
        {
            RebuildQueue();
            if (chargeQueue.Count == 0) { running = false; return; }
        }

        var next = chargeQueue[0];
        chargeQueue.RemoveAt(0);
        currentlyCharging = next.spaceId;

        // 레일 이동 + 충전 소요 실제 시간
        float realSec = Mathf.Max(2f, next.HoursToFull * 3600f / simSpeed);
        rail?.StartCharging(next.spaceId, realSec);
    }

    // ── 새 차량 입차 — 씬 스폰 포함 ──────────────────────────
    public void AddCar(CarData car, Color color)
    {
        Cars.Add(car);
        parkMgr?.SpawnCarAt(car.spaceId, color);
        if (running) RebuildQueue();
    }

    // ── 새 차량 입차 — 이미 스폰된 차량 등록만 (OnPark용) ───────
    public void RegisterCar(CarData car)
    {
        if (Cars.Exists(c => c.spaceId == car.spaceId)) return;
        Cars.Add(car);
        if (running) RebuildQueue();
    }

    // ── 출차 (외부 호출) ─────────────────────────────────────────
    public void RemoveCar(string spaceId)
    {
        Cars.RemoveAll(c => c.spaceId == spaceId);
        chargeQueue.RemoveAll(c => c.spaceId == spaceId);
        if (currentlyCharging == spaceId)
        {
            rail?.CancelCharging();
            currentlyCharging = null;
        }
    }

    /// <summary>
    /// 구역 하이라이트를 현재 상태에서 다시 계산해 그대로 반영한다.
    /// 충전 중 = 초록, Cars에 등록돼 대기 중 = 빨강, 그 외(초음파로만 놓인 차·빈 칸·충전 끝난 후) = 흰색(비활성).
    /// </summary>
    void RefreshHighlights()
    {
        if (lot == null) return;
        string manualId = ManualChargingSpaceId();
        foreach (var spaceId in lot.SpaceMap.Keys)
        {
            bool isCharging = (hwCharging && hwCurrentId == spaceId) || manualId == spaceId;
            // doneCharging된 차는 Cars에 잠깐 더 남아있어도(Z 하강 등 정리 중) "대기 중"이 아니다 —
            // 스케줄링 탭 목록(NeedsCharge 기준)에서 이미 빠진 것과 같은 기준으로 맞춘다.
            var  car       = isCharging ? null : Cars.Find(c => c.spaceId == spaceId);
            bool isWaiting = car != null && car.NeedsCharge;
            Color? color = isCharging ? HL_CHARGING : isWaiting ? HL_WAITING : (Color?)null;
            lot.SetSpaceHighlight(spaceId, color);
        }
    }

    // ── 충전 완료 콜백 ────────────────────────────────────────────
    void OnChargingDone(string spaceId)
    {
        var car = Cars.Find(c => c.spaceId == spaceId);
        if (car != null) car.currentKWh = car.maxKWh;

        Cars.RemoveAll(c => c.spaceId == spaceId);
        chargeQueue.RemoveAll(c => c.spaceId == spaceId);
        currentlyCharging = null;
        parkMgr?.DepartCar(spaceId);
        if (car != null && !string.IsNullOrEmpty(car.vehicleId))
            supabaseBridge?.MarkDeparted(car.vehicleId);

        // 레일 위치 갱신 후 큐 재정렬
        if (running) RebuildQueue();
    }

    // ─────────────────────────────────────────────────────────────
    // 실물 하드웨어 충전 시퀀스
    // XY 이동(ArduinoBridge) → Z 상승 → 충전 ON → 미세조정(기준/+2cm/+4cm
    // 전력 스캔 → 최적점 30초 충전 → 충전 OFF → 기준 복귀) → Z 하강
    // → 큐 재계산 → 다음 차량으로 반복 (ZAxisChargeBridge)
    // ─────────────────────────────────────────────────────────────
    const float HW_CHARGE_SEC   = 30f;
    const float HW_MOVE_TIMEOUT = 60f;    // XY 레일 (실측 최대 약 20초)
    const float HW_Z_MOVE_TIMEOUT = 130f; // Z축: 80000스텝 * 1200us/스텝 ≈ 96초 + 여유
    const float HW_Z_START_WAIT = 2f;
    const float HW_RELAY_ACK_TIMEOUT = 3f;  // RELAY ON/OFF 응답 대기
    const float HW_CMD_GAP = 0.4f;  // 연속 명령 사이 여유 — HC-06/SoftwareSerial은 송신 중 수신을 놓칠 수 있어 바로 이어 보내면 유실됨

    // ── 미세조정(조그 스캔) 설정 ────────────────────────────────
    const double HW_FINE_STEP_CM   = 2.0;  // 한 번에 이동하는 거리 (X+ 방향, A→C쪽)
    const float  HW_FINE_SETTLE_SEC = 1.5f; // 각 지점에 멈춘 뒤 측정 전 안정화 대기

    bool      hwRunning;
    string    hwCurrentId;
    string    hwStatus = "대기 중";
    bool      hwCharging;
    float     hwChargeElapsed;
    Coroutine hwCoroutine;

    public bool   HwRunning       => hwRunning;
    public string HwCurrentId     => hwCurrentId;
    public string HwStatus        => hwStatus;
    public bool   HwCharging      => hwCharging;
    public float  HwChargeElapsed => hwChargeElapsed;

    public bool HwHardwareReady =>
        arduinoBridge != null && arduinoBridge.IsConnected &&
        zAxisBridge   != null && zAxisBridge.IsConnected;

    public void StartHardwareSequence()
    {
        if (hwRunning) return;
        if (!HwHardwareReady)
        {
            hwStatus = "XY(ArduinoBridge)/Z(ZAxisChargeBridge) 하드웨어가 연결되지 않았습니다.";
            HwWarn($"시작 거부 — arduinoBridge={(arduinoBridge != null)}({arduinoBridge?.IsConnected}), zAxisBridge={(zAxisBridge != null)}({zAxisBridge?.IsConnected})");
            return;
        }
        hwRunning   = true;
        hwCoroutine = StartCoroutine(HardwareChargeRoutine());
    }

    public void StopHardwareSequence()
    {
        if (!hwRunning) return;
        hwRunning = false;
        if (hwCoroutine != null) StopCoroutine(hwCoroutine);
        hwCoroutine = null;
        hwCharging  = false;
        hwCurrentId = null;
        hwStatus    = "중지됨";
    }

    static void HwLog(string msg)    => Debug.Log($"[HW-SEQ] {Time.time:F1}s {msg}");
    static void HwWarn(string msg)   => Debug.LogWarning($"[HW-SEQ] {Time.time:F1}s {msg}");

    IEnumerator HardwareChargeRoutine()
    {
        HwLog("=== 하드웨어 충전 시퀀스 시작 ===");
        while (hwRunning)
        {
            var order = ComputeChargeOrder(hwCurrentId);
            if (order.Count == 0)
            {
                hwStatus = "충전 대상 없음";
                HwLog("충전 대상 없음 — 시퀀스 종료");
                break;
            }

            var car = order[0];
            hwCurrentId = car.spaceId;
            HwLog($"══════ {car.spaceId} 처리 시작 ══════");

            // ── [1/6] XY 이동 ──────────────────────────────────────
            hwStatus = $"{car.spaceId} 로 이동 중...";
            HwLog($"[1/6] XY 이동 명령 전송: {car.spaceId} (arduinoBridge={(arduinoBridge != null)}, IsConnected={arduinoBridge?.IsConnected})");
            arduinoBridge?.SendZoneCommand(car.spaceId);
            yield return WaitWhile(() => arduinoBridge != null && arduinoBridge.IsMoving, HW_MOVE_TIMEOUT, "[1/6] XY 이동 완료 대기");
            if (!hwRunning) yield break;
            HwLog($"✅ [1/6] XY 이동 완료 확인됨 ({car.spaceId} 도착, IsMoving={arduinoBridge?.IsMoving}) → [2/6] Z 상승 시작");

            // ── [2/6] Z 올리기 — 80000스텝(또는 COMPLETE 메시지)까지 완전히 확인 ──
            hwStatus = $"{car.spaceId} Z축 상승 중...";
            zAxisBridge?.SendUp();
            yield return WaitWhile(() => zAxisBridge != null && !zAxisBridge.IsMoving, HW_Z_START_WAIT, "[2/6] Z 상승 시작 대기");
            if (zAxisBridge != null && !zAxisBridge.IsMoving && hwRunning)
            {
                HwWarn("[2/6] Z 상승 시작 확인 안 됨 — SendUp() 재전송");
                zAxisBridge.SendUp();
                yield return WaitWhile(() => zAxisBridge != null && !zAxisBridge.IsMoving, HW_Z_START_WAIT, "[2/6] Z 상승 시작 재시도 대기");
            }
            yield return WaitWhile(() => zAxisBridge != null && zAxisBridge.IsMoving, HW_Z_MOVE_TIMEOUT, "[2/6] Z 상승 완료 대기");
            if (!hwRunning) yield break;
            HwLog($"✅ [2/6] Z 상승 완료 확인됨 (IsMoving={zAxisBridge?.IsMoving}, 스텝={zAxisBridge?.CurrentStep:F0}) → [3/6] 충전 ON 시작");

            // ── [3/6] 충전 ON 확인 ───────────────────────────────────
            hwStatus = $"{car.spaceId} 충전 준비...";
            zAxisBridge?.SendChargeOn();
            yield return WaitWhile(() => zAxisBridge != null && !zAxisBridge.ChargeOn, HW_RELAY_ACK_TIMEOUT, "[3/6] 충전 ON 확인(RELAY ON) 대기");
            if (zAxisBridge != null && !zAxisBridge.ChargeOn && hwRunning)
            {
                HwWarn("[3/6] 충전 ON 확인 안 됨 — SendChargeOn() 재전송");
                zAxisBridge.SendChargeOn();
                yield return WaitWhile(() => zAxisBridge != null && !zAxisBridge.ChargeOn, HW_RELAY_ACK_TIMEOUT, "[3/6] 충전 ON 재시도 대기");
            }
            if (!hwRunning) yield break;
            HwLog($"✅ [3/6] 충전 ON 확인됨 (ChargeOn={zAxisBridge?.ChargeOn}) → [4/6] 미세조정 시작");

            // ── [4/6] 미세조정 (기준/+2cm/+4cm 전력 스캔 → 최적점 30초 충전 → 기준 복귀) ──
            yield return FineAdjustAndCharge(car);
            if (!hwRunning) yield break;
            HwLog("✅ [4/6] 미세조정 완료 → [5/6] Z 하강 시작");

            // ── [5/6] Z 내리기 ──────────────────────────────────────
            hwStatus = $"{car.spaceId} Z축 하강 중...";
            zAxisBridge?.SendDown();
            yield return WaitWhile(() => zAxisBridge != null && !zAxisBridge.IsMoving, HW_Z_START_WAIT, "[5/6] Z 하강 시작 대기");
            if (zAxisBridge != null && !zAxisBridge.IsMoving && hwRunning)
            {
                HwWarn("[5/6] Z 하강 시작 확인 안 됨 — SendDown() 재전송");
                zAxisBridge.SendDown();
                yield return WaitWhile(() => zAxisBridge != null && !zAxisBridge.IsMoving, HW_Z_START_WAIT, "[5/6] Z 하강 시작 재시도 대기");
            }
            yield return WaitWhile(() => zAxisBridge != null && zAxisBridge.IsMoving, HW_Z_MOVE_TIMEOUT, "[5/6] Z 하강 완료 대기");
            if (!hwRunning) yield break;
            HwLog($"✅ [5/6] Z 하강 완료 확인됨 (IsMoving={zAxisBridge?.IsMoving}, 스텝={zAxisBridge?.CurrentStep:F0}) → [6/6] 다음 차량 XY 이동으로");

            // 충전 세션 종료 → 출차, 다음 차량으로
            HwLog($"══════ {car.spaceId} 완료 → 출차, 다음 차량으로 ══════");
            Cars.RemoveAll(c => c.spaceId == car.spaceId);
            parkMgr?.DepartCar(car.spaceId);
            if (!string.IsNullOrEmpty(car.vehicleId))
                supabaseBridge?.MarkDeparted(car.vehicleId);   // 앱/DB 쪽에서도 자동으로 출차 처리
            hwCurrentId = null;
        }

        hwRunning   = false;
        hwCurrentId = null;
        hwCoroutine = null;
        hwStatus    = "대기 중";
        HwLog("=== 하드웨어 충전 시퀀스 종료 ===");
    }

    // ─────────────────────────────────────────────────────────────
    // 미세조정: 기준(0cm)/+2cm/+4cm 3지점에서 무선충전 전력을 측정하고
    // 가장 강한 지점으로 이동해 30초 충전한 뒤, 충전 OFF 확인하고 기준으로 복귀한다.
    // 진입 시점에 릴레이는 이미 ON 상태([3/6]에서 켜둠) — 여기선 끄기만 한다.
    // ─────────────────────────────────────────────────────────────
    IEnumerator FineAdjustAndCharge(CarData car)
    {
        if (arduinoBridge == null || !arduinoBridge.IsConnected)
        {
            HwWarn("[4/6] arduinoBridge 연결 안 됨 — 미세조정 생략, 기준 위치에서 바로 충전");
            yield return ChargeAtCurrentSpot(car, 0);
            yield break;
        }

        hwStatus = $"{car.spaceId} 미세조정 중...";
        HwLog("[4/6] 조그 모드 진입");
        arduinoBridge.EnterJogMode();
        yield return new WaitForSeconds(HW_CMD_GAP);

        long stepPerHop = arduinoBridge.StepsForCmX(HW_FINE_STEP_CM);
        HwLog($"[4/6] 1회 이동 스텝수: {stepPerHop} ({HW_FINE_STEP_CM:F0}cm 기준, X+ = A→C 방향)");

        var powerAt = new float[3];  // 0=기준, 1=+2cm, 2=+4cm

        // 지점 0 (기준)
        yield return new WaitForSeconds(HW_FINE_SETTLE_SEC);
        powerAt[0] = powerMonitor != null ? powerMonitor.MaxPortPowerMW : 0f;
        HwLog($"[4/6] 지점0(기준) 전력 측정: {powerAt[0]:F1} mW");
        if (!hwRunning) { arduinoBridge.ExitJogMode(); yield break; }

        // 지점 1 (+2cm)
        yield return JogStepAndWait(stepPerHop, true, "[4/6] +2cm 이동");
        yield return new WaitForSeconds(HW_FINE_SETTLE_SEC);
        powerAt[1] = powerMonitor != null ? powerMonitor.MaxPortPowerMW : 0f;
        HwLog($"[4/6] 지점1(+{HW_FINE_STEP_CM:F0}cm) 전력 측정: {powerAt[1]:F1} mW");
        if (!hwRunning) { arduinoBridge.ExitJogMode(); yield break; }

        // 지점 2 (+4cm, 추가로 +2cm 더 이동)
        yield return JogStepAndWait(stepPerHop, true, "[4/6] 추가 +2cm 이동");
        yield return new WaitForSeconds(HW_FINE_SETTLE_SEC);
        powerAt[2] = powerMonitor != null ? powerMonitor.MaxPortPowerMW : 0f;
        HwLog($"[4/6] 지점2(+{HW_FINE_STEP_CM * 2:F0}cm) 전력 측정: {powerAt[2]:F1} mW");
        if (!hwRunning) { arduinoBridge.ExitJogMode(); yield break; }

        // 최적 지점 결정 (현재는 지점2, 즉 +4cm 위치에 있음)
        int bestIdx = 0;
        for (int i = 1; i < 3; i++)
            if (powerAt[i] > powerAt[bestIdx]) bestIdx = i;
        HwLog($"✅ [4/6] 최적 지점: 지점{bestIdx} ({powerAt[bestIdx]:F1} mW, 기준0={powerAt[0]:F1} +2cm={powerAt[1]:F1} +4cm={powerAt[2]:F1})");

        // 지점2(현재) → 최적 지점으로 이동
        int hopsBack = 2 - bestIdx;
        if (hopsBack > 0)
            yield return JogStepAndWait(stepPerHop * hopsBack, false, $"[4/6] 최적 지점으로 복귀 이동 ({hopsBack}칸)");

        // 최적 지점에서 30초 충전 (릴레이는 계속 ON 상태)
        yield return ChargeAtCurrentSpot(car, bestIdx);
        if (!hwRunning) { arduinoBridge.ExitJogMode(); yield break; }

        // 기준(0cm)으로 복귀
        if (bestIdx > 0)
            yield return JogStepAndWait(stepPerHop * bestIdx, false, "[4/6] 기준 위치로 복귀 이동");

        arduinoBridge.ExitJogMode();
        yield return new WaitForSeconds(HW_CMD_GAP);
    }

    /// <summary>
    /// 실측 전력(mW)만큼 배터리량을 채우고 앱/Supabase에도 실시간으로 반영한다.
    /// mW * s = mW·s(mJ) → MobileLink에서 maxKWh를 만들 때와 같은 스케일(÷1000)로 맞춘다.
    /// 자동 하드웨어 시퀀스(ChargeAtCurrentSpot)와 수동 조작(ApplyManualCharging) 양쪽에서 공용으로 쓴다.
    /// </summary>
    void ApplyChargeTick(CarData car, float measuredMW, float dt)
    {
        if (measuredMW > 0f)
            car.currentKWh = Mathf.Min(car.maxKWh, car.currentKWh + measuredMW * dt / 1000f);

        // 내부에서 pushInterval 주기로 묶어서 전송하므로 매 프레임 호출해도 안전하다.
        if (supabaseBridge != null && !string.IsNullOrEmpty(car.vehicleId))
            supabaseBridge.PushState(car.vehicleId, car.SocPct, measuredMW, status: "charging");
    }

    /// <summary>
    /// 수동 조작(가상 이동이든, 실물 레일을 직접 움직여서 실제 좌표로 도착했든)으로
    /// 지금 충전되고 있는 구역 id. 레일의 현재 좌표에서 가장 가까운 구역을 찾기 때문에
    /// "가상 이동"뿐 아니라 실물 XY 이동(POS 피드백)으로 도착한 경우도 잡힌다.
    /// 자동 하드웨어 시퀀스가 이미 돌고 있으면 그쪽이 우선이라 null을 반환한다.
    /// </summary>
    string ManualChargingSpaceId()
    {
        if (hwRunning) return null;
        if (rail == null || zAxisBridge == null || !zAxisBridge.ChargeOn) return null;

        string id = rail.NearestSpaceId();
        if (string.IsNullOrEmpty(id)) return null;

        var car = Cars.Find(c => c.spaceId == id);
        return (car != null && car.NeedsCharge) ? id : null;
    }

    /// <summary>수동으로 충전 중인 구역이 있으면 매 프레임 배터리량/Supabase에 반영한다.</summary>
    void ApplyManualCharging()
    {
        string id = ManualChargingSpaceId();
        if (id == null) return;

        var car = Cars.Find(c => c.spaceId == id);
        if (car == null) return;

        float measuredMW = powerMonitor != null ? powerMonitor.MaxPortPowerMW : 0f;
        ApplyChargeTick(car, measuredMW, Time.deltaTime);

        if (car.currentKWh >= car.maxKWh) car.doneCharging = true;
    }

    /// <summary>현재 지점에서 30초 충전 후 충전 OFF까지 확인.</summary>
    IEnumerator ChargeAtCurrentSpot(CarData car, int spotIdx)
    {
        hwStatus   = $"{car.spaceId} 충전 중 (지점{spotIdx})...";
        hwCharging = true;
        hwChargeElapsed = 0f;
        while (hwRunning && hwChargeElapsed < HW_CHARGE_SEC && car.currentKWh < car.maxKWh)
        {
            float dt = Time.deltaTime;
            hwChargeElapsed += dt;

            float measuredMW = powerMonitor != null ? powerMonitor.MaxPortPowerMW : 0f;
            ApplyChargeTick(car, measuredMW, dt);

            yield return null;
        }
        hwCharging = false;
        bool fullBattery = car.currentKWh >= car.maxKWh;
        if (fullBattery || hwChargeElapsed >= HW_CHARGE_SEC) car.doneCharging = true;  // 배터리 바를 초록으로 전환
        string doneReason = fullBattery ? "배터리 100% 도달" : $"{HW_CHARGE_SEC:F0}초 경과";
        HwLog($"✅ [4/6] 무선충전 완료 ({doneReason}, {hwChargeElapsed:F1}초 소요, 지점{spotIdx}) → 충전 OFF");

        zAxisBridge?.SendChargeOff();
        yield return WaitWhile(() => zAxisBridge != null && zAxisBridge.ChargeOn, HW_RELAY_ACK_TIMEOUT, "[4/6] 충전 OFF 확인(RELAY OFF) 대기");
        if (zAxisBridge != null && zAxisBridge.ChargeOn && hwRunning)
        {
            HwWarn("[4/6] 충전 OFF 확인 안 됨 — SendChargeOff() 재전송");
            zAxisBridge.SendChargeOff();
            yield return WaitWhile(() => zAxisBridge != null && zAxisBridge.ChargeOn, HW_RELAY_ACK_TIMEOUT, "[4/6] 충전 OFF 재시도 대기");
        }
        HwLog($"✅ [4/6] 충전 OFF 확인됨 (ChargeOn={zAxisBridge?.ChargeOn})");
    }

    IEnumerator JogStepAndWait(long steps, bool positive, string label)
    {
        if (arduinoBridge == null || steps <= 0) yield break;
        arduinoBridge.JogMoveX(steps, positive);
        yield return WaitWhile(() => arduinoBridge != null && arduinoBridge.IsMoving, HW_MOVE_TIMEOUT, label);
    }

    IEnumerator WaitWhile(System.Func<bool> condition, float timeoutSec, string label)
    {
        float t = 0f;
        while (hwRunning && condition() && t < timeoutSec)
        {
            t += Time.deltaTime;
            yield return null;
        }

        if (!hwRunning) yield break;

        if (t >= timeoutSec && condition())
            HwWarn($"{label} → 타임아웃({timeoutSec:F0}s) — 조건이 여전히 참인 채로 다음 단계 진행됨 (명령이 전달/응답되지 않았을 가능성)");
        else
            HwLog($"{label} → 완료 ({t:F1}s)");
    }

    // ── UI: 스케줄링 패널 ────────────────────────────────────────
    GUIStyle stSchedTitle, stSchedStatus, stSchedSmall, stSchedRow, stSchedRowActive,
             stSchedOk, stSchedWarn, stSchedBtnStart, stSchedBtnStop;
    bool schedStylesReady = false;

    void InitSchedStyles()
    {
        if (schedStylesReady) return;
        schedStylesReady = true;

        stSchedTitle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
        stSchedTitle.normal.textColor = new Color(0.9f, 0.9f, 1f);

        stSchedStatus = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        stSchedStatus.normal.textColor = Color.white;

        stSchedSmall = new GUIStyle(GUI.skin.label) { fontSize = 10 };
        stSchedSmall.normal.textColor = new Color(0.6f, 0.6f, 0.6f);

        stSchedRow = new GUIStyle(GUI.skin.label) { fontSize = 11 };
        stSchedRow.normal.textColor = new Color(0.75f, 0.80f, 0.88f);

        stSchedRowActive = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold };
        stSchedRowActive.normal.textColor = new Color(0.3f, 1f, 0.5f);

        stSchedOk = new GUIStyle(GUI.skin.label) { fontSize = 11 };
        stSchedOk.normal.textColor = new Color(0.3f, 0.95f, 0.5f);

        stSchedWarn = new GUIStyle(GUI.skin.label) { fontSize = 11 };
        stSchedWarn.normal.textColor = new Color(1f, 0.6f, 0.4f);

        stSchedBtnStart = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold };
        stSchedBtnStop  = new GUIStyle(GUI.skin.button) { fontSize = 12, fontStyle = FontStyle.Bold };
        stSchedBtnStop.normal.textColor = new Color(1f, 0.55f, 0.55f);
    }

    /// <summary>지정된 영역 안에만 스케줄링 UI를 그린다 (ControlPanelUI가 호출)</summary>
    public void DrawSchedulingUI(Rect area)
    {
        InitSchedStyles();

        GUILayout.BeginArea(area);
        GUILayout.BeginVertical();

        GUILayout.Label("📋 충전 스케줄링", stSchedTitle, GUILayout.Height(20));
        GUILayout.Space(2);

        DrawConnRow("차량감지", parkingSensor != null && parkingSensor.IsConnected);
        DrawConnRow("XY 레일",  arduinoBridge != null && arduinoBridge.IsConnected);
        DrawConnRow("Z 레일",   zAxisBridge   != null && zAxisBridge.IsConnected);
        DrawConnRow("무선충전", powerMonitor  != null && powerMonitor.AnyConnected);
        GUILayout.Space(4);

        GUILayout.Label(hwStatus, stSchedStatus, GUILayout.Height(18));
        if (hwCharging)
            GUILayout.Label($"충전 {hwChargeElapsed:F0} / {HW_CHARGE_SEC:F0} 초", stSchedSmall, GUILayout.Height(14));
        GUILayout.Space(6);

        GUILayout.BeginHorizontal();
        GUI.enabled = !hwRunning && HwHardwareReady;
        if (GUILayout.Button("▶ 충전 시작", stSchedBtnStart, GUILayout.Height(30)))
            StartHardwareSequence();
        GUI.enabled = hwRunning;
        if (GUILayout.Button("■ 중지", stSchedBtnStop, GUILayout.Height(30)))
            StopHardwareSequence();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.Space(8);
        GUILayout.Label($"주차된 차량 ({Cars.Count})", stSchedSmall, GUILayout.Height(14));
        GUILayout.BeginHorizontal();
        GUILayout.Label("차량번호", stSchedSmall, GUILayout.Width(70));
        GUILayout.Label("위치",   stSchedSmall, GUILayout.Width(32));
        GUILayout.Label("배터리량", stSchedSmall, GUILayout.Width(40));
        GUILayout.Label("출차시간", stSchedSmall);
        GUILayout.EndHorizontal();
        Sep();

        if (hwCurrentId != null)
        {
            var cur = Cars.Find(c => c.spaceId == hwCurrentId);
            if (cur != null) DrawCarRow(cur, stSchedRowActive);
        }

        foreach (var c in ComputeChargeOrder(hwCurrentId))
            DrawCarRow(c, stSchedRow);

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    void DrawConnRow(string label, bool connected)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(connected ? "●" : "○", connected ? stSchedOk : stSchedWarn, GUILayout.Width(16));
        GUILayout.Label(label, stSchedRow, GUILayout.Width(70));
        GUILayout.Label(connected ? "연결됨" : "연결 안 됨", connected ? stSchedOk : stSchedWarn);
        GUILayout.EndHorizontal();
    }

    static readonly Color BAT_COL_IDLE     = new Color(0.80f, 0.25f, 0.25f);  // 충전 안 됨 — 빨강
    static readonly Color BAT_COL_CHARGING = new Color(0.25f, 0.55f, 0.95f);  // 충전 중 — 파랑
    static readonly Color BAT_COL_DONE     = new Color(0.25f, 0.85f, 0.40f);  // 충전 완료 — 초록

    void DrawCarRow(CarData c, GUIStyle style)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(string.IsNullOrEmpty(c.carNumber) ? "-" : c.carNumber, style, GUILayout.Width(70));
        GUILayout.Label(c.spaceId, style, GUILayout.Width(32));
        GUILayout.Label($"{c.SocPct:F0}%", style, GUILayout.Width(40));
        GUILayout.Label(string.IsNullOrEmpty(c.exitTimeLabel) ? "-" : c.exitTimeLabel, style);
        GUILayout.EndHorizontal();

        DrawBatteryBar(c);
        GUILayout.Space(3);
    }

    /// <summary>차량 한 대의 배터리 잔량 바. 안 채워진 부분=회색 배경, 채워진 부분 색으로 충전 상태 표시.</summary>
    void DrawBatteryBar(CarData c)
    {
        Color fillCol = c.doneCharging     ? BAT_COL_DONE
                       : hwCurrentId == c.spaceId ? BAT_COL_CHARGING
                       : BAT_COL_IDLE;

        Rect r = GUILayoutUtility.GetRect(0, 8, GUILayout.ExpandWidth(true));
        Color prev = GUI.color;

        GUI.color = new Color(1f, 1f, 1f, 0.12f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);

        float pct = Mathf.Clamp01(c.SocPct / 100f);
        if (pct > 0f)
        {
            GUI.color = fillCol;
            GUI.DrawTexture(new Rect(r.x, r.y, r.width * pct, r.height), Texture2D.whiteTexture);
        }

        GUI.color = prev;
    }

    void Sep()
    {
        GUILayout.Space(2);
        Rect r = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
        Color prev = GUI.color;
        GUI.color = new Color(1, 1, 1, 0.15f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = prev;
        GUILayout.Space(2);
    }
}
