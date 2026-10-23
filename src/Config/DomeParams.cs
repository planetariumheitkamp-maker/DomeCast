using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DomeCast.Config;

/// <summary>
/// All user-calibratable dome parameters. Serialized to domecast.json.
/// </summary>
public sealed class DomeParams
{
    // --- Dome / projector geometry ---
    /// <summary>Fisheye lens full field of view, degrees. 180 for a standard dome lens.</summary>
    public float ProjFovDeg { get; set; } = 180f;

    /// <summary>Scale of the fisheye circle relative to the inscribed square. &gt;1 pushes the edge off-screen (crop), &lt;1 shrinks.</summary>
    public float Overscan { get; set; } = 1.0f;

    /// <summary>Dome axis tilt, X (pitch: + leans the game's forward direction down toward the audience), degrees.</summary>
    public float DomeTiltXDeg { get; set; } = 15f;

    /// <summary>Dome axis yaw around vertical, degrees.</summary>
    public float DomeTiltYDeg { get; set; } = 0f;

    // --- Game camera ---
    /// <summary>Horizontal FOV the game is rendering with — MUST match the in-game setting.</summary>
    public float CamHfovDeg { get; set; } = 90f;

    // --- Off-axis projector correction (spec §4.2) ---
    /// <summary>Enable ray-traced correction for a projector not sitting at dome center.</summary>
    public bool OffAxisEnable { get; set; } = false;

    /// <summary>Projector position in dome radii, dome coords (x right, y up, z along dome axis).</summary>
    public Vector3 ProjOffset { get; set; } = new(0f, -0.45f, -0.25f);

    /// <summary>Projector pan (yaw), degrees. 0 = fisheye axis aligned with dome axis.</summary>
    public float ProjPanDeg { get; set; } = 0f;

    /// <summary>Projector tilt (pitch), degrees.</summary>
    public float ProjTiltDeg { get; set; } = 0f;

    // --- Debug / calibration aids ---
    public bool TestPattern { get; set; } = false;
    public bool GridOverlay { get; set; } = false;

    // ---- computed matrices (not serialized) ----
    [JsonIgnore] public Matrix4x4 ProjOrient { get; private set; }
    [JsonIgnore] public Matrix4x4 DomeTilt { get; private set; }

    public void Recalculate()
    {
        static Matrix4x4 Rx(float deg) => Matrix4x4.CreateRotationX(deg * MathF.PI / 180f);
        static Matrix4x4 Ry(float deg) => Matrix4x4.CreateRotationY(deg * MathF.PI / 180f);
        ProjOrient = Ry(ProjPanDeg) * Rx(ProjTiltDeg);
        DomeTilt = Rx(DomeTiltXDeg) * Ry(DomeTiltYDeg);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static DomeParams Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var p = JsonSerializer.Deserialize<DomeParams>(File.ReadAllText(path));
                if (p is not null) { p.Recalculate(); return p; }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[config] failed to load {path}: {ex.Message} — using defaults");
        }
        var d = new DomeParams();
        d.Recalculate();
        return d;
    }

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        Console.WriteLine($"[config] saved to {path}");
    }
}
