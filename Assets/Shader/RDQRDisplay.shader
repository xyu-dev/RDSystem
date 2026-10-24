Shader "RDSystem/RDQRDisplay"
{
    // A reaction-diffusion field rendered as a field of QR codes.
    //
    // The surface is divided into square patches; each patch carries the real structural
    // furniture of a QR symbol - three finder squares, their separators, the timing lines,
    // an alignment square, the dark module - and the remaining data area is filled from the
    // simulation. Because the data area is dithered rather than thresholded, the modules read
    // as machine-random while their density still carries the pattern the viewer's gaze made.
    //
    // Nothing here encodes anything. It is scannable-looking and unscannable, which is the point.
    //
    // Deliberately unlit: a QR code is printed, not lit. No specular, no fresnel, no bevel.

    Properties
    {
        _MainTex("RD Texture", 2D) = "white" {}

        [Header(Colors)]
        _BackgroundColor("Quiet / Light Module", Color) = (0.03, 0.03, 0.035, 1)
        _ForegroundColor("Dark Module", Color) = (0.93, 0.93, 0.90, 1)

        [Header(Code Layout)]
        _PatchesX("Patches Around (U)", Float) = 8
        _PatchesY("Patches Over (V)", Float) = 4
        _ModulesPerPatch("Modules Per Patch Edge", Range(21, 77)) = 25
        _QuietZone("Quiet Zone (fraction of patch)", Range(0, 0.25)) = 0.08

        [Header(Value Mapping)]
        _Gain("Value Gain", Range(0, 4)) = 1.7
        _Threshold("Threshold", Range(0, 1)) = 0.35
        _Dither("Dither (0 blobs, 1 QR noise)", Range(0, 1)) = 0.75
        _MaskPattern("QR Mask Pattern (8 = off)", Range(0, 8)) = 8

        [Header(Module Shape)]
        _ModuleGap("Module Gap", Range(0, 0.4)) = 0
        _Sharpness("Edge Sharpness", Range(0, 1)) = 1

        [Header(Debug)]
        _ShowStructureOnly("Show Structure Only", Float) = 0
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
            Name "QRUnlit"
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

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;

                float4 _BackgroundColor;
                float4 _ForegroundColor;

                float _PatchesX;
                float _PatchesY;
                float _ModulesPerPatch;
                float _QuietZone;

                float _Gain;
                float _Threshold;
                float _Dither;
                float _MaskPattern;

                float _ModuleGap;
                float _Sharpness;

                float _ShowStructureOnly;
            CBUFFER_END

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

            // ---------------------------------------------------------------- QR structure

            // 7x7 finder: black ring, white ring, 3x3 black core. p in [0,6].
            float FinderCell(int2 p)
            {
                int2 d = abs(p - 3);
                int cheb = max(d.x, d.y);
                return (cheb == 3 || cheb <= 1) ? 1.0 : 0.0;
            }

            // 5x5 alignment: black ring, white ring, single black centre. p in [0,4].
            float AlignmentCell(int2 p)
            {
                int2 d = abs(p - 2);
                int cheb = max(d.x, d.y);
                return (cheb == 2 || cheb == 0) ? 1.0 : 0.0;
            }

            // Returns 1 = forced dark, 0 = forced light, -1 = free data area.
            float QRReserved(int2 m, int n)
            {
                // Top-left finder, with its 1-module separator making an 8x8 block.
                if (m.x < 8 && m.y < 8)
                    return (m.x < 7 && m.y < 7) ? FinderCell(m) : 0.0;

                // Top-right finder.
                if (m.x >= n - 8 && m.y < 8)
                {
                    int2 q = int2(m.x - (n - 7), m.y);
                    return (q.x >= 0 && q.y < 7) ? FinderCell(q) : 0.0;
                }

                // Bottom-left finder.
                if (m.x < 8 && m.y >= n - 8)
                {
                    int2 q = int2(m.x, m.y - (n - 7));
                    return (q.x < 7 && q.y >= 0) ? FinderCell(q) : 0.0;
                }

                // Alignment square, inset 4 modules from the bottom-right corner.
                int2 a = m - int2(n - 9, n - 9);
                if (a.x >= 0 && a.x < 5 && a.y >= 0 && a.y < 5)
                    return AlignmentCell(a);

                // Timing lines: row 6 and column 6, alternating, between the finders.
                if (m.y == 6 && m.x >= 8 && m.x < n - 8)
                    return ((m.x % 2) == 0) ? 1.0 : 0.0;

                if (m.x == 6 && m.y >= 8 && m.y < n - 8)
                    return ((m.y % 2) == 0) ? 1.0 : 0.0;

                // The dark module, always set, just under the top-left finder.
                if (m.x == 8 && m.y == n - 8)
                    return 1.0;

                return -1.0;
            }

            // The eight real QR mask patterns. Returns 1 where the data bit is inverted.
            float QRMask(int2 m, int pattern)
            {
                int i = m.y;    // row
                int j = m.x;    // column

                if (pattern == 0) return ((i + j) % 2 == 0) ? 1.0 : 0.0;
                if (pattern == 1) return (i % 2 == 0) ? 1.0 : 0.0;
                if (pattern == 2) return (j % 3 == 0) ? 1.0 : 0.0;
                if (pattern == 3) return ((i + j) % 3 == 0) ? 1.0 : 0.0;
                if (pattern == 4) return (((i / 2) + (j / 3)) % 2 == 0) ? 1.0 : 0.0;
                if (pattern == 5) return (((i * j) % 2 + (i * j) % 3) == 0) ? 1.0 : 0.0;
                if (pattern == 6) return ((((i * j) % 2 + (i * j) % 3) % 2) == 0) ? 1.0 : 0.0;
                if (pattern == 7) return ((((i + j) % 2 + (i * j) % 3) % 2) == 0) ? 1.0 : 0.0;

                return 0.0;
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(127.1, 311.7));
                p += dot(p, p + 34.45);
                return frac(p.x * p.y * 95.4307);
            }

            // ---------------------------------------------------------------- fragment

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 patches = float2(max(1.0, _PatchesX), max(1.0, _PatchesY));
                float n = floor(max(21.0, _ModulesPerPatch));

                float2 patchUV = i.uv * patches;
                float2 patchId = floor(patchUV);
                float2 inPatch = frac(patchUV);

                // Quiet zone: the code occupies the middle of the patch, the margin stays light.
                float quiet = clamp(_QuietZone, 0.0, 0.24);
                float span = 1.0 - 2.0 * quiet;
                float2 codeUV = (inPatch - quiet) / span;

                if (any(codeUV < 0.0) || any(codeUV > 1.0))
                    return half4(_BackgroundColor.rgb, 1.0);

                float2 g = codeUV * n;
                int2 m = (int2)floor(g);
                m = clamp(m, 0, (int)n - 1);
                float2 local = frac(g);

                // Sample the field at this module's own centre, so every module is one flat value.
                // Explicit LOD 0 is required: sampleUV jumps between modules, so the hardware's
                // own derivatives would pick a high mip and wash the whole surface to grey.
                float2 centreInPatch = quiet + (((float2)m + 0.5) / n) * span;
                float2 sampleUV = (patchId + centreInPatch) / patches;
                float rd = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, sampleUV, 0).g;
                float v = saturate(rd * _Gain);

                // Dither against a per-module hash. At _Dither = 0 this is a plain threshold and
                // the pattern reads as blobs; at 1 the modules look machine-random and only their
                // density carries the pattern. That density-not-shape reading is the QR look.
                float noise = Hash21((float2)m + patchId * 97.0);
                float cut = lerp(_Threshold, noise, saturate(_Dither));
                float data = step(cut, v);

                int pattern = (int)floor(clamp(_MaskPattern, 0.0, 8.0));
                if (pattern < 8)
                    data = abs(data - QRMask(m, pattern));

                float reserved = QRReserved(m, (int)n);
                float on = (reserved >= 0.0) ? reserved : data;

                if (_ShowStructureOnly > 0.5)
                    on = (reserved >= 0.0) ? reserved : 0.0;

                // Module shape. Gap 0 gives flush modules, which is what a printed code looks like.
                float cell = 1.0;
                if (_ModuleGap > 0.0001)
                {
                    float2 p = abs(local - 0.5) * 2.0;
                    float edge = 1.0 - _ModuleGap;
                    float soft = lerp(0.25, 0.01, saturate(_Sharpness));
                    cell = 1.0 - smoothstep(edge - soft, edge + soft, max(p.x, p.y));
                }

                float3 col = lerp(_BackgroundColor.rgb, _ForegroundColor.rgb, on * cell);
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
