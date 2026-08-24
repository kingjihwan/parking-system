using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// X-Y 가동 레일 — 십자(+) 구조
///   X빔 (가로, 전체 너비 스팬) → Z방향 슬라이딩 → 행(A/B/C) 선택
///   Y빔 (세로, 전체 깊이 스팬) → X방향 슬라이딩 → 열(1~6) 선택
///   충전 모듈: 두 빔의 교차점
///
/// 이동 순서:
///   Phase 1 – X빔이 먼저 목표 행(A/B/C)으로 Z 이동
///   Phase 2 – Y빔이 목표 열(1~6)으로 X 이동
///   Phase 3 – 교차점에서 충전 (노란 펄스)
/// </summary>
public class ParkingRail : MonoBehaviour
{
    // ── 치수 (m) ────────────────────────────────────────────────
    const float RAIL_W = 0.004f;   // 프레임 가이드 폭
    const float RAIL_H = 0.002f;   // 프레임 가이드 높이
    const float BEAM_H = 0.004f;   // 빔 높이
    const float BEAM_D = 0.006f;   // 빔 두께
    const float PAD_T  = 0.002f;   // 충전 모듈 두께
    const float PAD_R  = 0.020f;   // 충전 모듈 반지름
    const float RING_R = 0.013f;   // 내부 링 반지름
    const float SPAD_R = 0.018f;   // 고정 패드 반지름
    const float SPAD_T = 0.0005f;  // 고정 패드 두께

    // ── 색상 ────────────────────────────────────────────────────
    static readonly Color COL_FRAME    = new Color(0.30f, 0.34f, 0.38f);
    static readonly Color COL_XBEAM   = new Color(0.52f, 0.58f, 0.65f);
    static readonly Color COL_YBEAM   = new Color(0.44f, 0.50f, 0.58f);
    static readonly Color COL_CHARGER  = new Color(0.10f, 0.82f, 0.95f);
    static readonly Color COL_RING     = new Color(0.05f, 0.55f, 0.75f);
    static readonly Color COL_PAD_OFF  = new Color(0.08f, 0.25f, 0.42f);
    static readonly Color COL_PAD_ON   = new Color(0.10f, 0.95f, 0.55f);
    static readonly Color COL_CHARGING = new Color(1.00f, 0.85f, 0.00f);

    // ── 오브젝트 참조 ────────────────────────────────────────────
    MiniatureParkingLot lot;
    GameObject          root;
    Transform           xBeamTf;   // 가로빔 (Z이동)
    Transform           yBeamTf;   // 세로빔 (X이동)
    Transform           chargerTf; // 충전 모듈 (교차점)
    Material            chargerMat;

    readonly Dictionary<string, Material> padMats = new Dictionary<string, Material>();

    // ── 상태 머신 ────────────────────────────────────────────────
    enum Phase { Idle, MoveXBeam, MoveYBeam, Settling, Charging }
    Phase phase = Phase.Idle;

    const float SETTLE_TIME = 0.35f;  // 도착 후 충전 시작까지 대기 (초)
    float settleTimer;

    float tX, tZ;   // 목표
    float cX, cZ;   // 현재 (보간 중)
    bool  ready = false;

    const float SNAP = 0.001f;

    [Header("이동 속도 (m/s)")]
    public float beamSpeed = 0.18f;

    string currentId;
    float  chargeTimer;
    float  chargeDuration;

    // ── 공개 ────────────────────────────────────────────────────
    public System.Action<string> onChargingDone;
    public bool    IsIdle        => phase == Phase.Idle;
    public bool    IsCharging    => phase == Phase.Charging;
    public string  CurrentTarget => currentId;
    public Vector2 CurrentRailPos => new Vector2(cX, cZ);

