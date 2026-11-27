using Nekoare.ClickRecolor.Editor.Pipeline;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>毛先からの長さを Scene の髪のクリックで決める（TipMap.TrySampleTriangle・GradientBox.TipPickValue）</summary>
    public class TipPickTests
    {
        [Test]
        public void クリックした三角形の中の位置で頂点の毛先からの距離と房の長さを混ぜる()
        {
            // 縦に並んだ 4 頂点の帯: 下端（y = 0）が毛先
            var positions = new[] { new Vector3(0f, 0f, 0f), new Vector3(0.01f, 0f, 0f), new Vector3(0f, 0.1f, 0f), new Vector3(0.01f, 0.1f, 0f) };
            var triangles = new[] { 0, 2, 1, 1, 2, 3 };
            var result = TipDistance.Compute(positions, triangles, new Vector3(0.005f, 1f, 0f));

            // 三角形 0 の、頂点 0 と頂点 2 の真ん中（高さ 5cm）
            Assert.That(TipMap.TrySampleTriangle(result, triangles, 0, new Vector3(0.5f, 0.5f, 0f), out float distance, out float length), Is.True);
            Assert.That(distance, Is.EqualTo(0.05f).Within(1e-4f));
            Assert.That(length, Is.EqualTo(0.1f).Within(1e-4f));
        }

        [Test]
        public void 範囲外の三角形の番号なら値を返さない()
        {
            var positions = new[] { Vector3.zero, Vector3.right, Vector3.up };
            var triangles = new[] { 0, 1, 2 };
            var result = TipDistance.Compute(positions, triangles, Vector3.up * 2f);

            Assert.That(TipMap.TrySampleTriangle(result, triangles, 1, Vector3.one / 3f, out _, out _), Is.False);
            Assert.That(TipMap.TrySampleTriangle(result, triangles, -1, Vector3.one / 3f, out _, out _), Is.False);
            Assert.That(TipMap.TrySampleTriangle(result, null, 0, Vector3.one / 3f, out _, out _), Is.False);
        }

        [Test]
        public void cm指定ではクリックした場所の毛先からの距離が長さになり_スライダーの範囲に収まる()
        {
            Assert.That(GradientBox.TipPickValue(false, 0.12f, 0.3f), Is.EqualTo(0.12f).Within(1e-6f));
            Assert.That(GradientBox.TipPickValue(false, 0f, 0.3f), Is.EqualTo(GradientBox.TipLengthMin));
            Assert.That(GradientBox.TipPickValue(false, 2f, 3f), Is.EqualTo(GradientBox.TipLengthMax));
        }

        [Test]
        public void 割合指定ではクリックした場所の距離をその房の長さで割った割合になる()
        {
            Assert.That(GradientBox.TipPickValue(true, 0.06f, 0.2f), Is.EqualTo(0.3f).Within(1e-6f));
            Assert.That(GradientBox.TipPickValue(true, 0f, 0.2f), Is.EqualTo(GradientBox.TipRatioMin));
            Assert.That(GradientBox.TipPickValue(true, 0.05f, 0f), Is.EqualTo(GradientBox.TipRatioMax), "房の長さが 0 なら最大");
        }
    }
}
