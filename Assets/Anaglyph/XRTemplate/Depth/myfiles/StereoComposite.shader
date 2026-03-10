Shader "Custom/StereoComposite"
{
    Properties
    {
        _StripeTex ("Stripe Tex", 2D) = "white" {}
        _MaskTex ("Mask Tex", 2D) = "black" {}
        _Opacity ("Opacity", Range(0,1)) = 1
        _Feather ("Feather", Range(0,0.2)) = 0.02
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
            #include "UnityCG.cginc"

            sampler2D _StripeTex;
            sampler2D _MaskTex;
            float _Opacity;
            float _Feather;

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
                float4 stripe = tex2D(_StripeTex, i.uv);
                float m = tex2D(_MaskTex, i.uv).r;

                float scanned = smoothstep(0.5 - _Feather, 0.5 + _Feather, m);
                float alpha = (1.0 - scanned) * _Opacity;

                return float4(stripe.rgb, alpha);
                //return float4(m, m, m, 1);
                
            }
            ENDHLSL
        }
    }
}