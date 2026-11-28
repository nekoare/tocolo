using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Copy
{
    /// <summary>
    /// コピーのファイルに入っていた［画像を入れる］の画像を、移す先のプロジェクトに画像ファイルとして作る。
    /// 名前に中身のハッシュを入れ、同じ画像は作り直さずに使い回す（同じファイルを何度読み込んでも増えない）
    /// </summary>
    internal static class CopiedImageStore
    {
        /// <summary>置き場所。テストでは差し替える</summary>
        internal static string Folder = "Assets/Tocolo/CopiedImages";

        /// <summary>
        /// 作ってよい拡張子。ファイルは人から渡されることがあるので、拡張子を信じると .cs などを書かされて
        /// Unity にコンパイル・実行される → 画像として取り込める形式だけに絞る
        /// </summary>
        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "png", "jpg", "jpeg", "tga", "psd", "tif", "tiff", "bmp", "exr", "hdr", "gif",
        };

        internal static Texture2D GetOrCreate(CopyImage image)
        {
            if (image == null || string.IsNullOrEmpty(image.base64) || !IsHex(image.key)) return null;
            string extension = string.IsNullOrEmpty(image.extension) ? "png" : image.extension;
            if (!ImageExtensions.Contains(extension)) return null;

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(image.base64);
            }
            catch (FormatException)
            {
                return null;
            }
            // key は保存先の名前に使うので、中身から作り直して一致するものだけ受ける（パスを書き換える値を入れられない）
            if (!string.Equals(CopyExporter.Hash(bytes), image.key, StringComparison.OrdinalIgnoreCase)) return null;

            string path = $"{Folder}/{SafeName(image.name)}_{image.key.Substring(0, 8)}.{extension.ToLowerInvariant()}";
            var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (existing != null) return existing;

            EnsureFolder(Folder);
            File.WriteAllBytes(path, bytes);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
            {
                AssetDatabase.DeleteAsset(path);
                return null;
            }
            importer.sRGBTexture = image.sRGB;
            importer.alphaIsTransparency = image.alphaIsTransparency;
            importer.wrapMode = (TextureWrapMode)image.wrapMode;
            importer.filterMode = (FilterMode)image.filterMode;
            importer.maxTextureSize = image.maxTextureSize > 0 ? image.maxTextureSize : 2048;
            importer.isReadable = image.isReadable;
            importer.mipmapEnabled = image.mipmapEnabled;
            ApplyImporterSettings(importer, image);
            importer.SaveAndReimport();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null) AssetDatabase.DeleteAsset(path);
            return texture;
        }

        /// <summary>元の読み込み設定（種類・ミップマップなど、圧縮・プラットフォームごとの上書き）を重ねる。読めない JSON は飛ばす</summary>
        private static void ApplyImporterSettings(TextureImporter importer, CopyImage image)
        {
            if (!string.IsNullOrEmpty(image.textureSettingsJson))
            {
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                try
                {
                    JsonUtility.FromJsonOverwrite(image.textureSettingsJson, settings);
                    importer.SetTextureSettings(settings);
                }
                catch (ArgumentException)
                {
                }
            }
            if (image.platformSettingsJson == null) return;
            foreach (var json in image.platformSettingsJson)
            {
                if (string.IsNullOrEmpty(json)) continue;
                var platform = new TextureImporterPlatformSettings();
                try
                {
                    JsonUtility.FromJsonOverwrite(json, platform);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                if (string.IsNullOrEmpty(platform.name)) continue;
                importer.SetPlatformTextureSettings(platform);
            }
        }

        private static bool IsHex(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length < 8) return false;
            foreach (char c in key)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        private static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "image";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Replace("..", "_");
            return name.Length > 64 ? name.Substring(0, 64) : name;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}
