using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 対象（ルート）の切り替え（RecolorSceneTool.SetTarget）と編集の選択（SelectEdit）:
    /// 別の対象・別の編集に移るときだけ色未設定の仮の編集を捨て、前の対象でのクリックの印を消す
    /// </summary>
    public class TargetSwitchTests
    {
        private GameObject _a;
        private GameObject _b;

        [SetUp]
        public void SetUp()
        {
            ToolSession.Clear();
            _a = new GameObject("AvatarA");
            _b = new GameObject("AvatarB");
        }

        [TearDown]
        public void TearDown()
        {
            ToolSession.Clear();
            foreach (var go in new[] { _a, _b })
            {
                if (go == null) continue;
                foreach (var c in go.GetComponents<Component>()) if (c != null) Undo.ClearUndo(c);
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>root に色未設定の編集を作り、選んだ状態にする</summary>
        private static RecolorEdit AddPendingEdit(GameObject root)
        {
            var component = root.AddComponent<ClickRecolor>();
            var edit = RecolorEdit.CreateNew();
            component.AddEdit(edit);
            ToolSession.CurrentEditId = edit.id;
            return edit;
        }

        [Test]
        public void 別の対象に変えると_色未設定の編集を捨ててクリックの印を消す()
        {
            var pending = AddPendingEdit(_a);
            ToolSession.SetActiveRoot(_a);
            ToolSession.LastPick = default(PickHit);
            ToolSession.LastPickColor = Color.red;

            RecolorSceneTool.SetTarget(_b);

            Assert.That(ToolSession.TryGetActiveRoot(out var root) && root == _b, Is.True);
            Assert.That(_a.GetComponent<ClickRecolor>().FindEdit(pending.id), Is.Null, "色未設定の仮の編集は残さない");
            Assert.That(ToolSession.CurrentEditId, Is.Null);
            Assert.That(ToolSession.LastPick, Is.Null);
            Assert.That(ToolSession.LastPickColor, Is.Null);
        }

        [Test]
        public void 同じ対象なら何も捨てない()
        {
            var pending = AddPendingEdit(_a);
            ToolSession.SetActiveRoot(_a);

            RecolorSceneTool.SetTarget(_a);

            Assert.That(_a.GetComponent<ClickRecolor>().FindEdit(pending.id), Is.Not.Null);
            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(pending.id));
        }

        [Test]
        public void 対象を外しても_色を決めた編集は残す()
        {
            var edit = AddPendingEdit(_a);
            edit.hasTarget = true;
            ToolSession.SetActiveRoot(_a);

            RecolorSceneTool.SetTarget(null);

            Assert.That(ToolSession.TryGetActiveRoot(out _), Is.False);
            Assert.That(_a.GetComponent<ClickRecolor>().FindEdit(edit.id), Is.Not.Null);
            Assert.That(ToolSession.CurrentEditId, Is.Null, "選択だけ外す");
        }

        [Test]
        public void 別の編集を選ぶと色未設定の編集を捨て_同じ編集なら何もしない()
        {
            var pending = AddPendingEdit(_a);
            var component = _a.GetComponent<ClickRecolor>();
            var other = RecolorEdit.CreateNew();
            other.hasTarget = true;
            component.AddEdit(other);

            RecolorSceneTool.SelectEdit(pending.id);
            Assert.That(component.FindEdit(pending.id), Is.Not.Null, "同じ編集の選び直しでは捨てない");

            RecolorSceneTool.SelectEdit(other.id);
            Assert.That(component.FindEdit(pending.id), Is.Null);
            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(other.id));
        }
    }
}
