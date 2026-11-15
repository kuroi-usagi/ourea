// Compare wipe, camera side: the real Sentinel-2 photo nearest the NISAR pass, clouds and all.
// Drawn on a copy of the delta mesh just in front of Kason's surface (Offset -1,-1) and clipped to
// the part of the screen LEFT of _WipeX, so the right side keeps showing the radar view untouched.
Shader "Ourea/Optical"
{
    Properties
    {
        _MainTex ("Sentinel-2 photo", 2D) = "white" {}
        _WipeX ("Wipe position (0..1 of screen width)", Range(0, 1)) = 0.5
        _Gain ("Brightness", Range(0.5, 2)) = 1.0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+1" }
        Pass
        {
            Name "OpticalUnlit"
            Tags { "LightMode" = "UniversalForward" }
            Offset -1, -1
            ZWrite On
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST; float _WipeX; half _Gain;
            CBUFFER_END
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; half fog : TEXCOORD1; };
            V vert(A v)
            {
                V o; VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS; o.uv = v.uv; o.fog = ComputeFogFactor(p.positionCS.z); return o;
            }
            half4 frag(V i) : SV_Target
            {
                float sx = i.positionCS.x / _ScreenParams.x;
                clip(_WipeX - sx);
                half3 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).rgb * _Gain;
                c = MixFog(c, i.fog);
                return half4(c, 1);
            }
            ENDHLSL
        }
    }
}
