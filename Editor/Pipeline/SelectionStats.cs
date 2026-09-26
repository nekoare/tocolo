using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Masks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.Pipeline
{
    /// <summary>選択内の統計（設計 §7.1）。元テクスチャ基準で 1 回だけ取る（スライダーで揺れない）</summary>
    internal struct Stats
    {
        /// <summary>M 加重の Oklab L の 5 パーセンタイル</summary>
        public float lP05;
        /// <summary>M 加重の Oklab L の 95 パーセンタイル</summary>
        public float lP95;
        /// <summary>代表色相（M 加重の Oklab a,b 平均の角度、ラジアン）</summary>
        public float hDominant;
        /// <summary>M 加重平均色（sRGB、α = 1）。色パネルの「元の色」</summary>
        public Color rep;
        /// <summary>数えた画素数（M &gt; 0.5 かつ α ≥ 0.01）</summary>
        public int count;
    }

    /// <summary>
    /// 選択マスクの内側の色の統計を取る。GPU の作業 RT は ≤1024² に縮めて読み戻してから CPU で集計する
    /// </summary>
    internal static class SelectionStats
    {
        /// <summary>読み戻す大きさ（長辺）の上限</summary>
        internal const int MaxReadbackSize = 1024;
        /// <summary>これ未満の画素数ではパーセンタイルを使わない</summary>
        internal const int MinCount = 64;
        /// <summary>小さい選択で代表色の L の前後に置く幅</summary>
        internal const float FallbackHalfRange = 0.05f;

        private const float MaskThreshold = 0.5f;
        private const float AlphaThreshold = 0.01f;

        /// <summary>
        /// 線形 RGB の作業 RT と選択マスクから統計を取る（maxSize 以下に縮めて読み戻す）
        /// </summary>
        internal static Stats Compute(RenderTexture sourceLinear, RenderTexture mask, int maxSize = MaxReadbackSize)
        {
            ReadbackDownscaled(sourceLinear, mask, maxSize, out var pixels, out var maskValues);
            return Compute(pixels, maskValues);
        }

        /// <summary>
        /// linearPixels（線形 RGB + α）と mask（0..1、同じ並び）から統計を取る。
        /// M &gt; 0.5 かつ α ≥ 0.01 の画素だけを M 加重で数える。
        /// 数えた画素が MinCount 未満なら、p05 / p95 は代表色の L ± 0.05 に置く（ゼロ除算と暴走を防ぐ）
        /// </summary>
        internal static Stats Compute(Color[] linearPixels, float[] mask)
        {
            if (linearPixels == null) throw new ArgumentNullException(nameof(linearPixels));
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            if (mask.Length < linearPixels.Length) throw new ArgumentException("mask の長さが画素数より短いです", nameof(mask));

            var ls = new float[linearPixels.Length];
            var weights = new float[linearPixels.Length];
            int count = 0;
            double totalWeight = 0, sumR = 0, sumG = 0, sumB = 0, sumOkA = 0, sumOkB = 0;

            for (int i = 0; i < linearPixels.Length; i++)
            {
                float m = mask[i];
                Color c = linearPixels[i];
                if (!(m > MaskThreshold) || c.a < AlphaThreshold) continue;

                var lin = new Vector3(c.r, c.g, c.b);
                Vector3 lab = OklabConverter.LinearRGBToOklab(lin);
                ls[count] = lab.x;
                weights[count] = m;
                count++;

                totalWeight += m;
                sumR += c.r * m;
                sumG += c.g * m;
                sumB += c.b * m;
                sumOkA += lab.y * m;
                sumOkB += lab.z * m;
            }

            Vector3 averageLinear = Vector3.zero;
            float hDominant = 0f;
            if (totalWeight > 0)
            {
                averageLinear = new Vector3((float)(sumR / totalWeight), (float)(sumG / totalWeight), (float)(sumB / totalWeight));
                float a = (float)(sumOkA / totalWeight);
                float b = (float)(sumOkB / totalWeight);
                // 両方 0（完全な無彩色）なら角度が定まらないので 0 にする
                hDominant = a == 0f && b == 0f ? 0f : Mathf.Atan2(b, a);
            }

            var stats = new Stats
            {
                hDominant = hDominant,
                rep = OklabConverter.LinearToSRGB(averageLinear, 1f),
                count = count,
            };

            if (count < MinCount)
            {
                float repL = OklabConverter.LinearRGBToOklab(averageLinear).x;
                stats.lP05 = repL - FallbackHalfRange;
                stats.lP95 = repL + FallbackHalfRange;
                return stats;
            }

            // L の昇順に並べ、重みの累積が q × 合計に届いた最初の画素の L を取る
            Array.Sort(ls, weights, 0, count);
            stats.lP05 = WeightedPercentile(ls, weights, count, totalWeight, 0.05);
            stats.lP95 = WeightedPercentile(ls, weights, count, totalWeight, 0.95);
            return stats;
        }

        /// <summary>
        /// 複数の統計を 1 つの分布としてまとめる（連結の全メンバーで明るさの範囲を共有するため）。
        /// p05 は最小・p95 は最大、代表色相は count 重み付きの円平均（sin/cos の平均の角度。両方 0 なら 0）、
        /// 代表色は count 重み付き平均、count は合計。count が 0 の統計は無視する。全部 0（または空）なら最初の要素（空なら既定値）
        /// </summary>
        internal static Stats Merge(IReadOnlyList<Stats> stats)
        {
            if (stats == null || stats.Count == 0) return default;

            bool any = false;
            float lP05 = 0f, lP95 = 0f;
            double total = 0, sumSin = 0, sumCos = 0, sumR = 0, sumG = 0, sumB = 0, sumA = 0;
            int count = 0;
            foreach (var s in stats)
            {
                if (s.count <= 0) continue;
                if (!any)
                {
                    lP05 = s.lP05;
                    lP95 = s.lP95;
                    any = true;
                }
                else
                {
                    lP05 = Mathf.Min(lP05, s.lP05);
                    lP95 = Mathf.Max(lP95, s.lP95);
                }
                double w = s.count;
                total += w;
                count += s.count;
                sumSin += Math.Sin(s.hDominant) * w;
                sumCos += Math.Cos(s.hDominant) * w;
                sumR += s.rep.r * w;
                sumG += s.rep.g * w;
                sumB += s.rep.b * w;
                sumA += s.rep.a * w;
            }
            if (!any) return stats[0];

            float sin = (float)(sumSin / total);
            float cos = (float)(sumCos / total);
            return new Stats
            {
                lP05 = lP05,
                lP95 = lP95,
                // 向きが打ち消し合って両方 0 なら角度が定まらないので 0 にする（Compute と同じ扱い）
                hDominant = sin == 0f && cos == 0f ? 0f : Mathf.Atan2(sin, cos),
                rep = new Color((float)(sumR / total), (float)(sumG / total), (float)(sumB / total), (float)(sumA / total)),
                count = count,
            };
        }

        private static float WeightedPercentile(float[] sortedValues, float[] weights, int count, double totalWeight, double q)
        {
            double threshold = q * totalWeight;
            double cumulative = 0;
            for (int i = 0; i < count; i++)
            {
                cumulative += weights[i];
                if (cumulative >= threshold) return sortedValues[i];
            }
            return sortedValues[count - 1];
        }

        /// <summary>
        /// source（線形 RGB の RT）と mask（R8）を、長辺 maxSize 以下の同じ大きさに縮めて CPU へ読み戻す。
        /// 並びは行優先・y = 0 が下（両方同じ）。sRGB 変換は掛けない（どちらも Linear）
        /// </summary>
        internal static void ReadbackDownscaled(
            RenderTexture source, RenderTexture mask, int maxSize, out Color[] pixels, out float[] maskValues)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (mask == null) throw new ArgumentNullException(nameof(mask));

            float scale = maxSize > 0 ? Mathf.Min(1f, maxSize / (float)Mathf.Max(source.width, source.height)) : 1f;
            int w = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int h = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));

            var colorDesc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGBHalf, 0)
            {
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
            };
            var colorRt = RenderTexture.GetTemporary(colorDesc);
            var maskRt = MaskTextures.GetTemporary(w, h);
            var colorTex = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true);
            // Blit / ReadPixels は RenderTexture.active を切り替えたまま戻さないので、最後に元へ戻す
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, colorRt);
                Graphics.Blit(mask, maskRt);

                RenderTexture.active = colorRt;
                colorTex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                colorTex.Apply(false);
                pixels = colorTex.GetPixels();

                var maskBytes = FloodFill.ReadR8(maskRt);
                maskValues = new float[maskBytes.Length];
                for (int i = 0; i < maskBytes.Length; i++) maskValues[i] = maskBytes[i] / 255f;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(colorTex);
                RenderTexture.ReleaseTemporary(colorRt);
                RenderTexture.ReleaseTemporary(maskRt);
            }
        }
    }
}
