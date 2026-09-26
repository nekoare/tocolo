// 流用元: dev.nekoare.uv-side-splitter/Editor/MaterialOps/MaterialCloner.cs
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    internal static class MaterialCloner
    {
        /// <summary>
        /// マテリアルを複製する。new Material(source) ではシェーダキーワードが
        /// 落ちることがあり、keyword 依存のシェーダで variant 不一致
        /// （ピンク表示）を起こすため、Instantiate したうえで明示的にコピーする。
        /// </summary>
        public static Material Clone(Material source, string nameSuffix)
        {
            if (source == null) return null;

            var clone = Object.Instantiate(source);
            clone.shader = source.shader;
            clone.shaderKeywords = source.shaderKeywords;
            clone.renderQueue = source.renderQueue;
            clone.name = source.name + nameSuffix;
            return clone;
        }
    }
}
