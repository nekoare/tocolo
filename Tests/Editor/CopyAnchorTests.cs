using Nekoare.ClickRecolor.Editor.Copy;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>コピーで、種の場所（メッシュ・三角形・テクスチャのスロット）を書き出し、移す先のアバターで探し直す</summary>
    public class CopyAnchorTests
    {
        private CopyTestKit _kit;

        [SetUp] public void SetUp() => _kit = new CopyTestKit();
        [TearDown] public void TearDown() => _kit.Dispose();

        [Test]
        public void 同じメッシュが1つならパスが違っても見つかる()
        {
            var mesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var a = _kit.MakeRoot("A");
            var hairA = _kit.AddRenderer(a, "Hair", mesh, tex);
            var b = _kit.MakeRoot("B");
            var hairB = _kit.AddRenderer(b, "Outfit/Hair", mesh, tex);

            var anchor = CopyAnchor.Describe(a.transform, hairA, 0, 1, new Vector2(0.1f, 0.2f), tex);
            var failure = CopyAnchor.Resolve(anchor, b.transform, out var renderer, out var texture);

            Assert.AreEqual(AnchorFailure.None, failure);
            Assert.AreSame(hairB, renderer);
            Assert.AreSame(tex, texture);
            Assert.AreEqual(1, anchor.triangle);
            Assert.AreEqual(new Vector2(0.1f, 0.2f), anchor.uv);
        }

        [Test]
        public void 色違いのテクスチャでも移す先のマテリアルのテクスチャになる()
        {
            var mesh = _kit.MakeMesh("Hair");
            var red = _kit.MakeTextureAsset(Color.red);
            var blue = _kit.MakeTextureAsset(Color.blue);
            var a = _kit.MakeRoot("A");
            var hairA = _kit.AddRenderer(a, "Hair", mesh, red);
            var b = _kit.MakeRoot("B");
            _kit.AddRenderer(b, "Hair", mesh, blue);

            var anchor = CopyAnchor.Describe(a.transform, hairA, 0, 0, Vector2.zero, red);
            var failure = CopyAnchor.Resolve(anchor, b.transform, out _, out var texture);

            Assert.AreEqual(AnchorFailure.None, failure);
            Assert.AreSame(blue, texture);
        }

        [Test]
        public void 同じメッシュが複数でパスが一致すれば付く()
        {
            var mesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var a = _kit.MakeRoot("A");
            var hairA = _kit.AddRenderer(a, "Hair", mesh, tex);
            var b = _kit.MakeRoot("B");
            var hairB = _kit.AddRenderer(b, "Hair", mesh, tex);
            _kit.AddRenderer(b, "Hair2", mesh, tex);

            var anchor = CopyAnchor.Describe(a.transform, hairA, 0, 0, Vector2.zero, tex);
            var failure = CopyAnchor.Resolve(anchor, b.transform, out var renderer, out _);

            Assert.AreEqual(AnchorFailure.None, failure);
            Assert.AreSame(hairB, renderer);
        }

        [Test]
        public void 同じメッシュが複数でパスが決まらなければAmbiguous()
        {
            var mesh = _kit.MakeMesh("Pierce");
            var tex = _kit.MakeTextureAsset(Color.red);
            var a = _kit.MakeRoot("A");
            var pierceA = _kit.AddRenderer(a, "Pierce", mesh, tex);
            var b = _kit.MakeRoot("B");
            _kit.AddRenderer(b, "L/Pierce", mesh, tex);
            _kit.AddRenderer(b, "R/Pierce", mesh, tex);

            var anchor = CopyAnchor.Describe(a.transform, pierceA, 0, 0, Vector2.zero, tex);
            var failure = CopyAnchor.Resolve(anchor, b.transform, out var renderer, out _);

            Assert.AreEqual(AnchorFailure.Ambiguous, failure);
            Assert.IsNull(renderer);
        }

        [Test]
        public void 親が増えていても末尾の名前で同じメッシュの左右を見分ける()
        {
            var mesh = _kit.MakeMesh("Ear");
            var tex = _kit.MakeTextureAsset(Color.red);
            var a = _kit.MakeRoot("A");
            var earA = _kit.AddRenderer(a, "Ear_L/Mesh", mesh, tex);
            _kit.AddRenderer(a, "Ear_R/Mesh", mesh, tex);
            var b = _kit.MakeRoot("B");
            var earB = _kit.AddRenderer(b, "髪/HairPrefab/Ear_L/Mesh", mesh, tex);
            _kit.AddRenderer(b, "髪/HairPrefab/Ear_R/Mesh", mesh, tex);

            var anchor = CopyAnchor.Describe(a.transform, earA, 0, 0, Vector2.zero, tex);
            var failure = CopyAnchor.Resolve(anchor, b.transform, out var renderer, out _);

            Assert.AreEqual(AnchorFailure.None, failure);
            Assert.AreSame(earB, renderer);
        }

        [Test]
        public void 親が外れていても末尾の名前で同じメッシュの左右を見分ける()
        {
            var mesh = _kit.MakeMesh("Ear");
            var tex = _kit.MakeTextureAsset(Color.red);
            var a = _kit.MakeRoot("A");
            var earA = _kit.AddRenderer(a, "髪/Ear_R/Mesh", mesh, tex);
            var b = _kit.MakeRoot("B");
            _kit.AddRenderer(b, "Ear_L/Mesh", mesh, tex);
            var earB = _kit.AddRenderer(b, "Ear_R/Mesh", mesh, tex);

            var anchor = CopyAnchor.Describe(a.transform, earA, 0, 0, Vector2.zero, tex);
            var failure = CopyAnchor.Resolve(anchor, b.transform, out var renderer, out _);

            Assert.AreEqual(AnchorFailure.None, failure);
            Assert.AreSame(earB, renderer);
        }

        [Test]
        public void 末尾の一致が同じ長さなら道筋がまったく同じ方に付く()
        {
            var mesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var a = _kit.MakeRoot("A");
            var hairA = _kit.AddRenderer(a, "Hair", mesh, tex);
            var b = _kit.MakeRoot("B");
            _kit.AddRenderer(b, "Outfit/Hair", mesh, tex);
            var hairB = _kit.AddRenderer(b, "Hair", mesh, tex);

            var anchor = CopyAnchor.Describe(a.transform, hairA, 0, 0, Vector2.zero, tex);
            var failure = CopyAnchor.Resolve(anchor, b.transform, out var renderer, out _);

            Assert.AreEqual(AnchorFailure.None, failure);
            Assert.AreSame(hairB, renderer);
        }

        [Test]
        public void 頂点数が違うメッシュはMeshNotFound()
        {
            var tex = _kit.MakeTextureAsset(Color.red);
            var a = _kit.MakeRoot("A");
            var hairA = _kit.AddRenderer(a, "Hair", _kit.MakeMesh("Hair", 2), tex);
            var b = _kit.MakeRoot("B");
            _kit.AddRenderer(b, "Hair", _kit.MakeMesh("Hair", 3), tex);

            var anchor = CopyAnchor.Describe(a.transform, hairA, 0, 0, Vector2.zero, tex);

            Assert.AreEqual(AnchorFailure.MeshNotFound, CopyAnchor.Resolve(anchor, b.transform, out _, out _));
        }

        [Test]
        public void 三角形の番号が範囲外ならTriangleOutOfRange()
        {
            var mesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var a = _kit.MakeRoot("A");
            var hairA = _kit.AddRenderer(a, "Hair", mesh, tex);
            var b = _kit.MakeRoot("B");
            _kit.AddRenderer(b, "Hair", mesh, tex);

            var anchor = CopyAnchor.Describe(a.transform, hairA, 0, 0, Vector2.zero, tex);
            anchor.triangle = 4;

            Assert.AreEqual(AnchorFailure.TriangleOutOfRange, CopyAnchor.Resolve(anchor, b.transform, out _, out _));
        }

        [Test]
        public void 移す先のマテリアルにテクスチャが無ければTextureNotFound()
        {
            var mesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var a = _kit.MakeRoot("A");
            var hairA = _kit.AddRenderer(a, "Hair", mesh, tex);
            var b = _kit.MakeRoot("B");
            _kit.AddRenderer(b, "Hair", mesh, null);

            var anchor = CopyAnchor.Describe(a.transform, hairA, 0, 0, Vector2.zero, tex);

            Assert.AreEqual(AnchorFailure.TextureNotFound, CopyAnchor.Resolve(anchor, b.transform, out _, out _));
        }
    }
}
