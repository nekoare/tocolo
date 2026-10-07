using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Utils;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>画像をつかんで表面に沿って動かす（DecalSurfaceDrag の計算部分と、面内の傾き・画像が覆う広さ）のテスト</summary>
    public class DecalSurfaceDragTests
    {
        /// <summary>中心 center・法線 normal の平面の、一辺 size の正方形を n×n に分けた面の小片（三角形の代わり）</summary>
        private static List<SurfaceTriangle> PlaneTriangles(Vector3 center, Vector3 normal, float size, int n = 20)
        {
            normal.Normalize();
            var u = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var v = Vector3.Cross(normal, u);
            float area = (size / n) * (size / n);
            var triangles = new List<SurfaceTriangle>();
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    float a = ((i + 0.5f) / n - 0.5f) * size;
                    float b = ((j + 0.5f) / n - 0.5f) * size;
                    triangles.Add(new SurfaceTriangle(center + u * a + v * b, normal * area));
                }
            }
            return triangles;
        }

        [Test]
        public void 掴んだ点は動かした先の面の点に来る()
        {
            var triangles = PlaneTriangles(Vector3.zero, Vector3.forward, 1f, n: 40);
            var anchor = new Vector3(0.2f, 0.1f, 0f);
            var grab = new Vector3(0.03f, -0.02f, 0.05f);

            DecalSurfaceDrag.Slide(triangles, 0.1f, anchor, Vector3.forward, 0f, grab, Quaternion.identity, true, out var position, out var rotation);

            Assert.That(Vector3.Distance(position + rotation * grab, anchor), Is.LessThan(1e-5f), "画像の掴んだ所がカーソルの下に来る");
            Assert.That(Vector3.Angle(rotation * Vector3.forward, Vector3.forward), Is.LessThan(0.01f));
        }

        [Test]
        public void 横を向いた面へ動かすと箱の正面がその面を向き_画像の傾きは保つ()
        {
            var triangles = PlaneTriangles(new Vector3(0.3f, 1f, 0f), Vector3.right, 0.4f);

            DecalSurfaceDrag.Slide(triangles, 0.1f, new Vector3(0.3f, 1f, 0f), Vector3.right, 30f, Vector3.zero, Quaternion.identity, true,
                out _, out var rotation);

            Assert.That(Vector3.Angle(rotation * Vector3.forward, Vector3.right), Is.LessThan(0.01f));
            Assert.That(DecalBox.TwistOf(rotation), Is.EqualTo(30f).Within(0.01f), "ギズモの輪で回した傾きが残る");
        }

        [Test]
        public void まわりに面が無い所ではカーソルの下の面の向きに合わせる()
        {
            var anchor = new Vector3(0.1f, 0f, 0f);
            var grab = new Vector3(0.01f, 0.02f, 0f);

            DecalSurfaceDrag.Slide(new List<SurfaceTriangle>(), 0.1f, anchor, Vector3.right, 0f, grab, Quaternion.identity, true, out var position, out var rotation);

            Assert.That(Vector3.Angle(rotation * Vector3.forward, Vector3.right), Is.LessThan(0.01f));
            Assert.That(Vector3.Distance(position + rotation * grab, anchor), Is.LessThan(1e-5f));
        }

        [Test]
        public void 面に合わせないときは横を向いた面へ動かしても向きを変えずに位置だけ動かす()
        {
            var triangles = PlaneTriangles(new Vector3(0.3f, 1f, 0f), Vector3.right, 0.4f);
            var previous = Quaternion.Euler(0f, 0f, 20f);
            var anchor = new Vector3(0.3f, 1f, 0f);
            var grab = new Vector3(0.01f, 0.02f, 0.03f);

            DecalSurfaceDrag.Slide(triangles, 0.1f, anchor, Vector3.right, 0f, grab, previous, false, out var position, out var rotation);

            Assert.That(rotation, Is.EqualTo(previous), "今までどおり箱の向きのまま平行に投影する");
            Assert.That(Vector3.Distance(position + rotation * grab, anchor), Is.LessThan(1e-5f), "掴んだ所は面についてくる");
        }

        [Test]
        public void 画像の端を掴んで動かしても_向きは画像の中心のまわりの面に合わせる()
        {
            // 右半分（x > 0）は上に 40° 傾いた面、左半分は正面向きの面（どちらも z = 0 に並べた面の小片）
            var tilted = Quaternion.AngleAxis(-40f, Vector3.right) * Vector3.forward;
            var triangles = new List<SurfaceTriangle>();
            for (int i = -40; i <= 40; i++)
            {
                for (int j = -20; j <= 20; j++)
                {
                    var p = new Vector3(i * 0.005f, j * 0.005f, 0f);
                    triangles.Add(new SurfaceTriangle(p, (p.x > 0f ? tilted : Vector3.forward) * 0.000025f));
                }
            }
            // 画像の右端（中心から +0.1）を、傾いた面の上（x = 0.08）で掴んでいる。中心は正面向きの面（x = -0.02）に来る
            var grab = new Vector3(0.1f, 0f, 0f);

            DecalSurfaceDrag.Slide(triangles, 0.03f, new Vector3(0.08f, 0f, 0f), tilted, 0f, grab, Quaternion.identity, true, out var position, out var rotation);

            Assert.That(Vector3.Angle(rotation * Vector3.forward, Vector3.forward), Is.LessThan(0.01f), "掴んだ所（傾いた面）ではなく中心の面の向き");
            Assert.That(Vector3.Distance(position + rotation * grab, new Vector3(0.08f, 0f, 0f)), Is.LessThan(1e-5f), "掴んだ所はカーソルの下のまま");
        }

        [Test]
        public void 小さい画像は掴んだ所の面の向きに_大きい画像は広く見た面の向きに合わせる()
        {
            // 正面を向いた広い面の真ん中だけ、横に 31° 傾いた小さな出っ張り（z = 0.6x）がある
            var bumpNormal = new Vector3(-0.6f, 0f, 1f).normalized;
            var triangles = new List<SurfaceTriangle>();
            for (int i = 0; i <= 100; i++)
            {
                for (int j = 0; j <= 100; j++)
                {
                    float x = (i - 50) * 0.01f, y = (j - 50) * 0.01f;
                    bool bump = Mathf.Abs(x) <= 0.06f && Mathf.Abs(y) <= 0.06f;
                    triangles.Add(new SurfaceTriangle(new Vector3(x, y, bump ? 0.6f * x : 0f), (bump ? bumpNormal : Vector3.forward) * 0.0001f));
                }
            }

            DecalSurfaceDrag.Slide(triangles, 0.05f, Vector3.zero, Vector3.forward, 0f, Vector3.zero, Quaternion.identity, true, out _, out var small);
            DecalSurfaceDrag.Slide(triangles, 0.5f, Vector3.zero, Vector3.forward, 0f, Vector3.zero, Quaternion.identity, true, out _, out var large);

            Assert.That(Vector3.Angle(small * Vector3.forward, Vector3.forward), Is.GreaterThan(20f), "出っ張りの向き");
            Assert.That(Vector3.Angle(large * Vector3.forward, Vector3.forward), Is.LessThan(0.01f), "広い面の向き（軸にそろう）");
        }

        [Test]
        public void 面内の傾きは正面の向きを変えても同じ値で取り出せる()
        {
            var normals = new[] { Vector3.forward, Vector3.right, Vector3.back, new Vector3(0.2f, 1f, 0.1f).normalized };
            foreach (var normal in normals)
            {
                foreach (float twist in new[] { -45f, 0f, 70f })
                {
                    var rotation = DecalBox.FacingWithTwist(normal, twist);
                    Assert.That(Vector3.Angle(rotation * Vector3.forward, normal), Is.LessThan(0.01f));
                    Assert.That(DecalBox.TwistOf(rotation), Is.EqualTo(twist).Within(0.01f), $"normal {normal}, twist {twist}");
                }
            }
        }

        [Test]
        public void 回転の輪はマウスの角度の差を足すので_1周回しても逆回転しない()
        {
            // 画面の角度は -180〜180 度で返るので、180 度をまたぐところで差をそのまま引くと逆回転になる
            float turned = 0f, last = 0f;
            foreach (float angle in new[] { 45f, 90f, 135f, 180f, -135f, -90f, -45f, 0f })
            {
                turned = StickerRing.Accumulate(turned, last, angle);
                last = angle;
            }

            Assert.That(turned, Is.EqualTo(360f).Within(1e-3f));
        }

        [Test]
        public void 回転の輪を少しずつ2周回しても_面の向きは掴んだときのまま()
        {
            // 輪はドラッグ中、毎回「掴んだときの向き」から回し直す（今の向きの面の向きを軸に取り直すと、誤差のずれが 60° を超えたあたりから増え続けて暴れた）
            var start = Quaternion.Normalize(Quaternion.Euler(20f, 35f, 10f));
            var normal = start * Vector3.forward;
            var rotation = start;
            for (int i = 1; i <= 1440; i++)
            {
                rotation = StickerRing.Turn(start, i * 0.5f);
                Assert.That(Vector3.Angle(rotation * Vector3.forward, normal), Is.LessThan(0.01f), $"{i * 0.5f}°");
            }
            Assert.That(Quaternion.Angle(rotation, start), Is.LessThan(0.05f), "2 周で元の向きに戻る");
        }

        [Test]
        public void 画面で時計回りに回すと_軸がカメラ側なら正_奥向きなら負の角度()
        {
            // Unity の回転は軸の正の側から見て時計回りが正（AngleAxis(90, up) で前が右に回る＝上から見て時計回り）
            Assert.That(Quaternion.AngleAxis(StickerRing.AxisAngle(90f, true), Vector3.up) * Vector3.forward, Is.EqualTo(Vector3.right).Using(Vector3EqualityComparer.Instance));
            Assert.That(StickerRing.AxisAngle(30f, false), Is.EqualTo(-30f));
        }

        [Test]
        public void 画像が選択範囲に少しでもかかっていれば動かせ_まったくかからなくなる所で止まる()
        {
            // 選択範囲は x = 0〜0.1 の帯。画像の横の半分は 0.03
            var points = new List<Vector3>();
            for (int i = 0; i <= 20; i++) points.Add(new Vector3(i * 0.005f, 0f, 0f));
            var half = new Vector2(0.03f, 0.03f);

            Assert.That(DecalSurfaceDrag.Overlaps(points, new Vector3(0.12f, 0f, 0f), Quaternion.identity, half, 0.03f), Is.True,
                "中心が範囲の外でも、画像の端が範囲にかかっていれば動かせる");
            Assert.That(DecalSurfaceDrag.Overlaps(points, new Vector3(0.14f, 0f, 0f), Quaternion.identity, half, 0.03f), Is.False,
                "画像が範囲に 1 点もかからなくなる手前で止まる");
            Assert.That(DecalSurfaceDrag.Overlaps(points, new Vector3(0.05f, 0f, 0.2f), Quaternion.identity, half, 0.03f), Is.False,
                "奥行きの外（前後に離れた別の層）は数えない");
        }

        [Test]
        public void 画像が覆う広さは比率を保って縮めた画像の長い辺の半分()
        {
            // 横 0.4・縦 0.2 の箱に正方形の画像を比率を保って入れると、画像は 0.2 四方（u が 2 倍に広がる）
            Assert.That(DecalBox.ImageFootprintRadius(new Vector3(0.4f, 0.2f, 1f), new Vector2(2f, 1f)), Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(DecalBox.ImageFootprintRadius(new Vector3(-0.4f, 0.2f, 1f), Vector2.one), Is.EqualTo(0.2f).Within(1e-6f), "反転した箱も同じ広さ");
            Assert.That(DecalBox.ImageFootprintRadius(new Vector3(0.001f, 0.001f, 1f), Vector2.one), Is.EqualTo(GradientBox.SelectionMinSize), "小さすぎる画像でも点を集められる広さは残す");
        }

        [Test]
        public void 選択マスクは下の行から並び_範囲の外や読めないマスクでは動かない()
        {
            // 4×2 のマスクの右上の画素だけ選ばれている
            var mask = new byte[8];
            mask[1 * 4 + 3] = 255;

            Assert.That(StickerSurface.IsSelected(mask, 4, 2, new Vector2(0.9f, 0.9f)), Is.True);
            Assert.That(StickerSurface.IsSelected(mask, 4, 2, new Vector2(0.9f, 0.1f)), Is.False);
            Assert.That(StickerSurface.IsSelected(mask, 4, 2, new Vector2(1.5f, 1.5f)), Is.True, "範囲の再クリックと同じく端に丸める");
            Assert.That(StickerSurface.IsSelected(null, 4, 2, new Vector2(0.9f, 0.9f)), Is.False);
        }
    }
}
