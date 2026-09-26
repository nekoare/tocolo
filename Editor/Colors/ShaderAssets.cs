using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Colors
{
    /// <summary>
    /// パッケージ同梱のシェーダーアセットを固定 GUID から読み込む。
    /// 名前検索（FindAssets）は同名ファイルを拾う恐れがあるので使わない（Locales と同じ流儀）。
    /// GUID は各ファイルに同梱した .meta と一致させること。
    /// </summary>
    internal static class ShaderAssets
    {
        /// <summary>Editor/Shader/OklabConverter.hlsl（compute から相対パスで include する。ここで読み込むことはない）</summary>
        internal const string OklabHlslGuid = "4f47e96b123b42878421bb0e356b8fdf";

        /// <summary>Editor/Shader/Rasterize.compute（UV 三角形をマスクへ塗る）</summary>
        internal const string RasterizeGuid = "6a8fdf7c775b4d80ba21594daac653d5";

        /// <summary>Editor/Shader/Morphology.compute（膨張・収縮・チャート意識パディング・ぼかし・積・最大値）</summary>
        internal const string MorphologyGuid = "c7a80eefae7949009186834d685f613d";

        /// <summary>Editor/Shader/ColorShift.compute（選択マスクの内側を目標色へ寄せる）</summary>
        internal const string ColorShiftGuid = "92aa2394e44840349e61db8ccd8d7687";

        /// <summary>Editor/Shader/ColorSelect.compute（色モードの選択: 種色との Oklab 距離・二値化しながらの縮小）</summary>
        internal const string ColorSelectGuid = "0dfe400d19e0439788e526f6930130f3";

        /// <summary>Editor/Shader/Highlight.compute（選択範囲のハイライト。プレビュー専用）</summary>
        internal const string HighlightGuid = "fe1c8fbd15fb463d873ec521d81aa203";

        /// <summary>Editor/Shader/IslandId.shader（Ctrl＋ドラッグの矩形選択で、見えている三角形の番号を描く。compute ではない通常のシェーダー）</summary>
        internal const string IslandIdGuid = "4c224652750740a2b3bd851dd3b28c34";

        /// <summary>Editor/Shader/PositionMap.shader（グラデーション用に、UV 空間の各画素へ表面の位置を描く。compute ではない通常のシェーダー）</summary>
        internal const string PositionMapGuid = "dddbdbf0127d497995cd270f39e8f806";

        /// <summary>Editor/Shader/PositionFill.compute（位置マップの描かれていない画素を近傍の位置で埋める膨張）</summary>
        internal const string PositionFillGuid = "21d33dd65755493484f296e2a3f03e8b";

        /// <summary>Editor/Icons/click-recolor-tool.png（ツールバーのアイコン 16×16。シェーダーではないが固定 GUID の同梱アセットなのでここに置く）</summary>
        internal const string IconGuid = "ebc1a884da954d1cb9d18887a57d60fa";

        /// <summary>Editor/Icons/click-recolor-tool@2x.png（高 DPI 用 32×32。LoadAssetAtPath は @2x を自動で選ばないので呼び出し側で選ぶ）</summary>
        internal const string Icon2xGuid = "767da2d807194ff3897aa66151599e1f";

        // 警告を出し済みの GUID。解決できないたびに警告が並ばないよう、GUID ごとに 1 回だけ出す
        private static readonly HashSet<string> WarnedGuids = new HashSet<string>();

        /// <summary>
        /// GUID から ComputeShader を読み込む。解決できなければ null を返し、警告は GUID ごとに 1 回だけ出す。
        /// </summary>
        internal static ComputeShader Load(string guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var shader = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            if (shader == null && WarnedGuids.Add(guid))
            {
                Debug.LogWarning($"[Tocolo] シェーダーが読み込めません（GUID: {guid}）。.meta の GUID を確認してください");
            }
            return shader;
        }

        /// <summary>
        /// GUID から通常のシェーダー（Shader）を読み込む。解決できなければ null を返し、警告は GUID ごとに 1 回だけ出す（Load と同じ）
        /// </summary>
        internal static Shader LoadShader(string guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var shader = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null && WarnedGuids.Add(guid))
            {
                Debug.LogWarning($"[Tocolo] シェーダーが読み込めません（GUID: {guid}）。.meta の GUID を確認してください");
            }
            return shader;
        }
    }
}
