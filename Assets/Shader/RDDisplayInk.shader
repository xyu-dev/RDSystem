Shader "RDSystem/RDDisplayInk"
{
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
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            sampler2D _MainTex;
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

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 viewDir : TEXCOORD2;
                float3 worldPos : TEXCOORD3;
                float4 vertex : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = normalize(_WorldSpaceCameraPos.xyz - o.worldPos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                // 使用 RD 的 V 通道作为显示值
                float v = tex2D(_MainTex, uv).g;

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
                float3 N = normalize(i.worldNormal);
                float3 V = normalize(i.viewDir);
                float fresnel = pow(1.0 - saturate(dot(N, V)), _FresnelPower);
                col += _FresnelColor.rgb * fresnel * _FresnelIntensity;

                // 一个简单假高光
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(N, H)), _SpecPower) * _SpecIntensity;
                col += _SpecColor.rgb * spec * 0.15;

                return float4(saturate(col), 1);
            }
            ENDHLSL
        }
    }
}