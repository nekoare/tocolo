// 「画像を入れる」第 2 段（重ね貼り）のデカール画像: 重ね貼りメッシュ（DecalOverlayMesh）の追加サブメッシュを
// UV0（投影 UV＝画像の座標）の位置に置いて描き、画像の大きさの RT に「画像 × 選択マスク」を作る（DecalImageBuilder が使う）。
// 第 1 段（DecalLayer.shader）が元テクスチャの UV 空間に描くのに対し、こちらは画像の空間に描く（重ね貼りマテリアルが画像を UV0 で直接引くため）。
// 選択マスクは元テクスチャの UV 空間にあるので、UV1（元の UV0）にマテリアルの Tiling/Offset を掛けた座標で引く。
// Pass 0（色）: 描画中は乗算済み（RGB = 画像の線形色 × a、A = max(a, CoverageFloor)、a = 画像の α × マスク）。
//   画像の外（と画像の外周 1 テクセル）は描いた印 (0,0,0,CoverageFloor)、三角形の外の塗り残しは (0,0,0,0)。ARGBHalf の RT に書く前提。
//   DecalImageBuilder が縁埋め（CSDilate）→ CSFinalize（DecalDilate.compute）で割り戻し、ストレート α にする。
// Pass 1（位置）: 対象ルートのローカル位置（A = 1 が描いた印）。ARGBFloat の RT に書く前提（PositionMap.shader と同じ形式。グラデーション・帯の打ち消し用）
// Pass 2（法線）: 元の法線マップ（_SourceNormal）を元の UV（lilToon の uvMain = 元の UV0 × メインの Tiling/Offset に、法線マップの Tiling/Offset）で引き、
//   画像の座標へ写し直す（「ノーマルも反映」。重ね貼りの面は UV0 が画像の座標なので、元の法線マップをそのまま引くと位置がずれるため）。
//   出力は接空間の法線を 0..1 に詰めた (n * 0.5 + 0.5, 1)。A = 1 なので UnpackNormal の「x に w を掛ける」形式でも、xyz をそのまま使う形式でも同じ法線になる。
//   接線は重ね貼りの頂点が元の値を複製しているので、元の面と同じ向きで解かれる。RT は線形・平らな法線 (0.5, 0.5, 1, 1) で消してから描く
// Pass 3（埋める）: Graphics.Blit で RT 全体を _FillColor にする（法線なら平らな (0.5, 0.5, 1, 1)、マスクなら白。GL.Clear の色は色空間の変換を受けうるので使わない）
// Pass 4（マスク）: 2nd ノーマルの強さマスク（_SourceNormal に入れる）を Pass 2 と同じ座標で引き、R を (r, r, r, 1) で写し直す。
//   lilToon は 2nd ノーマル本体は指定の UV で引くが、強さマスクはメインの UV（重ね貼りでは画像の座標）で引くため
Shader "Hidden/ClickRecolor/DecalImage"
{
    Properties
    {
        _MainTex ("Decal", 2D) = "white" {}
        _Mask ("Selection Mask", 2D) = "white" {}
        _UvScaleOffset ("UV Scale (xy) / Offset (zw)", Vector) = (1, 1, 0, 0)
        _EdgeTexel ("Edge Texel", Vector) = (0, 0, 0, 0)
        _SourceNormal ("Source Normal", 2D) = "bump" {}
        _SourceNormalST ("Source Normal Tiling (xy) / Offset (zw)", Vector) = (1, 1, 0, 0)
        // Color 型にすると Linear 色空間で値が線形化される（0.5 → 0.214）ので Vector 型にする
        _FillColor ("Fill Color", Vector) = (0.5, 0.5, 1, 1)
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    float4x4 _RootWorldToLocal; // 対象ルートの worldToLocalMatrix
    float4 _UvScaleOffset;      // 元スロットのマテリアルの Tiling (xy) / Offset (zw)

    struct appdata
    {
        float4 vertex : POSITION;
        float2 uv0 : TEXCOORD0;  // 投影 UV（画像の座標。0..1 の外もある）
        float2 uv1 : TEXCOORD1;  // 元の UV0
    };

    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;      // 投影 UV
        float2 maskUv : TEXCOORD1;  // 選択マスクの座標（元の UV0 × Tiling + Offset）
        float3 local : TEXCOORD2;   // 対象ルートのローカル位置
    };

    v2f vert(appdata v)
    {
        v2f o;
        // 頂点を投影 UV の位置に置く（投影行列は使わない）。uv (0,0) が RT の 0 行目に来るようにする（BoxMask.shader と同じ置き方）
        o.pos = float4(v.uv0 * 2.0 - 1.0, 0.0, 1.0);
        #if UNITY_UV_STARTS_AT_TOP
        // DirectX 系はクリップ空間の y = +1 が RT の 0 行目なので上下を返す（投影行列を通さないので Unity の自動反転が効かない）
        o.pos.y = -o.pos.y;
        #endif
        o.uv = v.uv0;
        o.maskUv = v.uv1 * _UvScaleOffset.xy + _UvScaleOffset.zw;
        // unity_ObjectToWorld は DrawMeshNow に渡した行列
        o.local = mul(_RootWorldToLocal, mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0))).xyz;
        return o;
    }
    ENDCG

    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        // Pass 0: 色（画像 × 選択マスク、乗算済み）
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            // 投影で同じ画像の位置に複数の面が重なっても（手前と奥の面など）α が 1 を超えない乗算済み over 合成
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0

            // ユーザーの画像が Repeat でも端に反対側が混ざらないように Clamp に固定する（DecalLayer.shader と同じ。Texture2D.Sample は SM4 以上）
            Texture2D _MainTex;
            SamplerState sampler_trilinear_clamp;
            Texture2D _Mask;
            // 選択マスクは元テクスチャの空間なので繰り返しで引く（Tiling > 1 で元 UV × Tiling/Offset が 0..1 の外に出ても、端に張り付かず元テクスチャと同じ位置を引く）
            SamplerState sampler_linear_repeat;
            // 出力 RT の 1 テクセルの大きさ（1/幅, 1/高さ）。外周 1 テクセルを画像の外として扱う
            float2 _EdgeTexel;

            // 描いた画素の α の下限（DecalLayer.shader・DecalDilate.compute の CoverageFloor と揃えること）
            static const float CoverageFloor = 1.0 / 1024.0;

            float4 frag(v2f i) : SV_Target
            {
                // 画像の外は描いた印だけ書く（CSFinalize で 0 に落ちる）。重ね貼りマテリアルは画像を Clamp で引くので、
                // 画像の縁の α が 0 でないと縁の色が箱の外（投影 UV が 0..1 の外の三角形）へ伸びる（DecalOverlayMesh の約束）。
                // RT に生じる画素の中心は必ず 0..1 の内側なので、0..1 の判定だけでは縁が残る: 外周 1 テクセルも外として扱う
                bool inImage = all(i.uv >= _EdgeTexel) && all(i.uv <= 1.0 - _EdgeTexel);
                if (!inImage) return float4(0.0, 0.0, 0.0, CoverageFloor);
                // 画像が sRGB 資産なら sampler が線形に戻す（Linear 色空間前提）。RT は線形（sRGB フラグ無し）なのでそのまま書く
                float4 img = _MainTex.Sample(sampler_trilinear_clamp, i.uv);
                float m = _Mask.Sample(sampler_linear_repeat, i.maskUv).r;
                float a = img.a * m;
                // 選択の外・画像の透明な所も描いた印（α = CoverageFloor）にして、縁埋めで埋められないようにする
                return float4(img.rgb * a, max(a, CoverageFloor));
            }
            ENDCG
        }

        // Pass 1: 位置（対象ルートのローカル）
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                return float4(i.local, 1.0);
            }
            ENDCG
        }

        // Pass 2: 法線（元の法線マップを画像の座標へ写し直す）
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            sampler2D _SourceNormal;
            float4 _SourceNormalST;

            float4 frag(v2f i) : SV_Target
            {
                // 画像の外は描かない（平らな法線のまま。画像の α も 0 なので見えない）
                clip(i.uv);
                clip(1.0 - i.uv);
                // maskUv は元の UV0 × メインの Tiling/Offset（lilToon の uvMain）。法線マップはそれにさらに自分の Tiling/Offset を掛けて引く
                float2 uv = i.maskUv * _SourceNormalST.xy + _SourceNormalST.zw;
                float3 n = UnpackNormal(tex2D(_SourceNormal, uv));
                return float4(n * 0.5 + 0.5, 1.0);
            }
            ENDCG
        }

        // Pass 3: _FillColor で埋める（Graphics.Blit 用。Blit の四角形は uv0 が 0..1 なので vert の置き方で RT 全体を覆う）
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 _FillColor;

            float4 frag(v2f i) : SV_Target
            {
                return _FillColor;
            }
            ENDCG
        }

        // Pass 4: 2nd ノーマルの強さマスク（元のマスクを画像の座標へ写し直す）
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            sampler2D _SourceNormal;
            float4 _SourceNormalST;

            float4 frag(v2f i) : SV_Target
            {
                clip(i.uv);
                clip(1.0 - i.uv);
                // lilToon と同じく uvMain（maskUv）にマスクの Tiling/Offset を掛けて R を引く
                float r = tex2D(_SourceNormal, i.maskUv * _SourceNormalST.xy + _SourceNormalST.zw).r;
                return float4(r, r, r, 1.0);
            }
            ENDCG
        }
    }
}
