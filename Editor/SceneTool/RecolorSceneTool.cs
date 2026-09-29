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
    /// <summary>
    /// Scene でモデルをクリックして編集対象を選ぶツール。
    /// グローバルツール（typeof 無し）: 選択に依存せず、対象（ルート）は ToolSession が覚える。
    /// 対象を決めただけでは ClickRecolor を付けない（付けるのは最初の編集を作るとき）。
    /// 対象ありのときは左クリックを全部消費する（外しても Selection を変えない＝Inspector が切り替わらない）。
    /// 対象未設定のときは、モデルに当たったクリックでそのルートを対象にし、外れたクリックは通常の選択動作に通す。
    /// 右・中・Alt＋左はカメラ操作に通す。Esc で終了。
    /// 色を決めた編集の範囲を再クリックするとその編集を選び直す（新しい編集は作らない）。Shift＋クリックは常に新しい編集。
    /// Ctrl（Mac は Command）＋クリックは現在の編集に島（種）を足す／外す（Shift より優先。現在の編集が無ければ通常のクリック）。
    /// 別のテクスチャの島は、そのテクスチャ用の編集を島の連結（EditGroups.LinkSeedBased）として足す。
    /// Ctrl＋ドラッグは矩形の中に見えている島をまとめて現在の編集（別のテクスチャなら連結のメンバー）に足す（島モードのみ。外しはしない）
    /// </summary>
    /// <summary>Ctrl＋クリック（RecolorSceneTool.ResolveSeedToggle）の結果</summary>
    internal enum SeedToggleResult
    {
        /// <summary>何もしなかった（編集が無いなど）</summary>
        None,
        /// <summary>追加の種を足した</summary>
        Added,
        /// <summary>種を外した（主種を外したときは追加の種の先頭を主種にした）</summary>
        Removed,
        /// <summary>最後の種を外したので編集を削除した</summary>
        EditRemoved,
        /// <summary>「アバター全体」の連結で、編集と別のテクスチャなので何もしなかった</summary>
        OtherTexture,
        /// <summary>「アバター全体」の連結なので何もしなかった</summary>
        Grouped,
        /// <summary>別のテクスチャの島なので、そのテクスチャ用の編集を作って島の連結に足した</summary>
        MemberAdded,
        /// <summary>島の連結のメンバーの最後の種を外したので、そのメンバーだけ削除した</summary>
        MemberRemoved,
    }

    /// <summary>Ctrl＋ドラッグの矩形（RecolorSceneTool.ApplyRectSeeds）の結果</summary>
    internal struct RectSeedResult
    {
        /// <summary>足した島の数（新しく作った連結のメンバーの主種も含む）</summary>
        public int added;
        /// <summary>別のテクスチャの島のために新しく作った連結のメンバーの数</summary>
        public int membersCreated;
        /// <summary>色モード・「アバター全体」の連結なので何もしなかった</summary>
        public bool rejected;
    }

    [EditorTool("Tocolo")]
    internal sealed class RecolorSceneTool : EditorTool
    {
        /// <summary>ホバー中の丸のリング色</summary>
        private static readonly Color HoverRingColor = Color.white;
        /// <summary>リングの外枠の色（背景が明るくても見えるように）</summary>
        private static readonly Color RingOutlineColor = Color.black;

        /// <summary>ホバーの判定の最短間隔（秒）。これ未満の MouseMove は判定を飛ばす（SMR の判定負荷対策）</summary>
        private const double HoverPickIntervalSeconds = 0.04;

        private static readonly Color RectFillColor = new Color(1f, 0.7f, 0.1f, 0.15f);
        private static readonly Color RectBorderColor = new Color(1f, 0.7f, 0.1f, 0.9f);

        /// <summary>Ctrl＋押下からこれ以上（GUI のポイント）動かしたら矩形選択にする。未満ならクリック</summary>
        private const float RectDragThreshold = 4f;

        private ScenePicker _picker;
        private readonly List<Renderer> _renderers = new List<Renderer>();
        /// <summary>前回ホバーを判定した時刻（EditorApplication.timeSinceStartup）</summary>
        private double _lastHoverPickTime;

        // Ctrl＋押下中の状態（クリックか矩形かは離したときに決まる）。ドメインリロードで消えてよい
        private bool _ctrlPressPending;
        private bool _rectActive;
        private bool _ctrlPressShift;
        private Vector2 _rectStart;
        private Vector2 _rectEnd;

        private static GUIContent s_icon;

        /// <summary>グラデーションの箱のドラッグ 1 回分を Undo 1 回にまとめる</summary>
        private static readonly UndoDragScope s_boxDrag = new UndoDragScope();

        /// <summary>グラデーションの箱の面の塗りの不透明度（下端 = 新しい色、上端 = 終了色）</summary>
        private const float BoxFaceAlpha = 0.25f;
        /// <summary>「箱の中だけ」のときの側面 4 面の塗りの不透明度</summary>

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            // Undo/Redo で連結のメンバーが増減しても縞（ハイライト）の対象が古いままにならないよう、そのたびに出し直す
            Undo.undoRedoPerformed += () => { if (IsActive) ToolSession.PublishHighlight(); };
            // 起動時・ドメインリロード時に、保存レイアウトから復元された Overlay の表示をツールの状態に揃える
            // （レイアウトの復元が後から効くので 1 フレーム遅らせる）
            EditorApplication.delayCall += () => SetOverlaysDisplayed(IsActive);
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Play 中の編集は Play 終了で消えるので、Play に入る前にツールを終了する
            if (change != PlayModeStateChange.ExitingEditMode) return;
            // ドラッグ・数値入力の途中なら Undo のまとめを閉じる（ツールが無効でもまとめだけ残っていることがある）
            ToolPanelOverlay.FinishDrag();
            Deactivate();
        }

        public override GUIContent toolbarIcon
        {
            get
            {
                if (s_icon == null)
                {
                    // 同梱の色相環（高 DPI なら @2x）→ 組み込みアイコン → 文字 の順にフォールバック。
                    // FindTexture は未存在なら無言で null（Pro スキンでは d_ 版を自動で引く）
                    var image = LoadIcon(EditorGUIUtility.pixelsPerPoint > 1f ? ShaderAssets.Icon2xGuid : ShaderAssets.IconGuid)
                        ?? LoadIcon(ShaderAssets.IconGuid)
                        ?? EditorGUIUtility.FindTexture("ColorPicker.CycleColor");
                    s_icon = image != null ? new GUIContent(image, "Tocolo") : new GUIContent("色", "Tocolo");
                }
                return s_icon;
            }
        }

        /// <summary>固定 GUID の同梱テクスチャを読む。解決できなければ null（警告は出さない。フォールバックがあるため）</summary>
        private static Texture2D LoadIcon(string guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        public override void OnActivated()
        {
            _picker = new ScenePicker();
            ToolPanelOverlay.ResetAutoSelect(); // 自動選択は有効化ごとに 1 回だけ
            // 終了時に消したハイライトを戻す（ドメインリロード後はハイライト状態が既定に戻っているので入れ直す）
            ToolSession.PublishHighlight();
            // 対象（ルート）は Activate() が ToolSession に入れている。
            // ツールバーのアイコンから直接有効化された場合は未設定のことがある（ToolPanelOverlay が案内を出す）
            SetOverlaysDisplayed(true);
            // ホバーの丸のため MouseMove を受ける（SceneView は既定で有効のはずだが念のため）
            foreach (var view in SceneView.sceneViews)
            {
                if (view is SceneView sv) sv.wantsMouseMove = true;
            }
            SceneView.RepaintAll();
        }

        public override void OnWillBeDeactivated()
        {
            // ドラッグ・数値入力の途中で終了した場合も Undo のまとめを閉じる（後続の操作まで 1 つの Undo に畳まないため）
            ToolPanelOverlay.FinishDrag();
            s_boxDrag.End();
            // Ctrl＋ドラッグの途中なら矩形を捨てる
            _ctrlPressPending = false;
            _rectActive = false;
            // 色を決めないまま終了した編集は残さない（プレビューにもビルドにも効かない空の編集になるため）
            DiscardPendingEdit();
            // 選択範囲のハイライトを消す（ビルドには元々乗らないが、ツール外のプレビューにも出さない）
            ToolSession.HideHighlight();
            ToolSession.EyedropperActive = false;
            SetOverlaysDisplayed(false);
            _picker?.Dispose();
            _picker = null;
            ToolSession.LastPick = null;
            ToolSession.LastPickColor = null;
            ToolSession.HoverPick = null;
            SceneView.RepaintAll();
        }

        public override void OnToolGUI(EditorWindow window)
        {
            if (!(window is SceneView sceneView)) return;

            var e = Event.current;
            // control ID の並びを全イベントで一定にするため、分岐より前に取る
            int controlId = GUIUtility.GetControlID(FocusType.Passive);
            // Ctrl＋押下中の Esc は矩形（とクリック）だけ中止する（ツールは終了しない）
            if (_ctrlPressPending && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                e.Use();
                _ctrlPressPending = false;
                _rectActive = false;
                if (GUIUtility.hotControl == controlId) GUIUtility.hotControl = 0;
                sceneView.Repaint();
                return;
            }
            // Esc は対象未設定でも効かせる（ツールバーから直接有効化した場合も抜けられるように）。スポイト中は先にスポイトだけやめる
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                e.Use();
                if (ToolSession.EyedropperActive)
                {
                    ToolSession.EyedropperActive = false;
                    sceneView.Repaint();
                    return;
                }
                ToolManager.RestorePreviousTool();
                return;
            }

            // Prefab 編集モード中はプレビューが出ないので、クリック・ホバー・箱の操作を受け付けない（パネルに案内を出す）
            if (ToolSession.IsInPrefabMode)
            {
                if (e.type == EventType.Layout)
                {
                    KeepHintAtBottomRight(sceneView);
                    SavePanelPositionIfMoved(sceneView);
                    KeepPanelInView(sceneView);
                }
                if (ToolSession.HoverPick.HasValue) { ToolSession.HoverPick = null; sceneView.Repaint(); }
                return;
            }

            // オーバーレイのつまみが離しを取りこぼして握ったままだと Scene の操作が効かなくなる（実機 2026-09-26）ので、
            // Scene 側で押し・離しを見たら解除する
            if (OverlayScaler.Dragging && (e.type == EventType.MouseUp || e.type == EventType.MouseDown || e.rawType == EventType.MouseUp))
            {
                OverlayScaler.ForceEndDrag();
            }

            // グラデーションの箱のハンドル。Layout でも呼んで距離を登録する（ハンドルに近いクリックはハンドルが取り、Pick に来ない）
            DrawGradientBox();
            DrawSelectionBox();
            // 箱のハンドルなど他のコントロールがマウスを掴んでいる間は、クリック・ドラッグを処理しない
            if (e.isMouse && GUIUtility.hotControl != 0 && GUIUtility.hotControl != controlId) return;

            // 対象の有無にかかわらず既定コントロールにしておく。外さないと Unity 側の選択処理が同じクリックを拾い、
            // 対象を決めたクリックで Selection と Inspector が切り替わってしまう
            if (e.type == EventType.Layout)
            {
                HandleUtility.AddDefaultControl(controlId);
                // SceneView の大きさが変わったら案内を右下に置き直す（ドラッグで動かされても次のリサイズで戻す）
                KeepHintAtBottomRight(sceneView);
                SavePanelPositionIfMoved(sceneView);
                KeepPanelInView(sceneView);
                return;
            }

            // マウスが SceneView の外に出たらホバーの丸を消す
            if (e.type == EventType.MouseLeaveWindow)
            {
                if (ToolSession.HoverPick.HasValue)
                {
                    ToolSession.HoverPick = null;
                    sceneView.Repaint();
                }
                return;
            }

            if (!ToolSession.TryGetActiveRoot(out var root))
            {
                ToolSession.HoverPick = null;
                // Ctrl＋押下中に対象が消えた（破棄など）: 押下を捨てて hotControl を返す
                if (_ctrlPressPending)
                {
                    _ctrlPressPending = false;
                    _rectActive = false;
                    if (GUIUtility.hotControl == controlId) GUIUtility.hotControl = 0;
                }
                // 対象未設定: 左クリックがモデルに当たったら、そのルートを対象にしてそのままクリック位置を表示する。
                // 外れたらクリックは消費しない（ドラッグ等はそのまま）。案内とアバターのボタンは ToolPanelOverlay が出す
                if (e.type == EventType.MouseDown && e.button == 0 && !e.alt && TryResolveTargetByClick(e.mousePosition, out root))
                {
                    e.Use();
                    Pick(root, e.mousePosition, forceNew: e.shift, toggleSeed: e.control || e.command);
                    sceneView.Repaint();
                }
                return;
            }

            if (e.type == EventType.MouseMove)
            {
                UpdateHover(root, sceneView, e.mousePosition);
                return;
            }

            // スポイト: 次のクリックでその場所の元の色を編集中の色に入れる（編集は作らない）。外したらスポイトのまま
            if (ToolSession.EyedropperActive)
            {
                // 十字のカーソル（自前のテクスチャ。CustomCursor は Cursor.SetCursor で設定したものを使う）
                Cursor.SetCursor(CrosshairCursor, new Vector2(CrosshairSize * 0.5f, CrosshairSize * 0.5f), CursorMode.Auto);
                EditorGUIUtility.AddCursorRect(new Rect(0f, 0f, sceneView.position.width, sceneView.position.height), MouseCursor.CustomCursor);
                if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
                {
                    e.Use();
                    PickColorWithEyedropper(root, e.mousePosition);
                    sceneView.Repaint();
                }
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt)
            {
                e.Use(); // 外しても消費する。素通しすると Selection が変わり Inspector が切り替わってしまう（グローバルツールなのでツール自体は終了しない）
                if (e.control || e.command)
                {
                    // Ctrl＋押下: RectDragThreshold 以上ドラッグすれば矩形選択、しなければ離したときに従来の Ctrl＋クリック。
                    // 離すまでのドラッグ・離すイベントを確実に受けるため、押した時点で hotControl を取る
                    _ctrlPressPending = true;
                    _rectActive = false;
                    _ctrlPressShift = e.shift;
                    _rectStart = e.mousePosition;
                    _rectEnd = e.mousePosition;
                    GUIUtility.hotControl = controlId;
                    return;
                }
                Pick(root, e.mousePosition, forceNew: e.shift, toggleSeed: false);
                sceneView.Repaint();
                return;
            }

            if (_ctrlPressPending)
            {
                var type = e.GetTypeForControl(controlId);
                if (type == EventType.MouseDrag && GUIUtility.hotControl == controlId)
                {
                    e.Use();
                    _rectEnd = e.mousePosition;
                    // 矩形選択は UV アイランドモード専用（色モードでは矩形を出さず、離したときに Ctrl＋クリック扱い）
                    if (!_rectActive && ToolSession.Mode == SelectionMode.Island && (_rectEnd - _rectStart).magnitude >= RectDragThreshold) _rectActive = true;
                    sceneView.Repaint();
                    return;
                }
                if (type == EventType.MouseUp && e.button == 0)
                {
                    if (GUIUtility.hotControl == controlId)
                    {
                        GUIUtility.hotControl = 0;
                        e.Use();
                    }
                    bool rect = _rectActive;
                    _ctrlPressPending = false;
                    _rectActive = false;
                    if (rect) SelectIslandsInRect(root, sceneView, RectFromPoints(_rectStart, e.mousePosition), _rectStart);
                    else Pick(root, _rectStart, forceNew: _ctrlPressShift, toggleSeed: true);
                    sceneView.Repaint();
                    return;
                }
            }

            if (e.type == EventType.Repaint)
            {
                if (ToolSession.HoverPick.HasValue) DrawHoverMarker(ToolSession.HoverPick.Value);
                if (ToolSession.LastPick.HasValue) DrawPickMarker(ToolSession.LastPick.Value);
                if (_ctrlPressPending && _rectActive) DrawSelectionRect(RectFromPoints(_rectStart, _rectEnd));
            }
        }

        /// <summary>
        /// 現在の編集がグラデーション ON なら、Scene に箱（対象ルートのローカル座標）を出す: 移動・回転・拡縮のハンドル、ワイヤーの箱、
        /// 下端（t = 0）の面を「新しい色」、上端（t = 1）の面を「終了色」で薄く塗る。
        /// ハンドルの操作は Undo 1 回にまとめ、「アバター全体」の連結なら連結の全編集に同じ箱を入れる
        /// </summary>
        private static void DrawGradientBox()
        {
            // 離したら Undo のまとめを締める（ハンドルは MouseUp で hotControl を 0 に戻す）
            if (s_boxDrag.IsActive && GUIUtility.hotControl == 0) s_boxDrag.End();

            if (!ToolSession.TryGetActiveRoot(out var root)) return;
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            if (edit == null || !edit.gradientEnabled) return;
            if (s_gradientDragging && GUIUtility.hotControl == 0) s_gradientDragging = false;

            var e = Event.current;
            using (new Handles.DrawingScope(component.transform.localToWorldMatrix))
            {
                Vector3 position = edit.gradientBoxPosition;
                Quaternion rotation = Quaternion.Normalize(edit.gradientBoxRotation);
                Vector3 size = edit.gradientBoxSize;

                EditorGUI.BeginChangeCheck();
                // 全体のギズモに加えて、各面のつまみでも伸縮できる（「箱の中」と同じ。ユーザー要望 2026-09-29）
                DrawBoxWithFaceHandles(ref position, ref rotation, ref size, s_gradientBoxHandle, ref s_gradientDragging, ref s_gradientDragOrigin);
                if (EditorGUI.EndChangeCheck())
                {
                    if (GUIUtility.hotControl != 0 && !s_boxDrag.IsActive) s_boxDrag.Begin("Tocolo: グラデーションの箱を変更");
                    Undo.RecordObject(component, "Tocolo: グラデーションの箱を変更");
                    EditGroups.ForEachInGroup(component, edit, m =>
                    {
                        m.gradientBoxPosition = position;
                        m.gradientBoxRotation = rotation;
                        m.gradientBoxSize = size;
                    });
                    EditorUtility.SetDirty(component);
                }

                if (e.type == EventType.Repaint) DrawBoxShape(position, rotation, size, edit.targetColor, edit.gradientColor, edit.gradientInsideOnly);
            }
        }

        /// <summary>
        /// 箱のワイヤーと、下端（startColor）・上端（endColor）の面の薄い塗り。insideOnly（箱の中だけ）なら側面 4 面も灰色で薄く塗る。
        /// Handles.matrix（ルートのローカル）の中で呼ぶ
        /// </summary>
        private static void DrawBoxShape(Vector3 position, Quaternion rotation, Vector3 size, Color startColor, Color endColor, bool insideOnly)
        {
            using (new Handles.DrawingScope(Color.white, Handles.matrix * Matrix4x4.TRS(position, rotation, Vector3.one)))
            {
                Handles.DrawWireCube(Vector3.zero, size);
                var half = size * 0.5f;
                DrawFace(-half.y, half, startColor);
                DrawFace(half.y, half, endColor);
                // 「箱の中だけ」は上下方向だけで切る（横は切らない）ので、側面は塗らない。上下の面は色 1／色 2 で常に塗られている
            }
        }

        /// <summary>
        /// 影響範囲「箱の中」の箱（RecolorEdit.box*）。グラデーションの箱と同じハンドルで動かせる（色は区別のため水色）。
        /// ハンドルの操作は Undo 1 回にまとめ、連結なら連結の全編集に同じ箱を入れる
        /// </summary>
        private static void DrawSelectionBox()
        {
            if (!ToolSession.TryGetActiveRoot(out var root)) return;
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            if (edit == null || edit.mode != SelectionMode.Box) return;
            // グラデーション ON のときは 2 つの箱が重なって見づらいので、選択の箱はギズモごと出さない（ユーザー要望 2026-09-29）
            if (edit.gradientEnabled) return;

            // 離したら、箱に触れるテクスチャのメンバーを増減してから Undo のまとめを締める（同じ 1 回の Undo に入れる）。
            // 離しの検知は s_boxDrag ではなく自分のフラグで行う（s_boxDrag は先に描くグラデーションの箱の関数が締めてしまい、
            // ここの分岐が通らなかった。実機 2026-09-29）
            if (ToolSession.BoxDragging && GUIUtility.hotControl == 0)
            {
                SelectionBox.SyncMembers(component, edit);
                if (s_boxDrag.IsActive) s_boxDrag.End();
                ToolSession.BoxDragging = false; // 離した: パーツごとの統計をここで掛け直す
                SceneView.RepaintAll();
            }

            var e = Event.current;
            using (new Handles.DrawingScope(component.transform.localToWorldMatrix))
            {
                Vector3 position = edit.boxPosition;
                Quaternion rotation = Quaternion.Normalize(edit.boxRotation);
                Vector3 size = edit.boxSize;

                EditorGUI.BeginChangeCheck();
                // 箱全体の移動・回転・拡大縮小はグラデーションの箱と同じハンドル。加えて各面のつまみで、その面だけを軸ごとに引ける
                // （比率は保たない。両方残す: ユーザー要望 2026-09-29）
                bool dragging = ToolSession.BoxDragging;
                DrawBoxWithFaceHandles(ref position, ref rotation, ref size, s_selectionBoxHandle, ref dragging, ref s_boxDragOrigin);
                if (EditorGUI.EndChangeCheck())
                {
                    if (GUIUtility.hotControl != 0)
                    {
                        if (!s_boxDrag.IsActive) s_boxDrag.Begin("Tocolo: 選択の箱を変更");
                        ToolSession.BoxDragging = dragging;
                    }
                    Undo.RecordObject(component, "Tocolo: 選択の箱を変更");
                    EditGroups.ForEachInGroup(component, edit, m =>
                    {
                        m.boxPosition = position;
                        m.boxRotation = rotation;
                        m.boxSize = size;
                    });
                    EditorUtility.SetDirty(component);
                }

                if (e.type == EventType.Repaint) DrawSelectionBoxShape(position, rotation, size);
            }
        }

        private static readonly Color SelectionBoxColor = new Color(0.25f, 0.75f, 1f, 1f);
        /// <summary>選択の箱のドラッグ開始時の中心（対象ルートのローカル）。ドラッグ中の Handles.matrix の原点に使う</summary>
        private static Vector3 s_boxDragOrigin;
        /// <summary>グラデーションの箱の面のつまみと、そのドラッグ状態・開始時の中心</summary>
        private static readonly UnityEditor.IMGUI.Controls.BoxBoundsHandle s_gradientBoxHandle =
            new UnityEditor.IMGUI.Controls.BoxBoundsHandle
            {
                wireframeColor = Color.clear,
                midpointHandleSizeFunction = p => HandleUtility.GetHandleSize(p) * 0.18f,
                midpointHandleDrawFunction = DrawSelectionBoxKnob,
            };
        private static bool s_gradientDragging;
        private static Vector3 s_gradientDragOrigin;

        /// <summary>
        /// 箱のギズモ（移動・回転・全体の拡大縮小）と各面のつまみ（その面だけ伸縮）を描き、position / rotation / size を更新する。
        /// Handles.matrix（ルートのローカル）の中で呼ぶ。EditorGUI.BeginChangeCheck の内側で呼ぶこと。
        /// 面のつまみ（1 軸スライダー）はドラッグ開始位置を Handles.matrix のローカルで覚えるので、ドラッグ中に matrix の原点（箱の中心）を
        /// 動かすと基準がずれて約 2 倍動く（実機 2026-09-29）。dragging の間は原点を dragOrigin（掴んだときの中心）に固定し、
        /// ずれはつまみ側の center に持たせる。大きさの符号（反転）は保つ
        /// </summary>
        private static void DrawBoxWithFaceHandles(
            ref Vector3 position, ref Quaternion rotation, ref Vector3 size,
            UnityEditor.IMGUI.Controls.BoxBoundsHandle handle, ref bool dragging, ref Vector3 dragOrigin)
        {
            Vector3 positionBefore = position;
            Handles.TransformHandle(ref position, ref rotation, ref size);
            Vector3 origin = dragging ? dragOrigin : position;
            using (new Handles.DrawingScope(Handles.matrix * Matrix4x4.TRS(origin, rotation, Vector3.one)))
            {
                var fed = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
                var fedCenter = Quaternion.Inverse(rotation) * (position - origin);
                handle.center = fedCenter;
                handle.size = fed;
                handle.SetColor(SelectionBoxHandleColor);
                handle.DrawHandle();
                // 面を引いたときだけ反映する（全体のギズモの結果を上書きしない）。中心は箱のローカルなのでルートのローカルへ戻す
                if (handle.center != fedCenter || handle.size != fed)
                {
                    position = origin + rotation * handle.center;
                    size = new Vector3(
                        Mathf.Sign(size.x == 0f ? 1f : size.x) * handle.size.x,
                        Mathf.Sign(size.y == 0f ? 1f : size.y) * handle.size.y,
                        Mathf.Sign(size.z == 0f ? 1f : size.z) * handle.size.z);
                }
            }
            if (GUI.changed && GUIUtility.hotControl != 0 && !dragging)
            {
                dragOrigin = positionBefore; // このフレームの matrix はこの位置で描いた
                dragging = true;
            }
        }
        /// <summary>面のつまみの色。箱の水色の上でも見えるよう補色寄りの橙（ユーザー要望 2026-09-29）</summary>
        private static readonly Color SelectionBoxHandleColor = new Color(1f, 0.6f, 0.1f, 1f);
        /// <summary>面のつまみにマウスが乗った・掴んだときの色（明るい黄）</summary>
        private static readonly Color SelectionBoxHandleHoverColor = new Color(1f, 1f, 0.2f, 1f);
        /// <summary>選択の箱の各面のつまみ（軸ごとに伸縮）。ワイヤーは DrawSelectionBoxShape が描くので消す</summary>
        private static readonly UnityEditor.IMGUI.Controls.BoxBoundsHandle s_selectionBoxHandle =
            new UnityEditor.IMGUI.Controls.BoxBoundsHandle
            {
                wireframeColor = Color.clear,
                // つまみは既定（0.03）の 6 倍の丸（ユーザー要望 2026-09-29）
                midpointHandleSizeFunction = p => HandleUtility.GetHandleSize(p) * 0.18f,
                midpointHandleDrawFunction = DrawSelectionBoxKnob,
            };

        /// <summary>
        /// 面のつまみの描画。マウスが乗っている（nearestControl）か掴んでいる（hotControl）つまみは明るい色にして、
        /// どれが反応するか分かるようにする（ユーザー要望 2026-09-29: 細かいところが掴みにくい）
        /// </summary>
        private static void DrawSelectionBoxKnob(int controlID, Vector3 position, Quaternion rotation, float size, EventType eventType)
        {
            if (eventType == EventType.Repaint)
            {
                bool active = GUIUtility.hotControl == controlID
                    || (GUIUtility.hotControl == 0 && HandleUtility.nearestControl == controlID);
                using (new Handles.DrawingScope(active ? SelectionBoxHandleHoverColor : Handles.color))
                {
                    Handles.SphereHandleCap(controlID, position, rotation, active ? size * 1.15f : size, eventType);
                }
                return;
            }
            Handles.SphereHandleCap(controlID, position, rotation, size, eventType);
        }

        /// <summary>選択の箱のワイヤーと、6 面の薄い塗り（水色）</summary>
        private static void DrawSelectionBoxShape(Vector3 position, Quaternion rotation, Vector3 size)
        {
            using (new Handles.DrawingScope(SelectionBoxColor, Handles.matrix * Matrix4x4.TRS(position, rotation, Vector3.one)))
            {
                Handles.DrawWireCube(Vector3.zero, size);
                var half = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)) * 0.5f;
                // 面の塗りはグラデーションの箱の半分から、25% 薄く、さらに 40% 薄く（ユーザー要望 2026-09-29）
                var fill = new Color(SelectionBoxColor.r, SelectionBoxColor.g, SelectionBoxColor.b, BoxFaceAlpha * 0.5f * 0.75f * 0.6f);
                var outline = new Color(SelectionBoxColor.r, SelectionBoxColor.g, SelectionBoxColor.b, 1f);
                // 6 面: 各軸の ± の面
                for (int axis = 0; axis < 3; axis++)
                {
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        var verts = new Vector3[4];
                        int a = (axis + 1) % 3, b = (axis + 2) % 3;
                        for (int k = 0; k < 4; k++)
                        {
                            var v = Vector3.zero;
                            v[axis] = sign * half[axis];
                            v[a] = (k == 0 || k == 3 ? -1 : 1) * half[a];
                            v[b] = (k < 2 ? -1 : 1) * half[b];
                            verts[k] = v;
                        }
                        Handles.DrawSolidRectangleWithOutline(verts, fill, outline);
                    }
                }
            }
        }

        /// <summary>高さ y の水平な面（箱のローカル）を color で薄く塗る</summary>
        private static void DrawFace(float y, Vector3 half, Color color)
        {
            var verts = new[]
            {
                new Vector3(-half.x, y, -half.z),
                new Vector3(half.x, y, -half.z),
                new Vector3(half.x, y, half.z),
                new Vector3(-half.x, y, half.z),
            };
            Handles.DrawSolidRectangleWithOutline(verts,
                new Color(color.r, color.g, color.b, BoxFaceAlpha),
                new Color(color.r, color.g, color.b, 1f));
        }

        /// <summary>
        /// マウス位置の面を拾ってホバーの丸（ToolSession.HoverPick）を更新する。
        /// 前回の判定から HoverPickIntervalSeconds 未満なら何もしない。丸が出ていない状態のまま外れたときは再描画しない
        /// </summary>
        private void UpdateHover(GameObject root, SceneView sceneView, Vector2 mousePosition)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastHoverPickTime < HoverPickIntervalSeconds) return;
            _lastHoverPickTime = now;

            if (_picker == null) _picker = new ScenePicker(); // ドメインリロード直後の保険
            CollectPickRenderers(root);
            var ray = HandleUtility.GUIPointToWorldRay(mousePosition);
            bool hadHover = ToolSession.HoverPick.HasValue;
            ToolSession.HoverPick = _picker.TryPick(ray, _renderers, null, out var hit) ? hit : (PickHit?)null;
            if (hadHover || ToolSession.HoverPick.HasValue) sceneView.Repaint();
        }

        /// <summary>
        /// リングの外側に黒の細い外枠（1 ポイント相当）を描く。size はリングの半径（ワールド単位）。
        /// GetHandleSize は画面上およそ 80 ポイントに当たるワールド長さなので、それを 80 で割って 1 ポイント分とする
        /// </summary>
        private static void DrawRingOutline(Vector3 position, Vector3 normal, float size)
        {
            float onePoint = HandleUtility.GetHandleSize(position) / 80f;
            Handles.color = RingOutlineColor;
            Handles.DrawWireDisc(position, normal, size + onePoint);
        }

        /// <summary>ホバー中の丸（白いリング＋黒の細い外枠。色見本・ラベルは無し）。大きさは DrawPickMarker と同じ</summary>
        private static void DrawHoverMarker(in PickHit hit)
        {
            if (hit.renderer == null) return;
            float size = HandleUtility.GetHandleSize(hit.worldPosition) * 0.08f;
            var prev = Handles.color;
            DrawRingOutline(hit.worldPosition, hit.worldNormal, size);
            Handles.color = HoverRingColor;
            Handles.DrawWireDisc(hit.worldPosition, hit.worldNormal, size);
            Handles.color = prev;
        }

        /// <summary>
        /// クリック後のリング色。見本色の色相を 180° 回し、明度を反転する（彩度は 0.6 以上に持ち上げる）。
        /// 見本色が無ければ（Read/Write 無効・Crunch 等）橙にする（白を元にすると黒くなり、黒い外枠と重なって見えないため）
        /// </summary>
        private static Color PickRingColor(Color? swatch)
        {
            if (!swatch.HasValue) return new Color(1f, 0.7f, 0.1f);
            Color.RGBToHSV(swatch.Value, out float h, out float s, out float v);
            return Color.HSVToRGB(Mathf.Repeat(h + 0.5f, 1f), Mathf.Max(s, 0.6f), 1f - v);
        }

        private static Rect RectFromPoints(Vector2 a, Vector2 b)
        {
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        /// <summary>Ctrl＋ドラッグ中の矩形（半透明の塗り＋1 ポイントの枠）</summary>
        private static void DrawSelectionRect(Rect rect)
        {
            Handles.BeginGUI();
            EditorGUI.DrawRect(rect, RectFillColor);
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, rect.width, 1f), RectBorderColor);
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMax - 1f, rect.width, 1f), RectBorderColor);
            EditorGUI.DrawRect(new Rect(rect.xMin, rect.yMin, 1f, rect.height), RectBorderColor);
            EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.yMin, 1f, rect.height), RectBorderColor);
            Handles.EndGUI();
        }

        /// <summary>
        /// Ctrl＋ドラッグの矩形 guiRect の中に見えている島を、現在の編集に追加の種としてまとめて足す（外しはしない）。
        /// 現在の編集が無ければ、**ドラッグを始めた位置**の島（テクスチャ資産のもの）を主種にして新しい編集を作ってから残りを足す
        /// （始点に何も無ければ矩形内の最初の島。プリセットが色モードなら作らずに一時メッセージ）。足す規則は ApplyRectSeeds。
        /// 始点を基準にするのは、編集の対象テクスチャが「どこから描き始めたか」で決まる方が操作の意図に合うため（ユーザー要望）
        /// </summary>
        private void SelectIslandsInRect(GameObject root, SceneView sceneView, Rect guiRect, Vector2 startGui)
        {
            if (_picker == null) _picker = new ScenePicker(); // ドメインリロード直後の保険
            CollectPickRenderers(root);
            // 「隠れている範囲も選ぶ」なら、奥に隠れている島も矩形に入っていれば拾う（貫通選択）
            var islands = ToolSession.SelectHiddenEnabled
                ? IslandRectPicker.PickThrough(sceneView.camera, guiRect, _renderers, _picker)
                : IslandRectPicker.Pick(sceneView.camera, guiRect, _renderers, _picker);
            if (islands.Count == 0) return;

            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            if (edit == null)
            {
                if (ToolSession.Mode != SelectionMode.Island)
                {
                    ToolSession.ShowTransientNotice("Scene:Panel:RectColorModeNotice");
                    return;
                }
                // 始点の島を主種にする。始点に当たらなければ矩形内の最初の島。
                // 編集を作れないときに空のコンポーネントだけ残さないよう、コンポーネントの追加は種が決まってから
                var startRay = HandleUtility.GUIPointToWorldRay(startGui);
                bool startOk = _picker.TryPick(startRay, _renderers, null, out var startHit) && TryGetSourceTexture(startHit, out _);
                int first = startOk ? -1 : islands.FindIndex(island => TryGetSourceTexture(island.texture, out _));
                if (!startOk && first < 0) return;

                // Project の Prefab なら GetOrAddComponent が警告して null を返すので何もしない
                component = TargetResolver.GetOrAddComponent(root);
                if (component == null) return;
                if (startOk)
                {
                    ToolSession.LastPick = startHit;
                    ToolSession.LastPickColor = SampleSwatchColor(startHit);
                    edit = CreateEditFromHit(component, startHit, ToolSession.LastPickColor);
                }
                else
                {
                    var seed = islands[first];
                    edit = CreateEditFromSeed(component, seed.renderer, seed.submesh, seed.triangle, seed.uv, seed.texture,
                        SampleSwatchColor(seed.texture, seed.uv));
                }
                if (edit == null) return;
            }
            ApplyRectSeeds(component, edit, islands);
        }

        /// <summary>
        /// 矩形で集めた島 islands を編集 edit（と、その島の連結のメンバー）の種として足す。島モードの編集だけ（色モードなら一時メッセージで何もしない）。
        /// 「アバター全体」の連結も何もしない（Ctrl＋クリックと同じ一時メッセージ）。
        /// edit と同じテクスチャの島は edit の追加の種に、別のテクスチャの島はそのテクスチャの連結のメンバーの追加の種に足す。
        /// メンバーが無ければ、そのテクスチャで最初の島を主種にしてメンバーを作る（EditGroups.LinkSeedBased）。
        /// 既に含まれる島（主種・追加の種と同じ島。IsSameIsland）は無視する（外さない）。テクスチャの無い島・資産でないテクスチャの島は黙って無視。
        /// Undo 記録は足す島があるときに 1 回
        /// </summary>
        internal static RectSeedResult ApplyRectSeeds(ClickRecolor component, RecolorEdit edit, IReadOnlyList<RectIsland> islands)
        {
            var result = new RectSeedResult();
            if (component == null || edit == null || islands == null) return result;
            if (edit.mode != SelectionMode.Island)
            {
                ToolSession.ShowTransientNotice("Scene:Panel:RectColorModeNotice");
                result.rejected = true;
                return result;
            }
            if (EditGroups.IsWholeAvatarGroup(edit))
            {
                ToolSession.ShowTransientNotice("Scene:Panel:CtrlGroupNotice");
                result.rejected = true;
                return result;
            }

            // テクスチャごとに、足す先（既存の編集。無ければ null＝メンバーを作る）と足す種をまとめる（最初に出てきた順）
            var plans = new List<(Texture2D texture, RecolorEdit target, List<RecolorSeed> seeds)>();
            int skippedOtherTexture = 0;
            foreach (var island in islands)
            {
                if (island.renderer == null || island.texture == null) continue;
                // 「マテリアルをまたいで選ぶ」が OFF なら別テクスチャの島は足さない（数だけ知らせる）
                if (!ToolSession.CrossTextureEnabled && island.texture != edit.sourceTexture) { skippedOtherTexture++; continue; }
                int index = plans.FindIndex(p => p.texture == island.texture);
                if (index < 0)
                {
                    RecolorEdit target;
                    if (island.texture == edit.sourceTexture) target = edit;
                    else
                    {
                        if (!TryGetSourceTexture(island.texture, out _)) continue;
                        target = EditGroups.FindMemberForTexture(component, edit, island.texture);
                    }
                    plans.Add((island.texture, target, new List<RecolorSeed>()));
                    index = plans.Count - 1;
                }
                var plan = plans[index];
                if (ContainsIsland(plan.target, plan.seeds, island)) continue;
                plan.seeds.Add(new RecolorSeed
                {
                    renderer = island.renderer,
                    submesh = island.submesh,
                    triangle = island.triangle,
                    // マテリアルの Tiling/Offset 適用後の UV（主種と同じ座標）
                    uv = island.uv,
                });
                result.added++;
            }
            if (result.added == 0) return result;

            Undo.RecordObject(component, "Tocolo: 範囲内の島を追加");
            foreach (var (texture, target, seeds) in plans)
            {
                if (seeds.Count == 0) continue;
                var into = target;
                int from = 0;
                if (into == null)
                {
                    // このテクスチャのメンバーが無い: 最初の島を主種にしてメンバーを作る
                    var head = seeds[0];
                    into = EditGroups.LinkSeedBased(component, edit, texture, head.renderer, head.submesh, head.triangle, head.uv,
                        SampleSwatchColor(texture, head.uv));
                    if (into == null)
                    {
                        result.added -= seeds.Count;
                        continue;
                    }
                    result.membersCreated++;
                    from = 1;
                }
                if (into.extraSeeds == null) into.extraSeeds = new List<RecolorSeed>();
                for (int i = from; i < seeds.Count; i++) into.extraSeeds.Add(seeds[i]);
            }
            EditorUtility.SetDirty(component);
            // 連結のメンバーが増えたら、ハイライトする編集の集合を取り直す
            if (result.membersCreated > 0) ToolSession.PublishHighlight();
            if (skippedOtherTexture > 0) ToolSession.ShowTransientNotice("Scene:Panel:CrossTextureOffRect", skippedOtherTexture);
            return result;
        }

        /// <summary>island が edit（null なら無し）の主種・追加の種、またはこれから足す種 pending のどれかと同じ島か</summary>
        private static bool ContainsIsland(RecolorEdit edit, List<RecolorSeed> pending, in RectIsland island)
        {
            if (edit != null)
            {
                if (IsSameIsland(edit.seedRenderer, edit.seedSubmesh, edit.seedTriangle, island.renderer, island.submesh, island.triangle)) return true;
                if (edit.extraSeeds != null)
                {
                    foreach (var seed in edit.extraSeeds)
                    {
                        if (seed != null && IsSameIsland(seed.renderer, seed.submesh, seed.triangle, island.renderer, island.submesh, island.triangle)) return true;
                    }
                }
            }
            foreach (var seed in pending)
            {
                if (IsSameIsland(seed.renderer, seed.submesh, seed.triangle, island.renderer, island.submesh, island.triangle)) return true;
            }
            return false;
        }

        /// <summary>
        /// 対象未設定時のクリック。シーンの全 Renderer にレイを当て、当たったモデルのルート（アバター or Hierarchy 最上位）を対象にする。
        /// ルートを覚えるだけでコンポーネントは付けない。Renderer の列挙はクリック時だけ（毎フレームは取らない）
        /// </summary>
        private bool TryResolveTargetByClick(Vector2 mousePosition, out GameObject root)
        {
            root = null;
            if (_picker == null) _picker = new ScenePicker(); // ドメインリロード直後の保険
            _renderers.Clear();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (IsPickable(r)) _renderers.Add(r);
            }
            var ray = HandleUtility.GUIPointToWorldRay(mousePosition);
            if (!_picker.TryPick(ray, _renderers, null, out var hit)) return false;

            root = TargetResolver.ResolveRoot(hit.renderer.gameObject);
            if (root == null) return false;
            ToolSession.SetActiveRoot(root);
            return true;
        }

        /// <summary>
        /// クリック位置を拾い、編集を選ぶか作る。forceNew = false なら、色を決めた既存の編集の範囲に当たったときその編集を選ぶ
        /// （新しい編集は作らない）。forceNew = true（Shift＋クリック）なら常に新しい編集を作る。
        /// toggleSeed = true（Ctrl＋クリック）で root に現在の編集があれば、編集は作らずその編集の種を足す／外す（ResolveSeedToggle。forceNew より優先）
        /// </summary>
        private void Pick(GameObject root, Vector2 mousePosition, bool forceNew, bool toggleSeed)
        {
            if (_picker == null) _picker = new ScenePicker(); // ドメインリロード直後の保険
            CollectPickRenderers(root);
            var ray = HandleUtility.GUIPointToWorldRay(mousePosition);
            if (_picker.TryPick(ray, _renderers, null, out var hit))
            {
                ToolSession.LastPick = hit;
                // 見本色はクリック時に 1 回だけ取る（Repaint ごとに GetPixel しない）
                ToolSession.LastPickColor = SampleSwatchColor(hit);

                // Ctrl＋クリック: 現在の編集があればその種を足す／外す。無ければ通常のクリックとして扱う
                if (toggleSeed)
                {
                    var current = root.GetComponent<ClickRecolor>();
                    var edit = current != null ? current.FindEdit(ToolSession.CurrentEditId) : null;
                    if (edit != null)
                    {
                        ResolveSeedToggle(current, edit, hit, ToolSession.LastPickColor);
                        return;
                    }
                }

                // テクスチャ資産に当たったときだけ編集を作る。ClickRecolor はここで初めて付く
                // （Project の Prefab なら GetOrAddComponent が警告して null を返すので何もしない）
                if (TryGetSourceTexture(hit, out _))
                {
                    // コンポーネントが無ければ既存の編集も無く、新しい編集を作るので、ここで付けてよい
                    var component = TargetResolver.GetOrAddComponent(root);
                    if (component != null) ResolveEdit(component, hit, ToolSession.LastPickColor, forceNew);
                }
            }
            else
            {
                ToolSession.LastPick = null;
                ToolSession.LastPickColor = null;
            }
        }

        private const int CrosshairSize = 32;
        private static Texture2D s_crosshair;

        /// <summary>スポイト用の十字カーソル（32×32、中心に隙間のある白い十字＋黒い縁）。ドメインリロードで作り直す</summary>
        private static Texture2D CrosshairCursor
        {
            get
            {
                if (s_crosshair != null) return s_crosshair;
                int n = CrosshairSize, c = n / 2;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
                var px = new Color32[n * n];
                for (int y = 0; y < n; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        int dx = Mathf.Abs(x - c), dy = Mathf.Abs(y - c);
                        // 腕: 中心から 4px 空けて端まで。太さ 1px（白）、その周り 1px は黒い縁
                        bool armX = dy == 0 && dx >= 4 && dx <= 14;
                        bool armY = dx == 0 && dy >= 4 && dy <= 14;
                        bool edgeX = dy <= 1 && dx >= 3 && dx <= 15;
                        bool edgeY = dx <= 1 && dy >= 3 && dy <= 15;
                        bool center = dx <= 1 && dy <= 1 && (dx == 1 || dy == 1);
                        Color32 col = armX || armY ? new Color32(255, 255, 255, 255)
                            : center ? new Color32(255, 255, 255, 220)
                            : edgeX || edgeY ? new Color32(0, 0, 0, 200)
                            : new Color32(0, 0, 0, 0);
                        px[y * n + x] = col;
                    }
                }
                tex.SetPixels32(px);
                tex.Apply(false, false);
                s_crosshair = tex;
                AssemblyReloadEvents.beforeAssemblyReload += () => { if (s_crosshair != null) Object.DestroyImmediate(s_crosshair); s_crosshair = null; };
                return tex;
            }
        }

        /// <summary>スポイト: mousePosition の下のモデルの元の色（見本色と同じ取り方）を現在の編集の色に入れ、スポイトを終える。当たらなければ何もしない</summary>
        private void PickColorWithEyedropper(GameObject root, Vector2 mousePosition)
        {
            if (_picker == null) _picker = new ScenePicker(); // ドメインリロード直後の保険
            CollectPickRenderers(root);
            var ray = HandleUtility.GUIPointToWorldRay(mousePosition);
            if (!_picker.TryPick(ray, _renderers, null, out var hit)) return;
            var color = SampleSwatchColor(hit);
            if (!color.HasValue) return;

            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            if (edit == null) return;
            ToolPanelOverlay.ApplyEyedropperColor(component, edit, color.Value);
            ToolSession.EyedropperActive = false;
        }

        /// <summary>root 配下のクリック・矩形選択の対象の Renderer（IsPickable）を _renderers に入れ直す</summary>
        private void CollectPickRenderers(GameObject root)
        {
            _renderers.Clear();
            var component = root.GetComponent<ClickRecolor>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                // 除外リストの Renderer はクリック・ホバー・エリア選択の対象にしない（選択のガード。見た目には影響しない。ユーザー判断 2026-09-28）
                if (component != null && component.IsExcluded(r)) continue;
                if (IsPickable(r)) _renderers.Add(r);
            }
        }

        /// <summary>
        /// クリック・ホバー・矩形選択で当てる Renderer か: Hierarchy 上で有効・enabled・MeshRenderer / SMR で、
        /// Hierarchy の目のマーク（Scene ビューの表示切り替え）で隠されていないもの。そのとき Scene に見えているものだけを選ぶ（ユーザー要望 2026-09-25）
        /// </summary>
        private static bool IsPickable(Renderer r)
        {
            return r != null && r.gameObject.activeInHierarchy && r.enabled && RendererMeshAccess.IsSupported(r)
                && !SceneVisibilityManager.instance.IsHidden(r.gameObject);
        }

        /// <summary>資産でないテクスチャの警告を出済みのテクスチャ（InstanceID）。クリックのたびに同じ警告を出さないため</summary>
        private static readonly HashSet<int> s_warnedNonAssetTextures = new HashSet<int>();

        /// <summary>
        /// 編集の対象にできるメインテクスチャ（プロジェクトの Texture2D 資産）を返す。
        /// テクスチャが無ければ黙って false、資産でなければ（実行時に作られたテクスチャなど）テクスチャごとに 1 回だけ警告して false
        /// </summary>
        private static bool TryGetSourceTexture(in PickHit hit, out Texture2D texture)
        {
            return TryGetSourceTexture(hit.hasMainTexture ? hit.mainTexture.texture : null, out texture);
        }

        /// <summary>candidate が編集の対象にできるテクスチャ資産なら true（TryGetSourceTexture(PickHit) と同じ規則）</summary>
        internal static bool TryGetSourceTexture(Texture2D candidate, out Texture2D texture)
        {
            texture = candidate;
            if (texture == null) return false;
            if (AssetDatabase.Contains(texture)) return true;
            if (s_warnedNonAssetTextures.Add(texture.GetInstanceID()))
            {
                Debug.LogWarning($"[Tocolo] テクスチャ '{texture.name}' はプロジェクトの資産ではないため色を変えられません（実行時に作られたテクスチャなど）", texture);
            }
            texture = null;
            return false;
        }

        /// <summary>
        /// クリックで対象にする編集を決める。forceNew = false で、色を決めた既存の編集の範囲に hit が入っていれば
        /// その編集を選んで返す（直前の色未設定の編集は DiscardPendingEdit で消し、CurrentEditId をその編集にする。新しい編集は作らない）。
        /// それ以外（forceNew = true・既存に当たらない）は CreateEditFromHit で新しい編集を作って返す
        /// </summary>
        internal static RecolorEdit ResolveEdit(ClickRecolor component, in PickHit hit, Color? seedColor, bool forceNew)
        {
            if (component == null) return null;
            var existing = forceNew ? null : FindEditContaining(component, hit);
            if (existing == null) return CreateEditFromHit(component, hit, seedColor);

            // 同じ編集の再クリックなら何もしない（色未設定の編集は選ばれないので、消すべき保留中の編集も無い）
            if (ToolSession.CurrentEditId != existing.id)
            {
                DiscardPendingEdit();
                ToolSession.CurrentEditId = existing.id;
            }
            return existing;
        }

        /// <summary>
        /// Ctrl＋クリック: 現在の編集 edit に hit の島（種）を足すか、既に含まれていれば外す。
        /// edit が「アバター全体」の連結なら何もしない（一時メッセージを出す。別のテクスチャなら CtrlOtherTexture、同じなら CtrlGroupNotice）。
        /// hit のテクスチャが edit と違うときは、edit の連結にそのテクスチャのメンバーがあればそのメンバーに対して足す／外す。
        /// 無ければ hit を主種にしてメンバーを作り、島の連結に足す（EditGroups.LinkSeedBased。元の色は seedColor）。
        /// どちらも CurrentEditId は操作したメンバーに移す（パネルは連結全体に効くので、種の表示のため）。
        /// 含まれているかは、島モードなら主種か追加の種と同じ (メッシュ, サブメッシュ, チャート)、
        /// 色モードなら追加の種と同じ (メッシュ, サブメッシュ, チャート) か（色モードの主種は外さない）。
        /// 主種を外すときは追加の種の先頭を主種にし、種が 0 になるなら編集を削除する（色を決めた編集でも削除。CurrentEditId も外す）。
        /// 島の連結のメンバーなら、そのメンバーだけ削除し（連結の残りが 1 件なら単独の編集に戻す）、CurrentEditId を連結の残りに移す。
        /// 変更は Undo 記録つき
        /// </summary>
        internal static SeedToggleResult ResolveSeedToggle(ClickRecolor component, RecolorEdit edit, in PickHit hit, Color? seedColor = null)
        {
            if (component == null || edit == null) return SeedToggleResult.None;
            var texture = hit.hasMainTexture ? hit.mainTexture.texture : null;
            if (texture == null) return SeedToggleResult.None;
            bool wholeAvatar = EditGroups.IsWholeAvatarGroup(edit);
            if (texture != edit.sourceTexture)
            {
                if (wholeAvatar)
                {
                    ToolSession.ShowTransientNotice("Scene:Panel:CtrlOtherTexture");
                    return SeedToggleResult.OtherTexture;
                }
                if (!ToolSession.CrossTextureEnabled)
                {
                    ToolSession.ShowTransientNotice("Scene:Panel:CrossTextureOff");
                    return SeedToggleResult.OtherTexture;
                }
                // 資産でないテクスチャ（実行時に作られたものなど）は編集にできない（警告は TryGetSourceTexture が 1 回だけ出す）
                if (!TryGetSourceTexture(texture, out texture)) return SeedToggleResult.None;

                var member = EditGroups.FindMemberForTexture(component, edit, texture);
                if (member == null)
                {
                    Undo.RecordObject(component, "Tocolo: 別のテクスチャの島を追加");
                    member = EditGroups.LinkSeedBased(component, edit, texture,
                        hit.renderer, hit.subMeshIndex, hit.triangleIndex, hit.uv, seedColor);
                    if (member == null) return SeedToggleResult.None;
                    EditorUtility.SetDirty(component);
                    ToolSession.CurrentEditId = member.id;
                    ToolSession.SetEditHasTwin(member.id, HasUvTwin(hit.renderer, hit.subMeshIndex, hit.triangleIndex));
                    ToolSession.SetEditIsHairToolTarget(member.id, HairToolTargetDetector.IsTargetOf(component.gameObject, hit.renderer));
                    return SeedToggleResult.MemberAdded;
                }
                edit = member;
                if (ToolSession.CurrentEditId != edit.id) ToolSession.CurrentEditId = edit.id;
            }
            else if (wholeAvatar)
            {
                ToolSession.ShowTransientNotice("Scene:Panel:CtrlGroupNotice");
                return SeedToggleResult.Grouped;
            }

            Undo.RecordObject(component, "Tocolo: 種を追加／外す");
            if (edit.extraSeeds == null) edit.extraSeeds = new List<RecolorSeed>();

            // 島モードは主種の島もクリックで外せる
            if (edit.mode == SelectionMode.Island
                && IsSameIsland(edit.seedRenderer, edit.seedSubmesh, edit.seedTriangle, hit.renderer, hit.subMeshIndex, hit.triangleIndex))
            {
                if (edit.extraSeeds.Count == 0)
                {
                    if (EditGroups.IsGrouped(edit))
                    {
                        // 島の連結のメンバーの最後の種 = そのメンバーだけ削除（連結の残りは残す）
                        var rest = EditGroups.RemoveMember(component, edit);
                        EditorUtility.SetDirty(component);
                        ToolSession.CurrentEditId = rest?.id;
                        return rest != null ? SeedToggleResult.MemberRemoved : SeedToggleResult.EditRemoved;
                    }
                    // 最後の種を外す = 編集を削除
                    EditGroups.RemoveGroup(component, edit);
                    EditorUtility.SetDirty(component);
                    ToolSession.CurrentEditId = null;
                    return SeedToggleResult.EditRemoved;
                }
                var head = edit.extraSeeds[0];
                edit.extraSeeds.RemoveAt(0);
                edit.seedRenderer = head.renderer;
                edit.seedSubmesh = head.submesh;
                edit.seedTriangle = head.triangle;
                edit.seedUv = head.uv;
                EditorUtility.SetDirty(component);
                return SeedToggleResult.Removed;
            }

            for (int i = 0; i < edit.extraSeeds.Count; i++)
            {
                var seed = edit.extraSeeds[i];
                if (seed == null) continue;
                if (!IsSameIsland(seed.renderer, seed.submesh, seed.triangle, hit.renderer, hit.subMeshIndex, hit.triangleIndex)) continue;
                edit.extraSeeds.RemoveAt(i);
                EditorUtility.SetDirty(component);
                return SeedToggleResult.Removed;
            }

            edit.extraSeeds.Add(new RecolorSeed
            {
                renderer = hit.renderer,
                submesh = hit.subMeshIndex,
                triangle = hit.triangleIndex,
                // マテリアルの Tiling/Offset 適用後の UV（主種と同じ座標）
                uv = hit.uv,
            });
            EditorUtility.SetDirty(component);
            return SeedToggleResult.Added;
        }

        /// <summary>
        /// 2 つの (Renderer, サブメッシュ, 三角形) が同じ島か: 同じメッシュ・同じサブメッシュで、三角形の UV チャートが同じ。
        /// メッシュかチャート表が取れなければ、同じ Renderer・サブメッシュ・三角形のときだけ同じとみなす
        /// </summary>
        private static bool IsSameIsland(Renderer a, int submeshA, int triangleA, Renderer b, int submeshB, int triangleB)
        {
            if (a == null || b == null || submeshA != submeshB) return false;
            var meshA = RendererMeshAccess.GetSharedMesh(a);
            var meshB = RendererMeshAccess.GetSharedMesh(b);
            if (meshA == null || meshB == null) return a == b && triangleA == triangleB;
            if (meshA != meshB) return false;
            var table = UvChartDetector.GetOrBuild(meshA, submeshA);
            if (table == null
                || triangleA < 0 || triangleA >= table.chartOfTriangle.Length
                || triangleB < 0 || triangleB >= table.chartOfTriangle.Length)
            {
                return triangleA == triangleB;
            }
            return table.chartOfTriangle[triangleA] == table.chartOfTriangle[triangleB];
        }

        /// <summary>
        /// クリック結果から編集を 1 件作って component に追加し、ToolSession.CurrentEditId をその編集にする。
        /// 直前の編集（CurrentEditId）がまだ色未設定（hasTarget=false）なら、同じ Undo 記録の中で先に削除する
        /// （直前の編集が別のコンポーネントにあっても消す。DiscardPendingEdit）。
        /// 対象テクスチャが無い・資産でないときは何もせず null。
        /// 新しい色の初期値は元の色（hasTarget は false のまま）。既定の白のままだと、白を最初に選んだとき変化として拾えない。
        /// 選択のモード・しきい値・ぼかし・範囲は影響範囲のプリセット（ToolSession.Preset）で決める（ApplyPreset）。
        /// 色モードで範囲が「アバター全体」なら、対象ルート配下の他のメインテクスチャにも同じ設定の編集を作って連結する
        /// （EditGroups.Link。同じ Undo 記録の中。CurrentEditId はクリックしたテクスチャの編集）
        /// </summary>
        internal static RecolorEdit CreateEditFromHit(ClickRecolor component, in PickHit hit, Color? seedColor)
        {
            if (component == null || !TryGetSourceTexture(hit, out var texture)) return null;
            // マテリアルの Tiling/Offset 適用後の UV（島マスクの計算が前提とする座標）
            return CreateEditFromSeed(component, hit.renderer, hit.subMeshIndex, hit.triangleIndex, hit.uv, texture, seedColor);
        }

        /// <summary>
        /// 種の情報（Renderer・サブメッシュ・サブメッシュ内の三角形・Tiling/Offset 適用後の UV・対象テクスチャ）から編集を 1 件作る。
        /// CreateEditFromHit と Ctrl＋ドラッグの矩形選択が共用する。作成の規則は CreateEditFromHit の説明のとおり。
        /// texture が無い・資産でないときは何もせず null
        /// </summary>
        internal static RecolorEdit CreateEditFromSeed(ClickRecolor component, Renderer renderer, int submesh, int triangle, Vector2 uv, Texture2D texture, Color? seedColor)
        {
            if (component == null || !TryGetSourceTexture(texture, out texture)) return null;

            DiscardPendingEdit();
            Undo.RecordObject(component, "Tocolo: 編集を追加");

            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = texture;
            edit.seedRenderer = renderer;
            edit.seedSubmesh = submesh;
            edit.seedTriangle = triangle;
            edit.seedUv = uv;
            edit.seedColor = seedColor ?? Color.gray;
            edit.targetColor = edit.seedColor;
            edit.gradientColor = edit.seedColor; // 終了色の初期値も元の色
            ApplyPreset(edit, ToolSession.Preset);
            edit.padding = 8;
            component.AddEdit(edit);
            // 「箱の中」で作った編集は、クリックしたパーツを囲む箱から始める（AddEdit の後: 箱の既定は編集のマスクから決める）。
            // 「マテリアルをまたいで選ぶ」ON なら箱に触れる他テクスチャのメンバーも作る
            if (edit.mode == SelectionMode.Box)
            {
                SelectionBox.ResetToDefault(component, edit);
                SelectionBox.SyncMembers(component, edit);
            }
            if (edit.mode == SelectionMode.Color && edit.scope == ColorScope.WholeAvatar) EditGroups.Link(component, edit);

            EditorUtility.SetDirty(component);
            ToolSession.CurrentEditId = edit.id;
            // 双子判定は覚えるだけ（パネルの案内は撤去済み。将来の表示や警告に使えるよう残す）
            ToolSession.SetEditHasTwin(edit.id, HasUvTwin(renderer, submesh, triangle));
            // 髪ツールの対象なら、パネルで「先に書き出してから」の警告を出す
            ToolSession.SetEditIsHairToolTarget(edit.id, HairToolTargetDetector.IsTargetOf(component.gameObject, renderer));
            return edit;
        }

        /// <summary>
        /// (renderer, submesh, triangle) の三角形のチャートに、同じ UV を使う別のパーツ（3D 位置が違う同じ UV の三角形）があるか。
        /// 編集作成時にパネルの注記（島モードのとき表示）のために 1 回だけ呼ぶ。判定できなければ false
        /// </summary>
        private static bool HasUvTwin(Renderer renderer, int submesh, int triangle)
        {
            var mesh = RendererMeshAccess.GetSharedMesh(renderer);
            var table = UvChartDetector.GetOrBuild(mesh, submesh);
            if (table == null || triangle < 0 || triangle >= table.chartOfTriangle.Length) return false;
            return UvTwinDetector.HasTwin(mesh, submesh, table.chartOfTriangle[triangle], table);
        }

        /// <summary>
        /// hit の位置を選択範囲に含む編集を返す（無ければ null）。対象は目標色を決めた（hasTarget）編集で、
        /// 対象テクスチャが hit のメインテクスチャと同じもの。後ろの編集ほど上に重なって効くので末尾から探す。
        /// 判定は編集の選択マスク（プレビューと同じ作業解像度。MaskCache にあればそれ、無ければ PrepareJob で作る）の
        /// hit.uv の画素が 0.5 を超えるか。マスクが作れない編集は飛ばす
        /// </summary>
        internal static RecolorEdit FindEditContaining(ClickRecolor component, in PickHit hit)
        {
            if (component == null || component.edits == null || !hit.hasMainTexture) return null;
            var texture = hit.mainTexture.texture;
            if (texture == null) return null;

            List<(Mesh, int, Vector2, Vector2)> users = null;
            List<Renderer> renderers = null;
            int prepared = 0;
            try
            {
                for (int i = component.edits.Count - 1; i >= 0; i--)
                {
                    var edit = component.edits[i];
                    // 無効化した編集は拾わない（選んでも縞が出ず、色を変えても見た目が変わらないため。レビュー指摘 2026-09-25）
                    if (edit == null || !edit.enabled || !edit.hasTarget || edit.sourceTexture != texture) continue;

                    // 利用者はプレビューと同じ集め方にする（マスクの鍵が揃い、プレビューが作ったマスクに当たる）
                    renderers ??= CollectPreviewRenderers(component.gameObject);
                    users ??= RecolorPreview.CollectUsers(renderers, texture);
                    var job = RecolorPipeline.PrepareJob(edit, texture, (int)component.previewResolution, users,
                        context: MaskContext.For(component, renderers));
                    if (job?.mask == null) continue;
                    prepared++;
                    if (IsSelectedAt(job.mask, hit.uv)) return edit;
                }
                return null;
            }
            finally
            {
                // PrepareJob の約束どおり、使い終わったら 1 回だけ減らす
                if (prepared > 0) MaskCache.Trim();
            }
        }

        /// <summary>root 配下の、プレビューが対象にする Renderer（MeshRenderer / SkinnedMeshRenderer、Hierarchy 上で有効なもの）</summary>
        internal static List<Renderer> CollectPreviewRenderers(GameObject root)
        {
            var result = new List<Renderer>();
            var component = root.GetComponent<ClickRecolor>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                if (!renderer.gameObject.activeInHierarchy) continue;
                result.Add(renderer);
            }
            return result;
        }

        /// <summary>
        /// マスク（R8）の uv の画素が 0.5 を超えるか。
        /// 部分矩形の ReadPixels は DirectX で y の原点が全体読みと逆になる（実機で確認）ので使わず、
        /// CopyTexture で 1 画素を 1×1 の RT に写してから全体を読む（SeedColorSampler と同じ流儀）
        /// </summary>
        private static bool IsSelectedAt(RenderTexture mask, Vector2 uv)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(uv.x * mask.width), 0, mask.width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(uv.y * mask.height), 0, mask.height - 1);

            if ((SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.Basic) == 0)
            {
                // CopyTexture が使えない環境は全体を読む（遅いが向きは正しい）
                var all = Nekoare.ClickRecolor.Editor.Masks.FloodFill.ReadR8(mask);
                return all[y * mask.width + x] > 127;
            }

            var desc = new RenderTextureDescriptor(1, 1, mask.format, 0)
            {
                sRGB = false, useMipMap = false, autoGenerateMips = false, msaaSamples = 1,
            };
            var region = RenderTexture.GetTemporary(desc);
            if (!region.IsCreated()) region.Create();
            var texel = new Texture2D(1, 1, TextureFormat.R8, false, true);
            var previous = RenderTexture.active;
            try
            {
                Graphics.CopyTexture(mask, 0, 0, x, y, 1, 1, region, 0, 0, 0, 0);
                RenderTexture.active = region;
                texel.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                return texel.GetRawTextureData()[0] > 127;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(texel);
                RenderTexture.ReleaseTemporary(region);
            }
        }

        /// <summary>
        /// 影響範囲のプリセットを編集の選択仕様にする。
        /// 同じ色 = 色モード（しきい値 0.08・ぼかし 0.05）、似た色 = 色モード（0.30・0.15）、島 = 島モード。
        /// 色モードの範囲は最後にパネルで選んだ範囲（ToolSession.ColorScopePreference。既定はつながった範囲）。
        /// 「アバター全体」になったときの連結（EditGroups.Link）は呼び出し側で行う
        /// </summary>
        internal static void ApplyPreset(RecolorEdit edit, RangePreset preset)
        {
            switch (preset)
            {
                case RangePreset.SameColor:
                    edit.mode = SelectionMode.Color;
                    edit.threshold = 0.08f;
                    edit.feather = 0.05f;
                    edit.scope = ToolSession.ColorScopePreference;
                    break;
                case RangePreset.SimilarColor:
                    edit.mode = SelectionMode.Color;
                    edit.threshold = 0.30f;
                    edit.feather = 0.15f;
                    edit.scope = ToolSession.ColorScopePreference;
                    break;
                case RangePreset.Box:
                    edit.mode = SelectionMode.Box;
                    edit.feather = 0f; // 面のぼかしは既定 0（面でぱきっと切る。ユーザー要望 2026-09-29）
                    break;
                default:
                    edit.mode = SelectionMode.Island;
                    break;
            }
        }

        /// <summary>
        /// 現在の編集（ToolSession.CurrentEditId）がまだ色未設定（hasTarget=false）なら削除し（Undo 記録つき。「アバター全体」の連結なら連結ごと）、
        /// その後 CurrentEditId を外す（色を決めた編集は残し、選択だけ外す）。
        /// 対象ルートを切り替えた後でも消せるよう、編集はシーンの全 ClickRecolor から探す
        /// </summary>
        internal static void DiscardPendingEdit()
        {
            string id = ToolSession.CurrentEditId;
            if (id == null) return;

            foreach (var component in Object.FindObjectsOfType<ClickRecolor>(true))
            {
                if (component == null) continue;
                var edit = component.FindEdit(id);
                if (edit == null) continue;
                if (!edit.hasTarget)
                {
                    Undo.RecordObject(component, "Tocolo: 色未設定の編集を削除");
                    EditGroups.RemoveGroup(component, edit);
                    EditorUtility.SetDirty(component);
                }
                break;
            }
            ToolSession.CurrentEditId = null;
        }

        /// <summary>
        /// ヒット位置にリング（見本色の補色・明度反転＋黒の外枠）＋色見本、横にラベル（Renderer / マテリアル / テクスチャ）。
        /// 流用元: dev.nekoare.tex-col-adjuster/Editor/TexColAdjusterWindow.ScenePick.cs（DrawScenePickMarker）
        /// </summary>
        private static void DrawPickMarker(in PickHit hit)
        {
            // クリック後に Renderer が消された場合は描かない（name 参照で例外になるため）
            if (hit.renderer == null) return;

            Color swatchColor = ToolSession.LastPickColor ?? Color.gray;
            float size = HandleUtility.GetHandleSize(hit.worldPosition) * 0.08f;

            var prev = Handles.color;
            DrawRingOutline(hit.worldPosition, hit.worldNormal, size);
            Handles.color = PickRingColor(ToolSession.LastPickColor);
            Handles.DrawWireDisc(hit.worldPosition, hit.worldNormal, size);
            Handles.color = swatchColor;
            Handles.DrawSolidDisc(hit.worldPosition, hit.worldNormal, size * 0.7f);
            Handles.color = prev;

            string materialName = hit.material != null ? hit.material.name : "-";
            string textureName = hit.hasMainTexture && hit.mainTexture.texture != null ? hit.mainTexture.texture.name : "-";
            string label = Locales.Tr("Scene:Label:Format", hit.renderer.name, materialName, textureName);

            // マーカー横にラベル＋色見本
            Handles.BeginGUI();
            Vector2 guiPos = HandleUtility.WorldToGUIPoint(hit.worldPosition);
            var labelContent = new GUIContent(label);
            var style = EditorStyles.helpBox;
            Vector2 labelSize = style.CalcSize(labelContent);
            const float swatch = 14f;
            var rect = new Rect(guiPos.x + 14f, guiPos.y - labelSize.y - 6f, labelSize.x + swatch + 14f, labelSize.y + 6f);
            GUI.Label(rect, GUIContent.none, style);
            EditorGUI.DrawRect(new Rect(rect.x + 4f, rect.y + (rect.height - swatch) / 2f, swatch, swatch),
                new Color(swatchColor.r, swatchColor.g, swatchColor.b, 1f));
            GUI.Label(new Rect(rect.x + swatch + 8f, rect.y + 3f, labelSize.x + 4f, labelSize.y), labelContent, EditorStyles.miniLabel);
            Handles.EndGUI();
        }

        /// <summary>
        /// 見本色の読み取りに使う縮小コピーの長辺。クリックのたびに取って即手放すので小さくてよい
        /// </summary>
        private const int SwatchSampleSize = 256;

        /// <summary>
        /// 見本色。クリック位置の色を元ファイル（PNG / JPG を直接デコードした縮小コピー）から取る。
        /// 直読みできない形式はインポート済みの資産を使うので、Read/Write 有効かつ Crunch でないときだけ取れる。
        /// 取れなければ null（表示側で灰色）
        /// </summary>
        private static Color? SampleSwatchColor(in PickHit hit)
        {
            // hit.uv は Tiling/Offset 適用後で 0〜1 に畳まれている（MaterialTextureResolver.ToTextureCoord）
            return SampleSwatchColor(hit.hasMainTexture ? hit.mainTexture.texture : null, hit.uv);
        }

        /// <summary>見本色を texture の uv（Tiling/Offset 適用後、0〜1）から取る。規則は SampleSwatchColor(PickHit) と同じ</summary>
        internal static Color? SampleSwatchColor(Texture2D texture, Vector2 uv)
        {
            if (texture == null) return null;

            using (var source = SourceTextureLoader.Acquire(texture, SwatchSampleSize))
            {
                if (source == null || !source.IsValid) return null;
                var readable = source.Texture;
                if (!source.IsDirectRead)
                {
                    // インポート済みの資産そのもの: Read/Write 無効・Crunch は CPU から読めない
                    if (!readable.isReadable
                        || UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsCrunchFormat(readable.format))
                    {
                        return null;
                    }
                }
                return readable.GetPixelBilinear(uv.x, uv.y);
            }
        }
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
                // 除外リストはボタンで開くので、ツールの終了時に閉じるだけ
                if (!displayed && view.TryGetOverlay(ExcludeListOverlay.Id, out Overlay exclude)) exclude.displayed = false;
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

        /// <summary>
        /// Inspector のボタンから呼ぶ。対象（ルート）を ToolSession に入れてツールを有効化する
        /// （グローバルツールなので選択は変えない。コンポーネントは付けない）
        /// </summary>
        public static void Activate(GameObject root)
        {
            if (root == null) return;
            // Project の Prefab アセットは Scene でクリックできないので対象にしない
            if (EditorUtility.IsPersistent(root)) return;
            ToolSession.SetActiveRoot(root);
            ToolManager.SetActiveTool<RecolorSceneTool>();
        }

        public static bool IsActive => ToolManager.activeToolType == typeof(RecolorSceneTool);

        public static void Deactivate()
        {
            if (IsActive) ToolManager.RestorePreviousTool();
        }
    }
}
