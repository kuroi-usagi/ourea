// Map markers. "Ourea/Beacon" is the solid glowing head of a pin; "Ourea/Beam" (below) is the
// additive light column and ground ring that BeaconField puts on the check-first top 10.
// Colours are HDR so URP Bloom makes them glow.
Shader "Ourea/Beacon"
{
    Properties
    {
        [HDR] _BaseColor ("Colour", Color) = (1, 1, 1, 1)
        _Glow ("Glow", Range(0, 8)) = 2.2
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Glow;
            CBUFFER_END
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct V { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 positionWS : TEXCOORD1; };
            V vert(A a)
            {
                V v;
                v.positionWS = TransformObjectToWorld(a.positionOS.xyz);
                v.positionCS = TransformWorldToHClip(v.positionWS);
                v.normalWS = TransformObjectToWorldNormal(a.normalOS);
                return v;
            }
            half4 frag(V v) : SV_Target
            {
                float3 view = normalize(GetWorldSpaceViewDir(v.positionWS));
                half rim = pow(1 - saturate(dot(normalize(v.normalWS), view)), 2);
                return half4(_BaseColor.rgb * (0.6 + rim * 0.8) * _Glow, 1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            float4 vert(float4 p : POSITION) : SV_POSITION { return TransformObjectToHClip(p.xyz); }
            half frag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
}
