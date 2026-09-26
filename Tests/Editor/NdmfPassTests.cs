using Nekoare.ClickRecolor.Editor.NDMF;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    public class NdmfPassTests
    {
        private GameObject _root;
        private Texture2D _texture;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_texture != null) Object.DestroyImmediate(_texture);
        }

        /// <summary>ビルドに効く編集（目標色あり・対象テクスチャあり）</summary>
        private RecolorEdit MakeEffectiveEdit()
        {
            if (_texture == null) _texture = new Texture2D(1, 1);
            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = _texture;
            edit.hasTarget = true;
            return edit;
        }

        [Test]
        public void RemoveAll_は非アクティブ子を含めて全部消し_個数を返す()
        {
            _root = new GameObject("avatar");
            _root.AddComponent<ClickRecolor>();
            var child = new GameObject("child");
            child.transform.SetParent(_root.transform);
            child.SetActive(false);
            child.AddComponent<ClickRecolor>();

            int removed = RemoveComponentsPass.RemoveAll(_root);

            Assert.That(removed, Is.EqualTo(2));
            Assert.That(_root.GetComponentsInChildren<ClickRecolor>(true), Is.Empty);
        }

        [Test]
        public void CountEnabledEdits_は_applyOnBuild_と_enabled_と目標色と対象テクスチャを見る()
        {
            _root = new GameObject("avatar");
            var component = _root.AddComponent<ClickRecolor>();
            component.AddEdit(MakeEffectiveEdit());
            var disabled = component.AddEdit(MakeEffectiveEdit());
            disabled.enabled = false;
            var noTarget = component.AddEdit(MakeEffectiveEdit());
            noTarget.hasTarget = false;
            var noTexture = component.AddEdit(MakeEffectiveEdit());
            noTexture.sourceTexture = null;

            Assert.That(RecolorPass.CountEnabledEdits(_root), Is.EqualTo(1));

            component.applyOnBuild = false;
            Assert.That(RecolorPass.CountEnabledEdits(_root), Is.EqualTo(0));

            // 非アクティブな子に付いたコンポーネントも数える
            var child = new GameObject("child");
            child.transform.SetParent(_root.transform);
            child.SetActive(false);
            var childComponent = child.AddComponent<ClickRecolor>();
            childComponent.AddEdit(MakeEffectiveEdit());
            Assert.That(RecolorPass.CountEnabledEdits(_root), Is.EqualTo(1));

            // null 要素は例外にならず数えない
            childComponent.edits.Add(null);
            Assert.That(RecolorPass.CountEnabledEdits(_root), Is.EqualTo(1));
        }
    }
}
