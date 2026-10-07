using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Decal
{
    /// <summary>
    /// 画像の割り付け方: 重ね貼りに描く三角形の選び方と、頂点ごとの画像の座標。置き方が箱なら平行投影（PlanarMapping）、
    /// シールなら面に沿った展開（UnfoldMapping）。頂点は元の頂点番号（判定用の頂点の並び）で指す
    /// </summary>
    internal interface IDecalMapping
    {
        /// <summary>頂点 v に画像の座標があるか（展開が届かない頂点は無い）</summary>
        bool Maps(int v);
        /// <summary>頂点 v の画像の座標（0..1 の外もある。座標の無い頂点は画像の外）</summary>
        Vector2 Uv(int v);
        /// <summary>三角形（元の頂点番号）を重ね貼りに描くか</summary>
        bool Draws(int i0, int i1, int i2);
    }

    /// <summary>箱の正面からの平行投影（今までの割り付け）: 箱の正面を向き、箱と交わる三角形を描く</summary>
    internal sealed class PlanarMapping : IDecalMapping
    {
        private readonly Vector3[] _judge;
        private readonly Matrix4x4 _judgeToBox;
        private readonly bool _flip;
        private readonly Vector3 _safeSize;
        private readonly Vector3 _half;
        private readonly Vector2 _fit;

        /// <summary>judgeVertices を judgeToBox で箱のローカルへ写して投影する。flip は鏡像の Renderer（カリングが反転する）</summary>
        public PlanarMapping(Vector3[] judgeVertices, Matrix4x4 judgeToBox, bool flip, Vector3 boxSize, Vector2 fit)
        {
            _judge = judgeVertices;
            _judgeToBox = judgeToBox;
            _flip = flip;
            _safeSize = DecalLayerBuilder.SafeSize(boxSize);
            _half = new Vector3(Mathf.Abs(_safeSize.x), Mathf.Abs(_safeSize.y), Mathf.Abs(_safeSize.z)) * 0.5f;
            _fit = fit;
        }

        public bool Maps(int v) => v >= 0 && v < _judge.Length;

        public Vector2 Uv(int v) => Maps(v) ? DecalProjection.ProjectUv(_judgeToBox.MultiplyPoint3x4(_judge[v]), _safeSize, _fit) : new Vector2(-1f, -1f);

        public bool Draws(int i0, int i1, int i2)
        {
            if (!Maps(i0) || !Maps(i1) || !Maps(i2)) return false;
            var p0 = _judgeToBox.MultiplyPoint3x4(_judge[i0]);
            var p1 = _judgeToBox.MultiplyPoint3x4(_judge[i1]);
            var p2 = _judgeToBox.MultiplyPoint3x4(_judge[i2]);
            return DecalProjection.IsFrontFacing(p0, p1, p2, _flip) && DecalProjection.TriangleTouchesBox(p0, p1, p2, _half);
        }
    }

    /// <summary>
    /// 面に沿った展開（SurfaceUnfold）の割り付け: 頂点の展開の座標を、平行投影と同じ式で画像の座標にする。向きは見ない（面に沿って回り込むので裏側も描く）。
    /// 両側から回り込んだ画像が裏でぶつかった所（隣どうしで座標が飛ぶ）は描かない
    /// </summary>
    internal sealed class UnfoldMapping : IDecalMapping
    {
        /// <summary>辺の 3D の長さに比べて展開の座標の差がこの倍率を超える三角形は描かない（裏でぶつかった所・展開が大きく歪んだ所）</summary>
        internal const float MaxStretch = 2.5f;

        private readonly Vector2[] _coords;
        private readonly Vector3[] _positions;
        private readonly Vector3 _safeSize;
        private readonly Vector2 _fit;

        /// <summary>coords は元の頂点ごとの展開の座標（届かない頂点は NaN）、positions はそれを求めた位置（同じ並び）</summary>
        public UnfoldMapping(Vector2[] coords, Vector3[] positions, Vector3 boxSize, Vector2 fit)
        {
            _coords = coords;
            _positions = positions;
            _safeSize = DecalLayerBuilder.SafeSize(boxSize);
            _fit = fit;
        }

        public bool Maps(int v) => v >= 0 && v < _coords.Length && !float.IsNaN(_coords[v].x);

        public Vector2 Uv(int v) => Maps(v) ? DecalProjection.ProjectUv(new Vector3(_coords[v].x, _coords[v].y, 0f), _safeSize, _fit) : new Vector2(-1f, -1f);

        public bool Draws(int i0, int i1, int i2)
        {
            if (!Maps(i0) || !Maps(i1) || !Maps(i2)) return false;
            if (!Smooth(i0, i1) || !Smooth(i1, i2) || !Smooth(i2, i0)) return false;
            var a = Uv(i0);
            var b = Uv(i1);
            var c = Uv(i2);
            // 画像（0..1）に重ならない三角形は描かない
            return Mathf.Max(a.x, Mathf.Max(b.x, c.x)) >= 0f && Mathf.Min(a.x, Mathf.Min(b.x, c.x)) <= 1f
                && Mathf.Max(a.y, Mathf.Max(b.y, c.y)) >= 0f && Mathf.Min(a.y, Mathf.Min(b.y, c.y)) <= 1f;
        }

        private bool Smooth(int a, int b)
        {
            float coordLength = (_coords[a] - _coords[b]).magnitude;
            float length = (_positions[a] - _positions[b]).magnitude;
            return coordLength <= length * MaxStretch + 1e-7f;
        }
    }

    /// <summary>編集の置き方に合わせて割り付け方を作る</summary>
    internal static class DecalMappings
    {
        /// <summary>展開を広げる距離の、見えている画像の対角線の半分に対する倍率（画像の角まで確実に届くように少し広く）</summary>
        internal const float ReachMargin = 1.15f;

        /// <summary>見えている画像（箱の縦横から比率を保つ縮みを除いた大きさ）の対角線の半分</summary>
        internal static float ImageHalfDiagonal(Vector3 boxSize, Vector2 fit)
        {
            float width = Mathf.Abs(boxSize.x) / Mathf.Max(fit.x, 1e-6f);
            float height = Mathf.Abs(boxSize.y) / Mathf.Max(fit.y, 1e-6f);
            return new Vector2(width, height).magnitude * 0.5f;
        }

        /// <summary>
        /// edit の割り付け方。judgeVertices（元の頂点の判定用の位置）は judgeLocalToWorld でワールドへ写るもの。flip は鏡像の Renderer（平行投影のカリング）。
        /// triangles は展開でたどる三角形（元の頂点番号、3 つずつ。対象スロットの三角形）を返す関数（平行投影では呼ばない）。
        /// graph に展開の面のつながりがあれば使い、無ければ作って入れる（置き直しのたびに作り直さないため。頂点の位置が同じ間だけ使い回すこと）。
        /// 作るときは、種から直線で届く距離の外の三角形は入れない（面に沿った距離は直線距離以上なので届かない）。
        /// selected（元の頂点番号 3 つ。選択範囲の三角形）を渡すと、展開の種をその中から選ぶ（画像の中心が選択範囲の外へ出ても、範囲に画像が続くように）
        /// </summary>
        internal static IDecalMapping For(RecolorEdit edit, Transform root, Vector3[] judgeVertices, Matrix4x4 judgeLocalToWorld, bool flip,
            Func<IReadOnlyList<int>> triangles, ref SurfaceUnfoldGraph graph, bool regionOnly = true, Func<int, int, int, bool> selected = null)
        {
            var fit = DecalLayerBuilder.FitScale(edit.decalTexture, edit.decalBoxSize, edit.decalKeepAspect);
            if (!edit.decalSticker)
            {
                var judgeToBox = BoxMaskBuilder.RootToBox(edit.decalBoxPosition, edit.decalBoxRotation) * root.worldToLocalMatrix * judgeLocalToWorld;
                return new PlanarMapping(judgeVertices, judgeToBox, flip, edit.decalBoxSize, fit);
            }

            var judgeToRoot = root.worldToLocalMatrix * judgeLocalToWorld;
            var positions = new Vector3[judgeVertices.Length];
            for (int i = 0; i < positions.Length; i++) positions[i] = judgeToRoot.MultiplyPoint3x4(judgeVertices[i]);
            float reach = ImageHalfDiagonal(edit.decalBoxSize, fit) * ReachMargin;
            var seed = edit.decalBoxPosition;
            if (graph == null)
            {
                Func<int, int, int, bool> near = null;
                if (regionOnly)
                {
                    // 三角形を囲む球が、種から reach の球と重なるものだけ（大きな三角形の中に種があっても落とさないよう、頂点の距離ではなく囲む球で見る）
                    near = (a, b, c) =>
                    {
                        var center = (positions[a] + positions[b] + positions[c]) / 3f;
                        float radius = Mathf.Sqrt(Mathf.Max((positions[a] - center).sqrMagnitude,
                            Mathf.Max((positions[b] - center).sqrMagnitude, (positions[c] - center).sqrMagnitude)));
                        return (center - seed).magnitude <= reach + radius;
                    };
                }
                graph = SurfaceUnfoldGraph.Build(positions, triangles(), near);
            }
            var coords = new Vector2[judgeVertices.Length];
            // 種（箱の中心）は、ふだんは面の上にあるが、画像を選択範囲の外へずらしている途中は範囲の外（別のパーツの上や宙）にある。
            // 届く距離の中に選択範囲の三角形があればそこから始め、座標は箱の中心から測る（届く所に何も無ければ何も描かない）
            SurfaceUnfold.TryUnfold(graph, seed, edit.decalBoxRotation, reach, reach, coords, selected);
            return new UnfoldMapping(coords, positions, edit.decalBoxSize, fit);
        }

        /// <summary>
        /// クリックの当たり hit の画像の座標: 当たった Renderer（今のポーズで焼いた判定用メッシュ。ScenePicker と同じ）で edit の割り付けを作り、
        /// 当たった三角形の 3 頂点の座標を重心係数で補間する。3 頂点のどれかに座標が無い・メッシュが読めなければ false
        /// </summary>
        internal static bool TryImageUvAt(RecolorEdit edit, Transform root, in Picking.PickHit hit, out Vector2 uv)
        {
            uv = default;
            if (edit == null || root == null || hit.renderer == null || edit.sourceTexture == null) return false;
            var geometry = DecalOverlayAssembler.RendererGeometry.Create(hit.renderer);
            if (geometry == null) return false;
            try
            {
                var mesh = geometry.judgeMesh;
                if (hit.subMeshIndex < 0 || hit.subMeshIndex >= mesh.subMeshCount || mesh.GetTopology(hit.subMeshIndex) != MeshTopology.Triangles) return false;
                var indices = mesh.GetTriangles(hit.subMeshIndex);
                int i = hit.triangleIndex * 3;
                if (i < 0 || i + 2 >= indices.Length) return false;
                var slots = DecalOverlayAssembler.CollectOverlaySlots(hit.renderer.sharedMaterials, mesh, edit.sourceTexture, out _);
                if (!slots.Contains(hit.subMeshIndex)) slots.Add(hit.subMeshIndex);
                // 展開の種は表示と同じく選択範囲の三角形から選ぶ（違う種から展開すると、画像の中心が範囲の外にあるとき座標がずれる）
                var component = root.GetComponent<ClickRecolor>();
                var meshUv = mesh.uv;
                var texture = hit.mainTexture;
                Func<int, int, int, bool> selected = null;
                if (component != null && hit.hasMainTexture && meshUv != null && meshUv.Length == mesh.vertexCount)
                {
                    bool Selected(Vector2 p) => SceneTool.StickerSurface.IsSelected(component, edit, edit, edit.sourceTexture,
                        Picking.MaterialTextureResolver.ToTextureCoord(texture, p));
                    selected = (a, b, c) => Selected((meshUv[a] + meshUv[b] + meshUv[c]) / 3f) || Selected(meshUv[a]) || Selected(meshUv[b]) || Selected(meshUv[c]);
                }
                // 平行投影のカリング（flip）は展開では使わない。箱の編集はこの関数を呼ばない
                SurfaceUnfoldGraph graph = null;
                var mapping = For(edit, root, geometry.judgeVertices, geometry.judgeLocalToWorld, false, () => TrianglesOf(mesh, slots), ref graph,
                    selected: selected);
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                if (!mapping.Maps(a) || !mapping.Maps(b) || !mapping.Maps(c)) return false;
                uv = mapping.Uv(a) * hit.barycentric.x + mapping.Uv(b) * hit.barycentric.y + mapping.Uv(c) * hit.barycentric.z;
                return true;
            }
            finally
            {
                geometry.Dispose();
            }
        }

        /// <summary>mesh の submeshes の三角形（元の頂点番号、3 つずつ）。三角形でないサブメッシュは飛ばす</summary>
        internal static List<int> TrianglesOf(Mesh mesh, IReadOnlyList<int> submeshes)
        {
            var result = new List<int>();
            if (mesh == null || submeshes == null) return result;
            var seen = new HashSet<int>();
            foreach (int submesh in submeshes)
            {
                if (submesh < 0 || submesh >= mesh.subMeshCount || !seen.Add(submesh) || mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                result.AddRange(mesh.GetTriangles(submesh));
            }
            return result;
        }
    }
}
