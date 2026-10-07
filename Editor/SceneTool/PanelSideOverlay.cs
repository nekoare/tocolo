using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// パネルの隣に出す小窓（除外リスト・編集一覧）の置き方。同じ場所に出すので、1 つ開くと他は閉じる。
    /// 大きさはパネルの倍率に追従し、位置は毎回パネルの隣に置き直す
    /// </summary>
    internal static class PanelSideOverlay
    {
        /// <summary>パネルとの横の間隔（実際の幅で並べるので、ほぼ引っ付ける）</summary>
        private const float Gap = 2f;
        /// <summary>オーバーレイの枠（見出しと余白）が中身に足す高さの概算（ToolPanelOverlay と同じ値）</summary>
        internal const float FrameHeight = 26f;

        private static readonly string[] s_ids = { ExcludeListOverlay.Id, EditListOverlay.Id };

        /// <summary>ボタンからの開閉。開くときは他の小窓を閉じ、パネルの隣に置く</summary>
        internal static void Toggle(SceneView view, string id, float width)
        {
            if (view == null || !view.TryGetOverlay(id, out Overlay overlay)) return;
            bool show = !overlay.displayed;
            if (show)
            {
                foreach (string other in s_ids)
                {
                    if (other != id && view.TryGetOverlay(other, out Overlay o) && o.displayed) o.displayed = false;
                }
            }
            overlay.displayed = show;
            if (show) PlaceNextToPanel(view, overlay, width);
        }

        /// <summary>ツールの終了時にすべて閉じる</summary>
        internal static void CloseAll(SceneView view)
        {
            if (view == null) return;
            foreach (string id in s_ids)
            {
                if (view.TryGetOverlay(id, out Overlay overlay)) overlay.displayed = false;
            }
        }

        internal static bool IsShown(SceneView view, string id) =>
            view != null && view.TryGetOverlay(id, out Overlay overlay) && overlay.displayed;

        /// <summary>
        /// パネルの左隣に置く。左に余白が無ければ右隣。大きさはパネルの倍率に追従するので、幅は自分の表示幅（倍率込み）で計算する
        /// </summary>
        internal static void PlaceNextToPanel(SceneView view, Overlay overlay, float ownWidth)
        {
            try
            {
                if (view == null || !view.TryGetOverlay(ToolPanelOverlay.Id, out Overlay panel) || !panel.floating) return;
                overlay.Undock();
                float panelWidth = ToolPanelOverlay.LastContentWidth > 0f ? ToolPanelOverlay.LastContentWidth : ToolPanelOverlay.PanelWidth;
                var p = panel.floatingPosition;
                // パネルを小さくすると、見出しの文字幅のほうが中身より広くなる。その幅で離さないとパネルに食い込む
                ownWidth = OverlayScaler.ActualWidth(overlay, ownWidth);
                float x = p.x - ownWidth - Gap;
                if (x < 0f) x = p.x + panelWidth + Gap;
                var target = new Vector2(x, p.y);
                if ((overlay.floatingPosition - target).sqrMagnitude > 0.25f) overlay.floatingPosition = target;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Tocolo] 小窓（{overlay.displayName}）の位置を設定できませんでした: {e.Message}");
            }
        }

        /// <summary>id の小窓が表示中なら、Scene ビューの中での位置と大きさ（枠込みの概算）。shownHeight は中身の表示の高さ（倍率込み）</summary>
        internal static bool TryGetShownRect(SceneView view, string id, float width, float shownHeight, out Rect rect)
        {
            rect = default;
            if (view == null || !view.TryGetOverlay(id, out Overlay overlay) || !overlay.displayed || !overlay.floating) return false;
            rect = new Rect(overlay.floatingPosition, new Vector2(OverlayScaler.ActualWidth(overlay, width), shownHeight + FrameHeight));
            return true;
        }

        /// <summary>表示中の小窓（開くのは 1 つだけ）の位置と大きさ。小窓「表示切替」が重ならないよう避けるのに使う</summary>
        internal static bool TryGetAnyShownRect(SceneView view, out Rect rect) =>
            ExcludeListOverlay.TryGetShownRect(view, out rect) || EditListOverlay.TryGetShownRect(view, out rect);
    }
}
