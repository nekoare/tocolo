using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>「元のグラデーションを打ち消す」の帯ごとの統計（BandStats の CPU 版）</summary>
    public class BandStatsTests
    {
        /// <summary>
        /// 高さ y ∈ [0, 1] で下が暗く上が明るいグラデーション（明るさ = 0.2 + 0.6y）に、帯の中の小さな陰影（±0.05）を足した画素を作る
        /// </summary>
        private static (Color[] pixels, float[] mask, Color[] positions) MakeGradient(int count)
        {
            var pixels = new Color[count];
            var mask = new float[count];
            var positions = new Color[count];
            for (int i = 0; i < count; i++)
            {
                float y = i / (float)(count - 1);
                float shade = (i % 2 == 0) ? 0.05f : -0.05f;
                float v = Mathf.Clamp01(0.2f + 0.6f * y + shade);
                pixels[i] = new Color(v, v, v, 1f);
                mask[i] = 1f;
                positions[i] = new Color(0f, y, 0f, 1f);
            }
            return (pixels, mask, positions);
        }

        [Test]
        public void 上下のグラデーションでは帯の明るさの基準が下から上へ上がる()
        {
            var (pixels, mask, positions) = MakeGradient(4000);
            var bands = BandStats.Compute(pixels, mask, positions, Vector3.up);

            Assert.That(bands, Is.Not.Null);
            Assert.That(bands.values.Length, Is.EqualTo(BandStats.Count));
            Assert.That(bands.min, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(bands.max, Is.EqualTo(1f).Within(1e-4f));
            for (int b = 1; b < BandStats.Count; b++)
            {
                Assert.That(bands.values[b].x, Is.GreaterThan(bands.values[b - 1].x), $"帯 {b} の p05 が下の帯より明るい");
                Assert.That(bands.values[b].y, Is.GreaterThanOrEqualTo(bands.values[b].x), $"帯 {b} は p95 ≥ p05");
            }
        }

        [Test]
        public void 画素の少ない帯は隣の帯の値で埋まる()
        {
            var (pixels, mask, positions) = MakeGradient(4000);
            // 上半分の途中（0.5〜0.6）の画素を選択から外す → その辺りの帯は画素不足
            for (int i = 0; i < pixels.Length; i++)
            {
                if (positions[i].g > 0.5f && positions[i].g < 0.6f) mask[i] = 0f;
            }
            var bands = BandStats.Compute(pixels, mask, positions, Vector3.up);

            Assert.That(bands, Is.Not.Null);
            foreach (var v in bands.values)
            {
                Assert.That(v.x, Is.GreaterThan(0f), "空の帯が 0 のまま残っていない");
            }
        }

        [Test]
        public void 選択が小さすぎる_位置が描かれていない_向きが無いときは_null()
        {
            var (pixels, mask, positions) = MakeGradient(20);
            Assert.That(BandStats.Compute(pixels, mask, positions, Vector3.up), Is.Null, "画素が少なすぎる");

            var (p2, m2, pos2) = MakeGradient(4000);
            for (int i = 0; i < pos2.Length; i++) pos2[i].a = 0f;
            Assert.That(BandStats.Compute(p2, m2, pos2, Vector3.up), Is.Null, "位置が描かれていない");

            var (p3, m3, pos3) = MakeGradient(4000);
            Assert.That(BandStats.Compute(p3, m3, pos3, Vector3.zero), Is.Null, "向きが無い");
        }
    }
}
