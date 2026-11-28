using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Copy
{
    /// <summary>
    /// Scene 編集のパネルに設定ファイル（.json）をドロップして読み込む。
    /// Project ウィンドウからでもエクスプローラーからでも受けるため、Unity の型ではなくパスから読む
    /// </summary>
    internal static class CopyDropHandler
    {
        private static readonly Dictionary<string, (DateTime time, bool ok)> s_checked = new Dictionary<string, (DateTime, bool)>();
        /// <summary>
        /// 案内を出すかは Layout のときだけ決める。ドラッグの終わりに Layout と続くイベントのあいだで DragAndDrop.paths が空になると、
        /// イベントごとに判定していてはコントロールの数が食い違い、IMGUI のレイアウトのエラーになる
        /// </summary>
        private static bool s_showHint;

        /// <summary>パネルの OnGUI から毎回呼ぶ。設定ファイルを運んでいる間は案内を出し、離したら読み込む</summary>
        internal static void Handle(GameObject root)
        {
            var e = Event.current;
            if (e.type == EventType.Layout) s_showHint = DraggedCopyFile() != null;
            if (s_showHint) EditorGUILayout.HelpBox(Locales.Tr("Scene:Panel:CopyDropHint"), MessageType.Info);
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform) return;
            string path = DraggedCopyFile();
            if (path == null) return;
            DragAndDrop.visualMode = root != null ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (e.type == EventType.DragPerform && root != null)
            {
                DragAndDrop.AcceptDrag();
                // ダイアログは描画の外で出す（IMGUI の途中でモーダルを出すとレイアウトが崩れる）
                EditorApplication.delayCall += () => Load(root, path);
            }
            e.Use();
        }

        private static string DraggedCopyFile()
        {
            var paths = DragAndDrop.paths;
            if (paths == null) return null;
            foreach (var p in paths)
            {
                if (IsCopyFile(p)) return p;
            }
            return null;
        }

        /// <summary>
        /// 運んでいる間は毎フレーム呼ばれるので、読んだ結果を更新日時ごとに覚えておく。
        /// 全体は読まない: 画像入りのファイルは数十 MB になりうるので、ドラッグを始めた瞬間に固まる。先頭の format だけ見る
        /// </summary>
        private static bool IsCopyFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            var time = File.GetLastWriteTimeUtc(path);
            if (s_checked.TryGetValue(path, out var cached) && cached.time == time) return cached.ok;
            bool ok;
            try
            {
                var head = new char[512];
                int n;
                using (var reader = new StreamReader(path, Encoding.UTF8)) n = reader.Read(head, 0, head.Length);
                ok = CopyFileIO.LooksLikeCopyFile(new string(head, 0, n));
            }
            catch (IOException)
            {
                ok = false;
            }
            catch (UnauthorizedAccessException)
            {
                ok = false;
            }
            s_checked[path] = (time, ok);
            return ok;
        }

        private static void Load(GameObject root, string path)
        {
            if (root == null) return;
            string title = Locales.Tr("Copy:Import:Title");
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                text = null;
            }
            if (text == null || !CopyFileIO.TryRead(text, out var file, out _))
            {
                EditorUtility.DisplayDialog(title, Locales.Tr("Copy:Import:Unreadable"), Locales.Tr("Copy:Import:Ok"));
                return;
            }

            var mode = ImportMode.Add;
            var component = root.GetComponent<ClickRecolor>();
            // 色を決めていない仮の編集だけなら聞かない（上書きしても消えて困るものが無い）
            if (component != null && component.edits != null && component.edits.Exists(x => x != null && x.IsKept))
            {
                int choice = EditorUtility.DisplayDialogComplex(title, Locales.Tr("Copy:Import:Ask", root.name),
                    Locales.Tr("Copy:Import:Overwrite"), Locales.Tr("Copy:Import:Cancel"), Locales.Tr("Copy:Import:Add"));
                if (choice == 1) return;
                mode = choice == 0 ? ImportMode.ReplaceSameTexture : ImportMode.Add;
            }

            var result = CopyImporter.Apply(root, file, mode);
            if (result.noComponent)
            {
                EditorUtility.DisplayDialog(title, Locales.Tr("Copy:Import:NoComponent"), Locales.Tr("Copy:Import:Ok"));
                return;
            }
            SceneView.RepaintAll();
            EditorUtility.DisplayDialog(title, Report(result), Locales.Tr("Copy:Import:Ok"));
        }

        internal static string Report(ImportResult result)
        {
            var sb = new StringBuilder(Locales.Tr("Copy:Import:Result", result.edits.Count));
            if (result.failed.Count > 0)
            {
                sb.Append("\n\n").Append(Locales.Tr("Copy:Import:Failed"));
                foreach (var (name, reason) in result.failed)
                {
                    sb.Append('\n').Append(Locales.Tr("Copy:Import:FailedLine", name, Locales.Tr("Copy:Import:Reason:" + reason)));
                }
            }
            AppendNames(sb, "Copy:Import:Partial", result.partial);
            AppendNames(sb, "Copy:Import:Regrouped", result.regrouped);
            AppendNames(sb, "Copy:Import:MissingImage", result.missingImages);
            return sb.ToString();
        }

        private static void AppendNames(StringBuilder sb, string key, List<string> names)
        {
            if (names.Count == 0) return;
            sb.Append("\n\n").Append(Locales.Tr(key));
            foreach (var name in names) sb.Append('\n').Append(Locales.Tr("Copy:Import:Line", name));
        }
    }
}
