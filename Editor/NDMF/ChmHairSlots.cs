using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Picking;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    /// <summary>
    /// 結果を作る単位: 元テクスチャ（範囲を選ぶ）と土台（色を掛ける。null なら元テクスチャ＝今までどおり）。
    /// 同じ元テクスチャでも Renderer ごとに髪ツールの出力が違うことがあるので、土台ごとに別の結果を作る
    /// </summary>
    internal readonly struct TextureUnit : System.IEquatable<TextureUnit>
    {
        public readonly Texture2D source;
        public readonly Texture2D baseTexture;

        public TextureUnit(Texture2D source, Texture2D baseTexture)
        {
            this.source = source;
            this.baseTexture = baseTexture;
        }

        public bool Equals(TextureUnit other) =>
            ReferenceEquals(source, other.source) && ReferenceEquals(baseTexture, other.baseTexture);

        public override bool Equals(object obj) => obj is TextureUnit other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = ReferenceEquals(source, null) ? 0 : source.GetInstanceID();
                return h * 397 ^ (ReferenceEquals(baseTexture, null) ? 0 : baseTexture.GetInstanceID());
            }
        }

        /// <summary>
        /// 結果の鍵のハッシュに土台を畳む: 土台の個体と更新回数（髪ツールが作り直すと別の個体か別の回数になる）。土台が無ければそのまま
        /// </summary>
        internal static int FoldBase(int hash, Texture baseTexture)
        {
            if (baseTexture == null) return hash;
            unchecked
            {
                int h = hash * 31 + baseTexture.GetInstanceID();
                return h * 31 + (int)baseTexture.updateCount;
            }
        }
    }

    /// <summary>髪用の入口が差し替えるスロット 1 つ</summary>
    internal readonly struct HairSlot
    {
        /// <summary>マテリアル配列の番号（元と今で同じ）</summary>
        public readonly int slot;
        /// <summary>元のマテリアルのメイン（編集の対象テクスチャ。範囲はこれで選ぶ）</summary>
        public readonly Texture2D source;
        /// <summary>今のマテリアルのメイン（色を掛ける土台）。元と同じなら null（髪ツールがメインを変えなかった）</summary>
        public readonly Texture2D baseTexture;
        /// <summary>今のマテリアル（複製元）</summary>
        public readonly Material current;
        /// <summary>今のマテリアルのメインのプロパティ名</summary>
        public readonly string propertyName;

        public HairSlot(int slot, Texture2D source, Texture2D baseTexture, Material current, string propertyName)
        {
            this.slot = slot;
            this.source = source;
            this.baseTexture = baseTexture;
            this.current = current;
            this.propertyName = propertyName;
        }
    }

    /// <summary>
    /// 髪ツールが先に加工した Renderer で、どのスロットに何を土台に掛けるかを決める。
    /// 参照の一致でなくスロットの番号で元と今を対応させる: 髪ツールはメッシュ統合 OFF ではマテリアルを複製して同じ番号に入れ直し
    /// （メインは髪ツールの出力）、影の追加は末尾に足すだけなので、番号が保たれる
    /// </summary>
    internal static class ChmHairSlots
    {
        /// <summary>
        /// original（元のマテリアル配列）の i 番のメインが sources に入っていれば、current（今の配列）の i 番を差し替え対象にする。
        /// 今のメインが取れないスロットは飛ばす。末尾に足されたスロット（i ≥ original の数）は見ない。
        /// 対象のスロットがあるのに current が original より短い（並びが変わった）ときは、どれも選ばず mismatch = true
        /// </summary>
        internal static List<HairSlot> Plan(
            IReadOnlyList<Material> original, IReadOnlyList<Material> current, ICollection<Texture2D> sources, out bool mismatch)
        {
            var result = new List<HairSlot>();
            mismatch = false;
            if (original == null || current == null || sources == null || sources.Count == 0) return result;

            for (int i = 0; i < original.Count; i++)
            {
                if (!MaterialTextureResolver.TryGetMainTexture(original[i], out var before) || !sources.Contains(before.texture)) continue;
                if (current.Count < original.Count)
                {
                    mismatch = true;
                    result.Clear();
                    return result;
                }
                var material = current[i];
                if (!MaterialTextureResolver.TryGetMainTexture(material, out var now)) continue;
                var baseTexture = now.texture == before.texture ? null : now.texture;
                result.Add(new HairSlot(i, before.texture, baseTexture, material, now.propertyName));
            }
            return result;
        }
    }
}
