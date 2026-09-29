using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Picking
{
    /// <summary>
    /// メッシュの Read/Write が無効で Scene から選べないとき、本人の同意を取ってインポート設定を有効にする（ユーザー判断 2026-09-29: 案 B）。
    /// 自動では変えない（アセットの書き換え・同じ FBX を使う他のアバターへの影響・アップロード後のメモリ増があるため）
    /// </summary>
    internal static class MeshReadWriteFixer
    {
        /// <summary>renderer のメッシュが Read/Write 無効か（メッシュが無ければ false）</summary>
        internal static bool IsUnreadable(Renderer renderer)
        {
            var mesh = RendererMeshAccess.GetSharedMesh(renderer);
            return mesh != null && !mesh.isReadable;
        }

        /// <summary>
        /// Read/Write を有効にできるメッシュなら、そのアセットのパス（FBX など）を返す。できなければ null
        /// （プロジェクトのアセットでない・書き換えられない場所（Registry / Git のパッケージ等）・モデルでも .asset でもない）
        /// </summary>
        internal static string GetFixablePath(Mesh mesh)
        {
            if (mesh == null || mesh.isReadable) return null;
            string path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path) || !IsWritable(path)) return null;
            if (AssetImporter.GetAtPath(path) is ModelImporter) return path;
            if (path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)) return path;
            return null;
        }

        /// <summary>
        /// meshes の Read/Write を有効にする。直せるもの（GetFixablePath）が無ければ何もしない。
        /// confirm なら対象の一覧を出して確認し、キャンセルなら何もしない。変えたら true
        /// </summary>
        internal static bool Fix(IEnumerable<Mesh> meshes, bool confirm = true)
        {
            var paths = new List<string>();
            var assetMeshes = new List<Mesh>();
            foreach (var mesh in meshes)
            {
                string path = GetFixablePath(mesh);
                if (path == null || paths.Contains(path)) continue;
                paths.Add(path);
                if (!(AssetImporter.GetAtPath(path) is ModelImporter)) assetMeshes.Add(mesh);
            }
            if (paths.Count == 0) return false;
            if (confirm && !EditorUtility.DisplayDialog(
                    Locales.Tr("Dialog:ReadWriteTitle"),
                    // 改行は .po に書かずここで組み立てる
                    Locales.Tr("Dialog:ReadWriteConfirm") + "\n\n" + string.Join("\n", paths) + "\n\n" + Locales.Tr("Dialog:ReadWriteNote"),
                    Locales.Tr("Scene:Panel:UnreadableFix"),
                    Locales.Tr("Inspector:Edit:Cancel")))
            {
                return false;
            }

            foreach (var path in paths)
            {
                if (!(AssetImporter.GetAtPath(path) is ModelImporter importer)) continue;
                importer.isReadable = true;
                importer.SaveAndReimport();
            }
            // .asset のメッシュは m_IsReadable を直接書き換えて保存する
            foreach (var mesh in assetMeshes)
            {
                var serialized = new SerializedObject(mesh);
                var property = serialized.FindProperty("m_IsReadable");
                if (property == null) continue;
                property.boolValue = true;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(mesh);
                AssetDatabase.SaveAssetIfDirty(mesh);
            }
            return true;
        }

        /// <summary>
        /// Assets 配下は可。Packages 配下は Embedded / Local パッケージのみ可（Registry / Git / BuiltIn は書き換えられない）。
        /// 有料版の ExportPathUtility.IsWritableAssetLocation と同じ規則
        /// </summary>
        private static bool IsWritable(string assetPath)
        {
            string normalized = assetPath.Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) return true;
            if (!normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) return false;
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(normalized);
            return info != null
                && (info.source == UnityEditor.PackageManager.PackageSource.Embedded
                    || info.source == UnityEditor.PackageManager.PackageSource.Local);
        }
    }
}
