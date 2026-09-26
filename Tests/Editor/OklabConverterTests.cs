// 流用元: com.nekoare.chimera-hair-master/Tests/Editor/OklabConverterTests.cs
using Nekoare.ClickRecolor.Editor.Colors;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    public class OklabConverterTests
    {
        private const float ColorEps = 1e-3f;    // 色成分の往復許容誤差（~1/1000）
        private const float AngleEps = 1e-3f;    // ラジアン角度の許容誤差

        [Test]
        public void SRGBToLinear_RoundTrip()
        {
            float[] samples = { 0f, 0.04045f, 0.1f, 0.5f, 0.9f, 1f };
            foreach (var v in samples)
            {
                float lin = OklabConverter.SRGBToLinear(v);
                float back = OklabConverter.LinearToSRGB(lin);
                Assert.That(back, Is.EqualTo(v).Within(ColorEps), $"SRGB round trip failed at {v}");
            }
        }

        [TestCase(0.0f, 0.0f, 0.0f)]
        [TestCase(1.0f, 1.0f, 1.0f)]
        [TestCase(0.5f, 0.3f, 0.8f)]
        [TestCase(0.72f, 0.12f, 0.05f)]
        public void SRGB_Oklab_RoundTrip(float r, float g, float b)
        {
            var srgb = new Color(r, g, b);
            var lin = OklabConverter.SRGBToLinear(srgb);
            var lab = OklabConverter.LinearRGBToOklab(lin);
            var linBack = OklabConverter.OklabToLinearRGB(lab);
            var srgbBack = OklabConverter.LinearToSRGB(linBack, srgb.a);

            Assert.That(srgbBack.r, Is.EqualTo(srgb.r).Within(ColorEps));
            Assert.That(srgbBack.g, Is.EqualTo(srgb.g).Within(ColorEps));
            Assert.That(srgbBack.b, Is.EqualTo(srgb.b).Within(ColorEps));
        }

        [Test]
        public void OklabToOklch_PreservesL()
        {
            var lab = new Vector3(0.6f, 0.1f, -0.05f);
            var lch = OklabConverter.OklabToOklch(lab);
            Assert.That(lch.x, Is.EqualTo(lab.x).Within(1e-6f));
            // C = sqrt(a^2 + b^2)
            float expectedC = Mathf.Sqrt(lab.y * lab.y + lab.z * lab.z);
            Assert.That(lch.y, Is.EqualTo(expectedC).Within(1e-6f));
        }

        [Test]
        public void OklabOklchRoundTrip()
        {
            var lab = new Vector3(0.6f, 0.1f, -0.05f);
            var lch = OklabConverter.OklabToOklch(lab);
            var labBack = OklabConverter.OklchToOklab(lch);
            Assert.That(labBack.x, Is.EqualTo(lab.x).Within(1e-6f));
            Assert.That(labBack.y, Is.EqualTo(lab.y).Within(1e-6f));
            Assert.That(labBack.z, Is.EqualTo(lab.z).Within(1e-6f));
        }

        [Test]
        public void SoftClip01_InsideRange_ReturnsInput()
        {
            // 境界から softZone 内側の値はそのまま
            Assert.That(OklabConverter.SoftClip01(0.5f, 0.05f), Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(OklabConverter.SoftClip01(0.1f, 0.05f), Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(OklabConverter.SoftClip01(0.9f, 0.05f), Is.EqualTo(0.9f).Within(1e-6f));
        }

        [Test]
        public void SoftClip01_AbovOne_StaysBelowOne()
        {
            float y = OklabConverter.SoftClip01(1.5f, 0.05f);
            Assert.Less(y, 1f);
            Assert.Greater(y, 0.95f); // 1 - softZone より大きい
        }

        [Test]
        public void SoftClip01_BelowZero_StaysAboveZero()
        {
            float y = OklabConverter.SoftClip01(-0.3f, 0.05f);
            Assert.Greater(y, 0f);
            Assert.Less(y, 0.05f);
        }

        [Test]
        public void WrapHueRadians_WithinRange_Unchanged()
        {
            Assert.That(OklabConverter.WrapHueRadians(0f), Is.EqualTo(0f).Within(AngleEps));
            Assert.That(OklabConverter.WrapHueRadians(1f), Is.EqualTo(1f).Within(AngleEps));
            Assert.That(OklabConverter.WrapHueRadians(-1f), Is.EqualTo(-1f).Within(AngleEps));
        }

        [Test]
        public void WrapHueRadians_Above2Pi_Wraps()
        {
            // 2π 相当の入力は 0 付近に戻る
            float wrapped = OklabConverter.WrapHueRadians(2f * Mathf.PI + 0.1f);
            Assert.That(wrapped, Is.EqualTo(0.1f).Within(AngleEps));
        }

        [Test]
        public void WrapHueRadians_BelowMinusPi_Wraps()
        {
            // -2π は 0 に戻る
            float wrapped = OklabConverter.WrapHueRadians(-2f * Mathf.PI);
            Assert.That(wrapped, Is.EqualTo(0f).Within(AngleEps));
        }

        [Test]
        public void SRGBToOklch_BlackIsZeroL()
        {
            var lch = OklabConverter.SRGBToOklch(Color.black);
            Assert.That(lch.x, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(lch.y, Is.EqualTo(0f).Within(1e-3f));
        }

        [Test]
        public void SRGBToOklch_WhiteIsOneL()
        {
            var lch = OklabConverter.SRGBToOklch(Color.white);
            Assert.That(lch.x, Is.EqualTo(1f).Within(1e-2f));
            Assert.That(lch.y, Is.EqualTo(0f).Within(1e-2f));
        }

        // --- ガマットマッピング（v1.5.10 で二分探索化）---

        [Test]
        public void OklchToSRGBGamutMapped_InGamutColor_RoundTrips()
        {
            // ガマット内の色は縮小されずそのまま返ること
            var srgb = new Color(0.45f, 0.32f, 0.20f);
            var lch = OklabConverter.SRGBToOklch(srgb);
            var result = OklabConverter.OklchToSRGBGamutMapped(lch, 1f);

            Assert.That(result.r, Is.EqualTo(srgb.r).Within(ColorEps), "ガマット内なのに R が動いた");
            Assert.That(result.g, Is.EqualTo(srgb.g).Within(ColorEps), "ガマット内なのに G が動いた");
            Assert.That(result.b, Is.EqualTo(srgb.b).Within(ColorEps), "ガマット内なのに B が動いた");
        }

        [TestCase(0.2462f, -0.063f)]   // ビビッドなピンク相当
        [TestCase(0.1400f, 3.000f)]    // シアン寄り
        [TestCase(0.1900f, 1.900f)]    // 青寄り
        public void OklchToSRGBGamutMapped_LightnessSweep_NoVisibleBanding(float chroma, float hue)
        {
            // 本命の回帰テスト。
            // 彩度を 0.9 倍ずつ最大 8 回縮める旧方式では、反復回数が整数のため
            // 出力彩度が飛び飛びになり、L のグラデーション上に段差が出ていた。
            // 旧方式ならこのテストは 30〜60 階調の差で落ちる。
            const int steps = 8000;
            const float lMin = 0.02f;
            const float lMax = 0.99f;
            const int maxJump = 3;   // 可視閾値（2〜3 階調）

            Color prev = OklabConverter.OklchToSRGBGamutMapped(new Vector3(lMin, chroma, hue), 1f);
            for (int i = 1; i <= steps; i++)
            {
                float l = lMin + (lMax - lMin) * i / steps;
                Color cur = OklabConverter.OklchToSRGBGamutMapped(new Vector3(l, chroma, hue), 1f);

                int jump = MaxChannelDiff8Bit(prev, cur);
                Assert.That(jump, Is.LessThanOrEqualTo(maxJump),
                    $"L={l:F4} で隣接出力が {jump} 階調跳んだ（バンディング）");
                prev = cur;
            }
        }

        [Test]
        public void OklchToSRGBGamutMapped_OutOfGamut_PreservesLightnessAndHue()
        {
            // 彩度だけを縮めるので、L と h は保たれること。
            // 旧方式は 8 回縮めても収まらない色をチャンネルごとに切っていたため
            // 色相までずれていた。
            var lch = new Vector3(0.55f, 0.35f, 1.2f);   // 大きく範囲外
            var result = OklabConverter.OklchToSRGBGamutMapped(lch, 1f);
            var back = OklabConverter.SRGBToOklch(result);

            Assert.That(back.x, Is.EqualTo(lch.x).Within(5e-3f), "L が保たれていない");
            Assert.That(OklabConverter.WrapHueRadians(back.z - lch.z), Is.EqualTo(0f).Within(2e-2f),
                "色相が保たれていない");
        }

        [TestCase(0.55f, 0.35f, 1.2f)]
        [TestCase(0.20f, 0.30f, -2.0f)]
        [TestCase(0.90f, 0.40f, 0.5f)]
        public void OklchToSRGBGamutMapped_OutOfGamut_ChromaNeverIncreases(float l, float c, float h)
        {
            // 二分探索は内側の端を返すので、彩度が上がる方向には振れないこと
            var result = OklabConverter.OklchToSRGBGamutMapped(new Vector3(l, c, h), 1f);
            var back = OklabConverter.SRGBToOklch(result);

            Assert.That(back.y, Is.LessThanOrEqualTo(c + 1e-3f), "彩度が入力より増えた");
        }

        [Test]
        public void SoftClip01_ExtremeInput_StaysWithinUnitRange()
        {
            // ガマットマッピングの二分探索は「C=0 が必ずガマット内」を前提とし、
            // その根拠は L が [0,1] に収まっていること。
            // ここが崩れると探索の下端が破れ、全ピクセルがグレーに潰れる。
            //
            // 0 と 1 ちょうどは許容してよい。C=0 のとき線形 RGB は (L³,L³,L³) なので
            // L=0 なら (0,0,0)、L=1 なら (1,1,1) となり、どちらもガマット内。
            // （float の精度上、極端な入力では端値ちょうどに丸まる）
            const float softZone = 0.05f;
            float[] extremes = { -1e6f, -1f, 0f, 1f, 2f, 1e6f };
            foreach (var x in extremes)
            {
                float clipped = OklabConverter.SoftClip01(x, softZone);
                Assert.That(clipped, Is.GreaterThanOrEqualTo(0f), $"入力 {x} で L が負になった");
                Assert.That(clipped, Is.LessThanOrEqualTo(1f), $"入力 {x} で L が 1 を超えた");
            }
        }

        private static int MaxChannelDiff8Bit(Color a, Color b)
        {
            int dr = Mathf.Abs(Mathf.RoundToInt(a.r * 255f) - Mathf.RoundToInt(b.r * 255f));
            int dg = Mathf.Abs(Mathf.RoundToInt(a.g * 255f) - Mathf.RoundToInt(b.g * 255f));
            int db = Mathf.Abs(Mathf.RoundToInt(a.b * 255f) - Mathf.RoundToInt(b.b * 255f));
            return Mathf.Max(dr, Mathf.Max(dg, db));
        }
    }
}
