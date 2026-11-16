using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Music and sound, synthesised in code at startup: no audio files, so nothing to license and nothing
// added to the download. A calm pad with soft bells throughout, water lapping on the map (louder as
// the flood grows), and short sounds for taps, choosing a hamlet, a new radar pass, the fly-in, a
// village flooding and a sent message. OureaSoundButton turns it all off; browsers start audio only
// after the first click or tap.
public class OureaAudio : MonoBehaviour
{
    const int SR = 22050;
    const float MusicVol = 0.30f, WaterMax = 0.32f;
    static OureaAudio I;

    AudioSource music, water, sfx;
    AudioClip click, chime, pass, whoosh, swell, success;
    OureaViewer viewer; GlobeJourney journey; FloodLab lab;
    int lastPass = -1; string lastSel = ""; GlobeJourney.Mode lastMode; bool lastObserved; DataLoader.SubState lastSub;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (I != null) return;
        var go = new GameObject("Ourea Audio");
        go.AddComponent<OureaAudio>();
        go.AddComponent<OureaSoundButton>();
    }

    public static void Success() { if (I != null) I.Play(I.success, 0.5f); }

    void Awake()
    {
        I = this;
        OureaSoundButton.Load();
        if (FindAnyObjectByType<AudioListener>() == null && Camera.main != null) Camera.main.gameObject.AddComponent<AudioListener>();
        music = Source(true); water = Source(true); sfx = Source(false);
        click = Mono("click", 0.05f, Click);
        chime = Mono("chime", 1.0f, t => Notes(t, new[] { 880f, 1318.5f }, 0.09f, 0.7f));
        success = Mono("success", 1.3f, t => Notes(t, new[] { 587.3f, 740f, 880f }, 0.11f, 0.9f));
        pass = Mono("pass", 2.2f, t => Bell(t, 293.7f, 1.6f));
        whoosh = Noisy("whoosh", 3.6f, Whoosh);
        swell = Noisy("swell", 2.6f, Swell);
        StartCoroutine(MakeLoops());
    }

    AudioSource Source(bool loop)
    {
        var s = gameObject.AddComponent<AudioSource>();
        s.loop = loop; s.playOnAwake = false; s.spatialBlend = 0f; s.volume = 0f;
        return s;
    }

    void Play(AudioClip c, float v) { if (c != null) sfx.PlayOneShot(c, v); }

    void Update()
    {
        if (viewer == null) viewer = FindAnyObjectByType<OureaViewer>();
        if (journey == null) journey = FindAnyObjectByType<GlobeJourney>();
        if (lab == null) lab = FindAnyObjectByType<FloodLab>();

        var mode = journey != null ? journey.mode : GlobeJourney.Mode.Site;
        if (mode != lastMode && mode == GlobeJourney.Mode.Flying) Play(whoosh, 0.55f);
        lastMode = mode;

        // water under the music on the map, louder as the flood spreads and while a village floods
        float waterV = 0f;
        if (mode == GlobeJourney.Mode.Site && viewer != null && viewer.passes.Count > 1)
            waterV = Mathf.Lerp(0.35f, 1f, viewer.currentPass / (float)(viewer.passes.Count - 1)) * WaterMax;
        bool observed = lab != null && lab.Observed;
        if (observed) waterV = WaterMax * 1.3f;
        water.volume = Mathf.MoveTowards(water.volume, waterV, Time.deltaTime * 0.25f);
        music.volume = Mathf.MoveTowards(music.volume, mode == GlobeJourney.Mode.Flying ? MusicVol * 0.6f : MusicVol, Time.deltaTime * 0.15f);

        if (observed && !lastObserved) Play(swell, 0.6f);
        lastObserved = observed;

        if (viewer != null && mode == GlobeJourney.Mode.Site)
        {
            if (lastPass >= 0 && viewer.currentPass != lastPass) Play(pass, 0.45f);
            string sel = viewer.SelectedPlaceId ?? "";
            if (sel != lastSel && sel.Length > 0) Play(chime, 0.4f);
            lastSel = sel;
        }
        if (viewer != null) lastPass = viewer.currentPass;

        if (lab != null && lab.live != null)
        {
            if (lab.live.subState == DataLoader.SubState.Done && lastSub != DataLoader.SubState.Done) Success();
            lastSub = lab.live.subState;
        }
    }

    // a tap that a button or panel took: drawn last, so it sees the click only after the UI used it
    void OnGUI()
    {
        GUI.depth = 100;
        var e = Event.current;
        if (e.rawType == EventType.MouseDown && e.type == EventType.Used) Play(click, 0.35f);
    }

    // ---------- loops: built over many frames so the page never stalls ----------
    IEnumerator MakeLoops()
    {
        yield return null;
        // music: four chords of 8 s with soft bells over them, a 32 s loop
        const float L = 32f;
        int n = (int)(L * SR);
        var buf = new float[n * 2];
        float[][] chords = {
            new[] { 146.83f, 220f, 293.66f, 369.99f, 659.26f },   // D (add 9)
            new[] { 123.47f, 185f, 293.66f, 440f, 554.37f },      // Bm7
            new[] { 98f, 146.83f, 246.94f, 369.99f, 587.33f },    // Gmaj7
            new[] { 110f, 164.81f, 220f, 329.63f, 493.88f } };    // A (sus)
        var rng = new System.Random(7);
        float[] scale = { 587.33f, 659.26f, 739.99f, 880f, 987.77f, 1174.66f };   // D major pentatonic
        var bells = new List<Vector3>();   // time, frequency, pan
        for (float h = 1.1f; h < L; h += 1.3f + (float)rng.NextDouble() * 1.6f)
            bells.Add(new Vector3(h, scale[rng.Next(scale.Length)], (float)rng.NextDouble() * 1.4f - 0.7f));
        int chunk = SR / 3;
        for (int i0 = 0; i0 < n; i0 += chunk)
        {
            int i1 = Mathf.Min(n, i0 + chunk);
            for (int i = i0; i < i1; i++)
            {
                double t = i / (double)SR;
                float l = 0f, r = 0f;
                for (int c = 0; c < 4; c++)
                {
                    float lt = Mathf.Repeat((float)t - c * 8f, L);
                    if (lt >= 11f) continue;   // each chord swells in over 3.5 s and overlaps the next
                    float env = Mathf.Clamp01(lt / 3.5f) * Mathf.Clamp01((11f - lt) / 3.5f);
                    env = env * env * (3f - 2f * env);
                    var ch = chords[c];
                    for (int v = 0; v < ch.Length; v++)
                    {
                        float g = (v == ch.Length - 1 ? 0.3f : 1f) * env;
                        // a slightly detuned pair spread left/right: a slow, wide chorus; frequencies
                        // rounded to whole cycles per loop so the loop has no seam
                        float a = Osc(Q(ch[v] * 1.0015, L), t), b = Osc(Q(ch[v] * 0.9985, L), t);
                        l += g * (a * 0.7f + b * 0.3f); r += g * (a * 0.3f + b * 0.7f);
                    }
                }
                l *= 0.05f; r *= 0.05f;
                foreach (var h in bells)
                {
                    float lt = Mathf.Repeat((float)t - h.x, L);
                    if (lt > 3.2f) continue;
                    float s = Bell(lt, h.y, 2.4f) * 0.11f;
                    l += s * (1f - h.z) * 0.5f; r += s * (1f + h.z) * 0.5f;
                }
                buf[2 * i] = l; buf[2 * i + 1] = r;
            }
            yield return null;
        }
        Echo(buf, n, 0.36f, 0.48f, 0.38f);
        Normalize(buf, 0.75f);
        var m = AudioClip.Create("ourea-music", n, 2, SR, false);
        m.SetData(buf, 0);
        music.clip = m; music.Play();
        yield return null;

        // water: brown noise that rises and falls, with small drops, a 12 s loop
        const float WL = 12f, X = 2f;   // render 2 s extra and fold it over the start: no seam
        int wn = (int)(WL * SR), wx = (int)((WL + X) * SR);
        var w = new float[wx * 2];
        var nr = new System.Random(3);
        float bl = 0f, br = 0f, ll = 0f, lr = 0f;
        float nextDrop = 0.4f; float dropT = -1f, dropF = 700f, dropP = 0f;
        for (int i = 0; i < wx; i++)
        {
            float t = i / (float)SR;
            bl = bl * 0.985f + ((float)nr.NextDouble() * 2f - 1f) * 0.015f;
            br = br * 0.985f + ((float)nr.NextDouble() * 2f - 1f) * 0.015f;
            ll += (bl - ll) * 0.18f; lr += (br - lr) * 0.18f;
            float am = 0.55f + 0.45f * Mathf.Sin(2f * Mathf.PI * t / 6f) * Mathf.Sin(2f * Mathf.PI * t / 4f + 1f);
            float dl = 0f;
            if (t >= nextDrop) { dropT = t; dropF = 500f + (float)nr.NextDouble() * 500f; dropP = (float)nr.NextDouble() * 2f - 1f; nextDrop = t + 0.3f + (float)nr.NextDouble() * 1.1f; }
            if (dropT >= 0f && t - dropT < 0.12f)
            {
                float dt = t - dropT;
                dl = Mathf.Sin(2f * Mathf.PI * dropF * dt * (1f - dt * 3f)) * Mathf.Exp(-dt * 38f) * 0.12f;
            }
            w[2 * i] = ll * 5f * am + dl * (1f - dropP) * 0.5f;
            w[2 * i + 1] = lr * 5f * am + dl * (1f + dropP) * 0.5f;
            if ((i & 16383) == 0) yield return null;
        }
        int xs = (int)(X * SR);
        for (int i = 0; i < xs; i++)
        {
            float k = i / (float)xs;
            for (int ch = 0; ch < 2; ch++) w[2 * i + ch] = w[2 * i + ch] * k + w[2 * (wn + i) + ch] * (1f - k);
        }
        Normalize(w, 0.6f, wn);
        var wc = AudioClip.Create("ourea-water", wn, 2, SR, false);
        var wdata = new float[wn * 2]; Array.Copy(w, wdata, wn * 2);
        wc.SetData(wdata, 0);
        water.clip = wc; water.Play();
    }

    // ---------- small instruments ----------
    static float Q(double f, float loop) { return (float)(Math.Round(f * loop) / loop); }
    static float Osc(float f, double t)
    {
        double p = 2.0 * Math.PI * f * t;
        return (float)(Math.Sin(p) + 0.22 * Math.Sin(2.0 * p) + 0.06 * Math.Sin(3.0 * p));
    }
    static float Bell(float t, float f, float dur)
    {
        float atk = Mathf.Clamp01(t * 300f);
        float k = 2f * Mathf.PI * f * t;
        return atk * (Mathf.Sin(k) * Mathf.Exp(-t * 4f / dur)
                    + 0.35f * Mathf.Sin(2.76f * k) * Mathf.Exp(-t * 9f / dur)
                    + 0.15f * Mathf.Sin(5.4f * k) * Mathf.Exp(-t * 16f / dur));
    }
    static float Notes(float t, float[] fs, float gap, float dur)
    {
        float s = 0f;
        for (int i = 0; i < fs.Length; i++) { float lt = t - i * gap; if (lt >= 0f) s += Bell(lt, fs[i], dur) * 0.6f; }
        return s;
    }
    static float Click(float t)
    {
        return Mathf.Sin(2f * Mathf.PI * 1900f * t) * Mathf.Exp(-t * 160f) * 0.6f + Mathf.Sin(2f * Mathf.PI * 950f * t) * Mathf.Exp(-t * 90f) * 0.3f;
    }
    // filtered noise sweeping up and back down, panned across: the fly-in
    static float Whoosh(float t, float dur, ref float a, ref float b, System.Random rn)
    {
        float u = t / dur, sweep = Mathf.Sin(Mathf.PI * u);
        float x = (float)rn.NextDouble() * 2f - 1f;
        float c1 = 0.01f + 0.25f * sweep * sweep, c2 = c1 * 0.25f;
        a += (x - a) * c1; b += (a - b) * c2;
        return (a - b) * Mathf.Pow(sweep, 1.5f) * 2.2f;
    }
    // a low rumble with a rising water rush: a village flooding
    static float Swell(float t, float dur, ref float a, ref float b, System.Random rn)
    {
        float u = t / dur, env = Mathf.Sin(Mathf.PI * Mathf.Min(1f, u * 1.3f)) * (1f - u * 0.3f);
        float x = (float)rn.NextDouble() * 2f - 1f;
        a = a * 0.99f + x * 0.01f; b += (x - b) * (0.03f + 0.12f * u);
        return (a * 6f + b * 0.5f + Mathf.Sin(2f * Mathf.PI * 55f * t) * 0.25f) * env;
    }

    delegate float NoiseFn(float t, float dur, ref float a, ref float b, System.Random rn);
    static AudioClip Mono(string name, float dur, Func<float, float> f)
    {
        int n = (int)(dur * SR); var d = new float[n];
        for (int i = 0; i < n; i++) d[i] = f(i / (float)SR);
        Normalize(d, 0.8f);
        var c = AudioClip.Create(name, n, 1, SR, false); c.SetData(d, 0); return c;
    }
    static AudioClip Noisy(string name, float dur, NoiseFn f)
    {
        int n = (int)(dur * SR); var d = new float[n]; float a = 0f, b = 0f; var rn = new System.Random(name.Length * 31);
        for (int i = 0; i < n; i++) d[i] = f(i / (float)SR, dur, ref a, ref b, rn);
        Normalize(d, 0.8f);
        var c = AudioClip.Create(name, n, 1, SR, false); c.SetData(d, 0); return c;
    }
    // a few echoes, different left and right, wrapping round the loop
    static void Echo(float[] s, int n, float dl, float dr, float g)
    {
        var o = (float[])s.Clone();
        int[] d = { (int)(dl * SR), (int)(dr * SR) };
        for (int ch = 0; ch < 2; ch++)
            for (int i = 0; i < n; i++)
            {
                float acc = 0f, k = g;
                for (int tap = 1; tap <= 4; tap++, k *= g) acc += s[2 * ((i - tap * d[ch] % n + n) % n) + ch] * k;
                o[2 * i + ch] += acc * 0.55f;
            }
        Array.Copy(o, s, s.Length);
    }
    static void Normalize(float[] s, float peak, int frames = -1)
    {
        int len = frames < 0 ? s.Length : frames * 2;
        float m = 1e-6f;
        for (int i = 0; i < len; i++) m = Mathf.Max(m, Mathf.Abs(s[i]));
        float k = peak / m;
        for (int i = 0; i < s.Length; i++) s[i] *= k;
    }
}