    /// <summary>
    /// 현재 레일 위치(cX,cZ)에서 가장 가까운 구역 id. 실물 하드웨어로 이동했을 때(POS 피드백,
    /// 캘리브레이션 오차 포함)도 어느 구역에 있는지 알아낼 수 있도록 위치 기반으로 찾는다.
    /// toleranceM 안에 있는 구역이 없으면 null.
    /// </summary>
    public string NearestSpaceId(float toleranceM = 0.05f)
    {
        if (lot == null) return null;
        string best     = null;
        float  bestDist = float.MaxValue;
        foreach (var kv in lot.SpaceMap)
        {
            float dx = kv.Value.localPos.x - cX;
            float dz = kv.Value.localPos.z - cZ;
            float d  = Mathf.Sqrt(dx * dx + dz * dz);
            if (d < bestDist) { bestDist = d; best = kv.Key; }
        }
        return bestDist <= toleranceM ? best : null;
    }

    // ── 아두이노 실물 위치 오버라이드 ────────────────────────────
    [HideInInspector] public bool arduinoMode = false;

    public void SetRealPosition(float x, float z)
    {
        if (!arduinoMode) return;
        cX = x;
        cZ = z;
        ApplyVisuals();
    }

    // ─────────────────────────────────────────────────────────────
    void Start() => Build();

    [ContextMenu("레일 생성")]
    public void Build()
    {
        Clear();
#if UNITY_2023_1_OR_NEWER
        lot = FindFirstObjectByType<MiniatureParkingLot>();
#else
        lot = Object.FindObjectOfType<MiniatureParkingLot>();
#endif
        if (lot == null) { Debug.LogWarning("[ParkingRail] MiniatureParkingLot 없음"); return; }

        root = new GameObject("ParkingRail_Root");
        root.transform.SetParent(lot.transform);
        root.transform.localPosition = Vector3.zero;

        float sy = lot.SurfY;
        BuildRailFrame(sy);
        BuildXBeam(sy);
        BuildYBeam(sy);
        BuildCharger(sy);
        BuildStaticPads(sy);

        // 홈: C1
        if (lot.SpaceMap.TryGetValue("C1", out var home))
        {
            tX = cX = home.localPos.x;
            tZ = cZ = home.localPos.z;
        }
        ready = true;
        ApplyVisuals();
    }

    // ── 1. 가이드 프레임 (4면 레일 트랙) ───────────────────────
    void BuildRailFrame(float sy)
    {
        float pw = MiniatureParkingLot.PW;
        float pd = MiniatureParkingLot.PD;
        float y  = sy + RAIL_H * 0.5f;
        var   g  = Child("RailFrame");

        // 앞/뒤 트랙 (X빔이 달리는 선로, Z 고정 X방향)
        MakBox("TrackF", new Vector3(pw * 0.5f, y, 0),    new Vector3(pw, RAIL_H, RAIL_W), COL_FRAME, g);
        MakBox("TrackB", new Vector3(pw * 0.5f, y, pd),   new Vector3(pw, RAIL_H, RAIL_W), COL_FRAME, g);
        // 좌/우 트랙 (Y빔이 달리는 선로, X 고정 Z방향)
        MakBox("TrackL", new Vector3(0,  y, pd * 0.5f),   new Vector3(RAIL_W, RAIL_H, pd), COL_FRAME, g);
        MakBox("TrackR", new Vector3(pw, y, pd * 0.5f),   new Vector3(RAIL_W, RAIL_H, pd), COL_FRAME, g);
    }

    // ── 2. X빔 — 전체 너비 가로방향, Z방향으로 슬라이딩 (행 선택) ─
    void BuildXBeam(float sy)
    {
        float pw = MiniatureParkingLot.PW;
        float y  = sy + RAIL_H + BEAM_H * 0.5f;
        xBeamTf = MakBox("XBeam",
            new Vector3(pw * 0.5f, y, 0f),
            new Vector3(pw, BEAM_H, BEAM_D),
            COL_XBEAM, root).transform;
    }

    // ── 3. Y빔 — 전체 깊이 세로방향, X방향으로 슬라이딩 (열 선택) ─
    void BuildYBeam(float sy)
    {
        float pd = MiniatureParkingLot.PD;
        float y  = sy + RAIL_H + BEAM_H * 0.5f;
        yBeamTf = MakBox("YBeam",
            new Vector3(0f, y, pd * 0.5f),
            new Vector3(BEAM_D, BEAM_H, pd),
            COL_YBEAM, root).transform;
    }

