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
        _Palette ("Use the house palette (1) or Dry/Roof (0)", Float) = 1
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
                float _Blend, _Palette;
            CBUFFER_END
            float _OureaFloodY;   // global, world units; very low when no water is modeled
            // "as NISAR saw it" (FloodLab): red when new flood water is within ~100 m, the rule behind
            // the "children within 100 m of flood water" count
            float _OureaObserved, _OureaBlend;
            float4 _OureaTile;    // sizeX, sizeZ (world units, 1 unit = 100 m)
            TEXTURE2D(_OureaMaskA); TEXTURE2D(_OureaMaskB); TEXTURE2D(_OureaMaskBase); SAMPLER(sampler_OureaMaskA);
            half FloodAt(float2 uv)
            {
                half a = SAMPLE_TEXTURE2D(_OureaMaskA, sampler_OureaMaskA, uv).r;
                half b = SAMPLE_TEXTURE2D(_OureaMaskB, sampler_OureaMaskA, uv).r;
                half now = lerp(step(a, 0.25), step(b, 0.25), _OureaBlend);
                half was = step(SAMPLE_TEXTURE2D(_OureaMaskBase, sampler_OureaMaskA, uv).r, 0.25);
                return saturate(now - was);
            }
            half NearFlood(float3 posWS)
            {
                float2 uv = posWS.xz / max(_OureaTile.xy, 1);
                float2 d = 1.0 / max(_OureaTile.xy, 1);   // 100 m
                half f = FloodAt(uv);
                f = max(f, max(FloodAt(uv + float2(d.x, 0)), FloodAt(uv - float2(d.x, 0))));
                f = max(f, max(FloodAt(uv + float2(0, d.y)), FloodAt(uv - float2(0, d.y))));
                f = max(f, max(FloodAt(uv + d * 0.7), FloodAt(uv - d * 0.7)));
                f = max(f, max(FloodAt(uv + float2(d.x, -d.y) * 0.7), FloodAt(uv + float2(-d.x, d.y) * 0.7)));
                return f;
            }
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float baseY : TEXCOORD2; half fog : TEXCOORD3; float pick : TEXCOORD4; };
            V vert(A a)
            {
                V v;
                v.positionWS = TransformObjectToWorld(a.positionOS.xyz);
                v.positionCS = TransformWorldToHClip(v.positionWS);
                v.normalWS = TransformObjectToWorldNormal(a.normalOS);
                v.baseY = TransformObjectToWorld(float3(0, a.uv.x, 0)).y;
                v.pick = a.uv.y;
                v.fog = ComputeFogFactor(v.positionCS.z);
                return v;
            }
            half4 frag(V i) : SV_Target
            {
                half wet = _OureaObserved > 0.5 ? NearFlood(i.positionWS) : saturate((_OureaFloodY - i.baseY) / max(_Blend, 1e-3));
                float3 n = normalize(i.normalWS);
                half3 dry = n.y > 0.5 ? _Roof.rgb : _Dry.rgb;   // tin roofs, pale walls
                if (_Palette > 0.5)
                {
                    // canal-side houses in Dong Thap: terracotta tile or orange-rust and blue-grey tin roofs, pale painted walls
                    half3 roof = i.pick < 0.45 ? half3(0.36, 0.13, 0.06) : (i.pick < 0.7 ? half3(0.42, 0.22, 0.09) : half3(0.155, 0.216, 0.262));
                    float w = frac(i.pick * 7.31);
                    half3 wall = w < 0.5 ? half3(0.84, 0.76, 0.58) : (w < 0.8 ? half3(0.87, 0.67, 0.30) : half3(0.51, 0.67, 0.55));
                    dry = n.y > 0.5 ? roof : wall;
                }
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
