using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace DomeCast.Capture;

/// <summary>
/// Out-of-process window capture via Windows.Graphics.Capture.
/// Latest-frame-wins: frames arriving faster than the render loop consumes them are
/// dropped immediately; the render loop always sees the freshest frame.
/// The captured frame is copied (under <see cref="SyncLock"/>) into an internal staging
/// texture on arrival, so the WGC frame/pool object can be released at once.
/// </summary>
internal sealed class CaptureService : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly object _syncLock = new();

    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private ID3D11ShaderResourceView? _stagingView;

    public object SyncLock => _syncLock;
    public ID3D11ShaderResourceView? StagingView => _stagingView;
    public int FrameWidth { get; private set; }
    public int FrameHeight { get; private set; }
    public long FramesArrived;
    public long FramesCopied;

    public CaptureService(ID3D11Device device)
    {
        _device = device;
        _context = device.ImmediateContext;
    }

    public void Start(IntPtr hwnd)
    {
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        _winrtDevice = Interop.CreateDirect3DDevice(dxgiDevice.NativePointer);
        _item = Interop.CreateCaptureItemForWindow(hwnd);
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _item.Size);
        _framePool.FrameArrived += OnFrameArrived;
        _session = _framePool.CreateCaptureSession(_item);
        _session.StartCapture();
        FrameWidth = _item.Size.Width;
        FrameHeight = _item.Size.Height;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;
        Interlocked.Increment(ref FramesArrived);

        var size = frame.ContentSize;
        if (size.Width != FrameWidth || size.Height != FrameHeight)
        {
            FrameWidth = size.Width;
            FrameHeight = size.Height;
            // Pool must be told the new size; staging is re-created lazily on next frame.
            sender.Recreate(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            lock (_syncLock)
            {
                _stagingView?.Dispose(); _stagingView = null;
                _staging?.Dispose(); _staging = null;
            }
        }

        IntPtr texPtr = Interop.GetD3D11TexturePointer(frame.Surface);
        using var src = new ID3D11Texture2D(texPtr);

        lock (_syncLock)
        {
            EnsureStaging(src.Description);
            _context.CopyResource(_staging!, src);
            Interlocked.Increment(ref FramesCopied);
        }
    }

    private void EnsureStaging(Texture2DDescription srcDesc)
    {
        if (_staging is not null
            && _staging.Description.Width == srcDesc.Width
            && _staging.Description.Height == srcDesc.Height) return;

        _stagingView?.Dispose();
        _staging?.Dispose();

        var desc = new Texture2DDescription(
            Format.B8G8R8A8_UNorm, srcDesc.Width, srcDesc.Height,
            mipLevels: 1, arraySize: 1,
            bindFlags: BindFlags.ShaderResource,
            usage: ResourceUsage.Default,
            cpuAccessFlags: CpuAccessFlags.None,
            miscFlags: ResourceOptionFlags.None);
        _staging = _device.CreateTexture2D(desc);
        _stagingView = _device.CreateShaderResourceView(_staging);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _framePool?.Dispose();
        _stagingView?.Dispose();
        _staging?.Dispose();
        _winrtDevice = null;
        _item = null;
    }
}
