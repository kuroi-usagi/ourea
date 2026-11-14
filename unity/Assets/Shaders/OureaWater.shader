// Delta surface: satellite basemap + water from the handoff landwater masks.
// Mask values (handoff/CONTRACT.md): 255 land · 0 water · 128 no data · 200 rice flooded on purpose.
// _MaskA/_MaskB are two neighbouring passes, _Blend scrubs between them; _MaskBase is the first
// pass, so water that wasn't there at the start reads as flood (silty water + a glowing front).
Shader "Ourea/Water"
{
    Properties
    {
        _BaseMap ("Land colour (satellite)", 2D) = "white" {}
        _NormalMap ("World normals (RGB = xyz*0.5+0.5, from the DEM)", 2D) = "bump" {}
        _MaskA ("Landwater pass A", 2D) = "white" {}
        _MaskB ("Landwater pass B", 2D) = "white" {}
        _MaskBase ("Landwater first pass", 2D) = "white" {}
        _Blend ("Blend A to B", Range(0, 1)) = 0
        _MaskTexel ("Mask texel size", Float) = 0.00048828125
        _LandGain ("Land brightness", Range(0.5, 2)) = 1.15
        _WaterColor ("River water", Color) = (0.17, 0.24, 0.22, 1)
        _FloodColor ("Flood water", Color) = (0.30, 0.27, 0.19, 1)
        [HDR] _FloodGlow ("Flood glow", Color) = (0.05, 0.45, 0.9, 1)
        [HDR] _FrontGlow ("Flood front", Color) = (0.35, 1.3, 2.6, 1)
        _RiceColor ("Rice flooded on purpose", Color) = (0.30, 0.62, 0.55, 1)
        _NoDataColor ("No data", Color) = (0.40, 0.40, 0.42, 1)
        _SkyHorizon ("Sky horizon", Color) = (0.55, 0.62, 0.70, 1)
        _SkyZenith ("Sky zenith", Color) = (0.22, 0.34, 0.52, 1)
        _Ripple ("Ripple strength", Range(0, 1)) = 0.5
        _GlowAmount ("Data glow amount", Range(0, 1)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);   SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);
            TEXTURE2D(_MaskA);     SAMPLER(sampler_MaskA);
            TEXTURE2D(_MaskB);
            TEXTURE2D(_MaskBase);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _Blend, _MaskTexel;
                half _LandGain, _Ripple, _GlowAmount;
                half4 _WaterColor, _FloodColor, _FloodGlow, _FrontGlow, _RiceColor, _NoDataColor, _SkyHorizon, _SkyZenith;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                half fog : TEXCOORD3;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = v.uv;
                o.fog = ComputeFogFactor(p.positionCS.z);
                return o;
            }

            // x = water, y = rice, z = no data (bilinear sample, so edges come out soft)
            half3 Classes(half m)
            {
                half water = 1 - smoothstep(0.15, 0.35, m);
                half rice = 1 - smoothstep(0.03, 0.08, abs(m - 200.0 / 255.0));
                half nodata = 1 - smoothstep(0.03, 0.08, abs(m - 128.0 / 255.0));
                return half3(water, rice, nodata);
            }

            half3 Mix(float2 uv)
            {
                half3 a = Classes(SAMPLE_TEXTURE2D(_MaskA, sampler_MaskA, uv).r);
                half3 b = Classes(SAMPLE_TEXTURE2D(_MaskB, sampler_MaskA, uv).r);
                return lerp(a, b, _Blend);
            }

            // Sum of travelling sine waves; returns the surface slope (dh/dx, dh/dz).
            float2 RippleSlope(float2 p, float t)
            {
                float2 s = 0;
                const float2 d0 = float2(0.8, 0.6), d1 = float2(-0.4, 0.9), d2 = float2(0.95, -0.3), d3 = float2(-0.7, -0.7);
                s += d0 * cos(dot(p, d0) * 2.1 + t * 1.3) * 0.50;
                s += d1 * cos(dot(p, d1) * 3.7 + t * 1.9) * 0.30;
                s += d2 * cos(dot(p, d2) * 6.3 + t * 2.6) * 0.18;
                s += d3 * cos(dot(p, d3) * 11.0 + t * 3.4) * 0.10;
                return s;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float t = _Time.y;
                half3 land = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb * _LandGain;
                half3 nTex = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, i.uv).rgb * 2 - 1;
                half3 landN = normalize(nTex.y > 0.01 ? nTex : i.normalWS);

                half3 c = Mix(i.uv);
                half baseWater = Classes(SAMPLE_TEXTURE2D(_MaskBase, sampler_MaskA, i.uv).r).x;
                half flood = saturate(c.x - baseWater);

                // flood front: where the water amount changes over a few texels
                float o = _MaskTexel * 4.0;
                half wl = Mix(i.uv + float2(-o, 0)).x, wr = Mix(i.uv + float2(o, 0)).x;
                half wd = Mix(i.uv + float2(0, -o)).x, wu = Mix(i.uv + float2(0, o)).x;
                half edge = saturate((abs(wr - wl) + abs(wu - wd)) * 1.2);
                half front = edge * edge * saturate(flood * 1.5);

                float3 V = normalize(GetWorldSpaceViewDir(i.positionWS));
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light sun = GetMainLight(shadowCoord);
                half shadow = sun.shadowAttenuation;

                // ---- land: satellite colour, lit by the DEM normals
                half ndl = saturate(dot(landN, sun.direction));
                half3 ambient = SampleSH(landN);
                half3 col = land * (sun.color * ndl * shadow * 0.85 + ambient * 0.55);
                col = lerp(col, _NoDataColor.rgb * (ambient * 0.6 + 0.3), c.z * 0.8);
                col = lerp(col, lerp(col, _RiceColor.rgb * (ambient * 0.5 + sun.color * ndl * 0.6), 0.55), c.y);

                // ---- water: tinted by what's under it, sky reflection + sun glint on ripples
                float2 sl = RippleSlope(i.positionWS.xz * 0.35, t) * 0.12 * _Ripple;
                float3 wN = normalize(float3(-sl.x, 1, -sl.y));
                float3 R = reflect(-V, wN);
                half3 sky = lerp(_SkyHorizon.rgb, _SkyZenith.rgb, saturate(R.y * 1.6));
                half fres = 0.04 + 0.96 * pow(1 - saturate(dot(wN, V)), 5);
                float3 H = normalize(sun.direction + V);
                half spec = pow(saturate(dot(wN, H)), 380) * 6 * shadow;
                half3 body = lerp(_WaterColor.rgb, land * 0.55 + _WaterColor.rgb * 0.45, baseWater * 0.5);
                body = lerp(body, _FloodColor.rgb, flood);
                half3 water = body * (ambient * 0.6 + sun.color * 0.35) ;
                water = lerp(water, sky, fres * 0.55) + sun.color * spec;

                // data glow: new water breathes, the front shimmers (HDR so Bloom picks it up)
                half pulse = 0.65 + 0.35 * sin(t * 2.2);
                half3 glow = _FloodGlow.rgb * flood * 0.10 * pulse;
                glow += _FrontGlow.rgb * front * 0.45 * (0.75 + 0.25 * sin(t * 6 + i.positionWS.x * 0.15));
                water += glow * _GlowAmount;

                col = lerp(col, water, c.x);
                col += _FrontGlow.rgb * front * (1 - c.x) * 0.12 * _GlowAmount;
                col = MixFog(col, i.fog);
                return half4(col, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4 vert(float4 positionOS : POSITION) : SV_POSITION { return TransformObjectToHClip(positionOS.xyz); }
            half frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct V { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; };
            V vert(A a) { V v; v.positionCS = TransformObjectToHClip(a.positionOS.xyz); v.normalWS = TransformObjectToWorldNormal(a.normalOS); return v; }
            half4 frag(V v) : SV_Target { return half4(NormalizeNormalPerPixel(v.normalWS), 0); }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
