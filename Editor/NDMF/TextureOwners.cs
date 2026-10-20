using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Decal;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    /// <summary>
    /// テクスチャごとの基準のコンポーネント（作業解像度と、グラデーション・箱・位置マップの位置の基準）。
    /// 同じテクスチャを複数のコンポーネントが編集しているとき、プレビュー（色変え・重ね貼り）とビルドで同じ決め方にする。
    /// isActive はプレビューなら previewEnabled、ビルドなら applyOnBuild
    /// </summary>
    internal sealed class TextureOwners
    {
        private readonly IReadOnlyList<ClickRecolor> _components;
        private readonly Dictionary<Texture2D, int> _owner = new Dictionary<Texture2D, int>();
        private readonly List<Texture2D> _order = new List<Texture2D>();

        private TextureOwners(IReadOnlyList<ClickRecolor> components)
        {
            _components = components;
        }

        /// <summary>
        /// そのテクスチャに効く編集（RecolorPreview.IsPreviewTarget）を最初に持つコンポーネント。重ね貼りの基準
        /// （重ね貼りかどうかに関係なく数えるので、色変えの編集と同じ大きさ・同じ文脈の選択マスクになり MaskCache を共有できる）
        /// </summary>
        internal static TextureOwners FirstEffective(IReadOnlyList<ClickRecolor> components, Func<ClickRecolor, bool> isActive) =>
            Collect(components, isActive, (component, edit) => true);

        /// <summary>
        /// そのテクスチャを焼く編集（効く編集のうち重ね貼りでないもの）を最初に持つコンポーネント。焼き込みの基準。
        /// プレビューでハイライト中だけ扱う編集（色未設定・重ね貼り）は数えない: 数えると、編集を選んだだけで
        /// 同じテクスチャの他の編集のグラデーションの基準と解像度が変わり、ビルドとも食い違う
        /// </summary>
        internal static TextureOwners FirstBaking(IReadOnlyList<ClickRecolor> components, Func<ClickRecolor, bool> isActive) =>
            Collect(components, isActive, (component, edit) => !DecalOverlayMaterial.UseOverlay(component, edit));

        private static TextureOwners Collect(
            IReadOnlyList<ClickRecolor> components, Func<ClickRecolor, bool> isActive, Func<ClickRecolor, RecolorEdit, bool> counts)
        {
            var result = new TextureOwners(components);
            if (components == null) return result;
            for (int c = 0; c < components.Count; c++)
            {
                var component = components[c];
                if (component == null || component.edits == null || !isActive(component)) continue;
                foreach (var edit in component.edits)
                {
                    if (!RecolorPreview.IsPreviewTarget(edit) || result._owner.ContainsKey(edit.sourceTexture)) continue;
                    if (!counts(component, edit)) continue;
                    result._owner.Add(edit.sourceTexture, c);
                    result._order.Add(edit.sourceTexture);
                }
            }
            return result;
        }

        /// <summary>texture の基準のコンポーネント。そのテクスチャに数える編集が無ければ null</summary>
        internal ClickRecolor Of(Texture2D texture) =>
            texture != null && _owner.TryGetValue(texture, out int c) ? _components[c] : null;

        /// <summary>texture の基準のコンポーネントの番号（components の中）。無ければ −1</summary>
        internal int IndexOf(Texture2D texture) =>
            texture != null && _owner.TryGetValue(texture, out int c) ? c : -1;

        /// <summary>基準が決まったテクスチャ（最初に見つけた順）</summary>
        internal IReadOnlyList<Texture2D> Textures => _order;
    }
}
