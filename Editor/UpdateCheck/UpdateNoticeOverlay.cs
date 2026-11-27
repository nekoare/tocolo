using Nekoare.ClickRecolor.Editor.Inspector;
using Nekoare.ClickRecolor.Editor.Localization;
using Nekoare.ClickRecolor.Editor.SceneTool;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.UpdateCheck
{
    /// <summary>
    /// ツール使用中に、お知らせを使うかの確認か、新しい版のお知らせを出す小窓。用が無いときは出さない（SyncDisplayed）。
    /// 位置は「操作」の小窓のすぐ上（RecolorSceneTool が置く）
    /// </summary>
    [Overlay(typeof(SceneView), Id, "Tocolo: 更新のお知らせ")]
    internal sealed class UpdateNoticeOverlay : IMGUIOverlay
    {
        public const string Id = "ClickRecolor/UpdateNotice";
        /// <summary>基準の幅（「操作」の小窓と同じ）。大きさは自分のつまみで変えられ、「操作」とは別に覚える</summary>
        public const float Width = HintOverlay.Width;

        private const string ChangelogUrl = "https://tocolo-docs.github.io/changelog";
        // 説明書の「通信の詳しい内容」の見出し（# 以降を URL エンコードしたもの。見出しの文言を変えたらここも直す）
        private const string DetailsUrl = "https://tocolo-docs.github.io/#%E9%80%9A%E4%BF%A1%E3%81%AE%E8%A9%B3%E3%81%97%E3%81%84%E5%86%85%E5%AE%B9";

        /// <summary>
        /// ［閉じる］を押したか。次にツールを始めるまでだけ覚える（保存しない）。
        /// オーバーレイの「隠す」はレイアウトに残り続け、新しい版が出ても気づけなくなるため、自前のボタンで引っ込める
        /// </summary>
        internal static bool Dismissed;

        /// <summary>直近の Repaint で測った中身の高さ・幅（倍率込み、px）。0 なら未測定</summary>
        internal static float LastContentHeight;
        internal static float LastContentWidth;

        private static float s_naturalHeight;
        /// <summary>Layout で決めた中身の種類（Layout と次のイベントの間で変わると IMGUI の並びが崩れるため、Layout のときだけ決める）</summary>
        private static UpdateNoticeKind s_shownKind;

        internal static UpdateNoticeKind CurrentKind() => UpdateCheckLogic.Decide(
            UpdateChecker.CurrentVersion, UpdateChecker.HasChosen, UpdateChecker.Enabled, UpdateChecker.Requesting,
            UpdateChecker.LatestVersion, UpdateChecker.SkippedVersion, Dismissed);

        public override void OnCreated()
        {
            EditorApplication.delayCall += () =>
            {
                if (!RecolorSceneTool.IsActive) displayed = false;
                OverlayScaler.ClearUnitySizeOverride(this);
                OverlayTitle.Bind(this, "Scene:Overlay:UpdateNotice");
            };
        }

        /// <summary>ツール使用中で、出す物があるときだけ出す。RecolorSceneTool の Layout で毎回呼ぶ（変わったときだけ書く）</summary>
        internal static bool SyncDisplayed(SceneView view)
        {
            if (view == null || !view.TryGetOverlay(Id, out Overlay overlay)) return false;
            bool want = RecolorSceneTool.IsActive && CurrentKind() != UpdateNoticeKind.None;
            if (overlay.displayed != want) overlay.displayed = want;
            return want;
        }

        public override void OnGUI()
        {
            ToolSession.DropPickToolsOnEscape(Event.current);
            if (Event.current.type == EventType.Layout) s_shownKind = CurrentKind();
            var scope = OverlayScaler.Begin(this, Width, s_naturalHeight > 0f ? s_naturalHeight : 80f);
            var rect = EditorGUILayout.BeginVertical(GUILayout.Width(Width));
            if (Event.current.type == EventType.Repaint && rect.height > 0f)
            {
                if (Mathf.Abs(rect.height - s_naturalHeight) > 0.5f) containerWindow?.Repaint();
                s_naturalHeight = rect.height;
                LastContentHeight = rect.height * scope.scale;
                // 自分のつまみの分も含めた幅（右端に見切れないよう置くのに使う）
                LastContentWidth = scope.outer.width;
            }
            try
            {
                if (s_shownKind == UpdateNoticeKind.Consent) DrawConsent();
                else if (s_shownKind == UpdateNoticeKind.Update) DrawNotice();
            }
            finally
            {
                EditorGUILayout.EndVertical();
                OverlayScaler.End(this, scope);
            }
        }

        // ［使う］［使わない］は同じ見た目にする。片方だけ目立たせて選ばせることはしない
        private static void DrawConsent()
        {
            EditorGUILayout.HelpBox(Locales.Tr("UpdateNotice:ConsentMessage"), MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(Locales.Tr("UpdateNotice:ConsentAccept"))) UpdateChecker.Choose(true);
                if (GUILayout.Button(Locales.Tr("UpdateNotice:ConsentDecline"))) UpdateChecker.Choose(false);
            }
            if (GUILayout.Button(Locales.Tr("UpdateNotice:ConsentDetails"))) Application.OpenURL(DetailsUrl);
        }

        private static void DrawNotice()
        {
            var current = UpdateChecker.CurrentVersion;
            var latest = UpdateChecker.LatestVersion;
            // Layout で決めた後に状態が変わっても、このコマは並びを崩さないよう空の枠だけ描く
            if (current == null || latest == null) return;

            bool major = UpdateCheckLogic.IsMajorUpdate(current.Value, latest.Value);
            // 有料版は本体と必ず同じ版で出すので、本体の版だけで両方の更新を案内できる
            bool pro = InspectorExtensions.DrawExportSection != null;
            var message = Locales.Tr(pro ? "UpdateNotice:AvailablePro" : "UpdateNotice:Available", latest.Value, current.Value);
            if (major) message += "\n" + Locales.Tr("UpdateNotice:Major");

            EditorGUILayout.HelpBox(message, major ? MessageType.Warning : MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(Locales.Tr("UpdateNotice:Changelog"))) Application.OpenURL(ChangelogUrl);
                // 小窓を引っ込めるのは Scene 側の次の Layout なので、待たずに描き直させる（空の枠が残らないように）
                if (GUILayout.Button(Locales.Tr("UpdateNotice:Skip"))) { UpdateChecker.SkippedVersion = latest; SceneView.RepaintAll(); }
                if (GUILayout.Button(Locales.Tr("UpdateNotice:Close"), GUILayout.ExpandWidth(false))) { Dismissed = true; SceneView.RepaintAll(); }
            }
        }
    }
}
