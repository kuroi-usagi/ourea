// Delta surface, drawn as a clean cartographic map (in the spirit of the Urban Sentinel atlas):
// pale land toned from the satellite basemap (roads and towns come out white), soft hillshade
// from the DEM, and water as crisp anti-aliased shapes from the handoff landwater masks.
// Mask values (handoff/CONTRACT.md): 255 land · 0 water · 128 no data · 200 rice flooded on purpose.
// _MaskA/_MaskB are two neighbouring passes, _Blend scrubs between them; _MaskBase is the first
// pass, so water that wasn't there at the start is drawn as flood.
Shader "Ourea/Water"
{
    Properties
    {
        _BaseMap ("Satellite basemap", 2D) = "white" {}
        _NormalMap ("World normals (RGB = xyz*0.5+0.5, from the DEM)", 2D) = "bump" {}
        _MaskA ("Landwater pass A", 2D) = "white" {}
        _MaskB ("Landwater pass B", 2D) = "white" {}
        _MaskBase ("Landwater first pass", 2D) = "white" {}
        _Blend ("Blend A to B", Range(0, 1)) = 0
        _LandLow ("Land (fields)", Color) = (0.80, 0.82, 0.76, 1)
        _LandHigh ("Land (trees, gardens)", Color) = (0.66, 0.72, 0.62, 1)
        _Built ("Roads and towns", Color) = (0.98, 0.97, 0.95, 1)
        _River ("River water", Color) = (0.62, 0.72, 0.80, 1)
        _Flood ("New flood water", Color) = (0.36, 0.58, 0.82, 1)
        _Rice ("Rice flooded on purpose", Color) = (0.50, 0.74, 0.70, 1)
        _NoData ("No data", Color) = (0.78, 0.78, 0.78, 1)
        _Outline ("Water outline", Color) = (0.30, 0.44, 0.56, 1)
        _Hillshade ("Hillshade", Range(0, 1)) = 0.55
        _Detail ("Satellite detail", Range(0, 1)) = 0.35
        _GlowAmount ("Flood pulse", Range(0, 1)) = 1
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
            #pragma multi_compile _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);   SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);
            TEXTURE2D(_MaskA);     SAMPLER(sampler_MaskA);
            TEXTURE2D(_MaskB);
            TEXTURE2D(_MaskBase);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _Blend;
                half _Hillshade, _Detail, _GlowAmount;
                half4 _LandLow, _LandHigh, _Built, _River, _Flood, _Rice, _NoData, _Outline;
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

            // Anti-aliased step: sharp at any zoom, one screen pixel of softness.
            half Crisp(half value, half edge)
            {
                half w = max(fwidth(value), 1e-4);
                return saturate((edge - value) / w + 0.5);
            }

            // x = water, y = rice, z = no data for one mask sample
            half3 Classes(half m)
            {
                half w = max(fwidth(m), 1e-4);
                half water = Crisp(m, 0.25);
                half rice = 1 - saturate(abs(m - 200.0 / 255.0) / w - 0.5);
                half nodata = 1 - saturate(abs(m - 128.0 / 255.0) / w - 0.5);
                return half3(water, rice * (1 - water), nodata * (1 - water));
            }

            half4 frag(Varyings i) : SV_Target
            {
                float t = _Time.y;

                // ---- land: tone the satellite image into a pale map palette
                half3 sat = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb;
                half lum = dot(sat, half3(0.299, 0.587, 0.114));
                half green = saturate((sat.g - max(sat.r, sat.b)) * 6);
                half3 land = lerp(_LandLow.rgb, _LandHigh.rgb, saturate(green * 0.8 + (0.18 - lum) * 3));
                land *= lerp(1, 0.75 + lum * 1.4, _Detail);                    // keep field texture, softly
                land = lerp(land, _Built.rgb, smoothstep(0.30, 0.48, lum));     // roads, towns, dikes

                half3 nTex = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, i.uv).rgb * 2 - 1;
                half3 n = normalize(nTex.y > 0.01 ? nTex : i.normalWS);

                // ---- water from the masks (blend two passes; crisp edges)
                half mA = SAMPLE_TEXTURE2D(_MaskA, sampler_MaskA, i.uv).r;
                half mB = SAMPLE_TEXTURE2D(_MaskB, sampler_MaskA, i.uv).r;
                half m = lerp(mA, mB, _Blend);
                half3 c = Classes(m);
                half base = Crisp(SAMPLE_TEXTURE2D(_MaskBase, sampler_MaskA, i.uv).r, 0.25);
                half flood = c.x * (1 - base);

                // thin outline around all water: band around the 0.25 iso-line
                half w = max(fwidth(m), 1e-4);
                half outline = saturate(1 - abs(m - 0.25) / (w * 1.2));

                half3 col = land;
                col = lerp(col, _NoData.rgb, c.z);
                col = lerp(col, _Rice.rgb, c.y);
                half pulse = 0.88 + 0.12 * sin(t * 2.0);
                half3 water = lerp(_River.rgb, _Flood.rgb * lerp(1, pulse, _GlowAmount), saturate(flood / max(c.x, 1e-3)));
                col = lerp(col, water, c.x);
                col = lerp(col, _Outline.rgb, outline * 0.5);

                // ---- light: soft hillshade + main light shadows, bright ambient (studio look)
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                half ndl = saturate(dot(n, sun.direction));
                half shade = lerp(1, 0.55 + 0.6 * ndl, _Hillshade * (1 - c.x));
                col *= shade * lerp(0.82, 1, sun.shadowAttenuation);

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
