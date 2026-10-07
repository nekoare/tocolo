using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// Scene の表示を切り替える小窓「表示切替」。一番上の［選択範囲］は、今の編集で色が変わる範囲の縞（ツール全体の設定）。
    /// その下は箱を選ぶボタン（「箱の中」の範囲・グラデーション・画像の箱やシールのつまみ。複数選択可）で、押した物だけ出して動かせ、押していない物は隠す。
    /// 箱が同じ位置に重なって分かりにくかったため、パネルの各ブロックの「箱を表示」をここにまとめた（ユーザー要望 2026-10-05）。
    /// 現在の編集があるときだけ出し（SyncDisplayed）、位置はパネルの隣のブロックの高さに置く（除外リストと同じくパネルに付いて動く）
    /// </summary>
    [Overlay(typeof(SceneView), Id, "Tocolo: 表示切替")]
    internal sealed class BoxToggleOverlay : IMGUIOverlay
    {
        public const string Id = "ClickRecolor/BoxToggle";
        public const float Width = 130f;
        /// <summary>パネルとの横の間隔（実際の幅で並べるので、ほぼ引っ付ける）</summary>
        private const float SideGap = 2f;
        /// <summary>除外リストを避けて下へずらすときの縦の間隔</summary>
        private const float Gap = 12f;
        /// <summary>オーバーレイの枠（見出しと余白）が中身に足す高さの概算（ToolPanelOverlay と同じ値）</summary>
        private const float FrameHeight = 26f;
        /// <summary>Scene ビューの下に残す余白（ToolPanelOverlay と同じ値）</summary>
        private const float ViewMargin = 12f;
        /// <summary>色の印（ボタンの左の四角）の大きさ</summary>
        private const float MarkSize = 10f;

        private static float s_naturalHeight;

        /// <summary>保存レイアウトの復元より後にツールの状態へ合わせる（表示は SyncDisplayed が決める）</summary>
        public override void OnCreated()
        {
            EditorApplication.delayCall += () =>
            {
                if (!RecolorSceneTool.IsActive) displayed = false;
                OverlayScaler.ClearUnitySizeOverride(this);
            };
        }

        /// <summary>現在の編集があるときだけ出す（編集が無ければ縞で見せる範囲も箱も無い）。RecolorSceneTool の Layout で毎回呼ぶ（変わったときだけ書く）</summary>
        internal static void SyncDisplayed(SceneView view)
        {
            if (view == null || !view.TryGetOverlay(Id, out Overlay overlay)) return;
            bool want = RecolorSceneTool.IsActive && !ToolSession.IsInPrefabMode && TryGetCurrentEdit(out _);
            if (overlay.displayed != want) overlay.displayed = want;
        }

        private static bool TryGetCurrentEdit(out RecolorEdit edit)
        {
            edit = null;
            if (!ToolSession.TryGetActiveRoot(out var root)) return false;
            var component = root.GetComponent<ClickRecolor>();
            edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            return edit != null;
        }

        /// <summary>
        /// パネルの左隣（左に余白が無ければ右隣。除外リストと同じ）、縦はパネルのグラデーション・画像のブロックの高さに置く。
        /// 除外リスト・編集一覧と重なるならその下へ（下に入らなければ外側の隣へ）ずらし、Scene ビューの下に見切れないよう収める
        /// </summary>
        private static void PlaceNextToPanel(SceneView view, Overlay overlay, float ownWidth, float ownHeight)
        {
            try
            {
                if (view == null || !view.TryGetOverlay(ToolPanelOverlay.Id, out Overlay panel) || !panel.floating) return;
                overlay.Undock();
                // パネルを小さくすると、見出しの文字幅のほうが中身より広くなる。その幅で離さないとパネルに食い込む
                ownWidth = OverlayScaler.ActualWidth(overlay, ownWidth);
                float panelWidth = ToolPanelOverlay.LastContentWidth > 0f ? ToolPanelOverlay.LastContentWidth : ToolPanelOverlay.PanelWidth;
                var p = panel.floatingPosition;
                float x = p.x - ownWidth - SideGap;
                if (x < 0f) x = p.x + panelWidth + SideGap;
                float y = p.y + Mathf.Max(ToolPanelOverlay.BoxBlocksOffsetY, 0f);
                float viewHeight = view.position.height;
                if (PanelSideOverlay.TryGetAnyShownRect(view, out var side) && new Rect(x, y, ownWidth, ownHeight).Overlaps(side))
                {
                    // 編集一覧は Scene ビューの下まで伸びることがあり、その下には入らない。そのときは小窓のさらに外側の隣へ
                    float below = side.yMax + Gap;
                    float outerX = side.x < p.x ? side.x - ownWidth - SideGap : side.xMax + SideGap;
                    bool fitsBelow = viewHeight <= 0f || below + ownHeight + ViewMargin <= viewHeight;
                    bool fitsOuter = outerX >= 0f && outerX + ownWidth <= view.position.width;
                    if (!fitsBelow && fitsOuter) x = outerX;
                    else y = below;
                }
                if (viewHeight > 0f) y = Mathf.Min(y, viewHeight - ownHeight - ViewMargin);
                y = Mathf.Max(y, ToolPanelOverlay.TopMargin);
                var target = new Vector2(x, y);
                if ((overlay.floatingPosition - target).sqrMagnitude > 0.25f) overlay.floatingPosition = target;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Tocolo] 箱の小窓の位置を設定できませんでした: {e.Message}");
            }
        }

        public override void OnGUI()
        {
            // 大きさはパネルの倍率に追従（自分のつまみは無し）。位置は毎回パネルの隣に置き直す
            float height = s_naturalHeight > 0f ? s_naturalHeight : 44f;
            var scope = OverlayScaler.Begin(this, Width, height, ToolPanelOverlay.Id);
            if (Event.current.type == EventType.Layout)
            {
                PlaceNextToPanel(containerWindow as SceneView, this, Width * scope.scale, height * scope.scale + FrameHeight);
            }
            var rect = EditorGUILayout.BeginVertical(GUILayout.Width(Width));
            if (Event.current.type == EventType.Repaint && rect.height > 0f)
            {
                if (Mathf.Abs(rect.height - s_naturalHeight) > 0.5f) containerWindow?.Repaint();
                s_naturalHeight = rect.height;
            }
            try
            {
                if (!TryGetCurrentEdit(out var edit)) return;
                bool highlight = ToolSession.HighlightEnabled;
                bool newHighlight = DrawToggleRow(highlight, Locales.Tr("Scene:Box:SelectionRange"), Locales.Tr("Help:ShowRange"),
                    (mark, shown) => DrawStripeMark(mark, shown));
                if (newHighlight != highlight) ToolSession.HighlightEnabled = newHighlight;

                bool selection = ToolSession.ShowsSelectionBox(edit);
                bool gradient = ToolSession.ShowsGradientBox(edit);
                bool decal = ToolSession.ShowsDecalBox(edit);
                // ボタンの数は「箱の中」・グラデーション・画像の有無で決まる（パネル側で変わるので、このイベントの中では変わらない）
                bool newSelection = ToolSession.HasSelectionBox(edit)
                    ? DrawBoxButton(selection, Locales.Tr("Scene:Panel:Mode:Box"), RecolorSceneTool.SelectionBoxColor)
                    : selection;
                bool newGradient = edit.gradientEnabled
                    ? DrawBoxButton(gradient, Locales.Tr("Scene:Box:Gradient"), RecolorSceneTool.SelectionBoxHandleColor)
                    : gradient;
                bool newDecal = edit.decalEnabled
                    ? DrawBoxButton(decal, Locales.Tr("Scene:Box:Decal"), RecolorSceneTool.DecalBoxColor)
                    : decal;
                if (newSelection != selection || newGradient != gradient || newDecal != decal)
                {
                    ToolSession.ChooseBoxes(edit, newSelection, newGradient, newDecal);
                    SceneView.RepaintAll();
                }
            }
            finally
            {
                EditorGUILayout.EndVertical();
                OverlayScaler.End(this, scope);
            }
        }

        /// <summary>箱の色の印（左の四角）と、押した状態が残るボタン。押していない間は印を薄くする</summary>
        private static bool DrawBoxButton(bool shown, string label, Color color) =>
            DrawToggleRow(shown, label, Locales.Tr("Scene:Box:Tooltip"),
                (mark, on) => EditorGUI.DrawRect(mark, on ? color : new Color(color.r, color.g, color.b, 0.3f)));

        /// <summary>左の印（drawMark が描く。押していない間は薄く描く約束）と、押した状態が残るボタン</summary>
        private static bool DrawToggleRow(bool shown, string label, string tooltip, System.Action<Rect, bool> drawMark)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var area = GUILayoutUtility.GetRect(MarkSize, EditorGUIUtility.singleLineHeight, GUILayout.Width(MarkSize));
                if (Event.current.type == EventType.Repaint) drawMark(new Rect(area.x, area.center.y - MarkSize * 0.5f, MarkSize, MarkSize), shown);
                return GUILayout.Toggle(shown, new GUIContent(label, tooltip), EditorStyles.miniButton);
            }
        }

        /// <summary>［選択範囲］の印: プレビューの縞と同じ橙の斜め縞（箱のボタンの四角と見分けるため）</summary>
        private static void DrawStripeMark(Rect mark, bool shown)
        {
            var tint = NDMF.SelectionHighlight.TintSrgb;
            var color = shown ? tint : new Color(tint.r, tint.g, tint.b, 0.3f);
            int size = Mathf.RoundToInt(mark.width);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // 幅 2px の斜めの帯を 2px おきに
                    if ((x + y) % 4 < 2) EditorGUI.DrawRect(new Rect(mark.x + x, mark.y + y, 1f, 1f), color);
                }
            }
        }
    }
}
