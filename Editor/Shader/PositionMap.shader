// グラデーション用の位置マップ: メッシュを UV 空間に広げて、各画素に「そこに写る表面の位置（対象ルートのローカル座標）」を書く（PositionMap が使う）
// 出力: RGB = 対象ルートのローカル位置、A = 1（描いた印。クリアは (0,0,0,0)）。ARGBFloat の RT に書く前提
Shader "Hidden/ClickRecolor/PositionMap"
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
            // UV 空間では表裏が意味を持たない（UV の向きで裏返る三角形もある）ので両面、深度は使わない
            Cull Off
            ZWrite Off
            ZTest Always

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            // 対象ルートの worldToLocalMatrix
            float4x4 _RootWorldToLocal;
            // マテリアルの Tiling (xy) / Offset (zw)
            float4 _UvScaleOffset;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 local : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                // 頂点を UV の位置に置く（投影行列は使わない）。uv (0,0) が RT の 0 行目（compute の id.y = 0）に来るようにする
                float2 uv = v.uv * _UvScaleOffset.xy + _UvScaleOffset.zw;
                o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                // DirectX 系はクリップ空間の y = +1 が RT の 0 行目なので上下を返す（投影行列を通さないので Unity の自動反転が効かない）
                o.pos.y = -o.pos.y;
                #endif
                // unity_ObjectToWorld は DrawMeshNow に渡した行列
                o.local = mul(_RootWorldToLocal, mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0))).xyz;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.local, 1.0);
            }
            ENDCG
        }
    }
}
