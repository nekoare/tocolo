using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.NDMF;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// テクスチャごとの基準のコンポーネント（TextureOwners）: 同じテクスチャを複数のコンポーネントが編集しているとき、
    /// 効く編集を最初に持つものを基準にし、効かない編集（色未設定・無効）しか持たないものは数えない
    /// </summary>
    public class TextureOwnersTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            _created.Clear();
        }

        private ClickRecolor NewComponent(string name)
        {
            var go = new GameObject(name);
            _created.Add(go);
            return go.AddComponent<ClickRecolor>();
        }

        private static RecolorEdit AddEdit(ClickRecolor component, Texture2D texture, bool hasTarget)
        {
            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = texture;
            edit.hasTarget = hasTarget;
            component.AddEdit(edit);
            return edit;
        }

        [Test]
        public void 色未設定の編集しか持たないコンポーネントは基準にしない()
        {
            var texture = new Texture2D(2, 2);
            _created.Add(texture);
            var first = NewComponent("First");
            var second = NewComponent("Second");
            // ハイライト中の色未設定の編集（プレビューだけで扱う）を前のコンポーネントが持っている
            AddEdit(first, texture, hasTarget: false);
            AddEdit(second, texture, hasTarget: true);
            var components = new List<ClickRecolor> { first, second };

            var owners = TextureOwners.FirstBaking(components, c => true);

            Assert.That(owners.Of(texture), Is.SameAs(second));
            Assert.That(owners.IndexOf(texture), Is.EqualTo(1));
            Assert.That(TextureOwners.FirstEffective(components, c => true).Of(texture), Is.SameAs(second));
        }

        [Test]
        public void 効く編集を最初に持つコンポーネントを基準にし_止めているものは飛ばす()
        {
            var texture = new Texture2D(2, 2);
            var other = new Texture2D(2, 2);
            _created.Add(texture);
            _created.Add(other);
            var first = NewComponent("First");
            var second = NewComponent("Second");
            AddEdit(first, texture, hasTarget: true);
            AddEdit(second, texture, hasTarget: true);
            AddEdit(second, other, hasTarget: true);
            var components = new List<ClickRecolor> { first, second };

            var owners = TextureOwners.FirstBaking(components, c => true);
            Assert.That(owners.Of(texture), Is.SameAs(first));
            Assert.That(owners.Of(other), Is.SameAs(second));
            Assert.That(owners.Textures, Is.EqualTo(new[] { texture, other }), "最初に見つけた順");

            var withoutFirst = TextureOwners.FirstBaking(components, c => c != first);
            Assert.That(withoutFirst.Of(texture), Is.SameAs(second), "プレビュー・ビルドを止めたコンポーネントは数えない");
        }

        [Test]
        public void 数える編集が無いテクスチャは_null()
        {
            var texture = new Texture2D(2, 2);
            _created.Add(texture);
            var component = NewComponent("Only");
            AddEdit(component, texture, hasTarget: false);

            var owners = TextureOwners.FirstBaking(new List<ClickRecolor> { component }, c => true);

            Assert.That(owners.Of(texture), Is.Null);
            Assert.That(owners.IndexOf(texture), Is.EqualTo(-1));
            Assert.That(owners.Of(null), Is.Null);
        }
    }
}
