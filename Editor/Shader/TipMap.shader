// グラデーション「毛先から」の毛先マップ: メッシュを UV 空間に広げて、各画素に頂点の毛先からの距離と房の長さ（TEXCOORD3 の xy）を補間して書く（TipMap が使う）
// 出力: R = 毛先からの距離（m）、G = 房の長さ（m）、A = 1（描いた印。クリアは (0,0,0,0)）。ARGBFloat の RT に書く前提
Shader "Hidden/ClickRecolor/TipMap"
{
    Properties
    {
        _UvScaleOffset ("UV Scale (xy) / Offset (zw)", Vector) = (1, 1, 0, 0)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            // UV 空間では表裏が意味を持たない（UV の向きで裏返る三角形もある）ので両面、深度は使わない（PositionMap.shader と同じ）
            Cull Off
            ZWrite Off
            ZTest Always

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            // マテリアルの Tiling (xy) / Offset (zw)
            float4 _UvScaleOffset;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                // x = 毛先からの距離、y = 房の長さ（TipMap.cs がメッシュの複製に入れる）
                float2 tip : TEXCOORD3;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 tip : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                // 頂点を UV の位置に置く（PositionMap.shader と同じ置き方。位置マップと同じ画素に書く）
                float2 uv = v.uv * _UvScaleOffset.xy + _UvScaleOffset.zw;
                o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                o.pos.y = -o.pos.y;
                #endif
                o.tip = v.tip;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.tip, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
