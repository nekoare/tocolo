// 影響範囲「箱の中」のマスク: メッシュを UV 空間に広げて、各画素に「そこに写る表面が箱の内側なら 1」を描く（BoxMaskBuilder が使う）。
// 位置マップ（1 画素に 1 つの位置）を経由しないのは、左右の袖など同じ UV を共有する面が箱の内外に分かれていても、
// どれか 1 面でも箱の中なら選ばれるようにするため（BlendOp Max で重ね描き。実機報告 2026-09-29）。
// Feather > 0 なら面からの距離（各軸の半分の長さに対する割合）で 0..1 に滑らかにする。R8 の RT に書く前提
Shader "Hidden/ClickRecolor/BoxMask"
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
            Cull Off
            ZWrite Off
            ZTest Always
            BlendOp Max
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4x4 _RootWorldToLocal; // 対象ルートの worldToLocalMatrix
            float4x4 _RootToBox;        // 対象ルートのローカル → 箱のローカル
            float3 _BoxHalf;            // 箱の各軸の半分の長さ（絶対値）
            float _Feather;             // 面のぼかし（半分の長さに対する割合。0 なら硬い辺）
            float4 _UvScaleOffset;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 box : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float2 uv = v.uv * _UvScaleOffset.xy + _UvScaleOffset.zw;
                o.pos = float4(uv * 2.0 - 1.0, 0.0, 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                o.pos.y = -o.pos.y;
                #endif
                float3 local = mul(_RootWorldToLocal, mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0))).xyz;
                o.box = mul(_RootToBox, float4(local, 1.0)).xyz;
                return o;
            }

            float frag(v2f i) : SV_Target
            {
                float3 half3 = max(_BoxHalf, 1e-5);
                float3 inside = (half3 - abs(i.box)) / half3;
                float m = min(inside.x, min(inside.y, inside.z));
                if (_Feather > 0.0) return saturate(m / _Feather);
                // ぼかし 0: テクセル単位の 0/1 だと面の切れ目が階段になるので、面からの距離を 1 テクセル幅だけなだらかにする
                // （距離の画面微分＝1 テクセルあたりの変化量で正規化。実機 2026-09-29）
                // 三角形の縁では微分がでたらめに大きくなり、切り口でない所まで半端な値になる（UV の縁が線に見える）ので上限を付ける。
                // 面から半分の長さの 1/4 以上離れた画素は必ず 1 になる
                float w = clamp(fwidth(m) * 1.5, 1e-6, 0.25);
                return saturate(m / w + 0.5);
            }
            ENDCG
        }
    }
}
