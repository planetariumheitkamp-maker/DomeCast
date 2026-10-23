# DomeCast

Play any PC game on a dome without the image looking stretched — **out-of-process** capture,
so nothing is ever injected into the game. Works on any DirectX 11/12 or Vulkan title,
including games with anti-cheat (EAC / BattlEye / Vanguard), because DomeCast reads frames
the same way OBS does: from the compositor, outside the process.

This is the **Option B** prototype from *Universal Dome Mod — Feasibility and Roadmap*;
see `docs/Implementation_Spec.md` for the full design.

## Requirements

- Windows 10 1903+ (Windows 11 recommended), .NET 8 runtime or SDK
- Single projector with a fisheye lens on a dome (multi-projector blending is out of scope)
- Game running in **borderless windowed** mode (exclusive fullscreen cannot be captured
  reliably by Windows.Graphics.Capture)

## Build

```powershell
dotnet build -c Release src/DomeCast.csproj
dotnet publish -c Release -r win-x64 --self-contained src/DomeCast.csproj
```

## Run

```powershell
DomeCast                # pick a window from the list, warp it to the projector display
DomeCast --pattern      # calibration test pattern only (no game needed)
DomeCast --bench        # test pattern + prints GPU cost of the warp pass
DomeCast --monitor 1    # force a specific display (default: first non-primary = projector)
```

The preview window opens borderless-fullscreen on the projector display. The console stays
interactive for calibration.

## Calibration walkthrough

Do this once per dome setup, **with `--pattern`** (no game required):

1. **Circle fit.** The pattern shows a fisheye circle. Press `1` (Overscan) and use
   Left/Right until the circle edge just touches the dome edge. If the circle is larger than
   the projector image, your projector's fisheye image must be zoomed/focused first.
2. **Axis.** Press `9` (ProjFov) only if your lens isn't 180°. Leave at 180 otherwise.
3. **Content orientation.** Press `2` (DomeTiltX) to pitch the game's forward direction
   down toward the audience (15–30° feels good for seated domes), `3` for yaw.
4. **Off-axis correction** (only if the projector is NOT at dome center):
   set `OffAxisEnable: true` in `domecast.json`, then measure the projector position in
   dome radii (x = right, y = up, z = along dome axis; e.g. a projector on the floor
   behind center might be `(0, -0.5, -0.2)`) and tune `5`/`6` (ProjOffY/Z) plus
   `7`/`8` (ProjTilt/Pan) until the pattern rings are concentric circles on the dome
   and the spokes run straight from nadir to zenith.
5. Press `S` to save. The config is shared with capture mode — done.

Then per game: launch DomeCast without `--pattern`, pick the game window, and set `4`
(CamHfov) to the **exact** FOV configured in the game. Run games at their maximum FOV
(100–120° where possible) — DomeCast can only show what the game rendered.

### Keys

| Key | Action |
|---|---|
| `1`–`9` | Select parameter (Overscan, DomeTiltX/Y, CamHfov, ProjOffY/Z, ProjTilt/Pan, ProjFov) |
| Left / Right | Adjust selected parameter (`Shift` = 10x finer) |
| `T` | Toggle synthetic test pattern |
| `G` | Toggle grid overlay on the captured image |
| `S` | Save `domecast.json` |
| `Esc` | Quit |

## What to expect

- **Latency:** roughly 1–2 frames on top of the game (capture handoff + vsync). Fine for
  seated dome play; not a competitive-shooter setup.
- **Coverage:** the dome region corresponding to the game's frustum shows real content;
  beyond it the image feathers to black. This is the documented Option-B limit — true
  180° peripheral content needs engine injection (Option C / UEVR-style), per the report.
- **Anti-cheat:** no injection, no hooking, no driver — nothing to flag. If a title blocks
  WGC capture (rare; some Valorant builds), DomeCast shows black — no ban risk.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Black preview, frames counter stuck at 0 | Game is exclusive fullscreen → switch to borderless; or the title blocks capture |
| Black preview on a laptop | Force both the game and DomeCast to the same GPU: Windows Settings → Graphics → High performance |
| Image looks rotated/tilted | Adjust DomeTiltX/Y; check `domecast.json` |
| Edges of the dome are black | Expected beyond the game FOV; raise in-game FOV and CamHfov to match |
| Stutter | Check the `[stat]` line — arrived ≈ copied ≈ game fps is healthy; if arrived is low, the game is the bottleneck |

## Layout

```
src/
├── Program.cs                CLI, window picker, render loop
├── Capture/                  Windows.Graphics.Capture interop + latest-frame-wins pool
├── Render/                   D3D11 device, warp pipeline, Shaders/DomeWarp.hlsl
├── Config/                   DomeParams (JSON) + live keyboard calibration
└── Output/                   IFrameOutput + borderless Win32 preview (Spout = v1.1)
```

License: MIT. Free and open source by design — never charge for a tool that warps other
people's games (see the Luke Ross DMCA precedent in the report).
