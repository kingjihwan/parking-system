using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 주차장 UI + 차량 관리
/// Resources/Cars/rc_car_unity.fbx 를 로드해 모든 차량에 사용, 실패 시 절차적 차량으로 폴백
/// </summary>
public class ParkingManager : MonoBehaviour
{
    // ── 차량 색상 (모든 차량 공통, 단일 파란색) ────────────────
    public static readonly Color CAR_BLUE = new Color(0.15f, 0.40f, 0.85f);

    MiniatureParkingLot  lot;
    ChargingScheduler    scheduler;
    ArduinoBridge        bridge;
    ParkingRail          rail;
    Dictionary<string, GameObject> parkedCars = new Dictionary<string, GameObject>();

    // ── 차량 모델 ────────────────────────────────────────────
    readonly List<GameObject> carTemplates = new List<GameObject>();
    // 모델별 자동 교정된 Y 회전 오프셋 (GameObject → 각도)
    readonly Dictionary<GameObject, float> templateYawOffsets = new Dictionary<GameObject, float>();
    bool modelLoaded = false;

    [Header("모델 공통 회전 보정 (인스펙터에서 조정)")]
    [Tooltip("차가 옆을 보면 90 또는 -90 으로 조정")]
    public float modelRotationY = 0f;

    public bool ModelLoaded => modelLoaded;

    // ── UI 상태 ───────────────────────────────────────────────
    string inputText   = "";
    string statusMsg   = "3D 모델 로딩 중...";
    Color  statusColor = new Color(0.70f, 0.70f, 0.70f);
    string railInput   = "";

    // ── GUIStyle ──────────────────────────────────────────────
    GUIStyle stTitle, stLabel, stRowLabel;
    GUIStyle stInput, stBtnPark, stBtnDepart, stStatus;
    GUIStyle stCellFree, stCellOcc;
    GUIStyle stSmall, stBtnStop, stBtnVirtual;
    bool stylesReady = false;

    // ─────────────────────────────────────────────────────────
    // 차량 모델은 Awake()에서 동기 로드한다 — Resources.Load는 이미 동기 호출이라
    // 코루틴으로 한 프레임 미룰 이유가 없고, 그렇게 미루면 그 한 프레임 사이에
    // 다른 컴포넌트(센서/앱 연동)가 먼저 차를 스폰해서 절차적 폴백 모델이 섞여 나오는
    // 경합이 생긴다. Awake()는 MiniParkSceneSetup이 이 컴포넌트를 추가하는 순간
    // 동기적으로 실행되므로, 이후 추가되는 다른 컴포넌트의 Start()보다 항상 먼저 끝난다.
    void Awake()
    {
        LoadCarModel("rc_car_colored");
        modelLoaded = true;
        Debug.Log($"[ParkingManager] 총 {carTemplates.Count}개 모델 로드");
    }

    void Start()
    {
#if UNITY_2023_1_OR_NEWER
        lot       = FindFirstObjectByType<MiniatureParkingLot>();
        scheduler = FindFirstObjectByType<ChargingScheduler>();
        bridge    = FindFirstObjectByType<ArduinoBridge>();
        rail      = FindFirstObjectByType<ParkingRail>();
#else
        lot       = Object.FindObjectOfType<MiniatureParkingLot>();
        scheduler = Object.FindObjectOfType<ChargingScheduler>();
        bridge    = Object.FindObjectOfType<ArduinoBridge>();
        rail      = Object.FindObjectOfType<ParkingRail>();
#endif
        string msg = carTemplates.Count > 0
            ? "차량 모델 준비 완료."
            : "구역을 입력하고 주차 버튼을 누르세요.";
        SetStatus(msg, new Color(0.7f, 0.7f, 0.7f));
    }

