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
            }
            }
        }
    }
}
