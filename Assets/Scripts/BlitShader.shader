Shader "Hidden/BlitOverlay"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Overlay"}
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert (float4 pos : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(pos);
                o.uv = pos.xy;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv); // 🚀 overlayRT의 원본 데이터를 유지
            }
            ENDCG
        }
    }
}
