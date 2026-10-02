using System;
using nadena.dev.ndmf;
using Nekoare.ClickRecolor.Editor.Localization;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    /// <summary>
    /// アップロード（NDMF ビルド）時に色変えを反映するパス（設計 §9）。
    /// 焼き込みの実装は有料版にあり、RecolorBuildHook.Bake に登録されていればそれを呼ぶ。
    /// 登録が無い（無料版）ときは、編集のあるアバターに「アップロードには反映されない」旨を NonFatal で 1 回出すだけで、
    /// コンポーネントも編集も変えない（コンポーネントは RemoveComponentsPass が消すので、アップロードは失敗しない）
    /// </summary>
    public class RecolorPass : Pass<RecolorPass>
    {
        private const string LogPrefix = "[Tocolo]";

        public override string DisplayName => "ClickRecolor: Recolor";

        protected override void Execute(BuildContext context)
        {
            var bake = RecolorBuildHook.Bake;
            int total = ExecuteCore(
                context.AvatarRootObject,
                bake != null ? () => bake(context) : (Func<int>)null);
            if (total > 0) Debug.Log($"{LogPrefix} {total}枚のテクスチャに反映");
        }

        /// <summary>
        /// BuildContext 無しで呼べる seam（テストから使う）。bake があればそれを呼んで焼いた枚数を返す。
        /// null（無料版）なら、有効な編集のあるコンポーネントの先頭を参照にして Error:FreeEdition を NonFatal で 1 回報告し、0 を返す
        /// </summary>
        internal static int ExecuteCore(GameObject avatarRoot, Func<int> bake)
        {
            if (bake != null) return bake();
            if (avatarRoot == null) return 0;

            var component = FindFirstWithEnabledEdit(avatarRoot);
            if (component != null)
            {
                ErrorReport.ReportError(Locales.L, ErrorSeverity.NonFatal, "Error:FreeEdition", component);
            }
            return 0;
        }

        /// <summary>
        /// 有効な編集（有効・目標色あり・対象テクスチャあり）の数。applyOnBuild が false のコンポーネントは数えない。
        /// BuildContext 無しで呼べる seam。テストから使う
        /// </summary>
        internal static int CountEnabledEdits(GameObject avatarRoot)
        {
            int count = 0;
            foreach (var component in avatarRoot.GetComponentsInChildren<ClickRecolor>(true))
            {
                if (component == null || !component.applyOnBuild || component.edits == null) continue;
                foreach (var edit in component.edits)
                {
                    if (RecolorPreview.IsPreviewTarget(edit)) count++;
                }
            }
            return count;
        }

        /// <summary>CountEnabledEdits と同じ条件で、有効な編集を 1 つ以上持つ最初のコンポーネント。無ければ null</summary>
        private static ClickRecolor FindFirstWithEnabledEdit(GameObject avatarRoot)
        {
            foreach (var component in avatarRoot.GetComponentsInChildren<ClickRecolor>(true))
            {
                if (component == null || !component.applyOnBuild || component.edits == null) continue;
                foreach (var edit in component.edits)
                {
                    if (RecolorPreview.IsPreviewTarget(edit)) return component;
                }
            }
            return null;
        }
    }
}
