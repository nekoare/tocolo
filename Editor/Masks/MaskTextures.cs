using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>
    /// マスク用 RenderTexture（R8・Linear・enableRandomWrite）の作成と破棄をまとめる
    /// </summary>
    internal static class MaskTextures
    {
        private static RenderTextureDescriptor Descriptor(int width, int height)
        {
            return new RenderTextureDescriptor(width, height, RenderTextureFormat.R8, 0)
            {
                sRGB = false,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
            };
        }

        /// <summary>処理中の一時 RT。使い終わったら RenderTexture.ReleaseTemporary すること</summary>
        internal static RenderTexture GetTemporary(int width, int height)
        {
            var rt = RenderTexture.GetTemporary(Descriptor(width, height));
            if (!rt.IsCreated()) rt.Create();
            return rt;
        }

        /// <summary>長く持つ RT（キャッシュ・呼び出し側に渡す結果）。使い終わったら Destroy すること</summary>
        internal static RenderTexture Create(int width, int height, string name)
        {
            var rt = new RenderTexture(Descriptor(width, height))
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        /// <summary>Create で作った RT を解放して破棄する（null は無視）</summary>
        internal static void Destroy(RenderTexture rt)
        {
            if (rt == null) return;
            // アクティブなまま破棄すると以後の描画・読み戻しが破棄済み RT を指すので外しておく（保険）
            if (RenderTexture.active == rt) RenderTexture.active = null;
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }
}
