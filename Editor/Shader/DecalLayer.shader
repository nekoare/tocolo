// 「画像を入れる」のデカール層: メッシュを UV 空間に広げて、各画素に「そこに写る表面が箱の中なら、箱の XY で引いた画像の色」を書く
// （DecalLayerBuilder が使う。BoxMask.shader と同じ座標の取り方）。
// 投影は箱の +Z 面から −Z 方向。正面（+Z 側）から見た人に画像が正しく見えるよう u は反転させる（u = 0.5 − x / size.x）。
// 出力: 描画中は乗算済み（RGB = 画像の線形色 × α、A = max(画像の α, CoverageFloor)。箱の外・比率で余った所は描いた印 (0,0,0,CoverageFloor)、
// 三角形の外の塗り残しは (0,0,0,0)）。
// ARGBHalf の RT に書く前提。乗算済みで重ねるのは、UV が重なっても α が 1 を超えず正しい over 合成になるようにするため。
// DecalLayerBuilder.Build の最後に縁埋め（CSDilate）→ CSFinalize（DecalDilate.compute）で割り戻し、層はストレート α で返す。
// 裏面カリングは UV 空間では判定できないので、ここでは行わない（DecalLayerBuilder が表の三角形だけを渡す）
Shader "Hidden/ClickRecolor/DecalLayer"
{
    Properties
    {
        _MainTex ("Decal", 2D) = "white" {}
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
            // 重なる UV（左右共有）は後勝ちの乗算済み over 合成（RGB・α とも。α は 1 を超えない）
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "UnityCG.cginc"

            // ユーザーの画像が Repeat でも端に反対側が混ざらないように、画像のサンプラー設定は使わず Clamp に固定する
            // （Unity のインラインサンプラー命名。trilinear でミップも効かせる。Texture2D.Sample は SM4 以上なので target 4.0）
            Texture2D _MainTex;
            SamplerState sampler_trilinear_clamp;
            float4x4 _RootWorldToLocal; // 対象ルートの worldToLocalMatrix
            float4x4 _RootToBox;        // 対象ルートのローカル → 箱のローカル
            float3 _BoxSize;            // 箱の大きさ（符号付き。負なら反転。各成分の絶対値は C# 側で 1e-5 以上にしてある）
            float2 _FitScale;           // 比率を保つための拡大（(1,1) なら引き伸ばし）
            float4 _UvScaleOffset;

            // 描いた画素の α の下限（DecalDilate.compute の CoverageFloor と揃えること）
            static const float CoverageFloor = 1.0 / 1024.0;

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

            float4 frag(v2f i) : SV_Target
            {
                float3 halfSize = max(abs(_BoxSize), 1e-5) * 0.5;
                float3 size = sign(_BoxSize) * max(abs(_BoxSize), 1e-5);
                float2 uv = float2(0.5 - i.box.x / size.x, i.box.y / size.y + 0.5);
                uv = (uv - 0.5) * _FitScale + 0.5;
                // 箱の外・比率で余った所は clip せず「描いた印」（色なし・α = CoverageFloor、画像の透明な部分と同じ）を書く。
                // clip すると未描画（α = 0）になり、(a) 縁埋め（CSDilate）が箱の縁の画像を外へ 2 テクセル広げて太らせ、
                // (b) スーパーサンプリングで縮めた箱の縁の中間 α の外側を、縁埋めが近傍の不透明な色で埋め直して階段に戻してしまう。
                // 印は CSFinalize で 0 に落ちる。未描画のまま残るのは三角形の外（チャートの縁の塗り残し）だけになる
                bool inBox = all(abs(i.box) <= halfSize);
                bool inImage = all(uv >= 0.0) && all(uv <= 1.0);
                if (!(inBox && inImage)) return float4(0.0, 0.0, 0.0, CoverageFloor);
                // 画像が sRGB 資産なら sampler が線形に戻す（Linear 色空間前提）。RT は線形（sRGB フラグ無し）なのでそのまま書く
                float4 c = _MainTex.Sample(sampler_trilinear_clamp, uv);
                // 描かれた画素と描かれていない画素（塗り残し）を縁埋めで区別するため、描いた画素の α は 0 にしない（最後に CSFinalize で戻す）。
                // RGB は元の α で乗算する（α だけ下限）。Blend One OneMinusSrcAlpha で重ねても下限は保たれる
                float a = max(c.a, CoverageFloor);
                return float4(c.rgb * c.a, a);   // 乗算済みで書く（Build の最後に割り戻す）
            }
            ENDCG
        }
    }
}
