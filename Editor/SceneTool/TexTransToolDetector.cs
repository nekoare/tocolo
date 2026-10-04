using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// アバターに有効な TexTransTool のコンポーネントがあるか（型の名前空間で見る。TexTransTool にコンパイル時に依存しないため）。
    /// TexTransTool はプレビューで上流の RenderTexture を下地として読み込めず（形式・縮小版の段数が違うとコピーに失敗する）、
    /// 本ツールで色を変えたマテリアルには TexTransTool の効果がプレビューで乗らない。アップロード時は TexTransTool の効果が上に掛かるので、
    /// クリックで選んだときに案内する（ユーザー判断 2026-10-04。この案内に限って TexTransTool を名指しする）
    /// </summary>
    internal static class TexTransToolDetector
    {
        private const string NamespacePrefix = "net.rs64.TexTransTool";

        /// <summary>root 配下（非アクティブは除く）に、有効な TexTransTool のコンポーネントがあるか</summary>
        internal static bool HasActive(GameObject root)
        {
            if (root == null) return false;
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(false))
            {
                if (behaviour == null || !behaviour.enabled) continue;
                var ns = behaviour.GetType().Namespace;
                if (ns != null && ns.StartsWith(NamespacePrefix, System.StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
