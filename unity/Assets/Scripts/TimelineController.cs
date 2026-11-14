using System;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// Owns "which NISAR pass are we looking at". Position is continuous (0 .. passCount-1)
// so the water shader can blend between neighbouring passes while scrubbing or playing.
// Keys: [ / ] step (arrows belong to OrbitCamera), Space play/pause, Home/End first/last.
public class TimelineController : MonoBehaviour
{
    public int passCount;
    public float secondsPerPass = 2.5f;
    public bool loop = true;

    public float Position { get; private set; }
    public bool Playing { get; private set; }
    public int Index { get { return Mathf.Clamp(Mathf.RoundToInt(Position), 0, Mathf.Max(0, passCount - 1)); } }

    // (lower pass, upper pass, blend 0..1 between them)
    public event Action<int, int, float> Blended;
    public event Action<int> IndexChanged;

    int lastIndex = -1;

    public void SetPosition(float p)
    {
        float max = Mathf.Max(0, passCount - 1);
        Position = Mathf.Clamp(p, 0f, max);
        int a = Mathf.FloorToInt(Position), b = Mathf.Min(a + 1, (int)max);
        if (Blended != null) Blended(a, b, Position - a);
        if (Index != lastIndex)
        {
            lastIndex = Index;
            if (IndexChanged != null) IndexChanged(lastIndex);
        }
    }

    public void Step(int d)
    {
        Playing = false;
        SetPosition(Index + d);
    }

    public void TogglePlay()
    {
        if (passCount < 2) return;
        if (!Playing && Position >= passCount - 1) SetPosition(0);
        Playing = !Playing;
    }

    void Update()
    {
        ReadKeys();
        if (!Playing || passCount < 2) return;
        float p = Position + Time.deltaTime / Mathf.Max(0.1f, secondsPerPass);
        if (p >= passCount - 1)
        {
            if (loop) p = 0f;
            else { p = passCount - 1; Playing = false; }
        }
        SetPosition(p);
    }

    void ReadKeys()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        if (k == null) return;
        if (k.leftBracketKey.wasPressedThisFrame) Step(-1);
        if (k.rightBracketKey.wasPressedThisFrame) Step(1);
        if (k.spaceKey.wasPressedThisFrame) TogglePlay();
        if (k.homeKey.wasPressedThisFrame) { Playing = false; SetPosition(0); }
        if (k.endKey.wasPressedThisFrame) { Playing = false; SetPosition(passCount - 1); }
#else
        if (Input.GetKeyDown(KeyCode.LeftBracket)) Step(-1);
        if (Input.GetKeyDown(KeyCode.RightBracket)) Step(1);
        if (Input.GetKeyDown(KeyCode.Space)) TogglePlay();
        if (Input.GetKeyDown(KeyCode.Home)) { Playing = false; SetPosition(0); }
        if (Input.GetKeyDown(KeyCode.End)) { Playing = false; SetPosition(passCount - 1); }
#endif
    }
}
