using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.Decal
{
    /// <summary>
    /// 「画像を入れる」第 2 段（重ね貼り。設計 docs/plans/2026-10-04-tocolo-decal-overlay-design.md §3）の重ね貼りメッシュ。
    /// 箱に入る表の三角形の頂点を元メッシュの末尾に複製し、1 つのサブメッシュとして末尾に足す。
    /// そのサブメッシュを元マテリアルの複製（透過、DecalOverlayMaterial）で描き、画像を UV0（投影 UV）で直接引く。
    /// 焼き込みと違い貼る場所のテクスチャ密度に縛られないので、縁が画像の解像度でなめらかになる。
    /// ボーンウェイト・ブレンドシェイプも複製するので、アニメーション・表情に追従する
    /// </summary>
    internal static class DecalOverlayMesh
    {
        /// <summary>UInt16 のインデックスで指せる頂点数の上限</summary>
        private const int MaxUInt16Vertices = 65535;

        /// <summary>
        /// MeshRenderer 用（判定に元メッシュの頂点をそのまま使う）。判定・複製の中身は判定用の頂点を分けて受け取る版と同じ。
        /// meshToBox は元メッシュのローカル頂点を箱のローカルへ写す行列。読み取り不可（Read/Write 無効）のメッシュは null
        /// </summary>
        internal static Mesh Build(Mesh source, IReadOnlyList<int> submeshes, Matrix4x4 meshToBox, bool flip,
            Vector3 boxSize, Vector2 fit, float offset, int materialCount = 0, bool allTriangles = false, List<int> duplicatedSources = null,
            Func<int, int, int, bool> include = null)
        {
            if (source == null || !source.isReadable) return null;
            // 判定用と複製元が同じ配列で済むので、mesh.vertices のコピーは 1 回だけ
            var vertices = source.vertices;
            return BuildCore(source, vertices, vertices, submeshes, meshToBox, flip, boxSize, fit, offset, materialCount, allTriangles, duplicatedSources, include);
        }

        /// <summary>
        /// 元メッシュ source の複製に、submeshes の三角形のうち「箱の +Z 側を向き（DecalProjection.IsFrontFacing）、箱と交わる（DecalProjection.TriangleTouchesBox）」のものの
        /// 頂点を末尾に複製して足し、それらを 1 つのサブメッシュ（Triangles）として末尾に追加した新しいメッシュを返す（HideAndDontSave。呼び出し側が破棄）。
        /// 該当が無い・読み取り不可なら null（重いブレンドシェイプの作り直しもしない）。
        ///
        /// judgeVertices は判定と投影 UV に使う頂点（source と同じ頂点数・順序）。SkinnedMeshRenderer は今のポーズで BakeMesh した頂点を渡す
        /// （BakeMesh は頂点の並びを保つ）。複製する頂点そのものは source（バインドポーズ）の値なので、スキニング・ブレンドシェイプが掛かって追従する。
        /// judgeToBox は judgeVertices を箱のローカルへ写す行列。flip は鏡像の Renderer（DecalLayerBuilder と同じ求め方）。
        /// fit は DecalLayerBuilder.FitScale。offset は複製頂点を元の法線方向に浮かせる距離（source のローカル単位。呼び出し側で換算する）。
        ///
        /// 箱の外にはみ出す三角形も含めるので、はみ出した部分は投影 UV が 0..1 の外になる。重ね貼りマテリアルの画像は Clamp で引かれるため、
        /// O3（画像空間のデカール画像）は画像の縁を α = 0 にしておく約束（そうしないと縁の色が箱の外へ伸びる）。
        ///
        /// materialCount は表示する Renderer のマテリアル数（画像用の一時メッシュなど Renderer に付けないときは 0）。
        /// source のサブメッシュ数より多いとき、Unity は余分なマテリアルで最後のサブメッシュを重ね描きする（lilToon の FakeShadow・Fur を余分なスロットに入れる手順）。
        /// そのまま末尾に重ね貼りサブメッシュを足すと余分なマテリアルが重ね貼りサブメッシュを描いてしまうので、先に最後のサブメッシュを
        /// （materialCount − サブメッシュ数）個複製してサブメッシュ数をマテリアル数に揃え、その後ろに重ね貼りサブメッシュを足す。
        /// 結果の重ね貼りサブメッシュの番号は max(サブメッシュ数, materialCount)（＝末尾）。
        ///
        /// allTriangles が true なら向きも箱との交わりも見ず、submeshes の三角形をすべて複製する（画像の箱のドラッグ中に、メッシュを作り直さず
        /// 投影 UV と描く三角形の並びだけを毎フレーム書き換えて追従させるため。DecalOverlayAssembler.UpdateSegment）。duplicatedSources を渡すと、
        /// 複製した頂点の複製元の番号を複製の順に入れて返す（複製頂点 k は結果のメッシュの source.vertexCount + k 番）。
        ///
        /// include（元の頂点番号 3 つ）を渡すと、それが false の三角形は allTriangles でも複製しない（選択範囲の外の三角形を重ねない。
        /// 画像は画像の空間で 1 枚なので、箱の正面から見て前後に重なる別の面が同じ画素を引いてしまうため。実機 2026-10-04）
        /// </summary>
        internal static Mesh Build(Mesh source, Vector3[] judgeVertices, IReadOnlyList<int> submeshes, Matrix4x4 judgeToBox, bool flip,
            Vector3 boxSize, Vector2 fit, float offset, int materialCount = 0, bool allTriangles = false, List<int> duplicatedSources = null,
            Func<int, int, int, bool> include = null)
        {
            if (source == null || !source.isReadable) return null;
            return BuildCore(source, source.vertices, judgeVertices, submeshes, judgeToBox, flip, boxSize, fit, offset, materialCount, allTriangles, duplicatedSources, include);
        }

        /// <summary>Build の本体。sourceVertices は source.vertices（呼び出し側で 1 回だけ取る）</summary>
        private static Mesh BuildCore(Mesh source, Vector3[] sourceVertices, Vector3[] judgeVertices, IReadOnlyList<int> submeshes,
            Matrix4x4 judgeToBox, bool flip, Vector3 boxSize, Vector2 fit, float offset, int materialCount, bool allTriangles,
            List<int> duplicatedSourcesOut, Func<int, int, int, bool> include)
        {
            if (judgeVertices == null || submeshes == null) return null;
            int n = source.vertexCount;
            if (judgeVertices.Length != n)
            {
                throw new ArgumentException($"judgeVertices の数（{judgeVertices.Length}）が元メッシュの頂点数（{n}）と違います", nameof(judgeVertices));
            }

            var safeSize = DecalLayerBuilder.SafeSize(boxSize);
            var half = new Vector3(Mathf.Abs(safeSize.x), Mathf.Abs(safeSize.y), Mathf.Abs(safeSize.z)) * 0.5f;
            var boxPositions = new Vector3[n];
            for (int i = 0; i < n; i++) boxPositions[i] = judgeToBox.MultiplyPoint3x4(judgeVertices[i]);

            // 三角形を選び、使う頂点を 1 回だけ複製する（三角形ごとに複製するより頂点が少なく、ブレンドシェイプの作り直しも軽い）
            var remap = new int[n];
            for (int i = 0; i < n; i++) remap[i] = -1;
            var duplicatedSources = new List<int>();
            var overlayTriangles = new List<int>();
            var seenSubmeshes = new HashSet<int>();
            foreach (var submesh in submeshes)
            {
                if (submesh < 0 || submesh >= source.subMeshCount || !seenSubmeshes.Add(submesh)) continue;
                if (source.GetTopology(submesh) != MeshTopology.Triangles) continue;
                var triangles = source.GetTriangles(submesh);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                    if (include != null && !include(i0, i1, i2)) continue;
                    var p0 = boxPositions[i0];
                    var p1 = boxPositions[i1];
                    var p2 = boxPositions[i2];
                    if (!allTriangles)
                    {
                        if (!DecalProjection.IsFrontFacing(p0, p1, p2, flip)) continue;
                        if (!DecalProjection.TriangleTouchesBox(p0, p1, p2, half)) continue;
                    }
                    overlayTriangles.Add(Duplicate(i0, n, remap, duplicatedSources));
                    overlayTriangles.Add(Duplicate(i1, n, remap, duplicatedSources));
                    overlayTriangles.Add(Duplicate(i2, n, remap, duplicatedSources));
                }
            }
            if (overlayTriangles.Count == 0) return null;
            if (duplicatedSourcesOut != null)
            {
                duplicatedSourcesOut.Clear();
                duplicatedSourcesOut.AddRange(duplicatedSources);
            }

            int m = duplicatedSources.Count;
            int total = n + m;
            var mesh = Object.Instantiate(source);
            mesh.name = source.name + " (Decal)";
            mesh.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                // ブレンドシェイプは頂点数と合わなくなるので、頂点数を変える前に消す（作り直しは元メッシュ source から読む）
                bool hasBlendShapes = source.blendShapeCount > 0;
                if (hasBlendShapes) mesh.ClearBlendShapes();
                // 元が UInt16 でも、足した結果が収まらなければ頂点を入れる前に UInt32 にする
                if (total > MaxUInt16Vertices && mesh.indexFormat == IndexFormat.UInt16) SwitchToUInt32(mesh);

                var normals = source.normals;
                bool hasNormals = normals != null && normals.Length == n;
                var vertices = Extend(sourceVertices, duplicatedSources, (s, v) => hasNormals ? v + normals[s] * offset : v);
                mesh.SetVertices(vertices);
                if (hasNormals) mesh.SetNormals(Extend(normals, duplicatedSources, (s, v) => v));
                var tangents = source.tangents;
                if (tangents != null && tangents.Length == n) mesh.SetTangents(Extend(tangents, duplicatedSources, (s, v) => v));
                ExtendColors(source, mesh, duplicatedSources);

                // UV0 = 投影 UV（判定に使った位置＝今のポーズの箱のローカルから）。0..1 の外もそのまま
                var uv0 = GetUvs(source, 0);
                SetUvs(mesh, 0, Math.Max(2, Dimension(source, 0)), n, uv0, duplicatedSources,
                    s => DecalProjection.ProjectUv(boxPositions[s], safeSize, fit));
                // UV1 = 元の UV0（O3 で選択マスク・元の位置を引くため）。元の頂点は元の UV1 のまま（無ければ 0）。
                // 元マテリアルが UV1 を使う機能（2nd テクスチャの UV1 指定など）は、複製頂点では UV1 が元の UV0 に置き換わるので
                // 重ね貼りマテリアル側では正しく引けない（DecalOverlayMaterial で UV0 依存のテクスチャを外すのと同じく、見た目の差として受け入れる）
                SetUvs(mesh, 1, Math.Max(2, Dimension(source, 1)), n, GetUvs(source, 1), duplicatedSources,
                    s => s < uv0.Count ? uv0[s] : Vector4.zero);
                // UV2 以降は元の値を写す（重ね貼りマテリアルが引いても元と同じ値になるように）
                for (int channel = 2; channel < 8; channel++)
                {
                    int dimension = Dimension(source, channel);
                    if (dimension == 0) continue;
                    var uvs = GetUvs(source, channel);
                    SetUvs(mesh, channel, dimension, n, uvs, duplicatedSources, s => uvs[s]);
                }

                ExtendBoneWeights(source, mesh, duplicatedSources);
                if (hasBlendShapes) CopyBlendShapes(source, mesh, duplicatedSources);

                int newSubmesh = PadSubmeshesToMaterialCount(source, mesh, materialCount);
                mesh.subMeshCount = newSubmesh + 1;
                mesh.SetTriangles(overlayTriangles, newSubmesh, false);
                mesh.RecalculateBounds();
                return mesh;
            }
            catch
            {
                Object.DestroyImmediate(mesh);
                throw;
            }
        }

        /// <summary>
        /// materialCount が mesh のサブメッシュ数より多ければ、最後のサブメッシュ（インデックス・トポロジ・baseVertex）を複製して数を揃える
        /// （余分なマテリアルが今までどおり最後のサブメッシュを描くように）。重ね貼りサブメッシュを入れる番号（揃えた後のサブメッシュ数）を返す
        /// </summary>
        private static int PadSubmeshesToMaterialCount(Mesh source, Mesh mesh, int materialCount)
        {
            int count = mesh.subMeshCount;
            if (materialCount <= count || count == 0) return count;
            int last = count - 1;
            var indices = source.GetIndices(last, false);
            var topology = source.GetTopology(last);
            int baseVertex = (int)source.GetBaseVertex(last);
            mesh.subMeshCount = materialCount;
            for (int i = count; i < materialCount; i++) mesh.SetIndices(indices, topology, i, false, baseVertex);
            return materialCount;
        }

        /// <summary>元の頂点 index の複製先（n 以降）。初めてなら複製元として登録する</summary>
        private static int Duplicate(int index, int n, int[] remap, List<int> duplicatedSources)
        {
            if (remap[index] < 0)
            {
                remap[index] = n + duplicatedSources.Count;
                duplicatedSources.Add(index);
            }
            return remap[index];
        }

        /// <summary>
        /// インデックス形式を UInt32 に変える。形式の切り替えで既存のインデックスが保たれるかは Unity の実装に頼らず、
        /// 全サブメッシュのインデックス（baseVertex を足す前）・トポロジ・baseVertex を退避し、切り替え後に書き戻す
        /// </summary>
        private static void SwitchToUInt32(Mesh mesh)
        {
            int count = mesh.subMeshCount;
            var indices = new int[count][];
            var topologies = new MeshTopology[count];
            var baseVertices = new int[count];
            for (int i = 0; i < count; i++)
            {
                indices[i] = mesh.GetIndices(i, false);
                topologies[i] = mesh.GetTopology(i);
                baseVertices[i] = (int)mesh.GetBaseVertex(i);
            }
            mesh.indexFormat = IndexFormat.UInt32;
            // 切り替えでサブメッシュ数が変わっても書き戻せるよう、数を明示的に戻す
            mesh.subMeshCount = count;
            for (int i = 0; i < count; i++) mesh.SetIndices(indices[i], topologies[i], i, false, baseVertices[i]);
        }

        /// <summary>original の後ろに、duplicatedSources の順で make(複製元, 元の値) を足した配列</summary>
        private static T[] Extend<T>(T[] original, List<int> duplicatedSources, Func<int, T, T> make)
        {
            var result = new T[original.Length + duplicatedSources.Count];
            Array.Copy(original, result, original.Length);
            for (int k = 0; k < duplicatedSources.Count; k++)
            {
                int s = duplicatedSources[k];
                result[original.Length + k] = make(s, original[s]);
            }
            return result;
        }

        /// <summary>頂点色は元の形式のまま延長する（8 bit の色を float で入れ直すと頂点の形式が変わり重くなる）</summary>
        private static void ExtendColors(Mesh source, Mesh mesh, List<int> duplicatedSources)
        {
            if (!source.HasVertexAttribute(VertexAttribute.Color)) return;
            if (source.GetVertexAttributeFormat(VertexAttribute.Color) == VertexAttributeFormat.UNorm8)
            {
                mesh.SetColors(Extend(source.colors32, duplicatedSources, (s, c) => c));
            }
            else
            {
                mesh.SetColors(Extend(source.colors, duplicatedSources, (s, c) => c));
            }
        }

        private static int Dimension(Mesh mesh, int channel)
        {
            var attribute = VertexAttribute.TexCoord0 + channel;
            return mesh.HasVertexAttribute(attribute) ? mesh.GetVertexAttributeDimension(attribute) : 0;
        }

        /// <summary>UV チャンネルを Vector4 で読む（無ければ空）</summary>
        private static List<Vector4> GetUvs(Mesh mesh, int channel)
        {
            var uvs = new List<Vector4>();
            if (Dimension(mesh, channel) > 0) mesh.GetUVs(channel, uvs);
            return uvs;
        }

        /// <summary>
        /// UV チャンネルを、元の頂点は original（無ければ 0）、複製頂点は make(複製元) で埋めて、dimension（2..4）成分で書く
        /// </summary>
        private static void SetUvs(Mesh mesh, int channel, int dimension, int n, List<Vector4> original,
            List<int> duplicatedSources, Func<int, Vector4> make)
        {
            var values = new List<Vector4>(n + duplicatedSources.Count);
            for (int i = 0; i < n; i++) values.Add(i < original.Count ? original[i] : Vector4.zero);
            foreach (var s in duplicatedSources) values.Add(make(s));
            switch (dimension)
            {
                case 2:
                    var uv2 = new List<Vector2>(values.Count);
                    foreach (var v in values) uv2.Add(v);
                    mesh.SetUVs(channel, uv2);
                    break;
                case 3:
                    var uv3 = new List<Vector3>(values.Count);
                    foreach (var v in values) uv3.Add(v);
                    mesh.SetUVs(channel, uv3);
                    break;
                default:
                    mesh.SetUVs(channel, values);
                    break;
            }
        }

        /// <summary>ボーンウェイトを頂点ごとの本数（可変）のまま延長する。bindposes は Instantiate で写ったまま</summary>
        private static void ExtendBoneWeights(Mesh source, Mesh mesh, List<int> duplicatedSources)
        {
            var bonesPerVertex = source.GetBonesPerVertex().ToArray();
            if (bonesPerVertex.Length == 0) return;
            var weights = source.GetAllBoneWeights().ToArray();
            // 頂点ごとのウェイトの開始位置（GetAllBoneWeights は頂点順に詰めて並ぶ）
            var start = new int[bonesPerVertex.Length];
            int sum = 0;
            for (int i = 0; i < bonesPerVertex.Length; i++)
            {
                start[i] = sum;
                sum += bonesPerVertex[i];
            }

            var newBonesPerVertex = new NativeArray<byte>(bonesPerVertex.Length + duplicatedSources.Count, Allocator.Temp);
            int added = 0;
            foreach (var s in duplicatedSources) added += bonesPerVertex[s];
            var newWeights = new NativeArray<BoneWeight1>(weights.Length + added, Allocator.Temp);
            try
            {
                for (int i = 0; i < bonesPerVertex.Length; i++) newBonesPerVertex[i] = bonesPerVertex[i];
                for (int i = 0; i < weights.Length; i++) newWeights[i] = weights[i];
                int w = weights.Length;
                for (int k = 0; k < duplicatedSources.Count; k++)
                {
                    int s = duplicatedSources[k];
                    newBonesPerVertex[bonesPerVertex.Length + k] = bonesPerVertex[s];
                    for (int b = 0; b < bonesPerVertex[s]; b++) newWeights[w++] = weights[start[s] + b];
                }
                mesh.SetBoneWeights(newBonesPerVertex, newWeights);
            }
            finally
            {
                newBonesPerVertex.Dispose();
                newWeights.Dispose();
            }
        }

        /// <summary>
        /// source の全シェイプ・全フレームを、複製頂点に複製元のデルタ（位置・法線・接線）を足した配列で mesh に足し直す（名前・重みはそのまま）。
        /// mesh 側は呼び出し前に ClearBlendShapes 済み。全フレームを一度に読むと表情の多い顔で数十 MB になるので、1 フレームずつ読んで書く
        /// </summary>
        private static void CopyBlendShapes(Mesh source, Mesh mesh, List<int> duplicatedSources)
        {
            int n = source.vertexCount;
            int total = n + duplicatedSources.Count;
            // 読み出し（元の頂点数）と書き込み（延長後）の配列を 1 回だけ確保して使い回す（AddBlendShapeFrame は中身を写すので再利用してよい）
            var deltaVertices = new Vector3[n];
            var deltaNormals = new Vector3[n];
            var deltaTangents = new Vector3[n];
            var outVertices = new Vector3[total];
            var outNormals = new Vector3[total];
            var outTangents = new Vector3[total];
            for (int shape = 0; shape < source.blendShapeCount; shape++)
            {
                var name = source.GetBlendShapeName(shape);
                int frameCount = source.GetBlendShapeFrameCount(shape);
                for (int frame = 0; frame < frameCount; frame++)
                {
                    source.GetBlendShapeFrameVertices(shape, frame, deltaVertices, deltaNormals, deltaTangents);
                    ExtendInto(deltaVertices, outVertices, duplicatedSources);
                    ExtendInto(deltaNormals, outNormals, duplicatedSources);
                    ExtendInto(deltaTangents, outTangents, duplicatedSources);
                    mesh.AddBlendShapeFrame(name, source.GetBlendShapeFrameWeight(shape, frame), outVertices, outNormals, outTangents);
                }
            }
        }

        /// <summary>original を result の先頭に写し、その後ろに duplicatedSources の順で複製元の値を入れる（result は延長後の長さ）</summary>
        private static void ExtendInto(Vector3[] original, Vector3[] result, List<int> duplicatedSources)
        {
            Array.Copy(original, result, original.Length);
            for (int k = 0; k < duplicatedSources.Count; k++) result[original.Length + k] = original[duplicatedSources[k]];
        }
    }
}
