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
    internal sealed partial class RecolorSceneTool : EditorTool
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
        /// <summary>クリック位置に Scene で描かれている Renderer（CollectVisibleRenderers の結果。TryPickVisible の作業用）</summary>
        private readonly List<Renderer> _visibleRenderers = new List<Renderer>();
        /// <summary>前回ホバーを判定した時刻（EditorApplication.timeSinceStartup）</summary>
        private double _lastHoverPickTime;

        // Ctrl＋押下中の状態（クリックか矩形かは離したときに決まる）。ドメインリロードで消えてよい
        private bool _ctrlPressPending;
        /// <summary>
        /// 押下（Ctrl＋押下・画像の上の押下）で掴んだマウスの部品の番号（0 = 掴んでいない）。離しを取りこぼしたり、ツールのインスタンスが替わって押下中の印が消えたりして
        /// 掴んだまま残ると、Scene の左右クリックが効かなくなる（ユーザー報告 2026-10-06）。残っていたら OnToolGUI で放す
        /// </summary>
        private static int s_capturedControl;
        /// <summary>Scene のクリック・矩形の部品の番号の印。印なしだと Scene でこれより前に描かれる部品の数で番号がずれ、離しが自分宛てと分からなくなる</summary>
        private static readonly int s_controlHint = "ClickRecolor.SceneTool".GetHashCode();
        // 診断用（2026-10-06。Scene のクリックが効かなくなる件の再発・報告の切り分けに残す。原因が確定したら消す）: 同じ部品の掴みを何度も記録しない
        private static int s_lastLoggedForeignControl;
        private bool _rectActive;
        private bool _ctrlPressShift;
        private Vector2 _rectStart;
        private Vector2 _rectEnd;

        // 選んでいる編集の画像の上での押下中の状態（RectDragThreshold 以上動かせば画像を表面に沿って動かし、動かさずに離せばふつうのクリック）。
        // 掴んだマウスの番号は Ctrl＋押下と同じ s_capturedControl で持つ。ドメインリロードで消えてよい
        private bool _imagePressPending;
        private Vector2 _imagePressStart;
        /// <summary>掴んだ点（ワールド）。画像の上の当たりか、シールの枠の面とレイの交点</summary>
        private Vector3 _imagePressGrabWorld;
        /// <summary>シールの枠の中を押したか（動かさずに離しても、枠の向こうに見えている物をクリックしたことにはしない）</summary>
        private bool _imagePressFromFrame;
        /// <summary>画像を動かしている途中なら、その 1 回分（動かし始める前は null）</summary>
        private DecalSurfaceDrag _imageDrag;

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
            // 箱のつまみを掴んだまま Esc・Play で抜けると離しが来ない。ドラッグ中の印が残ると軽い作り方（パーツごとの統計・
            // スーパーサンプリングなし・低解像度の層）のままになるので、ここで下ろしてプレビューに作り直させる
            EndBoxDrags();
            // Ctrl＋ドラッグの途中なら矩形を捨てる（掴んだマウスも放す）
            EndPress();
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
            ToolSession.UnreadableRenderer = null;
            ToolSession.BoxChoiceEditId = null;
            // シールの面探しに読み戻した選択マスクを手放す
            StickerSurface.Clear();
            ToolSession.HoverPick = null;
            // クリック用に取っておいた元テクスチャのデコード結果を手放す（プレビューが使っている分は残る）
            SourceTextureLoader.TrimIdle();
            SceneView.RepaintAll();
        }

        /// <summary>押下（Ctrl＋押下・画像の上の押下）を終える（掴んだマウスを放し、押下中の印を下ろす）</summary>
        private void EndPress()
        {
            if (s_capturedControl != 0 && GUIUtility.hotControl == s_capturedControl) GUIUtility.hotControl = 0;
            s_capturedControl = 0;
            _ctrlPressPending = false;
            _rectActive = false;
            _imagePressPending = false;
            _imageDrag = null;
        }

        /// <summary>
        /// 押下（Ctrl＋押下・画像の上の押下）で掴んだマウスが残っていたら放す: 押下中の印が無いのに掴んだまま（ツールのインスタンスが替わった等）、
        /// 離しを取りこぼしたまま次の押下が来た、ほかで放された・取られた。どれも押下（クリック・矩形・画像の移動の続き）は行わずに捨てる
        /// </summary>
        private void ReleaseStaleCapture(Event e)
        {
            if (s_capturedControl == 0) return;
            string reason = null;
            if (GUIUtility.hotControl != s_capturedControl) reason = "ほかで放された";
            else if (!_ctrlPressPending && !_imagePressPending) reason = "押下中の印が無いのに掴んだまま";
            else if (e.type == EventType.MouseDown) reason = "離しを取りこぼしたまま次の押下";
            if (reason == null) return;
            // ほかで放されたのは異常ではない（ツールの切り替え等）ので記録しない
            if (GUIUtility.hotControl == s_capturedControl) Debug.Log($"[Tocolo][調査] 掴んだままのマウスを放しました（{reason}）"); // 診断用（2026-10-06。Scene のクリックが効かなくなる件の再発・報告の切り分けに残す。原因が確定したら消す）
            EndPress();
        }

        public override void OnToolGUI(EditorWindow window)
        {
            if (!(window is SceneView sceneView)) return;

            var e = Event.current;
            // 小窓「箱を表示」はグラデーションか画像が ON の編集を選んでいる間だけ出す（Prefab 編集中は出さない）
            if (e.type == EventType.Layout) BoxToggleOverlay.SyncDisplayed(sceneView);
            // control ID の並びを全イベントで一定にするため、分岐より前に取る（印付きにして、他の部品の数で番号がずれないようにする）
            int controlId = GUIUtility.GetControlID(s_controlHint, FocusType.Passive);
            // 掴んだまま残ったマウスを放す（Scene の左右クリックが効かなくなるのを防ぐ）
            ReleaseStaleCapture(e);
            // 押下中の Esc は押下（矩形・クリック・画像の移動）だけ中止する（ツールは終了しない）。画像を動かしていたら掴む前の位置に戻す
            if ((_ctrlPressPending || _imagePressPending) && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                e.Use();
                RestoreImageDragStart();
                EndPress();
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

            // 選択の箱の離しは、s_boxDrag を締める DrawGradientBox より前に片付ける（メンバーの同期を同じ 1 回の Undo に入れる）
            FinishSelectionBoxDragIfReleased();
            // グラデーションの箱のハンドル。Layout でも呼んで距離を登録する（ハンドルに近いクリックはハンドルが取り、Pick に来ない）
            DrawGradientBox();
            DrawDecalBox();
            DrawSelectionBox();
            // 箱のハンドルなど他のコントロールがマウスを掴んでいる間は、クリック・ドラッグを処理しない
            if (e.isMouse && GUIUtility.hotControl != 0 && GUIUtility.hotControl != controlId && GUIUtility.hotControl != s_capturedControl)
            {
                // 診断用（2026-10-06。Scene のクリックが効かなくなる件の再発・報告の切り分けに残す。原因が確定したら消す）: 左の押下が誰にも使われていないのに、ほかの部品が掴んだまま（取り残し）なら記録する
                if (e.type == EventType.MouseDown && e.button == 0 && !e.alt && GUIUtility.hotControl != s_lastLoggedForeignControl)
                {
                    s_lastLoggedForeignControl = GUIUtility.hotControl;
                    Debug.Log($"[Tocolo][調査] Scene をクリックしたとき、ほかの部品がマウスを掴んだままでした（番号 {GUIUtility.hotControl}、Tocolo は {controlId}）");
                }
                return;
            }

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
                // 押下中に対象が消えた（破棄など）: 押下を捨てて hotControl を返す
                if (_ctrlPressPending || _imagePressPending) EndPress();
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
                    s_capturedControl = controlId;
                    return;
                }
                // 選んでいる編集の画像の上の押下: RectDragThreshold 以上動かせば画像を表面に沿って動かし、動かさなければ離したときにふつうのクリック。
                // シールの枠は手前の物に隠れないよう前面に描くので、枠の中なら手前の髪などに当たっていてもシールを掴む（画面で見えている形と判定をそろえる）。
                // Shift＋クリック（常に新しい編集）は従来どおりすぐ処理する
                if (!e.shift)
                {
                    bool fromFrame = TryHitCurrentStickerFrame(root, e.mousePosition, out var grabWorld);
                    bool onImage = false;
                    if (!fromFrame && TryHitCurrentDecalImage(root, e.mousePosition, out var imageHit))
                    {
                        grabWorld = imageHit.worldPosition;
                        onImage = true;
                    }
                    if (fromFrame || onImage)
                    {
                        _imagePressPending = true;
                        _imagePressStart = e.mousePosition;
                        _imagePressGrabWorld = grabWorld;
                        _imagePressFromFrame = fromFrame;
                        _imageDrag = null;
                        GUIUtility.hotControl = controlId;
                        s_capturedControl = controlId;
                        return;
                    }
                }
                Pick(root, e.mousePosition, forceNew: e.shift, toggleSeed: false);
                sceneView.Repaint();
                return;
            }

            if (_imagePressPending)
            {
                // 押したときに掴んだ番号で受ける（Ctrl＋押下と同じ）
                int pressId = s_capturedControl != 0 ? s_capturedControl : controlId;
                var type = e.GetTypeForControl(pressId);
                if (type == EventType.MouseDrag && GUIUtility.hotControl == pressId)
                {
                    e.Use();
                    if (_imageDrag == null && (e.mousePosition - _imagePressStart).magnitude >= RectDragThreshold) BeginImageDrag(root);
                    if (_imageDrag != null) MoveImage(root, e.mousePosition);
                    sceneView.Repaint();
                    return;
                }
                // 離し: 先に他の部品が使った（Used）離しも rawType で拾う
                if ((type == EventType.MouseUp || e.rawType == EventType.MouseUp) && e.button == 0)
                {
                    if (GUIUtility.hotControl == pressId) e.Use();
                    bool moved = _imageDrag != null;
                    bool fromFrame = _imagePressFromFrame;
                    var start = _imagePressStart;
                    // 掴んだマウスを放すと、次のイベントで DrawDecalBox がドラッグの終わり（フル解像度での作り直し）と Undo のまとめを締める
                    EndPress();
                    // 動かさずに離した: 画像の上ならふつうのクリック（その編集を選び直す）。シールの枠の中なら、枠の向こうの物は選ばずシールを選んだまま
                    if (!moved && !fromFrame) Pick(root, start, forceNew: false, toggleSeed: false);
                    sceneView.Repaint();
                    return;
                }
            }

            if (_ctrlPressPending)
            {
                // 押したときに掴んだ番号で受ける（万一番号がずれても離しを取りこぼさない）
                int pressId = s_capturedControl != 0 ? s_capturedControl : controlId;
                var type = e.GetTypeForControl(pressId);
                if (type == EventType.MouseDrag && GUIUtility.hotControl == pressId)
                {
                    e.Use();
                    _rectEnd = e.mousePosition;
                    // 矩形選択は UV アイランドモード専用（色モードでは矩形を出さず、離したときに Ctrl＋クリック扱い）
                    if (!_rectActive && ToolSession.Mode == SelectionMode.Island && (_rectEnd - _rectStart).magnitude >= RectDragThreshold) _rectActive = true;
                    sceneView.Repaint();
                    return;
                }
                // 離し: 先に他の部品が使った（Used）離しも rawType で拾う
                if ((type == EventType.MouseUp || e.rawType == EventType.MouseUp) && e.button == 0)
                {
                    if (GUIUtility.hotControl == pressId) e.Use();
                    bool rect = _rectActive;
                    EndPress();
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
                        SampleSwatchColor(seed.texture, seed.uv, SwatchWorkingSize(component)));
                }
                if (edit == null) return;
            }
            ApplyRectSeeds(component, edit, islands);
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
            // Scene に描かれている物（他ツールのプレビューで隠れた物・消した面を除く）を優先する
            CollectVisibleRenderers(mousePosition, null, _visibleRenderers);
            if (!TryPickVisible(_visibleRenderers, ray, out var hit)) return false;

            root = TargetResolver.ResolveRoot(hit.renderer.gameObject);
            if (root == null) return false;
            SetTarget(root);
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
            // Read/Write 無効のメッシュは自前のレイ判定に乗らず、クリックが奥の物に抜けてしまう。
            // Unity の Scene ピック（GPU で描いて判定するので Read/Write に関係なく当たる）で手前の物を調べ、
            // それが選べないメッシュなら編集は作らずパネルに案内を出す
            var picked = CollectVisibleRenderers(mousePosition, root, _visibleRenderers);
            var unreadable = FindClickedUnreadable(root, picked, mousePosition);
            ToolSession.UnreadableRenderer = unreadable;
            if (unreadable != null)
            {
                ToolSession.LastPick = null;
                ToolSession.LastPickColor = null;
                SceneView.RepaintAll();
                return;
            }
            var ray = HandleUtility.GUIPointToWorldRay(mousePosition);
            if (TryPickVisible(_visibleRenderers, ray, out var hit))
            {
                ToolSession.LastPick = hit;
                // 見本色はクリック時に 1 回だけ取る（Repaint ごとに GetPixel しない）
                ToolSession.LastPickColor = SampleSwatchColor(hit);
                // lilToon の 2nd／3rd が上に重なっているか（色が変わって見えにくい場所の案内。ユーザー判断 2026-10-01）
                var layerNotice = LilToonLayerOverlap.Check(hit.material, hit.uv0, hit.uv);
                layerNotice.material = hit.material;
                ToolSession.LayerNotice = layerNotice;
                ToolSession.LayerNoticeEditId = null;
                // TexTransTool の効果はプレビューでこの色変えの上に乗らない（アップロード時は乗る）ので案内する
                ToolSession.TexTransToolNotice = TexTransToolDetector.HasActive(root);

                // Ctrl＋クリック: 現在の編集があればその種を足す／外す。無ければ通常のクリックとして扱う
                if (toggleSeed)
                {
                    var current = root.GetComponent<ClickRecolor>();
                    var edit = current != null ? current.FindEdit(ToolSession.CurrentEditId) : null;
                    if (edit != null)
                    {
                        ResolveSeedToggle(current, edit, hit, ToolSession.LastPickColor);
                        ToolSession.LayerNoticeEditId = ToolSession.CurrentEditId;
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
                    ToolSession.LayerNoticeEditId = ToolSession.CurrentEditId;
                }
            }
            else
            {
                ToolSession.LastPick = null;
                ToolSession.LastPickColor = null;
                ToolSession.LayerNotice = default;
                ToolSession.LayerNoticeEditId = null;
                ToolSession.TexTransToolNotice = false;
                // テクスチャの無いマテリアルにだけ当たった（素通りした先に何も無い）ときは、無反応にせず理由を出す
                if (_pickBlockedByTextureless || _picker.LastSkippedTextureless) ToolSession.ShowTransientNotice("Scene:Panel:NoTextureMaterial");
            }
        }

        /// <summary>
        /// mousePosition が、選んでいる編集（連結ならそのメンバー）の画像の上か。範囲の再クリックで画像入りの編集を選ぶときと同じ判定
        /// （FindEditContaining: 範囲の中で、画像の不透明な所）にして、つかめる所と選び直せる所を揃える
        /// </summary>
        private bool TryHitCurrentDecalImage(GameObject root, Vector2 mousePosition, out PickHit hit)
        {
            hit = default;
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            if (edit == null || !edit.enabled || !edit.HasDecal) return false;
            if (_picker == null) _picker = new ScenePicker(); // ドメインリロード直後の保険
            CollectPickRenderers(root);
            CollectVisibleRenderers(mousePosition, root, _visibleRenderers);
            if (!TryPickVisible(_visibleRenderers, HandleUtility.GUIPointToWorldRay(mousePosition), out hit)) return false;
            var found = FindEditContaining(component, hit);
            if (found == null || !found.HasDecal) return false;
            return found == edit || (!string.IsNullOrEmpty(edit.groupId) && found.groupId == edit.groupId);
        }

        /// <summary>
        /// mousePosition のレイが、選んでいる編集のシールの枠（Scene に出している面の上の枠。小窓で隠しているときは無し）の中を通るか。
        /// 通るなら枠の面との交点（ワールド）を grabWorld に入れる
        /// </summary>
        private static bool TryHitCurrentStickerFrame(GameObject root, Vector2 mousePosition, out Vector3 grabWorld)
        {
            grabWorld = default;
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            if (edit == null || !edit.enabled || !edit.HasDecal || !edit.decalSticker || !ToolSession.ShowsDecalBox(edit)) return false;
            // 枠を描いている点と同じ（DrawDecalBox）
            var center = StickerSurface.TryFindUnderCenter(component, edit, out var onSurface) ? onSurface : edit.decalBoxPosition;
            var half = StickerHalfExtent(edit.decalBoxSize, edit.decalTexture, edit.decalKeepAspect);
            var ray = HandleUtility.GUIPointToWorldRay(mousePosition);
            var toRoot = component.transform.worldToLocalMatrix;
            if (!DecalBox.TryHitStickerFrame(toRoot.MultiplyPoint3x4(ray.origin), toRoot.MultiplyVector(ray.direction), center,
                    Quaternion.Normalize(edit.decalBoxRotation), half, out var point))
            {
                return false;
            }
            grabWorld = component.transform.localToWorldMatrix.MultiplyPoint3x4(point);
            return true;
        }

        /// <summary>画像を動かし始める（押下で決めた _imagePressGrabWorld を掴んだ点にする）。選んでいる編集が画像入りでなくなっていたら押下を捨てる</summary>
        private void BeginImageDrag(GameObject root)
        {
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            if (edit == null || !edit.HasDecal)
            {
                EndPress();
                return;
            }
            CollectPickRenderers(root);
            _imageDrag = DecalSurfaceDrag.Begin(component, edit, _imagePressGrabWorld, _renderers);
            // 押す前のホバーの丸は、動かしている間は古い位置のままになるので消す
            ToolSession.HoverPick = null;
            s_boxDrag.Begin("Tocolo: 画像を動かす");
            // 箱のギズモのドラッグと同じ表示にする（焼き込みは低解像度、重ね貼りは UV の書き換えで追従。離したら作り直す）
            BoxDragState.BeginDecal(edit, Decal.DecalOverlayMaterial.UseOverlay(component, edit));
        }

        /// <summary>カーソルの下の面へ画像を動かす（連結なら全メンバーに同じ箱）。範囲の外・何にも当たらないときは動かさない</summary>
        private void MoveImage(GameObject root, Vector2 mousePosition)
        {
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(_imageDrag.EditId) : null;
            if (edit == null) return;
            if (!_picker.TryPick(HandleUtility.GUIPointToWorldRay(mousePosition), _imageDrag.Renderers, null, out var hit)) return;
            if (!_imageDrag.TryMove(component, edit, hit, out var position, out var rotation)) return;
            Undo.RecordObject(component, "Tocolo: 画像を動かす");
            EditGroups.ForEachInGroup(component, edit, m =>
            {
                m.decalBoxPosition = position;
                m.decalBoxRotation = rotation;
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>画像を動かしている途中なら、箱を掴む前の位置に戻す（Esc。同じ Undo のまとめに入る）</summary>
        private void RestoreImageDragStart()
        {
            if (_imageDrag == null || !ToolSession.TryGetActiveRoot(out var root)) return;
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(_imageDrag.EditId) : null;
            if (edit == null) return;
            var drag = _imageDrag;
            Undo.RecordObject(component, "Tocolo: 画像を動かす");
            EditGroups.ForEachInGroup(component, edit, m =>
            {
                m.decalBoxPosition = drag.StartPosition;
                m.decalBoxRotation = drag.StartRotation;
            });
            EditorUtility.SetDirty(component);
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
            CollectVisibleRenderers(mousePosition, root, _visibleRenderers);
            if (!TryPickVisible(_visibleRenderers, ray, out var hit)) return;
            var color = SampleSwatchColor(hit);
            if (!color.HasValue) return;

            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            if (edit == null) return;
            ToolPanelOverlay.ApplyEyedropperColor(component, edit, color.Value);
            ToolSession.EyedropperActive = false;
        }

        /// <summary>
        /// mousePosition の手前に写っている物（Unity の Scene ピック）が root 配下の、選べる状態（IsPickable・除外リスト外）で
        /// メッシュの Read/Write が無効な Renderer ならそれを返す。そうでなければ null。
        /// Scene ピックが別の物（NDMF プレビューの代わりの物など）を返したときは、レイが境界箱に当たる選べないメッシュのうち
        /// 自前のレイ判定の当たりより手前のものを返す
        /// </summary>
        private Renderer FindClickedUnreadable(GameObject root, GameObject picked, Vector2 mousePosition)
        {
            var component = root.GetComponent<ClickRecolor>();
            if (picked != null && picked.transform.IsChildOf(root.transform))
            {
                var renderer = picked.GetComponent<Renderer>();
                if (renderer == null || !IsPickable(renderer) || (component != null && component.IsExcluded(renderer))) return null;
                // テクスチャの無いマテリアルだけの物（FakeShadow など）は選ぶ対象でないので、Read/Write が無効でも案内しない（奥の前髪を選べるように）
                if (!HasAnyMainTexture(renderer)) return null;
                return MeshReadWriteFixer.IsUnreadable(renderer) ? renderer : null;
            }

            // 予備: 選べないメッシュの境界箱にレイが当たり、自前の判定の当たりより手前なら、それを押したとみなす
            var ray = HandleUtility.GUIPointToWorldRay(mousePosition);
            float hitDistance = float.PositiveInfinity;
            if (_picker.TryPick(ray, _renderers, null, out var hit)) hitDistance = hit.distance;
            Renderer best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!IsPickable(renderer) || (component != null && component.IsExcluded(renderer))) continue;
                if (!MeshReadWriteFixer.IsUnreadable(renderer)) continue;
                if (!renderer.bounds.IntersectRay(ray, out float distance)) continue;
                if (distance >= hitDistance || distance >= bestDistance) continue;
                best = renderer;
                bestDistance = distance;
            }
            return best;
        }

        /// <summary>1 回のクリックで Scene ピックをやり直す回数の上限（重なっている物が多くても重くならないように）</summary>
        private const int MaxVisiblePicks = 8;

        private static readonly List<GameObject> s_pickIgnore = new List<GameObject>();

        /// <summary>
        /// mousePosition の位置に Scene で描かれている、当てられる Renderer（root 配下。root が null ならシーン全体。除外リスト外・IsPickable）を result に集め、
        /// 最初に Scene ピックが返した物（手前に写っている物。無ければ null）を返す。
        /// Unity の Scene ピックは実際に描かれた物で判定し、NDMF プレビューの代わりの物は元の物に読み替えられるので、他ツールのプレビュー
        /// （MA の Object Toggle で隠した物、メッシュの一部を消すツールなど）を反映した「見えている物」になる（ユーザー要望 2026-10-04）。
        /// ピックは 1 つしか返さず、半透明（奥行きを書かない）マテリアルは奥の物に負けて返らないことがあるので（実機 2026-10-04: 半透明の lilToon の
        /// クリックが奥に吸われた）、返った物を除いてピックし直し、その位置に描かれている物をすべて集める。
        /// Hierarchy で非アクティブな物（プレビューでだけ表示されている物）は集めない: 範囲を作る側（CollectPreviewRenderers 等）が有効な Renderer しか見ないため
        /// </summary>
        private static GameObject CollectVisibleRenderers(Vector2 mousePosition, GameObject root, List<Renderer> result)
        {
            result.Clear();
            s_pickIgnore.Clear();
            GameObject first = null;
            var component = root != null ? root.GetComponent<ClickRecolor>() : null;
            for (int i = 0; i < MaxVisiblePicks; i++)
            {
                var picked = HandleUtility.PickGameObject(mousePosition, false, s_pickIgnore.ToArray());
                if (picked == null || s_pickIgnore.Contains(picked)) break;
                s_pickIgnore.Add(picked);
                if (first == null) first = picked;
                if (root != null && !picked.transform.IsChildOf(root.transform)) continue;
                var renderer = picked.GetComponent<Renderer>();
                if (!IsPickable(renderer) || (component != null && component.IsExcluded(renderer))) continue;
                if (!result.Contains(renderer)) result.Add(renderer);
            }
            return first;
        }

        /// <summary>
        /// レイを当てる: Scene に描かれている Renderer（visible。CollectVisibleRenderers）があればそれらだけに当て、最も手前の当たりを採る。
        /// テクスチャ無しの面（ScenePicker が素通りする）がその当たりより明らかに手前にあれば、見えているのはテクスチャ無しの物（単色の服など）なので
        /// 奥の物は選ばずに止める（同じ形を重ねて描いた FakeShadow などはほぼ同じ距離なので通す。レビュー指摘 2026-10-04）。
        /// 描かれている物に当たらなければ（形がずれている等）、または Scene ピックが何も返さなければ（ピック無効など）、従来どおり _renderers 全体に当てる
        /// </summary>
        private bool TryPickVisible(List<Renderer> visible, Ray ray, out PickHit hit)
        {
            _pickBlockedByTextureless = false;
            if (visible != null && visible.Count > 0)
            {
                if (_picker.TryPick(ray, visible, null, out hit))
                {
                    if (_picker.LastSkippedTextureless
                        && _picker.LastSkippedTexturelessDistance < hit.distance - CoincidentTolerance(hit.distance))
                    {
                        hit = default;
                        _pickBlockedByTextureless = true;
                        return false;
                    }
                    return true;
                }
                if (_picker.LastSkippedTextureless)
                {
                    // 描かれている物がテクスチャ無しの面だけ
                    _pickBlockedByTextureless = true;
                    return false;
                }
            }
            return _picker.TryPick(ray, _renderers, null, out hit);
        }

        /// <summary>直前の TryPickVisible が、見えているテクスチャ無しの面で止まったか（案内を出すため）</summary>
        private bool _pickBlockedByTextureless;

        /// <summary>同じ形を重ねて描いた面とみなす距離の差（2mm か、距離の 0.2% の大きいほう）</summary>
        private static float CoincidentTolerance(float distance) => Mathf.Max(0.002f, distance * 0.002f);

        /// <summary>renderer のマテリアルのどれかにメインテクスチャがあるか</summary>
        private static bool HasAnyMainTexture(Renderer renderer)
        {
            foreach (var material in renderer.sharedMaterials)
            {
                if (MaterialTextureResolver.TryGetMainTexture(material, out _)) return true;
            }
            return false;
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
        internal static bool IsPickable(Renderer r)
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
                Debug.LogWarning($"[Tocolo] テクスチャ'{texture.name}'はプロジェクトの資産ではないため色を変えられません（実行時に作られたテクスチャなど）", texture);
            }
            texture = null;
            return false;
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
            var component = hit.renderer != null ? hit.renderer.GetComponentInParent<ClickRecolor>(true) : null;
            // hit.uv は Tiling/Offset 適用後で 0〜1 に畳まれている（MaterialTextureResolver.ToTextureCoord）
            return SampleSwatchColor(hit.hasMainTexture ? hit.mainTexture.texture : null, hit.uv, SwatchWorkingSize(component));
        }

        /// <summary>見本色と一緒に読んでおく作業解像度（プレビュー・範囲づくりと同じ。コンポーネントがまだ無ければ previewResolution の既定）</summary>
        internal static int SwatchWorkingSize(ClickRecolor component) =>
            (int)(component != null ? component.previewResolution : WorkingResolution.R2048);

        /// <summary>
        /// 見本色を texture の uv（Tiling/Offset 適用後、0〜1）から取る。規則は SampleSwatchColor(PickHit) と同じ。
        /// workingSize &gt; 0 なら先にその大きさの版（プレビュー・範囲づくりと共有）を取っておき、見本用の縮小版をそこから作らせる
        /// （元ファイルのデコードがクリックで 1 回、プレビューにあれば 0 回で済む。値は元ファイルから縮めたときと同じ）
        /// </summary>
        internal static Color? SampleSwatchColor(Texture2D texture, Vector2 uv, int workingSize = 0)
        {
            if (texture == null) return null;

            using (workingSize > 0 ? SourceTextureLoader.Acquire(texture, workingSize) : null)
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
        /// <summary>
        /// Inspector のボタンから呼ぶ。対象（ルート）を ToolSession に入れてツールを有効化する
        /// （グローバルツールなので選択は変えない。コンポーネントは付けない）
        /// </summary>
        public static void Activate(GameObject root)
        {
            if (root == null) return;
            // Project の Prefab アセットは Scene でクリックできないので対象にしない
            if (EditorUtility.IsPersistent(root)) return;
            SetTarget(root);
            ToolManager.SetActiveTool<RecolorSceneTool>();
        }

        /// <summary>
        /// 対象（ルート）を root に変える（null で外す）。別の対象に変えるときは、色未設定の仮の編集を捨て（DiscardPendingEdit。色欄は新しい対象の
        /// 編集しか出せず、消す手段が無くなるため）、前の対象でのクリックの印と案内も消す。
        /// 対象を決めただけではコンポーネントを付けない（付けるのは最初の編集を作るとき）
        /// </summary>
        internal static void SetTarget(GameObject root)
        {
            ToolSession.TryGetActiveRoot(out var current);
            if (current != root)
            {
                DiscardPendingEdit();
                ToolSession.LastPick = null;
                ToolSession.LastPickColor = null;
                ToolSession.UnreadableRenderer = null;
            }
            ToolSession.SetActiveRoot(root);
        }

        public static bool IsActive => ToolManager.activeToolType == typeof(RecolorSceneTool);

        public static void Deactivate()
        {
            if (IsActive) ToolManager.RestorePreviousTool();
        }
    }
}
