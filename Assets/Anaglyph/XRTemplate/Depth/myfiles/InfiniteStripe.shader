Shader "Custom/InfiniteStripe"
{
    Properties
    {
        _StripeFrequency ("Stripe Frequency", Float) = 24
        _StripeAngle ("Stripe Angle", Float) = 45
        _PinkColor ("Pink", Color) = (1,0.2,0.8,1)
        _WhiteColor ("White", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        ZWrite Off
        Cull Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _StripeFrequency;
            float _StripeAngle;
            float4 _PinkColor;
            float4 _WhiteColor;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float a = radians(_StripeAngle);
                float2x2 r = float2x2(cos(a), -sin(a), sin(a), cos(a));
                float2 uv = mul(r, i.uv * 2.0 - 1.0);

                float phase = frac(uv.x * _StripeFrequency);
                float t = step(0.5, phase);

                return lerp(_PinkColor, _WhiteColor, t);
            }
            ENDHLSL
        }
    }
}