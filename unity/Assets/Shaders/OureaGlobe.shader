// The Earth around the delta tile (GlobeField): the whole planet, and a finer ring of ground
// around the tile that fades into it. Soft day lighting from the scene sun; the study area itself
// is cut out (_Hole, world XZ) because the tile draws it, and framed with a thin cyan line.
Shader "Ourea/Globe"
{
    Properties
    {
        _MainTex ("Imagery (plate carree)", 2D) = "white" {}
        _Dim ("Brightness outside the study area", Range(0, 1.5)) = 0.9
        _Hole ("Study area cut-out: x0, z0, x1, z1 (world)", Vector) = (0, 0, 0, 0)
        _Frame ("Frame colour", Color) = (0.37, 0.83, 0.88, 1)
        _FrameWidth ("Frame width (world units)", Float) = 1.2
        _Fade ("Opacity (the context ground fades out from space)", Range(0, 1)) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+10" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST, _Hole;
                half4 _Frame;
                half _Dim, _Fade;
                float _FrameWidth;
            CBUFFER_END
            struct A { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; half4 color : COLOR; };
            struct V { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float2 uv : TEXCOORD2; half alpha : TEXCOORD3; half fog : TEXCOORD4; };
            V vert(A a)
            {
                V v;
                v.positionWS = TransformObjectToWorld(a.positionOS.xyz);
                v.positionCS = TransformWorldToHClip(v.positionWS);
                v.normalWS = TransformObjectToWorldNormal(a.normalOS);
                v.uv = a.uv;
                v.alpha = a.color.a;
                v.fog = ComputeFogFactor(v.positionCS.z);
                return v;
            }
            half4 frag(V i) : SV_Target
            {
                float2 p = i.positionWS.xz;
                float inside = step(_Hole.x, p.x) * step(p.x, _Hole.z) * step(_Hole.y, p.y) * step(p.y, _Hole.w);
                if (_Hole.z > _Hole.x && inside > 0.5) discard;
                half3 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).rgb;
                float3 n = normalize(i.normalWS);
                Light sun = GetMainLight();
                half ndl = saturate(dot(n, sun.direction) * 0.8 + 0.2);   // wrapped: the night side stays faintly readable
                half3 col = albedo * (0.35 + 0.95 * ndl * sun.color) * _Dim;
                // the frame just outside the study area
                if (_Hole.z > _Hole.x)
                {
                    float2 d = max(float2(_Hole.x - p.x, _Hole.y - p.y), float2(p.x - _Hole.z, p.y - _Hole.w));
                    float out_ = max(d.x, d.y);
                    half f = saturate(1 - out_ / _FrameWidth) * step(0, out_);
                    col = lerp(col, _Frame.rgb, f * 0.85);
                }
                col = MixFog(col, i.fog);
                return half4(col, i.alpha * _Fade);
            }
            ENDHLSL
        }
    }
}
