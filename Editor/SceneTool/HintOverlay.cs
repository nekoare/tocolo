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

        /// <summary>ToolPanelOverlay と同じく、保存レイアウトの復元より後にツールの状態へ合わせる</summary>
        public override void OnCreated() { EditorApplication.delayCall += () => displayed = RecolorSceneTool.IsActive; }

        public override void OnGUI()
        {
            var rect = EditorGUILayout.BeginVertical(GUILayout.Width(Width));
            // 中身の高さは対象の候補数などで変わるので実測して、右下から見切れないよう置き直してもらう（ユーザー報告 2026-09-25）
            if (Event.current.type == EventType.Repaint && rect.height > 0f) LastContentHeight = rect.height;
            try
            {
                if (ToolSession.IsInPrefabMode)
                {
                    // Prefab 編集モード中は操作できないので、終了ボタンだけ
                    if (GUILayout.Button(Locales.Tr("Scene:Panel:EscHint"), GUILayout.Height(26))) RecolorSceneTool.Deactivate();
                    return;
                }
                // 編集済みの範囲の再クリックはその編集を選ぶので、新しく作る方法を案内する
                GUILayout.Label(Locales.Tr("Scene:Panel:ShiftHint"), EditorStyles.miniLabel);
                // 現在の編集に島を足す／外す方法
                GUILayout.Label(Locales.Tr("Scene:Panel:CtrlHint"), EditorStyles.miniLabel);
                // 矩形でまとめて足す方法
                // 矩形選択は UV アイランドモード専用なので、そのときだけ案内する
                if (ToolSession.Mode == SelectionMode.Island) GUILayout.Label(Locales.Tr("Scene:Panel:RectHint"), EditorStyles.miniLabel);
                EditorGUILayout.Space(4);
                // 対象の表示と「対象を変える」（ToolPanelOverlay から移動）。対象未設定のときは候補のボタンをここに出す（ユーザー要望 2026-09-25）
                if (ToolSession.TryGetActiveRoot(out var root))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(Locales.Tr("Scene:Panel:Target", root.name), EditorStyles.wordWrappedLabel);
                        if (GUILayout.Button(Locales.Tr("Scene:Panel:ChangeTarget"), GUILayout.ExpandWidth(false))) ToolPanelOverlay.ChangeTarget();
                    }
                }
                else
                {
                    GUILayout.Label(Locales.Tr("Scene:Panel:PickTarget"), EditorStyles.wordWrappedLabel);
                    ToolPanelOverlay.DrawTargetButtons();
                }
                // 終了はボタンでも Esc でもできる
                if (GUILayout.Button(Locales.Tr("Scene:Panel:EscHint"), GUILayout.Height(26))) RecolorSceneTool.Deactivate();
            }
            finally
            {
                EditorGUILayout.EndVertical();
            }
        }
    }
}
