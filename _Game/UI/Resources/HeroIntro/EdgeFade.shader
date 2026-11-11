Shader "RTS/UI/HeroIntroEdgeFade"
{
    Properties { _MainTex ("Video", 2D) = "white" {} _Opacity ("Opacity", Range(0,1)) = 1 }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" }
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float _Opacity;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o; }
            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 color=tex2D(_MainTex,i.uv);
                float2 edge=min(i.uv,1-i.uv);
                color.a *= smoothstep(0,.12,edge.x)*smoothstep(0,.12,edge.y)*_Opacity;
                return color;
            }
            ENDCG
        }
    }
}
