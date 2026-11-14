// Delta surface: land colour + water from the handoff landwater masks.
// Mask values (handoff/CONTRACT.md): 255 land · 0 water · 128 no data · 200 rice flooded on purpose.
// _MaskA/_MaskB are two neighbouring passes, _Blend scrubs between them; _MaskBase is the first
// pass, so water that wasn't there at the start gets the "new water" tint.
Shader "Ourea/Water"
{
    Properties
    {
        _BaseMap ("Land colour", 2D) = "white" {}
        _MaskA ("Landwater pass A", 2D) = "white" {}
        _MaskB ("Landwater pass B", 2D) = "white" {}
        _MaskBase ("Landwater first pass", 2D) = "white" {}
        _Blend ("Blend A to B", Range(0, 1)) = 0
        _WaterColor ("Water", Color) = (0.16, 0.43, 0.75, 1)
        _NewWaterColor ("New water", Color) = (0.10, 0.62, 0.95, 1)
        _RiceColor ("Rice flooded on purpose", Color) = (0.27, 0.67, 0.67, 1)
        _NoDataColor ("No data", Color) = (0.47, 0.47, 0.47, 1)
        _Shimmer ("Water shimmer", Range(0, 1)) = 0.35
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);  SAMPLER(sampler_BaseMap);
            TEXTURE2D(_MaskA);    SAMPLER(sampler_MaskA);
            TEXTURE2D(_MaskB);
            TEXTURE2D(_MaskBase);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _Blend;
                half4 _WaterColor, _NewWaterColor, _RiceColor, _NoDataColor;
                half _Shimmer;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 normalWS : TEXCOORD1; float3 positionWS : TEXCOORD2; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.positionWS = p.positionWS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = v.uv;
                return o;
            }

            // Per-class weights from one mask sample (bilinear, so edges come out soft).
            // x = water, y = rice, z = no data
            half3 Classes(half m)
            {
                half water = 1 - smoothstep(0.15, 0.35, m);
                half rice = 1 - smoothstep(0.03, 0.08, abs(m - 200.0 / 255.0));
                half nodata = 1 - smoothstep(0.03, 0.08, abs(m - 128.0 / 255.0));
                return half3(water, rice, nodata);
            }

            half4 frag(Varyings i) : SV_Target
            {
                half3 land = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb;
                half3 a = Classes(SAMPLE_TEXTURE2D(_MaskA, sampler_MaskA, i.uv).r);
                half3 b = Classes(SAMPLE_TEXTURE2D(_MaskB, sampler_MaskA, i.uv).r);
                half3 c = lerp(a, b, _Blend);
                half baseWater = Classes(SAMPLE_TEXTURE2D(_MaskBase, sampler_MaskA, i.uv).r).x;

                half fresh = saturate(c.x - baseWater);
                half3 water = lerp(_WaterColor.rgb, _NewWaterColor.rgb, fresh);
                float t = _Time.y;
                half ripple = sin(i.positionWS.x * 0.9 + t * 1.7) * sin(i.positionWS.z * 0.7 - t * 1.3);
                water *= 1 + ripple * 0.08 * _Shimmer;

                half3 col = land;
                col = lerp(col, _NoDataColor.rgb, c.z);
                col = lerp(col, _RiceColor.rgb, c.y);
                col = lerp(col, water, c.x);

                Light light = GetMainLight();
                half ndl = saturate(dot(normalize(i.normalWS), light.direction));
                half3 lit = col * (light.color * (0.35 + 0.65 * ndl) + SampleSH(i.normalWS) * 0.35);
                return half4(lit, 1);
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
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
