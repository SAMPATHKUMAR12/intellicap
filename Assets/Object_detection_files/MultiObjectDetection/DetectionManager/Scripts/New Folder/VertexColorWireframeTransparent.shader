Shader "Custom/VertexColorWireframeTransparent"
{
    Properties
    {
        _WireColor("Wire Color", Color) = (0.2, 0.6, 1.0, 1.0)
        _WireThickness("Wire Thickness", Range(0.2, 5.0)) = 1.2
        _WireAlpha("Wire Alpha", Range(0,1)) = 0.8
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color  : COLOR;     // your per-vertex RGBA (A used to hide faces)
                float2 uv2    : TEXCOORD1; // barycentric (x,y); z = 1-x-y
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float4 col   : COLOR;
                float2 bcXY  : TEXCOORD0;
            };

            fixed4 _WireColor;
            float  _WireThickness;
            float  _WireAlpha;

            v2f vert(appdata v)
            {
                v2f o;
                o.pos  = UnityObjectToClipPos(v.vertex);
                o.col  = v.color;
                o.bcXY = v.uv2;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Fill color comes from vertex color
                fixed4 fill = i.col;

                // Reconstruct barycentric (x,y,z)
                float3 bc = float3(i.bcXY.x, i.bcXY.y, 1.0 - i.bcXY.x - i.bcXY.y);

                // Distance to nearest edge in barycentric space
                float d = min(bc.x, min(bc.y, bc.z));

                // Anti-alias width
                float aa = fwidth(d);

                // Thickness in "pixel-like" units (scaled by aa)
                float t = (_WireThickness * aa);

                // Edge mask: 1 on edges, 0 inside
                float edge = 1.0 - smoothstep(t, t + aa, d);

                // Make wire also disappear when face is hidden (vertex alpha 0)
                float visible = saturate(fill.a * 5.0); // strong gating
                float wireA = edge * _WireAlpha * visible;

                // Composite: overlay wire on top of fill
                fixed4 outCol = fill;
                outCol.rgb = lerp(outCol.rgb, _WireColor.rgb, wireA);
                outCol.a   = max(outCol.a, wireA);

                return outCol;
            }
            ENDHLSL
        }
    }
}
