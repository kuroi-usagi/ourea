// The MODELED flood surface: a flat sheet of water at the level the viewer drags to (FloodLab).
// Where the DEM is lower than the sheet, the sheet shows; higher ground pokes through it.
// Silty Mekong water with sky reflection, a soft sun glint and a wobble when the level moves.
Shader "Ourea/FloodPlane"
{
    Properties
    {
        _Water ("Water", Color) = (0.42, 0.50, 0.47, 1)
        _SkyHorizon ("Sky horizon", Color) = (0.62, 0.70, 0.78, 1)
        _SkyZenith ("Sky zenith", Color) = (0.30, 0.44, 0.62, 1)
        _Alpha ("Opacity", Range(0, 1)) = 0.78
        _Wobble ("Wobble (set by FloodLab from the level's speed)", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent-10" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _Water, _SkyHorizon, _SkyZenith;
                half _Alpha, _Wobble;
            CBUFFER_END
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float2 uv : TEXCOORD1; half fog : TEXCOORD2; };
            V vert(A a)
            {
                V v;
                v.positionWS = TransformObjectToWorld(a.positionOS.xyz);
                v.positionCS = TransformWorldToHClip(v.positionWS);
                v.uv = a.uv;
                v.fog = ComputeFogFactor(v.positionCS.z);
                return v;
            }
            half4 frag(V i) : SV_Target
            {
                float t = _Time.y;
                float2 p = i.positionWS.xz * 0.3;
                float amp = 0.05 + 0.35 * _Wobble;   // calm when still, sloshes while the level moves
                float2 sl = float2(cos(p.x * 1.7 + t * 1.2) + 0.6 * cos(dot(p, float2(0.6, 0.8)) * 3.1 + t * 2.0),
                                   cos(p.y * 1.9 - t * 1.0) + 0.6 * cos(dot(p, float2(-0.7, 0.7)) * 2.7 + t * 1.7)) * amp * 0.25;
                float3 n = normalize(float3(-sl.x, 1, -sl.y));
                float3 vdir = normalize(GetWorldSpaceViewDir(i.positionWS));
                Light sun = GetMainLight();
                half fres = 0.04 + 0.96 * pow(1 - saturate(dot(n, vdir)), 5);
                float3 r = reflect(-vdir, n);
                half3 sky = lerp(_SkyHorizon.rgb, _SkyZenith.rgb, saturate(r.y * 1.6));
                half spec = pow(saturate(dot(n, normalize(sun.direction + vdir))), 260) * 3;
                half3 col = _Water.rgb * (SampleSH(n) * 0.6 + sun.color * 0.3);
                col = lerp(col, sky, fres * 0.6) + sun.color * spec;
                // soft fade at the tile edge so the sheet doesn't end in a hard line
                half edge = saturate(min(min(i.uv.x, 1 - i.uv.x), min(i.uv.y, 1 - i.uv.y)) * 60);
                col = MixFog(col, i.fog);
                return half4(col, _Alpha * edge);
            }
            ENDHLSL
        }
    }
}
