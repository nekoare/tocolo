using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// 色変えの除外リスト（Renderer 単位）。「Tocolo」パネルの「除外リスト」ボタンで開閉する。
    /// Hierarchy から GameObject をドロップすると、その GameObject の Renderer（無ければ配下の Renderer 全部）を対象アバターのコンポーネントの
    /// 除外リストに足す。対象アバターの外の GameObject は無視して案内を出す（ユーザー判断 2026-09-27）
    /// </summary>
    [Overlay(typeof(SceneView), Id, "Tocolo: 除外リスト")]
    internal sealed class ExcludeListOverlay : IMGUIOverlay
    {
        public const string Id = "ClickRecolor/ExcludeList";
        public const float Width = 260f;
        /// <summary>パネルとの間隔</summary>
        private const float Gap = 12f;

        private static float s_naturalHeight;
        /// <summary>直近の OnGUI での倍率（小窓「箱を表示」が重ならないよう避けるのに使う）</summary>
        private static float s_lastScale = 1f;
        /// <summary>オーバーレイの枠（見出しと余白）が中身に足す高さの概算（ToolPanelOverlay と同じ値）</summary>
        private const float FrameHeight = 26f;
        private static string s_notice;
        private static double s_noticeUntil;

        /// <summary>保存レイアウトの復元より後にツールの状態へ合わせる（表示はボタンで開くまで OFF）</summary>
        public override void OnCreated()
        {
            EditorApplication.delayCall += () =>
            {
                if (!RecolorSceneTool.IsActive) displayed = false;
                OverlayScaler.ClearUnitySizeOverride(this);
            };
        }

        /// <summary>ボタンからの開閉。位置はパネルに追従して毎回置く（PlaceNextToPanel）</summary>
        internal static void Toggle(SceneView view)
        {
            if (view == null || !view.TryGetOverlay(Id, out Overlay overlay)) return;
            bool show = !overlay.displayed;
            overlay.displayed = show;
            if (show) PlaceNextToPanel(view, overlay, Width);
        }

        /// <summary>
        /// パネルの左隣に置く。左に余白が無ければ右隣。大きさはパネルの倍率に追従するので、幅は自分の表示幅（倍率込み）で計算する
        /// （ユーザー要望 2026-09-27: 位置は基本固定でパネルに付いて動く）
        /// </summary>
        private static void PlaceNextToPanel(SceneView view, Overlay overlay, float ownWidth)
        {
            try
            {
                if (!view.TryGetOverlay(ToolPanelOverlay.Id, out Overlay panel) || !panel.floating) return;
                overlay.Undock();
                float panelWidth = ToolPanelOverlay.LastContentWidth > 0f ? ToolPanelOverlay.LastContentWidth : ToolPanelOverlay.PanelWidth;
                var p = panel.floatingPosition;
                float x = p.x - ownWidth - Gap;
                if (x < 0f) x = p.x + panelWidth + Gap;
                var target = new Vector2(x, p.y);
                if ((overlay.floatingPosition - target).sqrMagnitude > 0.25f) overlay.floatingPosition = target;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Tocolo] 除外リストの位置を設定できませんでした: {e.Message}");
            }
        }

        internal static bool IsShown(SceneView view) =>
            view != null && view.TryGetOverlay(Id, out Overlay overlay) && overlay.displayed;

        /// <summary>表示中なら、Scene ビューの中での位置と大きさ（枠込みの概算）。小窓「箱を表示」が重ならないよう避けるのに使う</summary>
        internal static bool TryGetShownRect(SceneView view, out Rect rect)
        {
            rect = default;
            if (view == null || !view.TryGetOverlay(Id, out Overlay overlay) || !overlay.displayed || !overlay.floating) return false;
            float height = (s_naturalHeight > 0f ? s_naturalHeight : 100f) * s_lastScale + FrameHeight;
            rect = new Rect(overlay.floatingPosition, new Vector2(Width * s_lastScale, height));
            return true;
        }

        private static void ShowNotice(string key, params object[] args)
        {
            s_notice = args.Length > 0 ? Locales.Tr(key, args) : Locales.Tr(key);
            s_noticeUntil = EditorApplication.timeSinceStartup + 4.0;
        }

        public override void OnGUI()
        {
            // 大きさはパネルの倍率に追従（自分のつまみは無し）。位置は毎回パネルの隣に置き直す
            var scope = OverlayScaler.Begin(this, Width, s_naturalHeight > 0f ? s_naturalHeight : 100f, ToolPanelOverlay.Id);
            s_lastScale = scope.scale;
            if (Event.current.type == EventType.Layout) PlaceNextToPanel(containerWindow as SceneView, this, Width * scope.scale);
            var rect = EditorGUILayout.BeginVertical(GUILayout.Width(Width));
            if (Event.current.type == EventType.Repaint && rect.height > 0f)
            {
                if (Mathf.Abs(rect.height - s_naturalHeight) > 0.5f) containerWindow?.Repaint();
                s_naturalHeight = rect.height;
            }
            try
            {
                if (!ToolSession.TryGetActiveRoot(out var root))
                {
                    GUILayout.Label(Locales.Tr("Scene:Panel:PickTarget"), EditorStyles.wordWrappedLabel);
                    return;
                }
                var component = root.GetComponent<ClickRecolor>();

                DrawDropArea(root);

                if (s_notice != null && EditorApplication.timeSinceStartup < s_noticeUntil)
                {
                    EditorGUILayout.HelpBox(s_notice, MessageType.Info);
                }
                else
                {
                    s_notice = null;
                }

                var list = component != null ? component.excludedRenderers : null;
                if (list == null || list.Count == 0)
                {
                    GUILayout.Label(Locales.Tr("Scene:Exclude:Empty"), EditorStyles.miniLabel);
                }
                else
                {
                    Renderer toRemove = null;
                    for (int i = 0; i < list.Count; i++)
                    {
                        var renderer = list[i];
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Label(renderer != null ? renderer.name : "(missing)", EditorStyles.label);
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22f))) toRemove = renderer ?? list[i];
                        }
                    }
                    if (toRemove != null || list.Contains(null))
                    {
                        Undo.RecordObject(component, "Tocolo: 除外リストから外す");
                        if (toRemove != null) list.Remove(toRemove);
                        list.RemoveAll(r => r == null);
                        EditorUtility.SetDirty(component);
                        SceneView.RepaintAll();
                    }
                    // 一括削除（ユーザー要望 2026-09-27）。Undo で戻せる
                    EditorGUILayout.Space(4);
                    if (GUILayout.Button(Locales.Tr("Scene:Exclude:ClearAll")))
                    {
                        Undo.RecordObject(component, "Tocolo: 除外リストをすべて外す");
                        list.Clear();
                        EditorUtility.SetDirty(component);
                        SceneView.RepaintAll();
                    }
                }
            }
            finally
            {
                EditorGUILayout.EndVertical();
                OverlayScaler.End(this, scope);
            }
        }

        /// <summary>ドロップ枠。Hierarchy の GameObject を受ける（Project の資産は受けない）</summary>
        private static void DrawDropArea(GameObject root)
        {
            var area = GUILayoutUtility.GetRect(Width - 8f, 44f, GUILayout.ExpandWidth(true));
            var e = Event.current;
            bool hover = area.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(area, hover && DragAndDrop.objectReferences.Length > 0 ? new Color(0.4f, 0.6f, 0.9f, 0.35f) : new Color(1f, 1f, 1f, 0.06f));
                EditorGUI.DrawRect(new Rect(area.x, area.y, area.width, 1f), new Color(1f, 1f, 1f, 0.25f));
                EditorGUI.DrawRect(new Rect(area.x, area.yMax - 1f, area.width, 1f), new Color(1f, 1f, 1f, 0.25f));
                EditorGUI.DrawRect(new Rect(area.x, area.y, 1f, area.height), new Color(1f, 1f, 1f, 0.25f));
                EditorGUI.DrawRect(new Rect(area.xMax - 1f, area.y, 1f, area.height), new Color(1f, 1f, 1f, 0.25f));
            }
            var style = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(area, Locales.Tr("Scene:Exclude:DropHere"), style);

            if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && hover)
            {
                bool any = false;
                foreach (var o in DragAndDrop.objectReferences)
                {
                    if (o is GameObject go && !EditorUtility.IsPersistent(go)) { any = true; break; }
                }
                DragAndDrop.visualMode = any ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (e.type == EventType.DragPerform && any)
                {
                    DragAndDrop.AcceptDrag();
                    AddDropped(root, DragAndDrop.objectReferences);
                }
                e.Use();
            }
        }

        /// <summary>ドロップされた GameObject の Renderer（自身に無ければ配下の全部）を除外リストに足す。対象アバターの外は無視して案内</summary>
        private static void AddDropped(GameObject root, Object[] dropped)
        {
            var component = TargetResolver.GetOrAddComponent(root);
            if (component == null) return;
            var toAdd = new List<Renderer>();
            int outside = 0, noRenderer = 0;
            foreach (var o in dropped)
            {
                if (!(o is GameObject go) || EditorUtility.IsPersistent(go)) continue;
                if (!go.transform.IsChildOf(root.transform)) { outside++; continue; }
                var own = go.GetComponent<Renderer>();
                if (own != null && (own is SkinnedMeshRenderer || own is MeshRenderer))
                {
                    toAdd.Add(own);
                    continue;
                }
                int before = toAdd.Count;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is SkinnedMeshRenderer || r is MeshRenderer) toAdd.Add(r);
                }
                if (toAdd.Count == before) noRenderer++;
            }

            int added = 0;
            if (toAdd.Count > 0)
            {
                Undo.RecordObject(component, "Tocolo: 除外リストに追加");
                foreach (var r in toAdd)
                {
                    if (component.excludedRenderers.Contains(r)) continue;
                    component.excludedRenderers.Add(r);
                    added++;
                }
                EditorUtility.SetDirty(component);
            }
            if (outside > 0) ShowNotice("Scene:Exclude:NotInTarget");
            else if (noRenderer > 0 && added == 0) ShowNotice("Scene:Exclude:NoRenderer");
            SceneView.RepaintAll();
        }

    }
}
