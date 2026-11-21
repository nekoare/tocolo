using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Localization;
using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>パネルの「色」ブロックの［色を指定］［お手本に合わせる］の切り替えと、お手本に合わせるの欄</summary>
    internal sealed partial class ToolPanelOverlay
    {
        private static string[] s_colorModeNames;

        /// <summary>［色を指定］［お手本に合わせる］の切り替え</summary>
        private static void DrawColorModeSwitch(ClickRecolor component, RecolorEdit edit)
        {
            // 言語の切り替えに追従するよう毎回作る（2 語なので軽い）
            s_colorModeNames = new[] { Locales.Tr("Scene:Match:ModeColor"), Locales.Tr("Scene:Match:ModeMatch") };
            int current = edit.matchEnabled ? 1 : 0;
            int selected = GUILayout.Toolbar(current, s_colorModeNames);
            if (selected == current) return;
            // 「アバター全体」の連結は統計を連結でまとめる仕組みで、お手本に合わせるの髪全体の統計と噛み合わない
            if (selected == 1 && EditGroups.IsWholeAvatarGroup(edit))
            {
                ToolSession.ShowTransientNotice("Scene:Match:NoWholeAvatar");
                return;
            }
            SetMatchMode(component, edit, selected == 1);
        }

        /// <summary>
        /// お手本に合わせるの ON/OFF。ON ではグラデーション・画像を入れるを切り（組み合わせない。値は残す）、
        /// 測り終えたお手本があればそのまま効かせる。まだお手本が無ければ、すぐ Scene で選べるようにする。
        /// OFF では色の指定は未設定に戻る（お手本の指定は残し、ON に戻せば同じ色）
        /// </summary>
        internal static void SetMatchMode(ClickRecolor component, RecolorEdit edit, bool on)
        {
            if (component == null || edit == null) return;
            Undo.RecordObject(component, on ? "Tocolo: お手本に合わせる" : "Tocolo: 色を指定");
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.matchEnabled = on;
                if (on)
                {
                    if (e.gradientEnabled) e.gradientInitialized = true;
                    e.gradientEnabled = false;
                    if (e.decalEnabled) e.decalInitialized = true;
                    e.decalEnabled = false;
                    e.hasTarget = e.HasMatch;
                    if (e.hasTarget) e.confirmed = true;
                }
                else
                {
                    // 前に色を指定していれば（元の色と違う色が残っていれば）その色に戻す
                    e.hasTarget = e.targetColor != e.seedColor;
                }
            });
            EditorUtility.SetDirty(component);
            ToolSession.MatchPickActive = on && !edit.HasMatch;
            SceneView.RepaintAll();
        }

        /// <summary>
        /// renderer の slot 番目の枠をお手本にして、その場で測る（Scene のクリック・欄へのドラッグ＆ドロップから）。
        /// 指定できなければ理由を一時メッセージで出して何もしない。連結なら連結の全編集に効かせる（テクスチャごとに自分の統計で合わせる）
        /// </summary>
        internal static void ApplyMatchReference(ClickRecolor component, RecolorEdit edit, Renderer renderer, int slot)
        {
            if (component == null || edit == null) return;
            var status = MatchReference.Check(edit, renderer, slot);
            if (status != MatchReferenceStatus.Ok)
            {
                ToolSession.ShowTransientNotice(StatusKey(status));
                return;
            }
            Undo.RecordObject(component, "Tocolo: お手本を指定");
            int size = (int)component.previewResolution;
            var failed = MatchReferenceStatus.Ok;
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.matchEnabled = true;
                e.matchRenderer = renderer;
                e.matchSlot = slot;
                // 前のお手本の控えで別の色を出さないよう消してから測る
                e.matchProfileData = null;
                var measured = MatchReference.Refresh(component, e, size);
                if (measured != MatchReferenceStatus.Ok) failed = measured;
                e.hasTarget = e.HasMatch;
                if (e.hasTarget) e.confirmed = true;
            });
            EditorUtility.SetDirty(component);
            if (failed != MatchReferenceStatus.Ok) ToolSession.ShowTransientNotice(StatusKey(failed));
            SceneView.RepaintAll();
        }

        /// <summary>お手本の指定を外す（控えも消す。この編集は見た目を変えなくなる）</summary>
        private static void ClearMatchReference(ClickRecolor component, RecolorEdit edit)
        {
            Undo.RecordObject(component, "Tocolo: お手本を外す");
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.matchRenderer = null;
                e.matchSlot = 0;
                e.matchProfileData = null;
                e.hasTarget = false;
            });
            EditorUtility.SetDirty(component);
        }

        private static string StatusKey(MatchReferenceStatus status) => "Scene:Match:" + status;

        /// <summary>renderer の枠のうち、最初にお手本にできる（メインテクスチャが資産で、合わせる側と別の）枠。無ければ 0</summary>
        private static int DefaultMatchSlot(RecolorEdit edit, Renderer renderer)
        {
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                var texture = MatchReference.ReferenceTexture(renderer, i);
                if (texture != null && texture != edit.sourceTexture) return i;
            }
            return 0;
        }

        private static GUIContent s_matchPickContent;

        /// <summary>お手本を選ぶボタンの中身（スポイトと同じアイコン・別の説明）</summary>
        private static GUIContent MatchPickContent
        {
            get
            {
                if (s_matchPickContent == null)
                {
                    var icon = EditorGUIUtility.IconContent(EditorGUIUtility.isProSkin ? "d_eyeDropper.Large" : "eyeDropper.Large");
                    s_matchPickContent = icon != null && icon.image != null
                        ? new GUIContent(icon.image, Locales.Tr("Scene:Match:PickTooltip"))
                        : new GUIContent(Locales.Tr("Scene:Color:EyedropperShort"), Locales.Tr("Scene:Match:PickTooltip"));
                }
                return s_matchPickContent;
            }
        }

        /// <summary>お手本に合わせるの欄: 案内・お手本の行・見本の行・明るさの山・ずらし・陰影の暗さ・ハイライトの伸び・ガンマ・不透明度</summary>
        private static void DrawMatchSection(ClickRecolor component, RecolorEdit edit)
        {
            // 案内は行の部品より先に描く: 行のボタン・欄はこのイベントの中で状態を変えるので、後に描くと Layout と部品の数が食い違う
            DrawMatchNotices(component, edit);
            DrawMatchReferenceRow(component, edit);

            MatchProfile.TryParse(edit.matchProfileData, out var profile);
            using (new EditorGUI.DisabledScope(profile == null))
            {
                // 見本の行は「お手本の代表色」と「合わせた結果の代表色」を見せる。お手本がまだ無ければ灰色
                Color referenceColor = profile != null ? ToSrgb(MatchRepresentative.Reference(profile)) : Color.gray;
                Color resultColor = profile != null ? ToSrgb(MatchRepresentative.Result(profile, edit)) : Color.gray;
                DrawMatchColorRow(component, edit, referenceColor, resultColor);

                // 明るさの山。本体をつかんで左右＝明るさ、左の裾＝陰影の暗さ、右の裾＝ハイライトの伸び
                var mountainRect = GUILayoutUtility.GetRect(10f, MountainHeight, GUILayout.ExpandWidth(true));
                if (MatchMountainGUI.Draw(mountainRect, profile, edit, out float mountainLightness, out float mountainShadow, out float mountainHighlight))
                {
                    SetMountain(component, edit, mountainLightness, mountainShadow, mountainHighlight);
                }
                WithHelp("Help:MatchMountain", () =>
                {
                    GUILayout.Label(Locales.Tr("Scene:Match:MountainHint"), EditorStyles.wordWrappedMiniLabel);
                    return 0;
                });

                string lightnessLabel = Locales.Tr("Scene:Match:Lightness");
                string hueLabel = Locales.Tr("Scene:Match:Hue");
                string chromaLabel = Locales.Tr("Scene:Match:Chroma");
                string shadowLabel = Locales.Tr("Scene:Color:DarkEnd");
                string highlightLabel = Locales.Tr("Scene:Match:Highlight");
                string gammaLabel = Locales.Tr("Scene:Color:Gamma");
                string strengthLabel = Locales.Tr("Scene:Color:Strength");
                float previousLabelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = Mathf.Max(previousLabelWidth,
                    SliderLabelWidth(lightnessLabel, hueLabel, chromaLabel, shadowLabel, highlightLabel, gammaLabel, strengthLabel) + 4f);
                EditorGUI.BeginChangeCheck();
                float lightness = EditorGUILayout.Slider(lightnessLabel, edit.matchLightness, -0.5f, 0.5f);
                float hueShift = EditorGUILayout.Slider(hueLabel, edit.matchHue, -180f, 180f);
                float chromaShift = EditorGUILayout.Slider(chromaLabel, edit.matchChromaShift, -0.3f, 0.3f);
                float shadow = EditorGUILayout.Slider(shadowLabel, edit.matchShadow, -1f, 1f);
                float highlight = EditorGUILayout.Slider(highlightLabel, edit.matchHighlight, -1f, 1f);
                float gamma = EditorGUILayout.Slider(gammaLabel, edit.gamma, GammaMin, GammaMax);
                float strength = EditorGUILayout.Slider(strengthLabel, edit.strength, 0f, 1f);
                EditorGUIUtility.labelWidth = previousLabelWidth;
                if (EditorGUI.EndChangeCheck())
                {
                    BeginDragIfGrabbing("Tocolo: お手本からずらす");
                    Undo.RecordObject(component, "Tocolo: お手本からずらす");
                    EditGroups.ForEachInGroup(component, edit, e =>
                    {
                        e.matchLightness = lightness;
                        e.matchHue = hueShift;
                        e.matchChromaShift = chromaShift;
                        e.matchShadow = shadow;
                        e.matchHighlight = highlight;
                        e.gamma = gamma;
                        e.strength = strength;
                    });
                    EditorUtility.SetDirty(component);
                }
            }
        }

        private static Color ToSrgb(Vector3 oklab)
        {
            var color = OklabConverter.OklchToSRGBGamutMapped(OklabConverter.OklabToOklch(oklab));
            color.a = 1f;
            return color;
        }

        /// <summary>明るさの山の欄の高さ（px。下の▲の帯 MatchMountainGUI.MarkerStrip を含む）</summary>
        private const float MountainHeight = 90f + MatchMountainGUI.MarkerStrip;

        /// <summary>山で決めたずらし（明るさ・陰影の暗さ・ハイライトの伸び）を入れる。ドラッグ中は Undo を 1 回にまとめる</summary>
        private static void SetMountain(ClickRecolor component, RecolorEdit edit, float lightness, float shadow, float highlight)
        {
            BeginDragIfGrabbing("Tocolo: お手本からずらす");
            Undo.RecordObject(component, "Tocolo: お手本からずらす");
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.matchLightness = lightness;
                e.matchShadow = shadow;
                e.matchHighlight = highlight;
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>
        /// 「お手本の色｜合わせた色｜リセット」の行（色を指定の「元の色｜新しい色」と同じ並び）。お手本の色の見本を押してもお手本どおりに戻る
        /// </summary>
        private static void DrawMatchColorRow(ClickRecolor component, RecolorEdit edit, Color referenceColor, Color resultColor)
        {
            var labelRow = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight * 0.8f);
            var full = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            float swatchWidth = (full.width - ResetButtonWidth - RowGap * 2f) / 2f;
            EditorGUI.LabelField(new Rect(full.x, labelRow.y, swatchWidth, labelRow.height), Locales.Tr("Scene:Match:ReferenceColor"), EditorStyles.miniLabel);
            EditorGUI.LabelField(new Rect(full.x + swatchWidth + RowGap, labelRow.y, swatchWidth, labelRow.height),
                Locales.Tr("Scene:Match:ResultColor"), EditorStyles.miniLabel);

            var referenceRect = new Rect(full.x, full.y, swatchWidth, full.height);
            var resultRect = new Rect(referenceRect.xMax + RowGap, full.y, swatchWidth, full.height);
            var resetRect = new Rect(resultRect.xMax + RowGap, full.y, ResetButtonWidth, full.height);

            EditorGUI.DrawRect(referenceRect, referenceColor);
            GUI.Label(referenceRect, new GUIContent(string.Empty, Locales.Tr("Scene:Match:Reset")), GUIStyle.none);
            EditorGUIUtility.AddCursorRect(referenceRect, MouseCursor.Link);
            if (GUI.Button(referenceRect, GUIContent.none, GUIStyle.none)) ResetMatchAdjust(component, edit);

            EditorGUI.DrawRect(resultRect, resultColor);

            var content = new GUIContent(Locales.Tr("Scene:Color:ResetButton"), Locales.Tr("Scene:Match:Reset"));
            if (GUI.Button(resetRect, content, EditorStyles.miniButton)) ResetMatchAdjust(component, edit);
        }

        /// <summary>ずらし・陰影の暗さ・ハイライトの伸び・ガンマをお手本どおりに戻す（不透明度は戻さない。色を指定のリセットと同じ）</summary>
        internal static void ResetMatchAdjust(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null) return;
            Undo.RecordObject(component, "Tocolo: お手本どおりに戻す");
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.matchLightness = 0f;
                e.matchHue = 0f;
                e.matchChromaShift = 0f;
                e.matchShadow = 0f;
                e.matchHighlight = 0f;
                e.gamma = 1f;
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>「お手本」の行: 選ぶボタン（Scene でクリック）・Renderer の欄（ドラッグ＆ドロップ）・枠の選択（枠が複数のとき）</summary>
        private static void DrawMatchReferenceRow(ClickRecolor component, RecolorEdit edit)
        {
            // 枠の選択を出すかは、このイベントで欄が変わる前の Renderer で決める（ドロップしたイベントで部品の数を変えない）
            var current = edit.matchRenderer;
            GUILayout.Label(Locales.Tr("Scene:Match:Reference"), EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                bool active = GUILayout.Toggle(ToolSession.MatchPickActive, MatchPickContent, EditorStyles.miniButton,
                    GUILayout.Width(EyedropperButtonSize), GUILayout.Height(EditorGUIUtility.singleLineHeight + 2f));
                if (active != ToolSession.MatchPickActive)
                {
                    ToolSession.MatchPickActive = active;
                    SceneView.RepaintAll();
                }
                EditorGUI.BeginChangeCheck();
                var renderer = (Renderer)EditorGUILayout.ObjectField(edit.matchRenderer, typeof(Renderer), true);
                if (EditorGUI.EndChangeCheck())
                {
                    if (renderer == null) ClearMatchReference(component, edit);
                    else ApplyMatchReference(component, edit, renderer, DefaultMatchSlot(edit, renderer));
                }
            }

            if (current == null) return;
            var materials = current.sharedMaterials;
            if (materials == null || materials.Length <= 1) return;
            var names = new string[materials.Length];
            for (int i = 0; i < materials.Length; i++) names[i] = $"{i}: {(materials[i] != null ? materials[i].name : "-")}";
            int slot = Mathf.Clamp(edit.matchSlot, 0, materials.Length - 1);
            int chosen = EditorGUILayout.Popup(Locales.Tr("Scene:Match:Slot"), slot, names);
            if (chosen != slot && edit.matchRenderer == current) ApplyMatchReference(component, edit, current, chosen);
        }

        /// <summary>お手本の状態の案内（選び中・未指定・見つからない・測れない）。測れなかったときは［もう一度測る］</summary>
        private static void DrawMatchNotices(ClickRecolor component, RecolorEdit edit)
        {
            if (ToolSession.MatchPickActive)
            {
                EditorGUILayout.HelpBox(Locales.Tr("Scene:Match:PickHint"), MessageType.Info);
                return;
            }
            bool missing = !ReferenceEquals(edit.matchRenderer, null) && edit.matchRenderer == null;
            if (edit.matchRenderer == null && !missing)
            {
                EditorGUILayout.HelpBox(Locales.Tr(edit.HasMatch ? "Scene:Match:Missing" : "Scene:Match:ChooseHint"),
                    edit.HasMatch ? MessageType.Warning : MessageType.Info);
                return;
            }
            if (missing)
            {
                EditorGUILayout.HelpBox(Locales.Tr(edit.HasMatch ? "Scene:Match:Missing" : "Scene:Match:NoRenderer"), MessageType.Warning);
                return;
            }
            var status = MatchReference.LastStatus(edit) ?? MatchReference.Check(edit, edit.matchRenderer, edit.matchSlot);
            if (status == MatchReferenceStatus.Ok) return;
            EditorGUILayout.HelpBox(Locales.Tr(StatusKey(status)), MessageType.Warning);
            // 読めなかった（Read/Write 無効など）は直せば測れるので、指定し直さずに測り直せるようにする
            if (status != MatchReferenceStatus.Unreadable) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Locales.Tr("Scene:Match:Remeasure"), GUILayout.ExpandWidth(false)))
                {
                    ApplyMatchReference(component, edit, edit.matchRenderer, edit.matchSlot);
                }
            }
        }
    }
}
