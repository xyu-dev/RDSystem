Shader "RDSystem/RDDisplayInkShell"
{
    // Scene 2. One translucent shell of the stack.
    //
    // The white end of the ink ramp is not white here - it is absent. Alpha comes from how far
    // the field rises above the background, so the quiet areas are holes and only the halo and
    // the core are present. Looking through a shell therefore means looking through the parts
    // nobody has grown yet.
    //
    // Cells are fixed in WORLD units, not in angle: _CellWorldSize is how many metres across one
    // cell is on the surface, so a larger shell carries more cells and each subtends less angle.
    // That is what makes a shell read as clearer the farther away it is - and it is the reason the
    // stack has to expand rather than just recolour. With cells fixed in angle (the parameter the
    // other display shaders take) growing the sphere changes nothing at all: the surface moves
    // away and the cells grow at exactly the rate that holds their angular size constant.

    Properties
    {
        _MainTex("RD Texture", 2D) = "white" {}

        [Header(Ink)]
        _CoreColor("Core Color", Color) = (0.01, 0.03, 0.09, 1)
        _CoreThreshold("Core Threshold", Range(0,1)) = 0.40
        _CoreSoftness("Core Softness", Range(0.001,0.3)) = 0.05

        _HaloColor("Halo Color", Color) = (0.45, 0.70, 1.00, 1)
        _HaloThreshold("Halo Threshold", Range(0,1)) = 0.14
        _HaloSoftness("Halo Softness", Range(0.001,0.5)) = 0.16
        _HaloIntensity("Halo Intensity", Range(0,3)) = 1.15

        [Header(Contrast)]
        _ValuePower("Value Power", Range(0.2,4)) = 1.3
        _ValueGain("Value Gain", Range(0,3)) = 1.4

        [Header(Transparency)]
        _HaloAlpha("Halo Alpha", Range(0,1)) = 0.42
        _CoreAlpha("Core Alpha", Range(0,1)) = 0.92
        _ShellFade("Shell Fade", Range(0,1)) = 1

        [Header(Pixelation (world units))]
        _CellWorldSize("Cell Size (metres on the surface)", Range(0.01, 1)) = 0.12
        _Radius("Shell Radius (metres)", Range(0.25, 200)) = 5
        _MinCellArcDegrees("Minimum Cell Arc (degrees)", Range(0.05, 2)) = 0.2
        _Levels("Colour Levels", Range(2, 64)) = 12
        _EdgeSoftness("Cell Edge Softness (pixels)", Range(0, 3)) = 1

        [Header(Identity)]
        _HueShift("Hue Shift", Range(0,1)) = 0
        _UVRotation("Pattern Rotation", Range(0,1)) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "InkShell"
            Tags { "LightMode" = "UniversalForward" }

            // Nested shells all share the viewer's position, so Unity's own transparent sorting
            // cannot order them - RDShellStack assigns an explicit render queue per shell instead,
            // largest radius first. See the note in that script.
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
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

                float4 _CoreColor;
                float _CoreThreshold;
                float _CoreSoftness;

                float4 _HaloColor;
                float _HaloThreshold;
                float _HaloSoftness;
                float _HaloIntensity;

                float _ValuePower;
                float _ValueGain;

                float _HaloAlpha;
                float _CoreAlpha;
                float _ShellFade;

                float _CellWorldSize;
                float _Radius;
                float _MinCellArcDegrees;
                float _Levels;
                float _EdgeSoftness;

                float _HueShift;
                float _UVRotation;
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

            float3 RgbToHsv(float3 c)
            {
                float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));

                float d = q.x - min(q.w, q.y);
                float e = 1.0e-10;

                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            float3 HsvToRgb(float3 c)
            {
                float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
                float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
                return c.z * lerp(K.xxx, saturate(p - K.xxx), c.y);
            }

            float3 ShiftHue(float3 rgb, float shift)
            {
                if (shift < 0.0005) return rgb;

                float3 hsv = RgbToHsv(rgb);
                hsv.x = frac(hsv.x + shift);
                return HsvToRgb(hsv);
            }

            /// Returns rgb in .xyz and alpha in .w. There is no background colour: the quiet end
            /// of the ramp is a hole, not white.
            float4 Ink(float v)
            {
                float value = pow(saturate(v * _ValueGain), _ValuePower);
                value = Quantise(value, _Levels);

                float halo = saturate(smoothstep(_HaloThreshold - _HaloSoftness,
                                                 _HaloThreshold + _HaloSoftness, value)
                                      * _HaloIntensity * 0.75);

                float core = smoothstep(_CoreThreshold - _CoreSoftness,
                                        _CoreThreshold + _CoreSoftness, value);

                float3 rgb = lerp(_HaloColor.rgb, _CoreColor.rgb, core);
                float a = saturate(max(halo * _HaloAlpha, core * _CoreAlpha));

                return float4(rgb, a);
            }

            float4 CellInk(int face, float2 cellCentre, float cells)
            {
                float2 clamped = clamp(cellCentre, 0.5, cells - 0.5);
                float2 st = (clamped / cells) * 2.0 - 1.0;

                float2 uv = SphereDirToUV(CubeFaceDir(face, st));
                uv.x = frac(uv.x + _UVRotation);

                float v = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, uv, 0).g;
                return Ink(v);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                // Cells in world units. A cell is _CellWorldSize metres across on the surface, so
                // its angular size is that over the radius - and a bigger shell is a finer image.
                float minArc = radians(max(0.01, _MinCellArcDegrees));
                float arc = max(_CellWorldSize / max(0.01, _Radius), minArc);

                float cells = clamp(floor((RD_PI * 0.5) / arc), 2.0, 4096.0);

                float3 d = SphereUVToDir(i.uv);

                float2 st;
                int face = CubeFace(d, st);

                float2 g = (st * 0.5 + 0.5) * cells;

                float anglePerPixel = length(float3(fwidth(d.x), fwidth(d.y), fwidth(d.z)));
                float cellAngle = (RD_PI * 0.5) / cells;
                float e = clamp(anglePerPixel / max(1e-6, cellAngle) * _EdgeSoftness, 0.0, 0.5);

                float4 ink;

                if (e < 0.002)
                {
                    ink = CellInk(face, floor(g) + 0.5, cells);
                }
                else
                {
                    float2 base = floor(g - 0.5);
                    float2 fr = (g - 0.5) - base;
                    float2 w = smoothstep(0.5 - e, 0.5 + e, fr);

                    float4 c00 = CellInk(face, base + float2(0.5, 0.5), cells);
                    float4 c10 = CellInk(face, base + float2(1.5, 0.5), cells);
                    float4 c01 = CellInk(face, base + float2(0.5, 1.5), cells);
                    float4 c11 = CellInk(face, base + float2(1.5, 1.5), cells);

                    ink = lerp(lerp(c00, c10, w.x), lerp(c01, c11, w.x), w.y);
                }

                float3 rgb = ShiftHue(ink.xyz, _HueShift);
                float alpha = ink.w * saturate(_ShellFade);

                return half4(rgb, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
