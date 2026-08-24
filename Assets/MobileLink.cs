using UnityEngine;

/// <summary>
/// Han Mobile(SupabaseBridge) ↔ Unity 연결.
///
/// 입/출차 표시(차 모양)는 두 경로로 들어올 수 있다:
///   · 초음파 감지 (ParkingSensorBridge → ParkingManager.Park/Depart) — 시각 표시만.
///   · 앱 등록 (SupabaseBridge → 이 스크립트) — 시각 표시 + ChargingScheduler 등록.
///
/// ChargingScheduler.Cars(충전 스케줄링 대상)에는 앱으로 등록된 차량만 들어간다.
/// </summary>
public class MobileLink : MonoBehaviour
{
    SupabaseBridge      bridge;
    ParkingManager      parkMgr;
    ChargingScheduler   scheduler;
    MiniatureParkingLot lot;

    void Start()
    {
#if UNITY_2023_1_OR_NEWER
        bridge    = FindFirstObjectByType<SupabaseBridge>();
        parkMgr   = FindFirstObjectByType<ParkingManager>();
        scheduler = FindFirstObjectByType<ChargingScheduler>();
        lot       = FindFirstObjectByType<MiniatureParkingLot>();
#else
        bridge    = Object.FindObjectOfType<SupabaseBridge>();
        parkMgr   = Object.FindObjectOfType<ParkingManager>();
        scheduler = Object.FindObjectOfType<ChargingScheduler>();
        lot       = Object.FindObjectOfType<MiniatureParkingLot>();
#endif
        if (bridge == null) { Debug.LogWarning("[MobileLink] SupabaseBridge를 찾을 수 없습니다."); return; }

        bridge.OnVehicleArrived  += Arrived;
        bridge.OnVehicleDeparted += Departed;
    }

    void Arrived(SupabaseBridge.VehicleRecord v)
    {
        string zone = string.IsNullOrEmpty(v.zone) ? PickFreeZone() : v.zone;
        if (zone == null)
        {
            Debug.LogWarning($"[MobileLink] {v.car_number}: 배정할 빈 구역이 없습니다.");
            return;
        }

        // 앱이 구역을 비워둬서 Unity 가 배정한 경우, 그 결과를 앱/DB 쪽에도 돌려준다.
        // 이걸 빼먹으면 v.zone 이 계속 빈 값이라 나중에 Departed() 가 아무것도 못 지운다.
        if (string.IsNullOrEmpty(v.zone))
        {
            v.zone = zone;
            bridge.PushState(v.id, v.battery_pct, v.power_mw, zone: zone);
        }

        parkMgr?.SpawnCarAt(zone, ParkingManager.CAR_BLUE);
        if (scheduler == null) return;

        // 앱의 입/출차 시각 간격을 시뮬레이션 시간축(SimHour) 위에 그대로 얹는다.
        float entryHour     = scheduler.SimHour;
        float durationHours = (float)(v.ExitTime - v.EntryTime).TotalHours;
        if (durationHours <= 0f) durationHours = 2f;  // 앱 값이 비정상이면 기본 2시간

        // max_capacity_mws(mW·s) → 스케줄러 내부 단위(kWh 자리)로 축소 변환.
        // 절대 단위가 아니라 차량 간 상대적인 배터리 크기 비교용이라 실제 mWh 환산은 아니다.
        float maxKWh     = Mathf.Max(0.01f, v.max_capacity_mws / 1000f);
        float currentKWh = maxKWh * Mathf.Clamp01(v.battery_pct / 100f);

        scheduler.RegisterCar(new ChargingScheduler.CarData
        {
            spaceId       = zone,
            entryHour     = entryHour,
            exitHour      = entryHour + durationHours,
            speedKW       = 7.4f,
            currentKWh    = currentKWh,
            maxKWh        = maxKWh,
            carNumber     = v.car_number,
            exitTimeLabel = v.ExitTime.ToLocalTime().ToString("HH:mm"),
            vehicleId     = v.id,
        });
    }

    void Departed(SupabaseBridge.VehicleRecord v)
    {
        if (string.IsNullOrEmpty(v.zone)) return;
        parkMgr?.DepartCar(v.zone);
        scheduler?.RemoveCar(v.zone);
    }

    string PickFreeZone()
    {
        if (lot == null || parkMgr == null) return null;
        foreach (var id in lot.SpaceMap.Keys)
            if (!parkMgr.IsOccupied(id)) return id;
        return null;
    }
}
