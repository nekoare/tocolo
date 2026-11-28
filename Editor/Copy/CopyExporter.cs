using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Nekoare.ClickRecolor.Editor.SceneTool;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Copy
{
    /// <summary>選んだ編集をコピーのファイル（CopyFile）にする</summary>
    internal static class CopyExporter
    {
        /// <summary>
        /// 書き出しの一覧に出す行。連結は 1 行（「アバター全体」は種を持つ主、島の連結は先頭）。
        /// 色を決めていない仮の編集と、種の Renderer が見つからない編集は出さない（移す先で探す手がかりが無い）
        /// </summary>
        internal static List<RecolorEdit> ListRows(ClickRecolor component)
        {
            var rows = new List<RecolorEdit>();
            if (component == null || component.edits == null) return rows;
            var seenGroups = new HashSet<string>();
            foreach (var edit in component.edits)
            {
                if (edit == null || !edit.IsKept) continue;
                if (EditGroups.IsGrouped(edit))
                {
                    if (seenGroups.Contains(edit.groupId)) continue;
                    var head = RowOfGroup(component, edit);
                    if (head == null) continue;
                    seenGroups.Add(edit.groupId);
                    rows.Add(head);
                    continue;
                }
                if (edit.seedRenderer == null) continue;
                rows.Add(edit);
            }
            return rows;
        }

        private static RecolorEdit RowOfGroup(ClickRecolor component, RecolorEdit edit)
        {
            foreach (var member in EditGroups.Members(component, edit))
            {
                if (member != null && member.seedRenderer != null) return member;
            }
            return null;
        }

        /// <summary>アバターの Renderer のマテリアルが使っているテクスチャ（メインに限らず全部の欄）</summary>
        internal static HashSet<Texture> CollectAvatarTextures(ClickRecolor component)
        {
            var result = new HashSet<Texture>();
            if (component == null) return result;
            foreach (var renderer in component.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    foreach (int id in material.GetTexturePropertyNameIDs())
                    {
                        var texture = material.GetTexture(id);
                        if (texture != null) result.Add(texture);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 一覧の行（連結ならメンバー全員）の［画像を入れる］の画像のうち、avatarTextures に入っているもの。
        /// 商品のテクスチャをシールに使うと、プロファイルを人に渡したときにそのテクスチャを配ってしまうので、書き出す前に知らせるため
        /// </summary>
        internal static List<Texture2D> AvatarTexturesIn(ClickRecolor component, RecolorEdit row, HashSet<Texture> avatarTextures)
        {
            var result = new List<Texture2D>();
            var members = EditGroups.IsGrouped(row) && !EditGroups.IsWholeAvatarGroup(row) ? EditGroups.Members(component, row) : new List<RecolorEdit> { row };
            foreach (var member in members)
            {
                var texture = member?.decalTexture;
                if (texture != null && avatarTextures.Contains(texture) && !result.Contains(texture)) result.Add(texture);
            }
            return result;
        }

        /// <summary>missingImages を渡すと、画像を入れられなかった編集の名前を足す（編集そのものは書き出す）</summary>
        internal static CopyFile Build(ClickRecolor component, IEnumerable<RecolorEdit> rows, List<string> missingImages = null)
        {
            var file = new CopyFile { toolVersion = ToolVersion() };
            var root = component.transform;
            var imageKeys = new Dictionary<Texture2D, string>();
            foreach (var row in rows)
            {
                if (row == null || row.seedRenderer == null) continue;
                if (EditGroups.IsWholeAvatarGroup(row))
                {
                    var entry = MakeEntry(root, row, row, file, imageKeys, missingImages);
                    entry.wasGroupHead = true;
                    file.edits.Add(entry);
                }
                else if (EditGroups.IsGrouped(row))
                {
                    foreach (var member in EditGroups.Members(component, row))
                    {
                        if (member == null || member.seedRenderer == null) continue;
                        var entry = MakeEntry(root, member, row, file, imageKeys, missingImages);
                        entry.islandGroupKey = row.groupId;
                        file.edits.Add(entry);
                    }
                }
                else
                {
                    file.edits.Add(MakeEntry(root, row, row, file, imageKeys, missingImages));
                }
            }
            return file;
        }

        private static CopyEntry MakeEntry(Transform root, RecolorEdit edit, RecolorEdit basisEdit, CopyFile file, Dictionary<Texture2D, string> imageKeys,
            List<string> missingImages)
        {
            var entry = new CopyEntry
            {
                editJson = CopyFileIO.EditToJson(edit),
                seed = CopyAnchor.Describe(root, edit.seedRenderer, edit.seedSubmesh, edit.seedTriangle, edit.seedUv, edit.sourceTexture),
                basis = CopyAnchor.Describe(root, basisEdit.seedRenderer, basisEdit.seedSubmesh, basisEdit.seedTriangle, basisEdit.seedUv, basisEdit.sourceTexture),
            };
            if (edit.extraSeeds != null)
            {
                foreach (var seed in edit.extraSeeds)
                {
                    if (seed == null || seed.renderer == null) continue;
                    entry.extraSeeds.Add(CopyAnchor.Describe(root, seed.renderer, seed.submesh, seed.triangle, seed.uv, edit.sourceTexture));
                }
            }

            var m = HairSpace.RootToBasis(root, HairSpace.BasisOf(basisEdit.seedRenderer));
            entry.gradientBoxPosition = edit.gradientBoxPosition;
            entry.gradientBoxRotation = edit.gradientBoxRotation;
            entry.gradientBoxSize = edit.gradientBoxSize;
            HairSpace.ToBasis(m, ref entry.gradientBoxPosition, ref entry.gradientBoxRotation, ref entry.gradientBoxSize);
            entry.boxPosition = edit.boxPosition;
            entry.boxRotation = edit.boxRotation;
            entry.boxSize = edit.boxSize;
            HairSpace.ToBasis(m, ref entry.boxPosition, ref entry.boxRotation, ref entry.boxSize);
            entry.decalBoxPosition = edit.decalBoxPosition;
            entry.decalBoxRotation = edit.decalBoxRotation;
            entry.decalBoxSize = edit.decalBoxSize;
            HairSpace.ToBasis(m, ref entry.decalBoxPosition, ref entry.decalBoxRotation, ref entry.decalBoxSize);
            entry.gradientTipTop = HairSpace.PointToBasis(m, edit.gradientTipTop);
            entry.gradientTipLength = edit.gradientTipLength * HairSpace.ScaleOf(m);

            if (edit.decalTexture != null)
            {
                if (!imageKeys.TryGetValue(edit.decalTexture, out var key))
                {
                    var image = MakeImage(edit.decalTexture);
                    key = image?.key;
                    imageKeys[edit.decalTexture] = key;
                    if (image != null && !file.images.Exists(i => i.key == image.key)) file.images.Add(image);
                }
                entry.imageKey = key;
                if (key == null) missingImages?.Add(edit.name);
            }
            return entry;
        }

        /// <summary>
        /// 画像の元ファイルのバイト列をそのまま入れる。読み込み先で作り直したテクスチャを圧縮し直すと画質が落ちるため、
        /// 画像ファイル（png・jpg など）でない資産（.asset で作ったテクスチャ）のときだけ PNG に描き出す
        /// </summary>
        private static CopyImage MakeImage(Texture2D texture)
        {
            string path = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(path)) return null;
            byte[] bytes;
            string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            // Packages/… は Library/PackageCache などの実体を指す仮のパスなので、Path.GetFullPath で実体に直してから読む
            // （直さないと読めずに描き出し直しになり、色や透明度の扱いが元と変わる）
            string fullPath = Path.GetFullPath(path);
            if (importer != null && File.Exists(fullPath))
            {
                bytes = File.ReadAllBytes(fullPath);
            }
            else
            {
                bytes = EncodeToPng(texture);
                extension = "png";
            }
            if (bytes == null || bytes.Length == 0) return null;

            var image = new CopyImage
            {
                key = Hash(bytes),
                name = texture.name,
                extension = extension,
                base64 = Convert.ToBase64String(bytes),
            };
            if (importer != null)
            {
                image.sRGB = importer.sRGBTexture;
                image.alphaIsTransparency = importer.alphaIsTransparency;
                image.wrapMode = (int)importer.wrapMode;
                image.filterMode = (int)importer.filterMode;
                image.maxTextureSize = importer.maxTextureSize;
                image.isReadable = importer.isReadable;
                image.mipmapEnabled = importer.mipmapEnabled;
                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                image.textureSettingsJson = JsonUtility.ToJson(settings);
                image.platformSettingsJson.Add(JsonUtility.ToJson(importer.GetDefaultPlatformTextureSettings()));
                foreach (var platform in PlatformNames)
                {
                    var platformSettings = importer.GetPlatformTextureSettings(platform);
                    if (platformSettings != null && platformSettings.overridden) image.platformSettingsJson.Add(JsonUtility.ToJson(platformSettings));
                }
            }
            return image;
        }

        /// <summary>上書きを写すプラットフォーム（VRChat で使うもの＋他にありうるもの）</summary>
        private static readonly string[] PlatformNames = { "Standalone", "Android", "iPhone", "WebGL" };

        private static byte[] EncodeToPng(Texture2D texture)
        {
            var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            var readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                readable.Apply();
                return readable.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        internal static string Hash(byte[] bytes)
        {
            using (var sha1 = SHA1.Create())
            {
                var sb = new StringBuilder();
                foreach (var b in sha1.ComputeHash(bytes)) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string ToolVersion()
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(CopyExporter).Assembly);
            return info != null ? info.version : "";
        }

        /// <summary>assetPath（Assets/… の .json）に書き、取り込む。改行は LF（別 OS へ運んでも差分が出ないように）</summary>
        internal static void WriteToPath(CopyFile file, string assetPath)
        {
            File.WriteAllText(assetPath, CopyFileIO.Write(file).Replace("\r\n", "\n"), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(assetPath);
        }
    }
}
