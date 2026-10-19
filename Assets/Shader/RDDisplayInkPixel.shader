Shader "RDSystem/RDDisplayInkPixel"
{
    // The exhibition ink display, pixelated.
    //
    // Same colour pipeline as RDSystem/RDDisplayInk - background, blue halo, dark core, driven
    // by pow(v * gain, power) through two smoothsteps - so the gaze response reads exactly as it
    // does now. What changes is that the value is sampled once per cell and held flat across it,
    // and the ramp is quantised into a small number of bands.
    //
    // The cells come from a CUBE grid in sphere-direction space, not from mesh UV. An
    // equirectangular UV grid converges to a point at the poles, so UV cells go wedge-shaped and
    // vanish there - the pole deformation, now in the pixels rather than the simulation. A cube
    // partition has no pole: cell area varies by about 1.7x between a face centre and a face
    // corner and that is the whole of it. The cost is that the grid orientation rotates across
    // the 12 cube edges, which inside a sphere room is rarely in view all at once.
    //
    // Direction comes from the mesh UV via SphereUVToDir, NOT from the object normal, so the grid
    // and the field live in the same space the gaze ring does regardless of how the model is
    // oriented.
    //
    // Fresnel and the fake specular from RDDisplayInk are gone on purpose. With the viewer at the
    // centre of the sphere, dot(N, V) is ~1 everywhere, so pow(1 - dot(N,V), 3) is a constant
    // brightness offset rather than shading - and a smooth gradient laid over flat cells is
    // exactly what destroys a pixel read.

    Properties
    {
        _MainTex("RD Texture", 2D) = "white" {}

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
        _CellsPerFace("Cells Per Cube Face Edge", Range(8, 256)) = 64
        _Levels("Colour Levels", Range(2, 64)) = 12
        _EdgeSoftness("Cell Edge Softness (pixels)", Range(0, 3)) = 1

        [Header(Debug)]
        _ShowCellGrid("Show Cell Grid", Float) = 0
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
            Name "InkPixelUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

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

                float _CellsPerFace;
                float _Levels;
                float _EdgeSoftness;

                float _ShowCellGrid;
            CBUFFER_END

            #define RD_PI 3.14159265359

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            // ---------------------------------------------------------- sphere space

            // Must match RDSystem.shader exactly, or the cells will not line up with the field.
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

            // ---------------------------------------------------------- cube partition

            // Which of the six faces the direction falls on, plus its position on that face in
            // [-1,1]. The axis pairings are chosen only to be self-consistent with CubeFaceDir
            // below; they do not need to match any hardware cubemap convention.
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

            // ---------------------------------------------------------- ink ramp

            float Quantise(float x, float levels)
            {
                float l = max(2.0, floor(levels));
                return floor(saturate(x) * (l - 1.0) + 0.5) / (l - 1.0);
            }

            /// The RDDisplayInk colour pipeline, evaluated for one cell. cellCentre is in cell
            /// units on the given face; it is clamped inside the face so the four taps used for
            /// edge softening never wander onto a neighbouring face.
            float3 CellColour(int face, float2 cellCentre, float cells)
            {
                float2 clamped = clamp(cellCentre, 0.5, cells - 0.5);
                float2 st = (clamped / cells) * 2.0 - 1.0;

                float2 uv = SphereDirToUV(CubeFaceDir(face, st));

                // Explicit LOD 0: the UV jumps from cell to cell, so the hardware's own
                // derivatives would pick a high mip and wash the surface out.
                float v = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv, 0).g;

                float value = pow(saturate(v * _ValueGain), _ValuePower);
                value = Quantise(value, _Levels);

                float halo = smoothstep(_HaloThreshold - _HaloSoftness,
                                        _HaloThreshold + _HaloSoftness, value);

                float core = smoothstep(_CoreThreshold - _CoreSoftness,
                                        _CoreThreshold + _CoreSoftness, value);

                float3 col = _BackgroundColor.rgb;
                col = lerp(col, _HaloColor.rgb, saturate(halo * _HaloIntensity * 0.75));
                col = lerp(col, _CoreColor.rgb, core);

                return col;
            }

            // ---------------------------------------------------------- fragment

            half4 frag(Varyings i) : SV_Target
            {
                float cells = max(2.0, floor(_CellsPerFace));

                float3 d = SphereUVToDir(i.uv);

                float2 st;
                int face = CubeFace(d, st);

                // Continuous cell coordinates on this face.
                float2 g = (st * 0.5 + 0.5) * cells;

                // Edge softening width, in cell units, derived from how fast the DIRECTION moves
                // per screen pixel. Taking it from the direction rather than from g matters: g is
                // discontinuous where the face index changes, so fwidth(g) would blow up and
                // smear a band along all 12 cube edges.
                float anglePerPixel = length(float3(fwidth(d.x), fwidth(d.y), fwidth(d.z)));
                float cellAngle = (RD_PI * 0.5) / cells;
                float cellsPerPixel = anglePerPixel / max(1e-6, cellAngle);
                float e = clamp(cellsPerPixel * _EdgeSoftness, 0.0, 0.5);

                // Blend the four nearest cells. At _EdgeSoftness = 0 the smoothstep collapses to
                // a step and this picks exactly one cell, giving a hard grid; above that the cell
                // boundary is antialiased over roughly that many screen pixels. MSAA cannot do
                // this for us - the edges are inside the triangle, not on its silhouette.
                float3 col;

                if (e < 0.002)
                {
                    // Hard grid: one cell, one tap. Each CellColour costs an acos and an atan2 to
                    // reach the equirectangular buffer, so taking this path is a 4x saving on the
                    // display pass - the lever to pull first if the surface costs frames.
                    col = CellColour(face, floor(g) + 0.5, cells);
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

                    col = lerp(lerp(c00, c10, w.x), lerp(c01, c11, w.x), w.y);
                }

                if (_ShowCellGrid > 0.5)
                {
                    // Not named "line": that is a reserved HLSL keyword and will not parse.
                    float2 p = abs(frac(g) - 0.5) * 2.0;
                    float interior = 1.0 - smoothstep(0.86, 0.98, max(p.x, p.y));
                    col = lerp(float3(1.0, 0.2, 0.2), col, interior);
                }

                return half4(saturate(col), 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
