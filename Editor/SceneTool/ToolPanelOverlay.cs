using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// ツールの操作パネル（色・対象・影響範囲）。操作の案内と終了ボタンは HintOverlay に分けた。
    /// 既定は非表示で、RecolorSceneTool が有効化／終了時に displayed を切り替える
    /// </summary>
    [Overlay(typeof(SceneView), Id, "Tocolo")]
    internal sealed class ToolPanelOverlay : IMGUIOverlay
    {
        public const string Id = "ClickRecolor/ToolPanel";

        /// <summary>
        /// Unity 再起動時やツール有効中に新しく開いた SceneView でも、ツールの状態に合わせる。
        /// 保存レイアウトの表示状態の復元が OnCreated より後に効くため、1 フレーム遅らせて上書きする
        /// </summary>
        public override void OnCreated()
        {
            EditorApplication.delayCall += () => displayed = RecolorSceneTool.IsActive;
            OverlayScaler.ClearUnitySizeOverride(this);
        }

        /// <summary>ドラッグ・数値入力の途中で SceneView ごと閉じられた場合も、Undo のまとめを閉じる</summary>
        public override void OnWillBeDestroyed() { FinishDrag(); }

        /// <summary>直近の Repaint で測った中身の高さ（px）。RecolorSceneTool が下に見切れないよう置き直すのに使う。0 なら未測定</summary>
        internal static float LastContentHeight;
        /// <summary>直近の Repaint での表示幅（倍率・つまみ込み）。除外リストを隣に置くのに使う。0 なら未測定</summary>
        internal static float LastContentWidth;

        /// <summary>パネルの中身の素の高さ（スクロールしないときの高さ）。0 なら未測定</summary>
        private static float s_naturalHeight;
        /// <summary>Scene ビューの高さ（直近の OnGUI で取得）。0 なら不明</summary>
        private static float s_viewHeight;
        private static Vector2 s_scroll;
        private static bool s_lastScroll;
        /// <summary>スクロールにするとき、縦スクロールバーのぶん広げる幅</summary>
        private const float ScrollBarWidth = 16f;
        /// <summary>オーバーレイの枠（見出しと余白）が中身に足す高さの概算（RecolorSceneTool と同じ値）</summary>
        private const float FrameHeight = 26f;
        /// <summary>Scene ビューの下に残す余白</summary>
        private const float ViewMargin = 12f;
        /// <summary>Scene ビューの上に残す余白。上端のツールバー（2D・ライト等のボタン列）にパネルが重ならないようにする（ユーザー要望 2026-09-26）</summary>
        internal const float TopMargin = 40f;

        /// <summary>
        /// 中身が Scene ビューに収まる最大の高さ。収まらないときはこの高さのスクロール領域にする（FHD 等で見切れないため。ユーザー報告 2026-09-26）
        /// </summary>
        private static float MaxContentHeight => s_viewHeight > 0f ? s_viewHeight - TopMargin - ViewMargin - FrameHeight : float.MaxValue;

        /// <summary>倍率（リサイズで拡大しているとき）で割った、基準の大きさでの最大の高さ</summary>
        private static float MaxContentHeightUnscaled(float scale) => MaxContentHeight / Mathf.Max(scale, 0.01f);

        /// <summary>今スクロール領域にしているか（中身の素の高さ×倍率が Scene ビューに収まらない）</summary>
        private static bool UseScroll(float scale) => s_naturalHeight > 0f && s_naturalHeight > MaxContentHeightUnscaled(scale);

        public override void OnGUI()
        {
            if (containerWindow != null) s_viewHeight = containerWindow.position.height;
            float scale = OverlayScaler.CurrentScale(this, PanelWidth + ScrollBarWidth);
            // つまみのドラッグ中はスクロールの有無を切り替えない（切り替えの瞬間にレイアウトが変わって引っかかる）。離した後に反映する
            bool scroll = OverlayScaler.Dragging ? s_lastScroll : UseScroll(scale);
            s_lastScroll = scroll;
            float shownHeight = scroll ? MaxContentHeightUnscaled(scale) : (s_naturalHeight > 0f ? s_naturalHeight : 100f);
            // 中身は基準幅で描き、リサイズの幅に応じて GUI.matrix で拡大する（比率を保つ）
            var scope = OverlayScaler.Begin(this, PanelWidth + ScrollBarWidth, shownHeight);
            if (scroll)
            {
                s_scroll = EditorGUILayout.BeginScrollView(s_scroll, false, true,
                    GUILayout.Width(PanelWidth + ScrollBarWidth), GUILayout.Height(shownHeight));
            }
            var rect = EditorGUILayout.BeginVertical(GUILayout.Width(PanelWidth));
            if (Event.current.type == EventType.Repaint && rect.height > 0f)
            {
                // 中身の高さが変わったら、固定している高さを次の描画で合わせるために描き直す（HintOverlay と同じ）
                if (Mathf.Abs(rect.height - s_naturalHeight) > 0.5f) containerWindow?.Repaint();
                s_naturalHeight = rect.height;
                // 置き直しに使う高さは実際に表示している高さ（倍率込み。スクロール中はスクロール領域の高さ）
                LastContentHeight = shownHeight * scope.scale;
                LastContentWidth = scope.outer.width;
            }
            try
            {
                if (ToolSession.IsInPrefabMode)
                {
                    EditorGUILayout.HelpBox(Locales.Tr("Scene:Panel:PrefabModeNotice"), MessageType.Warning);
                    return;
                }

                DropTargetIfUnusable();
                SyncBoxMembersIfExcludedChanged();
                if (!ToolSession.TryGetActiveRoot(out var root))
                {
                    // 対象未設定: 案内と候補のボタン（ユーザー要望 2026-09-26: 候補は「操作」側からこちらへ戻す）
                    GUILayout.Label(Locales.Tr("Scene:Panel:PickTarget"), EditorStyles.wordWrappedLabel);
                    DrawTargetButtons();
                    QueueAutoSelectIfSingleAvatar();
                    return;
                }

                // 対象の表示と「対象アバターを切り替える」（切り替え先の候補が 2 つ以上あるときだけ）。「範囲」ブロックの上
                if (CountTargetCandidates() > 1)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(Locales.Tr("Scene:Panel:Target", root.name), EditorStyles.wordWrappedLabel);
                        if (GUILayout.Button(Locales.Tr("Scene:Panel:ChangeTarget"), GUILayout.ExpandWidth(false))) ChangeTarget();
                    }
                }
                // 除外リスト（右寄せ。押すと別のオーバーレイを開閉。ユーザー要望 2026-09-27）
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    var component = root.GetComponent<ClickRecolor>();
                    int count = component != null && component.excludedRenderers != null ? component.excludedRenderers.Count : 0;
                    var view = containerWindow as SceneView;
                    bool shown = ExcludeListOverlay.IsShown(view);
                    string label = count > 0 ? Locales.Tr("Scene:Panel:ExcludeListCount", count) : Locales.Tr("Scene:Panel:ExcludeList");
                    if (GUILayout.Toggle(shown, label, EditorStyles.miniButton, GUILayout.ExpandWidth(false)) != shown) ExcludeListOverlay.Toggle(view);
                    HelpMark.Draw("Help:ExcludeList");
                }
                HelpMark.DrawBoxIfOpen("Help:ExcludeList");

                DrawUnreadableNotice(root);

                // 「範囲」ブロック → 「色」ブロック → 削除、の順（Overlay 2 枚だと位置が合わせられないので 1 枚に統合した）。
                // 「対象: ○○／対象を変える」は HintOverlay（右下）に移した（ユーザー要望 2026-09-24）
                DrawPanel(root);
            }
            finally
            {
                EditorGUILayout.EndVertical();
                if (scroll) EditorGUILayout.EndScrollView();
                OverlayScaler.End(this, scope);
            }
        }

        /// <summary>「範囲」ブロックの背景（青系の薄い色）</summary>
        private static readonly Color ScopeBlockColor = new Color(0.30f, 0.45f, 0.70f, 0.18f);

        /// <summary>「色」ブロックの背景（暖色系の薄い色）</summary>
        private static readonly Color ColorBlockColor = new Color(0.75f, 0.50f, 0.30f, 0.18f);

        /// <summary>
        /// 対象設定済みのパネル本体。編集の有無にかかわらず「範囲」「色」の 2 ブロックを出し、
        /// 現在の編集（ToolSession.CurrentEditId）があれば各ブロックに設定を、ブロックの外の最後に削除を出す。
        /// 編集は Undo で List ごと入れ替わるので、描画のたびに id から引き直す（参照を持ち越さない）
        /// </summary>
        private static void DrawPanel(GameObject root)
        {
            // 離したイベントでこの欄が描かれなかった場合も、次の描画で締める
            FinishDragIfReleased();

            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 70f; // パネル幅 PanelWidth に収める
            try
            {
                RecolorEdit edit;
                ClickRecolor component;
                using (new BlockScope(ScopeBlockColor, "Scene:Panel:GroupScope"))
                {
                    // 影響範囲のボタンは連結を解いて現在の編集を入れ替えることがあるので、編集はその後に引く
                    DrawModeSection(root);

                    // ClickRecolor は対象ルートに付く（TargetResolver.GetOrAddComponent）。別アバターの編集はここでは出さない
                    component = root.GetComponent<ClickRecolor>();
                    edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;

                    // 別のマテリアル（テクスチャ）の島も同じ編集に足せるか（島の連結）。既定 OFF
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool cross = EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Panel:CrossTexture"), ToolSession.CrossTextureEnabled);
                        if (cross != ToolSession.CrossTextureEnabled)
                        {
                            ToolSession.CrossTextureEnabled = cross;
                            // 「箱の中」の編集は、ON なら箱に触れる他テクスチャのメンバーを作り、OFF なら外して 1 テクスチャに戻す
                            if (edit != null && edit.mode == SelectionMode.Box)
                            {
                                Undo.RecordObject(component, "Tocolo: マテリアルをまたいで選ぶ");
                                SelectionBox.SyncMembers(component, edit);
                            }
                        }
                        HelpMark.Draw("Help:CrossTexture");
                    }
                    HelpMark.DrawBoxIfOpen("Help:CrossTexture");

                    // 矩形選択で奥に隠れている島も拾うか（UV アイランドモードの矩形選択だけに効く）。既定 OFF（ユーザー要望 2026-09-25）
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool hidden = EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Panel:SelectHidden"), ToolSession.SelectHiddenEnabled);
                        if (hidden != ToolSession.SelectHiddenEnabled) ToolSession.SelectHiddenEnabled = hidden;
                        HelpMark.Draw("Help:SelectHidden");
                    }
                    HelpMark.DrawBoxIfOpen("Help:SelectHidden");

                    // 現在の編集の選択範囲をプレビューに縞で重ねる（ビルドには乗らない）
                    bool showRange = ToolSession.HighlightEnabled;
                    bool newShowRange;
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        newShowRange = EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Panel:ShowRange"), showRange);
                        HelpMark.Draw("Help:ShowRange");
                    }
                    HelpMark.DrawBoxIfOpen("Help:ShowRange");
                    if (newShowRange != showRange) ToolSession.HighlightEnabled = newShowRange;

                    // 編集の有無はこのイベント内で変わらないので、部品の数は Layout と Repaint で揃う
                    if (edit != null)
                    {
                        // 範囲の切り替えで連結を解くと、現在の編集が入れ替わることがある
                        edit = DrawSelectionSettings(component, edit);
                        DrawPadding(component, edit);
                    }
                }

                EditorGUILayout.Space(6);

                using (new BlockScope(ColorBlockColor, "Scene:Panel:GroupColor"))
                {
                    if (edit == null) GUILayout.Label(Locales.Tr("Scene:Panel:NoPick"), EditorStyles.wordWrappedLabel);
                    else DrawColorSection(component, edit);
                }

                // 削除はパネルの最後（ブロックの外）に置く。これより後に部品が無いので、このイベント内で部品の数が変わらない
                // 色未設定の仮の編集は別の場所をクリックすれば捨てられるので、削除は残す編集（色を決めた・一度決めた）にだけ有効にする
                if (edit != null)
                {
                    EditorGUILayout.Space(4);
                    using (new EditorGUI.DisabledScope(!edit.IsKept))
                    {
                        if (GUILayout.Button(Locales.Tr("Scene:Color:DeleteEdit"))) DeleteEdit(component, edit);
                    }
                }
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
                // 離したイベント（MouseUp）はこの欄の部品が hotControl を 0 に戻すので、ここで締める
                FinishDragIfReleased();
            }
        }

        /// <summary>
        /// 背景色つきのブロック（見出し＋左右 4px の余白）。背景は Repaint 時に矩形を先に塗るので内容の背面になる
        /// </summary>
        private static readonly Color GradientBlockColor = new Color(0.7875f, 0.525f, 0.315f, 0.18f); // 色ブロック（0.75,0.50,0.30）と同じ色相で 5% 明るく
        private static readonly Color GradientBlockBorder = new Color(0.8269f, 0.5513f, 0.3308f, 0.50f); // 背景よりさらに 5% 明るく

        /// <summary>見出しの無い入れ子ブロック（背景＋1px の枠）。色ブロックの中でグラデーションの項目をまとめるのに使う</summary>
        private readonly struct SubBlockScope : System.IDisposable
        {
            public SubBlockScope(Color background, Color border)
            {
                var rect = EditorGUILayout.BeginVertical();
                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(rect, background);
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, 1f), border);
                    EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), border);
                    EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), border);
                    EditorGUI.DrawRect(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), border);
                }
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(4);
                EditorGUILayout.BeginVertical();
                GUILayout.Space(3);
            }

            public void Dispose()
            {
                GUILayout.Space(3);
                EditorGUILayout.EndVertical();
                GUILayout.Space(4);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
        }

        private readonly struct BlockScope : System.IDisposable
        {
            public BlockScope(Color background, string headingKey)
            {
                var rect = EditorGUILayout.BeginVertical(GUILayout.Width(PanelWidth));
                if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(rect, background);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(4);
                EditorGUILayout.BeginVertical();
                GUILayout.Label(Locales.Tr(headingKey), EditorStyles.miniBoldLabel);
            }

            public void Dispose()
            {
                EditorGUILayout.EndVertical();
                GUILayout.Space(4);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
            }
        }

        /// <summary>
        /// 「影響範囲」の説明: 全体の説明の後に 3 つのボタン（島・同じ色・似た色）の説明を続けて 1 つの HelpBox に出す
        /// </summary>
        private static void DrawModeHelpIfOpen()
        {
            if (!HelpMark.IsOpen("Help:Mode")) return;
            EditorGUILayout.HelpBox(string.Join("\n",
                Locales.Tr("Help:Mode"),
                Locales.Tr("Help:Mode:Island"),
                Locales.Tr("Help:Mode:Box"),
                Locales.Tr("Help:Mode:SameColor"),
                Locales.Tr("Help:Mode:SimilarColor")), MessageType.Info);
        }

        /// <summary>スライダーなど 1 行の部品の右に「？」を置き、開いていれば直下に説明を出す</summary>
        private static T WithHelp<T>(string helpKey, System.Func<T> drawField)
        {
            T value;
            using (new EditorGUILayout.HorizontalScope())
            {
                value = drawField();
                HelpMark.Draw(helpKey);
            }
            HelpMark.DrawBoxIfOpen(helpKey);
            return value;
        }

        /// <summary>ホイールの一辺（px）</summary>
        private const float WheelSize = 200f;
        /// <summary>Scene ビューの高さがこれ未満ならホイールを SmallWheelSize にする</summary>
        private const float SmallWheelViewHeight = 900f;
        private const float SmallWheelSize = 150f;

        private static readonly int s_wheelHint = "ClickRecolor.ColorWheel".GetHashCode();

        /// <summary>ホイール・スライダーのドラッグ 1 回分を Undo 1 回にまとめる</summary>
        private static readonly UndoDragScope s_drag = new UndoDragScope();

        /// <summary>まだ最近の色に入れていない、決めかけの色（ドラッグ中・スライダーの数値入力中）</summary>
        private static Color? s_pendingRecent;

        /// <summary>ホイールの下のスライダーを HSV と RGB のどちらで出すか（EditorPrefs に保存）</summary>
        private const string SliderModeKey = "ClickRecolor.ColorSliderMode";
        private const int SliderModeHsv = 0;
        private const int SliderModeRgb = 1;
        private static readonly string[] s_sliderModeNames = { "HSV", "RGB" };

        /// <summary>グラデーション ON のとき、ホイール・スライダー・最近の色の書き込み先を「終了色」にするか（SessionState。0 = 新しい色、1 = 終了色）</summary>
        private const string ColorEditTargetKey = "ClickRecolor.ColorEditTarget";

        // 現在の色で変わるスライダーのグラデーション（色が変わったときだけ作り直す）
        private static readonly GradientSlider.Cache s_satGradient = new GradientSlider.Cache(GradientSlider.GradientSize, "ClickRecolor.SatGradient");
        private static readonly GradientSlider.Cache s_valGradient = new GradientSlider.Cache(GradientSlider.GradientSize, "ClickRecolor.ValGradient");
        private static readonly GradientSlider.Cache s_redGradient = new GradientSlider.Cache(GradientSlider.GradientSize, "ClickRecolor.RedGradient");
        private static readonly GradientSlider.Cache s_greenGradient = new GradientSlider.Cache(GradientSlider.GradientSize, "ClickRecolor.GreenGradient");
        private static readonly GradientSlider.Cache s_blueGradient = new GradientSlider.Cache(GradientSlider.GradientSize, "ClickRecolor.BlueGradient");

        private static readonly RangePreset[] ModeButtonOrder = { RangePreset.Island, RangePreset.Box, RangePreset.SameColor, RangePreset.SimilarColor };

        /// <summary>影響範囲（島／箱の中／同じ色／似た色）のプリセット。パネル最上部に置く（ユーザー要望 2026-09-24）</summary>
        private static void DrawModeSection(GameObject root)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(Locales.Tr("Scene:Panel:Mode"));
                GUILayout.FlexibleSpace();
                HelpMark.Draw("Help:Mode");
            }
            DrawModeHelpIfOpen();
            // ボタンの並びは パーツ・箱の中・同じ色・似た色（「箱の中」はパーツの右。ユーザー要望 2026-09-29）。enum の値とは別
            int current = System.Array.IndexOf(ModeButtonOrder, ToolSession.Preset);
            if (current < 0) current = 0;
            int selected = GUILayout.Toolbar(current, new[]
            {
                Locales.Tr("Scene:Panel:Mode:Island"),
                Locales.Tr("Scene:Panel:Mode:Box"),
                Locales.Tr("Scene:Panel:Mode:SameColor"),
                Locales.Tr("Scene:Panel:Mode:SimilarColor"),
            });
            // 次のクリックで作る編集の選択仕様になる。現在の編集があればその編集にも即反映する
            // （設計 §5.3: ボタンはプリセット、調整は現在の編集に効く。色や暗部の明るさなどは保つ）
            if (selected != current)
            {
                var preset = ModeButtonOrder[selected];
                ToolSession.Preset = preset;
                var component = root.GetComponent<ClickRecolor>();
                var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
                if (edit != null)
                {
                    Undo.RecordObject(component, "Tocolo: 影響範囲を変更");
                    // 「アバター全体」の連結は色モード専用なので、プリセットを変えたら連結を解いて 1 件に戻す
                    if (EditGroups.IsWholeAvatarGroup(edit))
                    {
                        edit = EditGroups.Unlink(component, edit);
                        edit.scope = ColorScope.Contiguous;
                        ToolSession.CurrentEditId = edit.id;
                    }
                    // 島の連結（各メンバーが自分の種を持つ）はプリセットを連結の全編集に効かせる
                    EditGroups.ForEachInGroup(component, edit, e => RecolorSceneTool.ApplyPreset(e, preset));
                    // 「箱の中」に切り替えたら、箱をクリックしたパーツを囲む位置に置き直す（Scene に箱が出る）
                    if (preset == RangePreset.Box)
                    {
                        SelectionBox.ResetToDefault(component, edit);
                        SelectionBox.SyncMembers(component, edit);
                    }
                    // 覚えている範囲が「アバター全体」なら、作成時（CreateEditFromHit）と同じく連結し直す
                    // （島の連結なら現在の編集だけ残してから。SetScopeCore と同じ）
                    if (edit.mode == SelectionMode.Color && edit.scope == ColorScope.WholeAvatar)
                    {
                        if (EditGroups.IsGrouped(edit))
                        {
                            edit = EditGroups.Unlink(component, edit);
                            ToolSession.CurrentEditId = edit.id;
                        }
                        EditGroups.Link(component, edit);
                    }
                    EditorUtility.SetDirty(component);
                    // 連結のメンバーが増減したので、ハイライトする編集の集合を取り直す（同じ値なら publish しない）
                    ToolSession.PublishHighlight();
                }
            }
        }

        /// <summary>
        /// 「色」ブロックの中身（現在の編集があるとき）。元の色・新しい色（16 進入力）・ホイール・HSV／RGB・最近の色・
        /// 種ごとの色揃え・陰影の暗さ・強さを出す。現在の編集が連結（「アバター全体」・島の連結）なら、変更は連結の全編集に効かせる（EditGroups.ForEachInGroup）
        /// </summary>
        private static void DrawColorSection(ClickRecolor component, RecolorEdit edit)
        {
            // 「色未設定」と「同じ UV の双子」の案内は 2026-09-24 に撤去（ユーザー判断: 不要）。双子判定自体は残している
            // 髪ツール（キメラヘアマスター）の対象パーツなら、先にそちらで書き出すよう警告（ユーザー要望 2026-09-25）
            if (ToolSession.CurrentEditIsHairToolTarget) EditorGUILayout.HelpBox(Locales.Tr("Scene:Panel:HairToolNotice"), MessageType.Warning);
            // クリックした場所に lilToon の 2nd／3rd が重なっているときの案内（その編集を選んでいる間だけ）
            var layerNotice = ToolSession.LayerNotice;
            if (ToolSession.LayerNoticeEditId == edit.id)
            {
                if (layerNotice.kind != Picking.LilToonLayerOverlap.Kind.None)
                {
                    bool covers = layerNotice.kind == Picking.LilToonLayerOverlap.Kind.Covers;
                    string key = layerNotice.poiyomi
                        ? (covers ? "Scene:Panel:PoiLayerCovers" : "Scene:Panel:PoiLayerMixes")
                        : (covers ? "Scene:Panel:LayerCovers" : "Scene:Panel:LayerMixes");
                    EditorGUILayout.HelpBox(Locales.Tr(key, layerNotice.layers), MessageType.Info);
                }
                if (layerNotice.colorAdjust)
                {
                    EditorGUILayout.HelpBox(
                        Locales.Tr(layerNotice.poiyomi ? "Scene:Panel:PoiColorAdjust" : "Scene:Panel:LilColorAdjust"), MessageType.Info);
                }
                if (layerNotice.mainColorTint) EditorGUILayout.HelpBox(Locales.Tr("Scene:Panel:MainColorTint"), MessageType.Info);
            }
            // Ctrl＋クリックで種を足せなかった理由など（数秒で消える）
            string notice = ToolSession.TransientNotice;
            if (notice != null)
            {
                var args = ToolSession.TransientNoticeArgs;
                EditorGUILayout.HelpBox(args.Length > 0 ? Locales.Tr(notice, args) : Locales.Tr(notice), MessageType.Info);
            }
            // ホイール・スライダー・最近の色・見本行の書き込み先（グラデーション ON で「色 2」を選んでいれば色 2）
            bool end = IsEditingGradientEnd(edit);

            // 元の色（クリック時の種色）と編集中の色（色 1 または色 2）の見本を横並びに。アルファは無視して色だけ見せる
            // 元の色の見本は押すと編集中の色を元に戻す。編集中の色の右に 16 進入力（# なし）
            DrawColorRow(component, edit, end);

            // スポイト中の案内
            if (ToolSession.EyedropperActive) EditorGUILayout.HelpBox(Locales.Tr("Scene:Color:EyedropperHint"), MessageType.Info);

            // Scene ビューが低い（FHD で通常の大きさ等）ときはホイールを小さくして、全体が収まりやすくする
            float wheelSize = s_viewHeight > 0f && s_viewHeight < SmallWheelViewHeight ? SmallWheelSize : WheelSize;
            // カラーホイール（中央寄せ）。左上にスポイトのトグル（ユーザー要望 2026-09-26）
            using (new EditorGUILayout.HorizontalScope())
            {
                // 高さをホイールに固定する（FlexibleSpace が親の高さいっぱいに伸びて行が間延びしないように）
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(EyedropperButtonSize), GUILayout.Height(wheelSize)))
                {
                    var eyedropperContent = EyedropperContent;
                    bool active = GUILayout.Toggle(ToolSession.EyedropperActive, eyedropperContent, EditorStyles.miniButton,
                        GUILayout.Width(EyedropperButtonSize), GUILayout.Height(EyedropperButtonSize));
                    if (active != ToolSession.EyedropperActive)
                    {
                        ToolSession.EyedropperActive = active;
                        SceneView.RepaintAll();
                    }
                    GUILayout.FlexibleSpace();
                }
                GUILayout.FlexibleSpace();
                var wheelRect = GUILayoutUtility.GetRect(wheelSize, wheelSize, GUILayout.ExpandWidth(false));
                EditorGUI.BeginChangeCheck();
                var wheelColor = ColorWheelGUI.Draw(wheelRect, EditingColor(edit, end), s_wheelHint);
                if (EditorGUI.EndChangeCheck()) ChangeColor(component, edit, wheelColor, end);
                GUILayout.FlexibleSpace();
            }

            // HSV／RGB のスライダー（切り替えはホイールの下の右端）
            DrawColorSliders(component, edit, end);

            DrawRecentColors(component, edit, end);

            // 種の数（主種＋Ctrl＋クリックで足した種。共有の種色の編集は追加の種を使わないので 1）
            int seedCount = 1 + (!edit.hasSeedOklab && edit.extraSeeds != null ? edit.extraSeeds.Count : 0);
            // 種が複数か連結のメンバーが複数のときだけ、種（メンバー）ごとに色を揃えるかを選べる（どちらも 1 つなら効かない）
            if (seedCount > 1 || EditGroups.Count(component, edit) > 1)
            {
                bool perSeedStats = WithHelp("Help:PerSeedStats",
                    () => EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Panel:PerSeedStats"), edit.perSeedStats));
                if (perSeedStats != edit.perSeedStats)
                {
                    Undo.RecordObject(component, "Tocolo: 種ごとに色を揃えるかを変更");
                    EditGroups.ForEachInGroup(component, edit, e => e.perSeedStats = perSeedStats);
                    EditorUtility.SetDirty(component);
                }
            }

            // 色を決める前は新しい色が仮の値（元の色）なので、自動調整の基準にしない
            // end（色 2 を編集中）なら自動調整・陰影の暗さ・強さは色 2 側の値（gradientDarkEndRatio / gradientStrength）を読み書きする
            using (new EditorGUI.DisabledScope(!edit.hasTarget))
            {
                if (GUILayout.Button(Locales.Tr("Scene:Color:AutoDarkEnd")))
                {
                    Undo.RecordObject(component, "Tocolo: 陰影の暗さを自動調整");
                    EditGroups.ForEachInGroup(component, edit, e =>
                    {
                        if (end) e.gradientDarkEndRatio = DarkEndAutoAdjust.Compute(e.gradientColor);
                        else e.darkEndRatio = DarkEndAutoAdjust.Compute(e.targetColor);
                    });
                    EditorUtility.SetDirty(component);
                }
            }

            // ドラッグ中・数値欄への入力中の変更は Undo 1 回にまとめる
            EditorGUI.BeginChangeCheck();
            // 色 2 のときはラベルに「（色 2）」を付けて、どちらの値を触っているか分かるようにする
            string shadingSuffix = end ? Locales.Tr("Scene:Color:ShadingFor2") : "";
            float currentDarkEndRatio = end ? edit.gradientDarkEndRatio : edit.darkEndRatio;
            float currentStrength = end ? edit.gradientStrength : edit.strength;
            string darkEndLabel = Locales.Tr("Scene:Color:DarkEnd") + shadingSuffix;
            string stretchLabel = Locales.Tr("Scene:Color:ShadingStretch");
            string strengthLabel = Locales.Tr("Scene:Color:Strength") + shadingSuffix;
            // 「（色 2）」付きの文言がパネル既定のラベル幅（70px）に収まらず見切れるので、3 本のラベル幅は
            // 「（色 2）」付きの文言に合わせる。色 1 の編集中も同じ幅にして、切り替えでスライダーの位置が動かないようにする（ユーザー要望 2026-09-25）
            string suffix2 = Locales.Tr("Scene:Color:ShadingFor2");
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Max(previousLabelWidth,
                SliderLabelWidth(Locales.Tr("Scene:Color:DarkEnd") + suffix2, stretchLabel, Locales.Tr("Scene:Color:Strength") + suffix2) + 4f);
            // 表示は「陰影の暗さ」（右ほど暗い）。保存値 darkEndRatio は「暗部の明るさ」（右ほど明るい）なので 1 − 値で表裏を変換する
            float darkEnd = 1f - WithHelp("Help:DarkEnd",
                () => EditorGUILayout.Slider(darkEndLabel, 1f - currentDarkEndRatio, 0f, 1f));
            // 陰影の強調は色 1・色 2 で共通なので「（色 2）」は付けない
            float shadingStretch = WithHelp("Help:ShadingStretch",
                () => EditorGUILayout.Slider(stretchLabel, edit.shadingStretch, 0f, 1f));
            float strength = WithHelp("Help:Strength",
                () => EditorGUILayout.Slider(strengthLabel, currentStrength, 0f, 1f));
            EditorGUIUtility.labelWidth = previousLabelWidth;
            if (EditorGUI.EndChangeCheck())
            {
                BeginDragIfGrabbing("Tocolo: 色の設定を変更");
                Undo.RecordObject(component, "Tocolo: 色の設定を変更");
                EditGroups.ForEachInGroup(component, edit, e =>
                {
                    e.shadingStretch = shadingStretch;
                    if (end)
                    {
                        e.gradientDarkEndRatio = darkEnd;
                        e.gradientStrength = strength;
                    }
                    else
                    {
                        e.darkEndRatio = darkEnd;
                        e.strength = strength;
                    }
                });
                EditorUtility.SetDirty(component);
            }

            // 元のグラデーションを打ち消す（実験的。ユーザー要望 2026-09-29）。トグルを押したイベントで部品の数が変わらないよう押す前の値で出し分ける
            bool wasFlatten = edit.flattenBase;
            using (new EditorGUI.DisabledScope(!edit.hasTarget))
            {
                bool flatten = WithHelp("Help:FlattenBase",
                    () => EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Color:FlattenBase"), wasFlatten));
                float flattenStrength = edit.flattenStrength;
                if (wasFlatten)
                {
                    float previousWidth = EditorGUIUtility.labelWidth;
                    EditorGUIUtility.labelWidth = 105f;
                    flattenStrength = EditorGUILayout.Slider(Locales.Tr("Scene:Color:FlattenStrength"), edit.flattenStrength, 0f, 1f);
                    EditorGUIUtility.labelWidth = previousWidth;
                }
                if (flatten != wasFlatten || !Mathf.Approximately(flattenStrength, edit.flattenStrength))
                {
                    BeginDragIfGrabbing("Tocolo: 元のグラデーションを打ち消す");
                    Undo.RecordObject(component, "Tocolo: 元のグラデーションを打ち消す");
                    EditGroups.ForEachInGroup(component, edit, e =>
                    {
                        e.flattenBase = flatten;
                        e.flattenStrength = flattenStrength;
                    });
                    EditorUtility.SetDirty(component);
                }
            }

            // グラデーションの項目は暗めの赤の枠でまとめる（ユーザー要望 2026-09-25）。上の不透明度スライダーと詰まらないよう少し空ける
            EditorGUILayout.Space(6);
            using (new SubBlockScope(GradientBlockColor, GradientBlockBorder)) DrawGradient(component, edit);
        }

        /// <summary>
        /// 「グラデーション」トグルと、ON のときの終了色（見本＋カラーコード）・色の書き込み先の切り替え。
        /// ON にすると Scene に箱が出る（RecolorSceneTool）。連結（「アバター全体」・島の連結）なら連結の全編集に効かせる
        /// </summary>
        private static void DrawGradient(ClickRecolor component, RecolorEdit edit)
        {
            // トグルを押したイベントの中で部品の数が変わらないよう、押す前の値で後半を出すか決める
            bool wasEnabled = edit.gradientEnabled;
            bool enabled = WithHelp("Help:Gradient",
                () => EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Color:Gradient"), wasEnabled));
            if (enabled != wasEnabled) GradientBox.SetEnabled(component, edit, enabled);
            if (!wasEnabled) return;

            // トグル以外は字下げして入れ子だと分かるようにする（ユーザー要望 2026-09-25）
            using (new EditorGUILayout.HorizontalScope())
            {
            GUILayout.Space(16);
            using (new EditorGUILayout.VerticalScope())
            {
            // 色 1 → 色 2 の見本
            DrawGradientRow(edit);

            // ホイール・スライダー・最近の色をどちらの色に効かせるか
            int target = SessionState.GetInt(ColorEditTargetKey, 0) == 1 ? 1 : 0;
            int selected = GUILayout.Toolbar(target, new[]
            {
                Locales.Tr("Scene:Color:EditTarget:Start"),
                Locales.Tr("Scene:Color:EditTarget:End"),
            });
            if (selected != target) SessionState.SetInt(ColorEditTargetKey, selected);

            // 箱の中だけグラデーションにするか（外側は「新しい色」のまま）
            bool insideOnly = WithHelp("Help:GradientInsideOnly",
                () => EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Color:GradientInsideOnly"), edit.gradientInsideOnly));
            if (insideOnly != edit.gradientInsideOnly)
            {
                Undo.RecordObject(component, "Tocolo: 箱の中だけを変更");
                EditGroups.ForEachInGroup(component, edit, e => e.gradientInsideOnly = insideOnly);
                EditorUtility.SetDirty(component);
            }

            // 箱を表示（既定 ON。OFF はその編集を選んでいる間だけで、コンポーネントには保存しない。ユーザー要望 2026-10-03）
            bool wasShown = !ToolSession.IsGradientBoxHidden;
            bool shown = WithHelp("Help:ShowGradientBox",
                () => EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Color:ShowGradientBox"), wasShown));
            if (shown != wasShown)
            {
                ToolSession.GradientBoxHiddenEditId = shown ? null : edit.id;
                SceneView.RepaintAll();
            }
            }
            }
        }

        private const float EyedropperButtonSize = 26f;
        private static GUIContent s_eyedropperContent;

        /// <summary>スポイトボタンの中身（Unity 組み込みのスポイトアイコン。無ければ文字）</summary>
        private static GUIContent EyedropperContent
        {
            get
            {
                if (s_eyedropperContent == null)
                {
                    var icon = EditorGUIUtility.IconContent(EditorGUIUtility.isProSkin ? "d_eyeDropper.Large" : "eyeDropper.Large");
                    s_eyedropperContent = icon != null && icon.image != null
                        ? new GUIContent(icon.image, Locales.Tr("Scene:Color:Eyedropper"))
                        : new GUIContent(Locales.Tr("Scene:Color:EyedropperShort"), Locales.Tr("Scene:Color:Eyedropper"));
                }
                return s_eyedropperContent;
            }
        }

        /// <summary>
        /// スポイトで取った色を編集中の色（色 1、グラデーションで色 2 を選んでいれば色 2）に入れる。RecolorSceneTool から呼ぶ。
        /// 連結なら連結の全編集に効く（CommitColor と同じ）
        /// </summary>
        internal static void ApplyEyedropperColor(ClickRecolor component, RecolorEdit edit, Color color)
        {
            if (component == null || edit == null) return;
            CommitColor(component, edit, new Color(color.r, color.g, color.b, 1f), IsEditingGradientEnd(edit));
        }

        /// <summary>ホイール・スライダー・最近の色の書き込み先が「終了色」か（グラデーション ON かつ切り替えで終了色を選んでいる）</summary>
        private static bool IsEditingGradientEnd(RecolorEdit edit) =>
            edit != null && edit.gradientEnabled && SessionState.GetInt(ColorEditTargetKey, 0) == 1;

        /// <summary>書き込み先の色（end なら終了色、そうでなければ新しい色）</summary>
        private static Color EditingColor(RecolorEdit edit, bool end) => end ? edit.gradientColor : edit.targetColor;

        /// <summary>
        /// はみ出し幅（「範囲」ブロックの最後）。ドラッグ中・数値欄への入力中の変更は Undo 1 回にまとめる。
        /// 連結（「アバター全体」・島の連結）なら連結の全編集に効かせる
        /// </summary>
        private static void DrawPadding(ClickRecolor component, RecolorEdit edit)
        {
            EditorGUI.BeginChangeCheck();
            int padding = WithHelp("Help:Padding",
                () => EditorGUILayout.IntSlider(Locales.Tr("Scene:Color:Padding"), edit.padding, -5, 32));
            if (EditorGUI.EndChangeCheck())
            {
                BeginDragIfGrabbing("Tocolo: はみ出し幅を変更");
                Undo.RecordObject(component, "Tocolo: はみ出し幅を変更");
                EditGroups.ForEachInGroup(component, edit, e => e.padding = padding);
                EditorUtility.SetDirty(component);
            }
        }

        /// <summary>
        /// 選択の設定。色モードの編集ではしきい値・ぼかし・範囲・ゴマ塩除去、島モードではゴマ塩除去だけを出す。
        /// 変えるとマスクを作り直す（MaskCache の鍵に入っている）。ドラッグ中・数値入力中の変更は Undo 1 回にまとめる。
        /// 連結（「アバター全体」・島の連結）なら連結の全編集に効かせる。範囲の切り替えで連結を作る／解く（SetScopeCore）。
        /// 操作後の現在の編集を返す（連結を解いたときは残した編集）
        /// </summary>
        private static RecolorEdit DrawSelectionSettings(ClickRecolor component, RecolorEdit edit)
        {
            // モードはこのイベント内で変わらないので、部品の数は Layout と Repaint で揃う
            bool colorMode = edit.mode == SelectionMode.Color;
            bool boxMode = edit.mode == SelectionMode.Box;
            float threshold = edit.threshold;
            float feather = edit.feather;
            ColorScope scope = edit.scope;

            EditorGUI.BeginChangeCheck();
            if (boxMode)
            {
                // 箱の中: 面のぼかしだけ（箱の位置・大きさは Scene のハンドル）
                feather = WithHelp("Help:BoxFeather",
                    () => EditorGUILayout.Slider(Locales.Tr("Scene:Select:BoxFeather"), edit.feather, 0f, 1f));
                bool perPart = WithHelp("Help:BoxPerPartStats",
                    () => EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Select:BoxPerPartStats"), edit.boxPerPartStats));
                if (perPart != edit.boxPerPartStats)
                {
                    Undo.RecordObject(component, "Tocolo: パーツごとに色を揃えるかを変更");
                    EditGroups.ForEachInGroup(component, edit, e => e.boxPerPartStats = perPart);
                    EditorUtility.SetDirty(component);
                }
            }
            if (colorMode)
            {
                threshold = WithHelp("Help:Threshold",
                    () => EditorGUILayout.Slider(Locales.Tr("Scene:Select:Threshold"), edit.threshold, 0f, 1f));
                feather = WithHelp("Help:Feather",
                    () => EditorGUILayout.Slider(Locales.Tr("Scene:Select:Feather"), edit.feather, 0f, 1f));
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(Locales.Tr("Scene:Select:Scope"));
                    GUILayout.FlexibleSpace();
                    HelpMark.Draw("Help:Scope");
                }
                HelpMark.DrawBoxIfOpen("Help:Scope");
                // 並びは ColorScope の値の順（Contiguous = 0, Island = 1, WholeTexture = 2, WholeAvatar = 3）。
                // 4 つを 1 行に並べるとパネル幅に収まらないので 2 列にする
                scope = (ColorScope)GUILayout.SelectionGrid((int)edit.scope, new[]
                {
                    Locales.Tr("Scene:Select:Scope:Contiguous"),
                    Locales.Tr("Scene:Select:Scope:Island"),
                    Locales.Tr("Scene:Select:Scope:Whole"),
                    Locales.Tr("Scene:Select:Scope:WholeAvatar"),
                }, 2);
            }
            // ゴマ塩除去は色モードだけ（UV アイランドモードは島単位なので不要。ユーザー判断 2026-09-24）
            int cleanup = colorMode
                ? WithHelp("Help:Cleanup", () => EditorGUILayout.IntSlider(Locales.Tr("Scene:Select:Cleanup"), edit.cleanupRadius, 0, 2))
                : edit.cleanupRadius;
            if (EditorGUI.EndChangeCheck())
            {
                BeginDragIfGrabbing("Tocolo: 選択の設定を変更");
                Undo.RecordObject(component, "Tocolo: 選択の設定を変更");
                if (scope != edit.scope)
                {
                    edit = SetScopeCore(component, edit, scope);
                    // 次に作る色モードの編集（ApplyPreset）もこの範囲にする
                    ToolSession.ColorScopePreference = scope;
                }
                EditGroups.ForEachInGroup(component, edit, e =>
                {
                    e.threshold = threshold;
                    e.feather = feather;
                    e.cleanupRadius = cleanup;
                });
                EditorUtility.SetDirty(component);
            }
            return edit;
        }

        /// <summary>
        /// 範囲を変える（Undo 記録つき）。単独の編集を「アバター全体」にすると他のテクスチャ分を作って連結し、
        /// 連結を他の範囲に戻すと他のテクスチャ分を削除して 1 件に戻す（確認なし・Undo 可）。
        /// 操作後の現在の編集（連結を解いたときは残した編集）を返し、CurrentEditId もそれにする
        /// </summary>
        internal static RecolorEdit ChangeScope(ClickRecolor component, RecolorEdit edit, ColorScope scope)
        {
            if (component == null || edit == null || edit.scope == scope) return edit;
            Undo.RecordObject(component, "Tocolo: 範囲を変更");
            var current = SetScopeCore(component, edit, scope);
            EditorUtility.SetDirty(component);
            return current;
        }

        /// <summary>ChangeScope の中身（Undo.RecordObject・SetDirty は呼び出し側）</summary>
        private static RecolorEdit SetScopeCore(ClickRecolor component, RecolorEdit edit, ColorScope scope)
        {
            var current = edit;
            if (scope == ColorScope.WholeAvatar)
            {
                if (EditGroups.IsWholeAvatarGroup(edit)) EditGroups.ForEachInGroup(component, edit, e => e.scope = scope);
                else
                {
                    // 島の連結（各メンバーが自分の種を持つ）は、現在の編集だけ残してから「アバター全体」にする
                    if (EditGroups.IsGrouped(edit)) current = EditGroups.Unlink(component, edit);
                    // Link が範囲を WholeAvatar にし、他のテクスチャ分を同じ設定で足す
                    EditGroups.Link(component, current);
                }
            }
            else
            {
                if (EditGroups.IsWholeAvatarGroup(edit))
                {
                    current = EditGroups.Unlink(component, edit);
                    current.scope = scope;
                }
                else
                {
                    // 単独の編集・島の連結は範囲だけ変える（島の連結なら全メンバー）
                    EditGroups.ForEachInGroup(component, edit, e => e.scope = scope);
                }
            }
            if (current.id != ToolSession.CurrentEditId) ToolSession.CurrentEditId = current.id;
            // 連結のメンバーが増減したので、ハイライトする編集の集合を取り直す（同じ値なら publish しない）
            else ToolSession.PublishHighlight();
            return current;
        }

        /// <summary>編集を削除する（連結（「アバター全体」・島の連結）なら連結ごと。Undo 記録つき）。パネルの選択も外す</summary>
        internal static void DeleteEdit(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null) return;
            Undo.RecordObject(component, "Tocolo: 編集を削除");
            EditGroups.RemoveGroup(component, edit);
            EditorUtility.SetDirty(component);
            ToolSession.CurrentEditId = null;
        }

        /// <summary>
        /// パネルの幅。陰影の 3 本のスライダーは「陰影の暗さ（色 2）」の文言幅（約 105px）をラベルに取るが、
        /// Unity の Slider はラベルの残りが約 115px（トラック 60 ＋ 数値欄 50 ＋ 間隔）を切るとトラックを描かない。
        /// ？ボタン（約 20px）込みで収まるよう 240 → 270 に広げた（ユーザー報告 2026-09-25: スライダーが消えた）
        /// </summary>
        internal const float PanelWidth = 270f;
        private const float HexFieldWidth = 70f;
        private const float ResetButtonWidth = 44f;
        private const float RowGap = 4f;

        /// <summary>
        /// 「元の色｜色 1（色 2 の編集中は色 2）｜カラーコード｜リセット」の行。見本・入力欄・ボタンを 1 つのコントロール矩形を分割して描くので、
        /// 高さと中心が必ず揃う（縦並びのラベル＋部品を横に並べると数 px ずれる）。ラベルは上の行に別で出す。
        /// end（色 2 の編集中）は見本・入力・リセットの全部が色 2 に効く（ユーザー要望 2026-09-25: グラデーション側のカラーコードを無くすため）
        /// </summary>
        private static void DrawColorRow(ClickRecolor component, RecolorEdit edit, bool end)
        {
            // ラベル行 → 部品の行、の順に矩形を取る（ラベルは見本の列幅に合わせて置く）
            var labelRow = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight * 0.8f);
            var full = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            float swatchWidth = (full.width - HexFieldWidth - ResetButtonWidth - RowGap * 3f) / 2f;
            EditorGUI.LabelField(new Rect(full.x, labelRow.y, swatchWidth, labelRow.height), Locales.Tr("Scene:Color:Original"), EditorStyles.miniLabel);
            EditorGUI.LabelField(new Rect(full.x + swatchWidth + RowGap, labelRow.y, swatchWidth, labelRow.height),
                Locales.Tr(end ? "Scene:Color:EditTarget:End" : "Scene:Color:Target"), EditorStyles.miniLabel);

            var originalRect = new Rect(full.x, full.y, swatchWidth, full.height);
            var targetRect = new Rect(originalRect.xMax + RowGap, full.y, swatchWidth, full.height);
            var hexRect = new Rect(targetRect.xMax + RowGap, full.y, HexFieldWidth, full.height);
            var resetRect = new Rect(hexRect.xMax + RowGap, full.y, ResetButtonWidth, full.height);

            // 元の色（押すと編集中の色を元に戻す。色 1 なら色の指定の取り消し、色 2 なら色 2 = 元の色）
            var seed = edit.seedColor;
            EditorGUI.DrawRect(originalRect, new Color(seed.r, seed.g, seed.b, 1f));
            GUI.Label(originalRect, new GUIContent(string.Empty, Locales.Tr("Scene:Color:ResetToOriginal")), GUIStyle.none);
            EditorGUIUtility.AddCursorRect(originalRect, MouseCursor.Link);
            if (GUI.Button(originalRect, GUIContent.none, GUIStyle.none)) ResetEditingColor(component, edit, end);

            // 編集中の色（色 1 または色 2）
            var editing = EditingColor(edit, end);
            EditorGUI.DrawRect(targetRect, new Color(editing.r, editing.g, editing.b, 1f));

            // カラーコード（# なし）
            EditorGUI.BeginChangeCheck();
            string typed = EditorGUI.DelayedTextField(hexRect, ColorUtility.ToHtmlStringRGB(editing));
            if (EditorGUI.EndChangeCheck() && TryParseHex(typed, out var typedColor)) CommitColor(component, edit, typedColor, gradientEnd: end);

            // リセット（色 1: 色未設定のあいだは無効。色 2: 元の色と同じなら無効）
            bool canReset = end
                ? edit.gradientColor != edit.seedColor
                : edit.gradientEnabled ? edit.targetColor != edit.seedColor : edit.hasTarget;
            using (new EditorGUI.DisabledScope(!canReset))
            {
                var content = new GUIContent(Locales.Tr("Scene:Color:ResetButton"), Locales.Tr("Scene:Color:ResetToOriginal"));
                if (GUI.Button(resetRect, content, EditorStyles.miniButton)) ResetEditingColor(component, edit, end);
            }
        }

        /// <summary>
        /// 見本行のリセット（「元の色」の見本・［リセット］）。色 2 の編集中は色 2 を元の色に、グラデーション中の色 1 は色 1 を元の色にする
        /// （どちらもグラデーションと選択範囲は残す）。グラデーション OFF の色 1 は ResetToOriginal（色の指定の取り消し）
        /// </summary>
        internal static void ResetEditingColor(ClickRecolor component, RecolorEdit edit, bool end)
        {
            if (component == null || edit == null) return;
            if (end)
            {
                Undo.RecordObject(component, "Tocolo: 色2を元の色に戻す");
                EditGroups.ForEachInGroup(component, edit, e => e.gradientColor = e.seedColor);
                EditorUtility.SetDirty(component);
                return;
            }
            // グラデーション中の色 1 は、色だけを元の色にする（色の指定・グラデーション・選択範囲は残す）。
            // 取り消すと色未設定の編集になり、別の場所を触ったときに選択範囲ごと捨てられていた（ユーザー報告 2026-10-03）
            if (edit.gradientEnabled)
            {
                Undo.RecordObject(component, "Tocolo: 色1を元の色に戻す");
                EditGroups.ForEachInGroup(component, edit, e => e.targetColor = e.seedColor);
                EditorUtility.SetDirty(component);
                return;
            }
            ResetToOriginal(component, edit);
        }

        /// <summary>「色 1 → 色 2」のグラデーション見本の行。色 2 のカラーコードは見本行（DrawColorRow の色 2 編集中）に出すのでここには無い</summary>
        private static void DrawGradientRow(RecolorEdit edit)
        {
            GUILayout.Label(Locales.Tr("Scene:Color:GradientPreview"), EditorStyles.miniLabel);
            var full = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            DrawGradientRect(full, edit.targetColor, edit.gradientColor);
        }

        /// <summary>矩形に左→右で start→end のグラデーションを描く（Oklab で補間、32 分割）。Repaint 以外では何もしない</summary>
        private static void DrawGradientRect(Rect rect, Color start, Color end)
        {
            if (Event.current.type != EventType.Repaint) return;
            const int steps = 32;
            var a = OklabConverter.LinearRGBToOklab(OklabConverter.SRGBToLinear(start));
            var b = OklabConverter.LinearRGBToOklab(OklabConverter.SRGBToLinear(end));
            float w = rect.width / steps;
            for (int i = 0; i < steps; i++)
            {
                float t = (i + 0.5f) / steps;
                var c = OklabConverter.LinearToSRGB(OklabConverter.OklabToLinearRGB(Vector3.Lerp(a, b, t)));
                EditorGUI.DrawRect(new Rect(rect.x + i * w, rect.y, w + 0.5f, rect.height), new Color(c.r, c.g, c.b, 1f));
            }
        }


        /// <summary>
        /// ホイールの下の HSV／RGB 切り替えと、グラデーション付きのスライダー 3 本。
        /// HSV: 色相（0〜360）・彩度・明るさ（0〜100）、RGB: R・G・B（0〜255）。どちらも 3 行なので、切り替えても部品の数は変わらない。
        /// end なら終了色を操作する
        /// </summary>
        private static void DrawColorSliders(ClickRecolor component, RecolorEdit edit, bool end)
        {
            int mode = EditorPrefs.GetInt(SliderModeKey, SliderModeHsv) == SliderModeRgb ? SliderModeRgb : SliderModeHsv;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                int selected = EditorGUILayout.Popup(mode, s_sliderModeNames, GUILayout.Width(80f));
                if (selected != mode) EditorPrefs.SetInt(SliderModeKey, selected);
            }

            if (mode == SliderModeHsv)
            {
                string hLabel = Locales.Tr("Scene:Color:Hue");
                string sLabel = Locales.Tr("Scene:Color:Sat");
                string vLabel = Locales.Tr("Scene:Color:Val");
                float labelWidth = SliderLabelWidth(hLabel, sLabel, vLabel);

                Vector3 hsv = ColorWheelGUI.GetHsv(EditingColor(edit, end));
                s_satGradient.Update(hsv, t => Color.HSVToRGB(hsv.x, t, hsv.z));
                s_valGradient.Update(hsv, t => Color.HSVToRGB(hsv.x, hsv.y, t));

                EditorGUI.BeginChangeCheck();
                float hue = GradientSlider.Draw(hLabel, hsv.x * 360f, 0f, 360f, GradientSlider.HueTexture, "0", labelWidth);
                float sat = GradientSlider.Draw(sLabel, hsv.y * 100f, 0f, 100f, s_satGradient.Texture, "0", labelWidth);
                float val = GradientSlider.Draw(vLabel, hsv.z * 100f, 0f, 100f, s_valGradient.Texture, "0", labelWidth);
                if (EditorGUI.EndChangeCheck())
                {
                    ChangeColor(component, edit, ColorWheelGUI.FromHsv(new Vector3(hue / 360f, sat / 100f, val / 100f)), end);
                }
            }
            else
            {
                string rLabel = Locales.Tr("Scene:Color:Red");
                string gLabel = Locales.Tr("Scene:Color:Green");
                string bLabel = Locales.Tr("Scene:Color:Blue");
                float labelWidth = SliderLabelWidth(rLabel, gLabel, bLabel);

                Color c = EditingColor(edit, end);
                var rgb = new Vector3(c.r, c.g, c.b);
                s_redGradient.Update(rgb, t => new Color(t, c.g, c.b));
                s_greenGradient.Update(rgb, t => new Color(c.r, t, c.b));
                s_blueGradient.Update(rgb, t => new Color(c.r, c.g, t));

                EditorGUI.BeginChangeCheck();
                float r = GradientSlider.Draw(rLabel, c.r * 255f, 0f, 255f, s_redGradient.Texture, "0", labelWidth);
                float g = GradientSlider.Draw(gLabel, c.g * 255f, 0f, 255f, s_greenGradient.Texture, "0", labelWidth);
                float b = GradientSlider.Draw(bLabel, c.b * 255f, 0f, 255f, s_blueGradient.Texture, "0", labelWidth);
                if (EditorGUI.EndChangeCheck())
                {
                    ChangeColor(component, edit, new Color(r / 255f, g / 255f, b / 255f, 1f), end);
                }
            }
        }

        /// <summary>
        /// 並べるスライダーのラベル幅（最低 GradientSlider.LabelWidth）。
        /// 「明るさ」など 20px に収まらない文言を切らさず、3 行のトラックの位置を揃える
        /// </summary>
        private static float SliderLabelWidth(params string[] labels)
        {
            float width = GradientSlider.LabelWidth;
            foreach (var label in labels)
            {
                width = Mathf.Max(width, EditorStyles.label.CalcSize(new GUIContent(label)).x);
            }
            return Mathf.Ceil(width);
        }



        /// <summary>
        /// 最近使った色 5 枠。押すとその色を新しい色（end なら終了色）にする。
        /// 枠の数は常に 5（まだ無い枠は押せない空欄）。このイベント内で色が増えても部品の数が変わらないようにするため
        /// </summary>
        private static void DrawRecentColors(ClickRecolor component, RecolorEdit edit, bool end)
        {
            GUILayout.Label(Locales.Tr("Scene:Color:Recent"));
            var recent = RecentColors.Get();
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int i = 0; i < RecentColors.Capacity; i++)
                {
                    bool has = i < recent.Count;
                    Color color = has ? recent[i] : default;
                    bool clicked;
                    using (new EditorGUI.DisabledScope(!has))
                    {
                        clicked = GUILayout.Button(GUIContent.none, GUILayout.Width(32f), GUILayout.Height(18f));
                    }
                    if (has && Event.current.type == EventType.Repaint)
                    {
                        var r = GUILayoutUtility.GetLastRect();
                        EditorGUI.DrawRect(new Rect(r.x + 3f, r.y + 3f, r.width - 6f, r.height - 6f), color);
                    }
                    if (clicked && has) CommitColor(component, edit, color, end);
                }
            }
        }

        /// <summary>"RRGGBB"（先頭の # はあってもなくてもよい）を色にする。不透明にする</summary>
        private static bool TryParseHex(string text, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!ColorUtility.TryParseHtmlString("#" + text.Trim().TrimStart('#'), out color)) return false;
            color.a = 1f;
            return true;
        }

        /// <summary>
        /// ホイール・HSV／RGB のスライダーで色を変える。ドラッグ中なら Undo をまとめ始める。
        /// 最近の色に入れるのは、離したとき／数値入力を終えたとき（FinishDragIfReleased）。gradientEnd なら終了色を変える
        /// </summary>
        private static void ChangeColor(ClickRecolor component, RecolorEdit edit, Color color, bool gradientEnd)
        {
            BeginDragIfGrabbing("Tocolo: 色を変更");
            ApplyTargetColor(component, edit, color, gradientEnd);
            s_pendingRecent = color;
        }

        /// <summary>16 進入力・最近の色で色を決める。その場で最近の色に入れる。gradientEnd なら終了色を変える</summary>
        private static void CommitColor(ClickRecolor component, RecolorEdit edit, Color color, bool gradientEnd)
        {
            ApplyTargetColor(component, edit, color, gradientEnd);
            s_pendingRecent = null;
            RecentColors.Push(color);
        }

        /// <summary>
        /// 部品がマウスを掴んでいる（hotControl が 0 でない）間、またはスライダーの数値欄に入力中の最初の変更で、
        /// Undo のまとめを始める（数値欄への入力が 1 文字ごとに Undo にならないようにする）
        /// </summary>
        private static void BeginDragIfGrabbing(string undoName)
        {
            if (IsGrabbing() && !s_drag.IsActive) s_drag.Begin(undoName);
        }

        /// <summary>
        /// マウスを離した（hotControl が 0 に戻った）／数値欄の入力を終えたら、Undo のまとめを終え、決めかけの色を最近の色に入れる
        /// </summary>
        private static void FinishDragIfReleased()
        {
            if (IsGrabbing()) return;
            FinishDrag();
        }

        /// <summary>部品がマウスを掴んでいるか、テキスト欄（スライダーの数値欄など）に入力中か</summary>
        private static bool IsGrabbing() => GUIUtility.hotControl != 0 || EditorGUIUtility.editingTextField;

        /// <summary>
        /// Undo のまとめを終え、決めかけの色を最近の色に入れる。
        /// ドラッグ・入力の途中でツール終了・Play 遷移・Overlay 破棄が起きた場合も、
        /// まとめを開いたまま残して後続の操作まで 1 つの Undo に畳まないよう、ここを呼んで締める
        /// </summary>
        internal static void FinishDrag()
        {
            s_drag.End();
            if (s_pendingRecent.HasValue)
            {
                RecentColors.Push(s_pendingRecent.Value);
                s_pendingRecent = null;
            }
        }

        /// <summary>
        /// 新しい色を決める。最初の 1 回だけ hasTarget を立てて暗部の明るさを自動値にする
        /// （以後は手動値を保ち、再計算は「暗部の明るさを自動調整」ボタンで行う）。連結（「アバター全体」・島の連結）なら連結の全編集に効かせる。
        /// gradientEnd なら終了色（gradientColor）だけを変える（hasTarget と暗部の明るさの自動調整は「新しい色」のときだけ）
        /// </summary>
        internal static void ApplyTargetColor(ClickRecolor component, RecolorEdit edit, Color newColor, bool gradientEnd = false)
        {
            if (component == null || edit == null) return;
            Undo.RecordObject(component, "Tocolo: 色を変更");
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                if (gradientEnd)
                {
                    e.gradientColor = newColor;
                    // 色 1 を決める前に色 2 を決めた場合も編集を確定する（仮のままだとパイプラインが素通しして
                    // グラデーションが掛からない）。色 1 は元の色のままなので、暗部の明るさはその色から自動調整
                    if (!e.hasTarget)
                    {
                        e.hasTarget = true;
                        e.darkEndRatio = DarkEndAutoAdjust.Compute(e.targetColor);
                        e.gradientDarkEndRatio = DarkEndAutoAdjust.Compute(newColor);
                    }
                    e.confirmed = true;
                    return;
                }
                e.targetColor = newColor;
                if (!e.hasTarget)
                {
                    e.hasTarget = true;
                    e.darkEndRatio = DarkEndAutoAdjust.Compute(newColor);
                }
                e.confirmed = true;
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>
        /// 色の指定を取り消して元の色に戻す（新しい色 = 元の色、hasTarget = false、グラデーション OFF）。プレビューは元の見た目に戻り、
        /// 一度色を決めた編集（confirmed）なので選択範囲は残る（自動で捨てない・再クリックで選び直せる）。
        /// 現在の編集（ハイライト）はそのまま残る。暗部の明るさ等の手動値は変えない
        /// （次に色を選んだときは ApplyTargetColor が初回と同じく暗部の明るさを自動調整する）。連結（「アバター全体」・島の連結）なら連結の全編集に効かせる
        /// </summary>
        internal static void ResetToOriginal(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null) return;
            Undo.RecordObject(component, "Tocolo: 元の色に戻す");
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.targetColor = e.seedColor;
                e.hasTarget = false;
                // グラデーションも切る（元の色に戻したのに終了色が残って見えるのを防ぐ。箱と終了色の値は残す）
                e.gradientEnabled = false;
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>「対象を変える」。対象を外して選び直す（自動選択は走らないので、アバター 1 体でも小物を選べる）。HintOverlay から呼ぶ</summary>
        internal static void ChangeTarget()
        {
            s_autoSelectQueued = true; // 選び直し中はアバター 1 体でも自動選択しない
            // 色を決めないまま対象を変えた編集は残さない（色欄は新しい対象の編集しか出せず、消す手段が無くなるため）
            RecolorSceneTool.DiscardPendingEdit();
            ToolSession.SetActiveRoot(null);
            ToolSession.LastPick = null;
            ToolSession.LastPickColor = null;
            ToolSession.UnreadableRenderer = null;
            SceneView.RepaintAll();
        }

        /// <summary>
        /// 最後のクリックが Read/Write 無効のメッシュに当たったときの案内と［Read/Write を有効にする］（ユーザー判断 2026-09-29: 案 B）。
        /// 書き換えられない場所のメッシュならボタンは出さず、元の FBX で有効にするよう案内する
        /// </summary>
        private static void DrawUnreadableNotice(GameObject root)
        {
            var renderer = ToolSession.UnreadableRenderer;
            if (renderer == null || !renderer.transform.IsChildOf(root.transform)) return;
            var mesh = RendererMeshAccess.GetSharedMesh(renderer);
            // 直った（他の方法で有効にされた）なら消す。部品の数を変えないよう描画はこのイベントでは続ける
            bool fixable = mesh != null && Picking.MeshReadWriteFixer.GetFixablePath(mesh) != null;
            EditorGUILayout.HelpBox(
                Locales.Tr(fixable ? "Scene:Panel:Unreadable" : "Scene:Panel:UnreadableLocked", renderer.name),
                MessageType.Warning);
            if (!fixable) return;
            if (GUILayout.Button(Locales.Tr("Scene:Panel:UnreadableFix")))
            {
                // ダイアログと再インポートは OnGUI の外で行う
                EditorApplication.delayCall += () =>
                {
                    if (Picking.MeshReadWriteFixer.Fix(new[] { RendererMeshAccess.GetSharedMesh(renderer) }))
                    {
                        ToolSession.UnreadableRenderer = null;
                        SceneView.RepaintAll();
                    }
                };
                GUIUtility.ExitGUI();
            }
        }

        private static bool s_dropQueued;
        private static int s_lastExcludedHash;
        private static bool s_excludedSyncQueued;

        /// <summary>
        /// 除外リスト（オーバーレイ・Inspector どちらで変えても）が変わったら、「箱の中」の現在の編集の連結メンバーを次の更新で揃え直す
        /// （除外した Renderer のテクスチャのメンバーが外れる／戻すと戻る。マスク自体は鍵に除外が入っているので自動で作り直る。ユーザー要望 2026-09-29）
        /// </summary>
        private static void SyncBoxMembersIfExcludedChanged()
        {
            if (!ToolSession.TryGetActiveRoot(out var root)) return;
            var component = root.GetComponent<ClickRecolor>();
            if (component == null) return;
            int hash = 17;
            if (component.excludedRenderers != null)
            {
                unchecked
                {
                    foreach (var renderer in component.excludedRenderers) hash = hash * 31 + (renderer != null ? renderer.GetInstanceID() : 0);
                    hash = hash * 31 + component.excludedRenderers.Count;
                }
            }
            if (hash == s_lastExcludedHash) return;
            s_lastExcludedHash = hash;
            if (s_excludedSyncQueued) return;
            var edit = component.FindEdit(ToolSession.CurrentEditId);
            if (edit == null || edit.mode != SelectionMode.Box) return;
            s_excludedSyncQueued = true;
            // OnGUI の途中でメンバー数（部品の数）を変えないよう次の更新で
            EditorApplication.delayCall += () =>
            {
                s_excludedSyncQueued = false;
                if (!RecolorSceneTool.IsActive || !ToolSession.TryGetActiveRoot(out var currentRoot)) return;
                var currentComponent = currentRoot.GetComponent<ClickRecolor>();
                var current = currentComponent != null ? currentComponent.FindEdit(ToolSession.CurrentEditId) : null;
                if (current == null || current.mode != SelectionMode.Box) return;
                Undo.RecordObject(currentComponent, "Tocolo: 除外リストの変更を反映");
                SelectionBox.SyncMembers(currentComponent, current);
                SceneView.RepaintAll();
            };
        }

        /// <summary>
        /// 対象が無効化・非表示（目のマーク）になっていたら次の更新で外す。外れた後は対象未設定と同じ流れになり、
        /// 候補が 1 体なら自動で選び、2 体以上なら候補のボタンを出す（ユーザー要望 2026-09-29: A を非表示にして開いたら B を選んでほしい）。
        /// OnGUI の途中で外すと Layout と Repaint で部品の数が変わるので delayCall にする
        /// </summary>
        private static void DropTargetIfUnusable()
        {
            if (s_dropQueued || !ToolSession.TryGetActiveRoot(out var root) || TargetResolver.IsUsableTarget(root)) return;
            s_dropQueued = true;
            EditorApplication.delayCall += () =>
            {
                s_dropQueued = false;
                if (!RecolorSceneTool.IsActive || !ToolSession.TryGetActiveRoot(out var current) || TargetResolver.IsUsableTarget(current)) return;
                RecolorSceneTool.DiscardPendingEdit();
                ToolSession.SetActiveRoot(null);
                ToolSession.LastPick = null;
                ToolSession.LastPickColor = null;
                ToolSession.UnreadableRenderer = null;
                ResetAutoSelect(); // 外した後の自動選択を許す
                SceneView.RepaintAll();
            };
        }

        /// <summary>自動選択を積んだか。ツールの有効化ごとに 1 回だけ走らせる（RecolorSceneTool.OnActivated がリセット）</summary>
        private static bool s_autoSelectQueued;

        internal static void ResetAutoSelect() { s_autoSelectQueued = false; }

        /// <summary>対象の候補: シーンのアバターと、アバター一覧に無い選択中オブジェクトのルート（小物単体用）</summary>
        private static List<GameObject> CollectTargetCandidates(out GameObject selectionRoot)
        {
            var avatars = TargetResolver.FindSceneAvatarRoots();
            selectionRoot = null;
            var selected = Selection.activeGameObject;
            if (selected != null && !EditorUtility.IsPersistent(selected))
            {
                var root = TargetResolver.ResolveRoot(selected);
                if (!avatars.Contains(root)) selectionRoot = root;
            }
            return avatars;
        }

        /// <summary>対象になりうるものの数（シーンのアバター＋アバターでない選択中のルート）。HintOverlay が切り替えボタンを出すかの判定に使う</summary>
        internal static int CountTargetCandidates()
        {
            var avatars = CollectTargetCandidates(out var selectionRoot);
            return avatars.Count + (selectionRoot != null ? 1 : 0);
        }

        /// <summary>
        /// アバターが 1 体だけで選択中の候補も無いときは、次の更新で自動で対象にする（ツールの有効化ごとに 1 回）。
        /// 早期 return はしない（Layout でフラグが立ち、同じ描画パスの Repaint で部品の数が変わると IMGUI のレイアウト例外になる）
        /// </summary>
        private static void QueueAutoSelectIfSingleAvatar()
        {
            var avatars = CollectTargetCandidates(out var selectionRoot);
            if (avatars.Count == 1 && selectionRoot == null && !s_autoSelectQueued) QueueAutoSelect(avatars[0]);
        }

        /// <summary>
        /// 対象未設定時の候補ボタン（「操作」オーバーレイ＝HintOverlay に出す）。シーンのアバターと選択中のオブジェクトのルートを並べる
        /// </summary>
        internal static void DrawTargetButtons()
        {
            var avatars = CollectTargetCandidates(out var selectionRoot);
            foreach (var avatar in avatars)
            {
                if (GUILayout.Button(avatar.name)) SelectTarget(avatar);
            }
            if (selectionRoot != null && GUILayout.Button(Locales.Tr("Scene:Panel:UseSelection", selectionRoot.name)))
            {
                SelectTarget(selectionRoot);
            }
        }

        /// <summary>対象（ルート）を覚えるだけ。コンポーネントは付けない（付けるのは最初の編集を作るとき）</summary>
        private static void SelectTarget(GameObject root)
        {
            ToolSession.SetActiveRoot(root);
        }

        /// <summary>OnGUI の途中（Layout と Repaint の間）でパネルの中身が切り替わらないよう、次のエディタ更新で 1 回だけ対象にする</summary>
        private static void QueueAutoSelect(GameObject avatar)
        {
            if (s_autoSelectQueued) return;
            s_autoSelectQueued = true;
            EditorApplication.delayCall += () =>
            {
                // 積んでから実行までの間に、アバターが消えた・ツールが終了した・対象が決まった場合は何もしない
                if (avatar == null || !RecolorSceneTool.IsActive || ToolSession.TryGetActiveRoot(out _)) return;
                SelectTarget(avatar);
                // パネルの表示を対象ありに切り替える
                SceneView.RepaintAll();
            };
        }
    }
}
