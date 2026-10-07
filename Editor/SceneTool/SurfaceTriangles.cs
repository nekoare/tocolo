using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Picking;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>面の向きを平均するための三角形 1 枚（対象ルートのローカル）: 重心と、表の向きに面積を掛けたベクトル</summary>
    internal readonly struct SurfaceTriangle
    {
        public readonly Vector3 center;
        public readonly Vector3 areaNormal;

        public SurfaceTriangle(Vector3 center, Vector3 areaNormal)
        {
            this.center = center;
            this.areaNormal = areaNormal;
        }
    }

    /// <summary>
    /// 画像（シール）を面に向けるときの面の向き: クリックした所のまわりの三角形の表の向きを面積で重みづけして平均する。
    /// 選択範囲の点の広がりが一番薄い方向を面の向きとみなす（平面の当てはめ）と、毛束・ネクタイ・紐のような細いパーツが曲がっているとき、
    /// 曲がりによる前後の広がりがパーツの幅より大きくなり、幅の方向（横）を面の向きと取り違える。三角形の向きならパーツの形に関係なく面の向きになる
    /// </summary>
    internal static class SurfaceTriangles
    {
        /// <summary>
        /// renderer の三角形（対象ルートのローカル。worldToRoot は対象ルートの worldToLocalMatrix）。textures を渡すと、メインテクスチャがそのどれかの
        /// マテリアル枠が描くサブメッシュだけ（同じメッシュの別の素材の面に向きを引っぱられないように）。SkinnedMeshRenderer は今のポーズで焼く。
        /// 読み取り不可・三角形でないサブメッシュは飛ばす
        /// </summary>
        internal static List<SurfaceTriangle> Collect(Renderer renderer, Matrix4x4 worldToRoot, ICollection<Texture2D> textures)
        {
            var result = new List<SurfaceTriangle>();
            if (renderer == null || !MeshRaycaster.TryGetMeshData(renderer, out var mesh, out var localToWorld, out bool owns) || mesh == null) return result;
            try
            {
                if (!mesh.isReadable) return result;
                var toRoot = worldToRoot * localToWorld;
                // 鏡像（負のスケール）では Unity が巻き方を裏返して描くので、表の向きも逆にする
                bool flip = toRoot.determinant < 0f;
                var vertices = mesh.vertices;
                var materials = renderer.sharedMaterials;
                var done = new HashSet<int>();
                int slots = textures == null ? mesh.subMeshCount : materials.Length;
                for (int slot = 0; slot < slots; slot++)
                {
                    int submesh = MaterialTextureResolver.SubmeshOfSlot(slot, mesh.subMeshCount);
                    if (submesh < 0) break;
                    if (textures != null
                        && !(MaterialTextureResolver.TryGetMainTexture(materials[slot], out var info) && info.texture != null && textures.Contains(info.texture)))
                    {
                        continue;
                    }
                    if (!done.Add(submesh) || mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                    var indices = mesh.GetTriangles(submesh);
                    for (int i = 0; i + 2 < indices.Length; i += 3)
                    {
                        var a = toRoot.MultiplyPoint3x4(vertices[indices[i]]);
                        var b = toRoot.MultiplyPoint3x4(vertices[indices[i + 1]]);
                        var c = toRoot.MultiplyPoint3x4(vertices[indices[i + 2]]);
                        // Unity の表の面は時計回りで、法線は Cross(b - a, c - a)（DecalBox.TryGetSeedSurface と同じ）。大きさの半分が面積
                        var n = Vector3.Cross(b - a, c - a) * 0.5f;
                        result.Add(new SurfaceTriangle((a + b + c) / 3f, flip ? -n : n));
                    }
                }
            }
            finally
            {
                if (owns) Object.DestroyImmediate(mesh);
            }
            return result;
        }

        /// <summary>
        /// anchor（面の上の点）から radius 以内に重心がある三角形のうち、anchorNormal（見えている側の向き）と同じ側を向くものの、表の向きの面積重みつきの平均（単位ベクトル）。
        /// 裏を向いた三角形（髪の裏側の面など）を足すと打ち消し合うので数えない。1 枚も無い・打ち消し合って向きが無ければ false
        /// </summary>
        internal static bool TryAverageNormal(IReadOnlyList<SurfaceTriangle> triangles, Vector3 anchor, Vector3 anchorNormal, float radius, out Vector3 normal)
        {
            normal = anchorNormal;
            if (triangles == null || triangles.Count == 0 || anchorNormal.sqrMagnitude < 1e-12f) return false;
            float radiusSq = radius * radius;
            var sum = Vector3.zero;
            foreach (var t in triangles)
            {
                if ((t.center - anchor).sqrMagnitude > radiusSq) continue;
                if (Vector3.Dot(t.areaNormal, anchorNormal) <= 0f) continue;
                sum += t.areaNormal;
            }
            // 面積の重みの合計は細かい面だけだと小さいので、Vector3.normalized（長さ 1e-5 未満はゼロ）を使わず長さで割る
            float length = sum.magnitude;
            if (!(length > 0f)) return false;
            normal = sum / length;
            return true;
        }
    }
}
