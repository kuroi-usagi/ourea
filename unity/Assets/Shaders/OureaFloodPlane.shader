// The MODELED flood surface: a flat sheet of water at the level the viewer drags to (FloodLab).
// Where the bare-earth ground is lower than the sheet, the sheet shows; higher ground and the
// building blocks poke through it. Water depth (sheet minus ground, from _GroundTex) sets colour
// and opacity: a few centimetres reads as a light film, two metres as deep brown-blue water.
Shader "Ourea/FloodPlane"
{
    Properties
    {
        _Shallow ("Shallow water", Color) = (0.62, 0.70, 0.64, 1)
        _Deep ("Deep water", Color) = (0.13, 0.25, 0.33, 1)
        _SkyHorizon ("Sky horizon", Color) = (0.62, 0.70, 0.78, 1)
        _SkyZenith ("Sky zenith", Color) = (0.30, 0.44, 0.62, 1)
        _Alpha ("Opacity", Range(0, 1)) = 0.9
        _Wobble ("Wobble (set by FloodLab from the level's speed)", Range(0, 1)) = 0
        _GroundTex ("Bare-earth ground, 0..1 over its range", 2D) = "black" {}
        _GroundScale ("World units for the ground range", Float) = 1
        _MetersToUnitsY ("World units per metre (vertical)", Float) = 0.24
        _DeepM ("Depth that reads as deep (m)", Float) = 2
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
            TEXTURE2D(_GroundTex); SAMPLER(sampler_GroundTex);
            CBUFFER_START(UnityPerMaterial)
                half4 _Shallow, _Deep, _SkyHorizon, _SkyZenith;
                half _Alpha, _Wobble;
                float _GroundScale, _MetersToUnitsY, _DeepM;
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
                // depth of water over the ground here, in metres
                float g = SAMPLE_TEXTURE2D(_GroundTex, sampler_GroundTex, i.uv).r * _GroundScale;
                float depthM = (i.positionWS.y - g) / max(_MetersToUnitsY, 1e-4);
                clip(depthM);
                half d = saturate(depthM / _DeepM);

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
                half3 body = lerp(_Shallow.rgb, _Deep.rgb, sqrt(d));
                half3 col = body * (SampleSH(n) * 0.6 + sun.color * 0.35);
                col = lerp(col, sky, fres * 0.5) + sun.color * spec;
                // a bright line where the water meets dry ground: the flood's edge
                half shore = 1 - saturate(depthM / 0.15);
                col = lerp(col, half3(0.92, 0.95, 0.93), shore * 0.6);
                half edge = saturate(min(min(i.uv.x, 1 - i.uv.x), min(i.uv.y, 1 - i.uv.y)) * 60);
                col = MixFog(col, i.fog);
                return half4(col, _Alpha * lerp(0.55, 1.0, d) * edge);
            }
            ENDHLSL
        }
    }
}
