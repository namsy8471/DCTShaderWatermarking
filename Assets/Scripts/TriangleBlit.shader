// TriangleOverlay.shader (원래 코드로 복구)
Shader "Hidden/TriangleOverlay"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha  // Alpha blending
            ZTest Always
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f {
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

            sampler2D _MainTex; // Compute Shader에서 만든 overlayRT

            fixed4 frag(v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv);  // overlayRT를 화면에 출력
                //return float4(1, 0, 0, 0.5f);  // 빨간색으로 출력
            }
            ENDCG
        }
    }
}