using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    /// <summary>
    /// 色変えのプレビューのマテリアルの複製（参照カウント付き）。鍵は (元マテリアル, その中身の CRC, 差し替えるプロパティ, 差し替える元のテクスチャ)。
    /// ノードを作り直すたびに複製し直さない: Unity は複製の中身の表を最初に触ったときに作り、Poiyomi では 1 個約 8ms かかる
    /// （画像の箱のドラッグ中は動かすたびにノードを作り直すので、これが一番重かった）。
    /// 同じ鍵の複製を新旧のノードで共有し、結果 RT だけ差し込み直す。複製はいつも、借りているノードのうち一番新しいものの結果 RT を指す
    /// （新しいノードを表示に使わずに捨てても、表示中の旧ノードの結果 RT に戻る）。誰も借りていない複製はその場で破棄する
    /// </summary>
    internal static class PreviewMaterialCache
    {
        internal readonly struct Key : IEquatable<Key>
        {
            private readonly int _materialId;
            private readonly int _materialCrc;
            private readonly string _propertyName;
            private readonly int _textureId;

            internal Key(Material material, string propertyName, Texture replacedTexture)
            {
                _materialId = material.GetInstanceID();
                // 元マテリアルを編集したら別の複製にする（変更回数は Undo で戻したときに増えないことがあるので、中身で見る）
                _materialCrc = material.ComputeCRC();
                _propertyName = propertyName;
                _textureId = replacedTexture != null ? replacedTexture.GetInstanceID() : 0;
            }

            public bool Equals(Key other) =>
                _materialId == other._materialId && _materialCrc == other._materialCrc
                && _propertyName == other._propertyName && _textureId == other._textureId;

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = _materialId;
                    h = h * 397 ^ _materialCrc;
                    h = h * 397 ^ (_propertyName != null ? _propertyName.GetHashCode() : 0);
                    h = h * 397 ^ _textureId;
                    return h;
                }
            }
        }

        internal sealed class Entry
        {
            public readonly Key key;
            public readonly Material clone;
            /// <summary>結果 RT を差し込むプロパティ（メインと、元のテクスチャを参照していた他のプロパティ）</summary>
            public readonly string[] properties;
            public RenderTexture bound;
            /// <summary>借りているノードの分（借りた順）。最後の物の結果 RT を差し込んでおく</summary>
            public readonly List<Lease> alive = new List<Lease>();

            public Entry(Key key, Material clone, string[] properties)
            {
                this.key = key;
                this.clone = clone;
                this.properties = properties;
            }
        }

        /// <summary>ノードが借りている複製 1 つ。Release で 1 回だけ返す</summary>
        internal sealed class Lease
        {
            internal readonly Entry entry;
            /// <summary>このノードの結果 RT</summary>
            internal readonly RenderTexture rt;

            internal Lease(Entry entry, RenderTexture rt)
            {
                this.entry = entry;
                this.rt = rt;
            }

            public Material Material => entry.clone;
        }

        private static readonly Dictionary<Key, Entry> s_entries = new Dictionary<Key, Entry>();

        /// <summary>今持っている複製の数（テスト用）</summary>
        internal static int Count => s_entries.Count;

        /// <summary>material の複製（保存しない）を借りる。メインの propertyName と、同じ replacedTexture を参照する他のプロパティに rt を差し込む</summary>
        internal static Lease Acquire(Material material, string propertyName, Texture replacedTexture, RenderTexture rt, string nameSuffix)
        {
            var key = new Key(material, propertyName, replacedTexture);
            if (!s_entries.TryGetValue(key, out var entry) || entry.clone == null)
            {
                var clone = MaterialCloner.Clone(material, nameSuffix);
                clone.hideFlags = HideFlags.HideAndDontSave;
                var properties = new List<string> { propertyName };
                // 同じテクスチャを参照する他のプロパティも差し替える（ビルド・書き出しと同じ）。
                // Poiyomi のロック済みマテリアルで _MainTex を改名してアニメートにすると、シェーダーは _MainTex_<名前> を読むため
                foreach (var name in clone.GetTexturePropertyNames())
                {
                    if (name != propertyName && clone.GetTexture(name) == replacedTexture) properties.Add(name);
                }
                entry = new Entry(key, clone, properties.ToArray());
                s_entries[key] = entry;
            }
            var lease = new Lease(entry, rt);
            entry.alive.Add(lease);
            Bind(entry, rt);
            return lease;
        }

        /// <summary>
        /// 借りた複製を返す（2 回目以降は何もしない）。まだ借りているノードがあれば、その一番新しいものの結果 RT に差し直す
        /// （返したノードの結果 RT はこの後で破棄されるので、指したままにしない）
        /// </summary>
        internal static void Release(Lease lease)
        {
            if (lease == null) return;
            var entry = lease.entry;
            if (!entry.alive.Remove(lease)) return;
            if (entry.alive.Count > 0)
            {
                Bind(entry, entry.alive[entry.alive.Count - 1].rt);
                return;
            }
            if (s_entries.TryGetValue(entry.key, out var current) && current == entry) s_entries.Remove(entry.key);
            if (entry.clone != null) Object.DestroyImmediate(entry.clone);
        }

        private static void Bind(Entry entry, RenderTexture rt)
        {
            if (entry.bound == rt || entry.clone == null) return;
            // Tiling/Offset は複製で引き継がれる。結果 RT は Linear（シェーダーは線形値として読む）
            foreach (var name in entry.properties) entry.clone.SetTexture(name, rt);
            entry.bound = rt;
        }
    }
}
