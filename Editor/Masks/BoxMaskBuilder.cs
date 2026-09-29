using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>
    /// 影響範囲「箱の中」のマスク。対象テクスチャを使うスロットのメッシュを UV 空間に描き、表面が箱の内側なら 1 にする
    /// （BoxMask.shader。重なる UV は最大値で合成するので、左右で UV を共有する面のどちらかが箱の中なら選ばれる）。
    /// UV の切れ間・パーツの境界に関係なく、箱の面で色変えが切れる（ユーザー要望 2026-09-29）
    /// </summary>
    internal static class BoxMaskBuilder
    {
        /// <summary>スーパーサンプリングする一時 RT の一辺の上限（これを超える大きさでは等倍で描く）</summary>
        private const int SupersampleMaxSize = 8192;

        private static readonly int RootWorldToLocalId = Shader.PropertyToID("_RootWorldToLocal");
        private static readonly int RootToBoxId = Shader.PropertyToID("_RootToBox");
        private static readonly int BoxHalfId = Shader.PropertyToID("_BoxHalf");
        private static readonly int FeatherId = Shader.PropertyToID("_Feather");

        internal static bool IsAvailable
        {
            get
            {
                var shader = ShaderAssets.LoadShader(ShaderAssets.BoxMaskGuid);
                return shader != null && shader.isSupported;
            }
        }

        /// <summary>対象ルートのローカル → 箱のローカル（RecolorEdit の箱の位置・回転から）</summary>
        internal static Matrix4x4 RootToBox(Vector3 position, Quaternion rotation) =>
            Matrix4x4.TRS(position, Quaternion.Normalize(rotation), Vector3.one).inverse;

        /// <summary>
        /// root 配下の slots（PositionMap.CollectSlots）を UV 空間に描いて、箱の中のマスク（R8・Linear、width×height、0..1）を作る。
        /// 返した RT は呼び出し側の所有なので、使い終わったら MaskTextures.Destroy すること。
        /// feather は面のぼかし（各軸の半分の長さに対する割合。0 で硬い辺）。supersample が false なら等倍で描く（ドラッグ中の軽さ優先）。
        /// 描くスロットが無い・シェーダーが無ければ null
        /// </summary>
        internal static RenderTexture Build(
            Transform root, IReadOnlyList<PositionMap.Slot> slots, int width, int height,
            Vector3 boxPosition, Quaternion boxRotation, Vector3 boxSize, float feather, bool supersample = true)
        {
            if (root == null || slots == null || slots.Count == 0 || width <= 0 || height <= 0) return null;
            var shader = ShaderAssets.LoadShader(ShaderAssets.BoxMaskGuid);
            if (shader == null || !shader.isSupported) return null;

            var rt = MaskTextures.Create(width, height, "ClickRecolor_BoxMask");
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var previous = RenderTexture.active;
            // 切れ目の階段を減らすため 2 倍の解像度で描いて平均で縮める（スーパーサンプリング。上限 SupersampleMaxSize）。
            // 縮小は Bilinear の Blit（2 倍→1 倍なら 4 画素の平均になる）
            // supersample=false（箱のドラッグ中）は等倍で軽く描く（離したときに掛け直す）
            supersample = supersample && width * 2 <= SupersampleMaxSize && height * 2 <= SupersampleMaxSize;
            RenderTexture target = rt;
            if (supersample)
            {
                target = MaskTextures.GetTemporary(width * 2, height * 2);
                target.filterMode = FilterMode.Bilinear;
            }
            try
            {
                Graphics.SetRenderTarget(target);
                GL.Clear(false, true, Color.clear);
                material.SetMatrix(RootWorldToLocalId, root.worldToLocalMatrix);
                material.SetMatrix(RootToBoxId, RootToBox(boxPosition, boxRotation));
                // 大きさは負でもよい（ハンドルで反転させたとき）ので絶対値の半分
                material.SetVector(BoxHalfId, new Vector3(Mathf.Abs(boxSize.x), Mathf.Abs(boxSize.y), Mathf.Abs(boxSize.z)) * 0.5f);
                material.SetFloat(FeatherId, Mathf.Clamp01(feather));
                GL.PushMatrix();
                try
                {
                    PositionMap.DrawSlots(material, slots);
                }
                finally
                {
                    GL.PopMatrix();
                }
                if (supersample)
                {
                    // 縮める前に 2 倍側で 1 画素（＝等倍の半画素）広げて、パーツの縁の半端な画素（縁の描き残し・縁の平均）を埋める。
                    // 切り口のなだらかさは幅が保たれるので残る
                    Morphology.Dilate(target, 1);
                    Graphics.Blit(target, rt);
                }
            }
            catch
            {
                MaskTextures.Destroy(rt);
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
                if (supersample) RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(material);
            }
            return rt;
        }
    }
}
