# DomeCast — Implementation Specification

**Version 1.0 · Companion to** *Universal Dome Mod Feasibility and Roadmap* (Option B: out-of-process capture)

---

## 1. Purpose and scope

DomeCast is a standalone Windows utility that lets any PC game be played on a dome
(single fisheye-lens projector) without the image looking stretched. It:

1. **Captures** the game's window out-of-process using `Windows.Graphics.Capture` (WGC).
2. **Warps** the captured frame into a calibrated dome master (angular fisheye) on the GPU.
3. **Presents** the warped image to the projector display (borderless fullscreen preview),
   with an integration point for Spout texture sharing.

**Explicitly out of scope (v1):** multi-projector tiling and edge blending, true 180°
multi-view capture (that is Option C / engine injection), HDR passthrough, audio.

### Why this architecture

| Requirement | Consequence |
|---|---|
| Universal: works on any game, incl. EAC/BattlEye/Vanguard titles | Nothing may enter the game process → out-of-process capture only |
| Cost is acceptable | One GPU warp pass ≈ <0.5 ms; WGC adds ≈ 1 frame of latency |
| Single fisheye-lens projector | No edge blending; but projector is rarely at dome center → off-axis correction required |

---

## 2. System architecture

```
┌──────────┐   WGC frame pool    ┌───────────────┐   ID3D11Texture2D   ┌────────────────┐
│  Game    │ ──────────────────► │ CaptureService │ ─────────────────► │ WarpRenderer   │
│ process  │  (B8G8R8A8, GPU)    │ (free-threaded)│                    │ (D3D11, 1 pass)│
└──────────┘                     └───────────────┘                     │  dome master   │
                                                                       └──────┬─────────┘
                                                                              │
                                              ┌───────────────────────────────┼────────────┐
                                              ▼                                             ▼
                                     ┌─────────────────┐                    ┌─────────────┐
                                     │ PreviewOutput    │                    │ SpoutOutput │
                                     │ (swapchain →     │                    │ (integration│
                                     │  projector screen)│                   │  point, v1.1)│
                                     └─────────────────┘                    └─────────────┘
```

**Threading model.** Capture events arrive on a free-threaded pool; the newest frame
wins (older frames are dropped immediately — a dome display never benefits from queued
frames). A single render thread runs the warp + present loop, vsynced to the projector
refresh. Calibration input is polled on the render thread via `GetAsyncKeyState`.

**Latency budget (target ≤ 2 frames end-to-end):**

| Stage | Cost | Notes |
|---|---|---|
| Game → WGC compositor handoff | ≈ 1 frame | Inherent to out-of-process capture |
| Frame pool → render thread | < 1 ms | Latest-frame-wins, no queue |
| Warp pass (2048² dome master) | < 0.5 ms | Single pixel-shader pass, bilinear |
| Present (vsync) | 0–1 frame | Use `Present(1)` on projector display |
| **Total** | **≈ 16–34 ms @60 Hz** | Acceptable for seated dome play |

---

## 3. Module specifications

### 3.1 CaptureService (Windows.Graphics.Capture)

- **Target framework:** `net8.0-windows10.0.22621.0`, built with CsWinRT projections.
- **Interop requirements** (no official projected APIs for these):
  - `CreateDirect3D11DeviceFromDXGIDevice` (d3d11.dll) → `IDirect3DDevice` from our `IDXGIDevice`.
  - `IGraphicsCaptureItemInterop::CreateForWindow` (GUID `3628E81B-3CAC-4C60-B7F4-23CE0E0C3356`)
    → `GraphicsCaptureItem` from an `HWND`.
  - `IDirect3DDxgiInterfaceAccess::GetInterface` (GUID `A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1`)
    → `ID3D11Texture2D` from each captured `IDirect3DSurface`.
- **Frame pool:** `Direct3D11CaptureFramePool.CreateFreeThreaded(device, B8G8R8A8UIntNormalized, 2, item.Size)`.
  Two buffers suffice because stale frames are dropped.
- **Frame handling:** on `FrameArrived`, atomically swap the newest frame into a slot;
  the render thread takes-and-clears the slot each tick.
- **Resize:** on `ContentSizeChanged`, recreate the frame pool at the new size.
- **Window discovery:** `EnumWindows` + `IsWindowVisible` + `GetWindowText` +
  `GetWindowThreadProcessId` → interactive picker list at startup.
