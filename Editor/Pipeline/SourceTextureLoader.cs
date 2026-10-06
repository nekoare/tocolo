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
    /// 同じ資産・同じ maxSize の取得は参照カウント付きで共有される。
    /// 参照が 0 になったデコード結果はすぐ捨てず、合計 IdleBudgetBytes まで新しい順に取っておく
    /// （クリックやプレビューの作り直しのたびに同じ画像をデコードし直さない。4096 の PNG で 1 回 150〜230ms かかる）。
    /// 縮小版は、キャッシュに「元のミップの続き」の版（Entry.isChain）があればそこから縮め、ファイルをデコードし直さない
    /// （縦横ちょうど 1/2^k のときだけ。デコードして縮めるのと同じ値になる）
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
            /// <summary>最後に参照が 0 になった順番（使っていない項目は小さい＝古いものから捨てる）</summary>
            public long releasedOrder;
            /// <summary>
            /// ミップ付きで、各段がファイルを原寸でデコードしたときのミップのどこかの段からの続きと同じか
            /// （原寸のデコード、またはそこから縦横ちょうど 1/2^k に縮めてミップを作り直した版）。
            /// ここから縦横ちょうど 1/2^k に縮めた版は、原寸のデコードから縮めたのと同じ値になる
            /// （Blit は縮める倍率ちょうどのミップの段を引くため）
            /// </summary>
            public bool isChain;
            /// <summary>isChain のとき、原寸（ファイル）の幅と高さ</summary>
            public int fileWidth;
            public int fileHeight;
            /// <summary>デコードしたときの色空間（sRGB なら true）</summary>
            public bool isSrgb;
        }

        /// <summary>
        /// 使っていない（参照 0）デコード結果を取っておく量の上限。CPU と GPU の両方の写しを数える
        /// （2048 のミップ付きで 1 枚約 45MB なので 5 枚ほど）
        /// </summary>
        internal const long IdleBudgetBytes = 256L * 1024 * 1024;

        private static readonly Dictionary<string, Entry> s_cache = new Dictionary<string, Entry>();
        private static long s_releaseCounter;

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

            string prefix = $"{assetPath}|{asset.imageContentsHash}|";
            string key = prefix + maxSize;
            Func<Entry> load = () => Load(asset, assetPath, maxSize);
            if (maxSize <= 0 || IsLive(key) || !IsDirectReadableExtension(assetPath))
            {
                return AcquireByKey(asset, key, maxSize, load);
            }
            return AcquireScaled(asset, prefix, maxSize, TextureColorSpaceUtility.IsTextureSRGB(asset), load);
        }

        /// <summary>
        /// 長辺 maxSize 以下の版を取得する（prefix は資産パスと中身のハッシュ）。キャッシュに元のミップの続きの版があれば、
        /// 大きさが同じならそれ自体を、縦横ちょうど 1/2^k に縮めればよいならそこから縮めた版を返し、ファイルをデコードし直さない。
        /// 無ければ load で作る
        /// </summary>
        internal static SourceTexture AcquireScaled(Texture2D asset, string prefix, int maxSize, bool isSrgb, Func<Entry> load)
        {
            string key = prefix + maxSize;
            if (!IsLive(key) && TryFindChainSource(prefix, maxSize, isSrgb, out string sourceKey, out var source, out bool same))
            {
                if (same) return AcquireByKey(asset, sourceKey, maxSize, load);
                return AcquireByKey(asset, key, maxSize, () => DeriveFromChain(source, maxSize));
            }
            return AcquireByKey(asset, key, maxSize, load);
        }

        private static bool IsLive(string key) => s_cache.TryGetValue(key, out var entry) && entry.texture != null;

        /// <summary>
        /// prefix の資産の、元のミップの続きの版（同じ色空間）のうち、長辺 maxSize の版をそのまま（same）か縮めて作れる、いちばん小さいもの
        /// </summary>
        private static bool TryFindChainSource(string prefix, int maxSize, bool isSrgb, out string key, out Entry entry, out bool same)
        {
            key = null;
            entry = null;
            same = false;
            foreach (var pair in s_cache)
            {
                var candidate = pair.Value;
                if (!candidate.isChain || candidate.texture == null || candidate.isSrgb != isSrgb) continue;
                if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!CanDeriveFromChain(candidate.fileWidth, candidate.fileHeight,
                        candidate.texture.width, candidate.texture.height, maxSize, out bool candidateSame))
                {
                    continue;
                }
                // 縮めるなら、いちばん小さい版から（大きさが同じものが見つかればそれで終わり）
                if (!candidateSame && entry != null && candidate.texture.width >= entry.texture.width) continue;
                key = pair.Key;
                entry = candidate;
                same = candidateSame;
                if (same) return true;
            }
            return entry != null;
        }

        /// <summary>
        /// 原寸 fileWidth×fileHeight の画像を長辺 maxSize に縮めた版を、元のミップの続きの版（width×height）から作れるか。
        /// same = 大きさが同じでそのまま使える。縮めるなら縦横ちょうど 1/2^k のときだけ作れる
        /// </summary>
        internal static bool CanDeriveFromChain(int fileWidth, int fileHeight, int width, int height, int maxSize, out bool same)
        {
            RecolorPipeline.ScaleToFit(fileWidth, fileHeight, maxSize, out int targetWidth, out int targetHeight);
            same = width == targetWidth && height == targetHeight;
            return same || IsPowerOfTwoReduction(width, height, targetWidth, targetHeight);
        }

        /// <summary>width×height → targetWidth×targetHeight が、縦横同じ倍率でちょうど 1/2^k（k ≥ 1）か</summary>
        internal static bool IsPowerOfTwoReduction(int width, int height, int targetWidth, int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0 || targetWidth >= width) return false;
            if (width % targetWidth != 0 || height % targetHeight != 0) return false;
            int factor = width / targetWidth;
            return factor == height / targetHeight && Mathf.IsPowerOfTwo(factor);
        }

        /// <summary>元のミップの続きの版 source から、長辺 maxSize の版（これも続きの版）を作る</summary>
        private static Entry DeriveFromChain(Entry source, int maxSize)
        {
            RecolorPipeline.ScaleToFit(source.fileWidth, source.fileHeight, maxSize, out int width, out int height);
            return new Entry
            {
                texture = Downscale(source.texture, width, height, source.isSrgb, withMips: true),
                ownsTexture = true,
                isDirectRead = true,
                isChain = true,
                fileWidth = source.fileWidth,
                fileHeight = source.fileHeight,
                isSrgb = source.isSrgb,
            };
        }

        /// <summary>key の項目の参照を 1 つ増やして返す。無い（破棄済み）なら load で作る。Acquire の本体（テストはここへ直接入る）</summary>
        internal static SourceTexture AcquireByKey(Texture2D asset, string key, int maxSize, Func<Entry> load)
        {
            if (s_cache.TryGetValue(key, out var entry) && entry.texture != null)
            {
                entry.refCount++;
                return new SourceTexture(asset, entry.texture, entry.isDirectRead, key, maxSize, entry);
            }

            entry = load();
            entry.refCount = 1;
            s_cache[key] = entry;
            return new SourceTexture(asset, entry.texture, entry.isDirectRead, key, maxSize, entry);
        }

        /// <summary>
        /// ハンドルが持つ項目の参照を 1 つ減らす。0 になったら、デコードした画像は使っていない項目として取っておき
        /// （TrimIdle で古いものから捨てる）、それ以外（インポート済みの資産そのまま・キャッシュから外れた項目）は破棄する。
        /// 鍵ではなくハンドルが取得時に持った項目そのものを減らすので、ReleaseAll の後に同じ鍵で作り直された
        /// 新しい項目を、古いハンドルの Dispose で減らしてしまうことはない
        /// </summary>
        internal static void Release(SourceTexture handle)
        {
            var entry = handle?.Entry;
            if (entry == null || entry.refCount <= 0) return; // キャッシュ対象外・ReleaseAll で破棄済み

            entry.refCount--;
            if (entry.refCount > 0) return;

            // キャッシュに載っているのがこの項目のときだけ扱う（同じ鍵の別の項目は残す）
            bool cached = handle.CacheKey != null
                && s_cache.TryGetValue(handle.CacheKey, out var current)
                && ReferenceEquals(current, entry);
            if (cached && entry.ownsTexture)
            {
                entry.releasedOrder = ++s_releaseCounter;
                TrimIdle(IdleBudgetBytes);
                return;
            }
            if (cached) s_cache.Remove(handle.CacheKey);
            DestroyEntry(entry);
        }

        /// <summary>
        /// 使っていない項目を、合計が budgetBytes 以下になるまで古い順に捨てる（既定の 0 なら全部）。
        /// 使用中の項目は捨てない
        /// </summary>
        internal static void TrimIdle(long budgetBytes = 0)
        {
            long idleBytes = 0;
            foreach (var entry in s_cache.Values)
            {
                if (IsIdle(entry)) idleBytes += EstimateBytes(entry.texture);
            }

            while (idleBytes > budgetBytes)
            {
                string oldestKey = null;
                Entry oldest = null;
                foreach (var pair in s_cache)
                {
                    if (!IsIdle(pair.Value)) continue;
                    if (oldest == null || pair.Value.releasedOrder < oldest.releasedOrder)
                    {
                        oldestKey = pair.Key;
                        oldest = pair.Value;
                    }
                }
                if (oldest == null) break;

                idleBytes -= EstimateBytes(oldest.texture);
                s_cache.Remove(oldestKey);
                DestroyEntry(oldest);
            }
        }

        /// <summary>assetPaths の資産の、使っていない項目を捨てる（再インポート・削除・移動で中身や鍵が古くなったもの）</summary>
        internal static void DropIdle(IEnumerable<string> assetPaths)
        {
            List<string> stale = null;
            foreach (string path in assetPaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                string prefix = path + "|";
                foreach (var pair in s_cache)
                {
                    if (!IsIdle(pair.Value) || !pair.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    (stale ??= new List<string>()).Add(pair.Key);
                }
            }
            if (stale == null) return;

            foreach (string key in stale)
            {
                if (!s_cache.TryGetValue(key, out var entry)) continue;
                s_cache.Remove(key);
                DestroyEntry(entry);
            }
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

        /// <summary>キャッシュ内の件数（使っていない項目も含む。テスト用）</summary>
        internal static int CachedEntryCount => s_cache.Count;

        private static bool IsIdle(Entry entry) => entry.refCount == 0 && entry.ownsTexture;

        /// <summary>
        /// テクスチャが持つメモリの見積もり（RGBA32、ミップ付きは 4/3 倍、CPU から読める写しがあれば 2 倍）。
        /// 直読みの画像は RGBA32・読める写しありで作るので、それ以外の形式は数えない
        /// </summary>
        internal static long EstimateBytes(Texture2D texture)
        {
            if (texture == null) return 0;
            long bytes = (long)texture.width * texture.height * 4;
            if (texture.mipmapCount > 1) bytes += bytes / 3;
            if (texture.isReadable) bytes *= 2;
            return bytes;
        }

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

        /// <summary>再インポート・削除・移動された資産の、使っていない項目を捨てる</summary>
        private sealed class ImportWatcher : AssetPostprocessor
        {
            private static void OnPostprocessAllAssets(
                string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
            {
                if (s_cache.Count == 0) return;
                DropIdle(importedAssets);
                DropIdle(deletedAssets);
                DropIdle(movedFromAssetPaths);
            }
        }

        private static Entry Load(Texture2D asset, string assetPath, int maxSize)
        {
            if (IsDirectReadableExtension(assetPath))
            {
                bool isSrgb = TextureColorSpaceUtility.IsTextureSRGB(asset);
                var direct = TryLoadFromFile(asset, assetPath, maxSize, isSrgb);
                if (direct != null) return direct;
            }

            // インポート済みの資産をそのまま使う（GPU の Blit は圧縮テクスチャを直接扱える）
            return new Entry { texture = asset, ownsTexture = false, isDirectRead = false };
        }

        /// <summary>
        /// ファイルをデコードした項目を作る（maxSize &gt; 0 ならミップ付きでデコードし、長辺が maxSize を超えたら縮める）。読めなければ null
        /// </summary>
        private static Entry TryLoadFromFile(Texture2D asset, string assetPath, int maxSize, bool isSrgb)
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
                Debug.LogWarning($"[Tocolo] 元画像'{assetPath}'を読めませんでした（{e.Message}）。インポート済みのテクスチャを使います");
                return null;
            }

            // 縮小するときはミップを作っておくと Blit の縮小がエイリアシングしない
            bool needMips = maxSize > 0;
            var loaded = DecodeImage(bytes, asset.name + " (source)", needMips, isSrgb);
            if (loaded == null)
            {
                Debug.LogWarning($"[Tocolo] 元画像'{assetPath}'をデコードできませんでした。インポート済みのテクスチャを使います");
                return null;
            }

            var entry = new Entry
            {
                texture = loaded,
                ownsTexture = true,
                isDirectRead = true,
                // ミップ付きの原寸のデコードは、元のミップそのもの
                isChain = needMips,
                fileWidth = loaded.width,
                fileHeight = loaded.height,
                isSrgb = isSrgb,
            };
            RecolorPipeline.ScaleToFit(loaded.width, loaded.height, maxSize, out int width, out int height);
            if (width == loaded.width && height == loaded.height) return entry;

            // 縦横ちょうど 1/2^k なら縮めた版にもミップを作り、元のミップの続きにする（小さい版をここから作れる）
            bool chain = IsPowerOfTwoReduction(loaded.width, loaded.height, width, height);
            entry.texture = Downscale(loaded, width, height, isSrgb, withMips: chain);
            entry.isChain = chain;
            TextureColorSpaceUtility.UnregisterRuntimeTexture(loaded);
            UnityEngine.Object.DestroyImmediate(loaded);
            return entry;
        }

        /// <summary>
        /// PNG / JPG のバイト列を RGBA32 のテクスチャにデコードする（mips なら Unity がミップを作る。色空間を登録する）。
        /// デコードできなければ null
        /// </summary>
        internal static Texture2D DecodeImage(byte[] bytes, string name, bool mips, bool isSrgb)
        {
            var loaded = new Texture2D(2, 2, TextureFormat.RGBA32, mips, linear: !isSrgb)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = name,
            };
            if (!loaded.LoadImage(bytes, false))
            {
                UnityEngine.Object.DestroyImmediate(loaded);
                return null;
            }
            TextureColorSpaceUtility.RegisterRuntimeTexture(loaded, isSrgb);
            return loaded;
        }

        /// <summary>
        /// source を width×height に縮めた読めるテクスチャを作る。withMips なら縮めた画像から Unity がミップを作る
        /// （source が元のミップの続きで、縦横ちょうど 1/2^k なら、結果も続きになる）
        /// </summary>
        internal static Texture2D Downscale(Texture2D source, int width, int height, bool isSrgb, bool withMips)
        {
            // 元と同じ色空間の RT に縮めてから読み戻す（sRGB ↔ 線形の往復で値を変えない）
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                isSrgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                var result = TextureColorSpaceUtility.CreateRuntimeTexture(width, height, TextureFormat.RGBA32, withMips, isSrgb);
                result.hideFlags = HideFlags.HideAndDontSave;
                result.name = source.name;
                result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                result.Apply(updateMipmaps: withMips);
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
