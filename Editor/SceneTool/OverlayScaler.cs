using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// IMGUI のオーバーレイを、右端のつまみのドラッグで比率を保ったまま拡大縮小する（ユーザー要望 2026-09-26）。
    /// Unity のリサイズ機能（Overlay.size / minSize / maxSize）は 2 つのオーバーレイで大きさが連動して見え、再起動後に最大になる
    /// （実測した幅を倍率にすると、幅→中身→幅の循環で膨らむ）ため使わない。倍率は自前で EditorPrefs に保存する。
    /// 使い方: OnGUI の先頭で Begin（GUI.matrix を倍率で拡大し、基準幅の領域を開く）、中身を基準幅で描き、最後に End（つまみを描いて戻す）
    /// </summary>
    internal static class OverlayScaler
    {
        private const float MinScale = 0.5f;
        private const float MaxScale = 1.5f;
        /// <summary>中身（スクロールバー含む）とつまみの間に空ける隙間（基準倍率での px）。スクロールバーに干渉して掴み直しにくい（実機 2026-09-27）ので広めに</summary>
        private const float GripGap = 10f;
        /// <summary>つまみ（ドラッグで大きさを変える帯）の幅（基準倍率での px）</summary>
        private const float GripWidth = 12f;
        /// <summary>中身の右に足す幅の合計（隙間＋つまみ）</summary>
        private const float GripSpace = GripGap + GripWidth;
        private const string ScaleKeyPrefix = "ClickRecolor.OverlayScale.";
        private static readonly int s_gripHint = "ClickRecolor.OverlayGrip".GetHashCode();

        /// <summary>つまみをドラッグ中か（hotControl を握っている）。離しが届かず握ったままになると Scene の操作を奪うので、外から解除できるようにする</summary>
        public static bool Dragging { get; private set; }
        private static int s_dragControl;
        /// <summary>ドラッグ開始時の画面座標の x と倍率（相対移動で倍率を決める。絶対位置だと寄せ直しで飛ぶ）</summary>
        private static float s_dragStartScreenX;
        private static float s_dragStartScale;
        private static double s_lastDragTime;
        /// <summary>この秒数ドラッグのイベントが無ければ離したとみなす（離しのイベントが別ウィンドウで消えたとき用）</summary>
        private const double DragTimeoutSeconds = 1.5;

        [InitializeOnLoadMethod]
        private static void Subscribe()
        {
            EditorApplication.update += () =>
            {
                if (Dragging && EditorApplication.timeSinceStartup - s_lastDragTime > DragTimeoutSeconds) ForceEndDrag();
            };
        }

        /// <summary>つまみのドラッグを強制的に終える（hotControl も返す）。Scene 側で離し・押しを見たときや時間切れで呼ぶ</summary>
        public static void ForceEndDrag()
        {
            if (!Dragging) return;
            Dragging = false;
            if (GUIUtility.hotControl == s_dragControl) GUIUtility.hotControl = 0;
            s_dragControl = 0;
            SceneView.RepaintAll();
        }

        public struct Scope
        {
            public float scale;
            public Matrix4x4 savedMatrix;
            public Rect outer;
            public float baseWidth;
            public float contentHeight;
            public string id;
            /// <summary>つまみのコントロール ID（Begin の先頭で取る。後ろにスクロールビュー等が増減しても番号が変わらないようにする）</summary>
            public int gripControl;
            /// <summary>他のオーバーレイの倍率に追従しているか（つまみ無し）</summary>
            public bool follow;
        }

        /// <summary>覚えた倍率を捨てて 1 に戻す</summary>
        [MenuItem("Tools/Tocolo/オーバーレイの大きさをリセット")]
        private static void ResetScales()
        {
            EditorPrefs.DeleteKey(ScaleKeyPrefix + ToolPanelOverlay.Id);
            EditorPrefs.DeleteKey(ScaleKeyPrefix + HintOverlay.Id);
            SceneView.RepaintAll();
        }

        /// <summary>
        /// 以前の試作で Unity 側のリサイズ（size の上書き）が保存レイアウトに残っていると、中身に合わせた自動の大きさに戻らず切れるので、
        /// 反射で上書きを解除する（sizeOverridden は internal。無い環境では何もしない）
        /// </summary>
        public static void ClearUnitySizeOverride(Overlay overlay)
        {
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var prop = typeof(Overlay).GetProperty("sizeOverridden", flags);
                if (prop != null && prop.CanWrite) { prop.SetValue(overlay, false); return; }
                var field = typeof(Overlay).GetField("m_SizeOverridden", flags);
                field?.SetValue(overlay, false);
            }
            catch { /* 解除できなくても致命ではない */ }
        }

        /// <summary>
        /// Scene ビュー全体に収まる倍率の上限（x = 0 から右端までに中身＋つまみが入る大きさ）。位置は KeepInsideView で寄せるので、ここは全幅で見る
        /// </summary>
        private static float MaxScaleInView(Overlay overlay, float baseWidth)
        {
            try
            {
                var window = overlay.containerWindow;
                if (window == null || !overlay.floating) return MaxScale;
                float byView = (window.position.width - 16f) / Mathf.Max(baseWidth + GripSpace, 1f);
                return Mathf.Clamp(byView, MinScale, MaxScale);
            }
            catch
            {
                return MaxScale;
            }
        }

        /// <summary>オーバーレイ（id）の倍率（EditorPrefs。無ければ 1）。Scene ビュー全体に収まる上限で切る</summary>
        public static float CurrentScale(Overlay overlay, float baseWidth) => CurrentScale(overlay, overlay.id, baseWidth);

        /// <summary>scaleId の倍率を使う版（他のオーバーレイの倍率に追従させるとき）</summary>
        public static float CurrentScale(Overlay overlay, string scaleId, float baseWidth)
        {
            float saved = Mathf.Clamp(EditorPrefs.GetFloat(ScaleKeyPrefix + scaleId, 1f), MinScale, MaxScale);
            return Mathf.Min(saved, MaxScaleInView(overlay, baseWidth));
        }

        private static void SetScale(Overlay overlay, float baseWidth, float scale)
        {
            EditorPrefs.SetFloat(ScaleKeyPrefix + overlay.id, Mathf.Clamp(scale, MinScale, MaxScaleInView(overlay, baseWidth)));
        }

        /// <summary>オーバーレイの枠（見出しと余白）が中身に足す高さの概算（位置合わせ用）</summary>
        private const float FrameHeight = 26f;

        /// <summary>
        /// 大きさを変えた後に、オーバーレイが Scene ビューの右・下からはみ出していれば左・上へ寄せる（ユーザー提案 2026-09-26）。
        /// 倍率で制限するより、はみ出したぶんだけ動かす方が引っかからない。Layout イベントで呼ぶ（位置の変更は次の描画に効く）
        /// </summary>
        private static void KeepInsideView(Overlay overlay, float width, float height)
        {
            try
            {
                var window = overlay.containerWindow;
                if (window == null || !overlay.floating) return;
                var pos = overlay.floatingPosition;
                float maxX = Mathf.Max(0f, window.position.width - width - 8f);
                float maxY = Mathf.Max(ToolPanelOverlay.TopMargin, window.position.height - (height + FrameHeight) - 8f);
                var clamped = new Vector2(Mathf.Min(pos.x, maxX), Mathf.Min(pos.y, maxY));
                if ((clamped - pos).sqrMagnitude > 0.25f) overlay.floatingPosition = clamped;
            }
            catch { /* 浮動でない・取れない場合は何もしない */ }
        }

        /// <summary>
        /// 中身の描画を始める。baseWidth×contentHeight（基準倍率での大きさ）＋つまみの幅を、倍率ぶん拡大した矩形をレイアウトに確保し、
        /// GUI.matrix を拡大にしてから GUILayout.BeginArea で基準の大きさの領域を開く。中身が変わる間は 1 フレーム下が切れるが、
        /// 呼び出し側が高さの変化で描き直しを要求するのですぐ追いつく
        /// </summary>
        public static Scope Begin(Overlay overlay, float baseWidth, float contentHeight) => Begin(overlay, baseWidth, contentHeight, null);

        /// <summary>
        /// followScaleId を渡すと、そのオーバーレイの倍率に追従する（自分のつまみは出さず、位置の寄せ直しもしない。除外リスト用）
        /// </summary>
        public static Scope Begin(Overlay overlay, float baseWidth, float contentHeight, string followScaleId)
        {
            bool follow = !string.IsNullOrEmpty(followScaleId);
            var scope = new Scope
            {
                scale = CurrentScale(overlay, follow ? followScaleId : overlay.id, baseWidth), savedMatrix = GUI.matrix, baseWidth = baseWidth,
                contentHeight = Mathf.Max(contentHeight, 1f), id = overlay.id, follow = follow,
                // 先頭で取る: スクロールバーが出た瞬間にコントロールの数が変わり、後ろで取った ID がずれてドラッグが切れる（実機 2026-09-27）
                gripControl = GUIUtility.GetControlID(s_gripHint, FocusType.Passive),
            };
            float extra = follow ? 0f : GripSpace;
            scope.outer = GUILayoutUtility.GetRect((baseWidth + extra) * scope.scale, scope.contentHeight * scope.scale, GUILayout.ExpandWidth(false));
            // ドラッグ中は寄せ直さない（寄せるとマウスとの相対位置が変わって倍率が飛び、端で引っかかる）。離した後の描画で寄せる
            if (!follow && Event.current.type == EventType.Layout && !Dragging) KeepInsideView(overlay, (baseWidth + extra) * scope.scale, scope.contentHeight * scope.scale);
            GUI.matrix = scope.savedMatrix * Matrix4x4.TRS(new Vector3(scope.outer.x, scope.outer.y, 0f), Quaternion.identity, new Vector3(scope.scale, scope.scale, 1f));
            GUILayout.BeginArea(new Rect(0f, 0f, baseWidth + extra, scope.contentHeight));
            return scope;
        }

        /// <summary>中身の描画を終える。右端につまみを描き（追従のときは描かない）、ドラッグで倍率を変える。GUI.matrix を戻す</summary>
        public static void End(Overlay overlay, in Scope scope)
        {
            if (!scope.follow) DrawGrip(overlay, scope);
            GUILayout.EndArea();
            GUI.matrix = scope.savedMatrix;
        }

        /// <summary>
        /// 右端のつまみ（基準倍率の座標で描く。GUI.matrix で倍率ぶん拡大される）。
        /// ドラッグ中はマウスの x（基準座標）を基準幅で割った比で倍率を変える（つまみの位置＝右端がマウスに追従する）
        /// </summary>
        private static void DrawGrip(Overlay overlay, in Scope scope)
        {
            // 当たり判定はスクロールバーとの隙間の右半分から右端まで（画面上で最低 16px）。縮小時に細くなって掴みにくいのを防ぐ
            float hitX = scope.baseWidth + GripGap * 0.5f;
            float hitWidth = Mathf.Max(GripSpace - GripGap * 0.5f, 16f / Mathf.Max(scope.scale, 0.01f));
            var rect = new Rect(hitX, 0f, hitWidth, scope.contentHeight);
            int id = scope.gripControl;
            var e = Event.current;
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && rect.Contains(e.mousePosition))
                    {
                        GUIUtility.hotControl = id;
                        Dragging = true;
                        s_dragControl = id;
                        s_dragStartScreenX = GUIUtility.GUIToScreenPoint(e.mousePosition).x;
                        s_dragStartScale = scope.scale;
                        s_lastDragTime = EditorApplication.timeSinceStartup;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        // 画面上の横移動量を基準幅で割って倍率に足す（開始時の倍率からの相対。位置の寄せ直しがあっても飛ばない）
                        float dx = GUIUtility.GUIToScreenPoint(e.mousePosition).x - s_dragStartScreenX;
                        float target = s_dragStartScale + dx / Mathf.Max(scope.baseWidth, 1f);
                        SetScale(overlay, scope.baseWidth, target);
                        s_lastDragTime = EditorApplication.timeSinceStartup;
                        e.Use();
                        SceneView.RepaintAll();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        Dragging = false;
                        s_dragControl = 0;
                        e.Use();
                    }
                    break;
                case EventType.Repaint:
                    // 縦の細い帯（ホバーとドラッグ中は明るく）
                    bool active = GUIUtility.hotControl == id || rect.Contains(e.mousePosition);
                    var bar = new Rect(scope.baseWidth + GripGap + 4f, rect.y + 8f, 4f, Mathf.Max(0f, rect.height - 16f));
                    EditorGUI.DrawRect(bar, active ? new Color(1f, 1f, 1f, 0.7f) : new Color(1f, 1f, 1f, 0.25f));
                    break;
            }
            // 握ったまま離しが来なかった場合の保険: 生のイベントが離しなら終える
            if (Dragging && e.rawType == EventType.MouseUp) ForceEndDrag();
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);
        }
    }
}
