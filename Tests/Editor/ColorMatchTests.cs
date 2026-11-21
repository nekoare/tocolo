using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>「お手本に合わせる」の統計（MatchProfile）と写し方（MatchParams・ColorMatchCpu）</summary>
    public class ColorMatchTests
    {
        /// <summary>髪らしい分布: L は lo〜hi に一様、色は明るさに沿って (a0 + aSlope·L, b0 + bSlope·L) ＋ ばらつき noise</summary>
        private static List<Vector3> HairLike(int seed, float lo, float hi, float a0, float aSlope, float b0, float bSlope, float noise, int count = 4000)
        {
            var random = new System.Random(seed);
            var result = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
            {
                float l = lo + (hi - lo) * (float)random.NextDouble();
                float na = ((float)random.NextDouble() - 0.5f) * 2f * noise;
                float nb = ((float)random.NextDouble() - 0.5f) * 2f * noise;
                result.Add(new Vector3(l, a0 + aSlope * l + na, b0 + bSlope * l + nb));
            }
            return result;
        }

        private static MatchParams Build(MatchProfile target, MatchProfile reference, RecolorEdit edit = null)
        {
            Assert.That(MatchParams.TryBuild(target, reference, edit ?? RecolorEdit.CreateNew(), out var p), Is.True);
            return p;
        }

        private static Vector3 MatchLab(Vector3 lab, in MatchParams p)
        {
            Vector3 lin = OklabConverter.OklabToLinearRGB(lab);
            Color result = ColorMatchCpu.MatchLinear(new Color(lin.x, lin.y, lin.z, 1f), 1f, p);
            return OklabConverter.LinearRGBToOklab(new Vector3(result.r, result.g, result.b));
        }

        [Test]
        public void 同じ分布どうしなら色はほぼ変わらない()
        {
            var labs = HairLike(1, 0.3f, 0.85f, 0.01f, 0.02f, 0.04f, -0.03f, 0.005f);
            var profile = MatchProfile.FromOklab(labs);
            var p = Build(profile, profile);
            for (int i = 0; i < 200; i += 7)
            {
                var result = MatchLab(labs[i], p);
                Assert.That((result - labs[i]).magnitude, Is.LessThan(0.01f), $"画素{i}");
            }
        }

        [Test]
        public void お手本の明るさの分位点へ写る()
        {
            var target = MatchProfile.FromOklab(HairLike(2, 0.2f, 0.5f, 0f, 0f, 0f, 0f, 0.002f));
            var reference = MatchProfile.FromOklab(HairLike(3, 0.55f, 0.9f, 0f, 0f, 0f, 0f, 0.002f));
            var p = Build(target, reference);
            for (int q = 0; q < MatchProfile.QuantileCount; q++)
            {
                Assert.That(ColorMatchCpu.EvalCurve(p.lCurve, target.quantiles[q]), Is.EqualTo(reference.quantiles[q]).Within(1e-4f), $"分位点{q}");
            }
        }

        [Test]
        public void 傾きは4で頭打ちになる()
        {
            var target = MatchProfile.FromOklab(HairLike(4, 0.50f, 0.52f, 0f, 0f, 0f, 0f, 0.001f));
            var reference = MatchProfile.FromOklab(HairLike(5, 0.1f, 0.95f, 0f, 0f, 0f, 0f, 0.001f));
            var p = Build(target, reference);
            for (int i = 0; i + 1 < p.lCurve.Length; i++)
            {
                float dx = p.lCurve[i + 1].x - p.lCurve[i].x;
                float dy = p.lCurve[i + 1].y - p.lCurve[i].y;
                Assert.That(dy, Is.GreaterThanOrEqualTo(0f), $"区間{i}は減らない");
                Assert.That(dy, Is.LessThanOrEqualTo(dx * MatchParams.MaxSlope + 1e-6f), $"区間{i}");
            }
            // 中央はお手本の中央にそろう
            Assert.That(p.lCurve[MatchProfile.MedianIndex].y, Is.EqualTo(reference.quantiles[MatchProfile.MedianIndex]).Within(1e-6f));
        }

        [Test]
        public void 対象が1色でもお手本の中央の明るさになりNaNを出さない()
        {
            var flat = new List<Vector3>();
            for (int i = 0; i < 500; i++) flat.Add(new Vector3(0.5f, 0.02f, 0.03f));
            var target = MatchProfile.FromOklab(flat);
            var reference = MatchProfile.FromOklab(HairLike(6, 0.3f, 0.9f, 0.05f, 0f, -0.02f, 0f, 0.01f));
            var p = Build(target, reference);

            var result = MatchLab(new Vector3(0.5f, 0.02f, 0.03f), p);
            Assert.That(float.IsNaN(result.x) || float.IsNaN(result.y) || float.IsNaN(result.z), Is.False);
            Assert.That(result.x, Is.EqualTo(reference.quantiles[MatchProfile.MedianIndex]).Within(0.01f));
            // 色はお手本の色の向きへ寄る
            Assert.That(result.y, Is.GreaterThan(0.03f));
            Assert.That(result.z, Is.LessThan(0f));
        }

        [Test]
        public void 影の色はお手本の影の色になる()
        {
            // お手本は暗いほど赤い（a が大きい）、明るいほど黄色い（b が大きい）髪
            var target = MatchProfile.FromOklab(HairLike(7, 0.3f, 0.9f, 0f, 0f, 0f, 0f, 0.003f));
            var reference = MatchProfile.FromOklab(HairLike(8, 0.3f, 0.9f, 0.12f, -0.12f, -0.03f, 0.08f, 0.003f));
            var p = Build(target, reference);

            var dark = MatchLab(new Vector3(0.35f, 0f, 0f), p);
            var light = MatchLab(new Vector3(0.85f, 0f, 0f), p);
            Assert.That(dark.y, Is.GreaterThan(light.y), "影ほど赤い");
            Assert.That(light.z, Is.GreaterThan(dark.z), "明るいほど黄色い");
        }

        [Test]
        public void 色ムラの倍率は025から2に収まる()
        {
            var noisy = MatchProfile.FromOklab(HairLike(9, 0.3f, 0.9f, 0f, 0f, 0f, 0f, 0.05f));
            var clean = MatchProfile.FromOklab(HairLike(10, 0.3f, 0.9f, 0f, 0f, 0f, 0f, 0.0005f));
            Assert.That(Build(noisy, clean).k, Is.EqualTo(MatchParams.MinResidualScale));
            Assert.That(Build(clean, noisy).k, Is.EqualTo(MatchParams.MaxResidualScale));
        }

        [Test]
        public void 色相をずらすとお手本から回る()
        {
            var target = MatchProfile.FromOklab(HairLike(11, 0.4f, 0.8f, 0f, 0f, 0f, 0f, 0.002f));
            var reference = MatchProfile.FromOklab(HairLike(12, 0.4f, 0.8f, 0.06f, 0f, 0f, 0f, 0.002f));
            var edit = RecolorEdit.CreateNew();
            edit.matchHue = 90f;
            var result = MatchLab(new Vector3(0.6f, 0f, 0f), Build(target, reference, edit));
            // a 方向（赤）の色が 90° 回って b 方向（黄）になる
            Assert.That(result.z, Is.GreaterThan(0.04f));
            Assert.That(Mathf.Abs(result.y), Is.LessThan(0.02f));
        }

        [Test]
        public void 灰色のお手本でも彩度を足せば色が付く()
        {
            var target = MatchProfile.FromOklab(HairLike(18, 0.4f, 0.8f, 0f, 0f, 0f, 0f, 0.002f));
            var gray = MatchProfile.FromOklab(HairLike(19, 0.4f, 0.8f, 0f, 0f, 0f, 0f, 0.002f));
            var edit = RecolorEdit.CreateNew();
            edit.matchChromaShift = 0.08f;
            edit.matchHue = 90f;
            var result = MatchLab(new Vector3(0.6f, 0f, 0f), Build(target, gray, edit));
            Assert.That(Mathf.Sqrt(result.y * result.y + result.z * result.z), Is.GreaterThan(0.06f), "灰色に色が付く");
        }

        [Test]
        public void ハイライトの伸びは中央より明るい側だけを広げる()
        {
            var profile = MatchProfile.FromOklab(HairLike(22, 0.3f, 0.8f, 0f, 0f, 0f, 0f, 0.002f));
            var edit = RecolorEdit.CreateNew();
            edit.matchHighlight = 0.5f;
            var p = Build(profile, profile, edit);
            float pivot = profile.quantiles[MatchProfile.MedianIndex];
            Assert.That(MatchLab(new Vector3(pivot + 0.15f, 0f, 0f), p).x, Is.GreaterThan(pivot + 0.15f + 0.05f), "明るい側は明るくなる");
            Assert.That(MatchLab(new Vector3(pivot - 0.15f, 0f, 0f), p).x, Is.EqualTo(pivot - 0.15f).Within(0.01f), "暗い側はそのまま");
        }

        [Test]
        public void 山の本体を動かすと両裾が同じだけ動く()
        {
            var profile = MatchProfile.FromOklab(HairLike(23, 0.3f, 0.8f, 0f, 0f, 0f, 0f, 0.002f));
            var edit = RecolorEdit.CreateNew();
            float left = MatchMountain.Transform(profile, edit, profile.quantiles[MatchMountain.LeftIndex]);
            float right = MatchMountain.Transform(profile, edit, profile.quantiles[MatchMountain.RightIndex]);
            edit.matchLightness = 0.1f;
            Assert.That(MatchMountain.Transform(profile, edit, profile.quantiles[MatchMountain.LeftIndex]), Is.EqualTo(left + 0.1f).Within(1e-4f));
            Assert.That(MatchMountain.Transform(profile, edit, profile.quantiles[MatchMountain.RightIndex]), Is.EqualTo(right + 0.1f).Within(1e-4f));
        }

        [Test]
        public void 裾をつかんで置いた所に裾が来る()
        {
            var profile = MatchProfile.FromOklab(HairLike(24, 0.3f, 0.8f, 0f, 0f, 0f, 0f, 0.002f));
            var edit = RecolorEdit.CreateNew();
            edit.matchLightness = 0.05f;
            edit.matchShadow = MatchMountain.ShadowFor(profile, edit.matchLightness, 0.25f, 0f);
            edit.matchHighlight = MatchMountain.HighlightFor(profile, edit.matchLightness, 0.92f, 0f);
            Assert.That(MatchMountain.Transform(profile, edit, profile.quantiles[MatchMountain.LeftIndex]), Is.EqualTo(0.25f).Within(1e-3f));
            Assert.That(MatchMountain.Transform(profile, edit, profile.quantiles[MatchMountain.RightIndex]), Is.EqualTo(0.92f).Within(1e-3f));
        }

        [Test]
        public void 裾は中央を越えて反対側へ行かない()
        {
            var profile = MatchProfile.FromOklab(HairLike(25, 0.3f, 0.8f, 0f, 0f, 0f, 0f, 0.002f));
            Assert.That(MatchMountain.ShadowFor(profile, 0f, 0.99f, 0f), Is.EqualTo(-1f), "左の裾を右端へ引いても中央で止まる");
            Assert.That(MatchMountain.HighlightFor(profile, 0f, 0.01f, 0f), Is.EqualTo(-1f), "右の裾を左端へ引いても中央で止まる");
        }

        [Test]
        public void 山の高さは分位点の境目と裾で段差にならない()
        {
            var profile = MatchProfile.FromOklab(HairLike(27, 0.25f, 0.85f, 0f, 0f, 0f, 0f, 0.004f));
            var edit = RecolorEdit.CreateNew();
            edit.matchShadow = 0.4f;
            var r = MatchMountain.ResultQuantiles(profile, edit);
            var m = MatchMountain.Tangents(r);
            float peak = 0f;
            for (int i = 0; i <= 200; i++) peak = Mathf.Max(peak, MatchMountain.HeightAt(r, m, i / 200f));
            const float eps = 1e-4f;
            for (int j = 0; j < r.Length; j++)
            {
                float below = MatchMountain.HeightAt(r, m, r[j] - eps);
                float above = MatchMountain.HeightAt(r, m, r[j] + eps);
                Assert.That(Mathf.Abs(below - above), Is.LessThan(peak * 0.02f), $"分位点 {j} の境目");
            }
        }

        [Test]
        public void 山の高さは負にならず裾の外では0になる()
        {
            var profile = MatchProfile.FromOklab(HairLike(28, 0.25f, 0.85f, 0f, 0f, 0f, 0f, 0.004f));
            var r = MatchMountain.ResultQuantiles(profile, RecolorEdit.CreateNew());
            var m = MatchMountain.Tangents(r);
            for (int i = 0; i <= 400; i++) Assert.That(MatchMountain.HeightAt(r, m, i / 400f), Is.GreaterThanOrEqualTo(0f));
            Assert.That(MatchMountain.HeightAt(r, m, r[0] - MatchMountain.TailWidth - 0.01f), Is.EqualTo(0f));
            Assert.That(MatchMountain.HeightAt(r, m, r[r.Length - 1] + MatchMountain.TailWidth + 0.01f), Is.EqualTo(0f));
        }

        [Test]
        public void 山の柱は高さ0から1で色にNaNを出さない()
        {
            var flat = new List<Vector3>();
            for (int i = 0; i < 300; i++) flat.Add(new Vector3(0.6f, 0f, 0f));
            foreach (var profile in new[] { MatchProfile.FromOklab(flat), MatchProfile.FromOklab(HairLike(26, 0.2f, 0.9f, 0.05f, 0f, 0f, 0f, 0.01f)) })
            {
                var edit = RecolorEdit.CreateNew();
                edit.matchShadow = -1f;
                var heights = new float[64];
                var colors = new Color[64];
                MatchMountain.Columns(profile, edit, heights, colors);
                Assert.That(heights, Has.All.InRange(0f, 1f));
                Assert.That(heights, Has.Some.EqualTo(1f));
                foreach (var c in colors) Assert.That(float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b), Is.False);
            }
        }

        [Test]
        public void 控えの文字列から同じ統計に戻る()
        {
            var profile = MatchProfile.FromOklab(HairLike(13, 0.2f, 0.9f, 0.01f, 0.03f, 0.02f, -0.01f, 0.01f));
            Assert.That(MatchProfile.TryParse(profile.Serialize(), out var restored), Is.True);
            Assert.That(restored.count, Is.EqualTo(profile.count));
            Assert.That(restored.residualRms, Is.EqualTo(profile.residualRms));
            Assert.That(restored.quantiles, Is.EqualTo(profile.quantiles));
            Assert.That(restored.bands, Is.EqualTo(profile.bands));
        }

        [Test]
        public void 壊れた控えは読めない()
        {
            Assert.That(MatchProfile.TryParse("", out _), Is.False);
            Assert.That(MatchProfile.TryParse("1;3;0.1", out _), Is.False);
            Assert.That(MatchProfile.TryParse("2" + MatchProfile.FromOklab(HairLike(14, 0.2f, 0.9f, 0f, 0f, 0f, 0f, 0f)).Serialize().Substring(1), out _), Is.False);
        }

        [Test]
        public void 画素が少なければ無効な統計になり写し方も作らない()
        {
            var empty = MatchProfile.FromOklab(new List<Vector3>());
            Assert.That(empty.IsValid, Is.False);
            var valid = MatchProfile.FromOklab(HairLike(15, 0.2f, 0.9f, 0f, 0f, 0f, 0f, 0f));
            Assert.That(MatchParams.TryBuild(empty, valid, RecolorEdit.CreateNew(), out _), Is.False);
            Assert.That(MatchParams.TryBuild(valid, empty, RecolorEdit.CreateNew(), out _), Is.False);
        }

        [Test]
        public void マスクが0の画素は元のまま()
        {
            var profile = MatchProfile.FromOklab(HairLike(16, 0.2f, 0.9f, 0f, 0f, 0f, 0f, 0f));
            var other = MatchProfile.FromOklab(HairLike(17, 0.2f, 0.5f, 0.1f, 0f, 0f, 0f, 0f));
            var p = Build(profile, other);
            var src = new Color(0.3f, 0.2f, 0.1f, 0.4f);
            Assert.That(ColorMatchCpu.MatchLinear(src, 0f, p), Is.EqualTo(src));
        }
    }
}