    // ── 4. 충전 모듈 — 두 빔 교차점에 위치 ─────────────────────
    void BuildCharger(float sy)
    {
        float y = sy + RAIL_H + BEAM_H;

        var hub = new GameObject("Charger");
        hub.transform.SetParent(root.transform);
        chargerTf = hub.transform;

        // 외부 원판
        var outer = MakCyl("Pad_Outer",
            new Vector3(0f, PAD_T * 0.5f, 0f),
            new Vector3(PAD_R * 2f, PAD_T * 0.5f, PAD_R * 2f),
            COL_CHARGER, hub);
        chargerMat = outer.GetComponent<Renderer>().material;

        // 내부 링
        MakCyl("Pad_Inner",
            new Vector3(0f, PAD_T * 1.0f, 0f),
            new Vector3(RING_R * 2f, PAD_T * 0.4f, RING_R * 2f),
            COL_RING, hub);
    }

    // ── 5. 고정 무선충전 패드 (18개 격자점) ─────────────────────
    void BuildStaticPads(float sy)
    {
        float y = sy + SPAD_T * 0.5f + 0.0002f;
        var   g = Child("StaticPads");
        foreach (var kv in lot.SpaceMap)
        {
            var sp  = kv.Value;
            var out_ = MakCyl($"SPad_{sp.id}",
                new Vector3(sp.localPos.x, y, sp.localPos.z),
                new Vector3(SPAD_R * 2f, SPAD_T * 0.5f, SPAD_R * 2f),
                COL_PAD_OFF, g);
            MakCyl($"SPadIn_{sp.id}",
                new Vector3(sp.localPos.x, y + SPAD_T * 0.3f, sp.localPos.z),
                new Vector3(SPAD_R * 1.1f, SPAD_T * 0.3f, SPAD_R * 1.1f),
                COL_PAD_OFF, g);
            padMats[sp.id] = out_.GetComponent<Renderer>().material;
        }
    }

    // ── Update ──────────────────────────────────────────────────
    void Update()
    {
        if (!ready) return;
        float dt = Time.deltaTime;

        switch (phase)
        {
            // Phase 1: X빔이 목표 행(Z)으로 이동
            case Phase.MoveXBeam:
                cZ = Mathf.MoveTowards(cZ, tZ, beamSpeed * dt);
                if (Mathf.Abs(cZ - tZ) <= SNAP)
                {
                    cZ = tZ;
                    phase = Phase.MoveYBeam;
                }
                break;

            // Phase 2: Y빔이 목표 열(X)으로 이동
            case Phase.MoveYBeam:
                cX = Mathf.MoveTowards(cX, tX, beamSpeed * dt);
                if (Mathf.Abs(cX - tX) <= SNAP)
                {
                    cX = tX;
                    cZ = tZ;            // 정확히 스냅
                    ApplyVisuals();     // 시각 위치 먼저 확정
                    phase       = Phase.Settling;
                    settleTimer = 0f;
                }
                break;

            // Phase 3: 도착 후 짧은 대기 (시각 확인용)
            case Phase.Settling:
                settleTimer += dt;
                if (settleTimer >= SETTLE_TIME)
                    OnArrived();    // 여기서 Charging 페이즈로 전환
                break;

            // Phase 4: 충전 중 (노란 펄스)
            case Phase.Charging:
                float pulse = (Mathf.Sin(Time.time * 5f) + 1f) * 0.5f;
                if (chargerMat) chargerMat.color = Color.Lerp(COL_CHARGER, COL_CHARGING, pulse);
                chargeTimer += dt;
                if (chargeTimer >= chargeDuration)
                    OnChargingComplete();
                break;
        }

        ApplyVisuals();
    }

    void ApplyVisuals()
    {
        if (lot == null) return;
        float sy    = lot.SurfY;
        float pw    = MiniatureParkingLot.PW;
        float pd    = MiniatureParkingLot.PD;
        float beamY = sy + RAIL_H + BEAM_H * 0.5f;

        // X빔: 전체 너비 고정, Z방향으로만 이동
        if (xBeamTf)
            xBeamTf.localPosition = new Vector3(pw * 0.5f, beamY, cZ);

        // Y빔: 전체 깊이 고정, X방향으로만 이동
        if (yBeamTf)
            yBeamTf.localPosition = new Vector3(cX, beamY, pd * 0.5f);

        // 충전 모듈: 두 빔의 교차점
        if (chargerTf)
            chargerTf.localPosition = new Vector3(cX, sy + RAIL_H + BEAM_H, cZ);
    }

