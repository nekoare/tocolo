using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>グラデーション「毛先から」の頂点ごとの毛先からの距離と房の長さ（TipDistance）</summary>
    public class TipDistanceTests
    {
        private static readonly Vector3 Top = new Vector3(0f, 0.1f, 0f);

        /// <summary>
        /// x = centerX・z = z を中心に幅 0.02（x 方向）、y = 0 から真下へ length までの帯（segments 段の四角を 2 三角形ずつ）を positions / triangles に足す。
        /// 足した頂点の番号の範囲（最初・個数）を返す。頂点は段ごとに左・右の順
        /// </summary>
        private static (int first, int count) AddStrip(List<Vector3> positions, List<int> triangles, float centerX, float length, int segments, float z = 0f)
        {
            int first = positions.Count;
            for (int s = 0; s <= segments; s++)
            {
                float y = -length * s / segments;
                positions.Add(new Vector3(centerX - 0.01f, y, z));
                positions.Add(new Vector3(centerX + 0.01f, y, z));
            }
            for (int s = 0; s < segments; s++)
            {
                int a = first + s * 2;
                triangles.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 });
            }
            return (first, (segments + 1) * 2);
        }

        [Test]
        public void 垂れた房では下の端が毛先で上の端は房の長さだけ離れる()
        {
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            AddStrip(positions, triangles, 0f, 0.3f, 10);

            var result = TipDistance.Compute(positions, triangles, Top);

            int last = positions.Count - 1;
            Assert.That(result.distance[last], Is.EqualTo(0f).Within(1e-5f), "下の端の右");
            Assert.That(result.distance[last - 1], Is.EqualTo(0f).Within(1e-5f), "下の端の左");
            Assert.That(result.distance[0], Is.EqualTo(0.3f).Within(1e-4f), "上の端");
            Assert.That(result.distance[10], Is.EqualTo(0.15f).Within(1e-4f), "真ん中の段");
            foreach (var l in result.length) Assert.That(l, Is.EqualTo(0.3f).Within(1e-4f));
        }

        [Test]
        public void つながっていない房はそれぞれの毛先と長さを持つ()
        {
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            // てっぺんの前後に置く（左右対称にして、毛先の縁の両端がてっぺんから同じ距離になるように）
            var longStrip = AddStrip(positions, triangles, 0f, 0.3f, 10, z: -0.1f);
            var shortStrip = AddStrip(positions, triangles, 0f, 0.06f, 3, z: 0.1f);

            var result = TipDistance.Compute(positions, triangles, Top);

            int shortTip = shortStrip.first + shortStrip.count - 1;
            Assert.That(result.distance[shortTip], Is.EqualTo(0f).Within(1e-5f), "短い房の下の端も毛先");
            Assert.That(result.distance[shortStrip.first], Is.EqualTo(0.06f).Within(1e-4f));
            Assert.That(result.length[shortStrip.first], Is.EqualTo(0.06f).Within(1e-4f));
            Assert.That(result.length[longStrip.first], Is.EqualTo(0.3f).Within(1e-4f));
        }

        [Test]
        public void 同じ位置で分かれた頂点はつながった房として測る()
        {
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            // 上半分と下半分を別々の帯にする（境目の段の頂点が二重になる。UV の継ぎ目で分かれた状態）
            var upper = AddStrip(positions, triangles, 0f, 0.15f, 5);
            var lower = AddStrip(positions, triangles, 0f, 0.15f, 5);
            for (int i = lower.first; i < lower.first + lower.count; i++) positions[i] += new Vector3(0f, -0.15f, 0f);

            var result = TipDistance.Compute(positions, triangles, Top);

            Assert.That(result.distance[upper.first], Is.EqualTo(0.3f).Within(1e-4f), "上半分の上の端は下半分の毛先から測る");
            Assert.That(result.length[upper.first], Is.EqualTo(0.3f).Within(1e-4f));
        }

        [Test]
        public void てっぺんを包むお椀形の房では裾が毛先になる()
        {
            // てっぺんの真下を中心にした半球（下が開いた椀）。てっぺんは中心より上なので、裾がてっぺんから一番遠い
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            const int rings = 8, sectors = 16;
            const float radius = 0.1f;
            var center = new Vector3(0f, 0f, 0f);
            var top = new Vector3(0f, 0.15f, 0f);
            for (int r = 0; r <= rings; r++)
            {
                float polar = Mathf.PI * 0.5f * r / rings;
                for (int s = 0; s < sectors; s++)
                {
                    float az = Mathf.PI * 2f * s / sectors;
                    positions.Add(center + radius * new Vector3(Mathf.Sin(polar) * Mathf.Cos(az), Mathf.Cos(polar), Mathf.Sin(polar) * Mathf.Sin(az)));
                }
            }
            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < sectors; s++)
                {
                    int a = r * sectors + s, b = r * sectors + (s + 1) % sectors;
                    int c = a + sectors, d = b + sectors;
                    triangles.AddRange(new[] { a, c, b, b, c, d });
                }
            }

            var result = TipDistance.Compute(positions, triangles, top);

            int hem = rings * sectors;
            Assert.That(result.distance[hem], Is.EqualTo(0f).Within(1e-5f), "裾は毛先");
            Assert.That(result.distance[0], Is.GreaterThan(0.1f), "てっぺん側は毛先から離れる");
        }

        [Test]
        public void 三角形に使われていない頂点は0になる()
        {
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            AddStrip(positions, triangles, 0f, 0.3f, 4);
            positions.Add(new Vector3(5f, 5f, 5f));

            var result = TipDistance.Compute(positions, triangles, Top);

            Assert.That(result.distance[positions.Count - 1], Is.EqualTo(0f));
            Assert.That(result.length[positions.Count - 1], Is.EqualTo(0f));
        }
    }
}
