Shader "RDSystem/RDDisplayInk"
{
    // The smooth ink display: the original, with the fresnel rim and the fake specular that make
    // the sphere read as a surface rather than a projection.
    //
    // Ported from the built-in pipeline to URP. It used UnityCG.cginc, UnityObjectToClipPos and
    // tex2D, which still compile under URP - that is the trap. They compile, they render, and
    // they render to the left eye only, because a built-in-pipeline pass has no notion of the
    // instanced stereo pass Varjo runs. Pressing RD/Display/Use Smooth Ink therefore blinded the
    // right lens while reporting no error at all. The shading maths below is untouched; only the
    // pipeline plumbing changed.

    Properties
    {
        _MainTex("RD Texture", 2D) = "white" {}

        [Header(Background)]
        _BackgroundColor("Background Color", Color) = (0.96, 0.97, 0.98, 1)

        [Header(Core)]
        _CoreColor("Core Color", Color) = (0.02, 0.04, 0.10, 1)
        _CoreThreshold("Core Threshold", Range(0,1)) = 0.42
        _CoreSoftness("Core Softness", Range(0.001,0.3)) = 0.06

        [Header(Halo)]
        _HaloColor("Halo Color", Color) = (0.40, 0.68, 1.00, 1)
        _HaloThreshold("Halo Threshold", Range(0,1)) = 0.18
        _HaloSoftness("Halo Softness", Range(0.001,0.5)) = 0.18
        _HaloIntensity("Halo Intensity", Range(0,3)) = 1.1

        [Header(Contrast)]
        _ValuePower("Value Power", Range(0.2,4)) = 1.25
        _ValueGain("Value Gain", Range(0,3)) = 1.35

        [Header(Surface)]
        _FresnelColor("Fresnel Color", Color) = (0.85, 0.93, 1.0, 1)
        _FresnelPower("Fresnel Power", Range(0.1,8)) = 3.0
        _FresnelIntensity("Fresnel Intensity", Range(0,2)) = 0.2

        [Header(Specular Fake)]
        _SpecColor("Spec Color", Color) = (1,1,1,1)
        _SpecPower("Spec Power", Range(1,128)) = 36
        _SpecIntensity("Spec Intensity", Range(0,2)) = 0.18
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }
        LOD 200

        Pass
        {
            Name "Ink"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // XR single-pass instanced: both eyes come out of one draw, and without these the
            // shader only ever writes eye 0 - the right lens stays black.
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;

                float4 _BackgroundColor;

                float4 _CoreColor;
                float _CoreThreshold;
                float _CoreSoftness;

                float4 _HaloColor;
                float _HaloThreshold;
                float _HaloSoftness;
                float _HaloIntensity;

                float _ValuePower;
                float _ValueGain;

                float4 _FresnelColor;
                float _FresnelPower;
                float _FresnelIntensity;

                float4 _SpecColor;
                float _SpecPower;
                float _SpecIntensity;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;

                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv          : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 viewDirWS   : TEXCOORD2;
                float3 positionWS  : TEXCOORD3;
                float4 positionCS  : SV_POSITION;

                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);

                // GetWorldSpaceViewDir rather than a hand-rolled camera subtraction: under
                // instanced stereo each eye has its own camera position, and the built-in
                // _WorldSpaceCameraPos this used to read is only correct for one of them.
                o.viewDirWS = GetWorldSpaceViewDir(o.positionWS);

                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 uv = i.uv;

                // 使用 RD 的 V 通道作为显示值
                float v = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv).g;

                // 稍微增强对比，让细节更接近参考图
                float value = pow(saturate(v * _ValueGain), _ValuePower);

                // 外层蓝色扩散晕
                float halo = smoothstep(
                    _HaloThreshold - _HaloSoftness,
                    _HaloThreshold + _HaloSoftness,
                    value
                );

                // 内层深色核心
                float core = smoothstep(
                    _CoreThreshold - _CoreSoftness,
                    _CoreThreshold + _CoreSoftness,
                    value
                );

                float3 col = _BackgroundColor.rgb;

                // 先叠淡蓝晕染
                col = lerp(col, _HaloColor.rgb, halo * _HaloIntensity * 0.75);

                // 再叠深色核心
                col = lerp(col, _CoreColor.rgb, core);

                // Fresnel 让边缘更像球面材质
                float3 N = normalize(i.normalWS);
                float3 V = normalize(i.viewDirWS);
                float fresnel = pow(1.0 - saturate(dot(N, V)), _FresnelPower);
                col += _FresnelColor.rgb * fresnel * _FresnelIntensity;

                // 一个简单假高光
                float3 L = normalize(_MainLightPosition.xyz);
                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(N, H)), _SpecPower) * _SpecIntensity;
                col += _SpecColor.rgb * spec * 0.15;

                return half4(saturate(col), 1);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
