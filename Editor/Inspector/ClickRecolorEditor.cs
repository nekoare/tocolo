using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Localization;
using Nekoare.ClickRecolor.Editor.SceneTool;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditorInternal;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Inspector
{
    /// <summary>ClickRecolor の Inspector。Scene ツールの開始／終了・編集一覧・設定項目を出す</summary>
    [CustomEditor(typeof(ClickRecolor))]
    internal sealed class ClickRecolorEditor : UnityEditor.Editor
    {
        private SerializedProperty _previewResolution;
        private SerializedProperty _previewEnabled;
        private SerializedProperty _excludedRenderers;
        private SerializedProperty _applyOnBuild;
        private SerializedProperty _edits;
        private ReorderableList _editList;

        /// <summary>
        /// 一覧の描画中に押されたボタンの処理は、描画が終わってから行う
        /// （描画の途中で edits を直接書き換えると、後続の要素の描画と SerializedObject がずれるため）
        /// </summary>
        private string _selectRequestId;
        private bool _deleteAllRequested;

        private void OnEnable()
        {
            _previewResolution = serializedObject.FindProperty(nameof(ClickRecolor.previewResolution));
            _previewEnabled = serializedObject.FindProperty(nameof(ClickRecolor.previewEnabled));
            _applyOnBuild = serializedObject.FindProperty(nameof(ClickRecolor.applyOnBuild));
            _edits = serializedObject.FindProperty(nameof(ClickRecolor.edits));
            _excludedRenderers = serializedObject.FindProperty(nameof(ClickRecolor.excludedRenderers));
            _editList = new ReorderableList(serializedObject, _edits,
                draggable: true, displayHeader: true, displayAddButton: false, displayRemoveButton: true)
            {
                drawHeaderCallback = DrawEditListHeader,
                drawElementCallback = DrawEditElement,
                elementHeight = EditorGUIUtility.singleLineHeight + 4f,
                // 「アバター全体」の連結は先頭の編集だけを行にし、他は高さ 0 で隠す
                elementHeightCallback = GetEditElementHeight,
                onRemoveCallback = RemoveEditElement,
                onReorderCallbackWithDetails = OnEditReordered,
            };
            ToolManager.activeToolChanged += Repaint;
        }

        private void OnDisable()
        {
            ToolManager.activeToolChanged -= Repaint;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var component = (ClickRecolor)target;

            Locales.DrawLanguagePicker();

            // ツールが有効でも対象（ルート）がこの GameObject でなければ「開始」を出す（押すと対象を乗り換える）
            if (RecolorSceneTool.IsActive && ToolSession.TryGetActiveRoot(out var root) && root == component.gameObject)
            {
                if (GUILayout.Button(Locales.Tr("Inspector:StopTool"), GUILayout.Height(32))) RecolorSceneTool.Deactivate();
            }
            else
            {
                bool persistent = EditorUtility.IsPersistent(target);
                // Play 中の編集は Play 終了で消えるので開始させない
                bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
                // Prefab 編集モード中は NDMF のプレビューが出ないので開始させない
                bool prefabMode = ToolSession.IsInPrefabMode;
                using (new EditorGUI.DisabledScope(persistent || playing || prefabMode))
                {
                    if (GUILayout.Button(Locales.Tr("Inspector:StartTool"), GUILayout.Height(32))) RecolorSceneTool.Activate(component.gameObject);
                }
                if (persistent) EditorGUILayout.HelpBox(Locales.Tr("Inspector:PersistentNotice"), MessageType.Warning);
                if (playing) EditorGUILayout.HelpBox(Locales.Tr("Inspector:PlayModeNotice"), MessageType.Warning);
                if (prefabMode && !persistent) EditorGUILayout.HelpBox(Locales.Tr("Scene:Panel:PrefabModeNotice"), MessageType.Warning);
            }

            _editList.DoLayoutList();
            using (new EditorGUI.DisabledScope(component.edits.Count == 0))
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Locales.Tr("Inspector:Edit:DeleteAll"), GUILayout.ExpandWidth(false))) _deleteAllRequested = true;
            }
            string unreadable = FindUnreadableRendererNames(component);
            if (unreadable != null) EditorGUILayout.HelpBox(Locales.Tr("Inspector:UnreadableMesh", unreadable), MessageType.Warning);

            // 書き出し欄は有料版が差し込む。無ければ案内だけ出す
            var drawExportSection = InspectorExtensions.DrawExportSection;
            if (drawExportSection != null) drawExportSection(this, component);
            else EditorGUILayout.HelpBox(Locales.Tr("Inspector:Export:ProOnly"), MessageType.Info);

            // 項目名は日本語で出す（ユーザー要望 2026-09-26）。解像度は「2048×2048」のように分かりやすく
            DrawResolutionWithHelp(_previewResolution, "Help:PreviewResolution");
            EditorGUILayout.PropertyField(_previewEnabled, new GUIContent(Locales.Tr("Inspector:PreviewEnabled")));
            DrawPropertyWithHelp(_applyOnBuild, new GUIContent(Locales.Tr("Inspector:ApplyOnBuild")), "Help:ApplyOnBuild");
            // 除外リスト（Scene のオーバーレイと同じ一覧。ここでも足し引きできる）
            DrawPropertyWithHelp(_excludedRenderers, new GUIContent(Locales.Tr("Inspector:ExcludedRenderers")), "Help:ExcludeList");
            // 無料版では反映されないので、文言を分ける（レビュー指摘 2026-09-25）
            EditorGUILayout.LabelField(
                Locales.Tr(drawExportSection != null ? "Inspector:ApplyOnBuildHint" : "Inspector:ApplyOnBuildHint:Free"),
                EditorStyles.miniLabel);

            serializedObject.ApplyModifiedProperties();

            HandleDeferredRequests(component);
        }

        /// <summary>プロパティの右に「？」を置き、開いていれば直下に説明を出す</summary>
        private static void DrawPropertyWithHelp(SerializedProperty property, string helpKey)
        {
            DrawPropertyWithHelp(property, null, helpKey);
        }

        private static void DrawPropertyWithHelp(SerializedProperty property, GUIContent label, string helpKey)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (label != null) EditorGUILayout.PropertyField(property, label);
                else EditorGUILayout.PropertyField(property);
                HelpMark.Draw(helpKey);
            }
            HelpMark.DrawBoxIfOpen(helpKey);
        }

        private static readonly WorkingResolution[] ResolutionChoices = { WorkingResolution.R1024, WorkingResolution.R2048, WorkingResolution.Full };

        /// <summary>プレビュー解像度のポップアップ。表示は「1024×1024」「2048×2048」「元の解像度」（enum 名の R2048 は出さない）</summary>
        private static void DrawResolutionWithHelp(SerializedProperty property, string helpKey)
        {
            var names = new string[ResolutionChoices.Length];
            int current = 0;
            for (int i = 0; i < ResolutionChoices.Length; i++)
            {
                int v = (int)ResolutionChoices[i];
                names[i] = v > 0 ? $"{v}×{v}" : Locales.Tr("Inspector:PreviewResolution:Full");
                if (v == property.intValue) current = i;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                int selected = EditorGUILayout.Popup(Locales.Tr("Inspector:PreviewResolution"), current, names);
                if (EditorGUI.EndChangeCheck()) property.intValue = (int)ResolutionChoices[selected];
                HelpMark.Draw(helpKey);
            }
            HelpMark.DrawBoxIfOpen(helpKey);
        }

        /// <summary>見出し: 左に「編集一覧」、右に色を決めた編集の件数</summary>
        private void DrawEditListHeader(Rect rect)
        {
            var component = (ClickRecolor)target;
            // 色を決めた編集だけを数える（クリック直後の色未設定の編集は仮の状態なので数えない）
            // 「アバター全体」の連結は 1 件と数える（先頭の編集だけ）
            int decided = 0;
            foreach (var e in component.edits) if (e != null && e.hasTarget && EditGroups.IsHead(component, e)) decided++;
            EditorGUI.LabelField(rect, Locales.Tr("Inspector:Edits"));
            var countStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            EditorGUI.LabelField(rect, Locales.Tr("Inspector:EditCount", decided), countStyle);
        }

        /// <summary>index の編集（serializedObject.Update 済みなので component.edits と並びが一致する）。範囲外は null</summary>
        private RecolorEdit GetEdit(int index)
        {
            var edits = ((ClickRecolor)target).edits;
            return index >= 0 && index < edits.Count ? edits[index] : null;
        }

        /// <summary>「アバター全体」の連結の先頭でない編集は一覧に出さない（行の高さ 0）</summary>
        private float GetEditElementHeight(int index)
        {
            var edit = GetEdit(index);
            if (edit != null && !EditGroups.IsHead((ClickRecolor)target, edit)) return 0f;
            return EditorGUIUtility.singleLineHeight + 4f;
        }

        /// <summary>serialized の edits で、groupId が同じ要素の添字（並び順）</summary>
        private List<int> FindGroupIndices(string groupId)
        {
            var result = new List<int>();
            for (int i = 0; i < _edits.arraySize; i++)
            {
                if (_edits.GetArrayElementAtIndex(i).FindPropertyRelative(nameof(RecolorEdit.groupId)).stringValue == groupId) result.Add(i);
            }
            return result;
        }

        /// <summary>serialized の edits で、id が一致する要素の添字。無ければ -1</summary>
        private int FindIndexById(string id)
        {
            for (int i = 0; i < _edits.arraySize; i++)
            {
                if (_edits.GetArrayElementAtIndex(i).FindPropertyRelative(nameof(RecolorEdit.id)).stringValue == id) return i;
            }
            return -1;
        }

        /// <summary>
        /// 並べ替えは連結単位にする: 動かした行（連結の先頭）の直後へ、同じ連結の他の編集を元の並び順で寄せる
        /// （隠れている編集が元の位置に取り残されないようにする）
        /// </summary>
        private void OnEditReordered(ReorderableList list, int oldIndex, int newIndex)
        {
            var headElement = _edits.GetArrayElementAtIndex(newIndex);
            string groupId = headElement.FindPropertyRelative(nameof(RecolorEdit.groupId)).stringValue;
            if (string.IsNullOrEmpty(groupId)) return;
            string headId = headElement.FindPropertyRelative(nameof(RecolorEdit.id)).stringValue;

            // 動かすと添字がずれるので、id で覚えてから 1 件ずつ寄せる
            var memberIds = new List<string>();
            foreach (int i in FindGroupIndices(groupId))
            {
                if (i == newIndex) continue;
                memberIds.Add(_edits.GetArrayElementAtIndex(i).FindPropertyRelative(nameof(RecolorEdit.id)).stringValue);
            }
            int placed = 0;
            foreach (string id in memberIds)
            {
                int current = FindIndexById(id);
                int head = FindIndexById(headId);
                if (current < 0 || head < 0) continue;
                // 先頭より前から抜くと先頭が 1 つ前へずれる
                int to = current < head ? head + placed : head + 1 + placed;
                if (current != to) _edits.MoveArrayElement(current, to);
                placed++;
            }
            list.index = FindIndexById(headId);
        }

        /// <summary>1 行: 有効トグル／名前（連結ならテクスチャ数）／見本色（新しい色）／モード／「Scene で選ぶ」</summary>
        private void DrawEditElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            // 連結の先頭でない編集は行を出さない（高さ 0）
            var hidden = GetEdit(index);
            if (hidden != null && !EditGroups.IsHead((ClickRecolor)target, hidden)) return;
            var element = _edits.GetArrayElementAtIndex(index);
            var enabledProp = element.FindPropertyRelative(nameof(RecolorEdit.enabled));
            var nameProp = element.FindPropertyRelative(nameof(RecolorEdit.name));
            var targetColorProp = element.FindPropertyRelative(nameof(RecolorEdit.targetColor));
            var hasTargetProp = element.FindPropertyRelative(nameof(RecolorEdit.hasTarget));
            var modeProp = element.FindPropertyRelative(nameof(RecolorEdit.mode));
            var idProp = element.FindPropertyRelative(nameof(RecolorEdit.id));

            const float gap = 4f;
            const float toggleWidth = 16f;
            const float swatchWidth = 32f;
            const float modeWidth = 32f;
            const float buttonWidth = 96f;
            float line = EditorGUIUtility.singleLineHeight;
            float y = rect.y + (rect.height - line) * 0.5f;

            var toggleRect = new Rect(rect.x, y, toggleWidth, line);
            var buttonRect = new Rect(rect.xMax - buttonWidth, y, buttonWidth, line);
            var modeRect = new Rect(buttonRect.x - gap - modeWidth, y, modeWidth, line);
            var swatchRect = new Rect(modeRect.x - gap - swatchWidth, y + 2f, swatchWidth, line - 4f);
            var nameRect = new Rect(toggleRect.xMax + gap, y, Mathf.Max(0f, swatchRect.x - gap - (toggleRect.xMax + gap)), line);

            // 「アバター全体」の連結は名前の後ろにテクスチャ数を出し、有効トグルと名前は連結の全編集に効かせる
            string groupId = element.FindPropertyRelative(nameof(RecolorEdit.groupId)).stringValue;
            List<int> groupIndices = string.IsNullOrEmpty(groupId) ? null : FindGroupIndices(groupId);
            if (groupIndices != null)
            {
                var countContent = new GUIContent(Locales.Tr("Inspector:Edit:GroupCount", groupIndices.Count));
                float countWidth = Mathf.Min(EditorStyles.miniLabel.CalcSize(countContent).x, nameRect.width * 0.5f);
                var countRect = new Rect(nameRect.xMax - countWidth, y, countWidth, line);
                nameRect.width = Mathf.Max(0f, nameRect.width - countWidth - gap);
                EditorGUI.LabelField(countRect, countContent, EditorStyles.miniLabel);
            }

            EditorGUI.BeginChangeCheck();
            bool newEnabled = EditorGUI.Toggle(toggleRect, enabledProp.boolValue);
            if (EditorGUI.EndChangeCheck())
            {
                if (groupIndices == null) enabledProp.boolValue = newEnabled;
                else
                {
                    foreach (int i in groupIndices)
                    {
                        _edits.GetArrayElementAtIndex(i).FindPropertyRelative(nameof(RecolorEdit.enabled)).boolValue = newEnabled;
                    }
                }
            }

            EditorGUI.BeginChangeCheck();
            string newName = EditorGUI.TextField(nameRect, nameProp.stringValue);
            if (EditorGUI.EndChangeCheck())
            {
                // 名前も有効トグルと同じく連結の全編集に揃える（連結は同じ設定を持つ約束）
                if (groupIndices == null) nameProp.stringValue = newName;
                else
                {
                    foreach (int i in groupIndices)
                    {
                        _edits.GetArrayElementAtIndex(i).FindPropertyRelative(nameof(RecolorEdit.name)).stringValue = newName;
                    }
                }
            }

            if (hasTargetProp.boolValue)
            {
                var c = targetColorProp.colorValue;
                c.a = 1f;
                EditorGUI.DrawRect(swatchRect, c);
            }
            else
            {
                EditorGUI.DrawRect(swatchRect, Color.gray);
                GUI.Label(swatchRect, new GUIContent(string.Empty, Locales.Tr("Inspector:Edit:NoTarget")));
            }

            string modeLabel = (SelectionMode)modeProp.intValue == SelectionMode.Color
                ? Locales.Tr("Inspector:Edit:Mode:Color")
                : Locales.Tr("Inspector:Edit:Mode:Island");
            EditorGUI.LabelField(modeRect, modeLabel);

            // 開始ボタンと同じ条件で無効化する（Project の Prefab アセット／Play 中）
            bool persistent = EditorUtility.IsPersistent(target);
            bool playing = EditorApplication.isPlayingOrWillChangePlaymode;
            using (new EditorGUI.DisabledScope(persistent || playing))
            {
                if (GUI.Button(buttonRect, Locales.Tr("Inspector:Edit:SelectInScene"))) _selectRequestId = idProp.stringValue;
            }
        }

        /// <summary>
        /// 標準の削除に加え、消した編集がパネルで選ばれていれば選択を外す。
        /// 「アバター全体」の連結は連結の全編集を消す
        /// </summary>
        private void RemoveEditElement(ReorderableList list)
        {
            var element = _edits.GetArrayElementAtIndex(list.index);
            string groupId = element.FindPropertyRelative(nameof(RecolorEdit.groupId)).stringValue;
            if (string.IsNullOrEmpty(groupId))
            {
                string id = element.FindPropertyRelative(nameof(RecolorEdit.id)).stringValue;
                ReorderableList.defaultBehaviours.DoRemoveButton(list);
                if (!string.IsNullOrEmpty(id) && ToolSession.CurrentEditId == id) ToolSession.CurrentEditId = null;
                return;
            }

            var indices = FindGroupIndices(groupId);
            bool removedCurrent = false;
            // 後ろから消す（前から消すと後ろの添字がずれる）
            for (int k = indices.Count - 1; k >= 0; k--)
            {
                var member = _edits.GetArrayElementAtIndex(indices[k]);
                if (member.FindPropertyRelative(nameof(RecolorEdit.id)).stringValue == ToolSession.CurrentEditId) removedCurrent = true;
                _edits.DeleteArrayElementAtIndex(indices[k]);
            }
            if (removedCurrent) ToolSession.CurrentEditId = null;
            list.index = Mathf.Clamp(indices[0], -1, _edits.arraySize - 1);
        }

        /// <summary>一覧の描画中に受けたボタン操作を、ApplyModifiedProperties の後で実行する</summary>
        private void HandleDeferredRequests(ClickRecolor component)
        {
            if (_selectRequestId != null)
            {
                string id = _selectRequestId;
                _selectRequestId = null;
                if (component.FindEdit(id) != null)
                {
                    RecolorSceneTool.Activate(component.gameObject);
                    // Scene での再クリックと同じく、別の編集に切り替えるときは色未設定の仮の編集を捨てる
                    if (ToolSession.CurrentEditId != id)
                    {
                        RecolorSceneTool.DiscardPendingEdit();
                        ToolSession.CurrentEditId = id;
                    }
                    SceneView.RepaintAll();
                }
                GUIUtility.ExitGUI();
            }

            if (_deleteAllRequested)
            {
                _deleteAllRequested = false;
                if (EditorUtility.DisplayDialog(
                        Locales.Tr("Inspector:Title"),
                        Locales.Tr("Inspector:Edit:DeleteAllConfirm"),
                        Locales.Tr("Inspector:Edit:DeleteAll"),
                        Locales.Tr("Inspector:Edit:Cancel")))
                {
                    // パネルで選ばれている編集がこのコンポーネントのものなら選択も外す（他のアバターの選択は残す）
                    bool currentIsHere = component.FindEdit(ToolSession.CurrentEditId) != null;
                    Undo.RecordObject(component, "Tocolo: すべての編集を削除");
                    component.edits.Clear();
                    EditorUtility.SetDirty(component);
                    if (currentIsHere) ToolSession.CurrentEditId = null;
                }
                // モーダルダイアログの後は IMGUI のレイアウトが崩れるので、このイベントの描画を打ち切る
                GUIUtility.ExitGUI();
            }
        }

        /// <summary>
        /// 種の Renderer のメッシュが Read/Write 無効な編集があれば、その Renderer 名を「, 」区切りで返す（重複は 1 回）。無ければ null。
        /// 島の再計算にメッシュの読み取りが要るので、無効だとその編集はプレビューに効かない
        /// </summary>
        private static string FindUnreadableRendererNames(ClickRecolor component)
        {
            List<string> names = null;
            foreach (var edit in component.edits)
            {
                if (edit == null || edit.seedRenderer == null) continue;
                var mesh = RendererMeshAccess.GetSharedMesh(edit.seedRenderer);
                if (mesh == null || mesh.isReadable) continue;
                if (names == null) names = new List<string>();
                if (!names.Contains(edit.seedRenderer.name)) names.Add(edit.seedRenderer.name);
            }
            return names != null ? string.Join(", ", names) : null;
        }
    }
}
