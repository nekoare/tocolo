using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// UV チャート検出（UV エッジ連結）のテスト。頂点番号ではなく UV 座標で連結を判定すること、
    /// 量子化で微小誤差を吸収すること、結果が決定的であることを確かめる
    /// </summary>
    public class UvChartDetectorTests
    {
        private readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
            UvChartDetector.ClearCache();
        }

        [Test]
        public void UVシームで切れた三角形は別チャート()
        {
            // 3D では同じ位置の 2 枚のクアッドだが、UV はシームで左右に分かれている（頂点は複製）
            var uv = new[]
            {
                // 左クアッド
                new Vector2(0.0f, 0.0f), new Vector2(0.4f, 0.0f), new Vector2(0.4f, 1.0f), new Vector2(0.0f, 1.0f),
                // 右クアッド（シームの頂点は複製され、UV が離れている）
                new Vector2(0.6f, 0.0f), new Vector2(1.0f, 0.0f), new Vector2(1.0f, 1.0f), new Vector2(0.6f, 1.0f),
            };
            var triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };

            var table = UvChartDetector.Build(uv, triangles);

            Assert.That(table.chartCount, Is.EqualTo(2));
            Assert.That(table.chartOfTriangle, Is.EqualTo(new[] { 0, 0, 1, 1 }));
        }

        [Test]
        public void 頂点番号が違ってもUV座標が同じ辺なら同じチャート()
        {
            // 三角形ごとに頂点を持つ（頂点共有なし）が、対角線の UV は一致している
            var uv = new[]
            {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1),
                new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, 1),
            };
            var triangles = new[] { 0, 1, 2, 3, 4, 5 };

            var table = UvChartDetector.Build(uv, triangles);

            Assert.That(table.chartCount, Is.EqualTo(1));
            Assert.That(table.chartOfTriangle, Is.EqualTo(new[] { 0, 0 }));
        }

        [Test]
        public void 頂点を共有していなくてUVも離れていれば別チャート()
        {
            // 3D では辺を共有する位置にあっても、UV が別の場所なら別チャート
            var uv = new[]
            {
                new Vector2(0.0f, 0.0f), new Vector2(0.3f, 0.0f), new Vector2(0.3f, 0.3f),
                new Vector2(0.5f, 0.5f), new Vector2(0.8f, 0.8f), new Vector2(0.5f, 0.8f),
            };
            var triangles = new[] { 0, 1, 2, 3, 4, 5 };

            var table = UvChartDetector.Build(uv, triangles);

            Assert.That(table.chartCount, Is.EqualTo(2));
            Assert.That(table.chartOfTriangle[0], Is.Not.EqualTo(table.chartOfTriangle[1]));
        }

        [Test]
        public void 頂点だけを共有する蝶ネクタイは別チャート()
        {
            // 三角形 0 と 1 は頂点 2 だけを共有し、UV の辺は共有しない
            var uv = new[]
            {
                new Vector2(0.0f, 0.0f), new Vector2(0.0f, 0.4f), new Vector2(0.5f, 0.2f),
                new Vector2(1.0f, 0.0f), new Vector2(1.0f, 0.4f),
            };
            var triangles = new[] { 0, 1, 2, 2, 3, 4 };

            var table = UvChartDetector.Build(uv, triangles);

            Assert.That(table.chartCount, Is.EqualTo(2));
            Assert.That(table.chartOfTriangle, Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void 量子化誤差1e6は同じ座標として扱う()
        {
            var uv = new[]
            {
                new Vector2(0.1f, 0.1f), new Vector2(0.7f, 0.1f), new Vector2(0.7f, 0.7f),
                // 共有辺の UV が 1e-6 だけずれている
                new Vector2(0.1f + 1e-6f, 0.1f), new Vector2(0.7f, 0.7f - 1e-6f), new Vector2(0.1f, 0.7f),
            };
            var triangles = new[] { 0, 1, 2, 3, 4, 5 };

            var table = UvChartDetector.Build(uv, triangles);

            Assert.That(table.chartCount, Is.EqualTo(1));
        }

        [Test]
        public void 二回呼んでも同じ結果で_チャート番号は三角形番号順に振られる()
        {
            // 三角形 0 と 2 が同じチャート、1 が別チャート
            var uv = new[]
            {
                new Vector2(0.0f, 0.0f), new Vector2(0.4f, 0.0f), new Vector2(0.4f, 0.4f), new Vector2(0.0f, 0.4f),
                new Vector2(0.6f, 0.6f), new Vector2(0.9f, 0.6f), new Vector2(0.9f, 0.9f),
            };
            var triangles = new[] { 0, 1, 2, 4, 5, 6, 0, 2, 3 };

            var first = UvChartDetector.Build(uv, triangles);
            var second = UvChartDetector.Build(uv, triangles);

            Assert.That(first.chartOfTriangle, Is.EqualTo(new[] { 0, 1, 0 }));
            Assert.That(second.chartOfTriangle, Is.EqualTo(first.chartOfTriangle));
            Assert.That(second.chartCount, Is.EqualTo(first.chartCount));
        }

        [Test]
        public void CollectTriangles_はチャートの三角形を番号順に返す()
        {
            var uv = new[]
            {
                new Vector2(0.0f, 0.0f), new Vector2(0.4f, 0.0f), new Vector2(0.4f, 0.4f), new Vector2(0.0f, 0.4f),
                new Vector2(0.6f, 0.6f), new Vector2(0.9f, 0.6f), new Vector2(0.9f, 0.9f),
            };
            var triangles = new[] { 0, 1, 2, 4, 5, 6, 0, 2, 3 };
            var table = UvChartDetector.Build(uv, triangles);
            var result = new List<int>();

            table.CollectTriangles(0, result);

            Assert.That(result, Is.EqualTo(new[] { 0, 2 }));
        }

        [Test]
        public void GetOrBuild_は同じメッシュならキャッシュを返し_形が変われば作り直す()
        {
            var mesh = new Mesh();
            _cleanup.Add(mesh);
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            mesh.triangles = new[] { 0, 1, 2 };

            var first = UvChartDetector.GetOrBuild(mesh, 0);
            var again = UvChartDetector.GetOrBuild(mesh, 0);

            Assert.That(first, Is.Not.Null);
            Assert.That(again, Is.SameAs(first));

            // 頂点数・三角形数が変わる（フィンガープリントが変わる）と作り直す
            mesh.Clear();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.one };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
            mesh.triangles = new[] { 0, 1, 2, 1, 3, 2 };

            var rebuilt = UvChartDetector.GetOrBuild(mesh, 0);

            Assert.That(rebuilt, Is.Not.SameAs(first));
            Assert.That(rebuilt.chartOfTriangle.Length, Is.EqualTo(2));
        }

        [Test]
        public void GetOrBuild_は範囲外のサブメッシュなら_null()
        {
            var mesh = new Mesh();
            _cleanup.Add(mesh);
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            mesh.triangles = new[] { 0, 1, 2 };

            Assert.That(UvChartDetector.GetOrBuild(mesh, 1), Is.Null);
        }
    }
}
