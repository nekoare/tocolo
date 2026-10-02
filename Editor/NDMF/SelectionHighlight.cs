using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    /// <summary>
    /// 選択範囲のハイライト（設計 §8.3）。プレビューの結果 RT に、現在の編集のマスクを橙の縞で重ねた別の RT を作る。
    /// プレビュー専用で、ビルド（RecolorPass / RecolorPipeline）からは呼ばない
    /// </summary>
    internal static class SelectionHighlight
    {
        private const int ThreadGroupSize = 16;

        /// <summary>ハイライト色（sRGB の橙）。compute には線形にして渡す</summary>
        private static readonly Color TintSrgb = new Color(1f, 0.6f, 0.1f, 1f);

        private static ComputeShader s_shader;
        private static int s_kernel;

        private static readonly int InId = Shader.PropertyToID("In");
        private static readonly int MaskId = Shader.PropertyToID("Mask");
        private static readonly int OutId = Shader.PropertyToID("Out");
        private static readonly int WidthId = Shader.PropertyToID("Width");
        private static readonly int HeightId = Shader.PropertyToID("Height");
        private static readonly int MaskWidthId = Shader.PropertyToID("MaskWidth");
        private static readonly int MaskHeightId = Shader.PropertyToID("MaskHeight");
        private static readonly int TintId = Shader.PropertyToID("Tint");
        private static readonly int PhaseId = Shader.PropertyToID("Phase");
        private static readonly int PosMapId = Shader.PropertyToID("PosMap");
        private static readonly int PosWidthId = Shader.PropertyToID("PosWidth");
        private static readonly int PosHeightId = Shader.PropertyToID("PosHeight");
        private static readonly int ClipToBoxId = Shader.PropertyToID("ClipToBox");
        private static readonly int WorldToBoxId = Shader.PropertyToID("WorldToBox");
        private static readonly int BoxHeightId = Shader.PropertyToID("BoxHeight");

        /// <summary>
        /// グラデーションの「箱の中だけ」で縞を箱の中に限るときの情報（ユーザー要望 2026-10-03）。
        /// positionMap は位置マップ、rootToBox は対象ルートのローカル → 箱のローカル、boxHeight は箱の大きさの y
        /// </summary>
        internal struct BoxClip
        {
            public Texture positionMap;
            public Matrix4x4 rootToBox;
            public float boxHeight;
        }

        private static ComputeShader Compute
        {
            get
            {
                if (s_shader == null)
                {
                    s_shader = ShaderAssets.Load(ShaderAssets.HighlightGuid);
                    if (s_shader != null) s_kernel = s_shader.FindKernel("CSHighlight");
                }
                return s_shader;
            }
        }

        /// <summary>Highlight.compute が使えるか（compute 対応・ARGBHalf へ書ける・シェーダーが読める）</summary>
        internal static bool IsAvailable =>
            SystemInfo.supportsComputeShaders
            && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf)
            && Compute != null;

        /// <summary>
        /// dst = src のマスクの内側に橙の縞を重ねたもの（α は src のまま）。
        /// src と dst は別の RT で、dst は enableRandomWrite の RT（RecolorPipeline.CreateWorkTexture）であること。
        /// mask は src と別サイズでもよい（正規化座標で対応させる）。mip 0 だけ書く（ミップは呼び出し側で作る）
        /// </summary>
        internal static void Apply(RenderTexture src, RenderTexture mask, RenderTexture dst, BoxClip? clip = null)
        {
            var shader = Compute;
            if (shader == null || src == null || mask == null || dst == null) return;

            shader.SetTexture(s_kernel, InId, src);
            shader.SetTexture(s_kernel, MaskId, mask);
            shader.SetTexture(s_kernel, OutId, dst);
            shader.SetInt(WidthId, dst.width);
            shader.SetInt(HeightId, dst.height);
            shader.SetInt(MaskWidthId, mask.width);
            shader.SetInt(MaskHeightId, mask.height);
            // RT は線形の値を持つので、ハイライト色も線形にしてから混ぜる（見た目が sRGB の橙になる）
            shader.SetVector(TintId, TintSrgb.linear);
            // 縞はアニメしない（静止でも十分見える）
            shader.SetFloat(PhaseId, 0f);
            // 位置マップは切らないときも何か束縛しておく（未設定のテクスチャがあると Dispatch がエラーになる）
            bool useClip = clip.HasValue && clip.Value.positionMap != null;
            var pos = useClip ? clip.Value.positionMap : Texture2D.blackTexture;
            shader.SetTexture(s_kernel, PosMapId, pos);
            shader.SetInt(PosWidthId, pos.width);
            shader.SetInt(PosHeightId, pos.height);
            shader.SetInt(ClipToBoxId, useClip ? 1 : 0);
            shader.SetMatrix(WorldToBoxId, useClip ? clip.Value.rootToBox : Matrix4x4.identity);
            shader.SetFloat(BoxHeightId, useClip ? clip.Value.boxHeight : 0f);

            shader.Dispatch(s_kernel, Groups(dst.width), Groups(dst.height), 1);
        }

        /// <summary>
        /// src（プレビューの結果 RT）にハイライトを掛けた新しい RT を作って返す。src と同じ大きさ・形式・ミップの有無・サンプラー設定。
        /// 返した RT は呼び出し側の所有（RecolorPipeline.DestroyWorkTexture で破棄）。GPU が使えなければ null
        /// </summary>
        internal static RenderTexture CreateHighlighted(RenderTexture src, RenderTexture mask, BoxClip? clip = null)
        {
            if (src == null || mask == null || !mask.IsCreated() || !IsAvailable) return null;

            var dst = RecolorPipeline.CreateWorkTexture(src.width, src.height, "ClickRecolor_Highlight", src, src.useMipMap);
            bool succeeded = false;
            try
            {
                Apply(src, mask, dst, clip);
                // 遠目でちらつかないようにミップを作る（書き込みは mip 0 だけ）
                if (dst.useMipMap) dst.GenerateMips();
                succeeded = true;
                return dst;
            }
            finally
            {
                if (!succeeded) RecolorPipeline.DestroyWorkTexture(dst);
            }
        }

        private static int Groups(int size) => (size + ThreadGroupSize - 1) / ThreadGroupSize;
    }
}
