using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    public class RecolorEditTests
    {
        private GameObject _go;
        private GameObject _other;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_other != null) Object.DestroyImmediate(_other);
        }

        [Test]
        public void CreateNew_はユニークな_id_と既定値を持つ()
        {
            var a = RecolorEdit.CreateNew();
            var b = RecolorEdit.CreateNew();

            Assert.That(a.id, Is.Not.Null.And.Not.Empty);
            Assert.That(a.id, Is.Not.EqualTo(b.id));
            Assert.That(a.enabled, Is.True);
            Assert.That(a.hasTarget, Is.False);
            Assert.That(a.mode, Is.EqualTo(SelectionMode.Island));
            Assert.That(a.scope, Is.EqualTo(ColorScope.Contiguous));
            Assert.That(a.threshold, Is.EqualTo(0.15f));
            Assert.That(a.feather, Is.EqualTo(0.10f));
            Assert.That(a.padding, Is.EqualTo(8));
            Assert.That(a.darkEndRatio, Is.EqualTo(0.7f));
            Assert.That(a.strength, Is.EqualTo(1f));
        }

        [Test]
        public void AddEdit_は末尾に追加し_FindEdit_で引ける()
        {
            _go = new GameObject("avatar");
            var component = _go.AddComponent<ClickRecolor>();

            var first = component.AddEdit(RecolorEdit.CreateNew());
            var second = component.AddEdit(RecolorEdit.CreateNew());

            Assert.That(component.edits.Count, Is.EqualTo(2));
            Assert.That(component.edits[1], Is.SameAs(second));
            Assert.That(component.FindEdit(first.id), Is.SameAs(first));
            Assert.That(component.FindEdit("missing"), Is.Null);
            Assert.That(first.name, Is.EqualTo("編集 1"));
            Assert.That(second.name, Is.EqualTo("編集 2"));
        }

        [Test]
        public void RemoveEdit_は_id_で削除し_無ければ_false()
        {
            _go = new GameObject("avatar");
            var component = _go.AddComponent<ClickRecolor>();
            var edit = component.AddEdit(RecolorEdit.CreateNew());

            Assert.That(component.RemoveEdit(edit.id), Is.True);
            Assert.That(component.edits, Is.Empty);
            Assert.That(component.RemoveEdit(edit.id), Is.False);
        }

        [Test]
        public void 既定値のコンポーネントはプレビュー有効_ビルド適用_2048()
        {
            _go = new GameObject("avatar");
            var component = _go.AddComponent<ClickRecolor>();

            Assert.That(component.previewEnabled, Is.True);
            Assert.That(component.applyOnBuild, Is.True);
            Assert.That(component.previewResolution, Is.EqualTo(WorkingResolution.R2048));
            Assert.That(component.dataVersion, Is.EqualTo(1));
        }

        [Test]
        public void AddEdit_に_null_を渡すと_null_を返し追加しない()
        {
            _go = new GameObject("avatar");
            var component = _go.AddComponent<ClickRecolor>();

            Assert.That(component.AddEdit(null), Is.Null);
            Assert.That(component.edits, Is.Empty);
        }

        [Test]
        public void OnValidate_は重複した_id_を後勝ちで再採番する()
        {
            _go = new GameObject("avatar");
            var component = _go.AddComponent<ClickRecolor>();
            var first = component.AddEdit(RecolorEdit.CreateNew());
            var second = component.AddEdit(RecolorEdit.CreateNew());
            var firstId = first.id;
            second.id = firstId;

            component.OnValidate();

            Assert.That(first.id, Is.EqualTo(firstId));
            Assert.That(second.id, Is.Not.Null.And.Not.Empty);
            Assert.That(second.id, Is.Not.EqualTo(firstId));
        }

        [Test]
        public void シリアライズ往復で編集のフィールドが残る()
        {
            _go = new GameObject("avatar");
            var source = _go.AddComponent<ClickRecolor>();
            var edit = RecolorEdit.CreateNew();
            edit.name = "x";
            edit.mode = SelectionMode.Color;
            edit.scope = ColorScope.WholeTexture;
            edit.threshold = 0.3f;
            edit.hasTarget = true;
            edit.targetColor = Color.red;
            edit.padding = 3;
            source.AddEdit(edit);

            var json = EditorJsonUtility.ToJson(source);
            _other = new GameObject("other");
            var restored = _other.AddComponent<ClickRecolor>();
            EditorJsonUtility.FromJsonOverwrite(json, restored);

            Assert.That(restored.edits.Count, Is.EqualTo(1));
            var r = restored.edits[0];
            Assert.That(r.id, Is.EqualTo(edit.id));
            Assert.That(r.name, Is.EqualTo("x"));
            Assert.That(r.mode, Is.EqualTo(SelectionMode.Color));
            Assert.That(r.scope, Is.EqualTo(ColorScope.WholeTexture));
            Assert.That(r.threshold, Is.EqualTo(0.3f));
            Assert.That(r.hasTarget, Is.True);
            Assert.That(r.targetColor, Is.EqualTo(Color.red));
            Assert.That(r.padding, Is.EqualTo(3));
        }
    }
}
