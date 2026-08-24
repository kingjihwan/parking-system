// Han Mobile ↔ Unity 연동 브릿지
//
// Supabase 의 public.vehicles 테이블을 주기적으로 읽어
//   · 휴대폰 앱에서 새로 등록된 차량  → OnVehicleArrived
//   · 앱에서 출차 처리된 차량          → OnVehicleDeparted
// 이벤트로 알려주고, 반대로 Unity 의 충전 상태를 PushState() 로 다시 올린다.
//
// 이 스크립트는 데이터 계층만 담당한다. ParkingManager / ChargingScheduler 와
// 연결하는 코드는 프로젝트마다 다르므로 이벤트를 구독해서 붙인다.
// 연결 예시는 저장소의 README.md "Unity 연동" 절 참고.
//
// 설치: 이 파일을 Assets/ 아래에 두고, 빈 GameObject 에 붙인 뒤
//       인스펙터에서 Supabase Url / Anon Key 를 채운다.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class SupabaseBridge : MonoBehaviour
{
    [Header("Supabase")]
    [Tooltip("예: https://xxxxxxxxxxxx.supabase.co")]
    public string supabaseUrl = "https://yzcrmmrbwoprinvuhgor.supabase.co";

    [Tooltip("anon (publishable) key — service_role key 를 넣지 말 것")]
    public string anonKey = "sb_publishable_Kf2O3HbashSEhyeqAKO5sw_gJ1qcLT7";

    [Header("동작")]
    [Tooltip("테이블을 다시 읽어오는 주기 (초)")]
    public float pollInterval = 1.0f;

    [Tooltip("충전 상태를 Supabase 로 올리는 주기 (초). 너무 잦으면 요청이 낭비된다.")]
    public float pushInterval = 1.0f;

    // ── 이벤트 ───────────────────────────────────────────────────

    /// <summary>앱에서 새로 입차 등록된 차량. 구역이 비어 있으면 zone 이 null 이다.</summary>
    public event Action<VehicleRecord> OnVehicleArrived;

    /// <summary>앱에서 출차 처리됐거나 테이블에서 사라진 차량.</summary>
    public event Action<VehicleRecord> OnVehicleDeparted;

    /// <summary>폴링 한 사이클이 끝날 때마다, 현재 활성 차량 전체.</summary>
    public event Action<List<VehicleRecord>> OnSnapshot;

    // ── 상태 ─────────────────────────────────────────────────────

    readonly Dictionary<string, VehicleRecord> known = new Dictionary<string, VehicleRecord>();
    readonly Dictionary<string, PendingUpdate> pending = new Dictionary<string, PendingUpdate>();

    string lastError = "";
    bool   connected = false;

    public bool   IsConnected => connected;
    public string LastError   => lastError;
    public IReadOnlyDictionary<string, VehicleRecord> Vehicles => known;

    bool Configured =>
        !string.IsNullOrWhiteSpace(supabaseUrl) && !string.IsNullOrWhiteSpace(anonKey);

    string RestBase => supabaseUrl.TrimEnd('/') + "/rest/v1";

    // ─────────────────────────────────────────────────────────────

    void Start()
    {
        if (!Configured)
        {
            Debug.LogWarning("[SupabaseBridge] URL / anon key 가 비어 있습니다. 브릿지를 시작하지 않습니다.");
            enabled = false;
            return;
        }
        StartCoroutine(PollLoop());
        StartCoroutine(PushLoop());
    }

    // ── 폴링 ─────────────────────────────────────────────────────

    IEnumerator PollLoop()
    {
        while (true)
        {
            yield return Fetch();
            yield return new WaitForSeconds(Mathf.Max(0.25f, pollInterval));
        }
    }

    IEnumerator Fetch()
    {
        string url = $"{RestBase}/vehicles?select=*&status=neq.departed&order=entry_at.asc";
        using var req = UnityWebRequest.Get(url);
        ApplyHeaders(req);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            connected = false;
            lastError = req.error;
            yield break;
        }

        connected = true;
        lastError = "";

        List<VehicleRecord> rows;
        try
        {
            rows = ParseArray(req.downloadHandler.text);
        }
        catch (Exception e)
        {
            lastError = $"응답 파싱 실패: {e.Message}";
            yield break;
        }

        Reconcile(rows);
    }

    /// <summary>새로 나타난 차량 / 사라진 차량을 이벤트로 흘려보낸다.</summary>
    void Reconcile(List<VehicleRecord> rows)
    {
        var seen = new HashSet<string>();

        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.id)) continue;
            seen.Add(row.id);

            if (!known.ContainsKey(row.id))
            {
                known[row.id] = row;
                OnVehicleArrived?.Invoke(row);
            }
            else
            {
                known[row.id] = row;
            }
        }

        // 테이블에서 빠졌거나 departed 로 바뀐 차량
        var gone = new List<string>();
        foreach (var kv in known)
            if (!seen.Contains(kv.Key)) gone.Add(kv.Key);

        foreach (var id in gone)
        {
            var record = known[id];
            known.Remove(id);
            pending.Remove(id);
            OnVehicleDeparted?.Invoke(record);
        }

        OnSnapshot?.Invoke(rows);
    }

    // ── 상태 올리기 ──────────────────────────────────────────────

    struct PendingUpdate
    {
        public float  batteryPct;
        public float  powerMW;
        public string status;
        public string zone;
    }

    /// <summary>
    /// 충전 진행 상황을 예약한다. 실제 전송은 pushInterval 주기로 묶여서 나간다.
    /// 매 프레임 호출해도 안전하다.
    /// </summary>
    public void PushState(string vehicleId, float batteryPct, float powerMW,
                          string status = null, string zone = null)
    {
        if (string.IsNullOrEmpty(vehicleId)) return;
        pending[vehicleId] = new PendingUpdate
        {
            batteryPct = Mathf.Clamp(batteryPct, 0f, 100f),
            powerMW    = Mathf.Max(0f, powerMW),
            status     = status,
            zone       = zone,
        };
    }

    /// <summary>Unity 쪽에서 출차시킬 때 호출. 앱 목록에서도 사라진다.</summary>
    public void MarkDeparted(string vehicleId)
    {
        if (string.IsNullOrEmpty(vehicleId)) return;
        pending.Remove(vehicleId);
        known.Remove(vehicleId);
        StartCoroutine(Patch(vehicleId,
            "{\"status\":\"departed\",\"power_mw\":0,\"departed_at\":\"" + Iso(DateTime.UtcNow) + "\"}"));
    }

    IEnumerator PushLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(Mathf.Max(0.25f, pushInterval));
            if (pending.Count == 0) continue;

            var batch = new List<KeyValuePair<string, PendingUpdate>>(pending);
            pending.Clear();

            foreach (var kv in batch)
                yield return Patch(kv.Key, BuildPatchBody(kv.Value));
        }
    }

    static string BuildPatchBody(PendingUpdate u)
    {
        var sb = new StringBuilder("{");
        sb.Append("\"battery_pct\":").Append(Num(u.batteryPct));
        sb.Append(",\"power_mw\":").Append(Num(u.powerMW));
        if (!string.IsNullOrEmpty(u.status)) sb.Append(",\"status\":\"").Append(u.status).Append('"');
        if (!string.IsNullOrEmpty(u.zone))   sb.Append(",\"zone\":\"").Append(u.zone).Append('"');
        sb.Append('}');
        return sb.ToString();
    }

    IEnumerator Patch(string vehicleId, string json)
    {
        string url = $"{RestBase}/vehicles?id=eq.{UnityWebRequest.EscapeURL(vehicleId)}";
        using var req = new UnityWebRequest(url, "PATCH")
        {
            uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)),
            downloadHandler = new DownloadHandlerBuffer(),
        };
        ApplyHeaders(req);
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Prefer", "return=minimal");

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            lastError = $"PATCH 실패 ({vehicleId}): {req.error}";
            Debug.LogWarning($"[SupabaseBridge] {lastError}");
        }
    }

    void ApplyHeaders(UnityWebRequest req)
    {
        req.SetRequestHeader("apikey", anonKey);
        req.SetRequestHeader("Authorization", "Bearer " + anonKey);
        req.timeout = 8;
    }

    // ── JSON ─────────────────────────────────────────────────────

    /// <summary>
    /// 앱과 주고받는 차량 레코드. 필드명을 DB 컬럼명과 맞춰 JsonUtility 로 바로 매핑한다.
    /// </summary>
    [Serializable]
    public class VehicleRecord
    {
        public string id;
        public string car_number;
        public string vehicle_id;        // 'A' | 'B' | 'C' | 'D'
        public float  max_capacity_mws;  // 최대 용량 (mW·s = mJ)
        public float  battery_pct;
        public float  power_mw;
        public string zone;              // 'A1' ~ 'C6' 또는 null
        public string status;            // waiting | charging | done | departed
        public string entry_at;          // ISO 8601
        public string exit_at;
        public string departed_at;

        /// <summary>현재 저장 에너지 (mW·s)</summary>
        public float CurrentEnergy => max_capacity_mws * battery_pct / 100f;

        /// <summary>만충까지 남은 에너지 (mW·s)</summary>
        public float NeededEnergy => Mathf.Max(0f, max_capacity_mws - CurrentEnergy);

        public DateTime EntryTime => ParseIso(entry_at);
        public DateTime ExitTime  => ParseIso(exit_at);

        /// <summary>아두이노에 그대로 보낼 수 있는 구역 명령 (예: "a3"). 구역이 없으면 빈 문자열.</summary>
        public string ZoneCommand => string.IsNullOrEmpty(zone) ? "" : zone.ToLowerInvariant();

        static DateTime ParseIso(string s)
        {
            return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                     DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                     out var d)
                ? d
                : DateTime.MinValue;
        }
    }

    [Serializable]
    class Wrapper { public List<VehicleRecord> items; }

    /// <summary>JsonUtility 는 최상위 배열을 못 읽어서 객체로 한 번 감싼다.</summary>
    static List<VehicleRecord> ParseArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<VehicleRecord>();
        var wrapped = "{\"items\":" + json + "}";
        return JsonUtility.FromJson<Wrapper>(wrapped)?.items ?? new List<VehicleRecord>();
    }

    static string Num(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string Iso(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