    void LoadCarModel(string resourceName)
    {
        var source = Resources.Load<GameObject>($"Cars/{resourceName}");
        if (source == null) { Debug.LogWarning($"[ParkingManager] 모델 로드 실패: {resourceName}"); return; }

        var tmp = Object.Instantiate(source);
        tmp.name = $"_Template_{resourceName}";
        Object.DontDestroyOnLoad(tmp);
        tmp.SetActive(true);

        StripEmbeddedComponents(tmp);   // 내장 카메라/조명 제거
        FixShaders(tmp);                // Standard → URP Lit 변환
        TintAllBlue(tmp);               // 모델 원본 색상(빨간 파츠 등) 무시하고 전부 파란색으로 통일
        AutoScaleTemplate(tmp, resourceName);
        tmp.SetActive(false);
        carTemplates.Add(tmp);
    }

    // 모델의 원래 파츠 색상(빨간 테일램프 등)을 무시하고 전 파츠를 단일 파란색으로 칠한다.
    // 템플릿의 공유 머티리얼에 적용하므로, 이후 Instantiate되는 모든 차량 인스턴스에 자동 반영된다.
    static void TintAllBlue(GameObject tmp)
    {
        foreach (var r in tmp.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                var m = new Material(mats[i]);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", CAR_BLUE);
                if (m.HasProperty("_Color"))     m.SetColor("_Color", CAR_BLUE);
                mats[i] = m;
            }
            r.sharedMaterials = mats;
        }
    }

    void AutoScaleTemplate(GameObject tmp, string name)
    {
        var rends = tmp.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) { Debug.LogWarning($"[ParkingManager] {name}: Renderer 없음"); return; }

        var b = ComputeWorldBounds(rends);
        float yawOffset = 0f;

        // 자동 방향 교정: X가 Z보다 20% 이상 길면 옆으로 export된 모델 → 90° 회전
        if (b.size.x > b.size.z * 1.2f)
        {
            yawOffset = 90f;
            tmp.transform.Rotate(0, 90, 0);
            b = ComputeWorldBounds(rends);   // 회전 후 bounds 재계산
        }

        float longestXZ = Mathf.Max(b.size.x, b.size.z);
        float target    = MiniatureParkingLot.SD * 0.95f;
        if (longestXZ > 0.001f)
        {
            float s = target / longestXZ;
            tmp.transform.localScale = Vector3.one * s;
            Debug.Log($"[ParkingManager] {name}: yaw보정={yawOffset}°, scale={s:F5}");
        }

        templateYawOffsets[tmp] = yawOffset;
    }

    // FBX/OBJ의 Standard 셰이더 → URP Lit 변환 (검게 보이는 문제 해결)
    void FixShaders(GameObject tmp)
    {
        var urpShader = Shader.Find("Universal Render Pipeline/Lit");
        if (urpShader == null) return;

        foreach (var r in tmp.GetComponentsInChildren<Renderer>(true))
        {
            var mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                string sn = mats[i].shader.name;
                if (sn == "Standard" || sn == "Diffuse" || sn == "Specular" || sn.StartsWith("Legacy"))
                {
                    var src = mats[i];
                    var m   = new Material(urpShader);

                    // URP Lit은 _BaseColor/_BaseMap 을 사용 (_Color/_MainTex 매핑 필수)
                    Color col = src.HasProperty("_Color") ? src.GetColor("_Color") : Color.white;
                    m.SetColor("_BaseColor", col);
                    m.SetColor("_Color",     col);

                    Texture tex = src.mainTexture;
                    if (tex != null)
                    {
                        m.SetTexture("_BaseMap", tex);
                        m.mainTexture = tex;
                    }

                    // 금속성 / 거칠기 복사
                    if (src.HasProperty("_Metallic"))
                        m.SetFloat("_Metallic", src.GetFloat("_Metallic"));
                    if (src.HasProperty("_Glossiness"))
                        m.SetFloat("_Smoothness", src.GetFloat("_Glossiness"));

                    mats[i] = m;
                    changed  = true;
                }
            }
            if (changed) r.sharedMaterials = mats;
        }
    }

    // FBX/GLTF에 내장된 Camera/Light 노드 제거 (시점 변경 방지)
    static void StripEmbeddedComponents(GameObject tmp)
    {
        foreach (var cam in tmp.GetComponentsInChildren<Camera>(true))
            Object.Destroy(cam);
        foreach (var al in tmp.GetComponentsInChildren<AudioListener>(true))
            Object.Destroy(al);
        // 내장 Light는 남겨도 무방하지만 예상치 못한 조명 변화 방지를 위해 제거
        foreach (var lt in tmp.GetComponentsInChildren<Light>(true))
            Object.Destroy(lt);
    }

    static Bounds ComputeWorldBounds(Renderer[] rends)
    {
        var b = new Bounds(rends[0].bounds.center, Vector3.zero);
        foreach (var r in rends) b.Encapsulate(r.bounds);
        return b;
    }

    // 텍스처 또는 비흰색 재질이 충분히 있는지 검사
    bool HasValidMaterials(GameObject tmp)
    {
        var rends = tmp.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return false;

        // 텍스처가 하나라도 있으면 유효
        foreach (var r in rends)
            foreach (var mat in r.sharedMaterials)
                if (mat?.mainTexture != null) return true;

        // 텍스처 없이 색상만 있는 경우: 가장 큰 렌더러(=외관)의 색상 확인
        Renderer largest = null;
        float maxVol = 0f;
        foreach (var r in rends)
        {
            float vol = r.bounds.size.x * r.bounds.size.y * r.bounds.size.z;
            if (vol > maxVol) { maxVol = vol; largest = r; }
        }
        if (largest != null)
        {
            foreach (var mat in largest.sharedMaterials)
            {
                if (mat == null) continue;
                var c = mat.color;
                if ((c.r + c.g + c.b) / 3f < 0.88f) return true;  // 비흰색 = 유효
            }
        }
        return false;
    }

    // ── GUIStyle 초기화 ───────────────────────────────────────
    void InitStyles()
    {
        if (stylesReady) return;
        stylesReady = true;

        stTitle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        stTitle.normal.textColor = new Color(0.85f, 0.92f, 1f);

        stLabel = new GUIStyle(GUI.skin.label) { fontSize = 11 };
        stLabel.normal.textColor = new Color(0.60f, 0.68f, 0.80f);

        stRowLabel = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold };
        stRowLabel.normal.textColor = new Color(0.65f, 0.75f, 1.0f);

        stInput = new GUIStyle(GUI.skin.textField)
        {
            fontSize = 18, fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter, fixedHeight = 32
        };
        stInput.normal.background  = Tex(new Color(0.16f, 0.20f, 0.26f));
        stInput.focused.background = Tex(new Color(0.20f, 0.24f, 0.32f));
        stInput.normal.textColor   = new Color(0.90f, 0.95f, 1.0f);
        stInput.focused.textColor  = new Color(0.90f, 0.95f, 1.0f);

        stBtnPark = MakeBtnStyle(new Color(0.12f, 0.52f, 0.28f));
        stBtnPark.fontSize    = 12;
        stBtnPark.fixedHeight = 30;

        stBtnDepart = MakeBtnStyle(new Color(0.52f, 0.15f, 0.15f));
        stBtnDepart.fontSize    = 12;
        stBtnDepart.fixedHeight = 30;

        stStatus = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11, wordWrap = true, alignment = TextAnchor.MiddleCenter
        };

        stCellFree = MakeCellStyle(new Color(0.22f, 0.62f, 0.38f));
        stCellOcc  = MakeCellStyle(new Color(0.75f, 0.18f, 0.18f));

        stSmall = new GUIStyle(GUI.skin.label) { fontSize = 10 };
        stSmall.normal.textColor = new Color(0.48f, 0.58f, 0.74f);

        stBtnStop = MakeBtnStyle(new Color(0.55f, 0.14f, 0.14f));
        stBtnStop.fontSize    = 12;
        stBtnStop.fixedHeight = 30;

        stBtnVirtual = MakeBtnStyle(new Color(0.30f, 0.32f, 0.55f));
        stBtnVirtual.fontSize    = 11;
        stBtnVirtual.fixedHeight = 30;
    }

    // ── 탭 1: 주차장 제어 (특정 칸에 차 주차/출차) ─────────────
    // ControlPanelUI가 선택된 탭에 따라 호출한다.
    public void DrawParkingControlUI(Rect area)
    {
        InitStyles();
        GUILayout.BeginArea(area);
        GUILayout.BeginVertical();

        GUILayout.Label("주차장 제어", stTitle, GUILayout.Height(24));
        Sep();

        GUILayout.Label("구역 입력  (예: A3, C6)", stLabel, GUILayout.Height(16));
        GUILayout.Space(2);
        string prev = inputText;
        inputText = GUILayout.TextField(inputText, 3, stInput).ToUpper().Trim();
        if (inputText != prev)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char ch in inputText)
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            inputText = sb.ToString();
        }

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("주차 ▶", stBtnPark))   OnPark();
        GUILayout.Space(6);
        if (GUILayout.Button("출차 ✕", stBtnDepart)) OnDepart();
        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        stStatus.normal.textColor = statusColor;
        GUILayout.Label(statusMsg, stStatus, GUILayout.Height(32));
        Sep();

        GUILayout.BeginHorizontal();
        GUILayout.Label("주차 현황", stLabel);
        GUILayout.FlexibleSpace();
        GUILayout.Label($"{parkedCars.Count} / 18", stLabel);
        GUILayout.EndHorizontal();
        GUILayout.Space(2);

        foreach (string row in new[] { "A", "B", "C" })
        {
            GUILayout.Label($"{row} 열", stRowLabel, GUILayout.Height(16));
            for (int half = 0; half < 2; half++)
            {
                GUILayout.BeginHorizontal();
                for (int c = 0; c < 3; c++)
                {
                    string id  = $"{row}{half * 3 + c + 1}";
                    bool   occ = parkedCars.ContainsKey(id);
                    if (GUILayout.Button(id, occ ? stCellOcc : stCellFree, GUILayout.Height(22)))
                        inputText = id;
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(1);
            }
            if (row != "C") GUILayout.Space(2);
        }

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    // ── 탭 2: 레일 제어 (아두이노 실물 레일 이동 + 캘리브레이션) ──
    public void DrawRailControlUI(Rect area)
    {
        InitStyles();
        GUILayout.BeginArea(area);
        GUILayout.BeginVertical();

        GUILayout.Label("레일 제어", stTitle, GUILayout.Height(24));
        Sep();

        bool bridgeOk = bridge != null && bridge.IsConnected;
        Color connCol = bridgeOk ? new Color(0.3f, 0.95f, 0.5f) : new Color(0.9f, 0.4f, 0.4f);
        string connStr = bridgeOk ? "● 연결됨" : "○ 연결 안 됨";
        GUILayout.Label(connStr, stRowLabel, GUILayout.Height(18));

        if (bridge != null)
        {
            Color prev2 = GUI.color;
            GUI.color = connCol;
            GUILayout.Label(bridge.Status, stSmall, GUILayout.Height(16));
            GUI.color = prev2;
        }

        GUILayout.Space(6);
        GUILayout.Label("구역 입력  (예: A3, C6)", stLabel, GUILayout.Height(16));
        GUILayout.Space(2);
        GUILayout.BeginHorizontal();
        railInput = GUILayout.TextField(railInput.ToUpper(), stInput, GUILayout.Width(70), GUILayout.Height(30));
        GUILayout.Space(4);
        if (GUILayout.Button("이동", stBtnPark, GUILayout.Height(30)))
        {
            if (bridgeOk && railInput.Length >= 2)
                bridge.SendZoneCommand(railInput);
        }
        if (GUILayout.Button("가상 이동", stBtnVirtual, GUILayout.Height(30)))
        {
            // 실물 하드웨어에는 아무 명령도 보내지 않고, 화면상 레일 위치만 그 구역으로 맞춘다.
            // 실물 레일을 손으로 직접 옮겨놓고 화면만 동기화할 때 쓴다.
            if (railInput.Length >= 2)
                rail?.JumpTo(railInput);
        }
        if (GUILayout.Button("정지", stBtnStop, GUILayout.Height(30)))
        {
            if (bridgeOk) bridge.SendStop();
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        if (GUILayout.Button("↩  원점 복귀", stBtnPark, GUILayout.Height(28)))
        {
            if (bridgeOk) bridge.SendHome();
        }

        // ── 캘리브레이션 ───────────────────────────────────────────
        if (bridge != null)
        {
            GUILayout.Space(6); Sep();

            bool cal = bridge.IsCalibrated;
            Color calCol = cal ? new Color(0.3f, 0.95f, 0.5f) : new Color(1f, 0.75f, 0.2f);
            GUI.color = calCol;
            GUILayout.Label(cal ? "캘리브레이션 완료" : $"캘리브레이션 필요 ({bridge.CalibCount}/6)",
                stSmall, GUILayout.Height(16));
            GUI.color = Color.white;

            // 캡처 버튼: 입력한 구역이 캘리브 기준점이고 이동 완료된 경우
            string[] calibTargets = { "A6", "A1", "B6", "B1", "C6", "C1" };
            string upper = railInput.ToUpper().Trim();
            bool isCalibTarget = System.Array.IndexOf(calibTargets, upper) >= 0;

            if (isCalibTarget && !bridge.IsMoving)
            {
                Color btnCol = bridge.HasCaptured(upper)
                    ? new Color(0.2f, 0.55f, 0.2f)
                    : new Color(0.55f, 0.35f, 0.1f);
                GUIStyle stCapture = MakeBtnStyle(btnCol);
                stCapture.fontSize    = 11;
                stCapture.fixedHeight = 24;
                string capLabel = bridge.HasCaptured(upper) ? $"✓ {upper} 재캡처" : $"📍 {upper} 캡처";
                if (GUILayout.Button(capLabel, stCapture))
                    bridge.CapturePoint(upper);
            }

            if (bridge.CalibCount >= 4)
            {
                GUILayout.Space(2);
                if (GUILayout.Button("▶  변환 계산", stBtnPark, GUILayout.Height(26)))
                    bridge.ComputeCalibration();
            }

            if (cal)
            {
                if (GUILayout.Button("↺  캘리브레이션 초기화", stBtnStop, GUILayout.Height(24)))
                    bridge.ResetCalibration();
            }
        }

        GUILayout.EndVertical();
        GUILayout.EndArea();
    }

    // ── 스케줄러에서 호출: 충전 완료 자동 출차 ──────────────────
    public void DepartCar(string id)
    {
        if (!parkedCars.TryGetValue(id, out var car)) return;
        Destroy(car);
        parkedCars.Remove(id);
        SetStatus($"{id}  충전 완료 → 출차.", new Color(0.4f, 0.9f, 0.5f));
    }

    /// <summary>구역이 이미 점유돼 있는지 (MobileLink의 빈 구역 배정용)</summary>
    public bool IsOccupied(string id) => parkedCars.ContainsKey(id);

    // ── 스케줄러에서 호출: 특정 구역에 차량 사전 배치 ──────────
    public void SpawnCarAt(string id, Color col)
    {
        if (!lot.SpaceMap.ContainsKey(id)) return;
        if (parkedCars.ContainsKey(id)) return;

        Vector3 pos = lot.WorldPos(id);
        var car = modelLoaded
            ? SpawnCar(pos, id[0] != 'C', col)
            : SpawnProceduralCar(pos, id[0] != 'C', col);
        parkedCars[id] = car;
        StartCoroutine(ScaleIn(car.transform));
    }

    // ── 주차 ─────────────────────────────────────────────────
    void OnPark()   => Park(inputText.Trim().ToUpper());
    void OnDepart() => Depart(inputText.Trim().ToUpper());

    /// <summary>구역 id에 주차 (UI 버튼 / 센서 감지 공용)</summary>
    public void Park(string id)
    {
        if (!modelLoaded)
        {
            SetStatus("3D 모델 로딩 중... 잠시 후 다시 시도하세요.", new Color(1f, 0.75f, 0.2f));
            return;
        }
        if (!ValidateId(id)) return;
        if (parkedCars.ContainsKey(id))
        {
            SetStatus($"{id} 는 이미 주차 중입니다.", new Color(1f, 0.75f, 0.2f));
            return;
        }

        Vector3 pos = lot.WorldPos(id);
        var car = SpawnCar(pos, id[0] != 'C', CAR_BLUE);
        parkedCars[id] = car;
        SetStatus($"{id}  주차 완료!", new Color(0.4f, 0.9f, 0.5f));
        StartCoroutine(ScaleIn(car.transform));

        // 충전 스케줄러 등록은 여기서 하지 않는다 — 초음파 감지/수동 주차는 시각 표시만.
        // 스케줄링(ChargingScheduler.Cars)에는 앱(MobileLink → SupabaseBridge)으로
        // 등록된 차량만 들어간다.
    }

    /// <summary>구역 id에서 출차 (UI 버튼 / 센서 감지 공용)</summary>
    public void Depart(string id)
    {
        if (!ValidateId(id)) return;
        if (!parkedCars.TryGetValue(id, out var car))
        {
            SetStatus($"{id} 에 주차된 차가 없습니다.", new Color(1f, 0.75f, 0.2f));
            return;
        }
        Destroy(car);
        parkedCars.Remove(id);
        SetStatus($"{id}  출차 완료.", new Color(0.7f, 0.7f, 0.7f));
        scheduler?.RemoveCar(id);
    }

    bool ValidateId(string id)
    {
        if (lot == null || !lot.SpaceMap.ContainsKey(id))
        {
            SetStatus($"'{id}' 은 유효하지 않습니다.\nA1 ~ C6 사이로 입력하세요.", new Color(1f, 0.4f, 0.4f));
            return false;
        }
        return true;
    }

    void SetStatus(string msg, Color col) { statusMsg = msg; statusColor = col; }

    // ── 차량 생성 (랜덤 모델 선택) ───────────────────────────
    GameObject SpawnCar(Vector3 worldPos, bool faceDown, Color bodyColor)
    {
        if (carTemplates.Count > 0)
        {
            var template = carTemplates[Random.Range(0, carTemplates.Count)];
            var car = Object.Instantiate(template);
            car.SetActive(true);
            car.transform.position   = worldPos + Vector3.up * 0.001f;
            car.transform.localScale = template.transform.localScale;

            // AutoScaleTemplate 에서 교정된 yaw 오프셋 반영
            float offsetYaw;
            if (!templateYawOffsets.TryGetValue(template, out offsetYaw))
                offsetYaw = 0f;
            float yaw = faceDown ? (modelRotationY + offsetYaw) : (modelRotationY + offsetYaw + 180f);
            car.transform.rotation = Quaternion.Euler(0, yaw, 0);
            return car;
        }
        return SpawnProceduralCar(worldPos, faceDown, bodyColor);
    }

    // ── 절차적 차량 (폴백) ────────────────────────────────────
    GameObject SpawnProceduralCar(Vector3 worldPos, bool faceDown, Color bodyColor)
    {
        float bW = MiniatureParkingLot.SW * 0.74f;
        float bL = MiniatureParkingLot.SD * 0.58f;
        float bH = 0.017f;
        float cH = 0.013f;
        float wR = 0.007f;
        float wW = 0.006f;

        var carRoot = new GameObject("Car");
        carRoot.transform.position = worldPos + Vector3.up * 0.0005f;
        carRoot.transform.rotation = faceDown
            ? Quaternion.identity : Quaternion.Euler(0, 180, 0);

        void B(string nm, Vector3 lp, Vector3 sc, Color col, bool sh = true)
            => MiniatureParkingLot.Box(nm, lp, sc, col, carRoot, sh);

        void Cyl(string nm, Vector3 lp, Vector3 sc, Quaternion rot, Color col)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = nm;
            go.transform.SetParent(carRoot.transform);
            go.transform.localPosition = lp;
            go.transform.localScale    = sc;
            go.transform.localRotation = rot;
            Object.Destroy(go.GetComponent<Collider>());
            var mat = new Material(
                Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            mat.color = col;
            var rend = go.GetComponent<Renderer>();
            rend.material          = mat;
            rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            rend.receiveShadows    = true;
        }

        Color dk   = bodyColor * 0.80f;
        Color gls  = new Color(0.45f, 0.65f, 0.85f);
        Color crm  = new Color(0.78f, 0.78f, 0.80f);
        Color blk  = new Color(0.10f, 0.10f, 0.10f);
        Color hub  = new Color(0.65f, 0.65f, 0.68f);
        Color wht  = new Color(0.95f, 0.95f, 0.88f);
        Color red  = new Color(0.85f, 0.10f, 0.10f);
        Color snsr = new Color(0.20f, 0.20f, 0.22f);

        B("Chassis", new Vector3(0, bH*0.26f, 0),         new Vector3(bW,       bH*0.52f, bL),       bodyColor);
        B("Body",    new Vector3(0, bH*0.76f, 0),         new Vector3(bW,       bH*0.55f, bL*0.88f), bodyColor);
        B("Hood",    new Vector3(0, bH*0.57f, -bL*0.43f), new Vector3(bW*0.90f, bH*0.20f, bL*0.17f), bodyColor);
        B("Trunk",   new Vector3(0, bH*0.57f,  bL*0.42f), new Vector3(bW*0.88f, bH*0.18f, bL*0.14f), bodyColor);
        B("Cabin",   new Vector3(0, bH+cH*0.50f, bL*0.02f), new Vector3(bW*0.80f, cH, bL*0.54f), dk);
        B("WindF",   new Vector3(0, bH+cH*0.36f, -bL*0.24f), new Vector3(bW*0.76f, cH*0.68f, 0.003f), gls, false);
        B("WindR",   new Vector3(0, bH+cH*0.35f,  bL*0.24f), new Vector3(bW*0.72f, cH*0.60f, 0.003f), gls, false);
        B("WinL",    new Vector3(-bW*0.40f, bH+cH*0.42f, 0), new Vector3(0.002f, cH*0.55f, bL*0.36f), gls, false);
        B("WinR",    new Vector3( bW*0.40f, bH+cH*0.42f, 0), new Vector3(0.002f, cH*0.55f, bL*0.36f), gls, false);
        B("BumperF", new Vector3(0, bH*0.36f, -bL*0.50f), new Vector3(bW*0.86f, bH*0.36f, 0.003f), crm);
        B("BumperR", new Vector3(0, bH*0.35f,  bL*0.50f), new Vector3(bW*0.84f, bH*0.30f, 0.003f), crm);
        B("HeadL",   new Vector3(-bW*0.37f, bH*0.64f, -bL*0.50f), new Vector3(bW*0.18f, bH*0.22f, 0.002f), wht, false);
        B("HeadR",   new Vector3( bW*0.37f, bH*0.64f, -bL*0.50f), new Vector3(bW*0.18f, bH*0.22f, 0.002f), wht, false);
        B("TailL",   new Vector3(-bW*0.38f, bH*0.64f, bL*0.50f), new Vector3(bW*0.20f, bH*0.20f, 0.002f), red, false);
        B("TailR",   new Vector3( bW*0.38f, bH*0.64f, bL*0.50f), new Vector3(bW*0.20f, bH*0.20f, 0.002f), red, false);
        B("MirrorL", new Vector3(-bW*0.52f, bH+cH*0.15f, -bL*0.21f), new Vector3(0.005f, 0.004f, 0.009f), blk);
        B("MirrorR", new Vector3( bW*0.52f, bH+cH*0.15f, -bL*0.21f), new Vector3(0.005f, 0.004f, 0.009f), blk);
        B("SensorBase", new Vector3(0, bH+cH+0.003f, bL*0.05f), new Vector3(0.013f, 0.004f, 0.018f), snsr);
        Cyl("SensorDome", new Vector3(0, bH+cH+0.008f, bL*0.05f), new Vector3(0.008f, 0.003f, 0.008f), Quaternion.identity, snsr);

        float wxO = bW * 0.47f, wzO = bL * 0.33f;
        Quaternion wRot = Quaternion.Euler(0, 0, 90f);
        Vector3 wSc = new Vector3(wR * 2f, wW / 2f, wR * 2f);
        Vector3 hSc = new Vector3(wR * 1.4f, wW * 0.12f, wR * 1.4f);
        var wpts = new (float x, float z, string n)[]
        {
            (-wxO,  wzO, "WheelRL"), ( wxO,  wzO, "WheelRR"),
            (-wxO, -wzO, "WheelFL"), ( wxO, -wzO, "WheelFR"),
        };
        foreach (var (wx, wz, wn) in wpts)
        {
            Cyl(wn,          new Vector3(wx, wR, wz), wSc, wRot, blk);
            float hx = wx < 0 ? wx - wW * 0.55f : wx + wW * 0.55f;
            Cyl(wn + "_Hub", new Vector3(hx, wR, wz), hSc, wRot, hub);
        }
        return carRoot;
    }

    // ── 등장 애니메이션 (target scale 보존) ──────────────────
    IEnumerator ScaleIn(Transform t)
    {
        Vector3 targetScale = t.localScale;
        float dur = 0.35f, e = 0f;
        t.localScale = Vector3.zero;
        while (e < dur)
        {
            e += Time.deltaTime;
            t.localScale = targetScale * Mathf.SmoothStep(0f, 1f, e / dur);
            yield return null;
        }
        t.localScale = targetScale;
    }

    // ── UI 헬퍼 ───────────────────────────────────────────────
    void Sep()
    {
        GUILayout.Space(3);
        Rect r = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
        Color prev = GUI.color;
        GUI.color = new Color(1, 1, 1, 0.15f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = prev;
        GUILayout.Space(3);
    }

    static Texture2D Tex(Color col)
    {
        var t = new Texture2D(1, 1);
        t.SetPixel(0, 0, col);
        t.Apply();
        return t;
    }

    static GUIStyle MakeBtnStyle(Color bg)
    {
        var s = new GUIStyle(GUI.skin.button) { fontSize = 15, fontStyle = FontStyle.Bold, fixedHeight = 48 };
        s.normal.background  = Tex(bg);
        s.hover.background   = Tex(bg * 1.25f);
        s.active.background  = Tex(bg * 0.70f);
        s.normal.textColor   = Color.white;
        s.hover.textColor    = Color.white;
        s.active.textColor   = Color.white;
        return s;
    }

    static GUIStyle MakeCellStyle(Color bg)
    {
        var s = new GUIStyle(GUI.skin.button) { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        s.normal.background = Tex(bg);
        s.hover.background  = Tex(bg * 1.20f);
        s.active.background = Tex(bg * 0.75f);
        s.normal.textColor  = Color.white;
        s.hover.textColor   = Color.white;
        s.active.textColor  = Color.white;
        return s;
    }
}
