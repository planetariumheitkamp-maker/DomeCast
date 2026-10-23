using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace DomeCast.Render;

/// <summary>Owns the single D3D11 device shared by capture, warp, and output.</summary>
internal sealed class D3DContext : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    public D3DContext()
    {
        var flags = DeviceCreationFlags.None;
#if DEBUG
        flags |= DeviceCreationFlags.Debug;
#endif
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var hr = D3D11.D3D11CreateDevice(
            IntPtr.Zero, DriverType.Hardware, flags, levels, out ID3D11Device? device);
        if (hr.Failure || device is null)
        {
            // WARP fallback (no GPU available — e.g., RDP sessions)
            hr = D3D11.D3D11CreateDevice(
                IntPtr.Zero, DriverType.Warp, flags, levels, out device);
        }
        hr.CheckError();
        Device = device!;
        Context = Device.ImmediateContext;
    }

    public void Dispose()
    {
        Context?.Dispose();
        Device?.Dispose();
    }
}
