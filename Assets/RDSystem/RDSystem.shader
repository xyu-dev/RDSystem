Shader "RDSystem/RDSystem"
{
    Properties
    {
        [Header(Initialization), Space]
        _Pop("Population", Range(0, 1)) = 0.1
        _Seed("Random Seed", Integer) = 1234

        [Header(Reaction Diffusion), Space]
        _Du("Diffusion (u)", Range(0, 1)) = 1
        _Dv("Diffusion (v)", Range(0, 1)) = 0.4
        _Feed("Feed", Range(0.02, 0.12)) = 0.035
        _Kill("Kill", Range(0.01413, 0.07)) = 0.057
        _StepScale("Stencil Step Scale", Range(0.5, 4)) = 1

        [Header(Interaction), Space]
        _ClickState("Click State", Float) = 0
        _ClickDir("Interaction Direction (Shader Sphere Dir)", Vector) = (0, 1, 0, 0)

        _ClickRadius("Ring Outer Radius (Radians)", Range(0.001, 3.14159)) = 0.4
        _RingThickness("Ring Thickness (Radians)", Range(0.001, 1.0)) = 0.08
        _RingSoftness("Ring Softness (Radians)", Range(0.0001, 0.3)) = 0.01

        [Header(Local Gaze Modulation), Space]
        _GazeFeedBoost("Gaze Feed Boost", Range(0, 0.03)) = 0.010
        _GazeKillBoost("Gaze Kill Boost", Range(-0.02, 0.02)) = -0.004

        [Header(Fallback Ring Seeds), Space]
        _RingNeighborBand("Ring Neighbor Band (Radians)", Range(0.001,1.0)) = 0.025
        _ExistingSeedThreshold("Existing Seed Value Threshold", Range(0,1)) = 0.02
        _RingSeedThreshold("Local Seed Threshold", Range(0,1)) = 0.12
        _RingSeedStrength("Ring Seed Strength", Range(0,0.25)) = 0.10

        _RingBodyDensity("Ring Body Density", Range(1,256)) = 88
        _InnerBandDensity("Inner Band Density", Range(1,256)) = 32
        _OuterBandDensity("Outer Band Density", Range(1,256)) = 32

        _RingSeedDotRadius("Ring Seed Dot Radius", Range(0.001,0.08)) = 0.010
        _RingSeedDotSoftness("Ring Seed Dot Softness", Range(0.0001,0.03)) = 0.003

        [Header(Return To Seed), Space]
        _ResetBlend("Reset Blend", Range(0, 1)) = 0

        [Header(Debug), Space]
        _DebugShowRingMask("Debug Show Ring Mask", Float) = 0
    }

    HLSLINCLUDE

    #include "CustomRenderTexture.hlsl"
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Random.hlsl"

    float _Pop;
    uint _Seed;

    half _Du, _Dv;
    half _Feed, _Kill;
    float _StepScale;

    float _ClickState;
    float4 _ClickDir;
    float _ClickRadius;
    float _RingThickness;
    float _RingSoftness;

    float _GazeFeedBoost;
    float _GazeKillBoost;

    float _RingNeighborBand;
    float _ExistingSeedThreshold;
    float _RingSeedThreshold;
    float _RingSeedStrength;

    float _RingBodyDensity;
    float _InnerBandDensity;
    float _OuterBandDensity;

    float _RingSeedDotRadius;
    float _RingSeedDotSoftness;

    float _ResetBlend;
    float _DebugShowRingMask;

    #define RD_PI 3.14159265359

    half2 InitialSeedState(float2 uv)
    {
        uint x = (uint)(uv.x * _CustomRenderTextureWidth);
        uint y = (uint)(uv.y * _CustomRenderTextureHeight);

        float rnd = GenerateHashedRandomFloat(uint3(x, y, _Seed));
        half u = 1.0;
        half v = (rnd < _Pop * _Pop * 0.01) ? 1.0 : 0.0;

        return half2(u, v);
    }

    float3 SphereUVToDir(float2 uv)
    {
        float phi = uv.x * 2.0 * RD_PI;
        float theta = uv.y * RD_PI;

        float3 dir;
        dir.x = sin(theta) * cos(phi);
        dir.y = cos(theta);
        dir.z = sin(theta) * sin(phi);

        return normalize(dir);
    }

    /// The angular size of the coarsest texel. In an equirectangular map a u-texel subtends
    /// 2*pi/W of longitude at the equator while a v-texel subtends only pi/H of latitude, so on a
    /// square texture the u-texel is twice the v-texel. Step by the smaller of the two and the
    /// horizontal neighbours land half a texel away, bilinear filtering averages them with the
    /// centre, horizontal diffusion is halved, and the pattern locks onto the texel grid. Taking
    /// the max is what keeps every neighbour at least one whole texel out.
    float SphereTexelAngle()
    {
        float lonTexel = 2.0 * RD_PI / max(1.0, _CustomRenderTextureWidth);
        float latTexel = RD_PI / max(1.0, _CustomRenderTextureHeight);
        return max(lonTexel, latTexel);
    }

    /// Sample the state at a UV that may have stepped off the top or bottom of the map. Crossing
    /// a pole continues onto the opposite meridian - v folds back, u gains half a turn - which is
    /// both geometrically right and what keeps every tap inside the texture without a clamp.
    half2 SampleStateUV(float2 uv)
    {
        float belowPole = step(uv.y, 0.0);
        float abovePole = step(1.0, uv.y);
        float crossed = max(belowPole, abovePole);

        uv.y = lerp(uv.y, -uv.y, belowPole);
        uv.y = lerp(uv.y, 2.0 - uv.y, abovePole);
        uv.x = frac(uv.x + crossed * 0.5);

        return SAMPLE_TEXTURE2D_LOD(_SelfTexture2D, sampler_SelfTexture2D, uv, 0).xy;
    }

    /// Nine-point Laplacian whose neighbours sit at a constant ARC LENGTH rather than a constant
    /// UV offset. A v-offset spans pi of latitude, but a u-offset spans only 2*pi*sin(theta) of
    /// longitude - shrinking to nothing as the latitude circle closes at the poles. That is what
    /// collapsed the stencil onto its own centre and produced the pole deformation.
    ///
    /// Matching the two arcs needs one factor: scale the u-offset by 1/sin(theta). That is the
    /// whole correction, and it costs a single sine per fragment rather than an acos and an atan2
    /// per tap, which is why this replaced the direction-space version - same geometry, a small
    /// fraction of the cost.
    ///
    /// Weights are the original 0.20 / 0.05 so Du, Dv, feed and kill keep their tuning: both sets
    /// sum to one, only where the samples are taken has changed.
    half2 SphericalLaplacian(float2 uv, half2 centre)
    {
        float arc = SphereTexelAngle() * max(0.01, _StepScale);

        float sinTheta = sin(uv.y * RD_PI);

        float dv = arc / RD_PI;
        float du = arc / (2.0 * RD_PI * max(sinTheta, 1e-4));

        // Half the map is 180 degrees of longitude, the furthest a neighbour can usefully sit.
        // Only the top and bottom row or so ever reach this, and there the entire latitude circle
        // is a texel or two across, so "the opposite side of the polar circle" is the right
        // neighbour regardless.
        du = min(du, 0.5);

        half2 e  = SampleStateUV(uv + float2( du, 0.0));
        half2 w  = SampleStateUV(uv + float2(-du, 0.0));
        half2 nr = SampleStateUV(uv + float2(0.0,  dv));
        half2 s  = SampleStateUV(uv + float2(0.0, -dv));

        half2 ne = SampleStateUV(uv + float2( du,  dv));
        half2 nw = SampleStateUV(uv + float2(-du,  dv));
        half2 se = SampleStateUV(uv + float2( du, -dv));
        half2 sw = SampleStateUV(uv + float2(-du, -dv));

        return (e + w + nr + s) * 0.20 + (ne + nw + se + sw) * 0.05 - centre;
    }

    float3 SafeClickDir()
    {
        float3 d = _ClickDir.xyz;
        if (dot(d, d) < 0.000001)
            return float3(0, 1, 0);
        return normalize(d);
    }

    float ComputeAngularDistance(float2 uv, float3 centerDir)
    {
        float3 dir = SphereUVToDir(uv);
        float cosAngle = dot(normalize(dir), normalize(centerDir));
        cosAngle = clamp(cosAngle, -1.0, 1.0);
        return acos(cosAngle);
    }

    float ComputeBandMask(float angle, float minR, float maxR, float soft)
    {
        float outerMask = 1.0 - smoothstep(maxR - soft, maxR + soft, angle);
        float innerMask = smoothstep(minR - soft, minR + soft, angle);
        return saturate(outerMask * innerMask);
    }

    float3 ComputeRingRegionMasks(float2 uv)
    {
        float3 clickDir = SafeClickDir();
        float angle = ComputeAngularDistance(uv, clickDir);

        float outerRadius = _ClickRadius;
        float innerRadius = max(0.0, outerRadius - _RingThickness);
        float soft = max(0.0001, _RingSoftness);

        float ringBody = ComputeBandMask(angle, innerRadius, outerRadius, soft);

        float innerBandMin = max(0.0, innerRadius - _RingNeighborBand);
        float innerBandMax = innerRadius;
        float innerBand = ComputeBandMask(angle, innerBandMin, innerBandMax, soft);

        float outerBandMin = outerRadius;
        float outerBandMax = outerRadius + _RingNeighborBand;
        float outerBand = ComputeBandMask(angle, outerBandMin, outerBandMax, soft);

        return float3(ringBody, innerBand, outerBand) * _ClickState;
    }

    float ComputeRingMask(float2 uv)
    {
        return ComputeRingRegionMasks(uv).x;
    }

    float Hash21(float2 p)
    {
        p = frac(p * float2(123.34, 456.21));
        p += dot(p, p + 34.45);
        return frac(p.x * p.y);
    }

    float DotFromGrid(float2 uv, float density, float radius, float softness, float seedBias)
    {
        float2 gv = uv * density;
        float2 id = floor(gv);
        float2 local = frac(gv) - 0.5;

        float rnd = Hash21(id + seedBias);
        float active = step(0.985, rnd);

        float d = length(local);
        float dotMask = 1.0 - smoothstep(radius - softness, radius + softness, d);

        return active * dotMask;
    }

    float EstimateLocalSeed(float2 uv, float tw, float th)
    {
        float sum = 0.0;
        float count = 0.0;

        float2 offsets[9] =
        {
            float2( 0,   0),
            float2( tw,  0),
            float2(-tw,  0),
            float2( 0,  th),
            float2( 0, -th),
            float2( tw, th),
            float2(-tw, th),
            float2( tw,-th),
            float2(-tw,-th)
        };

        [unroll]
        for (int i = 0; i < 9; i++)
        {
            float2 suv = saturate(uv + offsets[i]);
            float v = SAMPLE_TEXTURE2D(_SelfTexture2D, sampler_SelfTexture2D, suv).y;
            sum += step(_ExistingSeedThreshold, v);
            count += 1.0;
        }

        return sum / count;
    }

    float GenerateFallbackRingSeed(float2 uv)
    {
        float3 regionMasks = ComputeRingRegionMasks(uv);

        float ringBody  = regionMasks.x;
        float innerBand = regionMasks.y;
        float outerBand = regionMasks.z;

        float ringDots =
            DotFromGrid(uv, _RingBodyDensity, _RingSeedDotRadius, _RingSeedDotSoftness, 11.7) * ringBody;

        float innerDots =
            DotFromGrid(uv, _InnerBandDensity, _RingSeedDotRadius, _RingSeedDotSoftness, 37.2) * innerBand;

        float outerDots =
            DotFromGrid(uv, _OuterBandDensity, _RingSeedDotRadius, _RingSeedDotSoftness, 71.9) * outerBand;

        return saturate(ringDots * 0.60 + innerDots * 0.20 + outerDots * 0.20);
    }

    half4 fragInit(InitCustomRenderTextureVaryings i) : SV_Target
    {
        half2 q = InitialSeedState(i.texcoord.xy);
        return half4(q, 0, 0);
    }

    half4 fragUpdate(CustomRenderTextureVaryings i) : SV_Target
    {
        float tw = 1.0 / _CustomRenderTextureWidth;
        float th = 1.0 / _CustomRenderTextureHeight;

        float2 uv = i.globalTexcoord.xy;

        half2 q = SAMPLE_TEXTURE2D(_SelfTexture2D, sampler_SelfTexture2D, uv).xy;

        half2 dq = SphericalLaplacian(uv, q);

        float ringMask = ComputeRingMask(uv);

        float localFeed = _Feed + ringMask * _GazeFeedBoost;
        float localKill = _Kill + ringMask * _GazeKillBoost;

        localFeed = clamp(localFeed, 0.02, 0.12);
        localKill = clamp(localKill, 0.01413, 0.07);

        half ABB = q.x * q.y * q.y;

        q += float2(
            dq.x * _Du - ABB + localFeed * (1.0 - q.x),
            dq.y * _Dv + ABB - (localKill + localFeed) * q.y
        );

        if (_ClickState > 0.5)
        {
            float3 regionMasks = ComputeRingRegionMasks(uv);
            float localRingRegion = saturate(regionMasks.x + regionMasks.y + regionMasks.z);

            if (localRingRegion > 0.001)
            {
                float localSeedRatio = EstimateLocalSeed(uv, tw, th);
                float needFallback = 1.0 - step(_RingSeedThreshold, localSeedRatio);

                if (needFallback > 0.5)
                {
                    float fallbackSeed = GenerateFallbackRingSeed(uv);
                    q.y = saturate(q.y + fallbackSeed * _RingSeedStrength * localRingRegion);
                }
            }
        }

        q.y = saturate(q.y + ringMask * 0.002);

        half2 seedState = InitialSeedState(uv);
        half2 softSeedState = half2(lerp(q.x, seedState.x, 0.35), lerp(q.y, seedState.y, 0.65));

        q = lerp(q, softSeedState, saturate(_ResetBlend));

        if (_DebugShowRingMask > 0.5)
        {
            float3 ringRegions = ComputeRingRegionMasks(uv);
            float debugRing = saturate(ringRegions.x + ringRegions.y * 0.5 + ringRegions.z * 0.5);
            return half4(debugRing, debugRing, debugRing, 1.0);
        }

        return half4(saturate(q), 0, 0);
    }

    ENDHLSL

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "Init"
            HLSLPROGRAM
            #pragma vertex InitCustomRenderTextureVertexShader
            #pragma fragment fragInit
            ENDHLSL
        }

        Pass
        {
            Name "Update"
            HLSLPROGRAM
            #pragma vertex CustomRenderTextureVertexShader
            #pragma fragment fragUpdate
            ENDHLSL
        }
    }
}