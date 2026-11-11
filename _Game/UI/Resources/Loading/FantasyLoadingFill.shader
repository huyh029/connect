Shader "RTS/UI/FantasyLoadingFill"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} }
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
            float4 _Size;
            float _Phase;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; return o; }
            fixed4 frag(v2f i) : SV_Target
            {
                float2 size=max(_Size.xy,1.0);
                float radius=min(size.x,size.y)*0.5;
                float2 q=abs((i.uv-0.5)*size)-(size*0.5-radius);
                float d=length(max(q,0))+min(max(q.x,q.y),0)-radius;
                float alpha=1-smoothstep(-max(fwidth(d),0.7),max(fwidth(d),0.7),d);
                float wave=i.uv.y+0.025*sin(i.uv.x*8.0-_Phase*1.6)-_Phase*0.12;
                float shade=0.5+0.5*sin(6.2831853*wave);
                float3 color=lerp(float3(0.36,0.035,0.68),float3(0.70,0.16,0.96),shade);
                // Two narrow moving accents: about 1% white and 1% black.
                float whiteDistance=abs(frac(wave-0.25+0.5)-0.5);
                float blackDistance=abs(frac(wave-0.75+0.5)-0.5);
                float whiteAccent=1-smoothstep(0.001,0.009,whiteDistance);
                float blackAccent=1-smoothstep(0.001,0.009,blackDistance);
                color=lerp(color,float3(0.86,0.70,0.98),whiteAccent*0.55);
                color=lerp(color,float3(0.18,0.025,0.30),blackAccent*0.55);
                return fixed4(color,alpha);
            }
            ENDCG
        }
    }
}
