// Stars behind the globe (GlobeJourney): one-pixel points, white with a little flicker.
Shader "Ourea/Stars"
{
    Properties { _Brightness ("Brightness", Float) = 0.9 }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Background+10" }
        ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half _Brightness;
            CBUFFER_END
            struct A { float4 positionOS : POSITION; uint id : SV_VertexID; };
            struct V { float4 positionCS : SV_POSITION; half b : TEXCOORD0; };
            V vert(A a)
            {
                V v;
                v.positionCS = TransformObjectToHClip(a.positionOS.xyz);
                float h = frac(sin(a.id * 12.9898) * 43758.5453);
                v.b = _Brightness * (0.35 + 0.65 * h) * (0.85 + 0.15 * sin(_Time.y * (1.0 + 3.0 * h) + h * 40.0));
                return v;
            }
            half4 frag(V i) : SV_Target { return half4(i.b, i.b, i.b * 1.05, 1); }
            ENDHLSL
        }
    }
}
