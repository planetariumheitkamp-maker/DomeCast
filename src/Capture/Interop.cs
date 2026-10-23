using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace DomeCast.Capture;

/// <summary>
/// COM interop glue for Windows.Graphics.Capture. WGC's projected APIs deliberately
/// omit HWND-based capture item creation and the IDirect3DSurface -> ID3D11Texture2D
/// hop, so those two come through manual COM interop here.
/// </summary>
internal static class Interop
{
    // IID of Windows.Graphics.Capture.IGraphicsCaptureItem
    internal static readonly Guid IidGraphicsCaptureItem =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll", PreserveSig = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>Creates a WinRT IDirect3DDevice wrapping an existing D3D11 device (zero-copy sharing).</summary>
    internal static IDirect3DDevice CreateDirect3DDevice(IntPtr dxgiDevice)
    {
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr pDevice);
        Marshal.ThrowExceptionForHR(hr);
        return WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(pDevice);
    }

    /// <summary>Creates a GraphicsCaptureItem for an existing top-level window.</summary>
    internal static GraphicsCaptureItem CreateCaptureItemForWindow(IntPtr hwnd)
    {
        int hr = WindowsCreateString("Windows.Graphics.Capture.GraphicsCaptureItem",
            (uint)"Windows.Graphics.Capture.GraphicsCaptureItem".Length, out IntPtr hClass);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            Guid iidInterop = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = RoGetActivationFactory(hClass, ref iidInterop, out IntPtr pFactory);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(pFactory);
                Guid iidItem = IidGraphicsCaptureItem;
                interop.CreateForWindow(hwnd, ref iidItem, out IntPtr pItem);
                return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(pItem);
            }
            finally
            {
                Marshal.Release(pFactory);
            }
        }
        finally
        {
            WindowsDeleteString(hClass);
        }
    }

    /// <summary>Maps a captured WinRT surface to its underlying D3D11 texture (caller must Release/Dispose).</summary>
    internal static IntPtr GetD3D11TexturePointer(Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface surface)
    {
        var access = (IDirect3DDxgiInterfaceAccess)surface;
        Guid iidTex2D = typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID;
        return access.GetInterface(ref iidTex2D);
    }
}

/// <summary>IGraphicsCaptureItemInterop — implemented by the GraphicsCaptureItem activation factory.</summary>
[ComImport]
[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    [PreserveSig]
    int CreateForWindow([In] IntPtr window, [In] ref Guid iid, out IntPtr result);

    [PreserveSig]
    int CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid, out IntPtr result);
}

/// <summary>IDirect3DDxgiInterfaceAccess — QI target on every WGC surface.</summary>
[ComImport]
[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface([In] ref Guid iid);
}
