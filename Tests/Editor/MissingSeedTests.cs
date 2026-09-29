using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Pipeline;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>クリックしたパーツが削除された編集の判定と一括削除（RecolorPipeline.IsSeedMissing / EditGroups.RemoveMissing）</summary>
    public class MissingSeedTests
    {
        private readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private MeshRenderer MakeRenderer(GameObject root, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform);
            go.AddComponent<MeshFilter>();
            return go.AddComponent<MeshRenderer>();
        }

        private static RecolorEdit MakeEdit(Renderer seed, string groupId = "")
        {
            var edit = RecolorEdit.CreateNew();
            edit.seedRenderer = seed;
            edit.hasTarget = true;
            edit.groupId = groupId;
            return edit;
        }

        [Test]
        public void 種の_Renderer_が削除された編集だけ見つからない扱いになり_一括削除で消える()
        {
            var root = new GameObject("Avatar");
            _cleanup.Add(root);
            var component = root.AddComponent<ClickRecolor>();
            var kept = MakeRenderer(root, "Kept");
            var deleted = MakeRenderer(root, "Deleted");
            var deletedMember = MakeRenderer(root, "DeletedMember");

            var alive = component.AddEdit(MakeEdit(kept));
            var missing = component.AddEdit(MakeEdit(deleted));
            // 連結（2 件）のうち 1 件だけパーツが消える
            var groupHead = component.AddEdit(MakeEdit(kept, "g1"));
            var groupMissing = component.AddEdit(MakeEdit(deletedMember, "g1"));

            Object.DestroyImmediate(deleted.gameObject);
            Object.DestroyImmediate(deletedMember.gameObject);

            Assert.That(RecolorPipeline.IsSeedMissing(alive), Is.False);
            Assert.That(RecolorPipeline.IsSeedMissing(missing), Is.True);
            Assert.That(RecolorPipeline.IsSeedMissing(groupMissing), Is.True);

            int removed = EditGroups.RemoveMissing(component);

            Assert.That(removed, Is.EqualTo(2));
            Assert.That(component.edits, Is.EquivalentTo(new[] { alive, groupHead }));
            Assert.That(groupHead.groupId, Is.Empty, "連結の残りが 1 件なら単独に戻る");
        }

        [Test]
        public void 共有の種色を持つアバター全体の編集は種が無くても見つからない扱いにしない()
        {
            var edit = MakeEdit(null);
            edit.mode = SelectionMode.Color;
            edit.scope = ColorScope.WholeAvatar;
            edit.hasSeedOklab = true;
            Assert.That(RecolorPipeline.IsSeedMissing(edit), Is.False);
        }
    }
}
