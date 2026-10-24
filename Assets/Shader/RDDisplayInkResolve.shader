Shader "RDSystem/RDDisplayInkResolve"
{
    // Scene 1. The surface starts pixelated and resolves to smooth where the viewer has looked.
    //
    // Each cell is either pixel or smooth, flipping as a unit, so the front advances cell by cell
    // and reads as resolution arriving rather than as a blur sweeping across. A per-cell hash
    // offsets the threshold slightly so the front has a ragged edge instead of a perfect circle.
    //
    // Level raises _CellsPerFace, so finishing a level hands the viewer a finer grid to resolve
    // from scratch. The reward for completing it is that it gets harder.

    Properties
    {
        _MainTex("RD Texture", 2D) = "white" {}
        _ResolveTex("Resolve Field", 2D) = "black" {}

        [Header(Background)]
        _BackgroundColor("Background Color", Color) = (0.97, 0.97, 0.97, 1)

        [Header(Core)]
        _CoreColor("Core Color", Color) = (0.01, 0.03, 0.09, 1)
        _CoreThreshold("Core Threshold", Range(0,1)) = 0.40
        _CoreSoftness("Core Softness", Range(0.001,0.3)) = 0.05

        [Header(Halo)]
        _HaloColor("Halo Color", Color) = (0.45, 0.70, 1.00, 1)
        _HaloThreshold("Halo Threshold", Range(0,1)) = 0.14
        _HaloSoftness("Halo Softness", Range(0.001,0.5)) = 0.16
        _HaloIntensity("Halo Intensity", Range(0,3)) = 1.15

        [Header(Contrast)]
        _ValuePower("Value Power", Range(0.2,4)) = 1.3
        _ValueGain("Value Gain", Range(0,3)) = 1.4

        [Header(Pixelation)]
        _CellsPerFace("Cells Per Cube Face Edge", Range(8, 512)) = 64
        _Levels("Colour Levels", Range(2, 64)) = 12
        _EdgeSoftness("Cell Edge Softness (pixels)", Range(0, 3)) = 1

        [Header(Resolve Front)]
        _ResolveThreshold("Resolve Threshold", Range(0,1)) = 0.30
        _ResolveJitter("Front Raggedness", Range(0,0.5)) = 0.12
        _ShowFront("Tint The Front", Range(0,1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "InkResolve"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            // XR single-pass instanced: both eyes come out of one draw, and without these the
            // shader only ever writes eye 0 - the right lens stays black.
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            TEXTURE2D(_ResolveTex);
            SAMPLER(sampler_ResolveTex);

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

                float _CellsPerFace;
                float _Levels;
                float _EdgeSoftness;

                float _ResolveThreshold;
                float _ResolveJitter;
                float _ShowFront;
            CBUFFER_END

            #define RD_PI 3.14159265359

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;

                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;

                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
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

            float2 SphereDirToUV(float3 dir)
            {
                dir = normalize(dir);

                float theta = acos(clamp(dir.y, -1.0, 1.0));
                float phi = atan2(dir.z, dir.x);
                if (phi < 0.0) phi += 2.0 * RD_PI;

                return float2(phi / (2.0 * RD_PI), theta / RD_PI);
            }

            int CubeFace(float3 d, out float2 st)
            {
                float3 a = abs(d);

                if (a.x >= a.y && a.x >= a.z)
                {
                    st = float2(d.y, d.z) / max(1e-6, a.x);
                    return d.x > 0.0 ? 0 : 1;
                }

                if (a.y >= a.z)
                {
                    st = float2(d.x, d.z) / max(1e-6, a.y);
                    return d.y > 0.0 ? 2 : 3;
                }

                st = float2(d.x, d.y) / max(1e-6, a.z);
                return d.z > 0.0 ? 4 : 5;
            }

            float3 CubeFaceDir(int face, float2 st)
            {
                if (face == 0) return normalize(float3( 1.0, st.x, st.y));
                if (face == 1) return normalize(float3(-1.0, st.x, st.y));
                if (face == 2) return normalize(float3(st.x,  1.0, st.y));
                if (face == 3) return normalize(float3(st.x, -1.0, st.y));
                if (face == 4) return normalize(float3(st.x, st.y,  1.0));
                return                normalize(float3(st.x, st.y, -1.0));
            }

            float Quantise(float x, float levels)
            {
                float l = max(2.0, floor(levels));
                return floor(saturate(x) * (l - 1.0) + 0.5) / (l - 1.0);
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(127.1, 311.7));
                p += dot(p, p + 34.45);
                return frac(p.x * p.y * 95.4307);
            }

            /// The shared ink ramp. quantise = 0 gives the smooth exhibition look; above 0 it
            /// bands into that many levels.
            float3 Ink(float v, float levels)
            {
                float value = pow(saturate(v * _ValueGain), _ValuePower);

                if (levels >= 2.0)
                    value = Quantise(value, levels);

                float halo = smoothstep(_HaloThreshold - _HaloSoftness,
                                        _HaloThreshold + _HaloSoftness, value);

                float core = smoothstep(_CoreThreshold - _CoreSoftness,
                                        _CoreThreshold + _CoreSoftness, value);

                float3 col = _BackgroundColor.rgb;
                col = lerp(col, _HaloColor.rgb, saturate(halo * _HaloIntensity * 0.75));
                col = lerp(col, _CoreColor.rgb, core);

                return col;
            }

            float3 CellColour(int face, float2 cellCentre, float cells)
            {
                float2 clamped = clamp(cellCentre, 0.5, cells - 0.5);
                float2 st = (clamped / cells) * 2.0 - 1.0;

                float2 uv = SphereDirToUV(CubeFaceDir(face, st));
                float v = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv, 0).g;

                return Ink(v, _Levels);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float cells = max(2.0, floor(_CellsPerFace));

                float3 d = SphereUVToDir(i.uv);

                float2 st;
                int face = CubeFace(d, st);

                float2 g = (st * 0.5 + 0.5) * cells;

                float anglePerPixel = length(float3(fwidth(d.x), fwidth(d.y), fwidth(d.z)));
                float cellAngle = (RD_PI * 0.5) / cells;
                float e = clamp(anglePerPixel / max(1e-6, cellAngle) * _EdgeSoftness, 0.0, 0.5);

                // ---- the pixel image -----------------------------------------

                float3 pixelCol;

                if (e < 0.002)
                {
                    pixelCol = CellColour(face, floor(g) + 0.5, cells);
                }
                else
                {
                    float2 base = floor(g - 0.5);
                    float2 fr = (g - 0.5) - base;
                    float2 w = smoothstep(0.5 - e, 0.5 + e, fr);

                    float3 c00 = CellColour(face, base + float2(0.5, 0.5), cells);
                    float3 c10 = CellColour(face, base + float2(1.5, 0.5), cells);
                    float3 c01 = CellColour(face, base + float2(0.5, 1.5), cells);
                    float3 c11 = CellColour(face, base + float2(1.5, 1.5), cells);

                    pixelCol = lerp(lerp(c00, c10, w.x), lerp(c01, c11, w.x), w.y);
                }

                // ---- the smooth image ----------------------------------------

                float vSmooth = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).g;
                float3 smoothCol = Ink(vSmooth, 0.0);

                // ---- which one this cell is ----------------------------------

                // Read the resolve field at the CELL centre, so a cell flips as a unit. Reading it
                // per fragment would leave cells half-resolved, which just looks like a smudge.
                float2 cellId = floor(g);
                float2 centreSt = ((clamp(cellId, 0.0, cells - 1.0) + 0.5) / cells) * 2.0 - 1.0;
                float2 centreUV = SphereDirToUV(CubeFaceDir(face, centreSt));

                float resolve = SAMPLE_TEXTURE2D_LOD(_ResolveTex, sampler_ResolveTex, centreUV, 0).r;

                // Offset each cell's threshold a little so the front is ragged, not a circle.
                float jitter = (Hash21(cellId + float2(face * 17.0, face * 31.0)) - 0.5) * _ResolveJitter;
                float crossed = step(_ResolveThreshold + jitter, resolve);

                float3 col = lerp(pixelCol, smoothCol, crossed);

                if (_ShowFront > 0.001)
                {
                    // Debug: tint cells that are resolved, so the front is unmistakable.
                    col = lerp(col, float3(0.1, 0.9, 0.5), crossed * _ShowFront * 0.35);
                }

                return half4(saturate(col), 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
