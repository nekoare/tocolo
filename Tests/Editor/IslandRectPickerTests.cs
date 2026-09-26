using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Picking;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// Ctrl＋ドラッグの矩形選択で、読み戻した島 ID 画素を島の集合に変換する部分（IslandRectPicker.CollectIslands）のテスト。
    /// 描画（Scene カメラで島 ID を描く部分）は Scene ビューに依存するので Unity 上で手で確かめる
    /// </summary>
    public class IslandRectPickerTests
    {
        private readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
            UvChartDetector.ClearCache();
        }

        /// <summary>
        /// UV に 2 つの長方形チャート（A = 頂点 0..3、B = 頂点 4..7）を置いたメッシュ。
        /// splitSubmesh = false なら 1 サブメッシュに A（三角形 0, 1）と B（三角形 2, 3）、
        /// true ならサブメッシュ 0 に A（三角形 0, 1）、サブメッシュ 1 に B（三角形 0, 1）
        /// </summary>
        private Renderer MakeRenderer(bool splitSubmesh)
        {
            var uv = new[]
            {
                new Vector2(0.05f, 0.05f), new Vector2(0.45f, 0.05f), new Vector2(0.45f, 0.45f), new Vector2(0.05f, 0.45f),
                new Vector2(0.55f, 0.55f), new Vector2(0.95f, 0.55f), new Vector2(0.95f, 0.95f), new Vector2(0.55f, 0.95f),
            };
            var vertices = new Vector3[uv.Length];
            for (int i = 0; i < uv.Length; i++) vertices[i] = uv[i];
            var mesh = new Mesh { vertices = vertices, uv = uv };
            if (splitSubmesh)
            {
                mesh.subMeshCount = 2;
                mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
                mesh.SetTriangles(new[] { 4, 5, 6, 4, 6, 7 }, 1);
            }
            else
            {
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            }
            var go = new GameObject("Body");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            _cleanup.Add(mesh);
            _cleanup.Add(go);
            return renderer;
        }

        /// <summary>島 ID 画素（RGBA の float 4 つ。R = Renderer 番号×256＋サブメッシュ、G = 三角形番号）の列を作る。無効画素は (-1, -1)</summary>
        private static float[] Pixels(params (float code, float primitive)[] values)
        {
            var pixels = new float[values.Length * 4];
            for (int i = 0; i < values.Length; i++)
            {
                pixels[i * 4] = values[i].code;
                pixels[i * 4 + 1] = values[i].primitive;
                pixels[i * 4 + 2] = 0f;
                pixels[i * 4 + 3] = 1f;
            }
            return pixels;
        }

        [Test]
        public void 同じ島の画素が複数あっても島は1つ()
        {
            var renderer = MakeRenderer(splitSubmesh: false);
            // 三角形 0 と 1 はどちらもチャート A
            var pixels = Pixels((0, 0), (0, 1), (0, 0), (0, 1));

            var islands = IslandRectPicker.CollectIslands(pixels, 2, 2, new[] { renderer });

            Assert.That(islands.Count, Is.EqualTo(1));
            Assert.That(islands[0].renderer, Is.SameAs(renderer));
            Assert.That(islands[0].submesh, Is.EqualTo(0));
            // 代表は最初に見つかった三角形
            Assert.That(islands[0].triangle, Is.EqualTo(0));
            // 代表三角形 (0,1,2) の UV 重心
            Assert.That(islands[0].uv0.x, Is.EqualTo((0.05f + 0.45f + 0.45f) / 3f).Within(1e-5f));
            Assert.That(islands[0].uv0.y, Is.EqualTo((0.05f + 0.05f + 0.45f) / 3f).Within(1e-5f));
        }

        [Test]
        public void 同じサブメッシュの別チャートは別の島()
        {
            var renderer = MakeRenderer(splitSubmesh: false);
            var pixels = Pixels((0, 1), (0, 3));

            var islands = IslandRectPicker.CollectIslands(pixels, 2, 1, new[] { renderer });

            Assert.That(islands.Count, Is.EqualTo(2));
            Assert.That(islands[0].triangle, Is.EqualTo(1));
            Assert.That(islands[1].triangle, Is.EqualTo(3));
            Assert.That(islands[0].chartId, Is.Not.EqualTo(islands[1].chartId));
        }

        [Test]
        public void 別サブメッシュの画素は別の島になる()
        {
            var renderer = MakeRenderer(splitSubmesh: true);
            // サブメッシュ 0 の三角形 0 と、サブメッシュ 1 の三角形 0（どちらもローカル番号）
            var pixels = Pixels((0 * 256 + 0, 0), (0 * 256 + 1, 0), (0 * 256 + 1, 1));

            var islands = IslandRectPicker.CollectIslands(pixels, 3, 1, new[] { renderer });

            Assert.That(islands.Count, Is.EqualTo(2));
            Assert.That(islands[0].submesh, Is.EqualTo(0));
            Assert.That(islands[1].submesh, Is.EqualTo(1));
            Assert.That(islands[1].triangle, Is.EqualTo(0));
        }

        [Test]
        public void 三角形番号がメッシュ全体の番号でもサブメッシュ内の番号に直す()
        {
            var renderer = MakeRenderer(splitSubmesh: true);
            // サブメッシュ 1 は全体の三角形 2, 3。3 はサブメッシュ 1 の三角形数（2）以上なので全体番号とみなせる
            var pixels = Pixels((1, 3), (1, 2));

            var islands = IslandRectPicker.CollectIslands(pixels, 2, 1, new[] { renderer });

            Assert.That(islands.Count, Is.EqualTo(1));
            Assert.That(islands[0].submesh, Is.EqualTo(1));
            Assert.That(islands[0].triangle, Is.EqualTo(1));
        }

        [Test]
        public void 無効画素と範囲外の番号は無視する()
        {
            var renderer = MakeRenderer(splitSubmesh: false);
            // -1 = 何も描かれていない画素。Renderer 番号 5 は範囲外、三角形 99 は範囲外
            var pixels = Pixels((-1, -1), (-1, -1), (5 * 256, 0), (0, 99));

            var islands = IslandRectPicker.CollectIslands(pixels, 2, 2, new[] { renderer });

            Assert.That(islands, Is.Empty);
        }

        [Test]
        public void 複数の_Renderer_は番号で区別する()
        {
            var first = MakeRenderer(splitSubmesh: false);
            var second = MakeRenderer(splitSubmesh: false);
            var pixels = Pixels((1 * 256, 0), (0 * 256, 0), (1 * 256, 1));

            var islands = IslandRectPicker.CollectIslands(pixels, 3, 1, new[] { first, second });

            Assert.That(islands.Count, Is.EqualTo(2));
            Assert.That(islands[0].renderer, Is.SameAs(second));
            Assert.That(islands[1].renderer, Is.SameAs(first));
        }

        [Test]
        public void メインテクスチャがあれば島のテクスチャと_Tiling_Offset_適用後の_UV_が入る()
        {
            var renderer = MakeRenderer(splitSubmesh: false);
            var texture = new Texture2D(4, 4);
            var material = new Material(Shader.Find("Standard"));
            material.SetTexture("_MainTex", texture);
            material.SetTextureOffset("_MainTex", new Vector2(0.5f, 0f));
            renderer.sharedMaterial = material;
            _cleanup.Add(texture);
            _cleanup.Add(material);

            var islands = IslandRectPicker.CollectIslands(Pixels((0, 0)), 1, 1, new[] { renderer });

            Assert.That(islands.Count, Is.EqualTo(1));
            Assert.That(islands[0].texture, Is.SameAs(texture));
            Assert.That(islands[0].uv.x, Is.EqualTo(islands[0].uv0.x + 0.5f).Within(1e-5f));
            Assert.That(islands[0].uv.y, Is.EqualTo(islands[0].uv0.y).Within(1e-5f));
        }
    }
}
