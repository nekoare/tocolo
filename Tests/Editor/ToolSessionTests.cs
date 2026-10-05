using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    public class ToolSessionTests
    {
        private GameObject _go;

        [SetUp] public void SetUp() { ToolSession.Clear(); }
        [TearDown] public void TearDown() { ToolSession.Clear(); if (_go != null) Object.DestroyImmediate(_go); }

        [Test]
        public void SetActiveRoot_は_TryGetActiveRoot_で取り出せる()
        {
            _go = new GameObject("avatar");

            ToolSession.SetActiveRoot(_go);

            Assert.That(ToolSession.TryGetActiveRoot(out var found), Is.True);
            Assert.That(found, Is.SameAs(_go));
        }

        [Test]
        public void SetActiveRoot_は_ClickRecolor_を付けない()
        {
            _go = new GameObject("avatar");

            ToolSession.SetActiveRoot(_go);

            Assert.That(_go.GetComponent<ClickRecolor>(), Is.Null);
        }

        [Test]
        public void SetActiveRoot_null_で解除される()
        {
            _go = new GameObject("avatar");
            ToolSession.SetActiveRoot(_go);

            ToolSession.SetActiveRoot(null);

            Assert.That(ToolSession.TryGetActiveRoot(out var found), Is.False);
            Assert.That(found, Is.Null);
        }

        [Test]
        public void 対象が破棄されたら_false()
        {
            _go = new GameObject("avatar");
            ToolSession.SetActiveRoot(_go);

            Object.DestroyImmediate(_go); _go = null;

            Assert.That(ToolSession.TryGetActiveRoot(out _), Is.False);
        }

        [Test]
        public void 既定のプリセットは島で_Mode_も島()
        {
            Assert.That(ToolSession.Preset, Is.EqualTo(RangePreset.Island));
            Assert.That(ToolSession.Mode, Is.EqualTo(SelectionMode.Island));

            ToolSession.Preset = RangePreset.SimilarColor;

            Assert.That(ToolSession.Mode, Is.EqualTo(SelectionMode.Color));
        }

        [Test]
        public void Clear_で箱の選択が消える()
        {
            ToolSession.ChooseBoxes(new RecolorEdit { id = "edit-1", decalEnabled = true }, selection: false, gradient: false, decal: false);

            ToolSession.Clear();

            Assert.That(ToolSession.BoxChoiceEditId, Is.Null);
        }

        [Test]
        public void 箱の既定は片方だけONならその箱_両方ONなら画像の箱だけ()
        {
            var gradientOnly = new RecolorEdit { id = "g", gradientEnabled = true };
            var decalOnly = new RecolorEdit { id = "d", decalEnabled = true };
            var both = new RecolorEdit { id = "b", gradientEnabled = true, decalEnabled = true };
            var none = new RecolorEdit { id = "n" };

            Assert.That(ToolSession.ShowsGradientBox(gradientOnly), Is.True);
            Assert.That(ToolSession.ShowsDecalBox(decalOnly), Is.True);
            Assert.That(ToolSession.ShowsGradientBox(both), Is.False, "ギズモが重ならないよう画像の箱だけ");
            Assert.That(ToolSession.ShowsDecalBox(both), Is.True);
            Assert.That(ToolSession.ShowsGradientBox(none), Is.False);
            Assert.That(ToolSession.ShowsDecalBox(none), Is.False);
            Assert.That(ToolSession.ShowsGradientBox(null), Is.False);
        }

        [Test]
        public void 選んだ箱だけ出す_両方も両方なしも選べる()
        {
            var edit = new RecolorEdit { id = "b", gradientEnabled = true, decalEnabled = true };

            ToolSession.ChooseBoxes(edit, selection: false, gradient: true, decal: true);
            Assert.That(ToolSession.ShowsGradientBox(edit), Is.True);
            Assert.That(ToolSession.ShowsDecalBox(edit), Is.True);

            ToolSession.ChooseBoxes(edit, selection: false, gradient: false, decal: false);
            Assert.That(ToolSession.ShowsGradientBox(edit), Is.False);
            Assert.That(ToolSession.ShowsDecalBox(edit), Is.False);
        }

        [Test]
        public void 箱の選択は選んだ編集だけに効く()
        {
            var chosen = new RecolorEdit { id = "a", gradientEnabled = true, decalEnabled = true };
            var other = new RecolorEdit { id = "b", gradientEnabled = true, decalEnabled = true };

            ToolSession.ChooseBoxes(chosen, selection: false, gradient: true, decal: false);

            Assert.That(ToolSession.ShowsDecalBox(chosen), Is.False);
            Assert.That(ToolSession.ShowsGradientBox(other), Is.False, "別の編集は既定");
            Assert.That(ToolSession.ShowsDecalBox(other), Is.True);
        }

        [Test]
        public void 箱の中の箱は既定でグラデーションも画像もOFFのときだけ出す()
        {
            var boxOnly = new RecolorEdit { id = "s", mode = SelectionMode.Box };
            var boxAndGradient = new RecolorEdit { id = "sg", mode = SelectionMode.Box, gradientEnabled = true };
            var wholeTexture = new RecolorEdit { id = "w", mode = SelectionMode.Box, wholeTexture = true };
            var island = new RecolorEdit { id = "i", mode = SelectionMode.Island };

            Assert.That(ToolSession.ShowsSelectionBox(boxOnly), Is.True);
            Assert.That(ToolSession.ShowsSelectionBox(boxAndGradient), Is.False, "ギズモが重ならないようグラデーションの箱だけ");
            Assert.That(ToolSession.ShowsGradientBox(boxAndGradient), Is.True);
            Assert.That(ToolSession.HasSelectionBox(wholeTexture), Is.False, "テクスチャ全体に広げている間は箱を使わない");
            Assert.That(ToolSession.ShowsSelectionBox(wholeTexture), Is.False);
            Assert.That(ToolSession.HasSelectionBox(island), Is.False);

            ToolSession.ChooseBoxes(boxAndGradient, selection: true, gradient: true, decal: false);
            Assert.That(ToolSession.ShowsSelectionBox(boxAndGradient), Is.True, "小窓で選べば一緒に出せる");
            Assert.That(ToolSession.ShowsGradientBox(boxAndGradient), Is.True);
        }

        [Test]
        public void 箱の中から別のモードに変えたら箱の選択は既定に戻る()
        {
            var edit = new RecolorEdit { id = "a", mode = SelectionMode.Box, gradientEnabled = true };
            ToolSession.ChooseBoxes(edit, selection: true, gradient: false, decal: false);

            edit.mode = SelectionMode.Island;

            Assert.That(ToolSession.ShowsGradientBox(edit), Is.True, "残ったグラデーションの箱は隠したままにしない");
        }

        [Test]
        public void ONOFFが選んだときと変わったら箱の選択は既定に戻る()
        {
            // グラデーションだけ ON → 画像も ON にした（画像の箱だけ）→ Undo で画像が OFF に戻った
            var edit = new RecolorEdit { id = "a", gradientEnabled = true, decalEnabled = true };
            ToolSession.ChooseBoxes(edit, selection: false, gradient: false, decal: true);

            edit.decalEnabled = false;

            Assert.That(ToolSession.ShowsGradientBox(edit), Is.True, "残ったグラデーションの箱は隠したままにしない");

            // Redo で戻れば選択も戻る
            edit.decalEnabled = true;
            Assert.That(ToolSession.ShowsGradientBox(edit), Is.False);
            Assert.That(ToolSession.ShowsDecalBox(edit), Is.True);
        }

        [Test]
        public void Clear_で_DecalBoxDragging_が_false_になる()
        {
            ToolSession.BeginDecalBoxDrag(new RecolorEdit());

            ToolSession.Clear();

            Assert.That(ToolSession.DecalBoxDragging, Is.False);
        }
    }
}
