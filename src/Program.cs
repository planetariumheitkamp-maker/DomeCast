using DomeCast.Capture;
using DomeCast.Config;
using DomeCast.Output;
using DomeCast.Render;

// ---------- DomeCast — out-of-process dome capture & warp (Option B prototype) ----------
// Usage:
//   DomeCast                    pick a window, warp it to the projector display
//   DomeCast --pattern          calibration test pattern only (no capture needed)
//   DomeCast --bench            pattern mode + GPU timing of the warp pass
//   DomeCast --monitor N        use monitor N (default: first non-primary)
//   DomeCast --config path.json (default: domecast.json next to the exe)

bool pattern = args.Contains("--pattern");
bool bench = args.Contains("--bench");
int monitor = ArgInt(args, "--monitor", 0);
string configPath = ArgStr(args, "--config",
    Path.Combine(AppContext.BaseDirectory, "domecast.json"));

var p = DomeParams.Load(configPath);
p.TestPattern |= pattern;

Console.WriteLine($"DomeCast — {Win32.MonitorCount} monitor(s) detected");
Console.WriteLine("Keys: 1-9 select param | Left/Right adjust (Shift=fine) | T pattern | G grid | S save | Esc quit");
Console.WriteLine();

IntPtr captureHwnd = IntPtr.Zero;
if (!pattern)
{
    var windows = WindowEnumerator.GetWindows();
    if (windows.Count == 0) { Console.WriteLine("No capturable windows found."); return; }
    for (int i = 0; i < windows.Count; i++)
        Console.WriteLine($"  [{i}] {windows[i].ProcessName,-20} {windows[i].Title}");
    Console.Write("Capture which window? ");
    if (!int.TryParse(Console.ReadLine(), out int pick) || pick < 0 || pick >= windows.Count)
    { Console.WriteLine("Invalid selection."); return; }
    captureHwnd = windows[pick].Hwnd;
    Console.WriteLine($"Capturing: {windows[pick].Title}");
}

using var d3d = new D3DContext();
using var capture = pattern ? null : new CaptureService(d3d.Device);
capture?.Start(captureHwnd);

string shaderPath = Path.Combine(AppContext.BaseDirectory, "Render", "Shaders", "DomeWarp.hlsl");
using var renderer = new WarpRenderer(d3d, shaderPath, bench);
using var output = new PreviewOutput(d3d.Device, monitor);
var input = new CalibrationInput(p, configPath);

double acc = 0; int accN = 0; long lastStat = Environment.TickCount64;
while (Win32.PumpMessages() && !input.QuitRequested)
{
    input.Poll();

    if (capture is not null)
    {
        // Hold the lock across render so the copy thread can't swap the staging
        // texture mid-draw. Copies are ~sub-ms, so contention is negligible.
        lock (capture.SyncLock)
        {
            if (capture.StagingView is null) { Thread.Sleep(1); continue; } // no frame yet
            float aspect = (float)capture.FrameWidth / Math.Max(1, capture.FrameHeight);
            var (view, w, h) = output.BeginFrame();
            renderer.Render(view, w, h, p, aspect, capture.StagingView);
            output.EndFrame();
        }
    }
    else
    {
        var (view, w, h) = output.BeginFrame();
        renderer.Render(view, w, h, p, 16f / 9f, null);
        output.EndFrame();
    }

    if (bench && renderer.LastGpuMs() is double ms) { acc += ms; accN++; }

    if (Environment.TickCount64 - lastStat > 5000)
    {
        string stats = capture is null
            ? "[stat] pattern mode"
            : $"[stat] frames arrived {capture.FramesArrived}, copied {capture.FramesCopied}";
        if (bench && accN > 0) stats += $", warp GPU avg {acc / accN:F2} ms";
        Console.WriteLine(stats);
        acc = 0; accN = 0; lastStat = Environment.TickCount64;
    }
}

p.Save(configPath);

static int ArgInt(string[] a, string name, int def)
{
    int i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out int v) ? v : def;
}
static string ArgStr(string[] a, string name, string def)
{
    int i = Array.IndexOf(a, name);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
}