- **Known capture caveats:**
  - Exclusive-fullscreen games on some drivers cannot be captured → run the game in
    **borderless windowed** mode.
  - Protected content returns black frames — irrelevant for games.
  - WGC requires Windows 10 1903+; 22621 target assumed.

### 3.2 WarpRenderer (D3D11)

- Device: `ID3D11Device` (feature level 11.0+), one immediate context. The **same**
  device is shared with CaptureService so captured textures are consumed with zero copy.
- Pipeline: no vertex buffer — fullscreen triangle generated from `SV_VertexID`;
  one pixel shader implementing the full backward map (§4); one constant buffer
  (`DomeParams`, 16-byte aligned); bilinear-clamp sampler.
- Dome master render target: square texture, configurable (default **2048×2048**).
- Per frame: if a new captured frame exists, bind it as SRV; otherwise reuse the last one.

### 3.3 Outputs

**PreviewOutput (v1):** borderless window on a chosen display (defaults to the
non-primary display — the projector), DXGI flip-model swapchain
(`DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL`), `Present(1)`. Window created with raw Win32
P/Invoke (no WinForms/WPF dependency).

**SpoutOutput (v1.1 integration point):** outputs implement `IFrameOutput`:

```csharp
interface IFrameOutput { ... (View, Width, Height) BeginFrame(); void EndFrame(); }
```

Spout wiring = create the dome master as a **shared** texture (`D3D11_RESOURCE_MISC_SHARED`),
register the sender via the Spout2 SDK (or the Spout.NET wrapper), copy/blit per frame.

### 3.4 Calibration & input

- All parameters live in `domecast.json` **and** are adjustable live from the keyboard:

| Key | Action |
|---|---|
| `1`–`9` | Select parameter slot |
| `←` / `→` | Decrease / increase selected parameter (hold `Shift` = fine step) |
| `T` | Toggle test pattern (grid + axis markers) |
| `G` | Toggle grid overlay on captured image |
| `S` | Save config to `domecast.json` |
| `Esc` | Quit |

- **Test pattern mode** synthesizes a synthetic wide-angle scene (polar grid in camera
  space) through the exact same warp — this makes dome calibration possible *without a
  game running*.

---

## 4. Warp mathematics

All angles in radians; dome radius normalized to 1. The shader performs a **backward
map**: for each output (dome master / projector image) pixel, find where to sample the
captured game frame.

### 4.1 Stage 1 — projector pixel → ray from projector

Output image normalized to `p = (x, y) ∈ [-1, 1]²` (y up), scaled by overscan.

```
r = |p · overscan|
if r > 1: output black, done          // outside fisheye circle
θ_p = r · (projFov / 2)               // angular fisheye: radius ∝ angle
φ_p = atan2(y, x)
d_local = (sin θ_p · cos φ_p,  sin θ_p · sin φ_p,  cos θ_p)
d_proj  = R_projTilt · R_projPan · d_local        // projector orientation
```

### 4.2 Stage 2 — off-axis correction (projector → dome surface)

If the projector sat exactly at dome center looking along the dome axis, skip this stage
(identity). Otherwise the projector is at `O` (in dome radii, origin = dome center,
z = dome axis) and each ray must be traced to the dome sphere:

```
|O + t·d_proj|² = 1   ⇒   t = −(O·d_proj) + √((O·d_proj)² − (|O|² − 1))
P = O + t·d_proj                       // point on dome surface (|P| = 1)
d_viewer = normalize(P)                // direction seen by viewer at dome center
```

This is Bourke's off-axis fisheye correction, folded into the same pass. With `O = 0`
it degenerates to `d_viewer = d_proj` at zero cost.

### 4.3 Stage 3 — dome direction → game camera

```
c = R_domeTilt · d_viewer              // dome coords → game camera coords (z = forward)
if c.z ≤ 0: output fade-to-black       // behind the camera — game never rendered this
```

### 4.4 Stage 4 — pinhole sample of captured frame

Game rendered with horizontal FOV `camHfov`, aspect `a = w/h` of the captured frame:

```
tan_h = tan(camHfov / 2);   tan_v = tan_h / a
u = (c.x / c.z) / tan_h · 0.5 + 0.5
v = 0.5 − (c.y / c.z) / tan_v · 0.5   // texture V is top-down
if (u,v) ∉ [0,1]²: output black with soft edge feather (≈ 2° cos falloff)
else: sample bilinear
```

