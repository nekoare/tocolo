using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>「画像を入れる」の ON/OFF・画像の割り当て（DecalBox）のテスト。箱の既定は GradientBox.DefaultFor の bounds 経路で確かめる</summary>
    public class DecalBoxTests
    {
        private readonly List<Object> _cleanup = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            ToolSession.Clear();
            // 前のテストや利用者の操作と Undo グループを分ける
            Undo.IncrementCurrentGroup();
        }

        [TearDown]
        public void TearDown()
        {
            ToolSession.Clear();
            // Undo.RecordObject の記録を消してから破棄する（残すと Ctrl+Z で空のオブジェクトが復活する）
            foreach (var o in _cleanup)
            {
                if (o is GameObject go)
                {
                    foreach (var c in go.GetComponentsInChildren<Component>(true)) if (c != null) Undo.ClearUndo(c);
                    foreach (var t in go.GetComponentsInChildren<Transform>(true)) Undo.ClearUndo(t.gameObject);
                }
                else if (o != null) Undo.ClearUndo(o);
            }
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private T Track<T>(T obj) where T : Object
        {
            _cleanup.Add(obj);
            return obj;
        }

        /// <summary>対象ルートと、その子の立方体の MeshRenderer（bounds を決めるため。マテリアルなし＝選択範囲は取れず bounds の箱になる）を主種に持つ編集</summary>
        private (ClickRecolor component, RecolorEdit edit) MakeEdit()
        {
            var root = Track(new GameObject("Avatar"));
            var component = root.AddComponent<ClickRecolor>();
            var mesh = Track(new Mesh());
            var vertices = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                vertices[i] = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
            }
            mesh.vertices = vertices;
            mesh.triangles = new[] { 0, 1, 2, 1, 3, 2, 4, 6, 5, 5, 6, 7 };
            mesh.RecalculateBounds();
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 1f, 0f);
            body.transform.localScale = new Vector3(0.5f, 2f, 0.3f);
            body.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = body.AddComponent<MeshRenderer>();

            var edit = RecolorEdit.CreateNew();
            edit.seedRenderer = renderer;
            component.AddEdit(edit);
            return (component, edit);
        }

        [Test]
        public void SetEnabled_true_で_ON_かつ_confirmed_になり箱が既定に置かれる()
        {
            var (component, edit) = MakeEdit();
            var box = GradientBox.DefaultFor(component, edit);
            edit.decalBoxPosition = new Vector3(5f, 5f, 5f);

            DecalBox.SetEnabled(component, edit, true, DecalPlacement.Box);

            Assert.That(edit.decalEnabled, Is.True);
            Assert.That(edit.confirmed, Is.True, "ON にしただけで別の場所のクリックで捨てられないように");
            Assert.That(Vector3.Distance(edit.decalBoxPosition, box.position), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(edit.decalBoxSize, box.size), Is.LessThan(1e-4f));
            Assert.That(edit.decalBoxRotation, Is.EqualTo(box.rotation));
        }

        [Test]
        public void SetTexture_で_confirmed_と_HasDecal_になり_null_に戻しても_confirmed_は残る()
        {
            var (component, edit) = MakeEdit();
            edit.decalEnabled = true;
            var image = Track(new Texture2D(4, 4));

            DecalBox.SetTexture(component, edit, image);

            Assert.That(edit.confirmed, Is.True);
            Assert.That(edit.HasDecal, Is.True);

            DecalBox.SetTexture(component, edit, null);

            Assert.That(edit.HasDecal, Is.False);
            Assert.That(edit.confirmed, Is.True, "一度画像を入れた編集は自動で捨てない");
        }

        [Test]
        public void SetEnabled_false_でも画像は残る()
        {
            var (component, edit) = MakeEdit();
            DecalBox.SetEnabled(component, edit, true, DecalPlacement.Box);
            var image = Track(new Texture2D(4, 4));
            DecalBox.SetTexture(component, edit, image);

            DecalBox.SetEnabled(component, edit, false, DecalPlacement.Box);

            Assert.That(edit.decalEnabled, Is.False);
            Assert.That(edit.decalTexture, Is.SameAs(image), "OFF→ON で画像を入れ直さずに済むように");
        }

        [Test]
        public void OFFからONに戻しても箱は前の位置のまま()
        {
            var (component, edit) = MakeEdit();
            DecalBox.SetEnabled(component, edit, true, DecalPlacement.Box);
            edit.decalBoxPosition = new Vector3(5f, 5f, 5f);

            DecalBox.SetEnabled(component, edit, false, DecalPlacement.Box);
            DecalBox.SetEnabled(component, edit, true, DecalPlacement.Box);

            Assert.That(edit.decalEnabled, Is.True);
            Assert.That(edit.decalBoxPosition, Is.EqualTo(new Vector3(5f, 5f, 5f)));
        }

        [Test]
        public void 印の無い版でONにした編集もOFFからONで箱を置き直さない()
        {
            var (component, edit) = MakeEdit();
            edit.decalEnabled = true; // 印（decalInitialized）が無い版で ON にした編集
            edit.decalBoxPosition = new Vector3(5f, 5f, 5f);

            DecalBox.SetEnabled(component, edit, false, DecalPlacement.Box);
            DecalBox.SetEnabled(component, edit, true, DecalPlacement.Box);

            Assert.That(edit.decalBoxPosition, Is.EqualTo(new Vector3(5f, 5f, 5f)));
        }

        [Test]
        public void ResetSettings_で箱が既定に戻り画像と設定は残る()
        {
            var (component, edit) = MakeEdit();
            DecalBox.SetEnabled(component, edit, true, DecalPlacement.Box);
            var image = Track(new Texture2D(4, 4));
            DecalBox.SetTexture(component, edit, image);
            edit.decalKeepAspect = false;
            edit.decalBoxPosition = new Vector3(5f, 5f, 5f);
            var box = GradientBox.DefaultFor(component, edit);

            DecalBox.ResetSettings(component, edit);

            Assert.That(Vector3.Distance(edit.decalBoxPosition, box.position), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(edit.decalBoxSize, box.size), Is.LessThan(1e-4f));
            Assert.That(edit.decalEnabled, Is.True);
            Assert.That(edit.decalTexture, Is.SameAs(image));
            Assert.That(edit.decalKeepAspect, Is.False);
        }

        // ── 面の向きに合わせる（DecalBox.SurfaceNormal・SurfaceTriangles。シールの置き方・画像をつかんで動かすときに使う）──

        /// <summary>
        /// 中心 center・法線 normal の平面上の、一辺 size の正方形に並べた点（法線方向へ ±bump のでこぼこ付き。乱数は固定）
        /// </summary>
        private static List<Vector3> PlanePoints(Vector3 center, Vector3 normal, float size, float bump, int n = 20, int seed = 1)
        {
            normal.Normalize();
            var u = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var v = Vector3.Cross(normal, u);
            var random = new System.Random(seed);
            var points = new List<Vector3>();
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    float a = (i / (float)(n - 1) - 0.5f) * size;
                    float b = (j / (float)(n - 1) - 0.5f) * size;
                    float d = ((float)random.NextDouble() * 2f - 1f) * bump;
                    points.Add(center + u * a + v * b + normal * d);
                }
            }
            return points;
        }

        /// <summary>
        /// 中心 center・法線 normal の平面の、一辺 size の正方形を n×n に分けた面の小片（三角形の代わり）。
        /// 各小片の向きは tilt 度までの乱数で傾ける（でこぼこ。乱数は固定）
        /// </summary>
        private static List<SurfaceTriangle> PlaneTriangles(Vector3 center, Vector3 normal, float size, float tilt = 0f, int n = 20, int seed = 1)
        {
            normal.Normalize();
            var u = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var v = Vector3.Cross(normal, u);
            var random = new System.Random(seed);
            float area = (size / n) * (size / n);
            var triangles = new List<SurfaceTriangle>();
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    float a = ((i + 0.5f) / n - 0.5f) * size;
                    float b = ((j + 0.5f) / n - 0.5f) * size;
                    var tilted = Quaternion.AngleAxis(((float)random.NextDouble() * 2f - 1f) * tilt, u)
                        * Quaternion.AngleAxis(((float)random.NextDouble() * 2f - 1f) * tilt, v) * normal;
                    triangles.Add(new SurfaceTriangle(center + u * a + v * b, tilted * area));
                }
            }
            return triangles;
        }

        [Test]
        public void でこぼこのある面でも平均した向きは面の向きになる()
        {
            var normal = new Vector3(-0.4f, 0f, 1f).normalized;
            var triangles = PlaneTriangles(Vector3.zero, normal, 0.2f, tilt: 20f);

            var surface = DecalBox.SurfaceNormal(triangles, Vector3.zero, normal, 0.1f);

            Assert.That(Vector3.Angle(surface, normal), Is.LessThan(3f));
        }

        [Test]
        public void 軸から少しだけ傾いた面は軸にそろえ_大きく傾いた面はそのまま()
        {
            var slight = Quaternion.AngleAxis(8f, Vector3.up) * Vector3.forward;
            var steep = Quaternion.AngleAxis(30f, Vector3.up) * Vector3.forward;

            Assert.That(DecalBox.SnapToAxis(slight, DecalBox.SnapAngle), Is.EqualTo(Vector3.forward));
            Assert.That(DecalBox.SnapToAxis(steep, DecalBox.SnapAngle), Is.EqualTo(steep));
        }

        [Test]
        public void 右を向いた面では箱の正面が右を向き_画像の上はアバターの上()
        {
            var center = new Vector3(0.1f, 1f, 0f);
            var rotation = DecalBox.RotationFacing(DecalBox.SurfaceNormal(PlaneTriangles(center, Vector3.right, 0.2f, tilt: 5f), center, Vector3.right, 0.1f));

            Assert.That(Vector3.Angle(rotation * Vector3.forward, Vector3.right), Is.LessThan(0.01f), "軸にそろう");
            Assert.That(Vector3.Angle(rotation * Vector3.up, Vector3.up), Is.LessThan(0.01f));
        }

        [Test]
        public void クリックした面が裏向きなら箱の正面も裏を向く()
        {
            var center = new Vector3(0f, 1f, -0.1f);
            var surface = DecalBox.SurfaceNormal(PlaneTriangles(center, Vector3.back, 0.2f), center, Vector3.back, 0.1f);

            Assert.That(Vector3.Angle(surface, Vector3.back), Is.LessThan(0.01f), "背中に貼るときは後ろから投影する");
        }

        [Test]
        public void 大きく傾いた面には軸にそろえずに合わせる()
        {
            var normal = Quaternion.AngleAxis(-35f, Vector3.right) * Vector3.forward; // 上に 35° 傾いた面（肩の斜面など）

            var surface = DecalBox.SurfaceNormal(PlaneTriangles(Vector3.zero, normal, 0.2f, tilt: 3f), Vector3.zero, normal, 0.1f);

            Assert.That(Vector3.Angle(surface, normal), Is.LessThan(2f));
        }

        [Test]
        public void 離れた別のパーツも選んでいるときは_クリックしたパーツのまわりだけで向きを決める()
        {
            // 左のパーツは正面、右のパーツは右を向いている。左をクリックしたら正面
            var triangles = PlaneTriangles(new Vector3(-1f, 1f, 0f), Vector3.forward, 0.2f);
            triangles.AddRange(PlaneTriangles(new Vector3(1f, 1f, 0f), Vector3.right, 0.2f));

            var surface = DecalBox.SurfaceNormal(triangles, new Vector3(-1f, 1f, 0f), Vector3.forward, 0.1f);

            Assert.That(Vector3.Angle(surface, Vector3.forward), Is.LessThan(0.01f));
        }

        [Test]
        public void 裏を向いた面は数えない()
        {
            // 髪の裏側のように、同じ場所に逆向きの面が重なっている
            var triangles = PlaneTriangles(Vector3.zero, Vector3.forward, 0.2f);
            foreach (var t in PlaneTriangles(new Vector3(0f, 0f, -0.002f), Vector3.back, 0.2f)) triangles.Add(t);

            Assert.That(SurfaceTriangles.TryAverageNormal(triangles, Vector3.zero, Vector3.forward, 0.1f, out var normal), Is.True, "打ち消し合わない");
            Assert.That(Vector3.Angle(normal, Vector3.forward), Is.LessThan(0.01f));
        }

        [Test]
        public void とても細かい面だけでも平均した向きは長さ1()
        {
            // 面積の合計が 1e-7（Vector3.normalized がゼロを返す長さ 1e-5 より小さい）
            var tilted = new Vector3(0.5f, 0f, 1f).normalized;
            var triangles = new List<SurfaceTriangle>();
            for (int i = 0; i < 10; i++) triangles.Add(new SurfaceTriangle(new Vector3(i * 0.0001f, 0f, 0f), tilted * 1e-8f));

            Assert.That(SurfaceTriangles.TryAverageNormal(triangles, Vector3.zero, tilted, 0.01f, out var normal), Is.True);
            Assert.That(normal.magnitude, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(Vector3.Angle(normal, tilted), Is.LessThan(0.01f));
        }

        [Test]
        public void まわりに面が無いときはクリックした面の向きを軸にそろえて使う()
        {
            var slightlyTilted = new Vector3(0.1f, 0f, 1f).normalized;

            var surface = DecalBox.SurfaceNormal(new List<SurfaceTriangle>(), Vector3.zero, slightlyTilted, 0.1f);

            Assert.That(surface, Is.EqualTo(Vector3.forward));
        }

        [Test]
        public void 細い毛束が後ろへ曲がっていても_横ではなく面の向きに置き_幅に収める()
        {
            // 額の前（正面向き）から頭頂（上向き）へ後ろに曲がる、幅 3cm の毛束。点の広がりで向きを決めると、曲がりの前後の広がりが幅より大きくなり横を向いていた
            const int along = 40, across = 6;
            const float bend = 0.08f, width = 0.03f;
            var points = new List<Vector3>();
            var triangles = new List<SurfaceTriangle>();
            for (int i = 0; i < along; i++)
            {
                float theta = (i + 0.5f) / along * Mathf.PI * 0.5f;
                var center = new Vector3(0f, 1f + bend * Mathf.Sin(theta), 0.1f - bend * (1f - Mathf.Cos(theta)));
                var normal = new Vector3(0f, Mathf.Sin(theta), Mathf.Cos(theta));
                float area = (bend * Mathf.PI * 0.5f / along) * (width / across);
                for (int j = 0; j < across; j++)
                {
                    var p = center + Vector3.right * (((j + 0.5f) / across - 0.5f) * width);
                    points.Add(p);
                    triangles.Add(new SurfaceTriangle(p, normal * area));
                }
            }

            var (_, rotation, size) = DecalBox.StickerFor(points, triangles, new Vector3(0f, 1f, 0.1f), Vector3.forward, 1.5f);

            var facing = rotation * Vector3.forward;
            Assert.That(Mathf.Abs(facing.x), Is.LessThan(0.05f), "毛束の幅の方向（横）を向かない");
            Assert.That(facing.z, Is.GreaterThan(0.5f), "額の前の毛束なので前寄りを向く");
            Assert.That(size.x, Is.EqualTo(width * (across - 1) / across * DecalBox.StickerSelectionRatio).Within(1e-4f), "毛束の幅に収まる");
        }

        [Test]
        public void 上を向いた面では画像の上がアバターの後ろを向く()
        {
            var rotation = DecalBox.RotationFacing(Vector3.up);

            Assert.That(Vector3.Angle(rotation * Vector3.forward, Vector3.up), Is.LessThan(0.01f));
            Assert.That(Vector3.Angle(rotation * Vector3.up, Vector3.back), Is.LessThan(0.01f), "正面から見て読める向き");
        }

        // ── シールとして置く（DecalBox.StickerFor / DefaultFor）──

        [Test]
        public void シールはクリックした点を中心に面の向きで置く()
        {
            var points = PlanePoints(new Vector3(0.3f, 1f, 0f), Vector3.right, 0.4f, 0.002f);
            var triangles = PlaneTriangles(new Vector3(0.3f, 1f, 0f), Vector3.right, 0.4f, tilt: 5f);

            var (position, rotation, _) = DecalBox.StickerFor(points, triangles, new Vector3(0.3f, 1f, 0.05f), Vector3.right, 1.5f);

            Assert.That(Vector3.Distance(position, new Vector3(0.3f, 1f, 0.05f)), Is.LessThan(1e-5f), "箱の中心は面の上（クリックした点）");
            Assert.That(Vector3.Angle(rotation * Vector3.forward, Vector3.right), Is.LessThan(0.01f));
            Assert.That(Vector3.Angle(rotation * Vector3.up, Vector3.up), Is.LessThan(0.01f), "画像の上はアバターの上");
        }

        [Test]
        public void シールの標準サイズは身長の10分の1で_細いパーツでは範囲の短い辺に収める()
        {
            var wide = PlanePoints(new Vector3(0f, 1f, 0.1f), Vector3.forward, 1f, 0f, n: 40);
            var wideTriangles = PlaneTriangles(new Vector3(0f, 1f, 0.1f), Vector3.forward, 1f, n: 40);
            var narrow = new List<Vector3>();
            var narrowTriangles = new List<SurfaceTriangle>();
            for (int i = 0; i < 10; i++)
            {
                for (int j = 0; j < 40; j++)
                {
                    var p = new Vector3((i / 9f - 0.5f) * 0.06f, 1f + (j / 39f - 0.5f) * 0.4f, 0.1f);
                    narrow.Add(p);
                    narrowTriangles.Add(new SurfaceTriangle(p, Vector3.forward * 0.0001f));
                }
            }

            var (_, _, wideSize) = DecalBox.StickerFor(wide, wideTriangles, new Vector3(0f, 1f, 0.1f), Vector3.forward, 1.5f);
            var (_, _, narrowSize) = DecalBox.StickerFor(narrow, narrowTriangles, new Vector3(0f, 1f, 0.1f), Vector3.forward, 1.5f);

            Assert.That(wideSize.x, Is.EqualTo(0.15f).Within(1e-5f), "シャツの胸のような広い面では身長の 1/10");
            Assert.That(wideSize, Is.EqualTo(new Vector3(wideSize.x, wideSize.x, wideSize.x)), "正方形で、奥行きも同じ");
            Assert.That(narrowSize.x, Is.EqualTo(0.06f * DecalBox.StickerSelectionRatio).Within(1e-4f), "ネクタイのような細い面では幅に収める");
        }

        [Test]
        public void 範囲の点も面も取れないときのシールはクリックした面の向きで身長の10分の1()
        {
            var (_, rotation, size) = DecalBox.StickerFor(new List<Vector3>(), new List<SurfaceTriangle>(), Vector3.zero, Vector3.back, 1.5f);

            Assert.That(size.x, Is.EqualTo(0.15f).Within(1e-5f));
            Assert.That(Vector3.Angle(rotation * Vector3.forward, Vector3.back), Is.LessThan(0.01f), "背中をクリックしたら背中に向ける");
        }

        [Test]
        public void シールの大きさを変えると縦横は同じ割合で_奥行きは長い辺にそろう()
        {
            // 箱モードで作った、横長で奥行きの浅い反転した箱
            var size = new Vector3(-0.2f, 0.1f, 0.02f);

            var half = DecalBox.ScaleSticker(size, 0.5f);
            var tiny = DecalBox.ScaleSticker(size, 0.0001f);

            Assert.That(half.x, Is.EqualTo(-0.1f).Within(1e-6f), "反転を保つ");
            Assert.That(half.y, Is.EqualTo(0.05f).Within(1e-6f));
            Assert.That(half.z, Is.EqualTo(0.1f).Within(1e-6f), "浅いまま縮めると面が奥行きから外れるので、長い辺にそろえる");
            Assert.That(Mathf.Abs(tiny.x), Is.EqualTo(DecalBox.StickerMinSize * 0.5f).Within(1e-6f), "つまみを掴める大きさは残す");
        }

        [Test]
        public void シールの枠の中を通るレイだけが枠に当たり_交点は枠の面の上()
        {
            // 正面向き（+Z）の枠。中心 (0, 1, 0.1)、横 0.04・縦 0.03 の半分
            var center = new Vector3(0f, 1f, 0.1f);
            var half = new Vector2(0.04f, 0.03f);
            var rotation = Quaternion.identity;

            Assert.That(DecalBox.TryHitStickerFrame(new Vector3(0.02f, 1.01f, 1f), Vector3.back, center, rotation, half, out var point), Is.True);
            Assert.That(Vector3.Distance(point, new Vector3(0.02f, 1.01f, 0.1f)), Is.LessThan(1e-5f));
            Assert.That(DecalBox.TryHitStickerFrame(new Vector3(0.05f, 1f, 1f), Vector3.back, center, rotation, half, out _), Is.False, "枠の外");
            Assert.That(DecalBox.TryHitStickerFrame(new Vector3(0f, 1f, -1f), Vector3.forward, center, rotation, half, out _), Is.True, "裏から見ても前面に描いているので当たる");
            Assert.That(DecalBox.TryHitStickerFrame(new Vector3(0f, 1f, 1f), Vector3.forward, center, rotation, half, out _), Is.False, "枠がレイの後ろ");
            Assert.That(DecalBox.TryHitStickerFrame(new Vector3(0f, 1f, 1f), Vector3.right, center, rotation, half, out _), Is.False, "枠の面と平行");
        }

        [Test]
        public void 初めてONにした編集は選んでいた置き方を覚え_OFFからONに戻しても変わらない()
        {
            var (component, sticker) = MakeEdit();
            DecalBox.SetEnabled(component, sticker, true, DecalPlacement.Sticker);
            DecalBox.SetEnabled(component, sticker, false, DecalPlacement.Box);
            DecalBox.SetEnabled(component, sticker, true, DecalPlacement.Box);
            var (component2, box) = MakeEdit();
            DecalBox.SetEnabled(component2, box, true, DecalPlacement.Box);

            Assert.That(DecalBox.PlacementOf(sticker), Is.EqualTo(DecalPlacement.Sticker), "あとで別の置き方を選んでも、作った編集は自分の置き方のまま");
            Assert.That(DecalBox.PlacementOf(box), Is.EqualTo(DecalPlacement.Box));
            Assert.That(DecalBox.PlacementOf(RecolorEdit.CreateNew()), Is.EqualTo(DecalPlacement.Box), "置き方の項目が無い版で作った編集は箱");
        }

        [Test]
        public void 箱からシールに整えると_見えている画像の大きさのまま奥行きは長い辺にそろう()
        {
            // 横 0.4・縦 0.2・奥行き 0.05 の反転した箱に、正方形の画像を比率を保って入れている（見えている画像は 0.2 四方）
            var size = new Vector3(-0.4f, 0.2f, 0.05f);
            var fit = new Vector2(2f, 1f);
            var start = DecalBox.FacingWithTwist(Vector3.forward, 20f);

            var notFound = DecalBox.StickerFromBox(new Vector3(0f, 1f, 0f), start, size, fit, false, Vector3.zero, Vector3.zero);
            var found = DecalBox.StickerFromBox(new Vector3(0f, 1f, 0f), start, size, fit, true, new Vector3(0.3f, 1f, 0.05f), Vector3.right);

            Assert.That(notFound.size.x, Is.EqualTo(-0.2f).Within(1e-6f), "反転は保つ");
            Assert.That(notFound.size.y, Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(notFound.size.z, Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(notFound.position, Is.EqualTo(new Vector3(0f, 1f, 0f)), "面が見つからなければ位置と向きはそのまま");
            Assert.That(Quaternion.Angle(notFound.rotation, start), Is.LessThan(0.01f), "向きもそのまま（正規化の丸めの差は見ない）");
            Assert.That(found.position, Is.EqualTo(new Vector3(0.3f, 1f, 0.05f)), "中心は面の上");
            Assert.That(Vector3.Angle(found.rotation * Vector3.forward, Vector3.right), Is.LessThan(0.01f), "面に向く");
            Assert.That(DecalBox.TwistOf(found.rotation), Is.EqualTo(20f).Within(0.01f), "画像の面内の傾きは保つ");
        }

        [Test]
        public void 箱からシールに切り替えた編集はシールの奥行きになり_箱に戻しても箱は変えない()
        {
            var (component, edit) = MakeEdit();
            DecalBox.SetEnabled(component, edit, true, DecalPlacement.Box);
            var boxSize = edit.decalBoxSize;

            DecalBox.SetPlacement(component, edit, DecalPlacement.Sticker);
            var stickerSize = edit.decalBoxSize;
            DecalBox.SetPlacement(component, edit, DecalPlacement.Box);

            Assert.That(stickerSize.x, Is.EqualTo(boxSize.x).Within(1e-6f), "画像を入れていない（引き伸ばし扱い）ので縦横はそのまま");
            Assert.That(stickerSize.z, Is.EqualTo(Mathf.Max(Mathf.Abs(boxSize.x), Mathf.Abs(boxSize.y))).Within(1e-6f), "奥行きは長い辺");
            Assert.That(edit.decalBoxSize, Is.EqualTo(stickerSize), "箱に戻すときは整えない");
            Assert.That(DecalBox.PlacementOf(edit), Is.EqualTo(DecalPlacement.Box));
        }

        [Test]
        public void シールでONにすると最初にクリックした三角形に面の向きで置かれる()
        {
            // 主種は立方体の三角形 0（後ろの面、+Z 向き）。選択範囲の点は取れない（マテリアルなし）ので、大きさは身長（立方体の高さ 2）の 1/10
            var (component, edit) = MakeEdit();

            DecalBox.SetEnabled(component, edit, true, DecalPlacement.Sticker);

            Assert.That(Vector3.Distance(edit.decalBoxPosition, new Vector3(-0.25f / 3f, 2f / 3f, -0.15f)), Is.LessThan(1e-4f));
            Assert.That(Vector3.Angle(edit.decalBoxRotation * Vector3.forward, Vector3.forward), Is.LessThan(0.01f));
            Assert.That(edit.decalBoxSize.x, Is.EqualTo(0.2f).Within(1e-4f));
        }
    }
}
