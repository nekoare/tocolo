// 流用元: dev.nekoare.uv-side-splitter/Runtime/RendererMeshAccess.cs
using UnityEngine;

namespace Nekoare.ClickRecolor
{
    /// <summary>
    /// SkinnedMeshRenderer と MeshRenderer(+MeshFilter) のメッシュアクセスを一本化する。
    /// </summary>
    public static class RendererMeshAccess
    {
        public static Mesh GetSharedMesh(Renderer renderer)
        {
            if (renderer == null) return null;
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;

            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        public static void SetSharedMesh(Renderer renderer, Mesh mesh)
        {
            if (renderer == null) return;
            if (renderer is SkinnedMeshRenderer skinned)
            {
                skinned.sharedMesh = mesh;
                return;
            }

            var filter = renderer.GetComponent<MeshFilter>();
            if (filter != null) filter.sharedMesh = mesh;
        }

        public static bool IsSupported(Renderer renderer)
        {
            if (renderer == null) return false;
            if (renderer is SkinnedMeshRenderer) return true;
            return renderer is MeshRenderer && renderer.GetComponent<MeshFilter>() != null;
        }
    }
}
