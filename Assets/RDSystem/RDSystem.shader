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
        float4 duv = float4(tw, th, -tw, 0);

        half2 q = SAMPLE_TEXTURE2D(_SelfTexture2D, sampler_SelfTexture2D, uv).xy;

        half2 dq = -q;
        dq += SAMPLE_TEXTURE2D(_SelfTexture2D, sampler_SelfTexture2D, uv - duv.xy).xy * 0.05;
        dq += SAMPLE_TEXTURE2D(_SelfTexture2D, sampler_SelfTexture2D, uv - duv.wy).xy * 0.20;
        dq += SAMPLE_TEXTURE2D(_SelfTexture2D, sampler_SelfTexture2D, uv - duv.zy).xy * 0.05;
        dq += SAMPLE_TEXTURE2D(_SelfTexture2D, sampler_SelfTexture2D, uv + duv.zw).xy * 0.20;
        dq += SAMPLE_TEXTURE2D(_SelfTexture2D, sampler_SelfTexture2D, uv + duv.xw).xy * 0.20;
        dq += SAMPLE_TEXTURE2D(_SelfTexture2D, sampler_SelfTexture2D, uv + duv.zy).xy * 0.05;
        dq += SAMPLE_TEXTURE2D(_SelfTexture2D, sampler_SelfTexture2D, uv + duv.wy).xy * 0.20;
        dq += SAMPLE_TEXTURE2D(_SelfTexture2D, sampler_SelfTexture2D, uv + duv.xy).xy * 0.05;

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