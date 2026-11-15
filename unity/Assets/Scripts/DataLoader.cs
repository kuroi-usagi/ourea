using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

// Live commune ranking from the team's Cloudflare API (Asher): https://ourea-api.team-ourea.workers.dev
// GET /api/passes, then GET /api/top?pass=latest&n=10. Until the first NISAR pass is imported the
// API has no ranking (404); the app then keeps the hamlet scores baked in from handoff/findings and
// checks again every minute. Wording rules from the API doc: pass date + PROVISIONAL next to any
// score, never "safe", "childcare gap not yet known" while the gap factor is off.
public class DataLoader : MonoBehaviour
{
    public string baseUrl = "https://ourea-api.team-ourea.workers.dev";
    public float retrySeconds = 60f;

    [Serializable] public class Commune
    {
        public int rank;
        public string code, name, name_en, province, status, reasons, confidence, gap_status;
        public float lon, lat, score, gap_factor;
        public int under5;
    }
    [Serializable] class TopResponse { public string as_of; public string pass; public string source; public Commune[] communes; }
    [Serializable] class Pass { public string id; public string date; public string status; }
    [Serializable] class PassList { public Pass[] items; }

    public enum State { Loading, Waiting, Live, Offline }
    public State state = State.Loading;
    public string asOf = "", source = "";
    public Commune[] top = new Commune[0];
    public string message = "Connecting to the Ourea API…";

    void Start() { StartCoroutine(Loop()); }

    IEnumerator Loop()
    {
        while (true)
        {
            yield return Fetch();
            if (state == State.Live) yield break;
            yield return new WaitForSeconds(retrySeconds);
        }
    }

    IEnumerator Fetch()
    {
        using (var req = UnityWebRequest.Get(baseUrl + "/api/passes"))
        {
            req.timeout = 15;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) { Offline(); yield break; }
            var passes = JsonUtility.FromJson<PassList>("{\"items\":" + req.downloadHandler.text + "}");
            if (passes == null || passes.items == null || passes.items.Length == 0)
            {
                state = State.Waiting;
                message = "Ourea API is online. No NISAR pass imported yet, so this shows the hamlet scores built into the app.";
                yield break;
            }
        }
        using (var req = UnityWebRequest.Get(baseUrl + "/api/top?pass=latest&n=10"))
        {
            req.timeout = 15;
            yield return req.SendWebRequest();
            if (req.responseCode == 404) { state = State.Waiting; message = "Ourea API is online. Scores appear once a pass is imported."; yield break; }
            if (req.result != UnityWebRequest.Result.Success) { Offline(); yield break; }
            var r = JsonUtility.FromJson<TopResponse>(req.downloadHandler.text);
            top = r != null && r.communes != null ? r.communes : new Commune[0];
            asOf = r != null ? r.as_of : "";
            source = r != null ? r.source : "";
            state = State.Live;
            message = "Live from the Ourea API · " + asOf + " pass · PROVISIONAL";
        }
    }

    void Offline()
    {
        state = State.Offline;
        message = "Can't reach the Ourea API right now. Showing the hamlet scores built into the app.";
    }

    // Commune centre in world units, for flying the camera to it (tile on the UTM 48N handoff grid).
    public static Vector3 ToWorld(double lon, double lat, double[] boundsM, float sizeX, float sizeZ)
    {
        double e, n;
        LatLonToUtm48(lat, lon, out e, out n);
        float u = (float)((e - boundsM[0]) / (boundsM[2] - boundsM[0]));
        float v = (float)((n - boundsM[1]) / (boundsM[3] - boundsM[1]));
        return new Vector3(u * sizeX, 0f, v * sizeZ);
    }

    // WGS84 → UTM zone 48N (Snyder's forward transverse Mercator)
    static void LatLonToUtm48(double latDeg, double lonDeg, out double easting, out double northing)
    {
        const double a = 6378137.0, f = 1 / 298.257223563, k0 = 0.9996;
        double e2 = f * (2 - f), ep2 = e2 / (1 - e2);
        double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180, lon0 = 105.0 * Math.PI / 180;
        double N = a / Math.Sqrt(1 - e2 * Math.Sin(lat) * Math.Sin(lat));
        double T = Math.Tan(lat) * Math.Tan(lat), C = ep2 * Math.Cos(lat) * Math.Cos(lat), A = Math.Cos(lat) * (lon - lon0);
        double M = a * ((1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256) * lat
                 - (3 * e2 / 8 + 3 * e2 * e2 / 32 + 45 * e2 * e2 * e2 / 1024) * Math.Sin(2 * lat)
                 + (15 * e2 * e2 / 256 + 45 * e2 * e2 * e2 / 1024) * Math.Sin(4 * lat)
                 - (35 * e2 * e2 * e2 / 3072) * Math.Sin(6 * lat));
        easting = 500000 + k0 * N * (A + (1 - T + C) * A * A * A / 6 + (5 - 18 * T + T * T + 72 * C - 58 * ep2) * Math.Pow(A, 5) / 120);
        northing = k0 * (M + N * Math.Tan(lat) * (A * A / 2 + (5 - T + 9 * C + 4 * C * C) * Math.Pow(A, 4) / 24
                 + (61 - 58 * T + T * T + 600 * C - 330 * ep2) * Math.Pow(A, 6) / 720));
    }
}
