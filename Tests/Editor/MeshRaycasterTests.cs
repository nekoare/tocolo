// 流用元: dev.nekoare.tex-col-adjuster/Tests/Editor/MeshRaycasterTests.cs
using NUnit.Framework;
using Nekoare.ClickRecolor.Editor.Picking;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>MeshRaycaster の三角形交差と UV 補間。</summary>
    public class MeshRaycasterTests
    {
        private GameObject _go;
        private readonly System.Collections.Generic.List<Object> _cleanup = new System.Collections.Generic.List<Object>();

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        [Test]
        public void IntersectTriangle_HitsFrontAndBack()
        {
            var v0 = new Vector3(0, 0, 0);
            var v1 = new Vector3(1, 0, 0);
            var v2 = new Vector3(0, 1, 0);

            Assert.IsTrue(MeshRaycaster.IntersectTriangle(
                new Ray(new Vector3(0.25f, 0.25f, -1f), Vector3.forward), v0, v1, v2, out float d, out _, out _));
            Assert.AreEqual(1f, d, 1e-4f);

            // 裏面からも当たる（両面判定）
            Assert.IsTrue(MeshRaycaster.IntersectTriangle(
                new Ray(new Vector3(0.25f, 0.25f, 1f), Vector3.back), v0, v1, v2, out _, out _, out _));

            // 三角形の外は外れる
            Assert.IsFalse(MeshRaycaster.IntersectTriangle(
                new Ray(new Vector3(0.9f, 0.9f, -1f), Vector3.forward), v0, v1, v2, out _, out _, out _));
        }

        [Test]
        public void Raycast_Quad_ReturnsInterpolatedUv()
        {
            _go = new GameObject("quad");
            var mf = _go.AddComponent<MeshFilter>();
            _go.AddComponent<MeshRenderer>();

            var mesh = new Mesh();
            _cleanup.Add(mesh);
            mesh.vertices = new[]
            {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                new Vector3(0, 1, 0), new Vector3(1, 1, 0),
            };
            mesh.uv = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(0, 1), new Vector2(1, 1),
            };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;

            var renderer = _go.GetComponent<MeshRenderer>();
            var ray = new Ray(new Vector3(0.75f, 0.25f, -1f), Vector3.forward);

            Assert.IsTrue(MeshRaycaster.Raycast(ray, renderer, out var hit));
            Assert.AreEqual(0, hit.subMeshIndex);
            Assert.AreEqual(0.75f, hit.uv.x, 1e-3f);
            Assert.AreEqual(0.25f, hit.uv.y, 1e-3f);

            // Transform を動かしても追従する
            _go.transform.position = new Vector3(10, 0, 0);
            var ray2 = new Ray(new Vector3(10.25f, 0.5f, -2f), Vector3.forward);
            Assert.IsTrue(MeshRaycaster.Raycast(ray2, renderer, out var hit2));
            Assert.AreEqual(0.25f, hit2.uv.x, 1e-3f);
            Assert.AreEqual(0.5f, hit2.uv.y, 1e-3f);

            // 外れたら false
            Assert.IsFalse(MeshRaycaster.Raycast(new Ray(new Vector3(50, 50, -1), Vector3.forward), renderer, out _));
        }

        [Test]
        public void 顔の目元のような細かい三角形でも_当たった面の向きは長さ1でレイ側を向く()
        {
            // 2mm 四方の四角形。三角形 1 枚の外積の長さは 4e-6 で、Vector3.normalized はこれをゼロにしてしまう（長さ 1e-5 未満）
            _go = new GameObject("tiny");
            var mf = _go.AddComponent<MeshFilter>();
            var renderer = _go.AddComponent<MeshRenderer>();
            var mesh = new Mesh();
            _cleanup.Add(mesh);
            const float s = 0.002f;
            mesh.vertices = new[] { new Vector3(0, 0, 0), new Vector3(s, 0, 0), new Vector3(0, s, 0), new Vector3(s, s, 0) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
            var ray = new Ray(new Vector3(s * 0.75f, s * 0.25f, -1f), Vector3.forward);

            Assert.IsTrue(MeshRaycaster.Raycast(ray, renderer, out var hit));
            Assert.AreEqual(1f, hit.worldNormal.magnitude, 1e-4f, "1 つだけ拾う判定");
            Assert.Greater(Vector3.Dot(hit.worldNormal, -ray.direction), 0.999f);

            var hits = new System.Collections.Generic.List<MeshRaycaster.RaycastHit>();
            MeshRaycaster.RaycastMeshAll(ray, mesh, _go.transform.localToWorldMatrix, renderer, hits);
            Assert.AreEqual(1, hits.Count);
            Assert.AreEqual(1f, hits[0].worldNormal.magnitude, 1e-4f, "すべて拾う判定（クリック・ホバーで使う）");
            Assert.Greater(Vector3.Dot(hits[0].worldNormal, -ray.direction), 0.999f);
        }

        [Test]
        public void Raycast_二つ目の三角形なら_triangleIndex_1_と重心係数を返す()
        {
            _go = new GameObject("quad");
            var mf = _go.AddComponent<MeshFilter>();
            var renderer = _go.AddComponent<MeshRenderer>();

            var mesh = new Mesh();
            _cleanup.Add(mesh);
            mesh.vertices = new[]
            {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                new Vector3(0, 1, 0), new Vector3(1, 1, 0),
            };
            mesh.uv = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(0, 1), new Vector2(1, 1),
            };
            mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            mesh.RecalculateBounds();
            mf.sharedMesh = mesh;

            // 2 つ目の三角形 (1,2,3) = (1,0),(0,1),(1,1) は x + y > 1 の側。
            // 狙う点 (0.875, 0.625) は w0*(1,0) + w1*(0,1) + w2*(1,1) を解いて (w0,w1,w2) = (0.375, 0.125, 0.5)。
            // 3 成分がすべて違うので頂点順の取り違えを検出できる
            var ray = new Ray(new Vector3(0.875f, 0.625f, -1f), Vector3.forward);

            Assert.IsTrue(MeshRaycaster.Raycast(ray, renderer, out var hit));
            AssertSecondTriangle(hit, mesh, _go.transform.localToWorldMatrix);

            var hits = new System.Collections.Generic.List<MeshRaycaster.RaycastHit>();
            Assert.AreEqual(1, MeshRaycaster.RaycastMeshAll(ray, mesh, _go.transform.localToWorldMatrix, renderer, hits));
            AssertSecondTriangle(hits[0], mesh, _go.transform.localToWorldMatrix);
        }

        private static void AssertSecondTriangle(MeshRaycaster.RaycastHit hit, Mesh mesh, Matrix4x4 localToWorld)
        {
            Assert.AreEqual(1, hit.triangleIndex);
            Vector3 b = hit.barycentric;
            Assert.That(b.x + b.y + b.z, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(b.x, Is.GreaterThanOrEqualTo(0f));
            Assert.That(b.y, Is.GreaterThanOrEqualTo(0f));
            Assert.That(b.z, Is.GreaterThanOrEqualTo(0f));

            // 厳密値
            Assert.That(b.x, Is.EqualTo(0.375f).Within(1e-4f));
            Assert.That(b.y, Is.EqualTo(0.125f).Within(1e-4f));
            Assert.That(b.z, Is.EqualTo(0.5f).Within(1e-4f));

            // 再構成恒等式: w0*v0 + w1*v1 + w2*v2 == worldPosition
            var triangles = mesh.GetTriangles(hit.subMeshIndex);
            var vertices = mesh.vertices;
            int baseIndex = hit.triangleIndex * 3;
            Vector3 v0 = localToWorld.MultiplyPoint3x4(vertices[triangles[baseIndex]]);
            Vector3 v1 = localToWorld.MultiplyPoint3x4(vertices[triangles[baseIndex + 1]]);
            Vector3 v2 = localToWorld.MultiplyPoint3x4(vertices[triangles[baseIndex + 2]]);
            Vector3 reconstructed = v0 * b.x + v1 * b.y + v2 * b.z;
            Assert.That(Vector3.Distance(reconstructed, hit.worldPosition), Is.LessThan(1e-4f));
        }

        [Test]
        public void SkinnedMeshRenderer_のスケールがヒット位置に反映される()
        {
            // このテストが Unity で落ちたら BakeMesh の useScale の意味が想定と逆。
            // その場合は MeshRaycaster.TryGetMeshData の SMR 経路を
            // BakeMesh(mesh, true) ＋ TRS(position, rotation, one)（TCA の元の組み合わせ）または
            // BakeMesh(mesh, false) ＋ smr.transform.localToWorldMatrix に切り替えること
            _go = new GameObject("skinned");
            _go.transform.localScale = new Vector3(2f, 2f, 2f);
            var smr = _go.AddComponent<SkinnedMeshRenderer>();

            var mesh = new Mesh();
            _cleanup.Add(mesh);
            mesh.vertices = new[]
            {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            };
            mesh.uv = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(1, 1), new Vector2(0, 1),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            var weight = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            mesh.boneWeights = new[] { weight, weight, weight, weight };
            mesh.bindposes = new[] { Matrix4x4.identity };
            mesh.RecalculateBounds();

            smr.bones = new[] { _go.transform };
            smr.rootBone = _go.transform;
            smr.sharedMesh = mesh;

            Assert.IsTrue(MeshRaycaster.TryGetMeshData(smr, out var baked, out var localToWorld, out bool ownsMesh));
            try
            {
                // 描画上は 0..2 の四角なので world (1.5, 0.5) は UV (0.75, 0.25)
                var ray = new Ray(new Vector3(1.5f, 0.5f, -1f), Vector3.forward);
                var hits = new System.Collections.Generic.List<MeshRaycaster.RaycastHit>();

                Assert.That(MeshRaycaster.RaycastMeshAll(ray, baked, localToWorld, smr, hits), Is.GreaterThan(0));
                Assert.That(hits[0].uv.x, Is.EqualTo(0.75f).Within(1e-3f));
                Assert.That(hits[0].uv.y, Is.EqualTo(0.25f).Within(1e-3f));
            }
            finally
            {
                if (ownsMesh) Object.DestroyImmediate(baked);
            }
        }
    }
}
