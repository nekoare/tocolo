using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>なぞって調整（PaintAdjustImage）を選択マスクに掛ける</summary>
    internal static class PaintAdjustMask
    {
        /// <summary>
        /// mask（R8、はみ出し幅・ぼかしの前）に、add なら足した所を最大値で、remove なら消した所を引き算で掛ける。
        /// 調整の画像とマスクの大きさが違えば、双線形で縮めて（引き伸ばして）掛ける（Upload）。
        /// edgeOffset はブラシのはみ出し（RecolorEdit.paintEdgeOffset。ShiftEdge）、coverage は全チャート被覆（足した所を広げる先を面のある画素に絞る）
        /// </summary>
        internal static void Apply(RenderTexture mask, PaintAdjustImage image, bool add, bool remove, int edgeOffset = 0, RenderTexture coverage = null)
        {
            if (mask == null || image == null || !Morphology.IsAvailable) return;
            if (add) Combine(mask, image, PaintState.Add, subtract: false, edgeOffset, coverage);
            if (remove) Combine(mask, image, PaintState.Remove, subtract: true, edgeOffset, coverage);
        }

        /// <summary>
        /// state の画素のマスク stateMask の境目を、選択が offset px 広がる向きにずらす（負なら狭まる向き）。
        /// 足した所は広げ（面のある画素へだけ。UV のすき間への広がりは仕上げのはみ出し幅に任せる）、消した所は削る
        /// </summary>
        internal static void ShiftEdge(RenderTexture stateMask, byte state, int offset, RenderTexture coverage)
        {
            if (stateMask == null || offset == 0) return;
            bool grow = (state == PaintState.Add) == (offset > 0);
            int radius = Mathf.Abs(offset);
            if (!grow)
            {
                Morphology.Erode(stateMask, radius);
                return;
            }
            // 消した所を広げる（選択を狭める）のは選んだパーツの中だけに効くので、行き先を絞らなくてよい
            if (state == PaintState.Add && coverage != null) Morphology.DilateInto(stateMask, coverage, radius);
            else Morphology.Dilate(stateMask, radius);
        }

        /// <summary>
        /// image の state の画素を 1、それ以外を 0 にした width×height のマスク（呼び出し側が MaskTextures.Destroy する）。
        /// state の画素が無ければ null
        /// </summary>
        internal static RenderTexture CreateStateMask(PaintAdjustImage image, byte state, int width, int height)
        {
            if (image == null || !image.Contains(state)) return null;
            var rt = MaskTextures.Create(width, height, "ClickRecolor_PaintAdjustMask");
            try
            {
                Upload(image, state, rt);
                return rt;
            }
            catch
            {
                MaskTextures.Destroy(rt);
                throw;
            }
        }

        private static void Combine(RenderTexture mask, PaintAdjustImage image, byte state, bool subtract, int edgeOffset, RenderTexture coverage)
        {
            if (!image.Contains(state)) return;
            var tmp = MaskTextures.GetTemporary(mask.width, mask.height);
            try
            {
                Upload(image, state, tmp);
                ShiftEdge(tmp, state, edgeOffset, coverage);
                if (subtract) Morphology.Subtract(mask, tmp);
                else Morphology.Max(mask, tmp);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(tmp);
            }
        }

        /// <summary>
        /// image の state の画素を 1 にした画像を target（大きさは問わない）へ写す。image の方が大きければ、双線形の Blit で半分ずつ縮めてから写す
        /// （2 倍→1 倍の双線形はちょうど 2×2 の平均になるので、調整の画像の 2 倍の細かさが境目の画素の途中の値になる。
        /// 一度に 4 倍以上縮めると、双線形は 4 画素しか見ないので間の画素を読み飛ばし、細い三角形が消えたり境目がまた階段になったりする）
        /// </summary>
        private static void Upload(PaintAdjustImage image, byte state, RenderTexture target)
        {
            var bytes = new byte[image.states.Length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = image.states[i] == state ? (byte)255 : (byte)0;
            var texture = new Texture2D(image.width, image.height, TextureFormat.R8, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var previous = RenderTexture.active;
            var halves = new List<RenderTexture>();
            try
            {
                texture.LoadRawTextureData(bytes);
                texture.Apply(false, false);
                Texture source = texture;
                int width = image.width, height = image.height;
                while (width > target.width * 2 || height > target.height * 2)
                {
                    width = Mathf.Max(target.width, (width + 1) / 2);
                    height = Mathf.Max(target.height, (height + 1) / 2);
                    var half = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
                    half.filterMode = FilterMode.Bilinear;
                    half.wrapMode = TextureWrapMode.Clamp;
                    halves.Add(half);
                    Graphics.Blit(source, half);
                    source = half;
                }
                Graphics.Blit(source, target);
            }
            finally
            {
                RenderTexture.active = previous;
                foreach (var half in halves) RenderTexture.ReleaseTemporary(half);
                Object.DestroyImmediate(texture);
            }
        }
    }
}
