using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.SceneTool;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Pipeline
{
    /// <summary>
    /// 編集ごとの選択マスク（合成マスクと部分＝種ごとのマスク・統計）のキャッシュ（LRU、参照カウント無し）。
    /// 鍵は編集の id・「編集の選択仕様」・作業解像度・書き出し由来か。色の設定（目標色・強さ・暗部の明るさ）は鍵に含めないので、
    /// スライダーを動かしてもマスクと統計は作り直さない（選択が揺れない）。
    /// 返す RT はキャッシュ所有なので、呼び出し側で解放しないこと。
    /// 枚数は Capacity までに抑える（編集が多いとあふれた分は捨て、次に PrepareJob されたときに作り直す）。
    /// 現在の編集（ToolSession.CurrentEditId）のマスクは Trim で捨てない。
    /// 「画像を入れる」のデカール層は別のキャッシュ（DecalLayerCache）が持つ（画像の箱を動かしても選択マスクを作り直さないため）
    /// </summary>
    internal static class MaskCache
    {
        /// <summary>Trim で残す枚数の上限（現在の編集のマスクもこの枚数に数える）</summary>
        internal const int Capacity = 16;

        internal readonly struct Key : IEquatable<Key>
        {
            private readonly string _editId;
            private readonly bool _fromExport;
            private readonly int _textureId;
            private readonly Hash128 _contentsHash;
            private readonly int _rendererId;
            private readonly int _submesh;
            private readonly int _triangle;
            private readonly Vector2 _seedUv;
            private readonly int _extraSeedsHash;
            private readonly bool _hasSeedOklab;
            private readonly Vector3 _seedOklab;
            private readonly SelectionMode _mode;
            private readonly ColorScope _scope;
            private readonly float _threshold;
            private readonly float _feather;
            private readonly int _cleanupRadius;
            private readonly int _padding;
            private readonly bool _perSeedStats;
            private readonly bool _hasDecal;
            private readonly int _size;
            private readonly int _usersHash;

            /// <summary>この鍵を作った編集の id（Trim で現在の編集のマスクを残すのに使う）</summary>
            internal string EditId => _editId;

            /// <summary>書き出し（ファイル解像度）で作ったマスクか。RemoveExportMasks で捨てる</summary>
            internal bool FromExport => _fromExport;

            /// <param name="usersHash">種のメッシュ（追加の種を含む）と、テクスチャの利用者（メッシュ・サブメッシュ・Tiling/Offset）の畳み込み</param>
            /// <param name="fromExport">
            /// 書き出しで作るマスクなら true（プレビュー解像度が Full でもプレビューのマスクとは別の鍵にし、書き出しの後にだけ捨てる）
            /// </param>
            internal Key(RecolorEdit edit, Texture2D sourceTexture, int size, int usersHash, bool fromExport = false)
            {
                _editId = edit.id;
                _fromExport = fromExport;
                _textureId = sourceTexture != null ? sourceTexture.GetInstanceID() : 0;
                // 再インポートで中身が変わったら統計が古くなるので、中身のハッシュも鍵にする
                _contentsHash = sourceTexture != null ? sourceTexture.imageContentsHash : default;
                _rendererId = edit.seedRenderer != null ? edit.seedRenderer.GetInstanceID() : 0;
                _submesh = edit.seedSubmesh;
                _triangle = edit.seedTriangle;
                _seedUv = edit.seedUv;
                // Ctrl＋クリックで足した種も範囲を決めるので鍵に入れる
                _extraSeedsHash = RecolorPipeline.HashExtraSeeds(edit);
                // 共有の種色（アバター全体の連結）は色マスクの種になるので鍵に入れる
                _hasSeedOklab = edit.hasSeedOklab;
                _seedOklab = edit.hasSeedOklab ? edit.seedOklab : default;
                _mode = edit.mode;
                _scope = edit.scope;
                _threshold = edit.threshold;
                _feather = edit.feather;
                _cleanupRadius = edit.cleanupRadius;
                _padding = edit.padding;
                // 種ごとに色を揃えるかで、部分（種ごとのマスクと統計）の作り方が変わる
                _perSeedStats = edit.perSeedStats;
                // 画像入りなら部分（種ごと・パーツごと）を作らない（RecolorPipeline.PrepareJob）ので、画像を外したら作り直す
                _hasDecal = edit.HasDecal;
                _size = size;
                _usersHash = usersHash;
            }

            public bool Equals(Key other) =>
                _editId == other._editId
                && _fromExport == other._fromExport
                && _textureId == other._textureId
                && _contentsHash.Equals(other._contentsHash)
                && _rendererId == other._rendererId
                && _submesh == other._submesh
                && _triangle == other._triangle
                && _seedUv.Equals(other._seedUv)
                && _extraSeedsHash == other._extraSeedsHash
                && _hasSeedOklab == other._hasSeedOklab
                && _seedOklab.Equals(other._seedOklab)
                && _mode == other._mode
                && _scope == other._scope
                && _threshold.Equals(other._threshold)
                && _feather.Equals(other._feather)
                && _cleanupRadius == other._cleanupRadius
                && _padding == other._padding
                && _perSeedStats == other._perSeedStats
                && _hasDecal == other._hasDecal
                && _size == other._size
                && _usersHash == other._usersHash;

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = _editId != null ? _editId.GetHashCode() : 0;
                    h = h * 397 ^ (_fromExport ? 1 : 0);
                    h = h * 397 ^ _textureId;
                    h = h * 397 ^ _contentsHash.GetHashCode();
                    h = h * 397 ^ _rendererId;
                    h = h * 397 ^ _submesh;
                    h = h * 397 ^ _triangle;
                    h = h * 397 ^ _seedUv.GetHashCode();
                    h = h * 397 ^ _extraSeedsHash;
                    h = h * 397 ^ (_hasSeedOklab ? 1 : 0);
                    h = h * 397 ^ _seedOklab.GetHashCode();
                    h = h * 397 ^ (int)_mode;
                    h = h * 397 ^ (int)_scope;
                    h = h * 397 ^ _threshold.GetHashCode();
                    h = h * 397 ^ _feather.GetHashCode();
                    h = h * 397 ^ _cleanupRadius;
                    h = h * 397 ^ _padding;
                    h = h * 397 ^ (_perSeedStats ? 1 : 0);
                    h = h * 397 ^ (_hasDecal ? 1 : 0);
                    h = h * 397 ^ _size;
                    h = h * 397 ^ _usersHash;
                    return h;
                }
            }
        }

        private sealed class Entry
        {
            public Key key;
            /// <summary>合成マスク（部分が 1 つなら parts[0].mask と同じ RT）</summary>
            public RenderTexture mask;
            /// <summary>部分（種ごとのマスクと統計。種ごとに色を揃えないなら 1 つ）</summary>
            public IReadOnlyList<EditJob.Part> parts;
        }

        private static readonly IReadOnlyList<EditJob.Part> s_noParts = Array.Empty<EditJob.Part>();

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

        /// <summary>鍵が一致し、合成マスクと部分のマスクがすべてまだ生きていれば返す（最近使ったものとして並べ替える）</summary>
        internal static bool TryGet(in Key key, out RenderTexture mask, out IReadOnlyList<EditJob.Part> parts)
        {
            for (int i = 0; i < s_entries.Count; i++)
            {
                var entry = s_entries[i];
                if (!entry.key.Equals(key)) continue;

                s_entries.RemoveAt(i);
                if (!IsAlive(entry))
                {
                    // 描画デバイスのリセット等で中身を失ったマスクは捨てて作り直させる
                    Release(entry);
                    break;
                }
                s_entries.Add(entry);
                mask = entry.mask;
                parts = entry.parts;
                return true;
            }
            mask = null;
            parts = s_noParts;
            return false;
        }

        /// <summary>
        /// 合成マスクと部分のマスク（MaskTextures.Create で作ったもの）の所有権を受け取って登録する。
        /// 部分のマスクは合成マスクと同じ RT でもよい（二重に解放しない）。
        /// ここでは減らさない（同じ Run に渡す前の編集のマスクを捨ててしまわないため）。減らすのは Trim
        /// </summary>
        internal static void Add(in Key key, RenderTexture mask, IReadOnlyList<EditJob.Part> parts)
        {
            var added = new Entry { key = key, mask = mask, parts = parts ?? s_noParts };
            for (int i = 0; i < s_entries.Count; i++)
            {
                if (!s_entries[i].key.Equals(key)) continue;
                Release(s_entries[i], added);
                s_entries.RemoveAt(i);
                break;
            }
            s_entries.Add(added);
        }

        private static bool IsAlive(Entry entry)
        {
            if (entry.mask == null || !entry.mask.IsCreated()) return false;
            foreach (var part in entry.parts)
            {
                if (part.mask == null || !part.mask.IsCreated()) return false;
            }
            return true;
        }

        /// <summary>
        /// entry の RT（合成マスクと部分のマスク）を解放する。keep が持っている RT は残す（同じ RT を二重に解放しない）
        /// </summary>
        private static void Release(Entry entry, Entry keep = null)
        {
            var released = new HashSet<RenderTexture>();
            ReleaseOne(entry.mask, keep, released);
            foreach (var part in entry.parts) ReleaseOne(part.mask, keep, released);
        }

        private static void ReleaseOne(RenderTexture rt, Entry keep, HashSet<RenderTexture> released)
        {
            if (rt == null || !released.Add(rt) || (keep != null && Owns(keep, rt))) return;
            MaskTextures.Destroy(rt);
        }

        private static bool Owns(Entry entry, RenderTexture rt)
        {
            if (entry.mask == rt) return true;
            foreach (var part in entry.parts)
            {
                if (part.mask == rt) return true;
            }
            return false;
        }

        /// <summary>
        /// Capacity 枚まで減らす（古いものから解放。現在の編集 ToolSession.CurrentEditId のマスクは残す）。
        /// 1 回の Run に渡すマスクを捨てないよう、PrepareJob → Run を終えてから呼ぶこと。
        /// 「画像を入れる」の層（DecalLayerCache）も同じ時機に減らす（PrepareJob の約束を呼び出し側で増やさないため）
        /// </summary>
        internal static void Trim()
        {
            Trim(Capacity, ToolSession.CurrentEditId);
            DecalLayerCache.Trim();
        }

        /// <summary>
        /// 最近使った keep 枚まで減らす（古いものから解放）。pinnedEditId の編集のマスクは捨てない（keep の枚数には数える。
        /// それだけで keep を超えるときは keep より多く残る）
        /// </summary>
        internal static void Trim(int keep, string pinnedEditId)
        {
            int excess = s_entries.Count - Mathf.Max(0, keep);
            for (int i = 0; i < s_entries.Count && excess > 0;)
            {
                if (pinnedEditId != null && s_entries[i].key.EditId == pinnedEditId)
                {
                    i++;
                    continue;
                }
                Release(s_entries[i]);
                s_entries.RemoveAt(i);
                excess--;
            }
        }

        /// <summary>
        /// 書き出しで作ったマスク（ファイル解像度で大きい）をすべて解放する。書き出しの後に残さない。
        /// プレビューのマスクは（プレビュー解像度が Full でも）残す（取得済みの書き出しの EditJob のマスクはこれ以降使えない）。
        /// 書き出しで作った「画像を入れる」の層（DecalLayerCache）も捨てる
        /// </summary>
        internal static void RemoveExportMasks()
        {
            DecalLayerCache.RemoveExportLayers();
            for (int i = s_entries.Count - 1; i >= 0; i--)
            {
                if (!s_entries[i].key.FromExport) continue;
                Release(s_entries[i]);
                s_entries.RemoveAt(i);
            }
        }

        /// <summary>キャッシュしたマスクをすべて解放する（取得済みの EditJob のマスクはこれ以降使えない）</summary>
        internal static void ClearCache()
        {
            foreach (var entry in s_entries) Release(entry);
            s_entries.Clear();
        }
    }
}
