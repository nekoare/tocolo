using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// 操作の案内（Shift／Ctrl／矩形）と終了ボタン。Scene の右下に固定して置く。
    /// 表示の切り替えと位置合わせは RecolorSceneTool が行う
    /// </summary>
    [Overlay(typeof(SceneView), Id, "Tocolo: 操作")]
    internal sealed class HintOverlay : IMGUIOverlay
    {
        public const string Id = "ClickRecolor/Hints";
        public const float Width = 260f;
        /// <summary>直近の Repaint で測った中身の高さ（px）。RecolorSceneTool が右下に置くときの高さに使う。0 なら未測定</summary>
        internal static float LastContentHeight;
        /// <summary>直近の Repaint での表示幅（倍率込み）。0 なら未測定（基準幅 Width を使う）</summary>
        internal static float LastContentWidth;

        /// <summary>対象 root に現在の編集（選択点）があるか</summary>
        private static bool HasCurrentEdit(GameObject root)
        {
            var component = root != null ? root.GetComponent<ClickRecolor>() : null;
            return component != null && component.FindEdit(ToolSession.CurrentEditId) != null;
        }

        /// <summary>対象 root の現在の編集が画像入り（HasDecal）か</summary>
        private static bool CurrentEditHasDecal(GameObject root)
        {
            var component = root != null ? root.GetComponent<ClickRecolor>() : null;
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            return edit != null && edit.HasDecal;
        }

        /// <summary>ToolPanelOverlay と同じく、保存レイアウトの復元より後にツールの状態へ合わせる</summary>
        public override void OnCreated()
        {
            EditorApplication.delayCall += () => displayed = RecolorSceneTool.IsActive;
            OverlayScaler.ClearUnitySizeOverride(this);
        }

        /// <summary>中身の素の高さ（倍率 1 のとき）</summary>
        private static float s_naturalHeight;

        public override void OnGUI()
        {
            // 中身は基準幅で描き、リサイズの幅に応じて GUI.matrix で拡大する（比率を保つ。ToolPanelOverlay と同じ）
            var scope = OverlayScaler.Begin(this, Width, s_naturalHeight > 0f ? s_naturalHeight : 100f);
            var rect = EditorGUILayout.BeginVertical(GUILayout.Width(Width));
            // 中身の高さは対象の候補数などで変わるので実測して、右下から見切れないよう置き直してもらう（ユーザー報告 2026-09-25）
            if (Event.current.type == EventType.Repaint && rect.height > 0f)
            {
                // 中身の高さが変わった（対象の行が増えた等）ら、固定している高さを次の描画で合わせるために描き直す
                // （描き直しが無いと 1 フレーム前の高さのままで下のボタンが見切れる。ユーザー報告 2026-09-26）
                if (Mathf.Abs(rect.height - s_naturalHeight) > 0.5f) containerWindow?.Repaint();
                s_naturalHeight = rect.height;
                LastContentHeight = rect.height * scope.scale;
                LastContentWidth = Width * scope.scale;
            }
            try
            {
                // 言語の切り替え（Inspector から移した。ユーザー要望 2026-10-02）。NDMF の言語設定と共通
                Locales.DrawLanguagePicker();
                if (ToolSession.IsInPrefabMode)
                {
                    // Prefab 編集モード中は操作できないので、終了ボタンだけ
                    if (GUILayout.Button(Locales.Tr("Scene:Panel:EscHint"), GUILayout.Height(26))) RecolorSceneTool.Deactivate();
                    return;
                }
                bool hasRoot = ToolSession.TryGetActiveRoot(out var root);
                // 現在の編集（選択点）があるかで文言を変える: 無ければ「選択」、1 点でもあれば「追加」（ユーザー要望 2026-09-26）
                bool hasEdit = hasRoot && HasCurrentEdit(root);
                // 編集済みの範囲の再クリックはその編集を選ぶので、新しく作る方法を案内する
                GUILayout.Label(Locales.Tr("Scene:Panel:ShiftHint"), EditorStyles.miniLabel);
                // Shift の行と Ctrl の行の間を 1 行空ける（ユーザー要望 2026-09-26）
                EditorGUILayout.Space(EditorGUIUtility.singleLineHeight * 0.8f);
                // 現在の編集にパーツを足す／外す方法
                GUILayout.Label(Locales.Tr(hasEdit ? "Scene:Panel:CtrlHint" : "Scene:Panel:CtrlHint:Select"), EditorStyles.miniLabel);
                // 矩形でまとめて足す方法（矩形選択はパーツモード専用なので、そのときだけ案内する）
                if (ToolSession.Mode == SelectionMode.Island)
                {
                    GUILayout.Label(Locales.Tr(hasEdit ? "Scene:Panel:RectHint" : "Scene:Panel:RectHint:Select"), EditorStyles.miniLabel);
                }
                // 画像入りの編集を選んでいる間は、画像をつかんで動かせることを案内する
                if (hasRoot && CurrentEditHasDecal(root)) GUILayout.Label(Locales.Tr("Scene:Panel:DecalDragHint"), EditorStyles.miniLabel);
                EditorGUILayout.Space(4);
                // 対象の表示・切り替え・候補のボタンは ToolPanelOverlay（「範囲」の上）に置く（ユーザー要望 2026-09-26）
                // 終了はボタンでも Esc でもできる
                if (GUILayout.Button(Locales.Tr("Scene:Panel:EscHint"), GUILayout.Height(26))) RecolorSceneTool.Deactivate();
            }
            finally
            {
                EditorGUILayout.EndVertical();
                OverlayScaler.End(this, scope);
            }
        }
    }
}
