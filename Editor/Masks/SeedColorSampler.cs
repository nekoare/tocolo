using System;
using Nekoare.ClickRecolor.Editor.Colors;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>
    /// 種色（設計 §6.1）: クリックしたテクセルの 5×5 近傍のガウス加重平均を Oklab で取る。
    /// α &lt; 0.01 の画素は数えない。1 画素のノイズで選択が化けないようにするため
    /// </summary>
    internal static class SeedColorSampler
    {
        /// <summary>近傍の半径（5×5）</summary>
        internal const int Radius = 2;

        private const float AlphaThreshold = 0.01f;

        /// <summary>5 タップの二項係数（σ ≈ 1 のガウスの近似）。重みは縦横の積</summary>
        private static readonly float[] Kernel = { 1f, 4f, 6f, 4f, 1f };

        /// <summary>
        /// linearPixels（線形 RGB + α、行優先・y = 0 が下、w×h）の texel を中心にした種色（Oklab）を返す。
        /// 画像の外の画素は数えない。数える画素が 1 つも無ければ（全部透明）texel の画素をそのまま Oklab にする
        /// </summary>
        internal static Vector3 SampleOklab(Color[] linearPixels, int w, int h, Vector2Int texel)
        {
            if (linearPixels == null) throw new ArgumentNullException(nameof(linearPixels));
            if (w <= 0 || h <= 0 || linearPixels.Length < w * h) throw new ArgumentException("画素数がw×hより少ないです", nameof(linearPixels));
            int cx = Mathf.Clamp(texel.x, 0, w - 1);
            int cy = Mathf.Clamp(texel.y, 0, h - 1);

            Vector3 sum = Vector3.zero;
            float totalWeight = 0f;
            for (int dy = -Radius; dy <= Radius; dy++)
            {
                int y = cy + dy;
                if (y < 0 || y >= h) continue;
                for (int dx = -Radius; dx <= Radius; dx++)
                {
                    int x = cx + dx;
                    if (x < 0 || x >= w) continue;
                    Color c = linearPixels[y * w + x];
                    if (c.a < AlphaThreshold) continue;
                    float weight = Kernel[dx + Radius] * Kernel[dy + Radius];
                    sum += OklabConverter.LinearRGBToOklab(new Vector3(c.r, c.g, c.b)) * weight;
                    totalWeight += weight;
                }
            }

            if (totalWeight > 0f) return sum / totalWeight;
            Color center = linearPixels[cy * w + cx];
            return OklabConverter.LinearRGBToOklab(new Vector3(center.r, center.g, center.b));
        }

        /// <summary>
        /// 線形 RGB の作業 RT（ARGBHalf）から texel の 5×5 近傍だけを読み戻して種色（Oklab）を返す。
        /// 近傍は CopyTexture で小さな RT へ写してから読む（compute の添字と同じ並び: y = 0 が下）
        /// </summary>
        internal static Vector3 SampleOklab(RenderTexture sourceLinear, Vector2Int texel)
        {
            if (sourceLinear == null) throw new ArgumentNullException(nameof(sourceLinear));
            int cx = Mathf.Clamp(texel.x, 0, sourceLinear.width - 1);
            int cy = Mathf.Clamp(texel.y, 0, sourceLinear.height - 1);
            int x0 = Mathf.Max(0, cx - Radius);
            int y0 = Mathf.Max(0, cy - Radius);
            int x1 = Mathf.Min(sourceLinear.width - 1, cx + Radius);
            int y1 = Mathf.Min(sourceLinear.height - 1, cy + Radius);
            int rw = x1 - x0 + 1;
            int rh = y1 - y0 + 1;

            var desc = new RenderTextureDescriptor(rw, rh, sourceLinear.format, 0)
            {
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
            };
            var region = RenderTexture.GetTemporary(desc);
            if (!region.IsCreated()) region.Create();
            var tex = new Texture2D(rw, rh, TextureFormat.RGBAHalf, false, true);
            // ReadPixels は RenderTexture.active を切り替えたまま戻さないので、最後に元へ戻す
            var previous = RenderTexture.active;
            try
            {
                Graphics.CopyTexture(sourceLinear, 0, 0, x0, y0, rw, rh, region, 0, 0, 0, 0);
                RenderTexture.active = region;
                tex.ReadPixels(new Rect(0, 0, rw, rh), 0, 0, false);
                tex.Apply(false);
                return SampleOklab(tex.GetPixels(), rw, rh, new Vector2Int(cx - x0, cy - y0));
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(tex);
                RenderTexture.ReleaseTemporary(region);
            }
        }
    }
}
