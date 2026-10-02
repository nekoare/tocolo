using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// グラデーションの位置マップ（PositionMap）の GPU テスト: UV 空間の各画素に対象ルートのローカル位置が書かれること
    /// </summary>
    public class PositionMapTests
    {
        private const int Size = 64;

        private readonly List<Object> _cleanup = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            {
                Assert.Ignore("この環境はARGBFloatのRenderTextureに対応していません");
            }
            Assert.That(PositionMap.IsAvailable, Is.True, "PositionMap.shaderが読み込めません（.metaのGUIDを確認）");
        }

        [TearDown]
        public void TearDown()
        {
            PositionMap.ClearCache();
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private T Track<T>(T obj) where T : Object
        {
            _cleanup.Add(obj);
            return obj;
        }

        /// <summary>
        /// 1 辺 1 の四角形（XY 平面、中心が原点）。uv (0,0) が (−0.5, −0.5)、(1,1) が (0.5, 0.5)。
        /// uvMax を 1 未満にすると UV の右上側が空く
        /// </summary>
        private Mesh MakeQuad(float uvMax = 1f)
        {
            var mesh = Track(new Mesh());
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(uvMax, 0f),
                new Vector2(0f, uvMax),
                new Vector2(uvMax, uvMax),
            };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
            return mesh;
        }

        /// <summary>ルート（動かして回して拡大）の子に、texture をメインに持つ四角形の MeshRenderer を置く</summary>
        private (Transform root, MeshRenderer renderer) MakeScene(Texture2D texture, Mesh mesh)
        {
            var root = Track(new GameObject("Root")).transform;
            root.position = new Vector3(1f, 2f, 3f);
            root.rotation = Quaternion.Euler(0f, 30f, 0f);
            root.localScale = Vector3.one * 2f;

            var child = new GameObject("Quad");
            child.transform.SetParent(root, false);
            child.transform.localPosition = new Vector3(0.25f, 0.5f, -0.1f);
            child.transform.localRotation = Quaternion.Euler(10f, 0f, 20f);
            child.transform.localScale = new Vector3(1.5f, 0.5f, 1f);
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = child.AddComponent<MeshRenderer>();
            var material = Track(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            renderer.sharedMaterial = material;
            return (root, renderer);
        }

        private static Color[] ReadFloat(RenderTexture rt)
        {
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                tex.Apply(false);
                return tex.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void 四角形の位置マップの4隅がルートのローカル位置になる()
        {
            var texture = Track(new Texture2D(4, 4));
            var (root, renderer) = MakeScene(texture, MakeQuad());

            var map = PositionMap.GetOrBuild(root, new Renderer[] { renderer }, texture, Size, Size);

            Assert.That(map, Is.Not.Null);
            var pixels = ReadFloat(map);
            // 画素中心の UV（0 行目 = v 最小）に写る四角形の点を、ルートのローカルへ
            foreach (var (x, y) in new[] { (0, 0), (Size - 1, 0), (0, Size - 1), (Size - 1, Size - 1) })
            {
                float u = (x + 0.5f) / Size;
                float v = (y + 0.5f) / Size;
                var world = renderer.transform.TransformPoint(new Vector3(u - 0.5f, v - 0.5f, 0f));
                var expected = root.InverseTransformPoint(world);
                var actual = pixels[y * Size + x];
                string at = $"画素({x}, {y})";
                Assert.That(actual.a, Is.EqualTo(1f).Within(1e-4f), at + "のA（描いた印）");
                Assert.That(actual.r, Is.EqualTo(expected.x).Within(1e-3f), at + "のx");
                Assert.That(actual.g, Is.EqualTo(expected.y).Within(1e-3f), at + "のy");
                Assert.That(actual.b, Is.EqualTo(expected.z).Within(1e-3f), at + "のz");
            }
        }

        [Test]
        public void 描かれない画素は_A_が_0_で_キャッシュは同じ_RT_を返す()
        {
            var texture = Track(new Texture2D(4, 4));
            // UV を左下 1/2 だけに縮める（右上は空く）
            var (root, renderer) = MakeScene(texture, MakeQuad(uvMax: 0.5f));

            var map = PositionMap.GetOrBuild(root, new Renderer[] { renderer }, texture, Size, Size);

            Assert.That(map, Is.Not.Null);
            var pixels = ReadFloat(map);
            Assert.That(pixels[0].a, Is.EqualTo(1f).Within(1e-4f), "左下は描かれる");
            Assert.That(pixels[(Size - 1) * Size + (Size - 1)].a, Is.EqualTo(0f), "右上は描かれない");

            Assert.That(PositionMap.GetOrBuild(root, new Renderer[] { renderer }, texture, Size, Size), Is.SameAs(map));
            Assert.That(PositionMap.Count, Is.EqualTo(1));
        }

        [Test]
        public void 膨張でUVの空き領域でも三角形から4px以内なら描かれて隣の位置に近い()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            {
                Assert.Ignore("この環境はcompute shader（ARGBFloatへの書き込み）に対応していません");
            }
            var texture = Track(new Texture2D(4, 4));
            // UV を左下 1/2 だけに縮める: 描かれるのは x, y ≤ 31 の画素
            var (root, renderer) = MakeScene(texture, MakeQuad(uvMax: 0.5f));

            var map = PositionMap.GetOrBuild(root, new Renderer[] { renderer }, texture, Size, Size, fillIterations: 4);

            Assert.That(map, Is.Not.Null);
            var pixels = ReadFloat(map);
            const int row = 10;
            var edge = pixels[row * Size + 31];
            Assert.That(edge.a, Is.EqualTo(1f).Within(1e-4f), "三角形の端は描かれる");
            // 端から 4px 外（x = 35）は膨張で埋まり、端の位置に近い（同じ行の端から横へ写した値）
            var filled = pixels[row * Size + 35];
            Assert.That(filled.a, Is.EqualTo(1f).Within(1e-4f), "4px以内は埋まる");
            float distance = Vector3.Distance(new Vector3(filled.r, filled.g, filled.b), new Vector3(edge.r, edge.g, edge.b));
            Assert.That(distance, Is.LessThan(1e-3f), "埋めた位置は隣（端）の位置に近い");
            // 5px 以上外は埋まらない
            Assert.That(pixels[row * Size + 40].a, Is.EqualTo(0f), "膨張の回数より外は描かれない");
        }

        [Test]
        public void 対象のテクスチャを使うスロットが無ければ_null()
        {
            var texture = Track(new Texture2D(4, 4));
            var other = Track(new Texture2D(4, 4));
            var (root, renderer) = MakeScene(texture, MakeQuad());

            Assert.That(PositionMap.GetOrBuild(root, new Renderer[] { renderer }, other, Size, Size), Is.Null);
        }
    }
}
