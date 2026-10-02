using Nekoare.ClickRecolor.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
#if CLICKRECOLOR_VRCSDK3_AVATARS
using System.Linq;
#endif

namespace Nekoare.ClickRecolor.Tests
{
    public class TargetResolverTests
    {
        private GameObject _root;

        [TearDown]
        public void TearDown()
        {
            // GetOrAddComponent は Undo.AddComponent を使うので、Undo 記録を消してから破棄する。
            // 消さずに DestroyImmediate すると、後で Ctrl+Z / Ctrl+Y したときに空の「avatar」がシーンに復活する
            if (_root != null)
            {
                foreach (var c in _root.GetComponentsInChildren<Component>(true)) if (c != null) Undo.ClearUndo(c);
                foreach (var t in _root.GetComponentsInChildren<Transform>(true)) Undo.ClearUndo(t.gameObject);
                Object.DestroyImmediate(_root);
            }
        }

        [Test]
        public void ResolveRoot_は祖先に_Descriptor_が無ければ_transform_root_を返す()
        {
            _root = new GameObject("prop");
            var middle = new GameObject("middle");
            middle.transform.SetParent(_root.transform);
            var leaf = new GameObject("leaf");
            leaf.transform.SetParent(middle.transform);

            Assert.That(TargetResolver.ResolveRoot(leaf), Is.SameAs(_root));
            Assert.That(TargetResolver.ResolveRoot(_root), Is.SameAs(_root));
        }

        [Test]
        public void GetOrAddComponent_は_2_回呼んでも_1_つしか付かない()
        {
            _root = new GameObject("avatar");

            var first = TargetResolver.GetOrAddComponent(_root);
            var second = TargetResolver.GetOrAddComponent(_root);

            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.SameAs(first));
            Assert.That(_root.GetComponents<ClickRecolor>().Length, Is.EqualTo(1));
        }

#if CLICKRECOLOR_VRCSDK3_AVATARS
        // テスト asmdef は overrideReferences=true で VRC SDK の dll を直接参照していないため、
        // 本体と同じく型名で Descriptor の型を引いて AddComponent する
        private static System.Type FindAvatarDescriptorType()
        {
            return TypeCache.GetTypesDerivedFrom<Component>().FirstOrDefault(t => t.Name == "VRCAvatarDescriptor");
        }

        [Test]
        public void IsUsableTarget_は_無効化と目のマークの非表示で_false()
        {
            _root = new GameObject("avatar");
            Assert.That(TargetResolver.IsUsableTarget(_root), Is.True);

            _root.SetActive(false);
            Assert.That(TargetResolver.IsUsableTarget(_root), Is.False, "無効化");
            _root.SetActive(true);

            SceneVisibilityManager.instance.Hide(_root, true);
            try
            {
                Assert.That(TargetResolver.IsUsableTarget(_root), Is.False, "目のマークで非表示");
            }
            finally
            {
                SceneVisibilityManager.instance.Show(_root, true);
            }
            Assert.That(TargetResolver.IsUsableTarget(null), Is.False);
        }

        [Test]
        public void FindSceneAvatarRoots_は_Descriptor_付きオブジェクトを検出する()
        {
            var descriptorType = FindAvatarDescriptorType();
            Assert.That(descriptorType, Is.Not.Null, "VRCAvatarDescriptor型が見つかりません");

            _root = new GameObject("avatar");
            _root.AddComponent(descriptorType);
            var child = new GameObject("child");
            child.transform.SetParent(_root.transform);

            // 一覧は Hierarchy 変更イベントまでキャッシュされる（同じフレームでは古いまま）ので捨ててから呼ぶ
            TargetResolver.InvalidateAvatarRoots();
            var roots = TargetResolver.FindSceneAvatarRoots();

            // 開いているシーンに別のアバターがあっても通るよう、個数ではなく含まれることを見る
            Assert.That(roots, Has.Member(_root));
            Assert.That(roots, Has.No.Member(child));
            Assert.That(roots.Count(r => r == _root), Is.EqualTo(1));
        }

        [Test]
        public void ResolveRoot_は祖先の_Descriptor_付きオブジェクトを返す()
        {
            var descriptorType = FindAvatarDescriptorType();
            Assert.That(descriptorType, Is.Not.Null, "VRCAvatarDescriptor型が見つかりません");

            _root = new GameObject("world");
            var avatar = new GameObject("avatar");
            avatar.transform.SetParent(_root.transform);
            avatar.AddComponent(descriptorType);
            var leaf = new GameObject("leaf");
            leaf.transform.SetParent(avatar.transform);

            Assert.That(TargetResolver.ResolveRoot(leaf), Is.SameAs(avatar));
        }
#endif
    }
}
