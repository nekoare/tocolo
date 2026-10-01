using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Picking
{
    /// <summary>
    /// クリックした場所に lilToon のメインカラー 2nd／3rd が重なっているかを調べる（ユーザー判断 2026-10-01: 案 E）。
    /// Tocolo はメインテクスチャだけを変えるので、上に重なる層があると色が変わって見えにくい。案内を出すためだけに使い、色変え自体は変えない。
    /// 覆い具合は lilToon と同じく「色のアルファ × テクスチャのアルファ × マスクの赤」（lil_common_frag.hlsl の lilGetMain2nd）。
    /// テクスチャが無い層は白・不透明、マスクが無ければ全面として扱う。デカール・UV1〜3 の層にテクスチャがあるときは位置を再現できないので見ない
    /// </summary>
    internal static class LilToonLayerOverlap
    {
        internal enum Kind { None, Covers, Mixes }

        internal struct Result
        {
            public Kind kind;
            /// <summary>当てはまる層の名前（"2nd" / "3rd" / "2nd/3rd"）</summary>
            public string layers;
        }

        /// <summary>lilToon の合成モード（_Main2ndTexBlendMode）の「通常」</summary>
        internal const int BlendNormal = 0;
        /// <summary>通常の層を合わせた覆い具合がこれ以上なら「色が変わりにくいかもしれない」</summary>
        internal const float CoverThreshold = 0.5f;
        /// <summary>加算・スクリーン・乗算の層の強さがこれ以上なら「色が混ざる」</summary>
        internal const float MixThreshold = 0.25f;
        /// <summary>これ未満の層は数えない</summary>
        private const float IgnoreBelow = 0.05f;

        private sealed class Layer
        {
            public readonly string name, use, texture, st, color, mask, blendMode, uvMode, isDecal, angle;

            public Layer(string name)
            {
                this.name = name;
                use = $"_UseMain{name}Tex";
                texture = $"_Main{name}Tex";
                st = $"_Main{name}Tex_ST";
                color = $"_Color{name}";
                mask = $"_Main{name}BlendMask";
                blendMode = $"_Main{name}TexBlendMode";
                uvMode = $"_Main{name}Tex_UVMode";
                isDecal = $"_Main{name}TexIsDecal";
                angle = $"_Main{name}TexAngle";
            }
        }

        private static readonly Layer[] Layers = { new Layer("2nd"), new Layer("3rd") };

        /// <summary>
        /// material の 2nd／3rd がクリック位置（uv0 = メッシュの UV0、uvMain = メインテクスチャの座標）でどれだけ重なるか。
        /// lilToon でない・層が無ければ None。テクスチャとマスクはクリック位置の 1 画素だけ GPU で読む（クリック時だけ呼ぶ）
        /// </summary>
        internal static Result Check(Material material, Vector2 uv0, Vector2 uvMain)
        {
            var layers = new List<(string name, float alpha, int blendMode)>();
            if (material == null) return default;
            foreach (var layer in Layers)
            {
                if (!material.HasProperty(layer.use) || material.GetFloat(layer.use) == 0f) continue;
                float alpha = material.HasProperty(layer.color) ? material.GetColor(layer.color).a : 1f;
                if (alpha < IgnoreBelow) continue;

                var texture = material.HasProperty(layer.texture) ? material.GetTexture(layer.texture) : null;
                if (texture != null)
                {
                    bool decal = material.HasProperty(layer.isDecal) && material.GetFloat(layer.isDecal) != 0f;
                    int uvMode = material.HasProperty(layer.uvMode) ? Mathf.RoundToInt(material.GetFloat(layer.uvMode)) : 0;
                    // デカール・UV0 以外は位置を再現できないので見ない（誤った案内を出さない）
                    if (decal || uvMode != 0) continue;
                    var st = material.HasProperty(layer.st) ? material.GetVector(layer.st) : new Vector4(1f, 1f, 0f, 0f);
                    float angle = material.HasProperty(layer.angle) ? material.GetFloat(layer.angle) : 0f;
                    alpha *= TexelSampler.Sample(texture, LayerUv(uv0, st, angle)).a;
                }
                var mask = material.HasProperty(layer.mask) ? material.GetTexture(layer.mask) : null;
                if (mask != null) alpha *= TexelSampler.Sample(mask, uvMain).r;

                int blendMode = material.HasProperty(layer.blendMode) ? Mathf.RoundToInt(material.GetFloat(layer.blendMode)) : BlendNormal;
                layers.Add((layer.name, alpha, blendMode));
            }
            return Evaluate(layers);
        }

        /// <summary>層ごとの (名前, 強さ, 合成モード) から案内の種類を決める。通常の層の覆いを優先する</summary>
        internal static Result Evaluate(IReadOnlyList<(string name, float alpha, int blendMode)> layers)
        {
            float through = 1f;
            var covering = new List<string>();
            var mixing = new List<string>();
            foreach (var (name, alpha, blendMode) in layers)
            {
                if (alpha < IgnoreBelow) continue;
                if (blendMode == BlendNormal)
                {
                    through *= 1f - Mathf.Clamp01(alpha);
                    covering.Add(name);
                }
                else if (alpha >= MixThreshold)
                {
                    mixing.Add(name);
                }
            }
            if (1f - through >= CoverThreshold) return new Result { kind = Kind.Covers, layers = string.Join("/", covering) };
            if (mixing.Count > 0) return new Result { kind = Kind.Mixes, layers = string.Join("/", mixing) };
            return default;
        }

        /// <summary>lilToon の lilCalcUV（スクロールは除く）: Tiling・Offset のあと中心回りに angle 回す</summary>
        internal static Vector2 LayerUv(Vector2 uv0, Vector4 st, float angle)
        {
            var uv = new Vector2(uv0.x * st.x + st.z, uv0.y * st.y + st.w) - new Vector2(0.5f, 0.5f);
            float si = Mathf.Sin(angle), co = Mathf.Cos(angle);
            return new Vector2(uv.x * co - uv.y * si, uv.x * si + uv.y * co) + new Vector2(0.5f, 0.5f);
        }
    }

    /// <summary>テクスチャの 1 画素を GPU で読む（Read/Write・圧縮形式に関係なく読める。デコードしないので軽い）</summary>
    internal static class TexelSampler
    {
        internal static Color Sample(Texture texture, Vector2 uv)
        {
            if (texture == null) return Color.white;
            var offset = new Vector2(uv.x - Mathf.Floor(uv.x), uv.y - Mathf.Floor(uv.y));
            var rt = RenderTexture.GetTemporary(1, 1, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            var previous = RenderTexture.active;
            try
            {
                // 拡大率 0 ＋ オフセットで、全画素が uv の 1 点を読む
                Graphics.Blit(texture, rt, Vector2.zero, offset);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                tex.Apply(false);
                return tex.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(tex);
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
