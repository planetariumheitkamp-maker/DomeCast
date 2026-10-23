using System.Runtime.InteropServices;

namespace DomeCast.Config;

/// <summary>
/// Live keyboard calibration. Polls global key state each render tick and nudges the
/// selected parameter. Focus must be on the preview window (or console).
///
///   1..9  select parameter        Left/Right  adjust (Shift = fine step)
///   T     test pattern            G           grid overlay
///   S     save domecast.json      Esc         quit
/// </summary>
internal sealed class CalibrationInput
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private const int VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_SHIFT = 0x10;
    private const int VK_T = 0x54, VK_G = 0x47, VK_S = 0x53;

    private readonly DomeParams _p;
    private readonly string _configPath;
    private readonly bool[] _wasDown = new bool[256];
    private int _slot;

    public bool QuitRequested { get; private set; }

    private readonly (string Name, Func<float> Get, Action<float> Set, float Step)[] _slots;

    public CalibrationInput(DomeParams p, string configPath)
    {
        _p = p; _configPath = configPath;
        _slots = new (string, Func<float>, Action<float>, float)[]
        {
            ("Overscan",     () => p.Overscan,      v => p.Overscan = Math.Clamp(v, 0.5f, 1.5f),      0.005f),
            ("DomeTiltX",    () => p.DomeTiltXDeg,  v => p.DomeTiltXDeg = v,                          0.5f),
            ("DomeTiltY",    () => p.DomeTiltYDeg,  v => p.DomeTiltYDeg = v,                          0.5f),
            ("CamHfov",      () => p.CamHfovDeg,    v => p.CamHfovDeg = Math.Clamp(v, 40f, 170f),     0.5f),
            ("ProjOffY",     () => p.ProjOffset.Y,  v => p.ProjOffset = p.ProjOffset with { },        0.01f), // set below
            ("ProjOffZ",     () => p.ProjOffset.Z,  v => p.ProjOffset = p.ProjOffset with { },        0.01f),
            ("ProjTilt",     () => p.ProjTiltDeg,   v => p.ProjTiltDeg = v,                           0.5f),
            ("ProjPan",      () => p.ProjPanDeg,    v => p.ProjPanDeg = v,                            0.5f),
            ("ProjFov",      () => p.ProjFovDeg,    v => p.ProjFovDeg = Math.Clamp(v, 120f, 200f),    1.0f),
        };
        // Vector3 offsets need explicit setters (records don't compose here)
        _slots[4] = ("ProjOffY", () => p.ProjOffset.Y,
            v => p.ProjOffset = new System.Numerics.Vector3(p.ProjOffset.X, v, p.ProjOffset.Z), 0.01f);
        _slots[5] = ("ProjOffZ", () => p.ProjOffset.Z,
            v => p.ProjOffset = new System.Numerics.Vector3(p.ProjOffset.X, p.ProjOffset.Y, v), 0.01f);
    }

    public void Poll()
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            if (Pressed(0x31 + i)) // '1'..'9'
            {
                _slot = i;
                Console.WriteLine($"[cal] selected {_slots[i].Name} = {_slots[i].Get():F3}");
            }
        }

        int dir = (Held(VK_RIGHT) ? 1 : 0) - (Held(VK_LEFT) ? 1 : 0);
        if (dir != 0)
        {
            float step = _slots[_slot].Step * (Held(VK_SHIFT) ? 0.1f : 1f);
            _slots[_slot].Set(_slots[_slot].Get() + dir * step);
            _p.Recalculate();
            Console.Write($"\r[cal] {_slots[_slot].Name} = {_slots[_slot].Get():F3}   ");
        }

        if (Pressed(VK_T)) { _p.TestPattern = !_p.TestPattern; Console.WriteLine($"\n[cal] test pattern: {_p.TestPattern}"); }
        if (Pressed(VK_G)) { _p.GridOverlay = !_p.GridOverlay; Console.WriteLine($"\n[cal] grid overlay: {_p.GridOverlay}"); }
        if (Pressed(VK_S)) _p.Save(_configPath);
        if (Pressed(VK_ESCAPE)) QuitRequested = true;
    }

    private bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private bool Pressed(int vk)
    {
        bool down = Held(vk);
        bool edge = down && !_wasDown[vk];
        _wasDown[vk] = down;
        return edge;
    }
}
