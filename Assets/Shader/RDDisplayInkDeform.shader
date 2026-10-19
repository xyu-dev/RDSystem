Shader "RDSystem/RDDisplayInkDeform"
{
    // The pixel ink display, with the surface pushed outward where the viewer has looked.
    //
    // Space grows locally and visibly, under the gaze, and relaxes when attention moves on. This
    // is the honest version of "the longer you look, the bigger the space": a sphere seen from its
    // own centre has no apparent size, so uniform expansion cannot be perceived at all - but a
    // sphere that DENTS has readable shape, and the dent parallaxes against the undeformed surface
    // around it the moment the viewer's head moves.
    //
    // The displacement is radial, so it does not disturb the cell grid: the grid is built from the
    // direction, and moving a point along its own normal leaves its direction unchanged. The
    // pixels stay put and the surface moves underneath them, which is what makes the dent read as
    // depth rather than as a smear.

    Properties
    {
        _MainTex("RD Texture", 2D) = "white" {}
        _DepthTex("Gaze Depth Field", 2D) = "black" {}

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

        [Header(Deformation)]
        _DeformMetres("Displacement At Full Depth (metres)", Range(0, 12)) = 2.5
        _DepthGain("Depth Gain", Range(0, 4)) = 1.4

        [Header(Gaze Bloom (works with zero displacement))]
        _ReliefMetres("Relief Height (metres)", Range(0, 12)) = 2.5
        _DepthRelief("Relief Strength", Range(0, 2)) = 0.8
        _DepthValueShift("Dent Value Shift", Range(-0.5, 0.5)) = 0.10
        _DepthBloom("Bloom Towards Colour", Range(0, 1)) = 0
        _DepthBloomColor("Bloom Colour", Color) = (0.62, 0.80, 1.0, 1)

        [Header(Pixelation)]
        _Pixelate("Pixelate (0 smooth, 1 cells)", Range(0, 1)) = 0
        _BaseRadius("Sphere Radius (metres)", Range(0.5, 200)) = 10
        _CellWorldSize("Cell Size (metres on the surface)", Range(0.01, 1)) = 0.24
        _MinCellArcDegrees("Minimum Cell Arc (degrees)", Range(0.05, 4)) = 0.2
        _Levels("Colour Levels", Range(2, 64)) = 12
        _EdgeSoftness("Cell Edge Softness (pixels)", Range(0, 3)) = 1

        [Header(Debug)]
        _ShowDepth("Show Depth Field", Range(0,1)) = 0
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
            Name "InkDeform"
            Tags { "LightMode" = "UniversalForward" }

            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            TEXTURE2D(_DepthTex);
            SAMPLER(sampler_DepthTex);

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

                float _DeformMetres;
                float _DepthGain;
                float _ReliefMetres;
                float _DepthRelief;
                float _DepthValueShift;
                float _DepthBloom;
                float4 _DepthBloomColor;

                float _Pixelate;
                float _BaseRadius;
                float _CellWorldSize;
                float _MinCellArcDegrees;
                float _Levels;
                float _EdgeSoftness;

                float _ShowDepth;
            CBUFFER_END

            #define RD_PI 3.14159265359

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 dirOS      : TEXCOORD1;
                float  depth      : TEXCOORD2;
            };

            float2 SphereDirToUV(float3 dir)
            {
                dir = normalize(dir);

                float theta = acos(clamp(dir.y, -1.0, 1.0));
                float phi = atan2(dir.z, dir.x);
                if (phi < 0.0) phi += 2.0 * RD_PI;

                return float2(phi / (2.0 * RD_PI), theta / RD_PI);
            }

            Varyings vert(Attributes v)
            {
                Varyings o;

                // The mesh UV is already equirectangular, and it is the same UV hit.textureCoord
                // returns, so the dent lands exactly where the gaze was.
                float depth = SAMPLE_TEXTURE2D_LOD(_DepthTex, sampler_DepthTex, v.uv, 0).r;
                depth = saturate(depth * _DepthGain);

                float3 displaced = v.positionOS.xyz + normalize(v.normalOS) * depth * _DeformMetres;

                o.positionCS = TransformObjectToHClip(displaced);

                // Direction from the UNDISPLACED position: radial displacement cannot change a
                // direction, but normalising the displaced point costs the same and says less.
                o.dirOS = normalize(v.positionOS.xyz);
                o.uv = v.uv;
                o.depth = depth;

                return o;
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

            /// levels below 2 means no quantisation at all - the smooth exhibition ramp.
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

            /// Relief shading from the gradient of the depth field.
            ///
            /// This is the thing the pixel version could not have: with no flat cells to protect,
            /// the dent can be given real form shading, so it reads as a bowl even when the
            /// viewer's head is completely still. Parallax only exists while someone is moving,
            /// and in a chair that is most of the time not.
            ///
            /// The gradient has to be converted from per-UV to per-metre, and the two axes scale
            /// differently: a step in u covers 2*pi*R*sin(theta) of arc, a step in v covers pi*R.
            /// That is the same anisotropy the simulation stencil deals with, for the same reason.
            float ReliefShade(float2 uv, float radius)
            {
                float texel = 1.0 / 512.0;      // the depth buffer is 512 x 256

                float hE = SAMPLE_TEXTURE2D_LOD(_DepthTex, sampler_DepthTex, uv + float2(texel, 0), 0).r;
                float hW = SAMPLE_TEXTURE2D_LOD(_DepthTex, sampler_DepthTex, uv - float2(texel, 0), 0).r;
                float hN = SAMPLE_TEXTURE2D_LOD(_DepthTex, sampler_DepthTex, uv + float2(0, texel), 0).r;
                float hS = SAMPLE_TEXTURE2D_LOD(_DepthTex, sampler_DepthTex, uv - float2(0, texel), 0).r;

                float sinTheta = max(sin(uv.y * RD_PI), 0.08);

                // Arc length per unit UV, so the gradient comes out per metre.
                float dsdu = 2.0 * RD_PI * radius * sinTheta;
                float dsdv = RD_PI * radius;

                // _ReliefMetres, not _DeformMetres, so the shading works with the surface held
                // completely flat. The bloom spreading out from the gaze is the part worth having
                // on its own; the displacement is a separate decision.
                float gx = ((hE - hW) * 0.5 / texel) * _ReliefMetres * _DepthGain / max(0.01, dsdu);
                float gy = ((hN - hS) * 0.5 / texel) * _ReliefMetres * _DepthGain / max(0.01, dsdv);

                float3 n = normalize(float3(-gx, -gy, 1.0));

                // A fixed light in the local tangent frame, not a scene light: the display is
                // unlit by design and this has to behave the same wherever the viewer looks.
                float3 l = normalize(float3(0.42, 0.56, 0.72));

                return dot(n, l);
            }

            half4 frag(Varyings i) : SV_Target
            {
                if (_ShowDepth > 0.5)
                {
                    float d = SAMPLE_TEXTURE2D(_DepthTex, sampler_DepthTex, i.uv).r;
                    return half4(d, d * 0.35, 1.0 - d, 1.0);
                }

                // Cells in world units against the UNDEFORMED radius, so the grid does not breathe
                // as the dent moves. The dent is depth, not resolution.
                float minArc = radians(max(0.01, _MinCellArcDegrees));
                float arc = max(_CellWorldSize / max(0.01, _BaseRadius), minArc);

                float cells = clamp(floor((RD_PI * 0.5) / arc), 2.0, 4096.0);

                float depth = saturate(i.depth);

                float vSmooth = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).g;

                // Shade in VALUE space, not in colour space.
                //
                // Multiplying the output colour was wrong: the background is 0.97 white, and any
                // multiplicative darkening of near-white is grey. Grey is not in this palette at
                // all, so the relief came out as dirty smudges over an ink wash.
                //
                // Pushing the value through the ink ramp instead means every pixel the shader can
                // produce is a colour the ramp already contains - white, blue, or near-black. The
                // relief reads as the ink pooling and thinning, which is also the right metaphor
                // for a wash, and grey is now unreachable by construction.
                if (_DepthRelief > 0.001)
                {
                    float shade = ReliefShade(i.uv, max(0.01, _BaseRadius));
                    vSmooth += (shade - 0.56) * _DepthRelief * 0.25;
                }

                vSmooth += depth * _DepthValueShift;

                float3 col = Ink(saturate(vSmooth), 0.0);

                // Only pay for the cells when they are actually wanted. Each cell tap costs an
                // acos and an atan2 to reach the equirectangular buffer, so skipping the four of
                // them in smooth mode is most of this shader's cost.
                if (_Pixelate > 0.001)
                {
                    float3 d = normalize(i.dirOS);

                    float2 st;
                    int face = CubeFace(d, st);

                    float2 g = (st * 0.5 + 0.5) * cells;

                    float anglePerPixel = length(float3(fwidth(d.x), fwidth(d.y), fwidth(d.z)));
                    float cellAngle = (RD_PI * 0.5) / cells;
                    float e = clamp(anglePerPixel / max(1e-6, cellAngle) * _EdgeSoftness, 0.0, 0.5);

                    float3 celled;

                    if (e < 0.002)
                    {
                        celled = CellColour(face, floor(g) + 0.5, cells);
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

                        celled = lerp(lerp(c00, c10, w.x), lerp(c01, c11, w.x), w.y);
                    }

                    col = lerp(col, celled, saturate(_Pixelate));
                }

                // An explicit wash towards a chosen colour, driven by the raw field. The one place
                // a colour outside the ramp is allowed, because here it is a deliberate choice
                // rather than a by-product of darkening.
                col = lerp(col, _DepthBloomColor.rgb, depth * _DepthBloom);

                return half4(saturate(col), 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
