using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DomeCast.Output;

/// <summary>
/// Borderless fullscreen window on the projector display with a flip-model swapchain.
/// Raw Win32 P/Invoke — no WinForms/WPF, keeps the tool a single small EXE.
/// </summary>
internal sealed class PreviewOutput : IFrameOutput
{
    private readonly IDXGISwapChain1 _swapchain;
    private readonly ID3D11Texture2D _backBuffer;
    private readonly ID3D11RenderTargetView _rtv;
    private readonly IntPtr _hwnd;

    public int Width { get; }
    public int Height { get; }

    public PreviewOutput(ID3D11Device device, int monitorIndex)
    {
        (var rect, _) = Win32.GetMonitorRect(monitorIndex);
        Width = rect.Right - rect.Left;
        Height = rect.Bottom - rect.Top;

        _hwnd = Win32.CreateFullscreenWindow("DomeCast Preview", rect.Left, rect.Top, Width, Height);

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var desc = new SwapChainDescription1
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };
        _swapchain = factory.CreateSwapChainForHwnd(device, _hwnd, desc, null, null);
        // This tool owns the screen — keep DXGI from grabbing Alt+Enter.
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAltEnter);

        _backBuffer = _swapchain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = device.CreateRenderTargetView(_backBuffer);

        Win32.ShowWindow(_hwnd, 5); // SW_SHOW
    }

    public (ID3D11RenderTargetView, int, int) BeginFrame() => (_rtv, Width, Height);

    public void EndFrame() => _swapchain.Present(1, PresentFlags.None);

    public void Dispose()
    {
        _rtv.Dispose();
        _backBuffer.Dispose();
        _swapchain.Dispose();
        Win32.DestroyWindow(_hwnd);
    }
}

/// <summary>Minimal Win32 surface: monitors, fullscreen popup window, message pump.</summary>
internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr hMon, ref MonitorInfo info);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(string? name);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint CbSize, Style; public IntPtr WndProc; public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string? MenuName, ClassName; public IntPtr IconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo { public int CbSize; public Rect RcMonitor, RcWork; public uint Flags; }

    private const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000;
    private const int SW_SHOW = 5, WM_QUIT = 0x0012, PM_REMOVE = 1;

    private static readonly List<(Rect Rect, bool Primary)> s_monitors = new();
    private static bool s_classRegistered;

    public static (Rect Rect, bool Primary) GetMonitorRect(int index)
    {
        if (s_monitors.Count == 0)
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
            {
                var mi = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfoW(hMon, ref mi))
                    s_monitors.Add((mi.RcMonitor, (mi.Flags & 1) != 0));
                return true;
            }, IntPtr.Zero);
        }
        if (s_monitors.Count == 0) return (new Rect { Right = 1920, Bottom = 1080 }, true);
        // Default (index 0): first non-primary monitor = the projector; fall back to primary.
        if (index <= 0)
        {
            var np = s_monitors.FindIndex(m => !m.Primary);
            index = np >= 0 ? np : 0;
        }
        index = Math.Min(index, s_monitors.Count - 1);
        return s_monitors[index];
    }

    public static int MonitorCount { get { GetMonitorRect(0); return s_monitors.Count; } }

    public static IntPtr CreateFullscreenWindow(string title, int x, int y, int w, int h)
    {
        if (!s_classRegistered)
        {
            var wc = new WndClassEx
            {
                CbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate((WndProc)DefWindowProcW),
                Instance = GetModuleHandleW(null),
                ClassName = "DomeCastPreview",
                Cursor = LoadCursor(IntPtr.Zero, 32512),
            };
            if (RegisterClassExW(ref wc) == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            s_classRegistered = true;
        }
        var hwnd = CreateWindowExW(0, "DomeCastPreview", title, WS_POPUP | WS_VISIBLE,
            x, y, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        ShowWindow(hwnd, SW_SHOW);
        return hwnd;
    }

    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, int id);

    /// <summary>Pump pending messages. Returns false on WM_QUIT.</summary>
    public static bool PumpMessages()
    {
        while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            if (msg.Message == WM_QUIT) return false;
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        return true;
    }
}