**Content-coverage note (honest limitation):** only rays inside the game's frustum get
real pixels. With a typical 90° hFOV game, a 180° dome gets full content on ≈ 90° of
its surface and feathered black beyond. Recommend running games at max FOV
(100–120° where available) — true peripheral content requires Option C (engine injection).

### 4.5 Test pattern

Replaces Stage 4's sample with a synthetic polar grid evaluated directly in camera
space: rings every 15° of `acos(c.z)`, spokes every 30° of `atan2(c.y, c.x)`, red
marker on +z, green on +x.

---

## 5. Performance and quality plan

| Item | Target | How verified |
|---|---|---|
| Warp GPU cost | < 0.5 ms @ 2048² | D3D11 timestamp queries, `--bench` flag |
| Added latency | ≤ 2 frames | Photodiode or high-speed camera: game screen vs dome |
| Capture frame rate | = game frame rate | Frame-arrival counter overlay |
| Memory | < 150 MB | Two capture buffers + dome master + swapchain |
| Startup | < 2 s to first dome image | Manual |

Quality guards: bilinear sampling only; sRGB handled implicitly (capture and
swapchain both B8G8R8A8 UNORM).

---

## 6. Compatibility & safety notes

- **Anti-cheat:** zero injection → zero ban surface. DomeCast reads frames the same way
  OBS Game Capture does. Some titles with aggressive capture blocking (e.g., some builds
  of Valorant) block WGC of the game window — falls back to black frame, not a ban.
- **Exclusive fullscreen:** unsupported by WGC on several GPU drivers → borderless windowed.
- **Multi-GPU laptops:** capture and render must live on one adapter (Windows Graphics
  Settings → set both game and DomeCast to "High performance").
- **Distribution:** standalone EXE via GitHub Releases, free and open source (MIT) —
  consistent with the Luke Ross DMCA precedent: never charge, never bundle game assets,
  no Steam Workshop (per-game only).

---

## 7. Repository layout (prototype)

```
DomeCast/
├── DomeCast.csproj            net8.0-windows10.0.22621.0, Vortice
├── Program.cs                 CLI: window picker, flags (--bench, --pattern)
├── Capture/
│   ├── CaptureService.cs      WGC lifecycle, latest-frame-wins slot
│   ├── WindowEnumerator.cs    capturable-window discovery
│   └── Interop.cs             d3d11 capture interop, IGraphicsCaptureItemInterop,
│                              IDirect3DDxgiInterfaceAccess
├── Render/
│   ├── D3DContext.cs          device, immediate context, feature checks
│   ├── WarpRenderer.cs        quad pipeline, constant buffer, dome master RT
│   └── Shaders/DomeWarp.hlsl  §4 stages 1–4 + test pattern + grid overlay
├── Config/
│   ├── DomeParams.cs          parameter struct + JSON (de)serialization
│   └── CalibrationInput.cs    hotkey polling, live tuning, save
├── Output/
│   ├── IFrameOutput.cs
│   └── PreviewOutput.cs       Win32 window + flip-model swapchain
└── README.md                  setup, calibration walkthrough, troubleshooting
```

---

## 8. Test plan

1. **Unit-ish:** `--pattern` mode renders the synthetic grid standalone — validates the
   math chain without WGC or a game.
2. **Capture smoke:** capture a known window; confirm markers track smoothly.
3. **Latency:** game renders a full-screen flash on input; measure game-screen → dome
   delta with a 240 fps phone camera; assert ≤ 34 ms @ 60 Hz.
4. **Game matrix:** one DX11 title, one DX12 title, one Vulkan title, one
   EAC-protected title — all borderless windowed.
5. **Soak:** 2-hour session; watch for handle/pool leaks.

## 9. Milestones

| Milestone | Content | Exit criterion |
|---|---|---|
| M1 | Test-pattern warp only | Grid correct on real dome |
| M2 | WGC capture path | Live game on dome, playable latency |
| M3 | Live calibration + config | Full calib. from keyboard, persisted |
| M4 | Hardening | Test plan §8 green; README complete |
| v1.1 | SpoutOutput | Receivers (Resolume/obs-spout) see sender |
