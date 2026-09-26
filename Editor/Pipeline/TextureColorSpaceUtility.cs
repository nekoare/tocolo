// 流用元: dev.nekoare.tex-col-adjuster/Editor/TextureColorSpaceUtility.cs（読み込みで使う部分だけ）
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Pipeline
{
    /// <summary>
    /// テクスチャが sRGB として扱われるかを判定する。
    /// 実行時に作ったテクスチャはインポーターが無いので、作成時の指定を覚えておいて引く
    /// </summary>
    internal static class TextureColorSpaceUtility
    {
        private sealed class RuntimeTextureInfo
        {
            public bool IsSrgb;
        }

        // テクスチャが破棄されたら自動で消える弱参照の表
        private static readonly ConditionalWeakTable<Texture, RuntimeTextureInfo> s_runtimeTextureColorSpace =
            new ConditionalWeakTable<Texture, RuntimeTextureInfo>();

        internal static void RegisterRuntimeTexture(Texture texture, bool isSrgb)
        {
            if (texture == null) return;
            s_runtimeTextureColorSpace.Remove(texture);
            s_runtimeTextureColorSpace.Add(texture, new RuntimeTextureInfo { IsSrgb = isSrgb });
        }

        internal static void UnregisterRuntimeTexture(Texture texture)
        {
            if (texture == null) return;
            s_runtimeTextureColorSpace.Remove(texture);
        }

        /// <summary>
        /// テクスチャが sRGB か。実行時テクスチャは登録値、RenderTexture は sRGB フラグ、資産はインポーターの sRGBTexture。
        /// どれでも分からなければ sRGB とみなす（色テクスチャの既定に合わせる）
        /// </summary>
        internal static bool IsTextureSRGB(Texture texture)
        {
            if (texture == null) return false;

            if (s_runtimeTextureColorSpace.TryGetValue(texture, out var info)) return info.IsSrgb;

            if (texture is RenderTexture renderTexture) return renderTexture.sRGB;

            string path = AssetDatabase.GetAssetPath(texture);
            if (!string.IsNullOrEmpty(path) && AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                return importer.sRGBTexture;
            }

            return true;
        }

        /// <summary>色空間を登録した実行時テクスチャを作る（linear 引数は isSrgb の逆）</summary>
        internal static Texture2D CreateRuntimeTexture(int width, int height, TextureFormat format, bool mipChain, bool isSrgb)
        {
            var texture = new Texture2D(width, height, format, mipChain, !isSrgb);
            RegisterRuntimeTexture(texture, isSrgb);
            return texture;
        }
    }
}
