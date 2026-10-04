using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Pipeline
{
    /// <summary>
    /// 「画像を入れる」のデカール層（ARGBHalf）と、その統計（層 ∩ 選択マスク）のキャッシュ（LRU、Capacity 枚）。
    /// 選択マスク（MaskCache）と鍵を分けてあるので、画像の箱を動かしても選択マスク（フラッドフィル・パディング・被覆）と
    /// 元テクスチャ基準の統計は作り直さず、層とその統計だけを作り直す（実機 2026-10-04: 箱のドラッグが重い）。
    /// 返す RT はキャッシュ所有なので、呼び出し側で解放しないこと。
    /// 層は EditJob が Run まで持つので、GetOrBuild では減らさない（MaskCache と同じく、全テクスチャの PrepareJob → Run の途中で
    /// 先に準備した編集の層を捨ててしまうため）。減らすのは Trim で、MaskCache.Trim から呼ぶ
    /// </summary>
    internal static class DecalLayerCache
    {
        /// <summary>Trim で残す枚数の上限</summary>
        internal const int Capacity = 4;

        /// <summary>画像の箱のドラッグ中に作る層の長辺の上限（離したら作業解像度で作り直す）。512 は粗すぎた（実機 2026-10-04）</summary>
        internal const int DragMaxSize = 1024;

        /// <summary>
        /// 鍵: 編集の id・元テクスチャ・作業解像度・書き出し由来か、と decalHash
        /// （画像・比率・画像の箱・位置マップの中身・ドラッグ中か・選択マスクの鍵の畳み込み。RecolorPipeline.FoldDecal）
        /// </summary>
        internal readonly struct Key : IEquatable<Key>
        {
            private readonly string _editId;
            private readonly int _textureId;
            private readonly int _size;
            private readonly bool _fromExport;
            private readonly int _decalHash;

            /// <summary>書き出し（ファイル解像度）で作った層か。RemoveExportLayers で捨てる</summary>
            internal bool FromExport => _fromExport;

            internal Key(string editId, int textureId, int size, bool fromExport, int decalHash)
            {
                _editId = editId;
                _textureId = textureId;
                _size = size;
                _fromExport = fromExport;
                _decalHash = decalHash;
            }

            public bool Equals(Key other) =>
                _editId == other._editId
                && _textureId == other._textureId
                && _size == other._size
                && _fromExport == other._fromExport
                && _decalHash == other._decalHash;

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = _editId != null ? _editId.GetHashCode() : 0;
                    h = h * 397 ^ _textureId;
                    h = h * 397 ^ _size;
                    h = h * 397 ^ (_fromExport ? 1 : 0);
                    h = h * 397 ^ _decalHash;
                    return h;
                }
            }
        }

        /// <summary>層を作る関数。層（RecolorPipeline.CreateWorkTexture で作ったもの）とその統計を返す。作れなければ null（統計は使わない）</summary>
        internal delegate RenderTexture BuildLayer(out Stats stats, out BandStats bands);

        private sealed class Entry
        {
            public Key key;
            /// <summary>デカール層（ARGBHalf。RecolorPipeline.DestroyWorkTexture で破棄する）</summary>
            public RenderTexture layer;
            /// <summary>層 ∩ 選択マスクの統計</summary>
            public Stats stats;
            /// <summary>「元のグラデーションを打ち消す」の帯ごとの統計（層から取る。OFF・作れないときは null）</summary>
            public BandStats bands;
            /// <summary>前回の Trim の後に使われたか（その回の Run に要った層。Trim で捨てない）</summary>
            public bool used;
        }

        // 末尾ほど最近使ったもの
        private static readonly List<Entry> s_entries = new List<Entry>();

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            AssemblyReloadEvents.beforeAssemblyReload += ClearCache;
            EditorSceneManager.sceneClosing += (scene, removingScene) => ClearCache();
        }

        /// <summary>キャッシュ中の枚数（テスト用）</summary>
        internal static int Count => s_entries.Count;

        /// <summary>
        /// 鍵が一致し、中身が生きている層を返す（最近使ったものとして並べ替える）。無ければ build で作って登録する。
        /// ここでは減らさない（減らすのは Trim）。作れなければ null
        /// </summary>
        internal static RenderTexture GetOrBuild(in Key key, BuildLayer build, out Stats stats, out BandStats bands)
        {
            for (int i = 0; i < s_entries.Count; i++)
            {
                var entry = s_entries[i];
                if (!entry.key.Equals(key)) continue;

                s_entries.RemoveAt(i);
                if (entry.layer == null || !entry.layer.IsCreated())
                {
                    // 描画デバイスのリセット等で中身を失った層は捨てて作り直す
                    RecolorPipeline.DestroyWorkTexture(entry.layer);
                    break;
                }
                entry.used = true;
                s_entries.Add(entry);
                stats = entry.stats;
                bands = entry.bands;
                return entry.layer;
            }

            var layer = build(out stats, out bands);
            if (layer == null)
            {
                stats = default;
                bands = null;
                return null;
            }
            s_entries.Add(new Entry { key = key, layer = layer, stats = stats, bands = bands, used = true });
            return layer;
        }

        /// <summary>
        /// Capacity 枚まで減らす（古いものから破棄）。1 回の Run に渡す層を捨てないよう、PrepareJob → Run を終えてから呼ぶこと
        /// （MaskCache.Trim から呼ぶ）。前回の Trim の後に使われた層（その回のプレビュー・ビルドに要った層）は Capacity を超えても残す:
        /// 画像入りの編集が Capacity より多いと、毎回いちばん古い 1 枚を捨てて次の回に作り直し続けるため（レビュー指摘 2026-10-04）
        /// </summary>
        internal static void Trim()
        {
            for (int i = 0; i < s_entries.Count && s_entries.Count > Capacity;)
            {
                if (s_entries[i].used)
                {
                    i++;
                    continue;
                }
                RecolorPipeline.DestroyWorkTexture(s_entries[i].layer);
                s_entries.RemoveAt(i);
            }
            foreach (var entry in s_entries) entry.used = false;
        }

        /// <summary>書き出しで作った層（ファイル解像度で大きい）をすべて破棄する（MaskCache.RemoveExportMasks から呼ぶ）</summary>
        internal static void RemoveExportLayers()
        {
            for (int i = s_entries.Count - 1; i >= 0; i--)
            {
                if (!s_entries[i].key.FromExport) continue;
                RecolorPipeline.DestroyWorkTexture(s_entries[i].layer);
                s_entries.RemoveAt(i);
            }
        }

        /// <summary>キャッシュした層をすべて破棄する（取得済みの EditJob の層はこれ以降使えない）</summary>
        internal static void ClearCache()
        {
            foreach (var entry in s_entries) RecolorPipeline.DestroyWorkTexture(entry.layer);
            s_entries.Clear();
        }
    }
}
