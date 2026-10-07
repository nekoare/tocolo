using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// 対象アバターの編集の一覧。「Tocolo」パネルの「編集一覧」ボタンで開閉し、除外リストと同じ場所に出す（開くと除外リストは閉じる）。
    /// 行を押すとその編集を選ぶ。有効のチェックはその場で切り替える。
    /// 名前の入力欄・並べ替え・行ごとの削除は置かない（この幅に名前の入力欄を入れると名前が読めなくなり、
    /// 連結の削除の扱いも Inspector の一覧と二重に持つことになる。削除は選んだ後のパネルの「削除」で足りる）
    /// </summary>
    [Overlay(typeof(SceneView), Id, "Tocolo: 編集一覧")]
    internal sealed class EditListOverlay : IMGUIOverlay
    {
        public const string Id = "ClickRecolor/EditList";
        public const float Width = 260f;
        private const float ScrollBarWidth = 16f;
        /// <summary>Scene ビューの下に残す余白</summary>
        private const float ViewMargin = 12f;
        private const float RowHeight = 20f;
        /// <summary>中身が未測定のときの仮の高さ</summary>
        private const float EstimatedHeight = 60f;
        /// <summary>警告の印に使う、テクスチャの使われ方を取り直す間隔（秒）。マテリアルを全部たどるので描画のたびには取らない</summary>
        private const double UsedTexturesRefreshSeconds = 1.0;

        private static readonly Color SelectedRowColor = new Color(0.24f, 0.48f, 0.9f, 0.35f);

        private static float s_naturalHeight;
        /// <summary>直近の OnGUI での表示の高さ（倍率なし。スクロール中はスクロール領域の高さ）と倍率。小窓「表示切替」が重ならないよう避けるのに使う</summary>
        private static float s_shownHeight;
        private static float s_lastScale = 1f;
        private static Vector2 s_scroll;
        private static HashSet<Texture2D> s_usedTextures;
        private static GameObject s_usedTexturesRoot;
        private static double s_usedTexturesTime;

        /// <summary>保存レイアウトの復元より後にツールの状態へ合わせる（表示はボタンで開くまで OFF）</summary>
        public override void OnCreated()
        {
            EditorApplication.delayCall += () =>
            {
                // 除外リストと同じ場所に出すので、両方が開いた状態で復元されたらこちらを閉じる
                if (!RecolorSceneTool.IsActive || ExcludeListOverlay.IsShown(containerWindow as SceneView)) displayed = false;
                OverlayScaler.ClearUnitySizeOverride(this);
            };
        }

        /// <summary>ボタンからの開閉。開くと除外リストは閉じる。位置はパネルに追従して毎回置く</summary>
        internal static void Toggle(SceneView view) => PanelSideOverlay.Toggle(view, Id, Width);

        internal static bool IsShown(SceneView view) => PanelSideOverlay.IsShown(view, Id);

        /// <summary>表示中なら、Scene ビューの中での位置と大きさ（枠込みの概算）</summary>
        internal static bool TryGetShownRect(SceneView view, out Rect rect) =>
            PanelSideOverlay.TryGetShownRect(view, Id, Width * s_lastScale, (s_shownHeight > 0f ? s_shownHeight : EstimatedHeight) * s_lastScale, out rect);

        /// <summary>色を決めた編集の数。連結は 1 件、クリック直後の色未設定の編集は仮の状態なので数えない（Inspector の一覧の件数と同じ）</summary>
        internal static int CountDecided(ClickRecolor component)
        {
            if (component == null || component.edits == null) return 0;
            int count = 0;
            foreach (var e in component.edits)
            {
                if (e != null && e.IsKept && EditGroups.IsHead(component, e)) count++;
            }
            return count;
        }

        public override void OnGUI()
        {
            var view = containerWindow as SceneView;
            // 大きさはパネルの倍率に追従（自分のつまみは無し）。縦は小窓の上端から Scene ビューの下までに収め、収まらなければスクロールにする
            float scale = OverlayScaler.CurrentScale(this, ToolPanelOverlay.Id, Width);
            float maxHeight = float.MaxValue;
            if (view != null && floating)
            {
                maxHeight = (view.position.height - floatingPosition.y - PanelSideOverlay.FrameHeight - ViewMargin) / Mathf.Max(scale, 0.01f);
            }
            maxHeight = Mathf.Max(maxHeight, EstimatedHeight);
            float natural = s_naturalHeight > 0f ? s_naturalHeight : EstimatedHeight;
            bool scroll = natural > maxHeight;
            float shownHeight = scroll ? maxHeight : natural;

            var scope = OverlayScaler.Begin(this, Width, shownHeight, ToolPanelOverlay.Id);
            s_lastScale = scope.scale;
            s_shownHeight = shownHeight;
            if (Event.current.type == EventType.Layout) PanelSideOverlay.PlaceNextToPanel(view, this, Width * scope.scale);
            // スクロールにしても幅は変えない（変えると隣に置く位置がずれる）。スクロールバーのぶん中身を狭くする
            if (scroll) s_scroll = EditorGUILayout.BeginScrollView(s_scroll, false, true, GUILayout.Width(Width), GUILayout.Height(shownHeight));
            float contentWidth = scroll ? Width - ScrollBarWidth : Width;
            var rect = EditorGUILayout.BeginVertical(GUILayout.Width(contentWidth));
            if (Event.current.type == EventType.Repaint && rect.height > 0f)
            {
                if (Mathf.Abs(rect.height - s_naturalHeight) > 0.5f) containerWindow?.Repaint();
                s_naturalHeight = rect.height;
            }
            try
            {
                DrawContent(contentWidth);
            }
            finally
            {
                EditorGUILayout.EndVertical();
                if (scroll) EditorGUILayout.EndScrollView();
                OverlayScaler.End(this, scope);
            }
        }

        private static void DrawContent(float contentWidth)
        {
            if (!ToolSession.TryGetActiveRoot(out var root))
            {
                GUILayout.Label(Locales.Tr("Scene:Panel:PickTarget"), EditorStyles.wordWrappedLabel);
                return;
            }
            var component = root.GetComponent<ClickRecolor>();
            if (component == null || component.edits == null || component.edits.Count == 0)
            {
                GUILayout.Label(Locales.Tr("Scene:EditList:Empty"), EditorStyles.miniLabel);
                return;
            }
            GUILayout.Label(Locales.Tr("Scene:EditList:Caption"), EditorStyles.wordWrappedMiniLabel);
            RefreshUsedTextures(root);

            // 押された行・切り替えたチェックは、一覧を描き終えてから反映する（描画の途中で edits が変わると後ろの行がずれる）
            RecolorEdit select = null, toggled = null;
            bool toggledValue = false;
            foreach (var edit in component.edits)
            {
                if (edit == null || !EditGroups.IsHead(component, edit)) continue;
                DrawRow(component, edit, contentWidth, ref select, ref toggled, ref toggledValue);
            }

            if (toggled != null)
            {
                Undo.RecordObject(component, "Tocolo: 編集の有効を切り替え");
                bool value = toggledValue;
                EditGroups.ForEachInGroup(component, toggled, member => member.enabled = value);
                EditorUtility.SetDirty(component);
                SceneView.RepaintAll();
            }
            if (select != null) Select(component, select);
        }

        private static void RefreshUsedTextures(GameObject root)
        {
            double now = EditorApplication.timeSinceStartup;
            if (s_usedTextures != null && s_usedTexturesRoot == root && now - s_usedTexturesTime < UsedTexturesRefreshSeconds) return;
            // Layout と Repaint で中身が変わると IMGUI の並びが崩れるので、取り直すのは Layout のときだけ
            if (s_usedTextures != null && Event.current.type != EventType.Layout) return;
            s_usedTextures = Picking.MaterialTextureResolver.CollectMainTextures(root);
            s_usedTexturesRoot = root;
            s_usedTexturesTime = now;
        }

        /// <summary>1 行: 有効のチェック／警告の印／名前（連結ならテクスチャ数）／色の見本／種類。チェック以外を押すとその編集を選ぶ</summary>
        private static void DrawRow(ClickRecolor component, RecolorEdit edit, float contentWidth,
            ref RecolorEdit select, ref RecolorEdit toggled, ref bool toggledValue)
        {
            var row = GUILayoutUtility.GetRect(contentWidth, RowHeight, GUILayout.ExpandWidth(true));
            var members = EditGroups.Members(component, edit);
            // Ctrl＋クリックで種を足すと、選んでいる編集が連結の先頭でないメンバーに移ることがある
            bool current = false;
            foreach (var member in members)
            {
                if (member != null && member.id == ToolSession.CurrentEditId) current = true;
            }
            var e = Event.current;
            if (e.type == EventType.Repaint && current) EditorGUI.DrawRect(row, SelectedRowColor);

            const float gap = 4f;
            const float toggleWidth = 16f;
            const float iconWidth = 18f;
            const float swatchWidth = 28f;
            const float modeWidth = 36f;
            float line = EditorGUIUtility.singleLineHeight;
            float y = row.y + (row.height - line) * 0.5f;
            var toggleRect = new Rect(row.x + 2f, y, toggleWidth, line);
            var modeRect = new Rect(row.xMax - modeWidth, y, modeWidth, line);
            var swatchRect = new Rect(modeRect.x - gap - swatchWidth, y + 2f, swatchWidth, line - 4f);
            var nameRect = new Rect(toggleRect.xMax + gap, y, Mathf.Max(0f, swatchRect.x - gap - (toggleRect.xMax + gap)), line);

            EditorGUI.BeginChangeCheck();
            bool enabled = EditorGUI.Toggle(toggleRect, edit.enabled);
            if (EditorGUI.EndChangeCheck())
            {
                toggled = edit;
                toggledValue = enabled;
            }

            string warn = EditRowInfo.WarningTooltip(members, s_usedTextures);
            if (warn != null)
            {
                var iconRect = new Rect(nameRect.x, y, iconWidth, line);
                nameRect.xMin += iconWidth;
                EditRowInfo.DrawWarnIcon(iconRect, warn);
            }
            if (members.Count > 1)
            {
                var countContent = new GUIContent(Locales.Tr("Inspector:Edit:GroupCount", members.Count));
                float countWidth = Mathf.Min(EditorStyles.miniLabel.CalcSize(countContent).x, nameRect.width * 0.5f);
                GUI.Label(new Rect(nameRect.xMax - countWidth, y, countWidth, line), countContent, EditorStyles.miniLabel);
                nameRect.width = Mathf.Max(0f, nameRect.width - countWidth - gap);
            }
            string name = string.IsNullOrEmpty(edit.name) ? Locales.Tr("Scene:EditList:Unnamed") : edit.name;
            GUI.Label(nameRect, name, current ? EditorStyles.boldLabel : EditorStyles.label);

            if (edit.hasTarget)
            {
                var c = edit.targetColor;
                c.a = 1f;
                EditorGUI.DrawRect(swatchRect, c);
            }
            else
            {
                EditorGUI.DrawRect(swatchRect, Color.gray);
                GUI.Label(swatchRect, new GUIContent(string.Empty, Locales.Tr("Inspector:Edit:NoTarget")));
            }
            GUI.Label(modeRect, EditRowInfo.ModeLabel(edit), EditorStyles.miniLabel);

            // チェックは先に描いたので、チェックの上で押した MouseDown はチェックが使い終えている
            if (e.type == EventType.MouseDown && e.button == 0 && row.Contains(e.mousePosition) && !toggleRect.Contains(e.mousePosition))
            {
                select = edit;
                e.Use();
            }
        }

        /// <summary>Scene での再クリックと同じく、別の編集に切り替えるときは色未設定の仮の編集を捨ててから選ぶ</summary>
        private static void Select(ClickRecolor component, RecolorEdit edit)
        {
            // 連結の中の編集を選んでいれば選び直さない（パネルは連結全体に効く）
            foreach (var member in EditGroups.Members(component, edit))
            {
                if (member != null && member.id == ToolSession.CurrentEditId) return;
            }
            RecolorSceneTool.DiscardPendingEdit();
            ToolSession.CurrentEditId = edit.id;
            SceneView.RepaintAll();
        }
    }
}
