Shader "RDSystem/RDResolveField"
{
    // How much of the sphere the viewer has resolved, stored per texel on its own buffer.
    //
    // Looking paints a disc at the gaze direction; each step the painted region dilates outward
    // by one texel, losing a little value as it goes. That loss is the mechanic: one gaze point's
    // influence reaches only so far, so the sphere CANNOT be completed by staring at one spot -
    // the viewer has to sweep their gaze over all of it.
    //
    // Lives in Assets/RDSystem/ so the relative CustomRenderTexture.hlsl include resolves.

    Properties
    {
        [Header(Gaze)]
        _GazeDir("Gaze Direction (Shader Sphere Dir)", Vector) = (0, 1, 0, 0)
        _GazeState("Gaze State", Float) = 0
        _GazeRadius("Paint Radius (Radians)", Range(0.01, 1.5)) = 0.22
        _GazeSoftness("Paint Softness (Radians)", Range(0.001, 0.5)) = 0.07

        [Header(Spread)]
        _SpreadRetain("Spread Retain Per Step", Range(0.9, 1.0)) = 0.994
        _StepScale("Dilation Step Scale", Range(0.5, 4)) = 1

        [Header(Decay)]
        _Decay("Decay Per Step", Range(0.9, 1.0)) = 0.99985
    }

    HLSLINCLUDE

    #include "CustomRenderTexture.hlsl"

    float4 _GazeDir;
    float _GazeState;
    float _GazeRadius;
    float _GazeSoftness;

    float _SpreadRetain;
    float _StepScale;
    float _Decay;

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

    /// Same pole handling as the simulation: stepping off the top continues onto the opposite
    /// meridian, so the front crosses the poles instead of piling up against them.
    float SampleResolve(float2 uv)
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

        float soft = max(0.001, _GazeSoftness);
        return 1.0 - smoothstep(_GazeRadius - soft, _GazeRadius + soft, angle);
    }

    half4 fragInit(InitCustomRenderTextureVaryings i) : SV_Target
    {
        return half4(0, 0, 0, 0);
    }

    half4 fragUpdate(CustomRenderTextureVaryings i) : SV_Target
    {
        float2 uv = i.globalTexcoord.xy;

        float self = SampleResolve(uv);

        // Constant arc, not constant UV, so the front advances at the same speed at every
        // latitude instead of stalling at the poles.
        float arc = SphereTexelAngle() * max(0.01, _StepScale);
        float sinTheta = sin(uv.y * RD_PI);

        float dv = arc / RD_PI;
        float du = min(arc / (2.0 * RD_PI * max(sinTheta, 1e-4)), 0.5);

        float neighbour = 0.0;
        neighbour = max(neighbour, SampleResolve(uv + float2( du, 0.0)));
        neighbour = max(neighbour, SampleResolve(uv + float2(-du, 0.0)));
        neighbour = max(neighbour, SampleResolve(uv + float2(0.0,  dv)));
        neighbour = max(neighbour, SampleResolve(uv + float2(0.0, -dv)));
        neighbour = max(neighbour, SampleResolve(uv + float2( du,  dv)));
        neighbour = max(neighbour, SampleResolve(uv + float2(-du,  dv)));
        neighbour = max(neighbour, SampleResolve(uv + float2( du, -dv)));
        neighbour = max(neighbour, SampleResolve(uv + float2(-du, -dv)));

        float grown = neighbour * _SpreadRetain;
        float painted = GazePaint(uv);

        float value = max(self * _Decay, max(painted, grown));

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
