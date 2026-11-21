using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// グラデーションの途中の色: 位置の順に並べ、隣り合う 2 色のあいだを Oklab でまっすぐ混ぜる（ColorShiftCpu.GradientTargetOklab）
    /// </summary>
    public class GradientMidStopsTests
    {
        private static readonly Vector3 Lab1 = new Vector3(0.6f, 0.2f, 0.1f);
        private static readonly Vector3 Lab2 = new Vector3(0.4f, -0.1f, -0.2f);
        private static readonly Vector3 LabA = new Vector3(0.8f, -0.2f, 0.15f);
        private static readonly Vector3 LabB = new Vector3(0.3f, 0.05f, 0.25f);

        private static ShiftParams Params(params Vector4[] midStops) => new ShiftParams { targetOklab2 = Lab2, midStops = midStops };

        private static Vector4 Stop(Vector3 lab, float position) => new Vector4(lab.x, lab.y, lab.z, position);

        private static void AssertLab(Vector3 actual, Vector3 expected, string message)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(1e-5f), $"{message}: {actual} ≠ {expected}");
        }

        [Test]
        public void 途中の色が無ければ色1から色2へまっすぐ混ぜる()
        {
            AssertLab(ColorShiftCpu.GradientTargetOklab(Lab1, Params(), 0.25f), Vector3.Lerp(Lab1, Lab2, 0.25f), "null");
            AssertLab(ColorShiftCpu.GradientTargetOklab(Lab1, Params(new Vector4[0]), 0.25f), Vector3.Lerp(Lab1, Lab2, 0.25f), "空");
        }

        [Test]
        public void 途中の色の位置ではその色になり_前後は隣の色と混ぜる()
        {
            var p = Params(Stop(LabA, 0.4f));
            AssertLab(ColorShiftCpu.GradientTargetOklab(Lab1, p, 0.4f), LabA, "位置ちょうど");
            AssertLab(ColorShiftCpu.GradientTargetOklab(Lab1, p, 0.2f), Vector3.Lerp(Lab1, LabA, 0.5f), "色1と途中の色の真ん中");
            AssertLab(ColorShiftCpu.GradientTargetOklab(Lab1, p, 0.7f), Vector3.Lerp(LabA, Lab2, 0.5f), "途中の色と色2の真ん中");
            AssertLab(ColorShiftCpu.GradientTargetOklab(Lab1, p, 0f), Lab1, "下端は色1");
            AssertLab(ColorShiftCpu.GradientTargetOklab(Lab1, p, 1f), Lab2, "上端は色2");
        }

        [Test]
        public void 同じ位置に2色あれば_その位置までは先の色_その上は後の色から始まる段差になる()
        {
            var p = Params(Stop(LabA, 0.5f), Stop(LabB, 0.5f));
            AssertLab(ColorShiftCpu.GradientTargetOklab(Lab1, p, 0.5f), LabA, "位置ちょうどは先の色");
            AssertLab(ColorShiftCpu.GradientTargetOklab(Lab1, p, 0.75f), Vector3.Lerp(LabB, Lab2, 0.5f), "上は後の色と色2のあいだ");
        }

        [Test]
        public void 端と同じ位置の途中の色でも割り算で壊れない()
        {
            var p = Params(Stop(LabA, 0f), Stop(LabB, 1f));
            AssertLab(ColorShiftCpu.GradientTargetOklab(Lab1, p, 0f), LabA, "下端に置いた途中の色");
            AssertLab(ColorShiftCpu.GradientTargetOklab(Lab1, p, 0.5f), Vector3.Lerp(LabA, LabB, 0.5f), "途中の色どうしのあいだ");
            AssertLab(ColorShiftCpu.GradientTargetOklab(Lab1, p, 1f), LabB, "上端ちょうどは途中の色");
        }

        [Test]
        public void 途中の色は位置の順に並べ_同じ位置なら足した順を保つ()
        {
            var green = new Color(0.1f, 0.8f, 0.2f);
            var yellow = new Color(0.9f, 0.85f, 0.1f);
            var cyan = new Color(0.1f, 0.85f, 0.9f);
            var sorted = ShiftParams.SortedMidStops(new List<RecolorGradientStop>
            {
                new RecolorGradientStop { position = 0.8f, color = yellow },
                new RecolorGradientStop { position = 0.3f, color = green },
                new RecolorGradientStop { position = 0.8f, color = cyan },
            });

            Assert.That(sorted, Has.Length.EqualTo(3));
            Assert.That(new[] { sorted[0].w, sorted[1].w, sorted[2].w }, Is.EqualTo(new[] { 0.3f, 0.8f, 0.8f }));
            AssertLab(sorted[1], OklabConverter.LinearRGBToOklab(OklabConverter.SRGBToLinear(yellow)), "同じ位置の先は先に足した黄");
            AssertLab(sorted[2], OklabConverter.LinearRGBToOklab(OklabConverter.SRGBToLinear(cyan)), "同じ位置の後は後に足した水色");
        }

        [Test]
        public void 上限を超えた途中の色と範囲外の位置は混ぜ方に持ち込まない()
        {
            var stops = new List<RecolorGradientStop>();
            for (int i = 0; i < RecolorGradientStop.MaxCount + 2; i++)
            {
                stops.Add(new RecolorGradientStop { position = i == 0 ? 1.5f : 0.1f * i, color = Color.gray });
            }
            var sorted = ShiftParams.SortedMidStops(stops);

            Assert.That(sorted, Has.Length.EqualTo(RecolorGradientStop.MaxCount));
            Assert.That(sorted[sorted.Length - 1].w, Is.EqualTo(1f), "範囲外の位置は 0〜1 に切り詰める");
        }

        [Test]
        public void 途中の色が無い一覧は_null_になる()
        {
            Assert.That(ShiftParams.SortedMidStops(null), Is.Null);
            Assert.That(ShiftParams.SortedMidStops(new List<RecolorGradientStop>()), Is.Null);
        }
    }
}
