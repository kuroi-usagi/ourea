// Additive light column (uv.y 0 = ground → 1 = top) and ground ring (_Ring = 1, radial uv) for
// the check-first beacons. See OureaBeacon.shader and Scripts/BeaconField.cs.
Shader "Ourea/Beam"
{
    Properties
    {
        [HDR] _BaseColor ("Colour", Color) = (1, 0.4, 0.2, 1)
        _Intensity ("Intensity", Range(0, 8)) = 1.6
        _Ring ("Ring mode", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Blend One One
        ZWrite Off
        Cull Off
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half _Intensity, _Ring;
            CBUFFER_END
            struct A { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct V { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            V vert(A a) { V v; v.positionCS = TransformObjectToHClip(a.positionOS.xyz); v.uv = a.uv; return v; }
            half4 frag(V v) : SV_Target
            {
                float t = _Time.y;
                half a;
                if (_Ring > 0.5)
                {
                    // expanding ripple ring on the ground
                    float r = length(v.uv - 0.5) * 2;
                    float wave = frac(t * 0.45);
                    a = smoothstep(0.08, 0.0, abs(r - wave)) * (1 - wave) + smoothstep(0.35, 0.0, r) * 0.35;
                    a *= step(r, 1);
                }
                else
                {
                    // column: bright at the ground, fading upward, with a slow shimmer
                    half fade = pow(saturate(1 - v.uv.y), 2.2);
                    half flick = 0.85 + 0.15 * sin(t * 3 + v.uv.y * 12);
                    a = fade * flick;
                }
                return half4(_BaseColor.rgb * a * _Intensity, 0);
            }
            ENDHLSL
        }
    }
}
