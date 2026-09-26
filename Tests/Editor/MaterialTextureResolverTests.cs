using Nekoare.ClickRecolor.Editor.Picking;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    public class MaterialTextureResolverTests
    {
        private Material _material;
        private Texture2D _texture;

        [SetUp]
        public void SetUp()
        {
            _material = new Material(Shader.Find("Standard"));
            _texture = new Texture2D(64, 32);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_material);
            Object.DestroyImmediate(_texture);
        }

        [Test]
        public void MainTex_を持つマテリアルからプロパティ名とテクスチャを返す()
        {
            _material.SetTexture("_MainTex", _texture);

            bool ok = MaterialTextureResolver.TryGetMainTexture(_material, out var info);

            Assert.That(ok, Is.True);
            Assert.That(info.propertyName, Is.EqualTo("_MainTex"));
            Assert.That(info.texture, Is.SameAs(_texture));
        }

        [Test]
        public void テクスチャ未設定なら_false()
        {
            Assert.That(MaterialTextureResolver.TryGetMainTexture(_material, out _), Is.False);
        }

        [Test]
        public void ToTextureCoord_は_Tiling_Offset_を適用して_0_1_に畳む()
        {
            var info = new MainTextureInfo { propertyName = "_MainTex", texture = _texture, scale = new Vector2(2f, 1f), offset = new Vector2(0.25f, 0f) };

            Vector2 uv = MaterialTextureResolver.ToTextureCoord(info, new Vector2(0.5f, 0.5f));

            Assert.That(uv.x, Is.EqualTo(0.25f).Within(1e-5f)); // 0.5*2+0.25 = 1.25 → 0.25
            Assert.That(uv.y, Is.EqualTo(0.5f).Within(1e-5f));
        }

        [Test]
        public void ToTexel_は範囲内にクランプする()
        {
            var info = new MainTextureInfo { propertyName = "_MainTex", texture = _texture };

            Assert.That(MaterialTextureResolver.ToTexel(info, new Vector2(0.999f, 0.999f)), Is.EqualTo(new Vector2Int(63, 31)));
            Assert.That(MaterialTextureResolver.ToTexel(info, new Vector2(1f, 1f)), Is.EqualTo(new Vector2Int(63, 31)));
            Assert.That(MaterialTextureResolver.ToTexel(info, new Vector2(0f, 0f)), Is.EqualTo(Vector2Int.zero));
        }
    }
}
