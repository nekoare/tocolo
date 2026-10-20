using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 影響範囲のボタン（RangePreset）と編集のモードの対応（RecolorSceneTool.ApplyPreset / PresetOf）:
    /// 選び直した編集に合わせてボタンを光らせるので、ボタンで決めた編集からは同じボタンに戻ること
    /// </summary>
    public class RangePresetTests
    {
        // RangePreset は internal なので、テストケースは値で渡す（Island・SameColor・SimilarColor・Box）
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void ボタンで決めた編集からは_ほかのボタンを選んでいても同じボタンに戻る(int value)
        {
            var preset = (RangePreset)value;
            var edit = RecolorEdit.CreateNew();
            RecolorSceneTool.ApplyPreset(edit, preset);

            // 今光っているのがパーツ（色のプリセットでない）なら、色モードはしきい値で見分ける
            Assert.That(RecolorSceneTool.PresetOf(edit, RangePreset.Island), Is.EqualTo(preset));
        }

        [Test]
        public void 色モードの編集は_今光っている色のボタンをそのまま使う()
        {
            // しきい値はスライダーで変えられるので、同じ色・似た色のどちらを選んでいたかは今のボタンを優先する
            var edit = RecolorEdit.CreateNew();
            RecolorSceneTool.ApplyPreset(edit, RangePreset.SimilarColor);
            edit.threshold = 0.05f;

            Assert.That(RecolorSceneTool.PresetOf(edit, RangePreset.SimilarColor), Is.EqualTo(RangePreset.SimilarColor));
            Assert.That(RecolorSceneTool.PresetOf(edit, RangePreset.Box), Is.EqualTo(RangePreset.SameColor), "色のボタンでなければしきい値の近い方");
        }
    }
}
