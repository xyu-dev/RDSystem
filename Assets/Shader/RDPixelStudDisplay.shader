Shader "RDSystem/RDPixelStudDisplayDense"
{
    Properties
    {
        _MainTex("RD Texture", 2D) = "white" {}

        [Header(Colors)]
        _BackgroundColor("Background Color", Color) = (0.02, 0.02, 0.02, 1)
        _ForegroundColor("Foreground Color", Color) = (1.0, 0.88, 0.12, 1)
        _ShadowColor("Shadow Color", Color) = (0.22, 0.16, 0.02, 1)

        [Header(Dense Grid)]
        _GridCount("Grid Count", Float) = 288
        _Gap("Gap", Range(0,0.45)) = 0.035
        _Roundness("Roundness", Range(0.01,1)) = 0.35

        [Header(Value Mapping)]
        _Threshold("Threshold", Range(0,1)) = 0.18
        _Softness("Threshold Softness", Range(0.001,0.2)) = 0.025
        _Levels("Posterize Levels", Range(2,6)) = 3
        _Gain("Value Gain", Range(0,4)) = 1.7

        [Header(Relief)]
        _Height("Height", Range(0,1)) = 0.12
        _EdgeDarken("Edge Darken", Range(0,2)) = 0.30
        _AO("Cell AO", Range(0,2)) = 0.15
        _NormalStrength("Normal Strength", Range(0,2)) = 0.65

        [Header(Specular)]
        _SpecColor("Specular Color", Color) = (1,1,1,1)
        _SpecPower("Specular Power", Range(1,128)) = 64
        _SpecIntensity("Specular Intensity", Range(0,3)) = 0.24

        [Header(Fresnel)]
        _FresnelColor("Fresnel Color", Color) = (1.0, 0.95, 0.55, 1)
        _FresnelPower("Fresnel Power", Range(0.1,8)) = 3.5
        _FresnelIntensity("Fresnel Intensity", Range(0,2)) = 0.08

        [Header(Cell Shape)]
        _UseCircleStud("Use Circle Stud", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 250

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;

            float4 _BackgroundColor;
            float4 _ForegroundColor;
            float4 _ShadowColor;

            float _GridCount;
            float _Gap;
            float _Roundness;

            float _Threshold;
            float _Softness;
            float _Levels;
            float _Gain;

            float _Height;
            float _EdgeDarken;
            float _AO;
            float _NormalStrength;

            float4 _SpecColor;
            float _SpecPower;
            float _SpecIntensity;

            float4 _FresnelColor;
            float _FresnelPower;
            float _FresnelIntensity;

            float _UseCircleStud;

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

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);

                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = normalize(UnityObjectToWorldNormal(v.normal));
                o.worldTangent = normalize(mul((float3x3)unity_ObjectToWorld, v.tangent.xyz));
                o.worldBinormal = normalize(cross(o.worldNormal, o.worldTangent) * v.tangent.w);
                o.viewDir = normalize(_WorldSpaceCameraPos.xyz - o.worldPos);
                return o;
            }

            float Posterize(float x, float levels)
            {
                levels = max(2.0, levels);
                return floor(x * levels) / (levels - 1.0);
            }

            float CellMaskSquare(float2 localUV, float gap, float roundness)
            {
                float2 p = abs(localUV - 0.5) * 2.0;

                float box = max(p.x, p.y);
                float corner = length(max(p - (1.0 - roundness), 0.0));
                float shape = max(box * (1.0 - roundness), corner);

                float edge = 1.0 - gap;
                float soft = 0.01;
                return 1.0 - smoothstep(edge - soft, edge + soft, shape);
            }

            float CellMaskCircle(float2 localUV, float gap)
            {
                float2 p = (localUV - 0.5) * 2.0;
                float d = length(p);
                float edge = 1.0 - gap;
                float soft = 0.012;
                return 1.0 - smoothstep(edge - soft, edge + soft, d);
            }

            float3 CellNormalSquare(float2 localUV, float height)
            {
                float2 p = (localUV - 0.5) * 2.0;

                float2 ap = abs(p);
                float2 s = sign(p);

                float bevelWidth = 0.35;
                float2 bevel = saturate((ap - (1.0 - bevelWidth)) / bevelWidth);

                float2 grad = s * bevel * height;
                return normalize(float3(-grad.x, -grad.y, 1.0));
            }

            float3 CellNormalCircle(float2 localUV, float height)
            {
                float2 p = (localUV - 0.5) * 2.0;
                float r = length(p);
                float2 dir = (r > 0.0001) ? normalize(p) : float2(0, 0);

                float bevel = smoothstep(0.2, 1.0, r);
                float slope = bevel * height;
                return normalize(float3(-dir.x * slope, -dir.y * slope, 1.0));
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;

                float2 gridUV = uv * _GridCount;
                float2 cellId = floor(gridUV);
                float2 localUV = frac(gridUV);

                float2 sampleUV = (cellId + 0.5) / _GridCount;
                float rd = tex2D(_MainTex, sampleUV).g;
                rd = saturate(rd * _Gain);

                float on = smoothstep(_Threshold - _Softness, _Threshold + _Softness, rd);
                float stepped = Posterize(on, _Levels);
                stepped = saturate(stepped);

                float squareMask = CellMaskSquare(localUV, _Gap, _Roundness);
                float circleMask = CellMaskCircle(localUV, _Gap);
                float studMask = lerp(squareMask, circleMask, saturate(_UseCircleStud));

                float3 nSquare = CellNormalSquare(localUV, _Height);
                float3 nCircle = CellNormalCircle(localUV, _Height);
                float3 localStudNormal = normalize(lerp(nSquare, nCircle, saturate(_UseCircleStud)));

                float3x3 tbn = float3x3(
                    normalize(i.worldTangent),
                    normalize(i.worldBinormal),
                    normalize(i.worldNormal)
                );

                float emboss = stepped * studMask;

                float3 worldStudNormal = normalize(mul(tbn, localStudNormal));
                float3 N = normalize(lerp(i.worldNormal, worldStudNormal, emboss * _NormalStrength));

                float3 V = normalize(i.viewDir);
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float3 H = normalize(L + V);

                float ndl = saturate(dot(N, L));
                float ndh = saturate(dot(N, H));
                float fresnel = pow(1.0 - saturate(dot(N, V)), _FresnelPower);

                float ao = lerp(1.0 - _AO, 1.0, studMask);
                float edgeShade = lerp(1.0 - _EdgeDarken, 1.0, studMask);

                float3 baseCol = lerp(_BackgroundColor.rgb, _ForegroundColor.rgb, emboss);
                baseCol = lerp(_ShadowColor.rgb, baseCol, ao * edgeShade);

                float lighting = 0.28 + 0.72 * ndl;
                float3 col = baseCol * lighting;

                float spec = pow(ndh, _SpecPower) * _SpecIntensity * emboss;
                col += _SpecColor.rgb * spec;
                col += _FresnelColor.rgb * fresnel * _FresnelIntensity * emboss;

                return float4(saturate(col), 1.0);
            }
            ENDHLSL
        }
    }
}