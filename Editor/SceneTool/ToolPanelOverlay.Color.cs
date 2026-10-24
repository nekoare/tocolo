using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Decal;
using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>パネルの「色」ブロック（カラーホイール・色の行・スライダー・最近の色・陰影・色の確定とリセット）</summary>
    internal sealed partial class ToolPanelOverlay
    {
        private static void DrawColorSection(ClickRecolor component, RecolorEdit edit)
        {
            // 「色未設定」と「同じ UV の双子」の案内は 2026-09-24 に撤去（ユーザー判断: 不要）。双子判定自体は残している
            // 髪ツール（キメラヘアマスター）の対象パーツなら、髪ツールとの重なり方を案内する。
            // 編集を作ったときの判定を覚えておかず描くたびに判定する: 髪ツールの削除・有効/無効・統合の切り替えをすぐ案内に映すため
            DrawHairToolNotice(NDMF.HairToolTargets.RoleOf(edit.seedRenderer));
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
                // 上の案内はどれもクリックした所のマテリアルの設定で直すので、そのマテリアルを Inspector に出すボタンを 1 つだけ置く
                if (layerNotice.HasMaterialNotice && layerNotice.material != null)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(Locales.Tr("Scene:Panel:OpenMaterial"), EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                        {
                            InspectorJump.Select(layerNotice.material);
                        }
                    }
                }
                if (ToolSession.TexTransToolNotice) EditorGUILayout.HelpBox(Locales.Tr("Scene:Panel:TexTransToolNotice"), MessageType.Warning);
            }
            // Ctrl＋クリックで種を足せなかった理由など（数秒で消える）。出すかは Layout のときに決めて、続くイベントでも同じにする
            // （時刻で消えるので、Layout とマウスのイベントの間に消える時刻を過ぎると行の数が食い違い、GUILayout が崩れる）
            if (Event.current.type == EventType.Layout)
            {
                s_shownNotice = ToolSession.TransientNotice;
                s_shownNoticeArgs = ToolSession.TransientNoticeArgs;
            }
            if (s_shownNotice != null)
            {
                var args = s_shownNoticeArgs ?? System.Array.Empty<object>();
                EditorGUILayout.HelpBox(args.Length > 0 ? Locales.Tr(s_shownNotice, args) : Locales.Tr(s_shownNotice), MessageType.Info);
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

            // 色を決める前は新しい色が仮の値（元の色）なので、自動調整の基準にしない
            // end（色 2 を編集中）なら自動調整・陰影の暗さ・強さは色 2 側の値（gradientDarkEndRatio / gradientStrength）を読み書きする
            using (new EditorGUI.DisabledScope(!edit.hasTarget))
            using (new EditorGUILayout.HorizontalScope())
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
                // 陰影の暗さ・陰影の強調を初期値に戻す（不透明度は戻さない。ユーザー要望 2026-10-04）
                var resetShading = new GUIContent(Locales.Tr("Scene:Color:ResetButton"), Locales.Tr("Scene:Color:ResetShadingTooltip"));
                if (GUILayout.Button(resetShading, GUILayout.ExpandWidth(false))) ResetShading(component, edit, end);
            }

            // ドラッグ中・数値欄への入力中の変更は Undo 1 回にまとめる
            EditorGUI.BeginChangeCheck();
            // 色 2 のときはラベルに「（色 2）」を付けて、どちらの値を触っているか分かるようにする
            string shadingSuffix = end ? Locales.Tr("Scene:Color:ShadingFor2") : "";
            float currentDarkEndRatio = end ? edit.gradientDarkEndRatio : edit.darkEndRatio;
            float currentStrength = end ? edit.gradientStrength : edit.strength;
            string darkEndLabel = Locales.Tr("Scene:Color:DarkEnd") + shadingSuffix;
            string strengthLabel = Locales.Tr("Scene:Color:Strength") + shadingSuffix;
            // 「（色 2）」付きの文言がパネル既定のラベル幅（70px）に収まらず見切れるので、ラベル幅は「（色 2）」付きの文言に合わせる。
            // 色 1 の編集中も同じ幅にして、切り替えでスライダーの位置が動かないようにする
            string suffix2 = Locales.Tr("Scene:Color:ShadingFor2");
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Max(previousLabelWidth,
                SliderLabelWidth(Locales.Tr("Scene:Color:DarkEnd") + suffix2, Locales.Tr("Scene:Color:Strength") + suffix2) + 4f);
            // 表示は「陰影の暗さ」（右ほど暗い）。保存値 darkEndRatio は「暗部の明るさ」（右ほど明るい）なので 1 − 値で表裏を変換する
            float darkEnd = 1f - WithHelp("Help:DarkEnd",
                () => EditorGUILayout.Slider(darkEndLabel, 1f - currentDarkEndRatio, 0f, 1f));
            float strength = WithHelp("Help:Strength",
                () => EditorGUILayout.Slider(strengthLabel, currentStrength, 0f, 1f));
            EditorGUIUtility.labelWidth = previousLabelWidth;
            if (EditorGUI.EndChangeCheck())
            {
                BeginDragIfGrabbing("Tocolo: 色の設定を変更");
                Undo.RecordObject(component, "Tocolo: 色の設定を変更");
                EditGroups.ForEachInGroup(component, edit, e =>
                {
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

            // 陰影の強調・各点の色を揃える・元のグラデーションを打ち消すは「▸ 設定」に畳む
            if (DrawSettingsFoldout(ColorSettingsOpenKey, ColorSettingsChanged(component, edit)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(16);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        DrawColorSettings(component, edit);
                    }
                }
            }

            // グラデーションの項目は暗めの赤の枠でまとめる（ユーザー要望 2026-09-25）。上の不透明度スライダーと詰まらないよう少し空ける
            EditorGUILayout.Space(6);
            // 小窓「箱を表示」をこのブロックの高さに並べるため、ブロックの上端（スクロールの中身の座標）を覚える
            if (Event.current.type == EventType.Repaint) s_boxBlocksContentY = GUILayoutUtility.GetLastRect().yMax;
            using (new SubBlockScope(GradientBlockColor, GradientBlockBorder)) DrawGradient(component, edit);

            // 画像を入れる（グラデーションと同じく編集ごとの付属設定＋専用の箱）
            EditorGUILayout.Space(6);
            using (new SubBlockScope(DecalBlockColor, DecalBlockBorder)) DrawDecal(component, edit);
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
        /// 色ブロックの「▸ 設定」の中身: 陰影の強調（色 1・色 2 で共通）・各点の色を揃える（種か連結のメンバーが複数のときだけ）・
        /// 元のグラデーションを打ち消す。連結なら連結の全編集に効かせる
        /// </summary>
        private static void DrawColorSettings(ClickRecolor component, RecolorEdit edit)
        {
            EditorGUI.BeginChangeCheck();
            string stretchLabel = Locales.Tr("Scene:Color:ShadingStretch");
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Max(previousLabelWidth, SliderLabelWidth(stretchLabel) + 4f);
            float shadingStretch = WithHelp("Help:ShadingStretch",
                () => EditorGUILayout.Slider(stretchLabel, edit.shadingStretch, 0f, 1f));
            EditorGUIUtility.labelWidth = previousLabelWidth;
            if (EditorGUI.EndChangeCheck())
            {
                BeginDragIfGrabbing("Tocolo: 色の設定を変更");
                Undo.RecordObject(component, "Tocolo: 色の設定を変更");
                EditGroups.ForEachInGroup(component, edit, e => e.shadingStretch = shadingStretch);
                EditorUtility.SetDirty(component);
            }

            // 種が複数か連結のメンバーが複数のときだけ、種（メンバー）ごとに色を揃えるかを選べる（どちらも 1 つなら効かない）
            if (ShowsPerSeedStats(component, edit))
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

            // 元のグラデーションを打ち消す（実験的）。トグルを押したイベントで部品の数が変わらないよう押す前の値で出し分ける
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
        }

        /// <summary>「各点の色を揃える」を出すか: 種（主種＋Ctrl＋クリックで足した種。共有の種色の編集は追加の種を使わないので 1）か連結のメンバーが複数</summary>
        private static bool ShowsPerSeedStats(ClickRecolor component, RecolorEdit edit)
        {
            // 「箱の中」は種を使わない（揃えるのは「箱の中の色を揃える」の方）
            if (edit.mode == SelectionMode.Box) return false;
            int seedCount = 1 + (!edit.hasSeedOklab && edit.extraSeeds != null ? edit.extraSeeds.Count : 0);
            return seedCount > 1 || EditGroups.Count(component, edit) > 1;
        }

        /// <summary>色ブロックの「▸ 設定」の中に、既定から変えた物があるか（「各点の色を揃える」は出ているときだけ数える）</summary>
        private static bool ColorSettingsChanged(ClickRecolor component, RecolorEdit edit)
        {
            if (!Mathf.Approximately(edit.shadingStretch, DefaultEdit.shadingStretch)) return true;
            if (edit.flattenBase != DefaultEdit.flattenBase) return true;
            return ShowsPerSeedStats(component, edit) && edit.perSeedStats != DefaultEdit.perSeedStats;
        }

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
                EditGroups.ForEachInGroup(component, edit, e =>
                {
                    e.gradientColor = e.seedColor;
                    // ガンマは色相・彩度と同じく色の一部としてリセットする（ユーザー要望 2026-10-04）
                    e.gradientGamma = 1f;
                });
                EditorUtility.SetDirty(component);
                return;
            }
            // グラデーション中の色 1 は、色だけを元の色にする（色の指定・グラデーション・選択範囲は残す）。
            // 取り消すと色未設定の編集になり、別の場所を触ったときに選択範囲ごと捨てられていた（ユーザー報告 2026-10-03）
            if (edit.gradientEnabled)
            {
                Undo.RecordObject(component, "Tocolo: 色1を元の色に戻す");
                EditGroups.ForEachInGroup(component, edit, e =>
                {
                    e.targetColor = e.seedColor;
                    e.gamma = 1f;
                });
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

            string gammaLabel = Locales.Tr("Scene:Color:Gamma");
            float labelWidth;
            if (mode == SliderModeHsv)
            {
                string hLabel = Locales.Tr("Scene:Color:Hue");
                string sLabel = Locales.Tr("Scene:Color:Sat");
                string vLabel = Locales.Tr("Scene:Color:Val");
                labelWidth = SliderLabelWidth(hLabel, sLabel, vLabel, gammaLabel);

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
                labelWidth = SliderLabelWidth(rLabel, gLabel, bLabel, gammaLabel);

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

            DrawGammaSlider(component, edit, end, gammaLabel, labelWidth);
        }

        /// <summary>ガンマのスライダーの範囲（lilToon の色調補正のガンマと同じ）</summary>
        private const float GammaMin = 0.01f, GammaMax = 2f;

        private static readonly GradientSlider.Cache s_gammaGradient = new GradientSlider.Cache(GradientSlider.GradientSize, "ClickRecolor.GammaGradient");

        /// <summary>
        /// 色のスライダーの下（明るさ・青の下）のガンマ（lilToon の色調補正のガンマと同じ式・範囲。小さいほど明るい。ユーザー要望 2026-10-04）。
        /// end（色 2 を編集中）なら色 2 のガンマ。トラックには編集中の色にそのガンマを掛けた色を敷く
        /// </summary>
        private static void DrawGammaSlider(ClickRecolor component, RecolorEdit edit, bool end, string label, float labelWidth)
        {
            float current = end ? edit.gradientGamma : edit.gamma;
            Color c = EditingColor(edit, end);
            var linear = new Vector3(Mathf.GammaToLinearSpace(c.r), Mathf.GammaToLinearSpace(c.g), Mathf.GammaToLinearSpace(c.b));
            s_gammaGradient.Update(new Vector3(c.r, c.g, c.b), t =>
            {
                var shifted = Colors.ColorShiftCpu.ApplyGamma(linear, Mathf.Lerp(GammaMin, GammaMax, t));
                return new Color(Mathf.LinearToGammaSpace(shifted.x), Mathf.LinearToGammaSpace(shifted.y), Mathf.LinearToGammaSpace(shifted.z));
            });

            EditorGUI.BeginChangeCheck();
            float gamma = GradientSlider.Draw(label, current, GammaMin, GammaMax, s_gammaGradient.Texture, "0.00", labelWidth);
            if (!EditorGUI.EndChangeCheck()) return;
            BeginDragIfGrabbing("Tocolo: ガンマを変更");
            Undo.RecordObject(component, "Tocolo: ガンマを変更");
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                if (end) e.gradientGamma = gamma;
                else e.gamma = gamma;
            });
            EditorUtility.SetDirty(component);
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
        /// 陰影の暗さ・陰影の強調を初期値に戻す（陰影の暗さは色を決めたときと同じ自動の値、陰影の強調 0）。不透明度は戻さない（ユーザー判断 2026-10-04）。
        /// end（色 2 を編集中）なら陰影の暗さは色 2 の値。陰影の強調は色 1・色 2 で共通。連結なら連結の全編集に効かせる
        /// </summary>
        internal static void ResetShading(ClickRecolor component, RecolorEdit edit, bool end)
        {
            if (component == null || edit == null) return;
            Undo.RecordObject(component, "Tocolo: 陰影の設定を戻す");
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.shadingStretch = 0f;
                if (end) e.gradientDarkEndRatio = DarkEndAutoAdjust.Compute(e.gradientColor);
                else e.darkEndRatio = DarkEndAutoAdjust.Compute(e.targetColor);
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>
        /// 色の指定を取り消して元の色に戻す（新しい色 = 元の色、hasTarget = false、グラデーション OFF）。プレビューは元の見た目に戻り、
        /// 一度色を決めた編集（confirmed）なので選択範囲は残る（自動で捨てない・再クリックで選び直せる）。
        /// 現在の編集（ハイライト）はそのまま残る。ガンマは色の一部として 1 に戻し、暗部の明るさ等の手動値は変えない
        /// （次に色を選んだときは ApplyTargetColor が初回と同じく暗部の明るさを自動調整する）。連結（「アバター全体」・島の連結）なら連結の全編集に効かせる
        /// </summary>
        internal static void ResetToOriginal(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null) return;
            Undo.RecordObject(component, "Tocolo: 元の色に戻す");
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.targetColor = e.seedColor;
                e.gamma = 1f;
                e.hasTarget = false;
                // グラデーションも切る（元の色に戻したのに終了色が残って見えるのを防ぐ。箱と終了色の値は残し、次に ON にしたときに使う）
                if (e.gradientEnabled) e.gradientInitialized = true;
                e.gradientEnabled = false;
            });
            EditorUtility.SetDirty(component);
        }
    }
}