    void OnArrived()
    {
        phase       = Phase.Charging;
        chargeTimer = 0f;
        SetPadActive(currentId, true);
    }

    void OnChargingComplete()
    {
        SetPadActive(currentId, false);
        if (chargerMat) chargerMat.color = COL_CHARGER;
        phase = Phase.Idle;
        onChargingDone?.Invoke(currentId);
    }

    // ── 공개 API ─────────────────────────────────────────────────

    /// <summary>진행 중인 이동·대기·충전을 즉시 취소하고 Idle로 복귀</summary>
    public void CancelCharging()
    {
        if (phase == Phase.Idle) return;
        if (currentId != null) SetPadActive(currentId, false);
        if (chargerMat) chargerMat.color = COL_CHARGER;
        phase       = Phase.Idle;
        settleTimer = 0f;
        currentId   = null;
    }

    /// <summary>2단계 이동 후 충전 시작 (스케줄러 호출)</summary>
    public void StartCharging(string spaceId, float durationSec)
    {
        if (!ready || lot == null || !lot.SpaceMap.ContainsKey(spaceId)) return;
        var sp         = lot.SpaceMap[spaceId];
        currentId      = spaceId;
        tX             = sp.localPos.x;
        tZ             = sp.localPos.z;
        chargeDuration = durationSec;
        chargeTimer    = 0f;
        phase          = Phase.MoveXBeam;   // 항상 X빔(행) 먼저
    }

    /// <summary>
    /// 실제 하드웨어에는 아무 명령도 보내지 않고, Unity 화면의 레일 위치만 그 구역으로
    /// 즉시 맞춘다. 실물 레일을 손으로 직접 옮겨놓고 화면만 맞출 때 쓴다.
    /// currentId(=CurrentTarget)를 그대로 유지해서, ChargingScheduler가 "레일이 지금
    /// 어느 구역에 가 있는지"를 수동 이동 시에도 그대로 알 수 있게 한다.
    /// </summary>
    public void JumpTo(string spaceId)
    {
        if (!ready || lot == null || !lot.SpaceMap.TryGetValue(spaceId, out var sp)) return;

        if (currentId != null && currentId != spaceId) SetPadActive(currentId, false);
        phase     = Phase.Idle;
        currentId = spaceId;

        tX = cX = sp.localPos.x;
        tZ = cZ = sp.localPos.z;
        ApplyVisuals();
    }

    /// <summary>고정 패드 색상 전환</summary>
    public void SetPadActive(string spaceId, bool active)
    {
        if (padMats.TryGetValue(spaceId, out var mat))
            mat.color = active ? COL_PAD_ON : COL_PAD_OFF;
    }

    // ── 유틸 ─────────────────────────────────────────────────────
    GameObject Child(string nm)
    {
        var g = new GameObject(nm);
        g.transform.SetParent(root.transform);
        g.transform.localPosition = Vector3.zero;
        return g;
    }

    static GameObject MakBox(string nm, Vector3 lp, Vector3 sc, Color col, GameObject parent)
        => MiniatureParkingLot.Box(nm, lp, sc, col, parent, false);

    static GameObject MakCyl(string nm, Vector3 lp, Vector3 sc, Color col, GameObject parent)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = nm;
        go.transform.SetParent(parent.transform);
        go.transform.localPosition = lp;
        go.transform.localScale    = sc;
        Object.Destroy(go.GetComponent<Collider>());
        var mat = new Material(
            Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
        mat.color = col;
        var rend = go.GetComponent<Renderer>();
        rend.material          = mat;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows    = false;
        return go;
    }

    [ContextMenu("레일 삭제")]
    public void Clear()
    {
        padMats.Clear();
        if (root) DestroyImmediate(root);
        var ex = transform.Find("ParkingRail_Root");
        if (ex) DestroyImmediate(ex.gameObject);
        ready = false;
        phase = Phase.Idle;
    }
}
