using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// UV 重なりの双子検出のテスト。同じ UV で 3D 位置が違う三角形があれば true、
    /// 位置まで同じ（両面の裏ポリゴン）なら false、UV が重ならなければ false、頂点順が違っても一致することを確かめる
    /// </summary>
    public class UvTwinDetectorTests
    {
        private static readonly Vector2[] TriangleUv =
        {
            new Vector2(0.1f, 0.1f), new Vector2(0.4f, 0.1f), new Vector2(0.1f, 0.4f),
        };

        private readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
            UvChartDetector.ClearCache();
            UvTwinDetector.ClearCache();
        }

        private Mesh CreateMesh(Vector3[] vertices, Vector2[] uv, int[] triangles)
        {
            var mesh = new Mesh { vertices = vertices, uv = uv, triangles = triangles };
            _cleanup.Add(mesh);
            return mesh;
        }

        /// <summary>三角形 0 のチャートについて判定する</summary>
        private static bool HasTwinOfFirstTriangle(Mesh mesh)
        {
            var table = UvChartDetector.GetOrBuild(mesh, 0);
            Assert.That(table, Is.Not.Null);
            return UvTwinDetector.HasTwin(mesh, 0, table.chartOfTriangle[0], table);
        }

        [Test]
        public void 同じUVで3D位置が違う三角形があればtrue()
        {
            // 左右の耳: UV は同じ、3D 位置は左右に離れている
            var vertices = new[]
            {
                new Vector3(-1f, 0f, 0f), new Vector3(-0.5f, 0f, 0f), new Vector3(-1f, 0.5f, 0f),
                new Vector3(1f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(1f, 0.5f, 0f),
            };
            var uv = new[] { TriangleUv[0], TriangleUv[1], TriangleUv[2], TriangleUv[0], TriangleUv[1], TriangleUv[2] };
            var mesh = CreateMesh(vertices, uv, new[] { 0, 1, 2, 3, 5, 4 });

            Assert.That(HasTwinOfFirstTriangle(mesh), Is.True);
        }

        [Test]
        public void 同じUVで3D位置も同じ裏ポリゴンならfalse()
        {
            // 両面メッシュ: 裏ポリゴンは頂点を複製し、巻き順を逆にしただけ（位置・UV とも同じ）
            var vertices = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0f, 0.5f, 0f),
                new Vector3(0f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(0f, 0.5f, 0f),
            };
            var uv = new[] { TriangleUv[0], TriangleUv[1], TriangleUv[2], TriangleUv[0], TriangleUv[1], TriangleUv[2] };
            var mesh = CreateMesh(vertices, uv, new[] { 0, 1, 2, 3, 5, 4 });

            Assert.That(HasTwinOfFirstTriangle(mesh), Is.False);
        }

        [Test]
        public void UVが重ならなければfalse()
        {
            var vertices = new[]
            {
                new Vector3(-1f, 0f, 0f), new Vector3(-0.5f, 0f, 0f), new Vector3(-1f, 0.5f, 0f),
                new Vector3(1f, 0f, 0f), new Vector3(0.5f, 0f, 0f), new Vector3(1f, 0.5f, 0f),
            };
            var uv = new[]
            {
                TriangleUv[0], TriangleUv[1], TriangleUv[2],
                new Vector2(0.6f, 0.6f), new Vector2(0.9f, 0.6f), new Vector2(0.6f, 0.9f),
            };
            var mesh = CreateMesh(vertices, uv, new[] { 0, 1, 2, 3, 4, 5 });

            Assert.That(HasTwinOfFirstTriangle(mesh), Is.False);
        }

        [Test]
        public void 頂点順が違っても同じUV三角形として一致する()
        {
            // 2 枚目は頂点の並びを回転させ（UV も同じく回転）、3D 位置は離す
            var vertices = new[]
            {
                new Vector3(-1f, 0f, 0f), new Vector3(-0.5f, 0f, 0f), new Vector3(-1f, 0.5f, 0f),
                new Vector3(0.5f, 0f, 0f), new Vector3(1f, 0.5f, 0f), new Vector3(1f, 0f, 0f),
            };
            var uv = new[] { TriangleUv[0], TriangleUv[1], TriangleUv[2], TriangleUv[1], TriangleUv[2], TriangleUv[0] };
            var mesh = CreateMesh(vertices, uv, new[] { 0, 1, 2, 3, 4, 5 });

            Assert.That(HasTwinOfFirstTriangle(mesh), Is.True);
        }
    }
}
