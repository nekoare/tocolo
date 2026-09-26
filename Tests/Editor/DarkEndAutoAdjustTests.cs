// 流用元: com.nekoare.chimera-hair-master/Tests/Editor/OklabDarkEndAutoAdjustTests.cs
using Nekoare.ClickRecolor.Editor.Colors;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 暗部の明るさ自動調整（DarkEndAutoAdjust.Compute）のテスト。
    /// 暗い側と中間ピーク（0.9）は V 三角波、明るい側の端点だけを
    /// 彩度で 無彩色0.7（白の銀髪化防止）↔ ビビッド0.4 に振り分ける。
    /// 「手動値を上書きしない」はコンポーネント側の振る舞いなのでここでは扱わない
    /// </summary>
    public class DarkEndAutoAdjustTests
    {
        private static float Auto(Color color) => DarkEndAutoAdjust.Compute(color);

        // 退行テスト: 旧式は白で 0.3（暗部が中間グレーまで落ちて銀髪化）だった
        [Test]
        public void White_GivesHighDarkEnd()
        {
            Assert.That(Auto(Color.white), Is.EqualTo(0.7f).Within(0.01f));
        }

        // ビビッド色は深い影のルックをほぼ維持
        [Test]
        public void PureRed_KeepsDeepShading()
        {
            Assert.That(Auto(Color.red), Is.EqualTo(0.4f).Within(0.01f));
        }

        [Test]
        public void PureBlue_KeepsDeepShading()
        {
            Assert.That(Auto(Color.blue), Is.EqualTo(0.4f).Within(0.01f));
        }

        // 中間 target のフラット化ピーク（明度感を揃える）
        [Test]
        public void MidGray_KeepsFlatteningPeak()
        {
            Assert.That(Auto(new Color(128f / 255f, 128f / 255f, 128f / 255f)),
                Is.EqualTo(0.9f).Within(0.01f));
        }

        // 暗い側は 0.3 + 1.2V
        [Test]
        public void DarkGray_KeepsLegacyFormula()
        {
            float v = 30f / 255f;
            float expected = 0.3f + 1.2f * v;
            Assert.That(Auto(new Color(v, v, v)), Is.EqualTo(expected).Within(0.01f));
        }

        // 中間彩度（パステル）は無彩色0.7とビビッド0.4の間に落ちる
        [Test]
        public void PastelPink_BlendsBetweenEndpoints()
        {
            Assert.That(Auto(new Color(1f, 200f / 255f, 220f / 255f)), Is.InRange(0.45f, 0.69f));
        }

        // どの色でもスライダー範囲 [0,1] 内の妥当な帯に収まる
        [Test]
        public void RepresentativeColors_StayInValidRange()
        {
            var colors = new[]
            {
                Color.white, Color.black, Color.red, Color.green, Color.blue,
                Color.yellow, Color.cyan, Color.magenta, Color.gray,
                new Color(1f, 0.78f, 0.86f), // パステルピンク
                new Color(0.59f, 0.39f, 0.31f), // 茶髪
            };
            foreach (var c in colors)
            {
                float ratio = Auto(c);
                Assert.That(ratio, Is.InRange(0.29f, 0.91f), $"color={c}");
            }
        }
    }
}
