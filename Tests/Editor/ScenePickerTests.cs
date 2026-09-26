using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Picking;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    public class ScenePickerTests
    {
        private readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        /// <summary>z 位置に XY 平面の 1x1 クアッド（中心原点、UV 0..1）を置く</summary>
        private Renderer MakeQuad(string name, float z)
        {
            var mesh = new Mesh();
            mesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            var go = new GameObject(name);
            go.transform.position = new Vector3(0, 0, z);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = new Material(Shader.Find("Standard"));
            _cleanup.Add(mesh); _cleanup.Add(go); _cleanup.Add(renderer.sharedMaterial);
            return renderer;
        }

        [Test]
        public void 最も近いヒットを返し_UV_とテクセルを埋める()
        {
            var near = MakeQuad("near", 1f);
            var far = MakeQuad("far", 3f);
            var tex = new Texture2D(100, 100); _cleanup.Add(tex);
            near.sharedMaterial.SetTexture("_MainTex", tex);
            far.sharedMaterial.SetTexture("_MainTex", tex);
            var picker = new ScenePicker();
            var ray = new Ray(new Vector3(0.25f, -0.25f, -1f), Vector3.forward);

            bool ok = picker.TryPick(ray, new[] { near, far }, null, out var hit);

            Assert.That(ok, Is.True);
            Assert.That(hit.renderer, Is.SameAs(near));
            Assert.That(hit.materialSlot, Is.EqualTo(0));
            Assert.That(hit.uv.x, Is.EqualTo(0.75f).Within(1e-3f));
            Assert.That(hit.uv.y, Is.EqualTo(0.25f).Within(1e-3f));
            Assert.That(hit.texel, Is.EqualTo(new Vector2Int(75, 25)));
            Assert.That(hit.mainTexture.texture, Is.SameAs(tex));
            picker.Dispose();
        }

        [Test]
        public void 透明判定に引っかかった手前は飛ばして奥を返す()
        {
            var near = MakeQuad("near", 1f);
            var far = MakeQuad("far", 3f);
            var tex = new Texture2D(4, 4); _cleanup.Add(tex);
            near.sharedMaterial.SetTexture("_MainTex", tex);
            far.sharedMaterial.SetTexture("_MainTex", tex);
            var picker = new ScenePicker();
            var ray = new Ray(new Vector3(0.25f, -0.25f, -1f), Vector3.forward);

            bool ok = picker.TryPick(ray, new[] { near, far }, h => h.renderer == near ? 0f : 1f, out var hit);

            Assert.That(ok, Is.True);
            Assert.That(hit.renderer, Is.SameAs(far));
            picker.Dispose();
        }

        [Test]
        public void 外れたら_false()
        {
            var quad = MakeQuad("q", 1f);
            var picker = new ScenePicker();
            var ray = new Ray(new Vector3(5f, 5f, -1f), Vector3.forward);

            Assert.That(picker.TryPick(ray, new[] { quad }, null, out _), Is.False);
            picker.Dispose();
        }

        [Test]
        public void テクスチャが無いマテリアルでもヒットは返す()
        {
            var quad = MakeQuad("q", 1f);
            var picker = new ScenePicker();
            var ray = new Ray(new Vector3(0.25f, -0.25f, -1f), Vector3.forward);

            bool ok = picker.TryPick(ray, new[] { quad }, null, out var hit);

            Assert.That(ok, Is.True);
            Assert.That(hit.hasMainTexture, Is.False);
            picker.Dispose();
        }

        [Test]
        public void 読み取り不可メッシュは警告して外れる()
        {
            var quad = MakeQuad("q", 1f);
            quad.GetComponent<MeshFilter>().sharedMesh.UploadMeshData(true); // Read/Write を落とす
            var picker = new ScenePicker();
            var ray = new Ray(new Vector3(0.25f, -0.25f, -1f), Vector3.forward);

            // 警告が出ること自体は LogAssert で、1 回だけであること（重複抑止）はハンドラの件数で検証する
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("Read/Write"));
            int count = 0;
            Application.LogCallback handler = (condition, stackTrace, type) =>
            {
                if (type == LogType.Warning && condition.Contains("Read/Write")) count++;
            };
            Application.logMessageReceived += handler;
            try
            {
                Assert.That(picker.TryPick(ray, new[] { quad }, null, out _), Is.False);
                Assert.That(picker.TryPick(ray, new[] { quad }, null, out _), Is.False);
            }
            finally
            {
                Application.logMessageReceived -= handler;
            }

            Assert.That(count, Is.EqualTo(1));
            picker.Dispose();
        }
    }
}
