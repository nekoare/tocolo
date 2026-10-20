using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Decal;
using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>パネルの「範囲」ブロック（影響範囲のボタン・▸ 設定・範囲の広げ方・編集の削除）</summary>
    internal sealed partial class ToolPanelOverlay
    {
        /// <summary>影響範囲（島／箱の中／同じ色／似た色）のプリセット。パネル最上部に置く（ユーザー要望 2026-09-24）</summary>
        private static void DrawModeSection(GameObject root)
        {
            // ［テクスチャの残りを選択］を出すかは、影響範囲のボタンを押す前の編集で決める（押したイベントの中で行が増減すると IMGUI の並びが崩れる）
            bool showRemainingParts = IsRemainingPartsTarget(root);
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
            string[] modeLabels =
            {
                Locales.Tr("Scene:Panel:Mode:Island"),
                Locales.Tr("Scene:Panel:Mode:Box"),
                Locales.Tr("Scene:Panel:Mode:SameColor"),
                Locales.Tr("Scene:Panel:Mode:SimilarColor"),
            };
            // 1 行の Toolbar は「一番長いボタンの幅 × 数」を求めるので、英語などで収まらないとパネルの中身ごと広がり、
            // 右寄せのボタンまで見切れる。そのときだけ「範囲」の 4 択と同じ 2 列にする（日本語は 1 行のまま）
            int selected = FitsToolbarInBlock(modeLabels)
                ? GUILayout.Toolbar(current, modeLabels)
                : GUILayout.SelectionGrid(current, modeLabels, 2);
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
                    // 「箱の中」に切り替えたら、箱をクリックしたパーツを囲む位置に置き直す（Scene にはその箱だけ出す。
                    // グラデーション・画像の箱は小窓「箱を表示」で出せる）
                    if (preset == RangePreset.Box)
                    {
                        SelectionBox.ResetToDefault(component, edit);
                        SelectionBox.SyncMembers(component, edit);
                        ToolSession.ChooseBoxes(edit, selection: true, gradient: false, decal: false);
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

            if (showRemainingParts) DrawSelectWholeTextureButton(root);
        }

        /// <summary>
        /// labels を GUILayout.Toolbar（既定の button スタイル）で 1 行に並べてブロックの中に収まるか。
        /// Unity は端と中のボタンに別のスタイルを使い、余白が数 px 違うので、その分 ToolbarFitSlack だけ早めに収まらない扱いにする
        /// </summary>
        private static bool FitsToolbarInBlock(string[] labels)
        {
            var style = GUI.skin.button;
            float widest = 0f;
            foreach (string label in labels) widest = Mathf.Max(widest, style.CalcSize(new GUIContent(label)).x);
            float available = PanelWidth - BlockPadding * 2f - style.margin.horizontal;
            return widest * labels.Length + ToolbarFitSlack <= available;
        }

        private const float ToolbarFitSlack = 4f;

        /// <summary>現在の編集が［テクスチャの残りを選択］の対象か: 影響範囲がパーツの編集（「アバター全体」の連結はもとから全体なので除く）</summary>
        private static bool IsRemainingPartsTarget(GameObject root)
        {
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            return edit != null && edit.mode == SelectionMode.Island && !EditGroups.IsWholeAvatarGroup(edit);
        }

        /// <summary>
        /// 影響範囲のボタンの真下・右端の「テクスチャの残りを選択」（影響範囲がパーツの編集のときだけ出す。箱の中・色のモードは範囲を箱・色で決めるので要らない）。
        /// 押すと、編集（連結なら各メンバー）のテクスチャのまだ選んでいないパーツを全部、点として足し、各点の色を揃えるを OFF にする（RemainingParts）。
        /// 要らないパーツは Ctrl＋クリックで外せる
        /// </summary>
        private static void DrawSelectWholeTextureButton(GameObject root)
        {
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            // 出すかは押す前の状態で決めているので、このイベントで影響範囲を変えたときは押せなくするだけ
            bool enabled = IsRemainingPartsTarget(root);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!enabled))
                {
                    var content = new GUIContent(Locales.Tr("Scene:Panel:SelectWholeTexture"), Locales.Tr("Scene:Panel:SelectWholeTextureTooltip"));
                    if (GUILayout.Button(content, GUILayout.ExpandWidth(false)) && enabled)
                    {
                        Undo.RecordObject(component, "Tocolo: テクスチャの残りを選択");
                        EditGroups.ForEachInGroup(component, edit, e =>
                        {
                            if (e.wholeTexture)
                            {
                                RemainingParts.ConvertLegacyWholeTexture(component, e);
                                return;
                            }
                            RemainingParts.Add(component, e);
                            // 全体で 1 つの明るさの分布にする（パーツごとに揃えると、テクスチャ全体を選んだときと見た目が変わる）
                            e.perSeedStats = false;
                        });
                        EditorUtility.SetDirty(component);
                        SceneView.RepaintAll();
                    }
                }
            }
        }

        /// <summary>
        /// 「色」ブロックの中身（現在の編集があるとき）。元の色・新しい色（16 進入力）・ホイール・HSV／RGB・最近の色・
        /// 種ごとの色揃え・陰影の暗さ・強さを出す。現在の編集が連結（「アバター全体」・島の連結）なら、変更は連結の全編集に効かせる（EditGroups.ForEachInGroup）
        /// </summary>
        private static void DrawHairToolNotice(NDMF.HairToolRole role)
        {
            switch (role)
            {
                case NDMF.HairToolRole.Over:
                    EditorGUILayout.HelpBox(Locales.Tr("Scene:Panel:HairToolOver"), MessageType.Info);
                    break;
                case NDMF.HairToolRole.Merged:
                    EditorGUILayout.HelpBox(Locales.Tr("Scene:Panel:HairToolMerged"), MessageType.Warning);
                    break;
                case NDMF.HairToolRole.Legacy:
                    EditorGUILayout.HelpBox(Locales.Tr("Scene:Panel:HairToolNotice"), MessageType.Warning);
                    break;
            }
        }

        /// <summary>
        /// 範囲ブロックの「▸ 設定」の中身: マテリアルをまたいで選ぶ・隠れている範囲も選ぶ（ツール全体。次のクリックに効く）と、
        /// 現在の編集があれば 箱の中の色を揃える（箱の中）・ゴマ塩除去（同じ色・似た色）・はみ出し幅。連結なら連結の全編集に効かせる
        /// </summary>
        private static void DrawRangeSettings(ClickRecolor component, RecolorEdit edit)
        {
            // 別のマテリアル（テクスチャ）の島も同じ編集に足せるか（島の連結）。既定 OFF
            bool cross = WithHelp("Help:CrossTexture",
                () => EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Panel:CrossTexture"), ToolSession.CrossTextureEnabled));
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

            // 矩形選択で奥に隠れている島も拾うか（UV アイランドモードの矩形選択だけに効く）。既定 OFF
            bool hidden = WithHelp("Help:SelectHidden",
                () => EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Panel:SelectHidden"), ToolSession.SelectHiddenEnabled));
            if (hidden != ToolSession.SelectHiddenEnabled) ToolSession.SelectHiddenEnabled = hidden;

            if (edit == null) return;
            // モードはこのイベント内で変わらないので、部品の数は Layout と Repaint で揃う
            if (edit.mode == SelectionMode.Box)
            {
                bool perPart = WithHelp("Help:BoxPerPartStats",
                    () => EditorGUILayout.ToggleLeft(Locales.Tr("Scene:Select:BoxPerPartStats"), edit.boxPerPartStats));
                if (perPart != edit.boxPerPartStats)
                {
                    Undo.RecordObject(component, "Tocolo: パーツごとに色を揃えるかを変更");
                    EditGroups.ForEachInGroup(component, edit, e => e.boxPerPartStats = perPart);
                    EditorUtility.SetDirty(component);
                }
            }
            // ゴマ塩除去は色モードだけ（UV アイランドモードは島単位なので不要）
            if (edit.mode == SelectionMode.Color)
            {
                EditorGUI.BeginChangeCheck();
                int cleanup = WithHelp("Help:Cleanup",
                    () => EditorGUILayout.IntSlider(Locales.Tr("Scene:Select:Cleanup"), edit.cleanupRadius, 0, 2));
                if (EditorGUI.EndChangeCheck())
                {
                    BeginDragIfGrabbing("Tocolo: 選択の設定を変更");
                    Undo.RecordObject(component, "Tocolo: 選択の設定を変更");
                    EditGroups.ForEachInGroup(component, edit, e => e.cleanupRadius = cleanup);
                    EditorUtility.SetDirty(component);
                }
            }

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

        /// <summary>範囲ブロックの「▸ 設定」の中に、既定から変えた物があるか（畳んだままでも効いていることに気付けるように見出しで知らせる）</summary>
        private static bool RangeSettingsChanged(RecolorEdit edit)
        {
            if (ToolSession.CrossTextureEnabled || ToolSession.SelectHiddenEnabled) return true;
            if (edit == null) return false;
            if (edit.padding != DefaultEdit.padding) return true;
            if (edit.mode == SelectionMode.Box && edit.boxPerPartStats != DefaultEdit.boxPerPartStats) return true;
            return edit.mode == SelectionMode.Color && edit.cleanupRadius != DefaultEdit.cleanupRadius;
        }

        /// <summary>画像の「▸ 設定」の中に、既定（シール・なめらかに貼る・ノーマルも反映・比率を保つ）から変えた物があるか</summary>
        private static bool DecalSettingsChanged(RecolorEdit edit)
        {
            // 「向きも面に合わせる」は箱で貼るときだけの設定なので、箱で貼る時点で「変更あり」になる
            if (DecalBox.PlacementOf(edit) != DecalPlacement.Sticker) return true;
            return edit.decalSmooth != DefaultEdit.decalSmooth
                || edit.decalNormal != DefaultEdit.decalNormal
                || edit.decalKeepAspect != DefaultEdit.decalKeepAspect;
        }

        /// <summary>「変更あり」の比較に使う既定値（フィールドの初期値。新しい編集もこの値で作る）</summary>
        private static readonly RecolorEdit DefaultEdit = new RecolorEdit();

        /// <summary>
        /// 各ブロックの末尾の「▸ 設定」の見出し（changed なら「設定（変更あり）」）。押す前の開閉を返す（押したイベントの中で部品の数が変わらないよう、
        /// 呼び出し側は戻り値で中身を出すか決める）。開閉はプロジェクトごとのユーザー設定（UserSettings）に覚える:
        /// EditorPrefs だと全プロジェクトで共有になり、SessionState だと Unity を閉じると忘れる
        /// </summary>
        private static bool DrawSettingsFoldout(string key, bool changed)
        {
            bool open = EditorUserSettings.GetConfigValue(key) == "1";
            bool next = EditorGUILayout.Foldout(open, Locales.Tr(changed ? "Scene:Panel:SettingsChanged" : "Scene:Panel:Settings"), true);
            if (next != open) EditorUserSettings.SetConfigValue(key, next ? "1" : "0");
            return open;
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
    }
}
