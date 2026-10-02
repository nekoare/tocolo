using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// グラデーション付きスライダー（GradientSlider）の、グラデーション生成と再生成の条件のテスト
    /// </summary>
    public class GradientSliderTests
    {
        private const float Eps = 1e-4f;

        private static void AssertColor(Color actual, Color expected, string message)
        {
            Assert.That(actual.r, Is.EqualTo(expected.r).Within(Eps), message + "のR");
            Assert.That(actual.g, Is.EqualTo(expected.g).Within(Eps), message + "のG");
            Assert.That(actual.b, Is.EqualTo(expected.b).Within(Eps), message + "のB");
            Assert.That(actual.a, Is.EqualTo(expected.a).Within(Eps), message + "のA");
        }

        [Test]
        public void 彩度のグラデーションは_彩度0から1になる()
        {
            const float h = 0.3f, v = 0.8f;
            var pixels = GradientSlider.BuildGradient(t => Color.HSVToRGB(h, t, v), 32);

            Assert.That(pixels.Length, Is.EqualTo(32));
            AssertColor(pixels[0], Color.HSVToRGB(h, 0f, v), "左端");
            AssertColor(pixels[31], Color.HSVToRGB(h, 1f, v), "右端");
        }

        [Test]
        public void 赤のグラデーションは_他の成分を保って0から1になる()
        {
            const float g = 0.25f, b = 0.75f;
            var pixels = GradientSlider.BuildGradient(t => new Color(t, g, b), 32);

            AssertColor(pixels[0], new Color(0f, g, b, 1f), "左端");
            AssertColor(pixels[31], new Color(1f, g, b, 1f), "右端");
        }

        [Test]
        public void 色相のグラデーションは_両端が赤になる()
        {
            var pixels = GradientSlider.BuildGradient(t => Color.HSVToRGB(t, 1f, 1f), 64);

            Assert.That(pixels.Length, Is.EqualTo(64));
            AssertColor(pixels[0], Color.red, "左端");
            AssertColor(pixels[63], Color.red, "右端");
        }

        [Test]
        public void 不透明にする()
        {
            var pixels = GradientSlider.BuildGradient(t => new Color(t, t, t, 0.2f), 8);

            foreach (var c in pixels) Assert.That(c.a, Is.EqualTo(1f));
        }

        [Test]
        public void 再生成は色が変わったときだけ起きる()
        {
            var cache = new GradientSlider.Cache(32, "GradientSliderTests");
            try
            {
                int calls = 0;
                System.Func<float, Color> colorAt = t => { calls++; return new Color(t, 0f, 0f); };

                Assert.That(cache.Update(new Vector3(0.1f, 0.2f, 0.3f), colorAt), Is.True, "初回は作る");
                Assert.That(cache.Texture, Is.Not.Null);
                int afterFirst = calls;
                Assert.That(afterFirst, Is.EqualTo(32));

                Assert.That(cache.Update(new Vector3(0.1f, 0.2f, 0.3f), colorAt), Is.False, "同じ色なら作り直さない");
                Assert.That(calls, Is.EqualTo(afterFirst));

                Assert.That(cache.Update(new Vector3(0.1f, 0.2f, 0.4f), colorAt), Is.True, "色が変われば作り直す");
                Assert.That(calls, Is.EqualTo(afterFirst * 2));
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        public void 破棄した後は作り直す()
        {
            var cache = new GradientSlider.Cache(8, "GradientSliderTests");
            try
            {
                var key = new Vector3(0.5f, 0.5f, 0.5f);
                cache.Update(key, t => Color.white);
                cache.Dispose();

                Assert.That(cache.Texture, Is.Null);
                Assert.That(cache.Update(key, t => Color.white), Is.True, "テクスチャが無ければ同じ色でも作る");
                Assert.That(cache.Texture, Is.Not.Null);
            }
            finally
            {
                cache.Dispose();
            }
        }
    }
}
