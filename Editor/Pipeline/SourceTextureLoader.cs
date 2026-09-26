// 流用元: dev.nekoare.tex-col-adjuster/Editor/Pipeline/SourceTextureLoader.cs
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Pipeline
{
    /// <summary>
    /// SourceTextureLoader から取得した元テクスチャのハンドル。使い終わったら Dispose（参照カウント減）
    /// </summary>
    internal sealed class SourceTexture : IDisposable
    {
        /// <summary>元の資産（パス・インポーター設定の基準）</summary>
        public Texture2D Asset { get; }
        /// <summary>パイプラインに渡すテクスチャ。直読みできなければ Asset そのもの</summary>
        public Texture2D Texture { get; private set; }
        /// <summary>ファイルから直接読めたか（false = インポート済みの資産をそのまま使用）</summary>
        public bool IsDirectRead { get; }
        /// <summary>Acquire 時に指定した縮小上限（0 = ファイル解像度）</summary>
        public int MaxSize { get; }

        internal string CacheKey { get; }
        /// <summary>このハンドルが参照を持つキャッシュの項目（キャッシュ対象外なら null）。Release はこれを減らす</summary>
        internal SourceTextureLoader.Entry Entry { get; }
        private bool _disposed;

        internal SourceTexture(
            Texture2D asset, Texture2D texture, bool isDirectRead, string cacheKey, int maxSize,
            SourceTextureLoader.Entry entry = null)
        {
            Asset = asset;
            Texture = texture;
            IsDirectRead = isDirectRead;
            CacheKey = cacheKey;
            MaxSize = maxSize;
            Entry = entry;
        }

        public bool IsValid => Texture != null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SourceTextureLoader.Release(this);
            Texture = null;
        }
    }

    /// <summary>
    /// 元テクスチャを「インポート時の圧縮を経ていない」状態で取得する。
    /// PNG / JPG はファイルのバイト列を直接デコードする（インポーターを書き換えないので再インポートは起きない）。
    /// それ以外の形式はインポート済みの資産をそのまま返す（インポーターを一時変更する方式は使わない）。
    /// 同じ資産・同じ maxSize の取得は参照カウント付きで共有される
    /// </summary>
    internal static class SourceTextureLoader
    {
        /// <summary>キャッシュの項目。ハンドル（SourceTexture）が参照を持つ</summary>
        internal sealed class Entry
        {
            public Texture2D texture;
            public bool ownsTexture;
            public bool isDirectRead;
            public int refCount;
        }

        private static readonly Dictionary<string, Entry> s_cache = new Dictionary<string, Entry>();

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            // 参照カウントが 0 に戻らないまま（例外・呼び出し漏れ）リロードやシーン切替を迎えても、デコード結果を残さない
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
            EditorSceneManager.sceneClosing += (scene, removingScene) => ReleaseAll();
        }

        /// <summary>
        /// 元テクスチャを取得する。
        /// </summary>
        /// <param name="asset">元テクスチャ（資産）</param>
        /// <param name="maxSize">0 = ファイル解像度のまま。&gt;0 = 直読みした画像の長辺がこの値を超えたら縮小する</param>
        internal static SourceTexture Acquire(Texture2D asset, int maxSize = 0)
        {
            if (asset == null) return null;

            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath))
            {
                // 実行時に作られたテクスチャ: そのまま使う（キャッシュ対象外）
                return new SourceTexture(asset, asset, false, null, maxSize);
            }

            string key = $"{assetPath}|{asset.imageContentsHash}|{maxSize}";

            if (s_cache.TryGetValue(key, out var entry) && entry.texture != null)
            {
                entry.refCount++;
                return new SourceTexture(asset, entry.texture, entry.isDirectRead, key, maxSize, entry);
            }

            entry = Load(asset, assetPath, maxSize);
            entry.refCount = 1;
            s_cache[key] = entry;
            return new SourceTexture(asset, entry.texture, entry.isDirectRead, key, maxSize, entry);
        }

        /// <summary>
        /// ハンドルが持つ項目の参照を 1 つ減らし、0 になったら破棄する。
        /// 鍵ではなくハンドルが取得時に持った項目そのものを減らすので、ReleaseAll の後に同じ鍵で作り直された
        /// 新しい項目を、古いハンドルの Dispose で減らしてしまうことはない
        /// </summary>
        internal static void Release(SourceTexture handle)
        {
            var entry = handle?.Entry;
            if (entry == null || entry.refCount <= 0) return; // キャッシュ対象外・ReleaseAll で破棄済み

            entry.refCount--;
            if (entry.refCount > 0) return;

            // キャッシュに載っているのがこの項目のときだけ外す（同じ鍵の別の項目は残す）
            if (handle.CacheKey != null
                && s_cache.TryGetValue(handle.CacheKey, out var current)
                && ReferenceEquals(current, entry))
            {
                s_cache.Remove(handle.CacheKey);
            }
            DestroyEntry(entry);
        }

        /// <summary>
        /// キャッシュをすべて解放する（参照カウントは無視）。
        /// 以後、取得済みハンドルの Texture は使えない（Dispose は安全に何もしない）
        /// </summary>
        internal static void ReleaseAll()
        {
            foreach (var entry in s_cache.Values)
            {
                DestroyEntry(entry);
                // 古いハンドルの Dispose で減らさないよう、破棄済みの印にする
                entry.refCount = 0;
            }
            s_cache.Clear();
        }

        /// <summary>キャッシュ内の件数（テスト用）</summary>
        internal static int CachedEntryCount => s_cache.Count;

        internal static bool IsDirectReadableExtension(string assetPath)
        {
            string ext = Path.GetExtension(assetPath)?.ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg";
        }

        private static void DestroyEntry(Entry entry)
        {
            if (entry.ownsTexture && entry.texture != null)
            {
                TextureColorSpaceUtility.UnregisterRuntimeTexture(entry.texture);
                UnityEngine.Object.DestroyImmediate(entry.texture);
            }
            entry.texture = null;
        }

        private static Entry Load(Texture2D asset, string assetPath, int maxSize)
        {
            if (IsDirectReadableExtension(assetPath))
            {
                bool isSrgb = TextureColorSpaceUtility.IsTextureSRGB(asset);
                var direct = TryLoadFromFile(asset, assetPath, maxSize, isSrgb);
                if (direct != null)
                {
                    return new Entry { texture = direct, ownsTexture = true, isDirectRead = true };
                }
            }

            // インポート済みの資産をそのまま使う（GPU の Blit は圧縮テクスチャを直接扱える）
            return new Entry { texture = asset, ownsTexture = false, isDirectRead = false };
        }

        private static Texture2D TryLoadFromFile(Texture2D asset, string assetPath, int maxSize, bool isSrgb)
        {
            // エディタのカレントディレクトリはプロジェクトのルートなので、資産パスをそのまま絶対パスにできる
            string fullPath = Path.GetFullPath(assetPath);
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(fullPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tocolo] 元画像 '{assetPath}' を読めませんでした（{e.Message}）。インポート済みのテクスチャを使います");
                return null;
            }

            // 縮小するときはミップを作っておくと Blit の縮小がエイリアシングしない
            bool needMips = maxSize > 0;
            var loaded = new Texture2D(2, 2, TextureFormat.RGBA32, needMips, linear: !isSrgb)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = asset.name + " (source)",
            };

            if (!loaded.LoadImage(bytes, false))
            {
                UnityEngine.Object.DestroyImmediate(loaded);
                Debug.LogWarning($"[Tocolo] 元画像 '{assetPath}' をデコードできませんでした。インポート済みのテクスチャを使います");
                return null;
            }

            TextureColorSpaceUtility.RegisterRuntimeTexture(loaded, isSrgb);

            if (maxSize > 0 && Mathf.Max(loaded.width, loaded.height) > maxSize)
            {
                var scaled = Downscale(loaded, maxSize, isSrgb);
                TextureColorSpaceUtility.UnregisterRuntimeTexture(loaded);
                UnityEngine.Object.DestroyImmediate(loaded);
                loaded = scaled;
            }

            return loaded;
        }

        private static Texture2D Downscale(Texture2D source, int maxSize, bool isSrgb)
        {
            float scale = (float)maxSize / Mathf.Max(source.width, source.height);
            int w = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int h = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));

            // 元と同じ色空間の RT に縮めてから読み戻す（sRGB ↔ 線形の往復で値を変えない）
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32,
                isSrgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var result = TextureColorSpaceUtility.CreateRuntimeTexture(w, h, TextureFormat.RGBA32, false, isSrgb);
                result.hideFlags = HideFlags.HideAndDontSave;
                result.name = source.name;
                result.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                result.Apply();
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
