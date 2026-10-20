using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Localization;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.NDMF;
using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>オーバーレイ（パネル・操作の案内）の表示と置き場所</summary>
    internal sealed partial class RecolorSceneTool
    {
        private static float PanelDefaultWidth => ToolPanelOverlay.PanelWidth;

        private static void SetOverlaysDisplayed(bool displayed)
        {
            foreach (SceneView view in SceneView.sceneViews)
            {
                if (view.TryGetOverlay(ToolPanelOverlay.Id, out Overlay panel))
                {
                    panel.displayed = displayed;
                    if (displayed) PlacePanelOnce(view, panel);
                }
                if (view.TryGetOverlay(HintOverlay.Id, out Overlay hint))
                {
                    hint.displayed = displayed;
                    // 案内は初回限定ではなく、表示のたびに右下へ置く
                    if (displayed) PlaceHintAtBottomRight(view, hint);
                }
                // 除外リスト・編集一覧はボタンで開くので、ツールの終了時に閉じるだけ（小窓「表示切替」は SyncDisplayed が開く）
                if (!displayed) PanelSideOverlay.CloseAll(view);
                if (!displayed && view.TryGetOverlay(BoxToggleOverlay.Id, out Overlay boxes)) boxes.displayed = false;
            }
        }

        /// <summary>案内オーバーレイの中身が未測定のときの推定の高さ（miniLabel 3 行＋余白＋対象行＋ボタン）</summary>
        private const float HintEstimatedContentHeight = 92f;
        /// <summary>オーバーレイの枠（見出しと余白）が中身に足す高さの概算</summary>
        private const float OverlayFrameHeight = 26f;

        /// <summary>SceneView ごとの、前回案内を置いたときの大きさと案内の高さ（リサイズ・中身の増減の検知用）</summary>
        private static readonly Dictionary<int, Vector3> s_hintPlacedKey = new Dictionary<int, Vector3>();

        /// <summary>案内オーバーレイの高さ（実測した中身＋枠）</summary>
        private static float HintHeight =>
            (HintOverlay.LastContentHeight > 0f ? HintOverlay.LastContentHeight : HintEstimatedContentHeight) + OverlayFrameHeight;

        private const string HintPosXKey = "ClickRecolor.HintPos.x";
        private const string HintPosYKey = "ClickRecolor.HintPos.y";
        /// <summary>見切れ防止・右下配置でこちらが最後に設定した案内の位置（ユーザーの移動と区別するため）</summary>
        private static Vector2? s_lastAppliedHintPos;

        /// <summary>
        /// 案内オーバーレイの位置合わせ（Layout ごと）。ユーザーが動かしていれば EditorPrefs に覚え（パネルと同じ。ユーザー要望 2026-09-26）、
        /// 覚えた位置があればそれを Scene に収まる範囲で使い、無ければ右下に置く。Scene の大きさや中身の高さが変わったら置き直す
        /// </summary>
        private static void KeepHintAtBottomRight(SceneView view)
        {
            if (!view.TryGetOverlay(HintOverlay.Id, out Overlay hint) || !hint.displayed) return;
            SaveHintPositionIfMoved(hint);
            var key = new Vector3(view.position.width, view.position.height, HintHeight);
            int id = view.GetInstanceID();
            if (s_hintPlacedKey.TryGetValue(id, out var last) && last == key) return;
            PlaceHintAtBottomRight(view, hint);
        }

        /// <summary>ユーザーが案内を動かしたら位置を保存する（こちらが設定した位置のままなら何もしない）</summary>
        private static void SaveHintPositionIfMoved(Overlay hint)
        {
            if (!hint.floating) return;
            var pos = hint.floatingPosition;
            if (s_lastAppliedHintPos.HasValue && (pos - s_lastAppliedHintPos.Value).sqrMagnitude < 0.25f) return;
            if (!s_lastAppliedHintPos.HasValue) return; // まだこちらで置いていない（復元前）なら判定しない
            EditorPrefs.SetFloat(HintPosXKey, pos.x);
            EditorPrefs.SetFloat(HintPosYKey, pos.y);
            s_lastAppliedHintPos = pos;
        }

        /// <summary>
        /// 案内オーバーレイを浮動にして置く。覚えた位置があればそれを Scene に収まるよう詰めて使い、無ければ右下
        /// （中身の高さが変わっても下に見切れないよう、高さぶん上にずらす）
        /// </summary>
        private static void PlaceHintAtBottomRight(SceneView view, Overlay hint)
        {
            float height = HintHeight;
            s_hintPlacedKey[view.GetInstanceID()] = new Vector3(view.position.width, view.position.height, height);
            try
            {
                hint.Undock();
                float hintWidth = HintOverlay.LastContentWidth > 0f ? HintOverlay.LastContentWidth : HintOverlay.Width;
                float maxX = Mathf.Max(0f, view.position.width - hintWidth - 12f);
                float maxY = Mathf.Max(0f, view.position.height - height - 12f);
                Vector2 pos;
                if (EditorPrefs.HasKey(HintPosXKey))
                {
                    pos = new Vector2(
                        Mathf.Clamp(EditorPrefs.GetFloat(HintPosXKey), 0f, maxX),
                        Mathf.Clamp(EditorPrefs.GetFloat(HintPosYKey), ToolPanelOverlay.TopMargin, Mathf.Max(ToolPanelOverlay.TopMargin, maxY)));
                }
                else
                {
                    pos = new Vector2(maxX, maxY);
                }
                hint.floatingPosition = pos;
                s_lastAppliedHintPos = pos;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Tocolo] 操作案内の位置を設定できませんでした: {e.Message}");
            }
        }

        /// <summary>
        /// 初めて表示するときだけ、パネルを浮動にして Scene の右上に置く（ユーザー要望）。
        /// 以後はユーザーが動かした位置を Unity が保存するので上書きしない（EditorPrefs で「置いた」ことを覚える）。
        /// [Overlay] 属性には既定のドック位置を指定する引数が無い（2022.3）ため座標で置く
        /// </summary>
        private static void PlacePanelOnce(SceneView view, Overlay panel)
        {
            // Unity は浮動オーバーレイの位置を再起動後に保持しないことがある（実機で左上に戻る）ので、
            // 位置は EditorPrefs に自前で保存し、表示のたびに復元する。保存が無ければ右上に置く
            try
            {
                panel.Undock();
                if (EditorPrefs.HasKey(PanelPosXKey))
                {
                    panel.floatingPosition = new Vector2(EditorPrefs.GetFloat(PanelPosXKey), EditorPrefs.GetFloat(PanelPosYKey));
                }
                else
                {
                    float x = Mathf.Max(0f, view.position.width - PanelDefaultWidth - 12f);
                    panel.floatingPosition = new Vector2(x, ToolPanelOverlay.TopMargin);
                }
                s_lastSavedPanelPos = panel.floatingPosition;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Tocolo] パネルの初期位置を設定できませんでした: {e.Message}");
            }
        }

        private const string PanelPosXKey = "ClickRecolor.PanelPos.x";
        private const string PanelPosYKey = "ClickRecolor.PanelPos.y";
        /// <summary>ユーザーが置いた位置（保存値）。見切れ防止でずらした位置はここに入れない</summary>
        private static Vector2 s_lastSavedPanelPos;
        /// <summary>見切れ防止で最後にこちらから設定した位置（ユーザーの移動と区別するため）</summary>
        private static Vector2? s_lastAppliedPanelPos;

        /// <summary>ユーザーがパネルを動かしたら位置を保存する（Layout イベントごとに差分を見る）。見切れ防止でこちらが動かした分は保存しない</summary>
        private static void SavePanelPositionIfMoved(SceneView view)
        {
            if (!view.TryGetOverlay(ToolPanelOverlay.Id, out Overlay panel) || !panel.displayed || !panel.floating) return;
            var pos = panel.floatingPosition;
            if (s_lastAppliedPanelPos.HasValue && (pos - s_lastAppliedPanelPos.Value).sqrMagnitude < 0.25f) return;
            if ((pos - s_lastSavedPanelPos).sqrMagnitude < 0.25f) return;
            s_lastSavedPanelPos = pos;
            s_lastAppliedPanelPos = null;
            EditorPrefs.SetFloat(PanelPosXKey, pos.x);
            EditorPrefs.SetFloat(PanelPosYKey, pos.y);
        }

        /// <summary>
        /// パネルが Scene の下に見切れないよう、中身の高さ（グラデーション ON 等で伸びる）に応じて上へずらす。
        /// 収まるようになったらユーザーが置いた位置（保存値）へ戻す（ユーザー報告 2026-09-25）
        /// </summary>
        private static void KeepPanelInView(SceneView view)
        {
            if (!view.TryGetOverlay(ToolPanelOverlay.Id, out Overlay panel) || !panel.displayed || !panel.floating) return;
            if (ToolPanelOverlay.LastContentHeight <= 0f) return;
            float height = ToolPanelOverlay.LastContentHeight + OverlayFrameHeight;
            var desired = s_lastSavedPanelPos;
            float maxY = view.position.height - height - 12f;
            // 上端のツールバーには重ねない（TopMargin より上へは寄せない）
            var clamped = new Vector2(desired.x, Mathf.Max(ToolPanelOverlay.TopMargin, Mathf.Min(desired.y, maxY)));
            var pos = panel.floatingPosition;
            if ((pos - clamped).sqrMagnitude < 0.25f) return;
            try
            {
                panel.floatingPosition = clamped;
                // 保存値と同じ位置に戻したときは「こちらが動かした」扱いを解く
                s_lastAppliedPanelPos = (clamped - desired).sqrMagnitude < 0.25f ? (Vector2?)null : clamped;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Tocolo] パネルの位置を調整できませんでした: {e.Message}");
            }
        }
    }
}
