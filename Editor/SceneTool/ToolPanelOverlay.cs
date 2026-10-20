using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Decal;
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
    internal sealed partial class ToolPanelOverlay : IMGUIOverlay
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

        /// <summary>
        /// 直近の Repaint での、グラデーション・画像のブロックの上端のパネル（floatingPosition）からの高さ（倍率・スクロール込み、見えている範囲に収める）。
        /// 小窓「箱を表示」を横に並べるのに使う。負なら未測定（ブロックを描いていない）
        /// </summary>
        internal static float BoxBlocksOffsetY = -1f;
        /// <summary>今回の Repaint で測ったブロックの上端（スクロールの中身の座標）。負なら今回は描いていない</summary>
        private static float s_boxBlocksContentY = -1f;

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
            if (Event.current.type == EventType.Repaint) s_boxBlocksContentY = -1f;
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
                // 編集一覧と除外リスト（右寄せ。押すと別のオーバーレイを開閉する。同じ場所に出すので、片方を開くともう片方は閉じる）
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    var component = root.GetComponent<ClickRecolor>();
                    var view = containerWindow as SceneView;
                    int edits = EditListOverlay.CountDecided(component);
                    bool listShown = EditListOverlay.IsShown(view);
                    string listLabel = edits > 0 ? Locales.Tr("Scene:Panel:EditListCount", edits) : Locales.Tr("Scene:Panel:EditList");
                    if (GUILayout.Toggle(listShown, listLabel, EditorStyles.miniButton, GUILayout.ExpandWidth(false)) != listShown) EditListOverlay.Toggle(view);
                    int count = component != null && component.excludedRenderers != null ? component.excludedRenderers.Count : 0;
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
                if (Event.current.type == EventType.Repaint)
                {
                    // スクロールで見えない位置なら、見えている範囲の端に寄せる
                    BoxBlocksOffsetY = s_boxBlocksContentY < 0f ? -1f
                        : FrameHeight + scope.outer.y + Mathf.Clamp(s_boxBlocksContentY - (scroll ? s_scroll.y : 0f), 0f, shownHeight) * scope.scale;
                }
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

                    // 編集の有無はこのイベント内で変わらないので、部品の数は Layout と Repaint で揃う
                    if (edit != null)
                    {
                        // 範囲の切り替えで連結を解くと、現在の編集が入れ替わることがある
                        edit = DrawSelectionSettings(component, edit);
                    }

                    // 使う人の少ない設定は「▸ 設定」に畳む。またぐ・隠れているは次のクリックに効くので、編集が無くても出す。
                    // 縞の表示は小窓「表示切替」の［選択範囲］
                    if (DrawSettingsFoldout(RangeSettingsOpenKey, RangeSettingsChanged(edit)))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(16);
                            using (new EditorGUILayout.VerticalScope())
                            {
                                DrawRangeSettings(component, edit);
                            }
                        }
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
        // 「画像を入れる」の枠。グラデーションの枠と区別するため色相を変える（青緑）
        private static readonly Color DecalBlockColor = new Color(0.30f, 0.55f, 0.60f, 0.18f);
        private static readonly Color DecalBlockBorder = new Color(0.33f, 0.58f, 0.63f, 0.50f);

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

        /// <summary>ブロックの左右の余白（影響範囲のボタンが 1 行に収まるかの判定にも使う）</summary>
        private const float BlockPadding = 4f;

        private readonly struct BlockScope : System.IDisposable
        {
            public BlockScope(Color background, string headingKey)
            {
                var rect = EditorGUILayout.BeginVertical(GUILayout.Width(PanelWidth));
                if (Event.current.type == EventType.Repaint) EditorGUI.DrawRect(rect, background);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(BlockPadding);
                EditorGUILayout.BeginVertical();
                GUILayout.Label(Locales.Tr(headingKey), EditorStyles.miniBoldLabel);
            }

            public void Dispose()
            {
                EditorGUILayout.EndVertical();
                GUILayout.Space(BlockPadding);
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

        /// <summary>範囲ブロック・「画像を入れる」・色ブロックの「▸ 設定」を開いているか（EditorUserSettings。DrawSettingsFoldout）</summary>
        private const string RangeSettingsOpenKey = "ClickRecolor.RangeSettingsOpen";
        private const string DecalSettingsOpenKey = "ClickRecolor.DecalSettingsOpen";
        private const string ColorSettingsOpenKey = "ClickRecolor.ColorSettingsOpen";

        /// <summary>グラデーション ON のとき、ホイール・スライダー・最近の色の書き込み先を「終了色」にするか（SessionState。0 = 新しい色、1 = 終了色）</summary>
        private const string ColorEditTargetKey = "ClickRecolor.ColorEditTarget";

        // 現在の色で変わるスライダーのグラデーション（色が変わったときだけ作り直す）
        private static readonly GradientSlider.Cache s_satGradient = new GradientSlider.Cache(GradientSlider.GradientSize, "ClickRecolor.SatGradient");
        private static readonly GradientSlider.Cache s_valGradient = new GradientSlider.Cache(GradientSlider.GradientSize, "ClickRecolor.ValGradient");
        private static readonly GradientSlider.Cache s_redGradient = new GradientSlider.Cache(GradientSlider.GradientSize, "ClickRecolor.RedGradient");
        private static readonly GradientSlider.Cache s_greenGradient = new GradientSlider.Cache(GradientSlider.GradientSize, "ClickRecolor.GreenGradient");
        private static readonly GradientSlider.Cache s_blueGradient = new GradientSlider.Cache(GradientSlider.GradientSize, "ClickRecolor.BlueGradient");

        private static readonly RangePreset[] ModeButtonOrder = { RangePreset.Island, RangePreset.Box, RangePreset.SameColor, RangePreset.SimilarColor };

        /// <summary>
        /// パネルの幅。陰影の 3 本のスライダーは「陰影の暗さ（色 2）」の文言幅（約 105px）をラベルに取るが、
        /// Unity の Slider はラベルの残りが約 115px（トラック 60 ＋ 数値欄 50 ＋ 間隔）を切るとトラックを描かない。
        /// ？ボタン（約 20px）込みで収まるよう 240 → 270 に広げた（ユーザー報告 2026-09-25: スライダーが消えた）
        /// </summary>
        internal const float PanelWidth = 270f;
    }
}
