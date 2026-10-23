// DomeCast — fisheye dome-master warp (spec §4, stages 1-4)
// One backward-mapped pixel pass: output pixel -> projector fisheye ray ->
// (optional off-axis sphere trace) -> dome viewer direction -> game camera -> source UV.

cbuffer DomeParams : register(b0)
{
    float4x4 ProjOrient;  // projector pan/tilt rotation
    float4x4 DomeTilt;    // dome-axis tilt/yaw -> game camera space
    float4 ProjOffset;    // xyz: projector position in dome radii, w: off-axis enable
    float4 Misc;          // x: projFovHalf (rad), y: overscan, z: tanHalfCamHfov, w: mode (0 capture, 1 pattern)
    float4 Misc2;         // x: tanHalfCamVfov, y: gridOverlay (0/1), z: unused, w: unused
};

Texture2D SourceTex : register(t0);
SamplerState Sampler : register(s0);

struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

// Fullscreen triangle from vertex id — no vertex buffer needed.
VSOut VSMain(uint vid : SV_VertexID)
{
    VSOut o;
    float2 p = float2((vid << 1) & 2, vid & 2); // (0,0) (2,0) (0,2)
    o.pos = float4(p.x * 2.0 - 1.0, 1.0 - p.y * 2.0, 0.0, 1.0);
    o.uv = p;
    return o;
}

// Synthetic polar grid in camera space: rings every 15 deg, spokes every 30 deg.
float3 TestPattern(float3 c)
{
    if (c.z <= 0.0) return float3(0.05, 0.05, 0.08);
    float ang = degrees(acos(saturate(c.z / length(c))));
    float azim = degrees(atan2(c.y, c.x)) + 180.0;
    float ring = 1.0 - smoothstep(0.6, 1.4, abs(frac(ang / 15.0 + 0.5) - 0.5) * 15.0);
    float spoke = 1.0 - smoothstep(0.6, 1.4, abs(frac(azim / 30.0 + 0.5) - 0.5) * 30.0 / max(ang * 0.11, 0.35));
    float3 col = float3(0.10, 0.10, 0.14);
    col += float3(0.55, 0.55, 0.6) * ring;
    col += float3(0.35, 0.35, 0.4) * spoke;
    // axis markers: +z red, +x green
    col += float3(1, 0, 0) * (1.0 - smoothstep(1.0, 3.0, ang));
    col += float3(0, 0.8, 0) * (1.0 - smoothstep(1.0, 3.0, degrees(acos(saturate(dot(normalize(c), float3(1, 0, 0)))))));
    return col;
}

float4 PSMain(VSOut i) : SV_Target
{
    // --- Stage 1: projector pixel -> fisheye ray in projector space ---
    float2 p = float2(i.uv.x * 2.0 - 1.0, 1.0 - i.uv.y * 2.0) * Misc.y; // y up, overscan
    float r = length(p);
    if (r > 1.0) return float4(0, 0, 0, 1); // outside the fisheye circle

    float theta = r * Misc.x;                 // angular fisheye: r proportional to angle
    float phi = atan2(p.y, p.x);
    float st, ct, sp, cp;
    sincos(theta, st, ct); sincos(phi, sp, cp);
    float3 d = float3(st * cp, st * sp, ct);  // z = fisheye axis
    d = mul(ProjOrient, float4(d, 0.0)).xyz;  // projector orientation

    // --- Stage 2: off-axis correction — trace ray from projector to the dome sphere ---
    if (ProjOffset.w > 0.5)
    {
        float3 O = ProjOffset.xyz;
        float od = dot(O, d);
        float disc = od * od - (dot(O, O) - 1.0);
        if (disc < 0.0) return float4(0, 0, 0, 1); // ray misses dome entirely
        float t = -od + sqrt(disc);
        d = normalize(O + t * d);                  // direction from dome center to hit point
    }

    // --- Stage 3: dome direction -> game camera space ---
    float3 c = mul(DomeTilt, float4(d, 0.0)).xyz;

    // --- Stage 4: sample ---
    if (Misc.w > 0.5)
        return float4(TestPattern(c), 1.0);

    if (c.z <= 0.0) return float4(0, 0, 0, 1);    // behind the camera

    float sx = (c.x / c.z) / Misc.z;               // pinhole, normalized to [-1,1] at frustum edge
    float sy = (c.y / c.z) / Misc2.x;
    float2 uv = float2(sx * 0.5 + 0.5, 0.5 - sy * 0.5);

    float edge = max(abs(sx), abs(sy));
    float alpha = saturate((1.04 - edge) / 0.04);  // ~2 deg soft feather past the frustum edge
    float3 col = SourceTex.Sample(Sampler, saturate(uv)).rgb * alpha;

    if (Misc2.y > 0.5)
    {
        float2 g = abs(frac(uv * 16.0) - 0.5);
        float line = smoothstep(0.48, 0.5, max(g.x, g.y));
        col = lerp(col, float3(0, 0.7, 1.0), line * 0.8 * alpha);
    }
    return float4(col, 1.0);
}
