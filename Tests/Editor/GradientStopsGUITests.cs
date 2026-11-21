using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// グラデーションの見本の下の▲で、押した位置からどの色を選ぶか（GradientStopsGUI.Pick）のテスト。
    /// 帯は x = 0〜100（左端＝色 1・右端＝色 2）。null は「▲が無い所」（途中の色を足す）
    /// </summary>
    public class GradientStopsGUITests
    {
        private static GradientTarget? Pick(float x, GradientTarget selected, params float[] mids) =>
            GradientStopsGUI.Pick(x, 0f, 100f, mids, selected);

        [Test]
        public void 端の三角を押すと色1か色2を選ぶ()
        {
            Assert.That(Pick(2f, GradientTarget.End), Is.EqualTo(GradientTarget.Start));
            Assert.That(Pick(97f, GradientTarget.Start), Is.EqualTo(GradientTarget.End));
        }

        [Test]
        public void 途中の三角を押すとその途中の色を選ぶ()
        {
            Assert.That(Pick(41f, GradientTarget.Start, 20f / 100f, 40f / 100f), Is.EqualTo(GradientTarget.Mid(1)));
        }

        [Test]
        public void どの三角からも離れた所は何も選ばない()
        {
            Assert.That(Pick(60f, GradientTarget.Start, 0.3f), Is.Null);
        }

        [Test]
        public void 近い三角が並んでいたらいちばん近い方を選ぶ()
        {
            Assert.That(Pick(52f, GradientTarget.Start, 0.5f, 0.56f), Is.EqualTo(GradientTarget.Mid(0)));
            Assert.That(Pick(55f, GradientTarget.Start, 0.5f, 0.56f), Is.EqualTo(GradientTarget.Mid(1)));
        }

        [Test]
        public void 重なった三角は押すたびに選んでいない方を選ぶ()
        {
            var first = Pick(50f, GradientTarget.Start, 0.5f, 0.5f);
            Assert.That(first, Is.EqualTo(GradientTarget.Mid(0)));
            var second = Pick(50f, first.Value, 0.5f, 0.5f);
            Assert.That(second, Is.EqualTo(GradientTarget.Mid(1)));
            Assert.That(Pick(50f, second.Value, 0.5f, 0.5f), Is.EqualTo(GradientTarget.Mid(0)), "一周したら最初に戻る");
        }

        [Test]
        public void 端に重なった途中の色も選べる()
        {
            var first = Pick(0f, GradientTarget.Start, 0f);
            Assert.That(first, Is.EqualTo(GradientTarget.Mid(0)), "色1を選んでいるときは重なった途中の色を先に");
            Assert.That(Pick(0f, first.Value, 0f), Is.EqualTo(GradientTarget.Start));
        }
    }
}
