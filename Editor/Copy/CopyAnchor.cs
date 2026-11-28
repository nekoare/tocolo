using System;
using System.Collections.Generic;
using System.Text;
using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.SceneTool;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Copy
{
    internal enum AnchorFailure
    {
        None,
        /// <summary>同じメッシュ（名前・頂点数・サブメッシュ）が移す先に無い</summary>
        MeshNotFound,
        /// <summary>同じメッシュが複数あり、階層の場所でも 1 つに決まらない</summary>
        Ambiguous,
        TriangleOutOfRange,
        /// <summary>メッシュはあったが、そのスロットのマテリアルに編集できるテクスチャが無い</summary>
        TextureNotFound,
    }

    /// <summary>
    /// 編集の種（クリックした三角形）の場所を、別のアバター・別のプロジェクトでも探せる形で表す。
    /// メッシュはアセットの GUID ではなく名前・頂点数・サブメッシュの形で見分ける（別プロジェクトでは GUID が変わりうるため）。
    /// テクスチャは持たず、移す先のマテリアルのスロットから取り直す（色違いの衣装は同じメッシュで画像だけが別のファイル）
    /// </summary>
    [Serializable]
    internal sealed class CopyAnchor
    {
        public string path;
        public string meshName;
        public int vertexCount;
        public int subMeshCount;
        public int submeshIndexCount;
        public int submesh;
        public int triangle;
        public Vector2 uv;
        public int materialSlot;

        internal static CopyAnchor Describe(Transform root, Renderer renderer, int submesh, int triangle, Vector2 uv, Texture2D sourceTexture)
        {
            var mesh = RendererMeshAccess.GetSharedMesh(renderer);
            return new CopyAnchor
            {
                path = PathOf(root, renderer.transform),
                meshName = mesh != null ? mesh.name : "",
                vertexCount = mesh != null ? mesh.vertexCount : 0,
                subMeshCount = mesh != null ? mesh.subMeshCount : 0,
                submeshIndexCount = mesh != null && submesh >= 0 && submesh < mesh.subMeshCount ? (int)mesh.GetIndexCount(submesh) : 0,
                submesh = submesh,
                triangle = triangle,
                uv = uv,
                materialSlot = SlotOf(renderer, mesh, submesh, sourceTexture),
            };
        }

        internal static AnchorFailure Resolve(CopyAnchor anchor, Transform targetRoot, out Renderer renderer, out Texture2D texture)
        {
            texture = null;
            var failure = ResolveRenderer(anchor, targetRoot, out var found);
            renderer = null;
            if (failure != AnchorFailure.None) return failure;

            var materials = found.sharedMaterials;
            if (anchor.materialSlot < 0 || anchor.materialSlot >= materials.Length) return AnchorFailure.TextureNotFound;
            if (!MaterialTextureResolver.TryGetMainTexture(materials[anchor.materialSlot], out var info)) return AnchorFailure.TextureNotFound;
            if (!RecolorSceneTool.TryGetSourceTexture(info.texture, out var asset)) return AnchorFailure.TextureNotFound;

            renderer = found;
            texture = asset;
            return AnchorFailure.None;
        }

        /// <summary>Renderer と三角形だけを探す（テクスチャは見ない。箱の基準にする髪を探すとき用）</summary>
        internal static AnchorFailure ResolveRenderer(CopyAnchor anchor, Transform targetRoot, out Renderer renderer)
        {
            renderer = null;
            if (anchor == null || targetRoot == null) return AnchorFailure.MeshNotFound;

            var candidates = new List<Renderer>();
            foreach (var r in targetRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (Matches(anchor, RendererMeshAccess.GetSharedMesh(r))) candidates.Add(r);
            }
            if (candidates.Count == 0) return AnchorFailure.MeshNotFound;

            Renderer found = candidates.Count == 1 ? candidates[0] : PickByPath(anchor.path, targetRoot, candidates);
            if (found == null) return AnchorFailure.Ambiguous;

            if (anchor.triangle < 0 || anchor.triangle >= anchor.submeshIndexCount / 3) return AnchorFailure.TriangleOutOfRange;

            renderer = found;
            return AnchorFailure.None;
        }

        /// <summary>
        /// 同じメッシュが複数あるとき、道筋を末尾から比べて一致する段数がいちばん多い候補を選ぶ。
        /// 完全一致で比べないのは、髪を空の GameObject の下に入れるなど親が 1 段増減しただけで左右（Ear_L/Ear_R）を見分けられなくなるため。
        /// いちばん多い候補が並んだら、道筋がまったく同じものを選び、それも無ければ null（先頭の候補に付けない:
        /// 左右のピアスや同じ衣装 2 つで、似た別の場所に黙って付いてしまう）
        /// </summary>
        private static Renderer PickByPath(string path, Transform targetRoot, List<Renderer> candidates)
        {
            var wanted = (path ?? "").Split('/');
            int best = 0;
            var top = new List<(Renderer renderer, string path)>();
            foreach (var r in candidates)
            {
                string candidatePath = PathOf(targetRoot, r.transform);
                int score = CommonSuffix(wanted, candidatePath.Split('/'));
                if (score == 0 || score < best) continue;
                if (score > best)
                {
                    best = score;
                    top.Clear();
                }
                top.Add((r, candidatePath));
            }
            if (top.Count == 1) return top[0].renderer;
            Renderer exact = null;
            foreach (var (r, candidatePath) in top)
            {
                if (candidatePath != path) continue;
                if (exact != null) return null;
                exact = r;
            }
            return exact;
        }

        private static int CommonSuffix(string[] a, string[] b)
        {
            int n = 0;
            while (n < a.Length && n < b.Length && a[a.Length - 1 - n] == b[b.Length - 1 - n]) n++;
            return n;
        }

        private static bool Matches(CopyAnchor anchor, Mesh mesh)
        {
            if (mesh == null) return false;
            if (mesh.name != anchor.meshName || mesh.vertexCount != anchor.vertexCount || mesh.subMeshCount != anchor.subMeshCount) return false;
            if (anchor.submesh < 0 || anchor.submesh >= mesh.subMeshCount) return false;
            return (int)mesh.GetIndexCount(anchor.submesh) == anchor.submeshIndexCount;
        }

        /// <summary>
        /// 種のテクスチャが載っているマテリアルのスロット。サブメッシュより多いスロット（重ね描き）があるので、
        /// サブメッシュに当たるスロットのうちテクスチャが一致するものを優先する
        /// </summary>
        private static int SlotOf(Renderer renderer, Mesh mesh, int submesh, Texture2D sourceTexture)
        {
            var materials = renderer.sharedMaterials;
            int subMeshCount = mesh != null ? mesh.subMeshCount : 0;
            int fallback = -1;
            for (int i = 0; i < materials.Length; i++)
            {
                if (!MaterialTextureResolver.TryGetMainTexture(materials[i], out var info) || info.texture != sourceTexture) continue;
                if (MaterialTextureResolver.SubmeshOfSlot(i, subMeshCount) == submesh) return i;
                if (fallback < 0) fallback = i;
            }
            if (fallback >= 0) return fallback;
            return Mathf.Clamp(submesh, 0, Mathf.Max(0, materials.Length - 1));
        }

        internal static string PathOf(Transform root, Transform target)
        {
            if (target == null) return "";
            var names = new List<string>();
            for (var t = target; t != null && t != root; t = t.parent) names.Add(t.name);
            names.Reverse();
            var sb = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) sb.Append('/');
                sb.Append(names[i]);
            }
            return sb.ToString();
        }
    }
}
