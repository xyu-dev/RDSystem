Shader "RDSystem/RDHalftoneMetalDots"
{
    Properties
    {
        _MainTex("RD Texture", 2D) = "white" {}

        [Header(Base Colors)]
        _BackgroundColor("Background Color", Color) = (0.96, 0.96, 0.96, 1)
        _DotColor("Dot Color", Color) = (0.05, 0.05, 0.06, 1)
        _ShadowColor("Shadow Color", Color) = (0.18, 0.18, 0.20, 1)
        _HighlightColor("Highlight Color", Color) = (1, 1, 1, 1)

        [Header(Halftone Dots)]
        _DotDensity("Dot Density", Float) = 180
        _DotMinRadius("Dot Min Radius", Range(0.0, 0.45)) = 0.03
        _DotMaxRadius("Dot Max Radius", Range(0.0, 0.49)) = 0.34
        _DotSoftness("Dot Softness", Range(0.001, 0.15)) = 0.025
        _Gain("Value Gain", Range(0, 4)) = 1.5
        _Threshold("Threshold", Range(0,1)) = 0.12
        _Invert("Invert", Float) = 0

        [Header(Emboss)]
        _ExtrudeAmount("Extrude Amount", Range(0, 0.05)) = 0.008
        _NormalStrength("Normal Strength", Range(0, 3)) = 1.4
        _DomePower("Dome Power", Range(0.2, 4)) = 1.8
        _EdgeDarken("Edge Darken", Range(0, 2)) = 0.85
        _AO("AO", Range(0, 2)) = 0.35

        [Header(Metal Specular)]
        _Metalness("Metalness", Range(0,1)) = 0.85
        _Smoothness("Smoothness", Range(0,1)) = 0.92
        _SpecIntensity("Spec Intensity", Range(0,4)) = 1.4
        _SpecPower("Spec Power", Range(1,256)) = 110

        [Header(Fresnel)]
        _FresnelColor("Fresnel Color", Color) = (1, 1, 1, 1)
        _FresnelPower("Fresnel Power", Range(0.1,8)) = 4.2
        _FresnelIntensity("Fresnel Intensity", Range(0,3)) = 0.22

        [Header(Micro Highlight Dots)]
        _MicroDotDensity("Micro Dot Density", Float) = 520
        _MicroDotStrength("Micro Dot Strength", Range(0,2)) = 0.35
        _MicroDotSoftness("Micro Dot Softness", Range(0.001,0.1)) = 0.02
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 300

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            float4 _BackgroundColor;
            float4 _DotColor;
            float4 _ShadowColor;
            float4 _HighlightColor;

            float _DotDensity;
            float _DotMinRadius;
            float _DotMaxRadius;
            float _DotSoftness;
            float _Gain;
            float _Threshold;
            float _Invert;

            float _ExtrudeAmount;
            float _NormalStrength;
            float _DomePower;
            float _EdgeDarken;
            float _AO;

            float _Metalness;
            float _Smoothness;
            float _SpecIntensity;
            float _SpecPower;

            float4 _FresnelColor;
            float _FresnelPower;
            float _FresnelIntensity;

            float _MicroDotDensity;
            float _MicroDotStrength;
            float _MicroDotSoftness;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 tangent : TANGENT;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float3 worldNormal : TEXCOORD2;
                float3 worldTangent : TEXCOORD3;
                float3 worldBinormal : TEXCOORD4;
                float3 viewDir : TEXCOORD5;
            };

            float GetRDValue(float2 uv)
            {
                float v = tex2Dlod(_MainTex, float4(uv, 0, 0)).g;
                v = saturate(v * _Gain);
                v = smoothstep(_Threshold, 1.0, v);
                if (_Invert > 0.5) v = 1.0 - v;
                return v;
            }

            float DotMask(float2 localUV, float radius, float softness)
            {
                float2 p = localUV - 0.5;
                float d = length(p);
                return 1.0 - smoothstep(radius - softness, radius + softness, d);
            }

            float3 DomeNormal(float2 localUV, float radius, float domePower)
            {
                float2 p = (localUV - 0.5) / max(radius, 1e-4);
                float r = saturate(length(p));
                float2 dir = (r > 1e-5) ? normalize(p) : float2(0, 0);

                float dome = pow(r, domePower);
                float slope = dome;
                return normalize(float3(-dir.x * slope, -dir.y * slope, 1.0));
            }

            v2f vert(appdata v)
            {
                v2f o;

                float2 uv = TRANSFORM_TEX(v.uv, _MainTex);

                float2 gridUV = uv * _DotDensity;
                float2 cellId = floor(gridUV);
                float2 sampleUV = (cellId + 0.5) / _DotDensity;

                float rd = GetRDValue(sampleUV);
                float radius = lerp(_DotMinRadius, _DotMaxRadius, rd);

                // 点越大，顶点沿 normal 轻微鼓起越多
                float extrude = rd * _ExtrudeAmount;

                float3 localPos = v.vertex.xyz + v.normal * extrude;

                o.vertex = UnityObjectToClipPos(float4(localPos, 1.0));
                o.uv = uv;

                float4 world = mul(unity_ObjectToWorld, float4(localPos, 1.0));
                o.worldPos = world.xyz;
                o.worldNormal = normalize(UnityObjectToWorldNormal(v.normal));
                o.worldTangent = normalize(mul((float3x3)unity_ObjectToWorld, v.tangent.xyz));
                o.worldBinormal = normalize(cross(o.worldNormal, o.worldTangent) * v.tangent.w);
                o.viewDir = normalize(_WorldSpaceCameraPos.xyz - o.worldPos);

                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                float2 gridUV = uv * _DotDensity;
                float2 cellId = floor(gridUV);
                float2 localUV = frac(gridUV);
                float2 sampleUV = (cellId + 0.5) / _DotDensity;

                float rd = GetRDValue(sampleUV);
                float radius = lerp(_DotMinRadius, _DotMaxRadius, rd);

                float dotMask = DotMask(localUV, radius, _DotSoftness);

                // 半球/圆帽型局部法线
                float3 localN = DomeNormal(localUV, radius, _DomePower);

                float3x3 tbn = float3x3(
                    normalize(i.worldTangent),
                    normalize(i.worldBinormal),
                    normalize(i.worldNormal)
                );

                float3 bumpedN = normalize(mul(tbn, localN));
                float3 N = normalize(lerp(i.worldNormal, bumpedN, dotMask * _NormalStrength));

                float3 V = normalize(i.viewDir);
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float3 H = normalize(L + V);

                float ndl = saturate(dot(N, L));
                float ndh = saturate(dot(N, H));
                float ndv = saturate(dot(N, V));

                // 金属感更强的基底
                float ao = lerp(1.0 - _AO, 1.0, dotMask);
                float edgeShade = lerp(1.0 - _EdgeDarken, 1.0, dotMask);

                float3 baseCol = lerp(_BackgroundColor.rgb, _DotColor.rgb, dotMask);
                baseCol = lerp(_ShadowColor.rgb, baseCol, ao * edgeShade);

                float lighting = 0.20 + 0.80 * ndl;
                float3 col = baseCol * lighting;

                // 强金属高光
                float spec = pow(ndh, _SpecPower) * _SpecIntensity * dotMask;
                float3 metalSpec = lerp(_HighlightColor.rgb, _DotColor.rgb, _Metalness * 0.35);
                col += metalSpec * spec * lerp(0.4, 1.2, _Smoothness);

                // Fresnel 不要太大，但有边缘闪光
                float fresnel = pow(1.0 - ndv, _FresnelPower);
                col += _FresnelColor.rgb * fresnel * _FresnelIntensity * dotMask;

                // 更细密的小原点反光层
                float2 microGridUV = uv * _MicroDotDensity;
                float2 microLocal = frac(microGridUV);
                float2 microP = microLocal - 0.5;
                float microD = length(microP);

                float microRadius = 0.12 + 0.08 * spec;
                float microMask = 1.0 - smoothstep(
                    microRadius - _MicroDotSoftness,
                    microRadius + _MicroDotSoftness,
                    microD
                );

                col += _HighlightColor.rgb * microMask * spec * _MicroDotStrength * dotMask;

                return float4(saturate(col), 1.0);
            }
            ENDHLSL
        }
    }
}