using System.Numerics;
using System.Runtime.InteropServices;
using DomeCast.Config;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DomeCast.Render;

/// <summary>
/// Single-pass fisheye warp. Renders into whatever render target the active output
/// provides (fullscreen preview swapchain in v1; a shared dome-master texture once
/// Spout output lands in v1.1).
/// </summary>
internal sealed class WarpRenderer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11Buffer _cbuffer;
    private readonly ID3D11SamplerState _sampler;

    // Optional GPU timing (bench mode)
    private readonly ID3D11Query? _qDisjoint;
    private readonly ID3D11Query? _qStart;
    private readonly ID3D11Query? _qEnd;

    public WarpRenderer(D3DContext d3d, string shaderPath, bool bench)
    {
        _device = d3d.Device;
        _ctx = d3d.Context;

        string src = File.ReadAllText(shaderPath);
        var vsBlob = Compiler.Compile(src, "VSMain", shaderPath, "vs_5_0", ShaderFlags.None, EffectFlags.None);
        var psBlob = Compiler.Compile(src, "PSMain", shaderPath, "ps_5_0", ShaderFlags.None, EffectFlags.None);
        _vs = _device.CreateVertexShader(vsBlob.Span, null!);
        _ps = _device.CreatePixelShader(psBlob.Span, null!);

        _cbuffer = _device.CreateBuffer(new BufferDescription(
            160, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        _sampler = _device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
            TextureAddressMode.Clamp));

        if (bench)
        {
            _qDisjoint = _device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
            _qStart = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
            _qEnd = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        }
    }

    /// <summary>Render one dome frame into the given target. Viewport is a centered square.</summary>
    public void Render(ID3D11RenderTargetView target, int targetW, int targetH,
                       DomeParams p, float sourceAspect, ID3D11ShaderResourceView? source)
    {
        UploadParams(p, sourceAspect);

        _ctx.OMSetRenderTargets(target, (ID3D11DepthStencilView?)null);
        _ctx.ClearRenderTargetView(target, new Color4(0, 0, 0, 1));

        int side = Math.Min(targetW, targetH);
        _ctx.RSSetViewport((targetW - side) / 2f, (targetH - side) / 2f, side, side, 0f, 1f);

        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(_ps);
        _ctx.PSSetConstantBuffer(0, _cbuffer);
        _ctx.PSSetSampler(0, _sampler);
        _ctx.PSSetShaderResource(0, source!);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        if (_qStart is null) { _ctx.Draw(3, 0); } else { DrawTimed(); }
        _ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView?)null!);
    }

    private void DrawTimed()
    {
        _ctx.Begin(_qDisjoint!);
        _ctx.End(_qStart!);
        _ctx.Draw(3, 0);
        _ctx.End(_qEnd!);
        _ctx.End(_qDisjoint!);
    }

    /// <summary>Bench helper: GPU time of the last timed draw in ms, or null while the query is in flight.</summary>
    public double? LastGpuMs()
    {
        if (_qDisjoint is null) return null;
        if (_ctx.GetData(_qDisjoint, AsyncGetDataFlags.DoNotFlush, out QueryDataTimestampDisjoint disjoint) && !disjoint.Disjoint
            && _ctx.GetData(_qEnd!, AsyncGetDataFlags.DoNotFlush, out ulong te)
            && _ctx.GetData(_qStart!, AsyncGetDataFlags.DoNotFlush, out ulong ts))
        {
            return (te - ts) * 1000.0 / disjoint.Frequency;
        }
        return null;
    }

    private void UploadParams(DomeParams p, float sourceAspect)
    {
        var projO = Matrix4x4.Transpose(p.ProjOrient);   // HLSL expects column-major
        var domeT = Matrix4x4.Transpose(p.DomeTilt);
        float tanHalfH = MathF.Tan(p.CamHfovDeg * MathF.PI / 360f);
        float tanHalfV = tanHalfH / Math.Max(sourceAspect, 0.01f);

        Span<float> f = stackalloc float[]
        {
            projO.M11, projO.M12, projO.M13, projO.M14,
            projO.M21, projO.M22, projO.M23, projO.M24,
            projO.M31, projO.M32, projO.M33, projO.M34,
            projO.M41, projO.M42, projO.M43, projO.M44,
            domeT.M11, domeT.M12, domeT.M13, domeT.M14,
            domeT.M21, domeT.M22, domeT.M23, domeT.M24,
            domeT.M31, domeT.M32, domeT.M33, domeT.M34,
            domeT.M41, domeT.M42, domeT.M43, domeT.M44,
            p.ProjOffset.X, p.ProjOffset.Y, p.ProjOffset.Z, p.OffAxisEnable ? 1f : 0f,
            p.ProjFovDeg * MathF.PI / 360f, p.Overscan, tanHalfH, p.TestPattern ? 1f : 0f,
            tanHalfV, p.GridOverlay ? 1f : 0f, 0f, 0f,
        };

        var mapped = _ctx.Map(_cbuffer, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        MemoryMarshal.AsBytes(f).CopyTo(mapped.AsSpan(160));
        _ctx.Unmap(_cbuffer, 0);
    }

    public void Dispose()
    {
        _qDisjoint?.Dispose(); _qStart?.Dispose(); _qEnd?.Dispose();
        _sampler.Dispose(); _cbuffer.Dispose(); _vs.Dispose(); _ps.Dispose();
    }
}
