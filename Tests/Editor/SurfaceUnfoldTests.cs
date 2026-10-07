using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Decal;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>シールの面に沿った展開（SurfaceUnfold・UnfoldMapping）のテスト。メッシュは頂点の位置と三角形だけを直接作る</summary>
    public class SurfaceUnfoldTests
    {
        /// <summary>z = 0 の平面の格子（(n+1)×(n+1) 頂点、間隔 step、中心が原点）。三角形の表（Cross(b - a, c - a)）は +Z</summary>
        private static (List<Vector3> positions, List<int> triangles) Plane(int n, float step, float z = 0f, int offset = 0, bool flipWinding = false)
        {
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            for (int j = 0; j <= n; j++)
            {
                for (int i = 0; i <= n; i++) positions.Add(new Vector3((i - n * 0.5f) * step, (j - n * 0.5f) * step, z));
            }
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    int a = offset + j * (n + 1) + i, b = a + 1, c = a + (n + 1), d = c + 1;
                    if (flipWinding) triangles.AddRange(new[] { a, c, b, b, c, d });
                    else triangles.AddRange(new[] { a, b, c, b, d, c });
                }
            }
            return (positions, triangles);
        }

        /// <summary>
        /// y 軸まわりの半径 radius の筒（周を segments 分割、高さ方向を rows 分割・間隔 step、y の中心が 0）。角度 0 が +Z（正面）、+X 側へ回る。
        /// 周の継ぎ目では頂点を分けず 1 周つなぐ。三角形の表は外向き
        /// </summary>
        private static (List<Vector3> positions, List<int> triangles) Cylinder(float radius, int segments, int rows, float step)
        {
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            for (int j = 0; j <= rows; j++)
            {
                float y = (j - rows * 0.5f) * step;
                for (int i = 0; i < segments; i++)
                {
                    float theta = i * 2f * Mathf.PI / segments;
                    positions.Add(new Vector3(radius * Mathf.Sin(theta), y, radius * Mathf.Cos(theta)));
                }
            }
            for (int j = 0; j < rows; j++)
            {
                for (int i = 0; i < segments; i++)
                {
                    int a = j * segments + i, b = j * segments + (i + 1) % segments, c = a + segments, d = b + segments;
                    // 外向きになる巻き方（Cross(角度が増える向き, 上向き) が外を向く）
                    triangles.AddRange(new[] { a, b, c, b, d, c });
                }
            }
            return (positions, triangles);
        }

        private static Vector2[] Unfold(List<Vector3> positions, List<int> triangles, Vector3 seed, Quaternion frame, float reach)
        {
            var graph = SurfaceUnfoldGraph.Build(positions, triangles);
            var coords = new Vector2[positions.Count];
            Assert.That(SurfaceUnfold.TryUnfold(graph, seed, frame, reach, 0.001f, coords), Is.True, "種が面の上にある");
            return coords;
        }

        [Test]
        public void 平らな面では今までの平行投影と同じ座標になる()
        {
            var (positions, triangles) = Plane(20, 0.01f);
            var seed = new Vector3(0.003f, -0.002f, 0f);

            var coords = Unfold(positions, triangles, seed, Quaternion.identity, 0.08f);

            for (int v = 0; v < positions.Count; v++)
            {
                if (float.IsNaN(coords[v].x)) continue;
                Assert.That(Vector2.Distance(coords[v], new Vector2(positions[v].x - seed.x, positions[v].y - seed.y)), Is.LessThan(1e-4f), $"頂点 {v}");
            }
            Assert.That(float.IsNaN(coords[0].x), Is.True, "届く距離より遠い隅には座標を入れない");
        }

        [Test]
        public void 筒では面に沿って回り込み_真横を越えて裏まで届く()
        {
            // 半径 5cm の筒の正面に、右が +X・上が +Y のシール。面に沿った横の座標は弧の長さ（半径 × 角度）になる
            const float radius = 0.05f;
            const int segments = 72;
            var (positions, triangles) = Cylinder(radius, segments, 4, 0.01f);
            var seed = new Vector3(0f, 0f, radius);

            var coords = Unfold(positions, triangles, seed, Quaternion.identity, radius * Mathf.PI * 0.9f);

            // 高さ 0 の段の頂点（2 段目）で、角度 150°（真横の 90° を越えた裏側）
            int row = 2;
            int at150 = row * segments + segments * 150 / 360;
            Assert.That(float.IsNaN(coords[at150].x), Is.False, "平行投影では届かない裏側にも座標がある");
            Assert.That(coords[at150].x, Is.EqualTo(radius * 150f * Mathf.Deg2Rad).Within(radius * 0.05f), "横の座標は弧の長さ");
            Assert.That(coords[at150].y, Is.EqualTo(0f).Within(0.002f));
            int atMinus150 = row * segments + segments - segments * 150 / 360;
            Assert.That(coords[atMinus150].x, Is.EqualTo(-radius * 150f * Mathf.Deg2Rad).Within(radius * 0.05f), "反対側にも同じだけ回り込む");
        }

        [Test]
        public void 筒の裏で両側から回り込んだ所は描かない()
        {
            const float radius = 0.05f;
            const int segments = 72;
            var (positions, triangles) = Cylinder(radius, segments, 4, 0.01f);
            var seed = new Vector3(0f, 0f, radius);
            // 1 周より大きいシール（横 40cm）なので、裏（180°）で両側からの座標がぶつかる
            var coords = Unfold(positions, triangles, seed, Quaternion.identity, 0.25f);
            var mapping = new UnfoldMapping(coords, positions.ToArray(), new Vector3(0.4f, 0.1f, 0.4f), Vector2.one);

            int drawnAtBack = 0, drawnAtFront = 0;
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                if (!mapping.Draws(triangles[t], triangles[t + 1], triangles[t + 2])) continue;
                var center = (positions[triangles[t]] + positions[triangles[t + 1]] + positions[triangles[t + 2]]) / 3f;
                if (center.z < -radius * 0.99f && Mathf.Abs(center.x) < radius * 0.05f) drawnAtBack++;
                if (center.z > radius * 0.9f) drawnAtFront++;
            }
            Assert.That(drawnAtBack, Is.EqualTo(0), "座標が飛ぶ三角形に画像を詰め込まない");
            Assert.That(drawnAtFront, Is.GreaterThan(0));
        }

        [Test]
        public void 同じ位置に逆向きの面を重ねたメッシュでは_表から裏へ回らない()
        {
            // 表（+Z 向き）の板と、同じ位置に別の頂点で裏向きに重ねた板（髪の裏側の面のような作り）
            var (front, frontTriangles) = Plane(10, 0.01f);
            var (back, backTriangles) = Plane(10, 0.01f, offset: front.Count, flipWinding: true);
            var positions = new List<Vector3>(front);
            positions.AddRange(back);
            var triangles = new List<int>(frontTriangles);
            triangles.AddRange(backTriangles);

            var coords = Unfold(positions, triangles, Vector3.zero, Quaternion.identity, 0.2f);

            for (int v = 0; v < front.Count; v++) Assert.That(float.IsNaN(coords[v].x), Is.False, "表の板には届く");
            for (int v = front.Count; v < positions.Count; v++) Assert.That(float.IsNaN(coords[v].x), Is.True, "裏の板には回らない");
        }

        [Test]
        public void すき間の小さい別の毛束には橋を架けて_続きの座標で回り込む()
        {
            // 横に 3mm 空けて並べた 2 枚の板（別々の毛束）。左の板の真ん中に貼ると、右の板にも左から続く座標が入る
            var (left, leftTriangles) = Plane(10, 0.01f);
            var (right, rightTriangles) = Plane(10, 0.01f, offset: left.Count);
            for (int i = 0; i < right.Count; i++) right[i] += new Vector3(0.103f, 0f, 0f);
            var positions = new List<Vector3>(left);
            positions.AddRange(right);
            var triangles = new List<int>(leftTriangles);
            triangles.AddRange(rightTriangles);

            var coords = Unfold(positions, triangles, Vector3.zero, Quaternion.identity, 0.2f);

            for (int v = left.Count; v < positions.Count; v++)
            {
                Assert.That(float.IsNaN(coords[v].x), Is.False, $"右の板の頂点 {v} にも届く");
                Assert.That(Vector2.Distance(coords[v], new Vector2(positions[v].x, positions[v].y)), Is.LessThan(0.002f), "平らに並んでいれば平行投影と同じ座標");
            }
        }

        [Test]
        public void すき間の大きい別のパーツには届かない()
        {
            // 前後に 5cm 離れた板（髪と体のような、橋を架ける上限より大きなすき間）
            var (near, nearTriangles) = Plane(10, 0.01f);
            var (far, farTriangles) = Plane(10, 0.01f, z: -0.05f, offset: near.Count);
            var positions = new List<Vector3>(near);
            positions.AddRange(far);
            var triangles = new List<int>(nearTriangles);
            triangles.AddRange(farTriangles);

            var coords = Unfold(positions, triangles, Vector3.zero, Quaternion.identity, 0.2f);

            for (int v = near.Count; v < positions.Count; v++) Assert.That(float.IsNaN(coords[v].x), Is.True);
        }

        [Test]
        public void 画像の中心が宙にあっても_近くの面から展開して中心から測った座標になる()
        {
            // 板の 2cm 手前に中心がある（画像を範囲の外へずらしている途中）
            var (positions, triangles) = Plane(10, 0.01f);
            var graph = SurfaceUnfoldGraph.Build(positions, triangles);
            var coords = new Vector2[positions.Count];
            var seed = new Vector3(0.02f, 0f, 0.02f);

            Assert.That(SurfaceUnfold.TryUnfold(graph, seed, Quaternion.identity, 0.2f, 0.1f, coords), Is.True);

            for (int v = 0; v < positions.Count; v++)
            {
                Assert.That(Vector2.Distance(coords[v], new Vector2(positions[v].x - seed.x, positions[v].y - seed.y)), Is.LessThan(1e-4f), $"頂点 {v}");
            }
        }

        [Test]
        public void 展開の種は選択範囲の三角形から選ぶ()
        {
            // 左の板（選択範囲）と右の板（範囲の外の別パーツ）が 10cm 離れていて、中心は右の板の方に少し近い
            var (left, leftTriangles) = Plane(10, 0.01f);
            var (right, rightTriangles) = Plane(10, 0.01f, offset: left.Count);
            for (int i = 0; i < right.Count; i++) right[i] += new Vector3(0.2f, 0f, 0f);
            var positions = new List<Vector3>(left);
            positions.AddRange(right);
            var triangles = new List<int>(leftTriangles);
            triangles.AddRange(rightTriangles);
            var graph = SurfaceUnfoldGraph.Build(positions, triangles);
            var seed = new Vector3(0.11f, 0f, 0f);
            int leftCount = left.Count;
            System.Func<int, int, int, bool> selected = (a, b, c) => a < leftCount && b < leftCount && c < leftCount;

            var any = new Vector2[positions.Count];
            var fromSelection = new Vector2[positions.Count];
            SurfaceUnfold.TryUnfold(graph, seed, Quaternion.identity, 0.2f, 0.2f, any);
            SurfaceUnfold.TryUnfold(graph, seed, Quaternion.identity, 0.2f, 0.2f, fromSelection, selected);

            int center = 5 * 11 + 5; // 左の板の真ん中（原点）
            Assert.That(float.IsNaN(any[center].x), Is.True, "条件が無ければ近い方（範囲の外の右の板）から展開してしまう");
            Assert.That(float.IsNaN(fromSelection[center].x), Is.False, "範囲の板から展開する");
            Assert.That(Vector2.Distance(fromSelection[center], new Vector2(positions[center].x - seed.x, positions[center].y - seed.y)), Is.LessThan(1e-4f),
                "座標は箱の中心から測る");
        }

        [Test]
        public void 種がこの面の上に無ければ展開しない()
        {
            var (positions, triangles) = Plane(10, 0.01f);
            var graph = SurfaceUnfoldGraph.Build(positions, triangles);
            var coords = new Vector2[positions.Count];

            Assert.That(SurfaceUnfold.TryUnfold(graph, new Vector3(0f, 0f, 0.05f), Quaternion.identity, 0.1f, 0.001f, coords), Is.False,
                "別の Renderer に貼ったシールは、この Renderer には描かない");
            Assert.That(float.IsNaN(coords[0].x), Is.True);
        }

        [Test]
        public void 画像の傾きは座標の向きに入る()
        {
            // 種の向きを面内で 90° 回すと、右（x）が元の上（+Y）になる
            var (positions, triangles) = Plane(10, 0.01f);
            var frame = Quaternion.AngleAxis(90f, Vector3.forward);
            int v = 6 * 11 + 5; // x = 0, y = +0.01

            var coords = Unfold(positions, triangles, Vector3.zero, frame, 0.2f);

            var right = frame * Vector3.right;
            var up = frame * Vector3.up;
            Assert.That(coords[v].x, Is.EqualTo(Vector3.Dot(positions[v], right)).Within(1e-4f));
            Assert.That(coords[v].y, Is.EqualTo(Vector3.Dot(positions[v], up)).Within(1e-4f));
        }
    }
}
