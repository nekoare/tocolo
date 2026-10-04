using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Decal;
using Nekoare.ClickRecolor.Editor.Masks;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 「画像を入れる」第 2 段（重ね貼り）のメッシュ（DecalOverlayMesh）のテスト。GPU を使わない:
    /// 箱の +Z 側を向き箱にかかる三角形の頂点が末尾に複製され、投影 UV・元の UV・法線方向のずれ・ブレンドシェイプ・ボーンウェイトが付くこと
    /// </summary>
    public class DecalOverlayMeshTests
    {
        private const float Offset = 0.001f;
        private const float Eps = 1e-5f;
        private static readonly Vector3 BoxSize = new Vector3(1f, 1f, 2f);

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

        /// <summary>
        /// 1 辺 1 の四角形（XY 平面、中心が原点）。uv (0,0) が (−0.5, −0.5)、(1,1) が (0.5, 0.5)（DecalLayerBuilderTests と同じ作り）。
        /// faceForward なら三角形の向き（幾何法線）が +Z、そうでなければ −Z。法線は三角形の向きに合わせる。side で 1 辺の長さを変えられる
        /// </summary>
        private Mesh MakeQuad(bool faceForward, float side = 1f)
        {
            var mesh = Track(new Mesh());
            float h = side * 0.5f;
            mesh.vertices = new[]
            {
                new Vector3(-h, -h, 0f), new Vector3(h, -h, 0f),
                new Vector3(-h, h, 0f), new Vector3(h, h, 0f),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            mesh.triangles = faceForward ? new[] { 0, 1, 2, 2, 1, 3 } : new[] { 0, 2, 1, 2, 3, 1 };
            var n = faceForward ? Vector3.forward : Vector3.back;
            mesh.normals = new[] { n, n, n, n };
            return mesh;
        }

        private Mesh Build(Mesh source, Matrix4x4 meshToBox, bool flip = false, Vector3? boxSize = null)
        {
            var result = DecalOverlayMesh.Build(source, new[] { 0 }, meshToBox, flip, boxSize ?? BoxSize, Vector2.one, Offset);
            if (result != null) Track(result);
            return result;
        }

        [Test]
        public void FrontFacingQuad_DuplicatesSharedVerticesOnce_AndAddsSubmesh()
        {
            var source = MakeQuad(true);
            var mesh = Build(source, Matrix4x4.identity);

            Assert.That(mesh, Is.Not.Null);
            // 共有頂点は 1 回だけ複製する（4 + 4）
            Assert.That(mesh.vertexCount, Is.EqualTo(8));
            Assert.That(mesh.subMeshCount, Is.EqualTo(2));
            var added = mesh.GetTriangles(1);
            Assert.That(added.Length / 3, Is.EqualTo(2));
            foreach (var index in added) Assert.That(index, Is.GreaterThanOrEqualTo(4), "追加サブメッシュは複製した頂点だけを指す");
            // 元のサブメッシュはそのまま
            Assert.That(mesh.GetTriangles(0), Is.EqualTo(source.GetTriangles(0)));
        }

        [Test]
        public void FrontFacingQuad_ProjectedUv0_OriginalUvInUv1_OffsetAlongNormal()
        {
            var source = MakeQuad(true);
            var mesh = Build(source, Matrix4x4.identity);
            Assert.That(mesh, Is.Not.Null);

            var srcVertices = source.vertices;
            var srcUv = source.uv;
            var vertices = mesh.vertices;
            var uv0 = new List<Vector2>();
            var uv1 = new List<Vector2>();
            mesh.GetUVs(0, uv0);
            mesh.GetUVs(1, uv1);
            Assert.That(uv1.Count, Is.EqualTo(mesh.vertexCount));

            var added = new HashSet<int>(mesh.GetTriangles(1));
            Assert.That(added.Count, Is.EqualTo(4));
            foreach (var i in added)
            {
                // 複製元を位置（法線方向のずれを戻した XY）で探す
                int s = FindSource(srcVertices, vertices[i]);
                Assert.That(s, Is.GreaterThanOrEqualTo(0), $"複製頂点 {i} の元が見つからない");
                // 法線（+Z）方向に Offset ずれている
                Assert.That(vertices[i].z, Is.EqualTo(Offset).Within(Eps));
                // 投影 UV: u = 0.5 − x / size.x（左右反転）、v = y / size.y + 0.5
                float expectedU = srcVertices[s].x < 0f ? 1f : 0f;
                float expectedV = srcVertices[s].y < 0f ? 0f : 1f;
                Assert.That(uv0[i].x, Is.EqualTo(expectedU).Within(Eps), $"頂点 {i} の u");
                Assert.That(uv0[i].y, Is.EqualTo(expectedV).Within(Eps), $"頂点 {i} の v");
                // UV1 は元の UV0
                Assert.That(uv1[i].x, Is.EqualTo(srcUv[s].x).Within(Eps));
                Assert.That(uv1[i].y, Is.EqualTo(srcUv[s].y).Within(Eps));
            }
        }

        private static int FindSource(Vector3[] sourceVertices, Vector3 duplicated)
        {
            for (int i = 0; i < sourceVertices.Length; i++)
            {
                if (Mathf.Abs(sourceVertices[i].x - duplicated.x) < Eps && Mathf.Abs(sourceVertices[i].y - duplicated.y) < Eps) return i;
            }
            return -1;
        }

        [Test]
        public void BackFacingQuad_ReturnsNull()
        {
            Assert.That(Build(MakeQuad(false), Matrix4x4.identity), Is.Null);
        }

        [Test]
        public void Mirrored_BackFacingQuadBecomesFront()
        {
            Assert.That(Build(MakeQuad(false), Matrix4x4.identity, flip: true), Is.Not.Null);
            Assert.That(Build(MakeQuad(true), Matrix4x4.identity, flip: true), Is.Null);
        }

        [Test]
        public void BoxOverLeftHalf_IncludesTrianglesTouchingBox()
        {
            var source = MakeQuad(true);
            var meshToBox = BoxMaskBuilder.RootToBox(new Vector3(-0.25f, 0f, 0f), Quaternion.identity);
            var mesh = Build(source, meshToBox, boxSize: new Vector3(0.5f, 1f, 2f));

            Assert.That(mesh, Is.Not.Null);
            var added = mesh.GetTriangles(1);
            // 左下の三角形（0,1,2）は頂点 0・2 が箱の中なので含まれる。右上の三角形（2,1,3）も頂点 2 が箱にかかるので含まれる
            Assert.That(added.Length / 3, Is.EqualTo(2));
        }

        [Test]
        public void BoxOutsideQuad_ReturnsNull()
        {
            var meshToBox = BoxMaskBuilder.RootToBox(new Vector3(3f, 0f, 0f), Quaternion.identity);
            Assert.That(Build(MakeQuad(true), meshToBox), Is.Null);
        }

        [Test]
        public void AllTriangles_DuplicatesTrianglesOutsideBox_AndReportsSources()
        {
            // 画像の箱のドラッグ中: 箱の外の三角形も複製し、複製元の番号を返す（描く三角形と UV だけ毎フレーム書き換えて追従させるため）
            var meshToBox = BoxMaskBuilder.RootToBox(new Vector3(3f, 0f, 0f), Quaternion.identity);
            var sources = new List<int>();
            var mesh = DecalOverlayMesh.Build(MakeQuad(true), new[] { 0 }, meshToBox, false, BoxSize, Vector2.one, Offset,
                allTriangles: true, duplicatedSources: sources);
            Assert.That(mesh, Is.Not.Null, "箱の外でも複製する");
            Track(mesh);
            Assert.That(mesh.GetTriangles(1).Length / 3, Is.EqualTo(2));
            Assert.That(sources, Is.EquivalentTo(new[] { 0, 1, 2, 3 }), "共有頂点は 1 回だけ、複製の順に返す");
            Assert.That(mesh.vertexCount, Is.EqualTo(4 + sources.Count));
        }

        [Test]
        public void AllTriangles_DuplicatesBackFacingTrianglesToo()
        {
            // ドラッグ中に箱を回すと裏表が変わるので、向きでも選ばない（描くかどうかは毎フレーム判定し直す）
            var mesh = DecalOverlayMesh.Build(MakeQuad(false), new[] { 0 }, Matrix4x4.identity, false, BoxSize, Vector2.one, Offset,
                allTriangles: true);
            Assert.That(mesh, Is.Not.Null);
            Track(mesh);
            Assert.That(mesh.GetTriangles(1).Length / 3, Is.EqualTo(2));
        }

        [Test]
        public void Include_ExcludesTrianglesOutsideSelection_EvenWithAllTriangles()
        {
            // 選択範囲の外の三角形は重ねない（前後に重なる別の面が画像の同じ画素を引くのを防ぐ）。ドラッグ中（allTriangles）も同じ
            bool OnlyFirst(int a, int b, int c) => a == 0 && b == 1 && c == 2;
            var normal = DecalOverlayMesh.Build(MakeQuad(true), new[] { 0 }, Matrix4x4.identity, false, BoxSize, Vector2.one, Offset, include: OnlyFirst);
            Assert.That(normal, Is.Not.Null);
            Track(normal);
            Assert.That(normal.GetTriangles(1).Length / 3, Is.EqualTo(1));
            var sources = new List<int>();
            var dragging = DecalOverlayMesh.Build(MakeQuad(true), new[] { 0 }, Matrix4x4.identity, false, BoxSize, Vector2.one, Offset,
                allTriangles: true, duplicatedSources: sources, include: OnlyFirst);
            Track(dragging);
            Assert.That(dragging.GetTriangles(1).Length / 3, Is.EqualTo(1));
            Assert.That(sources, Is.EquivalentTo(new[] { 0, 1, 2 }), "選択外の三角形だけが使う頂点（3）は複製しない");
        }

        [Test]
        public void TrianglesLargerThanBox_AreDuplicatedWhenTheyIntersect()
        {
            // 1 辺 10 の四角形の中央に小さな箱: どの頂点も箱の外だが、両方の三角形（共有する対角線が中央を通る）と交わる
            var mesh = Build(MakeQuad(true, 10f), Matrix4x4.identity, boxSize: new Vector3(0.2f, 0.2f, 2f));
            Assert.That(mesh, Is.Not.Null);
            Assert.That(mesh.GetTriangles(1).Length / 3, Is.EqualTo(2));
        }

        [Test]
        public void LargeTrianglesNotIntersectingBox_AreNotDuplicated()
        {
            var meshToBox = BoxMaskBuilder.RootToBox(new Vector3(20f, 0f, 0f), Quaternion.identity);
            Assert.That(Build(MakeQuad(true, 10f), meshToBox, boxSize: new Vector3(0.2f, 0.2f, 2f)), Is.Null);
        }

        [Test]
        public void BoxBeyondHypotenuse_DuplicatesOnlyTheTriangleOnThatSide()
        {
            // 1 辺 10 の四角形の頂点 3（右上）側の隅に小さな箱。左下の三角形 (0,1,2) とは AABB・平面では離れず、斜辺 × 箱の軸の分離軸でだけ離れる
            var meshToBox = BoxMaskBuilder.RootToBox(new Vector3(3f, 3f, 0f), Quaternion.identity);
            var mesh = Build(MakeQuad(true, 10f), meshToBox, boxSize: new Vector3(0.2f, 0.2f, 2f));
            Assert.That(mesh, Is.Not.Null);
            Assert.That(mesh.GetTriangles(1).Length / 3, Is.EqualTo(1));
        }

        [Test]
        public void TriangleTouchesBox_SeparatedByTrianglePlane_IsFalse()
        {
            // 平面 x + y + z = 3.5 の三角形: AABB・辺の軸では箱 (±1) と離れないが、平面が箱の隅 (1,1,1)（和 3）に届かない
            Assert.That(DecalProjection.TriangleTouchesBox(
                new Vector3(3.5f, 0f, 0f), new Vector3(0f, 3.5f, 0f), new Vector3(0f, 0f, 3.5f), Vector3.one), Is.False);
        }

        [Test]
        public void TriangleTouchesBox_PlaneCuttingCorner_IsTrue()
        {
            Assert.That(DecalProjection.TriangleTouchesBox(
                new Vector3(2.9f, 0f, 0f), new Vector3(0f, 2.9f, 0f), new Vector3(0f, 0f, 2.9f), Vector3.one), Is.True);
        }

        [Test]
        public void TriangleTouchesBox_EdgeOnBoxFace_IsTrue()
        {
            // 辺が箱の面 x = 1 にちょうど接する（境界は交わる扱い）
            Assert.That(DecalProjection.TriangleTouchesBox(
                new Vector3(1f, -0.5f, 0f), new Vector3(3f, 0f, 0f), new Vector3(1f, 0.5f, 0f), Vector3.one), Is.True);
        }

        [Test]
        public void TriangleTouchesBox_FarAway_IsFalse()
        {
            Assert.That(DecalProjection.TriangleTouchesBox(
                new Vector3(5f, 5f, 0f), new Vector3(6f, 5f, 0f), new Vector3(5f, 6f, 0f), Vector3.one), Is.False);
        }

        [Test]
        public void UInt16MeshExceedingLimit_SwitchesToUInt32AndKeepsOriginalIndices()
        {
            // 260 × 250 = 65000 頂点の格子（UInt16）。左下 40 × 40 マスだけ三角形を張り、中央 30 × 30 の箱で約 1000 頂点を複製して 65535 を超えさせる
            const int W = 260, H = 250, Cells = 40;
            var source = Track(new Mesh());
            Assert.That(source.indexFormat, Is.EqualTo(UnityEngine.Rendering.IndexFormat.UInt16));
            var vertices = new Vector3[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++) vertices[y * W + x] = new Vector3(x, y, 0f);
            }
            var triangles = new List<int>();
            for (int y = 0; y < Cells; y++)
            {
                for (int x = 0; x < Cells; x++)
                {
                    int a = y * W + x, b = a + 1, c = a + W, d = c + 1;
                    triangles.AddRange(new[] { a, b, c, c, b, d });   // +Z 向き
                }
            }
            source.vertices = vertices;
            source.SetTriangles(triangles, 0);
            var originalTriangles = source.GetTriangles(0);

            var meshToBox = BoxMaskBuilder.RootToBox(new Vector3(20f, 20f, 0f), Quaternion.identity);
            var mesh = Build(source, meshToBox, boxSize: new Vector3(30f, 30f, 2f));

            Assert.That(mesh, Is.Not.Null);
            Assert.That(mesh.vertexCount, Is.GreaterThan(65535));
            Assert.That(mesh.indexFormat, Is.EqualTo(UnityEngine.Rendering.IndexFormat.UInt32));
            Assert.That(mesh.GetTriangles(0), Is.EqualTo(originalTriangles));
            int max = 0;
            foreach (var index in mesh.GetTriangles(1)) max = Mathf.Max(max, index);
            Assert.That(max, Is.GreaterThan(65535));
            Assert.That(source.indexFormat, Is.EqualTo(UnityEngine.Rendering.IndexFormat.UInt16), "元は変わらない");
        }

        [Test]
        public void BlendShape_DeltasOfEachFrameFollowTheirSourceVertices()
        {
            // 頂点ごとに違うデルタ（頂点 i は (i, 0, 0)、2 フレーム目は (i, 1, 0)）で、複製頂点が対応する元頂点のデルタを持つことを見る
            var source = MakeQuad(true);
            var frame50 = new Vector3[4];
            var frame100 = new Vector3[4];
            for (int i = 0; i < 4; i++)
            {
                frame50[i] = new Vector3(i, 0f, 0f);
                frame100[i] = new Vector3(i, 1f, 0f);
            }
            source.AddBlendShapeFrame("Shape", 50f, frame50, null, null);
            source.AddBlendShapeFrame("Shape", 100f, frame100, null, null);

            var mesh = Build(source, Matrix4x4.identity);
            Assert.That(mesh, Is.Not.Null);
            Assert.That(mesh.blendShapeCount, Is.EqualTo(1));
            Assert.That(mesh.GetBlendShapeName(0), Is.EqualTo("Shape"));
            Assert.That(mesh.GetBlendShapeFrameCount(0), Is.EqualTo(2));
            Assert.That(mesh.GetBlendShapeFrameWeight(0, 0), Is.EqualTo(50f));
            Assert.That(mesh.GetBlendShapeFrameWeight(0, 1), Is.EqualTo(100f));

            var srcVertices = source.vertices;
            var vertices = mesh.vertices;
            var expectedFrames = new[] { frame50, frame100 };
            for (int frame = 0; frame < 2; frame++)
            {
                var deltas = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(0, frame, deltas, new Vector3[mesh.vertexCount], new Vector3[mesh.vertexCount]);
                // 元の頂点はそのまま
                for (int i = 0; i < 4; i++) Assert.That(deltas[i], Is.EqualTo(expectedFrames[frame][i]), $"フレーム {frame} の元の頂点 {i}");
                foreach (var i in new HashSet<int>(mesh.GetTriangles(1)))
                {
                    int s = FindSource(srcVertices, vertices[i]);
                    Assert.That(s, Is.GreaterThanOrEqualTo(0));
                    Assert.That(deltas[i], Is.EqualTo(expectedFrames[frame][s]), $"フレーム {frame} の複製頂点 {i}（元 {s}）");
                }
            }
        }

        [Test]
        public void BoneWeights_AreCopiedToDuplicatedVertices()
        {
            var source = MakeQuad(true);
            var weights = new BoneWeight[4];
            for (int i = 0; i < weights.Length; i++) weights[i] = new BoneWeight { boneIndex0 = i % 2, weight0 = 1f };
            source.boneWeights = weights;
            source.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };

            var mesh = Build(source, Matrix4x4.identity);
            Assert.That(mesh, Is.Not.Null);
            Assert.That(mesh.bindposes.Length, Is.EqualTo(2));

            var srcVertices = source.vertices;
            var vertices = mesh.vertices;
            var result = mesh.boneWeights;
            Assert.That(result.Length, Is.EqualTo(mesh.vertexCount));
            foreach (var i in new HashSet<int>(mesh.GetTriangles(1)))
            {
                int s = FindSource(srcVertices, vertices[i]);
                Assert.That(s, Is.GreaterThanOrEqualTo(0));
                Assert.That(result[i].boneIndex0, Is.EqualTo(weights[s].boneIndex0), $"頂点 {i} のボーン");
                Assert.That(result[i].weight0, Is.EqualTo(1f).Within(Eps));
            }
            // 元の頂点のウェイトも保たれる
            for (int i = 0; i < 4; i++) Assert.That(result[i].boneIndex0, Is.EqualTo(weights[i].boneIndex0));
        }

        [Test]
        public void SourceMesh_IsNotModified()
        {
            var source = MakeQuad(true);
            Build(source, Matrix4x4.identity);
            Assert.That(source.vertexCount, Is.EqualTo(4));
            Assert.That(source.subMeshCount, Is.EqualTo(1));
        }

        [Test]
        public void JudgeVertices_AreUsedForFacingAndProjection()
        {
            // 判定用の頂点（今のポーズで焼いた位置を想定）を X 方向に 0.25 ずらすと、投影 UV もずれる（複製する位置は元のまま）
            var source = MakeQuad(true);
            var judge = source.vertices;
            for (int i = 0; i < judge.Length; i++) judge[i] += new Vector3(0.25f, 0f, 0f);
            var mesh = Track(DecalOverlayMesh.Build(source, judge, new[] { 0 }, Matrix4x4.identity, false, BoxSize, Vector2.one, Offset));

            Assert.That(mesh, Is.Not.Null);
            var srcVertices = source.vertices;
            var vertices = mesh.vertices;
            var uv0 = new List<Vector2>();
            mesh.GetUVs(0, uv0);
            foreach (var i in new HashSet<int>(mesh.GetTriangles(1)))
            {
                int s = FindSource(srcVertices, vertices[i]);
                Assert.That(s, Is.GreaterThanOrEqualTo(0), "複製する位置は元メッシュの頂点");
                float expectedU = 0.5f - judge[s].x / BoxSize.x;
                Assert.That(uv0[i].x, Is.EqualTo(expectedU).Within(Eps));
            }
        }

        [Test]
        public void MaterialCount_サブメッシュより多ければ最後のサブメッシュを複製してから重ね貼りを足す()
        {
            // サブメッシュ 2（[0] は箱の外向き、[1] が +Z 向き）・マテリアル 3（余分な 1 枚が [1] を重ね描きする構成）
            var front = MakeQuad(true);
            var source = Track(new Mesh());
            source.vertices = front.vertices;
            source.uv = front.uv;
            source.normals = front.normals;
            source.subMeshCount = 2;
            source.SetTriangles(new[] { 0, 2, 1 }, 0);
            source.SetTriangles(front.GetTriangles(0), 1);

            var mesh = Track(DecalOverlayMesh.Build(source, source.vertices, new[] { 1 }, Matrix4x4.identity, false,
                BoxSize, Vector2.one, Offset, materialCount: 3));

            Assert.That(mesh, Is.Not.Null);
            Assert.That(mesh.subMeshCount, Is.EqualTo(4));
            Assert.That(mesh.GetTriangles(0), Is.EqualTo(source.GetTriangles(0)));
            Assert.That(mesh.GetTriangles(1), Is.EqualTo(source.GetTriangles(1)));
            // [2] は余分なマテリアル用に [1] を複製したもの
            Assert.That(mesh.GetTriangles(2), Is.EqualTo(source.GetTriangles(1)));
            // [3] が重ね貼り（複製した頂点だけを指す）
            var added = mesh.GetTriangles(3);
            Assert.That(added.Length, Is.EqualTo(6));
            foreach (var index in added) Assert.That(index, Is.GreaterThanOrEqualTo(4));
        }
    }
}
