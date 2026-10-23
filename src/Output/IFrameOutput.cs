using Vortice.Direct3D11;

namespace DomeCast.Output;

/// <summary>Destination for warped dome frames.</summary>
internal interface IFrameOutput : IDisposable
{
    /// <summary>Render target to draw this frame's dome master into, plus its pixel size.</summary>
    (ID3D11RenderTargetView View, int Width, int Height) BeginFrame();

    /// <summary>Present / transmit the completed frame.</summary>
    void EndFrame();
}
