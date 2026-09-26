using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>
    /// 色モード「島の中」で色マスクに掛ける島マスク（パディング 0・ゴマ塩除去 0）のキャッシュ（LRU、Capacity 枚）。
    /// 島マスクはしきい値・ぼかし・ゴマ塩・パディングに依らないので、それらのスライダーを動かしても島を作り直さない。
    /// 返す RT はキャッシュ所有なので、呼び出し側で解放しないこと
    /// </summary>
    internal static class IslandMaskCache
    {
        /// <summary>保持する枚数の上限</summary>
        internal const int Capacity = 8;

        /// <summary>
        /// 鍵: 種のメッシュ（InstanceID と形のフィンガープリント）・サブメッシュ・三角形・大きさ。
        /// 加えて、島マスクの中身を左右する種の位置（4 連結の種画素）と利用者の畳み込み（種の Tiling/Offset と、
        /// ぼかしを制約する全チャート被覆が変わる）も持つ
        /// </summary>
        internal readonly struct Key : IEquatable<Key>
        {
            private readonly int _meshId;
            private readonly int _fingerprint;
            private readonly int _submesh;
            private readonly int _seedTriangle;
            private readonly Vector2 _seedUv;
            private readonly int _usersHash;
            private readonly int _width;
            private readonly int _height;

            internal Key(int meshId, int fingerprint, int submesh, int seedTriangle, Vector2 seedUv, int usersHash, int width, int height)
            {
                _meshId = meshId;
                _fingerprint = fingerprint;
                _submesh = submesh;
                _seedTriangle = seedTriangle;
                _seedUv = seedUv;
                _usersHash = usersHash;
                _width = width;
                _height = height;
            }

            public bool Equals(Key other) =>
                _meshId == other._meshId
                && _fingerprint == other._fingerprint
                && _submesh == other._submesh
                && _seedTriangle == other._seedTriangle
                && _seedUv.Equals(other._seedUv)
                && _usersHash == other._usersHash
                && _width == other._width
                && _height == other._height;

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = _meshId;
                    h = h * 397 ^ _fingerprint;
                    h = h * 397 ^ _submesh;
                    h = h * 397 ^ _seedTriangle;
                    h = h * 397 ^ _seedUv.GetHashCode();
                    h = h * 397 ^ _usersHash;
                    h = h * 397 ^ _width;
                    h = h * 397 ^ _height;
                    return h;
                }
            }
        }

        private sealed class Entry
        {
            public Key key;
            public RenderTexture mask;
        }

        // 末尾ほど最近使ったもの
        private static readonly List<Entry> s_entries = new List<Entry>();

        /// <summary>キャッシュ中の枚数（テスト用）</summary>
        internal static int Count => s_entries.Count;

        /// <summary>
        /// 鍵が一致し、中身が生きている島マスクを返す。無ければ build で作って登録する（あふれたら最古を解放）。
        /// build は MaskTextures.Create で作った RT（所有権はキャッシュへ移る）か null を返すこと。作れなければ null
        /// </summary>
        internal static RenderTexture GetOrBuild(in Key key, Func<RenderTexture> build)
        {
            for (int i = 0; i < s_entries.Count; i++)
            {
                var entry = s_entries[i];
                if (!entry.key.Equals(key)) continue;

                s_entries.RemoveAt(i);
                if (entry.mask == null || !entry.mask.IsCreated())
                {
                    // 描画デバイスのリセット等で中身を失ったマスクは捨てて作り直す
                    MaskTextures.Destroy(entry.mask);
                    break;
                }
                s_entries.Add(entry);
                return entry.mask;
            }

            var mask = build();
            if (mask == null) return null;
            s_entries.Add(new Entry { key = key, mask = mask });
            // 今作ったものは末尾なので捨てない
            while (s_entries.Count > Capacity)
            {
                MaskTextures.Destroy(s_entries[0].mask);
                s_entries.RemoveAt(0);
            }
            return mask;
        }

        /// <summary>キャッシュした島マスクをすべて解放する（GetOrBuild が返した RT はこれ以降使えない）</summary>
        internal static void ClearCache()
        {
            foreach (var entry in s_entries) MaskTextures.Destroy(entry.mask);
            s_entries.Clear();
        }
    }
}
