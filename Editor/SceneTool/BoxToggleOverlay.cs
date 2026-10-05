using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// Scene に出す箱を選ぶ小窓（「箱の中」の範囲・グラデーション・画像の箱。複数選択可）。押した箱だけ出して動かせ、押していない箱は隠す。
    /// 箱が同じ位置に重なって分かりにくかったため、パネルの各ブロックの「箱を表示」をここにまとめた（ユーザー要望 2026-10-05）。
    /// 現在の編集が箱を使うとき（「箱の中」・グラデーション・画像）だけ出し（SyncDisplayed）、位置はパネルの隣のブロックの高さに置く（除外リストと同じくパネルに付いて動く）
    /// </summary>
    [Overlay(typeof(SceneView), Id, "Tocolo: 箱を表示")]
    internal sealed class BoxToggleOverlay : IMGUIOverlay
    {
        public const string Id = "ClickRecolor/BoxToggle";
        public const float Width = 130f;
        /// <summary>パネル・除外リストとの間隔</summary>
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

        /// <summary>現在の編集が箱を使うとき（「箱の中」・グラデーション・画像）だけ出す。RecolorSceneTool の Layout で毎回呼ぶ（変わったときだけ書く）</summary>
        internal static void SyncDisplayed(SceneView view)
        {
            if (view == null || !view.TryGetOverlay(Id, out Overlay overlay)) return;
            bool want = RecolorSceneTool.IsActive && !ToolSession.IsInPrefabMode
                && TryGetCurrentEdit(out var edit) && (ToolSession.HasSelectionBox(edit) || edit.gradientEnabled || edit.decalEnabled);
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
        /// 除外リストと重なるならその下へずらし、Scene ビューの下に見切れないよう収める
        /// </summary>
        private static void PlaceNextToPanel(SceneView view, Overlay overlay, float ownWidth, float ownHeight)
        {
            try
            {
                if (view == null || !view.TryGetOverlay(ToolPanelOverlay.Id, out Overlay panel) || !panel.floating) return;
                overlay.Undock();
                float panelWidth = ToolPanelOverlay.LastContentWidth > 0f ? ToolPanelOverlay.LastContentWidth : ToolPanelOverlay.PanelWidth;
                var p = panel.floatingPosition;
                float x = p.x - ownWidth - Gap;
                if (x < 0f) x = p.x + panelWidth + Gap;
                float y = p.y + Mathf.Max(ToolPanelOverlay.BoxBlocksOffsetY, 0f);
                if (ExcludeListOverlay.TryGetShownRect(view, out var exclude) && new Rect(x, y, ownWidth, ownHeight).Overlaps(exclude))
                {
                    y = exclude.yMax + Gap;
                }
                float viewHeight = view.position.height;
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
        private static bool DrawBoxButton(bool shown, string label, Color color)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var area = GUILayoutUtility.GetRect(MarkSize, EditorGUIUtility.singleLineHeight, GUILayout.Width(MarkSize));
                if (Event.current.type == EventType.Repaint)
                {
                    var mark = new Rect(area.x, area.center.y - MarkSize * 0.5f, MarkSize, MarkSize);
                    EditorGUI.DrawRect(mark, shown ? color : new Color(color.r, color.g, color.b, 0.3f));
                }
                var content = new GUIContent(label, Locales.Tr("Scene:Box:Tooltip"));
                return GUILayout.Toggle(shown, content, EditorStyles.miniButton);
            }
        }
    }
}
