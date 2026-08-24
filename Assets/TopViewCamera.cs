using UnityEngine;

/// <summary>
/// 주차장 모형 카메라 컨트롤러 — 탑뷰 / 쿼터뷰 전환 가능
///
///  Tab          : 탑뷰 ↔ 쿼터뷰 전환
///  우클릭 드래그 : 화면 패닝
///  스크롤 휠     : 줌 인/아웃
///  Q / E        : 쿼터뷰에서 좌/우 회전
///  F            : 모형 전체 화면 맞추기
///  R            : 카메라 초기화
/// </summary>
[RequireComponent(typeof(Camera))]
public class TopViewCamera : MonoBehaviour
{
    [Header("탑뷰")]
    public float topFOV      = 50f;

    [Header("쿼터뷰")]
    public float qPitch      = 58f;     // 수직 각도 (클수록 더 위에서 내려다봄)
    public float qYaw        = 225f;    // 초기 수평 각도
    public float qDistance   = 2.0f;   // 초기 거리
    public float rotSpeed    = 80f;

    [Header("공통")]
    public float zoomMin     = 0.30f;
    public float zoomMax     = 5.00f;
    public float zoomSpeed   = 0.20f;
    public float panSmooth   = 12f;

    [Header("타겟 (비우면 자동 탐색)")]
    public MiniatureParkingLot target;

    // ── 내부 상태 ──────────────────────────────────────────────
    bool    topMode = true;   // 기본: 탑뷰
    float   zoom,   tZoom;
    float   yaw,    tYaw;
    Vector3 focus,  tFocus;

    Vector3 dragWorld;
    Vector3 focusOnDrag;
    Camera  cam;

    static readonly Vector3 DEFAULT_CENTER = new Vector3(
        MiniatureParkingLot.PW / 2f, MiniatureParkingLot.PT, MiniatureParkingLot.PD / 2f);

    void Start()
    {
        cam = GetComponent<Camera>();
        if (target == null)
#if UNITY_2023_1_OR_NEWER
            target = FindFirstObjectByType<MiniatureParkingLot>();
#else
            target = Object.FindObjectOfType<MiniatureParkingLot>();
#endif
        ResetCamera();
    }

    [ContextMenu("카메라 초기화")]
    public void ResetCamera()
    {
        topMode = true;             // 리셋 = 탑뷰
        focus   = tFocus = GetCenter();
        yaw     = tYaw   = 0f;
        zoom    = tZoom  = FitZoom();

        cam.fieldOfView   = topFOV;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane  = 10f;
        cam.backgroundColor = new Color(0.15f, 0.17f, 0.20f);
        cam.clearFlags      = CameraClearFlags.SolidColor;

        Apply();
    }

    void Update()
    {
        Keys();
        Zoom();
        Pan();

        float t = Time.deltaTime * panSmooth;
        zoom  = Mathf.Lerp(zoom,  tZoom, t);
        yaw   = Mathf.LerpAngle(yaw, tYaw, t);
        focus = Vector3.Lerp(focus, tFocus, t);

        Apply();
    }

    void Keys()
    {
        if (Input.GetKeyDown(KeyCode.Tab)) ToggleMode();
        if (Input.GetKeyDown(KeyCode.R))   ResetCamera();
        if (Input.GetKeyDown(KeyCode.F))   FocusAll();

        if (!topMode)
        {
            if (Input.GetKey(KeyCode.Q)) tYaw -= rotSpeed * Time.deltaTime;
            if (Input.GetKey(KeyCode.E)) tYaw += rotSpeed * Time.deltaTime;
        }
    }

    void Zoom()
    {
        float s = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(s) < 0.001f) return;
        tZoom = Mathf.Clamp(tZoom - s * zoomSpeed * zoom, zoomMin, zoomMax);
    }

    void Pan()
    {
        if (Input.GetMouseButtonDown(1))
        {
            dragWorld   = RayToGround(Input.mousePosition);
            focusOnDrag = focus;
        }
        if (Input.GetMouseButton(1))
        {
            Vector3 cur = RayToGround(Input.mousePosition);
            tFocus = focusOnDrag + (dragWorld - cur);
        }
    }

    void Apply()
    {
        if (topMode)
        {
            // 탑뷰: 정면 위에서 수직으로 내려다봄
            transform.position = focus + Vector3.up * zoom;
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
        else
        {
            // 쿼터뷰: 비스듬히 위에서 내려다봄
            // 카메라를 focus 기준으로 pitch/yaw 방향 반대편(뒤쪽-위쪽)에 배치
            float pitchRad = qPitch * Mathf.Deg2Rad;
            float yawRad   = yaw    * Mathf.Deg2Rad;

            // focus 에서 카메라 방향 (구면 좌표 → 직교)
            float hDist = zoom * Mathf.Cos(pitchRad);  // 수평 거리
            float vDist = zoom * Mathf.Sin(pitchRad);  // 수직 높이

            Vector3 offset = new Vector3(
                hDist * Mathf.Sin(yawRad),
                vDist,
                hDist * Mathf.Cos(yawRad)
            );

            transform.position = focus + offset;
            transform.LookAt(focus);          // 항상 focus를 바라봄
        }
    }

    // ── 주차장 전체가 화면에 맞는 줌 거리 계산 ─────────────────
    float FitZoom()
    {
        float lotHalf = Mathf.Max(MiniatureParkingLot.PW, MiniatureParkingLot.PD) * 0.5f;
        float margin  = 1.25f;  // 25% 여유
        float fovHalf = topFOV  * 0.5f * Mathf.Deg2Rad;
        return (lotHalf * margin) / Mathf.Tan(fovHalf);
    }

    // ── 공개 API ────────────────────────────────────────────────
    public bool IsTopMode => topMode;

    public void ToggleMode()
    {
        topMode = !topMode;
        if (topMode)
        {
            tZoom = FitZoom();
            tYaw  = 0f;
            cam.fieldOfView = topFOV;
        }
        else
        {
            tZoom = qDistance;
            tYaw  = qYaw;
            cam.fieldOfView = 50f;
        }
    }

    public void FocusAll()
    {
        tFocus = GetCenter();
        tZoom  = topMode ? FitZoom() : qDistance;
    }

    public void RotateStep(float degrees)
    {
        if (!topMode) tYaw += degrees;
    }

    Vector3 GetCenter() =>
        target != null ? target.LotCenter : DEFAULT_CENTER;

    Vector3 RayToGround(Vector3 screenPos)
    {
        var ray = cam.ScreenPointToRay(screenPos);
        float groundY = target != null ? target.transform.position.y : 0f;
        float t = ray.direction.y == 0f ? 100f : (groundY - ray.origin.y) / ray.direction.y;
        if (t < 0f) t = 100f;
        return ray.origin + ray.direction * t;
    }
}
