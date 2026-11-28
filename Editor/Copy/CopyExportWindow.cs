using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Localization;
using Nekoare.ClickRecolor.Editor.SceneTool;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Copy
{
    /// <summary>書き出す編集をチェックで選ぶウィンドウ（既定は全部 ON）</summary>
    internal sealed class CopyExportWindow : EditorWindow
    {
        private ClickRecolor _component;
        private readonly List<RecolorEdit> _rows = new List<RecolorEdit>();
        private readonly HashSet<string> _unchecked = new HashSet<string>();
        private Vector2 _scroll;

        internal static void Open(ClickRecolor component)
        {
            if (component == null) return;
            var window = GetWindow<CopyExportWindow>(true, Locales.Tr("Copy:Window:Title"), true);
            window._component = component;
            window._unchecked.Clear();
            window.minSize = new Vector2(360, 260);
            window.Show();
        }

        private void OnGUI()
        {
            if (_component == null)
            {
                Close();
                return;
            }
            // 編集は Undo で List ごと入れ替わるので、描画のたびに引き直す（チェックは id で覚える）
            _rows.Clear();
            _rows.AddRange(CopyExporter.ListRows(_component));

            EditorGUILayout.LabelField(Locales.Tr("Copy:Window:Lead"), EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(4);
            if (_rows.Count == 0)
            {
                EditorGUILayout.HelpBox(Locales.Tr("Copy:Window:NoRows"), MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(Locales.Tr("Copy:Window:SelectAll"), EditorStyles.miniButton, GUILayout.ExpandWidth(false))) _unchecked.Clear();
                if (GUILayout.Button(Locales.Tr("Copy:Window:SelectNone"), EditorStyles.miniButton, GUILayout.ExpandWidth(false)))
                {
                    foreach (var row in _rows) _unchecked.Add(row.id);
                }
            }
            var avatarTextures = CopyExporter.CollectAvatarTextures(_component);
            var flagged = new List<string>();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var row in _rows)
            {
                bool on = !_unchecked.Contains(row.id);
                bool usesAvatarTexture = CopyExporter.AvatarTexturesIn(_component, row, avatarTextures).Count > 0;
                bool next;
                using (new EditorGUILayout.HorizontalScope())
                {
                    next = EditorGUILayout.ToggleLeft(Label(row), on);
                    if (usesAvatarTexture) GUILayout.Label(AvatarTextureTag(), EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                }
                if (next && usesAvatarTexture) flagged.Add(Label(row));
                if (next == on) continue;
                if (next) _unchecked.Remove(row.id);
                else _unchecked.Add(row.id);
            }
            EditorGUILayout.EndScrollView();

            if (flagged.Count > 0)
            {
                string message = Locales.Tr("Copy:Window:AvatarTextureWarning");
                foreach (var name in flagged) message += "\n" + Locales.Tr("Copy:Import:Line", name);
                EditorGUILayout.HelpBox(message, MessageType.Warning);
            }
            EditorGUILayout.LabelField(Locales.Tr("Copy:Window:ImageNote"), EditorStyles.wordWrappedMiniLabel);
            var selected = _rows.FindAll(r => !_unchecked.Contains(r.id));
            using (new EditorGUI.DisabledScope(selected.Count == 0))
            {
                // 保存先のダイアログは描画の外で開く（描画の途中でモーダルを開いてキャンセルすると、レイアウトのエラーになる）
                if (GUILayout.Button(Locales.Tr("Copy:Window:Export"))) EditorApplication.delayCall += () => Export(selected);
            }
        }

        // 毎回作る（使い回すと、言語を切り替えても文言が古いまま残る）
        private static GUIContent AvatarTextureTag() =>
            new GUIContent(Locales.Tr("Copy:Window:AvatarTexture"), EditorGUIUtility.IconContent("console.warnicon.sml").image,
                Locales.Tr("Copy:Window:AvatarTextureTooltip"));

        private string Label(RecolorEdit row)
        {
            string name = string.IsNullOrEmpty(row.name) ? "(no name)" : row.name;
            if (EditGroups.IsWholeAvatarGroup(row)) return Locales.Tr("Copy:Window:Group", name);
            if (EditGroups.IsGrouped(row)) return Locales.Tr("Copy:Window:IslandGroup", name, EditGroups.Count(_component, row));
            return name;
        }

        private void Export(List<RecolorEdit> selected)
        {
            if (this == null || _component == null) return;
            string path = EditorUtility.SaveFilePanelInProject(
                Locales.Tr("Copy:Window:SaveTitle"), _component.gameObject.name + ".tocolo", "json", "");
            if (string.IsNullOrEmpty(path)) return;
            var missingImages = new List<string>();
            var file = CopyExporter.Build(_component, selected, missingImages);
            CopyExporter.WriteToPath(file, path);
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Object>(path));
            string message = Locales.Tr("Copy:Window:Done", file.edits.Count, path);
            if (missingImages.Count > 0)
            {
                message += "\n\n" + Locales.Tr("Copy:Window:MissingImage");
                foreach (var name in missingImages) message += "\n" + Locales.Tr("Copy:Import:Line", name);
            }
            EditorUtility.DisplayDialog(Locales.Tr("Copy:Window:Title"), message, Locales.Tr("Copy:Import:Ok"));
            Close();
        }
    }
}
