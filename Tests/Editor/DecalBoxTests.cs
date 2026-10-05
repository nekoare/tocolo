using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>「画像を入れる」の ON/OFF・画像の割り当て（DecalBox）のテスト。箱の既定は GradientBox.DefaultFor の bounds 経路で確かめる</summary>
    public class DecalBoxTests
    {
        private readonly List<Object> _cleanup = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            ToolSession.Clear();
            // 前のテストや利用者の操作と Undo グループを分ける
            Undo.IncrementCurrentGroup();
        }

        [TearDown]
        public void TearDown()
        {
            ToolSession.Clear();
            // Undo.RecordObject の記録を消してから破棄する（残すと Ctrl+Z で空のオブジェクトが復活する）
            foreach (var o in _cleanup)
            {
                if (o is GameObject go)
                {
                    foreach (var c in go.GetComponentsInChildren<Component>(true)) if (c != null) Undo.ClearUndo(c);
                    foreach (var t in go.GetComponentsInChildren<Transform>(true)) Undo.ClearUndo(t.gameObject);
                }
                else if (o != null) Undo.ClearUndo(o);
            }
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private T Track<T>(T obj) where T : Object
        {
            _cleanup.Add(obj);
            return obj;
        }

        /// <summary>対象ルートと、その子の立方体の MeshRenderer（bounds を決めるため。マテリアルなし＝選択範囲は取れず bounds の箱になる）を主種に持つ編集</summary>
        private (ClickRecolor component, RecolorEdit edit) MakeEdit()
        {
            var root = Track(new GameObject("Avatar"));
            var component = root.AddComponent<ClickRecolor>();
            var mesh = Track(new Mesh());
            var vertices = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                vertices[i] = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
            }
            mesh.vertices = vertices;
            mesh.triangles = new[] { 0, 1, 2, 1, 3, 2, 4, 6, 5, 5, 6, 7 };
            mesh.RecalculateBounds();
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 1f, 0f);
            body.transform.localScale = new Vector3(0.5f, 2f, 0.3f);
            body.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = body.AddComponent<MeshRenderer>();

            var edit = RecolorEdit.CreateNew();
            edit.seedRenderer = renderer;
            component.AddEdit(edit);
            return (component, edit);
        }

        [Test]
        public void SetEnabled_true_で_ON_かつ_confirmed_になり箱が既定に置かれる()
        {
            var (component, edit) = MakeEdit();
            var box = GradientBox.DefaultFor(component, edit);
            edit.decalBoxPosition = new Vector3(5f, 5f, 5f);

            DecalBox.SetEnabled(component, edit, true);

            Assert.That(edit.decalEnabled, Is.True);
            Assert.That(edit.confirmed, Is.True, "ON にしただけで別の場所のクリックで捨てられないように");
            Assert.That(Vector3.Distance(edit.decalBoxPosition, box.position), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(edit.decalBoxSize, box.size), Is.LessThan(1e-4f));
            Assert.That(edit.decalBoxRotation, Is.EqualTo(box.rotation));
        }

        [Test]
        public void SetTexture_で_confirmed_と_HasDecal_になり_null_に戻しても_confirmed_は残る()
        {
            var (component, edit) = MakeEdit();
            edit.decalEnabled = true;
            var image = Track(new Texture2D(4, 4));

            DecalBox.SetTexture(component, edit, image);

            Assert.That(edit.confirmed, Is.True);
            Assert.That(edit.HasDecal, Is.True);

            DecalBox.SetTexture(component, edit, null);

            Assert.That(edit.HasDecal, Is.False);
            Assert.That(edit.confirmed, Is.True, "一度画像を入れた編集は自動で捨てない");
        }

        [Test]
        public void SetEnabled_false_でも画像は残る()
        {
            var (component, edit) = MakeEdit();
            DecalBox.SetEnabled(component, edit, true);
            var image = Track(new Texture2D(4, 4));
            DecalBox.SetTexture(component, edit, image);

            DecalBox.SetEnabled(component, edit, false);

            Assert.That(edit.decalEnabled, Is.False);
            Assert.That(edit.decalTexture, Is.SameAs(image), "OFF→ON で画像を入れ直さずに済むように");
        }

        [Test]
        public void OFFからONに戻しても箱は前の位置のまま()
        {
            var (component, edit) = MakeEdit();
            DecalBox.SetEnabled(component, edit, true);
            edit.decalBoxPosition = new Vector3(5f, 5f, 5f);

            DecalBox.SetEnabled(component, edit, false);
            DecalBox.SetEnabled(component, edit, true);

            Assert.That(edit.decalEnabled, Is.True);
            Assert.That(edit.decalBoxPosition, Is.EqualTo(new Vector3(5f, 5f, 5f)));
        }

        [Test]
        public void 印の無い版でONにした編集もOFFからONで箱を置き直さない()
        {
            var (component, edit) = MakeEdit();
            edit.decalEnabled = true; // 印（decalInitialized）が無い版で ON にした編集
            edit.decalBoxPosition = new Vector3(5f, 5f, 5f);

            DecalBox.SetEnabled(component, edit, false);
            DecalBox.SetEnabled(component, edit, true);

            Assert.That(edit.decalBoxPosition, Is.EqualTo(new Vector3(5f, 5f, 5f)));
        }

        [Test]
        public void ResetSettings_で箱が既定に戻り画像と詳細設定は残る()
        {
            var (component, edit) = MakeEdit();
            DecalBox.SetEnabled(component, edit, true);
            var image = Track(new Texture2D(4, 4));
            DecalBox.SetTexture(component, edit, image);
            edit.decalKeepAspect = false;
            edit.decalBoxPosition = new Vector3(5f, 5f, 5f);
            var box = GradientBox.DefaultFor(component, edit);

            DecalBox.ResetSettings(component, edit);

            Assert.That(Vector3.Distance(edit.decalBoxPosition, box.position), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(edit.decalBoxSize, box.size), Is.LessThan(1e-4f));
            Assert.That(edit.decalEnabled, Is.True);
            Assert.That(edit.decalTexture, Is.SameAs(image));
            Assert.That(edit.decalKeepAspect, Is.False);
        }
    }
}
