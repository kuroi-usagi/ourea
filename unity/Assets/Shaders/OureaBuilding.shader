// Building blocks (BuildingField). Each vertex carries the ground height under its block in uv.x
// (world units); FloodLab sets _OureaFloodY to the water sheet's height every frame. A block whose
// ground is under the water turns flood red, so villages visibly go under as the level rises.
Shader "Ourea/Building"
{
    Properties
    {
        _Dry ("Dry", Color) = (0.88, 0.85, 0.80, 1)
        _Roof ("Dry roof", Color) = (0.62, 0.66, 0.70, 1)
        _Wet ("Flooded", Color) = (0.86, 0.26, 0.18, 1)
        _Blend ("Units of water for the full colour change", Float) = 0.6
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
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _Dry, _Roof, _Wet;
                float _Blend;
            CBUFFER_END
            float _OureaFloodY;   // global, world units; very low when no water is modeled
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float baseY : TEXCOORD2; half fog : TEXCOORD3; };
            V vert(A a)
            {
                V v;
                v.positionWS = TransformObjectToWorld(a.positionOS.xyz);
                v.positionCS = TransformWorldToHClip(v.positionWS);
                v.normalWS = TransformObjectToWorldNormal(a.normalOS);
                v.baseY = TransformObjectToWorld(float3(0, a.uv.x, 0)).y;
                v.fog = ComputeFogFactor(v.positionCS.z);
                return v;
            }
            half4 frag(V i) : SV_Target
            {
                half wet = saturate((_OureaFloodY - i.baseY) / max(_Blend, 1e-3));
                float3 n = normalize(i.normalWS);
                half3 dry = n.y > 0.5 ? _Roof.rgb : _Dry.rgb;   // tin roofs, pale walls
                half3 albedo = lerp(dry, _Wet.rgb, wet);
                Light sun = GetMainLight(TransformWorldToShadowCoord(i.positionWS));
                half ndl = saturate(dot(n, sun.direction));
                half3 col = albedo * (SampleSH(n) + sun.color * ndl * sun.shadowAttenuation);
                col += _Wet.rgb * wet * 0.25;   // flooded roofs glow a little so they read from far away
                col = MixFog(col, i.fog);
                return half4(col, 1);
            }
            ENDHLSL
        }
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
    }
}
