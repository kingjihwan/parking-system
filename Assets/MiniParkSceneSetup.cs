using UnityEngine;

/// <summary>
/// 씬 원클릭 자동 구성
/// 빈 GameObject에 이 컴포넌트 하나만 추가하고 Play
/// </summary>
public class MiniParkSceneSetup : MonoBehaviour
{
    // Han Mobile 앱 연동 접속 정보.
    // SupabaseBridge 는 런타임에 생성돼서 인스펙터가 없다 — 값을 여기서 넘겨준다.
    // anon(publishable) key 는 공개용 키다. service_role key 를 넣지 말 것.
    [Header("Han Mobile 연동")]
    public string supabaseUrl     = "https://yzcrmmrbwoprinvuhgor.supabase.co";
    public string supabaseAnonKey = "sb_publishable_Kf2O3HbashSEhyeqAKO5sw_gJ1qcLT7";

    void Awake()
    {
        SetupLighting();
        SetupModel();
        SetupCamera();
        SetupParkingManager();
        SetupRail();
        SetupScheduler();
        SetupArduinoBridge();
        SetupPowerMonitor();
        SetupParkingSensorBridge();
        SetupZAxisChargeBridge();
        SetupControlPanelUI();
        SetupSupabaseBridge();
        SetupMobileLink();
    }

    void SetupLighting()
    {
#if UNITY_2023_1_OR_NEWER
        Light sun = FindFirstObjectByType<Light>();
#else
        Light sun = Object.FindObjectOfType<Light>();
#endif
        if (sun == null)
        {
            sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
        }
        sun.intensity          = 1.4f;
        sun.color              = new Color(1.0f, 0.98f, 0.94f);  // 맑은 낮 햇빛
        sun.shadows            = LightShadows.Soft;
        sun.shadowStrength     = 0.65f;
        sun.transform.rotation = Quaternion.Euler(55f, -35f, 0f);

        RenderSettings.ambientMode      = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight     = new Color(0.50f, 0.52f, 0.58f);
        RenderSettings.ambientIntensity = 1f;
    }

    void SetupModel()
    {
        var go = new GameObject("ParkingModel");
        go.AddComponent<MiniatureParkingLot>();
    }

    void SetupCamera()
    {
        Camera cam = Camera.main;
        if (cam == null)
        {
            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            cam    = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
        }
        if (cam.GetComponent<TopViewCamera>() == null)
            cam.gameObject.AddComponent<TopViewCamera>();
    }

    void SetupParkingManager()
    {
#if UNITY_2023_1_OR_NEWER
        if (FindFirstObjectByType<ParkingManager>() != null) return;
#else
        if (Object.FindObjectOfType<ParkingManager>() != null) return;
#endif
        var go = new GameObject("ParkingManager");
        go.AddComponent<ParkingManager>();
    }

    void SetupRail()
    {
#if UNITY_2023_1_OR_NEWER
        if (FindFirstObjectByType<ParkingRail>() != null) return;
#else
        if (Object.FindObjectOfType<ParkingRail>() != null) return;
#endif
        var go = new GameObject("ParkingRail");
        go.AddComponent<ParkingRail>();
    }

    void SetupScheduler()
    {
#if UNITY_2023_1_OR_NEWER
        if (FindFirstObjectByType<ChargingScheduler>() != null) return;
#else
        if (Object.FindObjectOfType<ChargingScheduler>() != null) return;
#endif
        var go = new GameObject("ChargingScheduler");
        go.AddComponent<ChargingScheduler>();
    }

    // ── 아두이노 4종 브릿지 (실물 하드웨어 연결 시 포트명은 각 컴포넌트 인스펙터에서 설정) ──
    void SetupArduinoBridge()
    {
#if UNITY_2023_1_OR_NEWER
        if (FindFirstObjectByType<ArduinoBridge>() != null) return;
#else
        if (Object.FindObjectOfType<ArduinoBridge>() != null) return;
#endif
        var go = new GameObject("ArduinoBridge");
        go.AddComponent<ArduinoBridge>();
    }

    void SetupPowerMonitor()
    {
#if UNITY_2023_1_OR_NEWER
        if (FindFirstObjectByType<PowerMonitor>() != null) return;
#else
        if (Object.FindObjectOfType<PowerMonitor>() != null) return;
#endif
        var go = new GameObject("PowerMonitor");
        go.AddComponent<PowerMonitor>();
    }

    void SetupParkingSensorBridge()
    {
#if UNITY_2023_1_OR_NEWER
        if (FindFirstObjectByType<ParkingSensorBridge>() != null) return;
#else
        if (Object.FindObjectOfType<ParkingSensorBridge>() != null) return;
#endif
        var go = new GameObject("ParkingSensorBridge");
        go.AddComponent<ParkingSensorBridge>();
    }

    void SetupZAxisChargeBridge()
    {
#if UNITY_2023_1_OR_NEWER
        if (FindFirstObjectByType<ZAxisChargeBridge>() != null) return;
#else
        if (Object.FindObjectOfType<ZAxisChargeBridge>() != null) return;
#endif
        var go = new GameObject("ZAxisChargeBridge");
        go.AddComponent<ZAxisChargeBridge>();
    }

    void SetupControlPanelUI()
    {
#if UNITY_2023_1_OR_NEWER
        if (FindFirstObjectByType<ControlPanelUI>() != null) return;
#else
        if (Object.FindObjectOfType<ControlPanelUI>() != null) return;
#endif
        var go = new GameObject("ControlPanelUI");
        go.AddComponent<ControlPanelUI>();
    }

    // Han Mobile 앱 연동 — 접속 정보는 위 인스펙터 필드에서 가져온다.
    void SetupSupabaseBridge()
    {
#if UNITY_2023_1_OR_NEWER
        if (FindFirstObjectByType<SupabaseBridge>() != null) return;
#else
        if (Object.FindObjectOfType<SupabaseBridge>() != null) return;
#endif
        var go = new GameObject("SupabaseBridge");
        var bridge = go.AddComponent<SupabaseBridge>();
        bridge.supabaseUrl = supabaseUrl;
        bridge.anonKey     = supabaseAnonKey;
    }

    void SetupMobileLink()
    {
#if UNITY_2023_1_OR_NEWER
        if (FindFirstObjectByType<MobileLink>() != null) return;
#else
        if (Object.FindObjectOfType<MobileLink>() != null) return;
#endif
        var go = new GameObject("MobileLink");
        go.AddComponent<MobileLink>();
    }
}
