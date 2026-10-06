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

            DecalBox.SetEnabled(component, edit, true);

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
            DecalBox.SetEnabled(component, edit, true);
            var image = Track(new Texture2D(4, 4));
            DecalBox.SetTexture(component, edit, image);

            DecalBox.SetEnabled(component, edit, false);

            Assert.That(edit.decalEnabled, Is.False);
            Assert.That(edit.decalTexture, Is.SameAs(image), "OFF→ON で画像を入れ直さずに済むように");
        }

        [Test]
        public void OFFからONに戻しても箱は前の位置のまま()
        {
            var (component, edit) = MakeEdit();
            DecalBox.SetEnabled(component, edit, true);
            edit.decalBoxPosition = new Vector3(5f, 5f, 5f);

            DecalBox.SetEnabled(component, edit, false);
            DecalBox.SetEnabled(component, edit, true);

            Assert.That(edit.decalEnabled, Is.True);
            Assert.That(edit.decalBoxPosition, Is.EqualTo(new Vector3(5f, 5f, 5f)));
        }

        [Test]
        public void 印の無い版でONにした編集もOFFからONで箱を置き直さない()
        {
            var (component, edit) = MakeEdit();
            edit.decalEnabled = true; // 印（decalInitialized）が無い版で ON にした編集
            edit.decalBoxPosition = new Vector3(5f, 5f, 5f);

            DecalBox.SetEnabled(component, edit, false);
            DecalBox.SetEnabled(component, edit, true);

            Assert.That(edit.decalBoxPosition, Is.EqualTo(new Vector3(5f, 5f, 5f)));
        }

        [Test]
        public void ResetSettings_で箱が既定に戻り画像と詳細設定は残る()
        {
            var (component, edit) = MakeEdit();
            DecalBox.SetEnabled(component, edit, true);
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

        [Test]
        public void 向きを決められないときは箱を動かさない()
        {
            // 立方体にマテリアルが無いので選択範囲の点が取れない
            var (component, edit) = MakeEdit();
            DecalBox.SetEnabled(component, edit, true);
            var position = new Vector3(5f, 5f, 5f);
            var rotation = Quaternion.Euler(0f, 30f, 0f);
            edit.decalBoxPosition = position;
            edit.decalBoxRotation = rotation;

            Assert.That(DecalBox.AlignToSurface(component, edit), Is.False);
            Assert.That(edit.decalBoxPosition, Is.EqualTo(position), "正面向きに戻さない");
            Assert.That(edit.decalBoxRotation, Is.EqualTo(rotation));
        }

        // ── 箱の向きを面に合わせる（DecalBox.AlignToSurface の計算部分）──

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

        [Test]
        public void でこぼこのある面でも平面を当てはめた法線は面の向きになる()
        {
            var normal = new Vector3(-0.3f, 0f, 1f).normalized;
            var points = PlanePoints(Vector3.zero, normal, 0.2f, 0.005f);

            Assert.That(DecalBox.TryFitPlane(points, out var fitted), Is.True);
            Assert.That(Mathf.Abs(Vector3.Dot(fitted, normal)), Is.GreaterThan(0.99f));
        }

        [Test]
        public void 棒状に並んだ点は平面とみなさない()
        {
            var points = new List<Vector3>();
            for (int i = 0; i < 50; i++) points.Add(new Vector3(i * 0.01f, 0f, 0f));

            Assert.That(DecalBox.TryFitPlane(points, out _), Is.False);
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
            var points = PlanePoints(new Vector3(0.1f, 1f, 0f), Vector3.right, 0.2f, 0.003f);

            Assert.That(DecalBox.TryOrient(points, new Vector3(0.1f, 1f, 0f), Vector3.right, out var rotation, out _), Is.True);
            Assert.That(Vector3.Angle(rotation * Vector3.forward, Vector3.right), Is.LessThan(0.01f), "軸にそろう");
            Assert.That(Vector3.Angle(rotation * Vector3.up, Vector3.up), Is.LessThan(0.01f));
        }

        [Test]
        public void クリックした面が裏向きなら箱の正面も裏を向く()
        {
            var points = PlanePoints(new Vector3(0f, 1f, -0.1f), Vector3.back, 0.2f, 0.003f);

            Assert.That(DecalBox.TryOrient(points, new Vector3(0f, 1f, -0.1f), Vector3.back, out var rotation, out _), Is.True);
            Assert.That(Vector3.Angle(rotation * Vector3.forward, Vector3.back), Is.LessThan(0.01f), "背中に貼るときは後ろから投影する");
        }

        [Test]
        public void 大きく傾いた面には軸にそろえずに合わせる()
        {
            var normal = Quaternion.AngleAxis(-35f, Vector3.right) * Vector3.forward; // 上に 35° 傾いた面（肩の斜面など）
            var points = PlanePoints(Vector3.zero, normal, 0.2f, 0.002f);

            Assert.That(DecalBox.TryOrient(points, Vector3.zero, normal, out var rotation, out _), Is.True);
            Assert.That(Vector3.Angle(rotation * Vector3.forward, normal), Is.LessThan(2f));
        }

        [Test]
        public void 離れた別のパーツも選んでいるときは_クリックしたパーツのまわりだけで向きを決める()
        {
            // 左のパーツは正面、右のパーツは右を向いている。左をクリックしたら正面
            var points = PlanePoints(new Vector3(-1f, 1f, 0f), Vector3.forward, 0.2f, 0.003f, seed: 2);
            points.AddRange(PlanePoints(new Vector3(1f, 1f, 0f), Vector3.right, 0.2f, 0.003f, seed: 3));

            Assert.That(DecalBox.TryOrient(points, new Vector3(-1f, 1f, 0f), Vector3.forward, out var rotation, out _), Is.True);
            Assert.That(Vector3.Angle(rotation * Vector3.forward, Vector3.forward), Is.LessThan(0.01f));
        }

        [Test]
        public void 上を向いた面では画像の上がアバターの後ろを向く()
        {
            var rotation = DecalBox.RotationFacing(Vector3.up);

            Assert.That(Vector3.Angle(rotation * Vector3.forward, Vector3.up), Is.LessThan(0.01f));
            Assert.That(Vector3.Angle(rotation * Vector3.up, Vector3.back), Is.LessThan(0.01f), "正面から見て読める向き");
        }

        [Test]
        public void 面に乗せるときは面に沿った方向の位置を変えず奥行きだけ動かす()
        {
            // 体の中（x = 0）にある箱の中心を、右を向いた面（x = 0.175）に乗せる
            var placed = DecalBox.PlaceOnPlane(new Vector3(0f, 1.2f, 0.05f), Vector3.right, new Vector3(0.175f, 1f, 0f));

            Assert.That(Vector3.Distance(placed, new Vector3(0.175f, 1.2f, 0.05f)), Is.LessThan(1e-5f));
        }
    }
}
