using System;
using System.Collections.Generic;
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
            var scoped = RecolorBuildHook.BakeScoped;
            var legacy = RecolorBuildHook.Bake;
            Func<int> bake = scoped != null ? () => scoped(context, RecolorScope.Main)
                : legacy != null ? () => legacy(context)
                : (Func<int>)null;
            if (bake != null) WarnUnsupportedFeatures(context.AvatarRootObject, RecolorBuildHook.SupportedFeatures);
            int total = ExecuteCore(context.AvatarRootObject, bake);
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
        /// 有料版が対応していない機能の編集があれば機能ごとに NonFatal で 1 回警告し、警告したら true（無料版だけ更新されて有料版が古いとき）。
        /// 「なめらかに貼る」（重ね貼り）: 古い有料版は重ね貼りを知らず焼き込みに回すが、無料版はその層を素通しするので画像が消える。
        /// 髪ツールの髪の上: 古い有料版は髪も本体の入口（髪ツールより前）で焼くので、プレビュー（髪ツールの上）と違う色になる
        /// </summary>
        internal static bool WarnUnsupportedFeatures(GameObject avatarRoot, int supportedFeatures) =>
            WarnUnsupportedFeatures(avatarRoot, supportedFeatures, HairToolTargets.OrderSupported);

        /// <summary>WarnUnsupportedFeatures の、髪ツールの宣言の有無を渡せる版（テスト用）</summary>
        internal static bool WarnUnsupportedFeatures(GameObject avatarRoot, int supportedFeatures, bool orderSupported)
        {
            if (avatarRoot == null) return false;
            bool warned = false;
            if ((supportedFeatures & RecolorBuildHook.FeatureDecalOverlay) == 0)
            {
                var component = FindFirstWithOverlayEdit(avatarRoot);
                if (component != null)
                {
                    ErrorReport.ReportError(Locales.L, ErrorSeverity.NonFatal, "Error:ProOutdated", component);
                    warned = true;
                }
            }
            if (orderSupported && (supportedFeatures & RecolorBuildHook.FeatureChmHair) == 0)
            {
                var component = FindFirstWithHairEdit(avatarRoot, orderSupported);
                if (component != null)
                {
                    ErrorReport.ReportError(Locales.L, ErrorSeverity.NonFatal, "Error:ProOutdatedHair", component);
                    warned = true;
                }
            }
            return warned;
        }

        private static ClickRecolor FindFirstWithOverlayEdit(GameObject avatarRoot)
        {
            foreach (var component in avatarRoot.GetComponentsInChildren<ClickRecolor>(true))
            {
                if (component == null || !component.applyOnBuild || component.edits == null) continue;
                foreach (var edit in component.edits)
                {
                    if (RecolorPreview.IsPreviewTarget(edit) && Decal.DecalOverlayMaterial.UseOverlay(component, edit)) return component;
                }
            }
            return null;
        }

        /// <summary>効く編集の対象テクスチャを、髪用の入口が担当する髪（役割 Over）が使っているコンポーネントの先頭。無ければ null</summary>
        private static ClickRecolor FindFirstWithHairEdit(GameObject avatarRoot, bool orderSupported)
        {
            var hairTextures = new HashSet<Texture2D>();
            foreach (var pair in HairToolTargets.RolesIn(avatarRoot, orderSupported))
            {
                if (pair.Key == null || pair.Value != HairToolRole.Over) continue;
                foreach (var material in pair.Key.sharedMaterials)
                {
                    if (Picking.MaterialTextureResolver.TryGetMainTexture(material, out var info)) hairTextures.Add(info.texture);
                }
            }
            if (hairTextures.Count == 0) return null;
            foreach (var component in avatarRoot.GetComponentsInChildren<ClickRecolor>(true))
            {
                if (component == null || !component.applyOnBuild || component.edits == null) continue;
                foreach (var edit in component.edits)
                {
                    if (RecolorPreview.IsPreviewTarget(edit) && hairTextures.Contains(edit.sourceTexture)) return component;
                }
            }
            return null;
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
