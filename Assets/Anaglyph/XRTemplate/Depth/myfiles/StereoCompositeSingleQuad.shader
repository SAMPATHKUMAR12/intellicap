Shader "Custom/StereoCompositeSingleQuad"
{
    Properties
    {
        _StripeTex ("Stripe Tex", 2D) = "white" {}
        _MaskTexLeft ("Mask Left", 2D) = "black" {}
        _MaskTexRight ("Mask Right", 2D) = "black" {}
        _Opacity ("Opacity", Range(0,1)) = 1
        _Feather ("Feather", Range(0,0.2)) = 0.02
        _StripeScale ("Stripe Scale", Float) = 1
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            sampler2D _StripeTex;
            sampler2D _MaskTexLeft;
            sampler2D _MaskTexRight;
            float _Opacity;
            float _Feather;
            float _StripeScale;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float2 StableStripeUV(float2 uv)
            {
                // Same stripe image for both eyes by anchoring to quad UV,
                // not eye-dependent scene geometry.
                return uv * _StripeScale;
            }

            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float m = (unity_StereoEyeIndex == 0)
                    ? tex2D(_MaskTexLeft, i.uv).r
                    : tex2D(_MaskTexRight, i.uv).r;

                // float mL = tex2D(_MaskTexLeft, i.uv).r;
                // float mR = tex2D(_MaskTexRight, i.uv).r;
                // float m = 0.5 * (mL + mR);

                float4 stripe = tex2D(_StripeTex, StableStripeUV(i.uv));

                float scanned = smoothstep(0.5 - _Feather, 0.5 + _Feather, m);
                float alpha = (1.0 - scanned) * _Opacity;

                return float4(stripe.rgb, alpha);
            }
            ENDHLSL
        }
    }
}