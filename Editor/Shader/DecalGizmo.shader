// Scene で画像の箱の +Z 面に画像を薄く描く（DecalGizmo.cs が Graphics.DrawMeshNow で使う。焼き込みには使わない）。
// 色はそのまま、α に _Alpha を掛けて半透明で重ねる。裏（−Z 側）から見たときは Cull Back で描かれない（投影する向きが分かるように）
// ZTest LEqual なので +Z 面がモデルの内側に入ると画像は隠れる（ワイヤーは透ける）。投影面はモデルの手前に置く前提
Shader "Hidden/ClickRecolor/DecalGizmo"
{
    Properties
    {
        _MainTex ("Image", 2D) = "white" {}
        _Alpha ("Alpha", Range(0, 1)) = 0.6
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Alpha;

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
                float4 c = tex2D(_MainTex, i.uv);
                return float4(c.rgb, c.a * _Alpha);
            }
            ENDCG
        }
    }
}
