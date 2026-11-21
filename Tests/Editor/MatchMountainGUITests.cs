using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using Part = Nekoare.ClickRecolor.Editor.SceneTool.MatchMountainGUI.Part;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 明るさの山の下の▲の帯で、押した位置からどの▲をつかむか（MatchMountainGUI.PickMarker）のテスト。
    /// 左＝影（Left）・中＝明るさ（Body）・右＝ハイライト（Right）。座標は帯の中の x（px）
    /// </summary>
    public class MatchMountainGUITests
    {
        [Test]
        public void 離れているときは押した三角をつかむ()
        {
            Assert.That(MatchMountainGUI.PickMarker(52f, 50f, 100f, 150f), Is.EqualTo(Part.Left));
            Assert.That(MatchMountainGUI.PickMarker(105f, 50f, 100f, 150f), Is.EqualTo(Part.Body));
            Assert.That(MatchMountainGUI.PickMarker(146f, 50f, 100f, 150f), Is.EqualTo(Part.Right));
        }

        [Test]
        public void どの三角からも離れた所を押しても何もつかまない()
        {
            Assert.That(MatchMountainGUI.PickMarker(75f, 50f, 100f, 150f), Is.EqualTo(Part.None));
        }

        [Test]
        public void 影の三角が中に重なっても_中の左側を押せば影をつかめる()
        {
            Assert.That(MatchMountainGUI.PickMarker(96f, 100f, 100f, 150f), Is.EqualTo(Part.Left));
        }

        [Test]
        public void ハイライトの三角が中に重なっても_中の右側を押せばハイライトをつかめる()
        {
            Assert.That(MatchMountainGUI.PickMarker(104f, 50f, 100f, 100f), Is.EqualTo(Part.Right));
        }

        [Test]
        public void 三つとも重なったら_左側で影_右側でハイライト_真ん中で明るさ()
        {
            Assert.That(MatchMountainGUI.PickMarker(96f, 100f, 100f, 100f), Is.EqualTo(Part.Left));
            Assert.That(MatchMountainGUI.PickMarker(104f, 100f, 100f, 100f), Is.EqualTo(Part.Right));
            Assert.That(MatchMountainGUI.PickMarker(101f, 100f, 100f, 100f), Is.EqualTo(Part.Body));
        }

        [Test]
        public void 端が中に寄っていても_反対側を押せば明るさをつかめる()
        {
            // 影の▲は中に重なっているが、中の右側（ハイライトの▲は遠い）を押したら明るさ
            Assert.That(MatchMountainGUI.PickMarker(105f, 100f, 100f, 150f), Is.EqualTo(Part.Body));
        }
    }
}
