using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// ［入れ替え］: 色 1 と色 2 を入れ替えると、グラデーション全体が上下反転した見た目になる（GradientBox.SwapEnds）
    /// </summary>
    public class GradientSwapEndsTests
    {
        private static RecolorEdit MakeEdit()
        {
            return new RecolorEdit
            {
                gradientEnabled = true,
                targetColor = new Color(0.9f, 0.1f, 0.1f),
                gradientColor = new Color(0.1f, 0.1f, 0.9f),
                darkEndRatio = 0.3f,
                gradientDarkEndRatio = 0.8f,
                gamma = 0.5f,
                gradientGamma = 1.5f,
                strength = 0.4f,
                gradientStrength = 0.9f,
                gradientMidStops = new List<RecolorGradientStop>
                {
                    new RecolorGradientStop { position = 0.2f, color = Color.green },
                    new RecolorGradientStop { position = 0.5f, color = Color.yellow },
                    new RecolorGradientStop { position = 0.5f, color = Color.cyan },
                },
            };
        }

        [Test]
        public void 入れ替えると色と暗部とガンマと強さが色1と色2で入れ替わる()
        {
            var e = MakeEdit();
            GradientBox.SwapEnds(e);

            Assert.That(e.targetColor, Is.EqualTo(new Color(0.1f, 0.1f, 0.9f)));
            Assert.That(e.gradientColor, Is.EqualTo(new Color(0.9f, 0.1f, 0.1f)));
            Assert.That(e.darkEndRatio, Is.EqualTo(0.8f));
            Assert.That(e.gradientDarkEndRatio, Is.EqualTo(0.3f));
            Assert.That(e.gamma, Is.EqualTo(1.5f));
            Assert.That(e.gradientGamma, Is.EqualTo(0.5f));
            Assert.That(e.strength, Is.EqualTo(0.9f));
            Assert.That(e.gradientStrength, Is.EqualTo(0.4f));
        }

        [Test]
        public void 入れ替えると途中の色は上下反転した位置に来て_同じ位置の段差も上下が入れ替わる()
        {
            var before = ShiftParams.SortedMidStops(MakeEdit().gradientMidStops);
            var e = MakeEdit();
            GradientBox.SwapEnds(e);
            var after = ShiftParams.SortedMidStops(e.gradientMidStops);

            Assert.That(after.Length, Is.EqualTo(before.Length));
            for (int i = 0; i < before.Length; i++)
            {
                var mirrored = before[before.Length - 1 - i];
                Assert.That(after[i].w, Is.EqualTo(1f - mirrored.w).Within(1e-6f), $"{i}番目の位置");
                Assert.That((Vector3)after[i], Is.EqualTo((Vector3)mirrored), $"{i}番目の色");
            }
        }

        [Test]
        public void 二回入れ替えると元に戻る()
        {
            var e = MakeEdit();
            GradientBox.SwapEnds(e);
            GradientBox.SwapEnds(e);
            var original = MakeEdit();

            Assert.That(e.targetColor, Is.EqualTo(original.targetColor));
            Assert.That(e.gradientGamma, Is.EqualTo(original.gradientGamma));
            for (int i = 0; i < original.gradientMidStops.Count; i++)
            {
                Assert.That(e.gradientMidStops[i].position, Is.EqualTo(original.gradientMidStops[i].position).Within(1e-6f));
                Assert.That(e.gradientMidStops[i].color, Is.EqualTo(original.gradientMidStops[i].color));
            }
        }
    }
}
