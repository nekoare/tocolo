using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Decal;
using Nekoare.ClickRecolor.Editor.NDMF;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 重ね貼りの NDMF プレビュー（DecalOverlayPreview）のうち、ComputeContext を使わない組み立て部分（DecalOverlayAssembler。有料版のビルドと共用）のテスト:
    /// スロットの抽出・浮かせ量の換算・表示用メッシュと画像用の区画・複数編集の連鎖・OnFrame のマテリアル配列の冪等性
    /// </summary>
    public class DecalOverlayPreviewTests
    {
        private const float Eps = 1e-6f;

        private readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private T Track<T>(T obj) where T : Object
        {
            _cleanup.Add(obj);
            return obj;
        }

        /// <summary>1 辺 1 の +Z 向き四角形（XY 平面、中心が原点。法線 +Z）。DecalOverlayMeshTests と同じ作り</summary>
        private Mesh MakeQuad()
        {
            var mesh = Track(new Mesh());
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            return mesh;
        }

        private static RecolorEdit MakeDecalEdit()
        {
            var edit = RecolorEdit.CreateNew();
            edit.decalEnabled = true;
            edit.decalBoxPosition = Vector3.zero;
            edit.decalBoxRotation = Quaternion.identity;
            edit.decalBoxSize = new Vector3(1f, 1f, 2f);
            return edit;
        }

        /// <summary>ルート（原点）の子に、四角形を持つ MeshRenderer を置く。childScale は子の一様スケール</summary>
        private (Transform root, MeshRenderer renderer) MakeMeshRenderer(Mesh mesh, float childScale = 1f)
        {
            var root = Track(new GameObject("Avatar")).transform;
            var child = new GameObject("Quad");
            child.transform.SetParent(root, false);
            child.transform.localScale = Vector3.one * childScale;
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = child.AddComponent<MeshRenderer>();
            return (root, renderer);
        }

        private Mesh TrackIfNotNull(Mesh mesh)
        {
            if (mesh != null) Track(mesh);
            return mesh;
        }

        [Test]
        public void CollectOverlaySlots_対象テクスチャのスロットだけ集め最初のスロットを返す()
        {
            var texture = Track(new Texture2D(4, 4));
            var other = Track(new Texture2D(4, 4));
            var mesh = Track(new Mesh());
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.subMeshCount = 3;
            for (int i = 0; i < 3; i++) mesh.SetTriangles(new[] { 0, 1, 2 }, i);

            var a = Track(new Material(Shader.Find("Standard")));
            a.mainTexture = other;
            var b = Track(new Material(Shader.Find("Standard")));
            b.mainTexture = texture;

            var slots = DecalOverlayAssembler.CollectOverlaySlots(new[] { a, b, b }, mesh, texture, out int firstSlot);

            Assert.That(slots, Is.EqualTo(new[] { 1, 2 }));
            Assert.That(firstSlot, Is.EqualTo(1));
        }

        [Test]
        public void OffsetInLocal_ルートのローカル単位の0_1mmをメッシュのローカル単位に換算する()
        {
            Assert.That(DecalOverlayAssembler.OffsetInLocal(Matrix4x4.identity), Is.EqualTo(DecalOverlayAssembler.OverlayOffset).Within(Eps));
            // メッシュのローカルがルートの 4 倍なら、ローカル単位では 1/4
            var scaled = Matrix4x4.TRS(new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 20f, 30f), Vector3.one * 4f);
            Assert.That(DecalOverlayAssembler.OffsetInLocal(scaled), Is.EqualTo(DecalOverlayAssembler.OverlayOffset / 4f).Within(Eps));
        }

        [Test]
        public void PadJudgeVertices_足りない分を0で延長し_多すぎればnull()
        {
            var judge = new[] { Vector3.one, Vector3.up };
            Assert.That(DecalOverlayAssembler.PadJudgeVertices(judge, 2), Is.SameAs(judge));
            var padded = DecalOverlayAssembler.PadJudgeVertices(judge, 4);
            Assert.That(padded.Length, Is.EqualTo(4));
            Assert.That(padded[0], Is.EqualTo(Vector3.one));
            Assert.That(padded[3], Is.EqualTo(Vector3.zero));
            Assert.That(DecalOverlayAssembler.PadJudgeVertices(judge, 1), Is.Null);
        }

        [Test]
        public void BuildRendererOverlay_MeshRenderer_サブメッシュが1つ増え_区画は表示用メッシュの末尾サブメッシュ()
        {
            var quad = MakeQuad();
            var (root, renderer) = MakeMeshRenderer(quad);
            var geometry = DecalOverlayAssembler.RendererGeometry.Create(renderer);
            Assert.That(geometry, Is.Not.Null);
            try
            {
                bool ok = DecalOverlayAssembler.BuildRendererOverlay(root, geometry, quad, MakeDecalEdit(), new[] { 0 },
                    new Vector2(2f, 2f), new Vector2(0.5f, 0f), 1, out var display, out var part, out var partMesh);
                TrackIfNotNull(display);

                Assert.That(ok, Is.True);
                Assert.That(display, Is.Not.Null);
                Assert.That(display, Is.Not.SameAs(quad), "元メッシュは書き換えない");
                Assert.That(display.subMeshCount, Is.EqualTo(quad.subMeshCount + 1));
                Assert.That(partMesh, Is.Null, "MeshRenderer は表示用メッシュをそのまま画像に使う");
                Assert.That(part.mesh, Is.SameAs(display));
                Assert.That(part.submesh, Is.EqualTo(display.subMeshCount - 1));
                Assert.That(part.uvScale, Is.EqualTo(new Vector2(2f, 2f)));
                Assert.That(part.uvOffset, Is.EqualTo(new Vector2(0.5f, 0f)));
                Assert.That(part.localToWorld, Is.EqualTo(renderer.transform.localToWorldMatrix));
            }
            finally
            {
                geometry.Dispose();
            }
        }

        [Test]
        public void BuildRendererOverlay_浮かせ量はRendererのスケールで割ったローカル単位()
        {
            var quad = MakeQuad();
            // 子のスケール 2 → ルートの 0.1mm はローカルでは 0.05mm
            var (root, renderer) = MakeMeshRenderer(quad, 2f);
            var geometry = DecalOverlayAssembler.RendererGeometry.Create(renderer);
            try
            {
                bool ok = DecalOverlayAssembler.BuildRendererOverlay(root, geometry, quad, MakeDecalEdit(), new[] { 0 },
                    Vector2.one, Vector2.zero, 1, out var display, out _, out _);
                TrackIfNotNull(display);

                Assert.That(ok, Is.True);
                var vertices = display.vertices;
                Assert.That(vertices.Length, Is.EqualTo(8));
                for (int i = 4; i < 8; i++)
                {
                    Assert.That(vertices[i].z, Is.EqualTo(DecalOverlayAssembler.OverlayOffset / 2f).Within(Eps), "複製頂点は法線 +Z 方向に浮く");
                }
            }
            finally
            {
                geometry.Dispose();
            }
        }

        [Test]
        public void BuildRendererOverlay_2段目は前段の表示用メッシュに足し_サブメッシュが2つ増える()
        {
            var quad = MakeQuad();
            var (root, renderer) = MakeMeshRenderer(quad);
            var geometry = DecalOverlayAssembler.RendererGeometry.Create(renderer);
            try
            {
                Assert.That(DecalOverlayAssembler.BuildRendererOverlay(root, geometry, quad, MakeDecalEdit(), new[] { 0 },
                    Vector2.one, Vector2.zero, 1, out var first, out _, out _), Is.True);
                TrackIfNotNull(first);
                Assert.That(DecalOverlayAssembler.BuildRendererOverlay(root, geometry, first, MakeDecalEdit(), new[] { 0 },
                    Vector2.one, Vector2.zero, 2, out var second, out var part, out _), Is.True);
                TrackIfNotNull(second);

                Assert.That(second.subMeshCount, Is.EqualTo(quad.subMeshCount + 2));
                Assert.That(second.vertexCount, Is.EqualTo(12));
                Assert.That(part.submesh, Is.EqualTo(second.subMeshCount - 1));
                // 1 段目の重ね貼りサブメッシュはそのまま残る
                Assert.That(second.GetTriangles(1), Is.EqualTo(first.GetTriangles(1)));
            }
            finally
            {
                geometry.Dispose();
            }
        }

        [Test]
        public void BuildRendererOverlay_余分なマテリアルがあれば最後のサブメッシュを複製し_2段目は複製しない()
        {
            var quad = MakeQuad();
            var (root, renderer) = MakeMeshRenderer(quad);
            var geometry = DecalOverlayAssembler.RendererGeometry.Create(renderer);
            try
            {
                // サブメッシュ 1・マテリアル 2（余分な 1 枚が [0] を重ね描きする構成）
                Assert.That(DecalOverlayAssembler.BuildRendererOverlay(root, geometry, quad, MakeDecalEdit(), new[] { 0 },
                    Vector2.one, Vector2.zero, 2, out var first, out var firstPart, out _), Is.True);
                TrackIfNotNull(first);
                Assert.That(first.subMeshCount, Is.EqualTo(3));
                Assert.That(first.GetTriangles(1), Is.EqualTo(quad.GetTriangles(0)), "余分なマテリアルは複製した [0] を描く");
                Assert.That(firstPart.submesh, Is.EqualTo(2));

                // 2 段目: マテリアルは 元 2 + 重ね貼り 1 = 3、サブメッシュも 3 なので複製は起きず末尾に 1 つ足すだけ
                Assert.That(DecalOverlayAssembler.BuildRendererOverlay(root, geometry, first, MakeDecalEdit(), new[] { 0 },
                    Vector2.one, Vector2.zero, 3, out var second, out var secondPart, out _), Is.True);
                TrackIfNotNull(second);
                Assert.That(second.subMeshCount, Is.EqualTo(4));
                Assert.That(second.GetTriangles(2), Is.EqualTo(first.GetTriangles(2)));
                Assert.That(secondPart.submesh, Is.EqualTo(3));
            }
            finally
            {
                geometry.Dispose();
            }
        }

        [Test]
        public void BuildRendererOverlay_箱にかからなければfalse()
        {
            var quad = MakeQuad();
            var (root, renderer) = MakeMeshRenderer(quad);
            var geometry = DecalOverlayAssembler.RendererGeometry.Create(renderer);
            try
            {
                var edit = MakeDecalEdit();
                edit.decalBoxPosition = new Vector3(10f, 0f, 0f);
                bool ok = DecalOverlayAssembler.BuildRendererOverlay(root, geometry, quad, edit, new[] { 0 },
                    Vector2.one, Vector2.zero, 1, out var display, out _, out var partMesh);
                TrackIfNotNull(display);
                TrackIfNotNull(partMesh);

                Assert.That(ok, Is.False);
                Assert.That(display, Is.Null);
                Assert.That(partMesh, Is.Null);
            }
            finally
            {
                geometry.Dispose();
            }
        }

        [Test]
        public void BuildRendererOverlay_ドラッグ中は全三角形を複製し_今の箱で描く三角形とUVを選び直す()
        {
            var quad = MakeQuad();
            var (root, renderer) = MakeMeshRenderer(quad);
            var geometry = DecalOverlayAssembler.RendererGeometry.Create(renderer);
            try
            {
                var edit = MakeDecalEdit();
                // 箱は左下の隅だけ（左下の三角形 0,1,2 にだけかかる）
                edit.decalBoxPosition = new Vector3(-0.4f, -0.4f, 0f);
                edit.decalBoxSize = new Vector3(0.1f, 0.1f, 2f);
                bool ok = DecalOverlayAssembler.BuildRendererOverlay(root, geometry, quad, edit, new[] { 0 },
                    Vector2.one, Vector2.zero, 1, out var display, out _, out var partMesh, true, out var segment);
                TrackIfNotNull(display);
                TrackIfNotNull(partMesh);

                Assert.That(ok, Is.True);
                Assert.That(partMesh, Is.Null, "ドラッグ中の画像は画像全体で作るので区画は作らない");
                Assert.That(segment, Is.Not.Null);
                Assert.That(segment.start, Is.EqualTo(quad.vertexCount));
                Assert.That(segment.sources.Length, Is.EqualTo(4), "向き・箱に関係なく対象の三角形をすべて複製する");
                Assert.That(display.GetTriangles(segment.submesh).Length / 3, Is.EqualTo(1), "作った直後は今の箱にかかる三角形だけを描く");

                var uvs = new List<Vector2>();
                var triangles = new List<int>();
                display.GetUVs(0, uvs);

                // 右上の隅へ動かす → 右上の三角形（2,1,3）だけ
                edit.decalBoxPosition = new Vector3(0.4f, 0.4f, 0f);
                Assert.That(DecalOverlayAssembler.UpdateSegment(segment, uvs, triangles), Is.True);
                Assert.That(triangles.Count / 3, Is.EqualTo(1));
                int k3 = System.Array.IndexOf(segment.sources, 3);
                Assert.That(triangles, Does.Contain(segment.start + k3), "右上の三角形は頂点 3 を含む");

                // Y で 180 度回す → 四角形は箱の裏向きになり、何も描かない（ドラッグを始めたときの向きに縛られない）
                edit.decalBoxRotation = Quaternion.Euler(0f, 180f, 0f);
                DecalOverlayAssembler.UpdateSegment(segment, uvs, triangles);
                Assert.That(triangles.Count, Is.EqualTo(0));

                // 向きを戻して箱を動かす → 頂点 0（x = −0.5）の箱のローカル x は −0.5 − (−0.1) = −0.4、u = 0.5 − (−0.4) / 0.1 = 4.5
                edit.decalBoxRotation = Quaternion.identity;
                edit.decalBoxPosition = new Vector3(-0.1f, -0.4f, 0f);
                DecalOverlayAssembler.UpdateSegment(segment, uvs, triangles);
                int k0 = System.Array.IndexOf(segment.sources, 0);
                Assert.That(uvs[segment.start + k0].x, Is.EqualTo(4.5f).Within(1e-4f));
                Assert.That(uvs[0], Is.EqualTo(quad.uv[0]), "元の頂点の UV0 は書き換えない");
            }
            finally
            {
                geometry.Dispose();
            }
        }

        [Test]
        public void SelectionFilter_選択マスクに頂点か重心か辺の中点がかかる三角形だけを選ぶ()
        {
            // 4×4 のマスクで左下の 1 画素（u, v ∈ [0, 0.25)）だけ選択
            var mask = new byte[16];
            mask[0] = 255;
            var uv = new[]
            {
                new Vector2(0.05f, 0.05f), new Vector2(0.2f, 0.05f), new Vector2(0.05f, 0.2f), // 三角形 A: 選択の中
                new Vector2(0.6f, 0.6f), new Vector2(0.9f, 0.6f), new Vector2(0.6f, 0.9f),     // 三角形 B: 選択の外
                new Vector2(0.1f, 0.1f), new Vector2(0.9f, 0.9f), new Vector2(0.9f, 0.1f),     // 三角形 C: 頂点が 1 つだけ中
            };
            var include = DecalOverlayAssembler.SelectionFilter(mask, 4, 4, uv, Vector2.one, Vector2.zero);
            Assert.That(include, Is.Not.Null);
            Assert.That(include(0, 1, 2), Is.True);
            Assert.That(include(3, 4, 5), Is.False);
            Assert.That(include(6, 7, 8), Is.True, "頂点が選択の中ならかかる");

            // Tiling/Offset はマスクの座標に掛ける（Offset 0.5 で三角形 B が左下へ折り返して選択の中に入る）
            var shifted = DecalOverlayAssembler.SelectionFilter(mask, 4, 4, uv, Vector2.one, new Vector2(0.5f, 0.5f));
            Assert.That(shifted(3, 4, 5), Is.True);
            Assert.That(DecalOverlayAssembler.SelectionFilter(null, 4, 4, uv, Vector2.one, Vector2.zero), Is.Null, "マスクが無ければ絞らない");
        }

        [Test]
        public void BuildRendererOverlay_ドラッグ中でなければ書き換え情報は作らない()
        {
            var quad = MakeQuad();
            var (root, renderer) = MakeMeshRenderer(quad);
            var geometry = DecalOverlayAssembler.RendererGeometry.Create(renderer);
            try
            {
                bool ok = DecalOverlayAssembler.BuildRendererOverlay(root, geometry, quad, MakeDecalEdit(), new[] { 0 },
                    Vector2.one, Vector2.zero, 1, out var display, out _, out var partMesh, false, out var segment);
                TrackIfNotNull(display);
                TrackIfNotNull(partMesh);
                Assert.That(ok, Is.True);
                Assert.That(segment, Is.Null);
                Assert.That(partMesh, Is.Null, "MeshRenderer はふだん表示用メッシュで画像を描く");
            }
            finally
            {
                geometry.Dispose();
            }
        }

        [Test]
        public void BuildRendererOverlay_SkinnedMeshRenderer_画像用は焼いたメッシュから別に作る()
        {
            var quad = MakeQuad();
            var root = Track(new GameObject("Avatar")).transform;
            var child = new GameObject("Skinned");
            child.transform.SetParent(root, false);
            var renderer = child.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = quad;
            var geometry = DecalOverlayAssembler.RendererGeometry.Create(renderer);
            Assert.That(geometry, Is.Not.Null);
            try
            {
                bool ok = DecalOverlayAssembler.BuildRendererOverlay(root, geometry, quad, MakeDecalEdit(), new[] { 0 },
                    Vector2.one, Vector2.zero, 1, out var display, out var part, out var partMesh);
                TrackIfNotNull(display);
                TrackIfNotNull(partMesh);

                Assert.That(ok, Is.True);
                Assert.That(display.subMeshCount, Is.EqualTo(quad.subMeshCount + 1));
                Assert.That(partMesh, Is.Not.Null);
                Assert.That(part.mesh, Is.SameAs(partMesh));
                Assert.That(part.submesh, Is.EqualTo(partMesh.subMeshCount - 1));
                Assert.That(part.localToWorld, Is.EqualTo(geometry.judgeLocalToWorld));
            }
            finally
            {
                geometry.Dispose();
            }
        }

        [Test]
        public void AppendOverlayMaterials_末尾に足し_足してあれば何もしない()
        {
            var a = Track(new Material(Shader.Find("Standard")));
            var overlay = Track(new Material(Shader.Find("Standard")));
            var overlays = new[] { overlay };

            var first = DecalOverlayPreview.AppendOverlayMaterials(new[] { a }, 1, overlays);
            Assert.That(first, Is.EqualTo(new[] { a, overlay }));

            // 毎フレーム呼ばれても 2 回足さない
            Assert.That(DecalOverlayPreview.AppendOverlayMaterials(first, 1, overlays), Is.Null);
        }

        [Test]
        public void AppendOverlayMaterials_長さは合うが末尾が別物なら足さない()
        {
            var a = Track(new Material(Shader.Find("Standard")));
            var other = Track(new Material(Shader.Find("Standard")));
            var overlay = Track(new Material(Shader.Find("Standard")));

            // 上流が配列を 1 つ長くしていた（末尾は重ね貼りマテリアルではない）。重ね貼りサブメッシュと対応しないので足さない
            Assert.That(DecalOverlayPreview.AppendOverlayMaterials(new[] { a, other }, 1, new[] { overlay }), Is.Null);
        }

        [Test]
        public void AppendOverlayMaterials_上流が配列の長さを変えていたら足さない()
        {
            var a = Track(new Material(Shader.Find("Standard")));
            var overlay = Track(new Material(Shader.Find("Standard")));

            Assert.That(DecalOverlayPreview.AppendOverlayMaterials(new[] { a, a, a }, 1, new[] { overlay }), Is.Null);
            Assert.That(DecalOverlayPreview.AppendOverlayMaterials(new Material[0], 1, new[] { overlay }), Is.Null);
        }

        [Test]
        public void lilToonのマテリアルから作った重ね貼りマテリアルを末尾に足せる()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null || !DecalOverlayMaterial.IsApiAvailable) Assert.Ignore("lilToon が入っていない環境です");
            var original = Track(new Material(shader));
            var image = Track(new Texture2D(4, 4));
            var overlay = DecalOverlayMaterial.Create(original, image);
            Assert.That(overlay, Is.Not.Null);
            Track(overlay);

            var result = DecalOverlayPreview.AppendOverlayMaterials(new[] { original }, 1, new[] { overlay });

            Assert.That(result.Length, Is.EqualTo(2));
            Assert.That(result[1], Is.SameAs(overlay));
            Assert.That(result[1].mainTexture, Is.SameAs(image));
        }
    }
}
