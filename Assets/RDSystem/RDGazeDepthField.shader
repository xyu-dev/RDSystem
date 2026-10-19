Shader "RDSystem/RDGazeDepthField"
{
    // How far the space has been pushed out, per texel. Drives the vertex displacement.
    //
    // Different dynamics from RDResolveField on purpose. That one DILATES - a maximum taken over
    // neighbours - which gives a front that travels a long way and a plateau with a hard edge.
    // A dent wants the opposite: it should accumulate under the gaze, spread only a little, and
    // stay smooth, so this DIFFUSES instead, and decays fast enough that space you stop looking
    // at closes back up.
    //
    // Everything is per-second and scaled by _DeltaTime from the driver, so the dent grows and
    // relaxes at the same rate whatever the frame rate - which matters, because this runs one
    // step per frame.

    Properties
    {
        [Header(Gaze)]
        _GazeDir("Gaze Direction (Shader Sphere Dir)", Vector) = (0, 1, 0, 0)
        _GazeState("Gaze State", Float) = 0
        _PaintRadius("Paint Radius (Radians)", Range(0.01, 1.5)) = 0.26
        _PaintSoftness("Paint Softness (Radians)", Range(0.001, 0.8)) = 0.22

        [Header(Rates)]
        _DeltaTime("Delta Time", Float) = 0.016
        _PaintPerSecond("Paint Per Second", Range(0.01, 4)) = 0.55
        _DiffusePerSecond("Diffuse Per Second", Range(0, 20)) = 3.5
        _DecayTau("Decay Time Constant (seconds)", Range(0.5, 120)) = 10
    }

    HLSLINCLUDE

    #include "CustomRenderTexture.hlsl"

    float4 _GazeDir;
    float _GazeState;
    float _PaintRadius;
    float _PaintSoftness;

    float _DeltaTime;
    float _PaintPerSecond;
    float _DiffusePerSecond;
    float _DecayTau;

    #define RD_PI 3.14159265359

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

    float SphereTexelAngle()
    {
        float lonTexel = 2.0 * RD_PI / max(1.0, _CustomRenderTextureWidth);
        float latTexel = RD_PI / max(1.0, _CustomRenderTextureHeight);
        return max(lonTexel, latTexel);
    }

    float SampleDepth(float2 uv)
    {
        float belowPole = step(uv.y, 0.0);
        float abovePole = step(1.0, uv.y);
        float crossed = max(belowPole, abovePole);

        uv.y = lerp(uv.y, -uv.y, belowPole);
        uv.y = lerp(uv.y, 2.0 - uv.y, abovePole);
        uv.x = frac(uv.x + crossed * 0.5);

        return SAMPLE_TEXTURE2D_LOD(_SelfTexture2D, sampler_SelfTexture2D, uv, 0).r;
    }

    float GazePaint(float2 uv)
    {
        if (_GazeState < 0.5) return 0.0;

        float3 gaze = _GazeDir.xyz;
        if (dot(gaze, gaze) < 0.000001) return 0.0;

        float3 dir = SphereUVToDir(uv);
        float angle = acos(clamp(dot(dir, normalize(gaze)), -1.0, 1.0));

        // Wide and soft, so the dent is a bowl rather than a crater with a rim.
        float soft = max(0.001, _PaintSoftness);
        return 1.0 - smoothstep(_PaintRadius - soft, _PaintRadius + soft, angle);
    }

    half4 fragInit(InitCustomRenderTextureVaryings i) : SV_Target
    {
        return half4(0, 0, 0, 0);
    }

    half4 fragUpdate(CustomRenderTextureVaryings i) : SV_Target
    {
        float2 uv = i.globalTexcoord.xy;
        float dt = max(0.0001, _DeltaTime);

        float self = SampleDepth(uv);

        // Constant arc, so the bowl keeps its shape at every latitude rather than stretching.
        float arc = SphereTexelAngle();
        float sinTheta = sin(uv.y * RD_PI);

        float dv = arc / RD_PI;
        float du = min(arc / (2.0 * RD_PI * max(sinTheta, 1e-4)), 0.5);

        float sum = 0.0;
        sum += SampleDepth(uv + float2( du, 0.0));
        sum += SampleDepth(uv + float2(-du, 0.0));
        sum += SampleDepth(uv + float2(0.0,  dv));
        sum += SampleDepth(uv + float2(0.0, -dv));
        sum += SampleDepth(uv + float2( du,  dv));
        sum += SampleDepth(uv + float2(-du,  dv));
        sum += SampleDepth(uv + float2( du, -dv));
        sum += SampleDepth(uv + float2(-du, -dv));

        float average = sum * 0.125;

        // Diffuse towards the neighbourhood average. Clamped, because at a high rate and a long
        // frame the lerp would overshoot past the average and ring.
        float blend = saturate(_DiffusePerSecond * dt);
        float value = lerp(self, average, blend);

        // Accumulate under the gaze, then relax. exp(-dt/tau) is the frame-rate-independent form:
        // at tau = 10 s a dent is about 95% gone half a minute after the viewer looks away.
        value += GazePaint(uv) * _PaintPerSecond * dt;
        value *= exp(-dt / max(0.1, _DecayTau));

        return half4(saturate(value), 0, 0, 0);
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
