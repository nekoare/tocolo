using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// グラデーション「毛先から」の毛先マップ（TipMap）の GPU テスト: UV 空間の各画素に毛先からの距離と房の長さが書かれること
    /// </summary>
    public class TipMapTests
    {
        private const int Size = 32;
        private const float Length = 0.4f;

        private readonly List<Object> _cleanup = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            {
                Assert.Ignore("この環境はARGBFloatのRenderTextureに対応していません");
            }
        }

        [TearDown]
        public void TearDown()
        {
            TipMap.ClearCache();
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
        /// 幅 0.02・長さ Length の垂れた帯（y = 0 から真下へ。4 段）。uv は u が横、v が下端（毛先）0 → 上端 1
        /// </summary>
        private Mesh MakeStrip()
        {
            const int segments = 4;
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            for (int s = 0; s <= segments; s++)
            {
                float v = 1f - s / (float)segments;
                float y = -Length * s / segments;
                vertices.Add(new Vector3(-0.01f, y, 0f));
                vertices.Add(new Vector3(0.01f, y, 0f));
                uvs.Add(new Vector2(0f, v));
                uvs.Add(new Vector2(1f, v));
            }
            for (int s = 0; s < segments; s++)
            {
                int a = s * 2;
                triangles.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 });
            }
            var mesh = Track(new Mesh());
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            return mesh;
        }

        private (Transform root, MeshRenderer renderer) MakeScene(Texture2D texture)
        {
            var root = Track(new GameObject("Root")).transform;
            var child = new GameObject("Strip");
            child.transform.SetParent(root, false);
            child.AddComponent<MeshFilter>().sharedMesh = MakeStrip();
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
        public void 垂れた房の毛先マップは毛先からの距離が下の端の0から上へ増え_房の長さが入る()
        {
            var texture = Track(new Texture2D(4, 4));
            var (root, renderer) = MakeScene(texture);

            var map = TipMap.GetOrBuild(root, new Renderer[] { renderer }, texture, Size, Size, 0, new Vector3(0f, 0.1f, 0f));

            Assert.That(map, Is.Not.Null, "TipMap.shaderが読み込めません（.metaのGUIDを確認）");
            var pixels = ReadFloat(map);
            foreach (int y in new[] { 0, Size / 2, Size - 1 })
            {
                var actual = pixels[y * Size + Size / 2];
                float v = (y + 0.5f) / Size;
                string at = $"行{y}";
                Assert.That(actual.a, Is.EqualTo(1f).Within(1e-4f), at + "のA（描いた印）");
                Assert.That(actual.r, Is.EqualTo(v * Length).Within(2e-3f), at + "の毛先からの距離");
                Assert.That(actual.g, Is.EqualTo(Length).Within(1e-3f), at + "の房の長さ");
            }
        }

        [Test]
        public void 同じてっぺんなら同じRTを返し_てっぺんを動かすと作り直す()
        {
            var texture = Track(new Texture2D(4, 4));
            var (root, renderer) = MakeScene(texture);
            var renderers = new Renderer[] { renderer };

            var first = TipMap.GetOrBuild(root, renderers, texture, Size, Size, 0, new Vector3(0f, 0.1f, 0f));
            var again = TipMap.GetOrBuild(root, renderers, texture, Size, Size, 0, new Vector3(0f, 0.1f, 0f));
            var moved = TipMap.GetOrBuild(root, renderers, texture, Size, Size, 0, new Vector3(0f, -1f, 0f));

            Assert.That(again, Is.SameAs(first));
            Assert.That(moved, Is.Not.SameAs(first));
            // てっぺんを帯の下に置くと、上の端が毛先になる（上の端の距離が 0 に近い）
            var pixels = ReadFloat(moved);
            Assert.That(pixels[(Size - 1) * Size + Size / 2].r, Is.LessThan(0.02f));
        }
    }
}
