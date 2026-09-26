using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// カラーホイールの座標 → HSV（ColorWheelGUI.PointToHsv）のテスト。
    /// リングは色相（右が 0、反時計回りに増える）、内側の正方形は 横 = 彩度・縦 = 明るさ（上が 1）
    /// </summary>
    public class ColorWheelGUITests
    {
        private const float Eps = 1e-3f;
        private static readonly Rect Wheel = new Rect(10f, 20f, 200f, 200f);

        // 彩度 0.6・明るさ 0.8 の色（色相 0.1）
        private static readonly Color Current = Color.HSVToRGB(0.1f, 0.6f, 0.8f);

        /// <summary>リングの太さの中央の半径で、角度（ラジアン、画面上で反時計回り）の位置</summary>
        private static Vector2 RingPoint(float angle)
        {
            ColorWheelGUI.GetRingRadii(Wheel, out float outer, out float inner);
            float r = (outer + inner) * 0.5f;
            // GUI 座標は y が下向きなので、画面上の反時計回りは y を引く
            return Wheel.center + new Vector2(Mathf.Cos(angle) * r, -Mathf.Sin(angle) * r);
        }

        [TestCase(0f, 0f)]
        [TestCase(0.5f * Mathf.PI, 0.25f)]
        [TestCase(Mathf.PI, 0.5f)]
        [TestCase(1.5f * Mathf.PI, 0.75f)]
        public void リング上の角度で色相が決まり_彩度と明るさは保つ(float angle, float expectedHue)
        {
            var hsv = ColorWheelGUI.PointToHsv(Wheel, RingPoint(angle), Current);

            // 0 と 1 は同じ色相
            float diff = Mathf.Abs(Mathf.DeltaAngle(hsv.x * 360f, expectedHue * 360f));
            Assert.That(diff, Is.LessThan(0.1f));
            Assert.That(hsv.y, Is.EqualTo(0.6f).Within(Eps));
            Assert.That(hsv.z, Is.EqualTo(0.8f).Within(Eps));
        }

        [Test]
        public void 正方形の中で彩度と明るさが決まり_色相は保つ()
        {
            var square = ColorWheelGUI.GetSquareRect(Wheel);

            var center = ColorWheelGUI.PointToHsv(Wheel, square.center, Current);
            var topLeft = ColorWheelGUI.PointToHsv(Wheel, new Vector2(square.xMin + 0.01f, square.yMin + 0.01f), Current);
            var bottomRight = ColorWheelGUI.PointToHsv(Wheel, new Vector2(square.xMax - 0.01f, square.yMax - 0.01f), Current);

            Assert.That(center.x, Is.EqualTo(0.1f).Within(Eps));
            Assert.That(center.y, Is.EqualTo(0.5f).Within(Eps));
            Assert.That(center.z, Is.EqualTo(0.5f).Within(Eps));
            // 左上 = 彩度 0・明るさ 1、右下 = 彩度 1・明るさ 0
            Assert.That(topLeft.y, Is.EqualTo(0f).Within(Eps));
            Assert.That(topLeft.z, Is.EqualTo(1f).Within(Eps));
            Assert.That(bottomRight.y, Is.EqualTo(1f).Within(Eps));
            Assert.That(bottomRight.z, Is.EqualTo(0f).Within(Eps));
        }

        [Test]
        public void リングと正方形の外では変わらない()
        {
            Color.RGBToHSV(Current, out float h, out float s, out float v);
            var square = ColorWheelGUI.GetSquareRect(Wheel);

            // 矩形の角（リングの外）と、リングの内側で正方形の外（正方形の真上の隙間）
            var corner = ColorWheelGUI.PointToHsv(Wheel, new Vector2(Wheel.xMin + 1f, Wheel.yMin + 1f), Current);
            var gap = ColorWheelGUI.PointToHsv(Wheel, new Vector2(square.center.x, square.yMin - 2f), Current);

            Assert.That(corner, Is.EqualTo(new Vector3(h, s, v)));
            Assert.That(gap, Is.EqualTo(new Vector3(h, s, v)));
        }

        [Test]
        public void 色相を保ったまま色に戻せる()
        {
            // 明るさ 0（黒）でも、直前に選んだ色相を覚えている
            var black = ColorWheelGUI.FromHsv(new Vector3(0.3f, 0.7f, 0f));

            var hsv = ColorWheelGUI.GetHsv(black);

            Assert.That(black.r, Is.EqualTo(0f));
            Assert.That(hsv.x, Is.EqualTo(0.3f).Within(Eps));
            Assert.That(hsv.y, Is.EqualTo(0.7f).Within(Eps));
        }
    }
}
