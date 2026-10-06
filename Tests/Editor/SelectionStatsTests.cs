using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 選択内の統計（CPU）のテスト。灰色は Oklab の L = 線形値の立方根になるので、線形値 = L³ で作る
    /// </summary>
    public class SelectionStatsTests
    {
        private const float Tolerance = 1e-3f;

        private static Color GrayOfL(float l, float alpha = 1f)
        {
            float v = l * l * l;
            return new Color(v, v, v, alpha);
        }

        private static void Add(List<Color> pixels, List<float> mask, int count, Color color, float m)
        {
            for (int i = 0; i < count; i++)
            {
                pixels.Add(color);
                mask.Add(m);
            }
        }

        [Test]
        public void パーセンタイルはマスク加重で取る()
        {
            var pixels = new List<Color>();
            var mask = new List<float>();
            // 加重あり: 合計 100 + 55 + 10 = 165。95% = 156.75 は 3 群目で初めて届く → 0.9
            // 加重なし（画素数）なら 合計 210、95% = 199.5 は 2 群目で届く → 0.5 になるはず
            Add(pixels, mask, 100, GrayOfL(0.2f), 1f);
            Add(pixels, mask, 100, GrayOfL(0.5f), 0.55f);
            Add(pixels, mask, 10, GrayOfL(0.9f), 1f);

            var stats = SelectionStats.Compute(pixels.ToArray(), mask.ToArray());

            Assert.That(stats.count, Is.EqualTo(210));
            Assert.That(stats.lP05, Is.EqualTo(0.2f).Within(Tolerance));
            Assert.That(stats.lP95, Is.EqualTo(0.9f).Within(Tolerance));
        }

        [Test]
        public void マスク半分以下と透明な画素は数えない()
        {
            var pixels = new List<Color>();
            var mask = new List<float>();
            Add(pixels, mask, 100, GrayOfL(0.3f), 1f);
            Add(pixels, mask, 100, GrayOfL(0.6f), 1f);
            // 数えないはずの画素: マスク 0.5 ちょうど（M > 0.5 が条件）・α < 0.01
            Add(pixels, mask, 500, GrayOfL(1f), 0.5f);
            Add(pixels, mask, 500, GrayOfL(0f, alpha: 0.005f), 1f);

            var stats = SelectionStats.Compute(pixels.ToArray(), mask.ToArray());

            Assert.That(stats.count, Is.EqualTo(200));
            Assert.That(stats.lP05, Is.EqualTo(0.3f).Within(Tolerance));
            Assert.That(stats.lP95, Is.EqualTo(0.6f).Within(Tolerance));
        }

        [Test]
        public void 代表色はマスク加重平均の_sRGB()
        {
            var pixels = new List<Color>();
            var mask = new List<float>();
            // 線形の赤 1.0（加重 1）と線形の赤 0.0（加重 1）→ 平均の線形 0.5 → sRGB に戻した値
            Add(pixels, mask, 50, new Color(1f, 0f, 0f, 1f), 1f);
            Add(pixels, mask, 50, new Color(0f, 0f, 0f, 1f), 1f);

            var stats = SelectionStats.Compute(pixels.ToArray(), mask.ToArray());

            Assert.That(stats.rep.r, Is.EqualTo(OklabConverter.LinearToSRGB(0.5f)).Within(Tolerance));
            Assert.That(stats.rep.g, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(stats.rep.b, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(stats.rep.a, Is.EqualTo(1f));
        }

        [Test]
        public void 小さい選択では代表色の_L_の前後_0_05_に置く()
        {
            var pixels = new List<Color>();
            var mask = new List<float>();
            // 有効画素 30 < 64。L の分布は 0.2 と 0.8 に割れているが、パーセンタイルは使わない
            Add(pixels, mask, 15, GrayOfL(0.2f), 1f);
            Add(pixels, mask, 15, GrayOfL(0.8f), 1f);

            var stats = SelectionStats.Compute(pixels.ToArray(), mask.ToArray());

            Vector3 repLinear = OklabConverter.SRGBToLinear(stats.rep);
            float repL = OklabConverter.LinearRGBToOklab(repLinear).x;
            Assert.That(stats.count, Is.EqualTo(30));
            Assert.That(stats.lP05, Is.EqualTo(repL - 0.05f).Within(Tolerance));
            Assert.That(stats.lP95, Is.EqualTo(repL + 0.05f).Within(Tolerance));
        }

        [Test]
        public void 無彩色だけでも_NaN_にならない()
        {
            var pixels = new List<Color>();
            var mask = new List<float>();
            Add(pixels, mask, 100, GrayOfL(0.4f), 1f);
            Add(pixels, mask, 100, GrayOfL(0.7f), 0.8f);

            var stats = SelectionStats.Compute(pixels.ToArray(), mask.ToArray());

            AssertFinite(stats);
        }

        [Test]
        public void 有効画素が無くても_NaN_にならない()
        {
            var stats = SelectionStats.Compute(new[] { GrayOfL(0.5f) }, new[] { 0f });

            Assert.That(stats.count, Is.EqualTo(0));
            AssertFinite(stats);
        }

        [Test]
        public void 代表色相は_a_b_の加重平均の角度()
        {
            var pixels = new List<Color>();
            var mask = new List<float>();
            // 線形の赤だけ → 色相は赤の Oklab 色相
            Add(pixels, mask, 100, new Color(0.8f, 0.05f, 0.05f, 1f), 1f);

            var stats = SelectionStats.Compute(pixels.ToArray(), mask.ToArray());

            float expected = OklabConverter.OklabToOklch(
                OklabConverter.LinearRGBToOklab(new Vector3(0.8f, 0.05f, 0.05f))).z;
            Assert.That(stats.hDominant, Is.EqualTo(expected).Within(Tolerance));
        }

        [Test]
        public void RT_経路でもマスクの内側だけを数える()
        {
            if (!SystemInfo.supportsComputeShaders
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf)
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.R8))
            {
                Assert.Ignore("この環境はcompute shader（ARGBHalf / R8への書き込み）に対応していません");
            }

            const int size = 8;
            // 左半分（x < 4）は L = 0.4、右半分は L = 0.9。マスクは左半分だけ 1
            var pixels = new Color[size * size];
            var maskBytes = new byte[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool inside = x < size / 2;
                    pixels[y * size + x] = GrayOfL(inside ? 0.4f : 0.9f);
                    maskBytes[y * size + x] = inside ? (byte)255 : (byte)0;
                }
            }

            var sourceTex = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var source = RecolorPipeline.CreateWorkTexture(size, size, "SelectionStatsTests_Source");
            var mask = MaskTextures.Create(size, size, "SelectionStatsTests_Mask");
            var previous = RenderTexture.active;
            try
            {
                sourceTex.SetPixels(pixels);
                sourceTex.Apply(false);
                // どちらも Linear なので色空間の変換は掛からない
                Graphics.Blit(sourceTex, source);
                RenderTexture.active = previous;
                FloodFill.WriteR8(maskBytes, mask);

                var stats = SelectionStats.Compute(source, mask);

                // 数えるのは左半分の 32 画素。64 未満なので p05 は代表色の L − 0.05
                Assert.That(stats.count, Is.EqualTo(size * size / 2));
                Assert.That(stats.lP05, Is.EqualTo(0.4f - SelectionStats.FallbackHalfRange).Within(2e-3f));
            }
            finally
            {
                RenderTexture.active = previous;
                RecolorPipeline.DestroyWorkTexture(source);
                MaskTextures.Destroy(mask);
                Object.DestroyImmediate(sourceTex);
            }
        }

        /// <summary>
        /// 複数のマスクの統計をまとめて取る（色の読み戻しは 1 回・マスクは範囲だけ）と、1 つずつ Compute するのと同じ値になるか。
        /// 縮めて読む（maxSize 32）・縮めない（0）の両方。範囲は塊にぴったり、または null（全体）
        /// </summary>
        [TestCase(32)]
        [TestCase(0)]
        public void 複数のマスクをまとめて取っても1つずつ取るのと同じ(int maxSize)
        {
            if (!SystemInfo.supportsComputeShaders
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf)
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.R8))
            {
                Assert.Ignore("この環境はcompute shader（ARGBHalf / R8への書き込み）に対応していません");
            }

            const int size = 64;
            var random = new System.Random(3);
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble(), 1f);
            }
            // 塊（縁に 0.5 前後の値を混ぜる）と、範囲
            var blobs = new[] { new RectInt(3, 5, 12, 9), new RectInt(30, 40, 20, 15), new RectInt(50, 2, 10, 30) };
            var bounds = new RectInt?[] { blobs[0], null, blobs[2] };

            var sourceTex = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var source = RecolorPipeline.CreateWorkTexture(size, size, "SelectionStatsTests_Source");
            var masks = new List<RenderTexture>();
            var previous = RenderTexture.active;
            try
            {
                sourceTex.SetPixels(pixels);
                sourceTex.Apply(false);
                Graphics.Blit(sourceTex, source);
                RenderTexture.active = previous;
                foreach (var blob in blobs)
                {
                    var bytes = new byte[size * size];
                    for (int y = blob.yMin; y < blob.yMax; y++)
                    {
                        for (int x = blob.xMin; x < blob.xMax; x++)
                        {
                            bool edge = x == blob.xMin || y == blob.yMin;
                            bytes[y * size + x] = edge ? (byte)(120 + (x + y) % 20) : (byte)255;
                        }
                    }
                    var mask = MaskTextures.Create(size, size, "SelectionStatsTests_Mask");
                    masks.Add(mask);
                    FloodFill.WriteR8(bytes, mask);
                }

                var parts = SelectionStats.ComputeParts(source, masks, bounds, maxSize);

                Assert.That(parts.Length, Is.EqualTo(masks.Count));
                for (int i = 0; i < masks.Count; i++)
                {
                    var one = SelectionStats.Compute(source, masks[i], maxSize);
                    Assert.That(parts[i].count, Is.EqualTo(one.count), $"{i}: count");
                    Assert.That(parts[i].count, Is.GreaterThan(0), $"{i}: 数えている");
                    Assert.That(parts[i].lP05, Is.EqualTo(one.lP05), $"{i}: lP05");
                    Assert.That(parts[i].lP95, Is.EqualTo(one.lP95), $"{i}: lP95");
                    Assert.That(parts[i].hDominant, Is.EqualTo(one.hDominant), $"{i}: hDominant");
                    Assert.That(parts[i].rep, Is.EqualTo(one.rep), $"{i}: rep");
                }
            }
            finally
            {
                RenderTexture.active = previous;
                RecolorPipeline.DestroyWorkTexture(source);
                foreach (var mask in masks) MaskTextures.Destroy(mask);
                Object.DestroyImmediate(sourceTex);
            }
        }

        // ── Merge（連結の統計の共有）──

        [Test]
        public void Merge_は_p05_の最小と_p95_の最大と_count_の合計を取る()
        {
            var merged = SelectionStats.Merge(new[]
            {
                new Stats { lP05 = 0.1f, lP95 = 0.3f, count = 100 },
                new Stats { lP05 = 0.6f, lP95 = 0.9f, count = 300 },
            });

            Assert.That(merged.lP05, Is.EqualTo(0.1f).Within(Tolerance));
            Assert.That(merged.lP95, Is.EqualTo(0.9f).Within(Tolerance));
            Assert.That(merged.count, Is.EqualTo(400));
        }

        [Test]
        public void Merge_の代表色相は_count_重み付きの円平均()
        {
            // +170° と −170° の平均は 0° ではなく 180° 付近（円平均）。count が同じなので真ん中
            float a = 170f * Mathf.Deg2Rad;
            float b = -170f * Mathf.Deg2Rad;
            var merged = SelectionStats.Merge(new[]
            {
                new Stats { hDominant = a, count = 100 },
                new Stats { hDominant = b, count = 100 },
            });
            Assert.That(Mathf.Abs(Mathf.Abs(merged.hDominant) - Mathf.PI), Is.LessThan(Tolerance));

            // 0° が 3 倍の重み、90° が 1 倍 → atan2(1, 3)
            var weighted = SelectionStats.Merge(new[]
            {
                new Stats { hDominant = 0f, count = 300 },
                new Stats { hDominant = Mathf.PI / 2f, count = 100 },
            });
            Assert.That(weighted.hDominant, Is.EqualTo(Mathf.Atan2(1f, 3f)).Within(Tolerance));
        }

        [Test]
        public void Merge_の代表色は_count_重み付き平均()
        {
            var merged = SelectionStats.Merge(new[]
            {
                new Stats { rep = new Color(1f, 0f, 0f, 1f), count = 300 },
                new Stats { rep = new Color(0f, 0f, 1f, 1f), count = 100 },
            });

            Assert.That(merged.rep.r, Is.EqualTo(0.75f).Within(Tolerance));
            Assert.That(merged.rep.g, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(merged.rep.b, Is.EqualTo(0.25f).Within(Tolerance));
            Assert.That(merged.rep.a, Is.EqualTo(1f).Within(Tolerance));
        }

        [Test]
        public void Merge_は_count_0_の統計を無視する()
        {
            var merged = SelectionStats.Merge(new[]
            {
                new Stats { lP05 = 0f, lP95 = 1f, hDominant = 2f, rep = Color.white, count = 0 },
                new Stats { lP05 = 0.4f, lP95 = 0.5f, hDominant = 1f, rep = Color.red, count = 100 },
            });

            Assert.That(merged.lP05, Is.EqualTo(0.4f).Within(Tolerance));
            Assert.That(merged.lP95, Is.EqualTo(0.5f).Within(Tolerance));
            Assert.That(merged.hDominant, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(merged.rep.r, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(merged.rep.g, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(merged.count, Is.EqualTo(100));
        }

        [Test]
        public void Merge_は全部_count_0_なら最初の要素を返す()
        {
            var first = new Stats { lP05 = 0.2f, lP95 = 0.3f, hDominant = 0.5f, rep = Color.green, count = 0 };
            var merged = SelectionStats.Merge(new[] { first, new Stats { lP05 = 0.7f, lP95 = 0.8f, count = 0 } });

            Assert.That(merged.lP05, Is.EqualTo(0.2f));
            Assert.That(merged.lP95, Is.EqualTo(0.3f));
            Assert.That(merged.hDominant, Is.EqualTo(0.5f));
            Assert.That(merged.count, Is.EqualTo(0));
        }

        private static void AssertFinite(Stats stats)
        {
            Assert.That(float.IsNaN(stats.hDominant) || float.IsInfinity(stats.hDominant), Is.False, "hDominant");
            Assert.That(float.IsNaN(stats.lP05) || float.IsInfinity(stats.lP05), Is.False, "lP05");
            Assert.That(float.IsNaN(stats.lP95) || float.IsInfinity(stats.lP95), Is.False, "lP95");
            Assert.That(float.IsNaN(stats.rep.r) || float.IsNaN(stats.rep.g) || float.IsNaN(stats.rep.b), Is.False, "rep");
        }
    }
}
