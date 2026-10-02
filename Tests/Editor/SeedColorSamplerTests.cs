using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>種色（クリック位置の 5×5 ガウス加重平均、Oklab）のテスト</summary>
    public class SeedColorSamplerTests
    {
        private const int Size = 8;

        private static readonly Color Base = new Color(0.6f, 0.2f, 0.1f, 1f);
        private static readonly Color Noise = new Color(0.05f, 0.8f, 0.9f, 1f);

        private static Color[] Fill(Color color)
        {
            var pixels = new Color[Size * Size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            return pixels;
        }

        private static Vector3 Lab(Color c) => OklabConverter.LinearRGBToOklab(new Vector3(c.r, c.g, c.b));

        [Test]
        public void 近傍1画素のノイズに引っ張られない()
        {
            var pixels = Fill(Base);
            // クリックした画素そのものがノイズ（重みが一番大きい画素）
            pixels[4 * Size + 4] = Noise;

            Vector3 seed = SeedColorSampler.SampleOklab(pixels, Size, Size, new Vector2Int(4, 4));

            float noiseDistance = Vector3.Distance(Lab(Noise), Lab(Base));
            // 中央の重みは 36/256 ≈ 0.14。ノイズとの距離の 2 割未満に収まる
            Assert.That(Vector3.Distance(seed, Lab(Base)), Is.LessThan(noiseDistance * 0.2f));
        }

        [Test]
        public void 透明な画素は数えない()
        {
            var pixels = Fill(Base);
            // 周りの透明な画素はどんな色でも無視する
            for (int x = 2; x <= 6; x++) pixels[5 * Size + x] = new Color(Noise.r, Noise.g, Noise.b, 0f);

            Vector3 seed = SeedColorSampler.SampleOklab(pixels, Size, Size, new Vector2Int(4, 4));

            Assert.That(Vector3.Distance(seed, Lab(Base)), Is.LessThan(1e-4f));
        }

        [Test]
        public void 画像の角でも画像内の画素だけで平均する()
        {
            var pixels = Fill(Base);

            Vector3 seed = SeedColorSampler.SampleOklab(pixels, Size, Size, new Vector2Int(0, 0));

            Assert.That(Vector3.Distance(seed, Lab(Base)), Is.LessThan(1e-4f));
        }

        [Test]
        public void RT_から読んでも同じ位置の色を取る()
        {
            if (!SystemInfo.supportsComputeShaders
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                Assert.Ignore("この環境はcompute shader（ARGBHalfへの書き込み）に対応していません");
            }

            // 左下の 4×4 だけ Noise、他は Base。(1,1) は周りも Noise、(6,6) は周りも Base
            var pixels = Fill(Base);
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) pixels[y * Size + x] = Noise;

            var sourceTex = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var source = RecolorPipeline.CreateWorkTexture(Size, Size, "SeedColorSamplerTests_Source");
            var previous = RenderTexture.active;
            try
            {
                sourceTex.SetPixels(pixels);
                sourceTex.Apply(false);
                Graphics.Blit(sourceTex, source);
                RenderTexture.active = previous;

                Vector3 low = SeedColorSampler.SampleOklab(source, new Vector2Int(1, 1));
                Vector3 high = SeedColorSampler.SampleOklab(source, new Vector2Int(6, 6));

                // ARGBHalf を通るので誤差は half 精度ぶん許す
                Assert.That(Vector3.Distance(low, Lab(Noise)), Is.LessThan(5e-3f), "左下（Noise）");
                Assert.That(Vector3.Distance(high, Lab(Base)), Is.LessThan(5e-3f), "右上（Base）");
                Assert.That(RenderTexture.active, Is.EqualTo(previous), "アクティブなRTを元に戻していない");
            }
            finally
            {
                RenderTexture.active = previous;
                RecolorPipeline.DestroyWorkTexture(source);
                Object.DestroyImmediate(sourceTex);
            }
        }
    }
}
