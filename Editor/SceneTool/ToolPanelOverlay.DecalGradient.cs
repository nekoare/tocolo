using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Decal;
using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>パネルの「画像を入れる」と「グラデーション」の欄</summary>
    internal sealed partial class ToolPanelOverlay
    {
        /// <summary>画像の欄（サムネイル付きの ObjectField）の一辺</summary>
        private const float DecalThumbnailSize = 64f;

        /// <summary>
        /// 「画像を入れる」トグルと、ON のときの画像・「▸ 設定」（箱で貼る・なめらかに貼る・ノーマルも反映・比率を保つ）。
        /// ON にすると Scene に置き方（［シール｜箱］）の操作部品が出る（RecolorSceneTool.DrawDecalBox。出すかどうかは小窓「箱を表示」の BoxToggleOverlay）。連結なら連結の全編集に効かせる（DrawGradient と同じ構造）
        /// </summary>
        private static void DrawDecal(ClickRecolor component, RecolorEdit edit)
        {
            // トグルを押したイベントの中で部品の数が変わらないよう、押す前の値で後半を出すか決める
            bool wasEnabled = edit.decalEnabled;
            // 置き方は編集ごと（新しく ON にする編集はいつもシールで始める）
            bool sticker = DecalBox.PlacementOf(edit) == DecalPlacement.Sticker;
            bool reset = false;
            bool enabled = WithHelp("Help:Decal", () =>
            {
                bool value = EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Color:Decal"), wasEnabled);
                reset = DrawToggleResetButton(wasEnabled, sticker ? "Scene:Color:DecalResetTooltip:Sticker" : "Scene:Color:DecalResetTooltip");
                return value;
            });
            if (reset)
            {
                DecalBox.ResetSettings(component, edit);
            }
            else if (enabled != wasEnabled)
            {
                DecalBox.SetEnabled(component, edit, enabled, DecalPlacement.Sticker);
                // ON にしたら画像の箱だけ出す（他の箱と重ならないように。小窓「箱を表示」で一緒に出すこともできる）
                if (enabled) ToolSession.ChooseBoxes(edit, selection: false, gradient: false, decal: true);
            }
            if (!wasEnabled) return;

            using (new EditorGUILayout.HorizontalScope())
            {
            GUILayout.Space(16);
            using (new EditorGUILayout.VerticalScope())
            {
            // Scene に出る物で何ができるかを一文で常に出す（？の説明は開かれないことが多いので、操作のしかただけはここに置く）
            GUILayout.Label(Locales.Tr(sticker ? "Scene:Color:DecalStickerCaption" : "Scene:Color:DecalBoxCaption"), EditorStyles.wordWrappedMiniLabel);
            // 画像の欄。置き方（箱で貼る）と、箱のときの「向きも面に合わせる」は「▸ 設定」に置く（既定のシールで足りることが多いため）
            Texture2D texture;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                texture = (Texture2D)EditorGUILayout.ObjectField(edit.decalTexture, typeof(Texture2D), false,
                    GUILayout.Width(DecalThumbnailSize), GUILayout.Height(DecalThumbnailSize));
            }
            // 消えた画像（参照切れ）は Unity の == で null と等しいので、None を選び直しても != では変化に見えない。参照が変わったかも見る
            // （触っていなければ ObjectField は渡した物をそのまま返すので、毎フレーム書き込むことはない）
            if (!ReferenceEquals(texture, edit.decalTexture) && (texture != edit.decalTexture || edit.HasMissingDecal))
            {
                DecalBox.SetTexture(component, edit, texture);
            }
            }
            }

            // 画像以外は「▸ 設定」に畳む。三角は「画像を入れる」のトグルと同じ列（字下げしない）、中身は 1 段字下げ（既定は閉じる）
            if (DrawSettingsFoldout(DecalSettingsOpenKey, DecalSettingsChanged(edit)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(16);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        // 箱で貼る（置き方。編集ごと）: オンで箱の正面からまっすぐ写す（平行投影）、オフ（既定）でシール（面に沿って貼る）
                        bool boxMode = WithHelp("Help:DecalPlacement",
                            () => EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Color:DecalBoxMode"), !sticker));
                        if (boxMode == sticker)
                        {
                            DecalBox.SetPlacement(component, edit, boxMode ? DecalPlacement.Box : DecalPlacement.Sticker);
                            SceneView.RepaintAll();
                        }
                        // 箱のときだけ「向きも面に合わせる」（シールはいつも面に向ける: 面とずれた向きのまま展開すると歪むだけ）。
                        // 押したイベントの中で部品の数が変わらないよう、押す前の置き方で出すか決める
                        if (!sticker)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                GUILayout.Space(16);
                                using (new EditorGUILayout.VerticalScope())
                                {
                                    bool follow = WithHelp("Help:DecalFollowSurface",
                                        () => EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Color:DecalFollowSurface"), ToolSession.DecalDragFollowsSurface));
                                    if (follow != ToolSession.DecalDragFollowsSurface) ToolSession.DecalDragFollowsSurface = follow;
                                }
                            }
                        }

                        // なめらかに貼る（重ね貼り）。lilToon 以外のマテリアルが混ざると重ね貼りできず焼き込みになるので、そのときは無効にして理由を出す
                        bool canOverlay = DecalOverlayMaterial.CanOverlayForUi(component, edit);
                        // 無効にするのはトグルだけ（「？」は押せるようにして、なぜ使えないかを読めるようにする）
                        bool smooth = WithHelp("Help:DecalSmooth", () =>
                        {
                            using (new EditorGUI.DisabledScope(!canOverlay))
                            {
                                return EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Color:DecalSmooth"), edit.decalSmooth);
                            }
                        });
                        if (smooth != edit.decalSmooth)
                        {
                            Undo.RecordObject(component, "Tocolo: なめらかに貼るを変更");
                            EditGroups.ForEachInGroup(component, edit, e => e.decalSmooth = smooth);
                            EditorUtility.SetDirty(component);
                        }
                        if (!canOverlay) GUILayout.Label(Locales.Tr("Scene:Color:DecalSmoothUnavailable"), EditorStyles.miniLabel);

                        // ノーマルも反映（なめらかに貼るの中の設定。焼き込みでは効かないので、なめらかに貼れるときだけ押せる）
                        // 字下げは他の入れ子と同じく 横並び＋空白＋縦並び（縦並びが無いと「？」の説明が横に並んで細長く潰れる: 実機 2026-10-04）
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(16);
                            using (new EditorGUILayout.VerticalScope())
                            {
                                bool normal = WithHelp("Help:DecalNormal", () =>
                                {
                                    using (new EditorGUI.DisabledScope(!canOverlay || !edit.decalSmooth))
                                    {
                                        return EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Color:DecalNormal"), edit.decalNormal);
                                    }
                                });
                                if (normal != edit.decalNormal)
                                {
                                    Undo.RecordObject(component, "Tocolo: ノーマルも反映を変更");
                                    EditGroups.ForEachInGroup(component, edit, e => e.decalNormal = normal);
                                    EditorUtility.SetDirty(component);
                                }
                            }
                        }

                        bool keepAspect = WithHelp("Help:DecalKeepAspect",
                            () => EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Color:DecalKeepAspect"), edit.decalKeepAspect));
                        if (keepAspect != edit.decalKeepAspect)
                        {
                            Undo.RecordObject(component, "Tocolo: 比率を保つを変更");
                            EditGroups.ForEachInGroup(component, edit, e => e.decalKeepAspect = keepAspect);
                            EditorUtility.SetDirty(component);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// グラデーション・画像を入れるのトグルの行の、？の左の［リセット］（ON の間だけ。OFF→ON では内容を残すので、初期値に戻す手段として置く。
        /// ユーザー要望 2026-10-05）。部品の数が変わらないよう、shown は押す前のトグルの値を渡す。押されたら true
        /// </summary>
        private static bool DrawToggleResetButton(bool shown, string tooltipKey)
        {
            if (!shown) return false;
            var content = new GUIContent(Locales.Tr("Scene:Color:ResetButton"), Locales.Tr(tooltipKey));
            return GUILayout.Button(content, EditorStyles.miniButton, GUILayout.ExpandWidth(false));
        }

        /// <summary>
        /// 「グラデーション」トグルと、ON のときの終了色（見本＋カラーコード）・色の書き込み先の切り替え。
        /// ON にすると Scene に箱が出る（RecolorSceneTool。出すかどうかは小窓「箱を表示」の BoxToggleOverlay）。連結（「アバター全体」・島の連結）なら連結の全編集に効かせる
        /// </summary>
        /// <summary>グラデーションのトグルより下の字下げ（px）</summary>
        private const float GradientIndent = 16f;

        private static void DrawGradient(ClickRecolor component, RecolorEdit edit)
        {
            // トグルを押したイベントの中で部品の数が変わらないよう、押す前の値で後半を出すか決める
            bool wasEnabled = edit.gradientEnabled;
            bool reset = false;
            bool enabled = WithHelp("Help:Gradient", () =>
            {
                bool value = EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Color:Gradient"), wasEnabled);
                reset = DrawToggleResetButton(wasEnabled, "Scene:Color:GradientResetTooltip");
                return value;
            });
            if (reset)
            {
                GradientBox.ResetSettings(component, edit);
            }
            else if (enabled != wasEnabled)
            {
                GradientBox.SetEnabled(component, edit, enabled);
                // ON にしたらグラデーションの箱だけ出す（他の箱と重ならないように。小窓「箱を表示」で一緒に出すこともできる）
                if (enabled) ToolSession.ChooseBoxes(edit, selection: false, gradient: true, decal: false);
            }
            if (!wasEnabled) return;

            // トグル以外は字下げして入れ子だと分かるようにする（ユーザー要望 2026-09-25）
            using (new EditorGUILayout.HorizontalScope())
            {
            GUILayout.Space(GradientIndent);
            using (new EditorGUILayout.VerticalScope())
            {
            // 混ぜ方［箱｜毛先グラデ］を先に、その設定（長さ・箱の中だけ）を続けて置き、色の見本はその下。
            // 押したイベントの中で部品の数が変わらないよう、押す前の値で長さの行を出すか決める
            bool tips = edit.gradientShape == GradientShape.Tips;
            int shape = WithHelp("Help:GradientShape", () => GUILayout.Toolbar(tips ? 1 : 0, new[]
            {
                Locales.Tr("Scene:Color:GradientShape:Box"),
                Locales.Tr("Scene:Color:GradientShape:Tips"),
            }));
            if (shape != (tips ? 1 : 0))
            {
                GradientBox.SetShape(component, edit, shape == 1 ? GradientShape.Tips : GradientShape.Box);
                SceneView.RepaintAll();
            }
            if (tips) DrawTipLength(component, edit);

            // 箱の中だけグラデーションにするか（外側は元の色のまま）。毛先からでは効かないので出さない（値は残し、箱に戻すと効く）
            if (!tips)
            {
                bool insideOnly = WithHelp("Help:GradientInsideOnly",
                    () => EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Color:GradientInsideOnly"), edit.gradientInsideOnly));
                if (insideOnly != edit.gradientInsideOnly)
                {
                    Undo.RecordObject(component, "Tocolo: 箱の中だけを変更");
                    EditGroups.ForEachInGroup(component, edit, e => e.gradientInsideOnly = insideOnly);
                    EditorUtility.SetDirty(component);
                }
            }

            // 色 1 → 色 2 の見本と▲、色 1・色 2 の切り替え（ホイール・スライダー・最近の色をどの色に効かせるか）。
            // 帯は字下げと枠の余白を越えて、枠線（1px）の内側いっぱいに広げる
            DrawGradientRow(component, edit, GradientIndent + BlockPadding - 1f, BlockPadding - 1f);
            }
            }
        }

        private const float TipPickButtonWidth = 24f;
        private static GUIContent s_tipPickContent;

        /// <summary>
        /// 長さを Scene で選ぶボタンの中身（エディタ組み込みの画鋲のアイコン。無ければ文字）。
        /// 色のスポイトと同じ絵にすると、同じパネルで働きの違うボタンを押し間違えるので、場所を指す絵にする
        /// </summary>
        private static GUIContent TipPickContent
        {
            get
            {
                if (s_tipPickContent == null)
                {
                    var icon = EditorGUIUtility.Load("icons/SceneTemplate/pinned.png") as Texture2D;
                    s_tipPickContent = icon != null
                        ? new GUIContent(icon, Locales.Tr("Scene:Color:GradientTipPick"))
                        : new GUIContent(Locales.Tr("Scene:Color:GradientTipPickShort"), Locales.Tr("Scene:Color:GradientTipPick"));
                }
                return s_tipPickContent;
            }
        }

        /// <summary>
        /// 毛先からの長さ: スライダー（cm なら面に沿った距離、% なら房の長さに対する割合）と［cm｜%］の切り替え。
        /// 値は RecolorEdit にメートル・割合（0..1）で持つ。連結なら連結の全編集に効かせる
        /// </summary>
        private static void DrawTipLength(ClickRecolor component, RecolorEdit edit)
        {
            bool ratio = edit.gradientTipUseRatio;
            float length = edit.gradientTipLength;
            float tipRatio = edit.gradientTipRatio;
            bool newRatio;
            // 見出しは置かない（［cm｜%］が単位なので何の値か分かる。見出しを付けるとパネルの幅でスライダーが潰れて消える）
            using (new EditorGUILayout.HorizontalScope())
            {
                if (ratio) tipRatio = EditorGUILayout.Slider(tipRatio * 100f, 1f, 100f) / 100f;
                else length = EditorGUILayout.Slider(length * 100f, 0.5f, 50f) / 100f;
                // Scene の髪をクリック・なぞって長さを決める（スポイトと同じ「押してから Scene をクリック」）
                bool picking = GUILayout.Toggle(ToolSession.TipPickActive, TipPickContent, EditorStyles.miniButton,
                    GUILayout.Width(TipPickButtonWidth), GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (picking != ToolSession.TipPickActive)
                {
                    ToolSession.TipPickActive = picking;
                    SceneView.RepaintAll();
                }
                newRatio = GUILayout.Toolbar(ratio ? 1 : 0, new[]
                {
                    Locales.Tr("Scene:Color:GradientTipUnit:Cm"),
                    Locales.Tr("Scene:Color:GradientTipUnit:Ratio"),
                }, GUILayout.Width(56f)) == 1;
                HelpMark.Draw("Help:GradientTipLength");
            }
            HelpMark.DrawBoxIfOpen("Help:GradientTipLength");
            if (newRatio == ratio && Mathf.Approximately(length, edit.gradientTipLength) && Mathf.Approximately(tipRatio, edit.gradientTipRatio)) return;
            BeginDragIfGrabbing("Tocolo: 毛先からの長さを変更");
            Undo.RecordObject(component, "Tocolo: 毛先からの長さを変更");
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.gradientTipUseRatio = newRatio;
                e.gradientTipLength = length;
                e.gradientTipRatio = tipRatio;
            });
            EditorUtility.SetDirty(component);
        }
    }
}
