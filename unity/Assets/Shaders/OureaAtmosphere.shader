// A thin glow around the globe, seen from space (GlobeField). Drawn on the inside of a shell a
// little bigger than the Earth, brightest at the limb, like the first concept demo's globe.
Shader "Ourea/Atmosphere"
{
    Properties
    {
        _Glow ("Glow", Color) = (0.37, 0.83, 0.88, 1)
        _Strength ("Strength", Float) = 1.6
        _Fade ("Fade (0 near the ground, 1 from space)", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent+20" }
        Blend One One
        ZWrite Off
        Cull Front
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _Glow;
                half _Strength, _Fade;
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
            half4 frag(V i) : SV_Target
            {
                float3 vdir = normalize(GetWorldSpaceViewDir(i.positionWS));
                half rim = pow(saturate(0.75 - abs(dot(normalize(i.normalWS), vdir))), 3.0);
                return half4(_Glow.rgb * rim * _Strength * _Fade, 1);
            }
            ENDHLSL
        }
    }
}
