// Ctrl＋ドラッグの矩形選択用: 各画素に「どの Renderer・サブメッシュの、どの三角形が見えているか」を書く（IslandRectPicker が使う）
// 出力: R = _RendererIndex（Renderer 番号×256＋サブメッシュ）、G = SV_PrimitiveID（三角形番号）。ARGBFloat の RT に書く前提
Shader "Hidden/ClickRecolor/IslandId"
{
    Properties
    {
        _RendererIndex ("Renderer Index", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            // 裏面も拾う（ScenePicker のレイ判定が両面なのに合わせる）
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // SV_PrimitiveID に必要
            #pragma target 4.0
            #include "UnityCG.cginc"

            float _RendererIndex;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                return o;
            }

            // SV_PrimitiveID は描画呼び出し（DrawMeshNow のサブメッシュ 1 回）の中での三角形番号のはず。
            // 環境によってメッシュ全体の番号になる恐れがあるので、読み戻し側（IslandRectPicker.CollectIslands）で両方に対応する
            float4 frag(v2f i, uint primitiveId : SV_PrimitiveID) : SV_Target
            {
                return float4(_RendererIndex, (float)primitiveId, 0, 1);
            }
            ENDCG
        }
    }
}
