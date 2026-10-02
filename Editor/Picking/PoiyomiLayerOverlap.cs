using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Picking
{
    /// <summary>
    /// クリックした場所に Poiyomi Toon の Decals（4 枠）／RGBA Color Masking（4 層）が重なっているか、Color Adjust が掛かっているかを調べる
    /// （lilToon の LilToonLayerOverlap と同じ案内を Poiyomi でも出す。ユーザー判断 2026-10-03）。
    /// 式は Poiyomi Toon 10.0.24 の Poiyomi Toon.shader（decalUV / SampleDecalLayers / Apply / calculateRGBMask）を写したもの。
    /// プロパティ名は 9.3 と共通。ロック済みのマテリアルでも値は読める（無効な機能はプロパティごと消えるので HasProperty で落ちる）。
    /// 再現できない設定（UV0 以外・ミラー・視差・動画・頂点カラーのマスク）の層は見ない（誤った案内を出さない）
    /// </summary>
    internal static class PoiyomiLayerOverlap
    {
        /// <summary>Poiyomi の customBlend の Replace（これ以外は「色が混ざる」扱い）</summary>
        internal const int BlendReplace = 0;

        /// <summary>Poiyomi のマテリアルか（シェーダー名か、Poiyomi 固有のプロパティで判定）</summary>
        internal static bool IsPoiyomi(Material material)
        {
            if (material == null || material.shader == null) return false;
            if (material.shader.name.IndexOf("poiyomi", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return material.HasProperty("_RGBMaskEnabled") || material.HasProperty("_DecalEnabled") || material.HasProperty("_MainColorAdjustToggle");
        }

        /// <summary>クリック位置（uv0 = メッシュの UV0）の重なり。Poiyomi でなければ None</summary>
        internal static LilToonLayerOverlap.Result Check(Material material, Vector2 uv0)
        {
            if (!IsPoiyomi(material)) return default;
            var layers = new List<(string name, float alpha, int blendMode)>();
            CollectRgbaLayers(material, uv0, layers);
            for (int slot = 0; slot < 4; slot++)
            {
                if (TryDecal(material, slot, uv0, out float alpha, out int blend)) layers.Add(($"Decal {slot}", alpha, blend));
            }
            var result = LilToonLayerOverlap.Evaluate(layers);
            result.poiyomi = true;
            result.colorAdjust = HasColorAdjust(material);
            return result;
        }

        /// <summary>Color Adjust（Hue Shift・彩度・明るさ・ガンマ・Chromatize・Tint・Color Grading のどれか）が効いているか</summary>
        internal static bool HasColorAdjust(Material material)
        {
            if (!On(material, "_MainColorAdjustToggle")) return false;
            return On(material, "_MainHueShiftToggle")
                || Differs(material, "_Saturation", 0f) || Differs(material, "_MainBrightness", 0f)
                || Differs(material, "_MainGamma", 1f) || Differs(material, "_MainChromatize", 0f)
                || (material.HasProperty("_MainTintColor") && material.GetColor("_MainTintColor").a > 0f)
                || On(material, "_ColorGradingToggle");
        }

        // ── RGBA Color Masking ──

        private static readonly string[] RgbaNames = { "Red", "Green", "Blue", "Alpha" };
        private static readonly string[] RgbaLabels = { "RGBA Color Masking (R)", "RGBA Color Masking (G)", "RGBA Color Masking (B)", "RGBA Color Masking (A)" };

        private static void CollectRgbaLayers(Material material, Vector2 uv0, List<(string, float, int)> layers)
        {
            if (!On(material, "_RGBMaskEnabled")) return;
            // マスクは頂点カラー（_RGBMaskType = 1）だとクリック位置で再現しないので見ない
            if (Int(material, "_RGBMaskType", 0) != 0) return;
            Vector4 mask = Vector4.one;
            var maskTexture = Tex(material, "_RGBMask");
            if (maskTexture != null)
            {
                if (Int(material, "_RGBMaskUV", 0) != 0) return;
                var st = St(material, "_RGBMask");
                mask = TexelSampler.Sample(maskTexture, new Vector2(uv0.x * st.x + st.z, uv0.y * st.y + st.w));
            }
            for (int i = 0; i < 4; i++)
            {
                string n = RgbaNames[i];
                if (!On(material, $"_RGBA{n}Enable")) continue;
                var color = material.HasProperty($"_{n}Color") ? material.GetColor($"_{n}Color") : Color.white;
                float alpha = color.a;
                var texture = Tex(material, $"_{n}Texture");
                if (texture != null)
                {
                    if (Int(material, $"_{n}TextureUV", 0) != 0) continue;
                    var st = St(material, $"_{n}Texture");
                    var so = material.HasProperty($"_RGBA{n}ScaleOffset") ? material.GetVector($"_RGBA{n}ScaleOffset") : new Vector4(1f, 1f, 0f, 0f);
                    // PoiUVMerge: xy は掛け、zw は足す
                    var uv = new Vector2(uv0.x * st.x * so.x + st.z + so.z, uv0.y * st.y * so.y + st.w + so.w);
                    alpha *= TexelSampler.Sample(texture, uv).a;
                }
                float add = material.HasProperty($"_{n}AlphaAdd") ? material.GetFloat($"_{n}AlphaAdd") : 0f;
                int channel = Mathf.Clamp(Int(material, $"_Rgb{n}MaskChannel", i), 0, 3);
                float coverage = mask[channel] * Mathf.Clamp01(alpha + add);
                layers.Add((RgbaLabels[i], coverage, Int(material, $"_RGBA{n}BlendType", BlendReplace)));
            }
        }

        // ── Decals ──

        /// <summary>Decal の枠 slot（0〜3）のプロパティ名。0 と 1〜3 で付け方が違う</summary>
        private static string P(int slot, string kind)
        {
            string s = slot == 0 ? "" : slot.ToString();
            switch (kind)
            {
                case "enable": return "_DecalEnabled" + s;
                case "texture": return "_DecalTexture" + s;
                case "uv": return "_DecalTexture" + s + "UV";
                case "color": return "_DecalColor" + s;
                case "tiled": return "_DecalTiled" + s;
                case "blend": return "_DecalBlendType" + s;
                case "rotation": return "_DecalRotation" + s;
                case "scale": return "_DecalScale" + s;
                case "side": return "_DecalSideOffset" + s;
                case "position": return "_DecalPosition" + s;
                case "blendAlpha": return "_DecalBlendAlpha" + s;
                case "intensity": return "_DecalAlphaIntensity" + s;
                case "mirror": return "_DecalMirroredUVMode" + s;
                case "symmetry": return "_DecalSymmetryMode" + s;
                case "aspect": return $"_Decal{slot}AspectRatioMode";
                case "maskChannel": return $"_Decal{slot}MaskChannel";
                case "depth": return $"_Decal{slot}Depth";
                case "video": return $"_Decal{slot}VideoEnabled";
                case "face": return $"_Decal{slot}FaceMask";
                default: return null;
            }
        }

        private static bool TryDecal(Material material, int slot, Vector2 uv0, out float alpha, out int blend)
        {
            alpha = 0f;
            blend = BlendReplace;
            if (!On(material, P(slot, "enable"))) return false;
            // 動画・ミラー（左右で隠す・反転）・視差は位置を再現できないので見ない
            if (On(material, P(slot, "video"))) return false;
            if (Int(material, P(slot, "mirror"), 0) != 0) return false;
            if (Differs(material, P(slot, "depth"), 0f)) return false;
            var texture = Tex(material, P(slot, "texture"));
            if (Int(material, P(slot, "uv"), 0) != 0) return false;

            var scale = (Vector2)Vec(material, P(slot, "scale"), Vector4.one);
            int aspect = Int(material, P(slot, "aspect"), 0);
            if (texture != null && texture.width > 0 && texture.height > 0) scale = ApplyAspect(scale, aspect, texture.width, texture.height);
            var dUv = DecalUv(
                uv0,
                (Vector2)Vec(material, P(slot, "position"), new Vector4(0.5f, 0.5f, 0f, 0f)),
                Float(material, P(slot, "rotation"), 0f),
                scale,
                Vec(material, P(slot, "side"), Vector4.zero),
                Int(material, P(slot, "symmetry"), 0));

            var color = material.HasProperty(P(slot, "color")) ? material.GetColor(P(slot, "color")) : Color.white;
            float a = color.a;
            if (texture != null)
            {
                var st = St(material, P(slot, "texture"));
                a *= TexelSampler.Sample(texture, new Vector2(dUv.x * st.x + st.z, dUv.y * st.y + st.w)).a;
            }
            // 共有マスク（R/G/B/A = Decal 0〜3 など、枠ごとに選んだチャンネル）
            var maskTexture = Tex(material, "_DecalMask");
            int channel = Int(material, P(slot, "maskChannel"), slot);
            if (maskTexture != null && channel <= 3 && Int(material, "_DecalMaskUV", 0) == 0)
            {
                var st = St(material, "_DecalMask");
                a *= TexelSampler.Sample(maskTexture, new Vector2(uv0.x * st.x + st.z, uv0.y * st.y + st.w))[Mathf.Clamp(channel, 0, 3)];
            }
            // タイルしないなら枠の外は 0
            if (!On(material, P(slot, "tiled")) && (dUv.x < 0f || dUv.x > 1f || dUv.y < 0f || dUv.y > 1f)) a = 0f;
            // 裏面だけに出すデカールは、表面のクリックでは 0
            if (Int(material, P(slot, "face"), 0) == 2) a = 0f;
            a *= Mathf.Clamp01(Float(material, P(slot, "blendAlpha"), 1f));
            alpha = Mathf.Clamp01(Float(material, P(slot, "intensity"), 1f) * a);
            blend = Int(material, P(slot, "blend"), BlendReplace);
            return true;
        }

        /// <summary>
        /// Poiyomi の decalUV（時間の回転・視差・ミラーは除く）: 対称 → 中心回りに回す → 位置と大きさ・サイドオフセットの枠を 0..1 に写す。
        /// side は (左, 右, 下, 上)
        /// </summary>
        internal static Vector2 DecalUv(Vector2 uv, Vector2 position, float rotationDegrees, Vector2 scale, Vector4 side, int symmetryMode)
        {
            var so = new Vector4(-side.x, side.y, -side.z, side.w);
            var centerOffset = new Vector2((so.x + so.y) / 2f, (so.z + so.w) / 2f);
            if (symmetryMode == 1) uv.x = Mathf.Abs(uv.x - 0.5f) + 0.5f;
            if (symmetryMode == 2 && uv.x < 0.5f) uv.x += 0.5f;
            var center = position + centerOffset;
            float theta = rotationDegrees * Mathf.Deg2Rad;
            float cs = Mathf.Cos(theta), sn = Mathf.Sin(theta);
            uv = new Vector2(
                (uv.x - center.x) * cs - (uv.y - center.y) * sn + center.x,
                (uv.x - center.x) * sn + (uv.y - center.y) * cs + center.y);
            var min = -scale / 2f + position + new Vector2(so.x, so.z);
            var max = scale / 2f + position + new Vector2(so.y, so.w);
            return new Vector2(Remap(uv.x, min.x, max.x), Remap(uv.y, min.y, max.y));
        }

        /// <summary>Poiyomi の ApplyAspectRatio（1 = 縮めて合わせる、2 = 広げて合わせる）</summary>
        internal static Vector2 ApplyAspect(Vector2 scale, int mode, int width, int height)
        {
            if (mode == 1) return width > height ? scale * new Vector2(1f, height / (float)width) : scale * new Vector2(width / (float)height, 1f);
            if (mode == 2) return width > height ? scale * new Vector2(width / (float)height, 1f) : scale * new Vector2(1f, height / (float)width);
            return scale;
        }

        private static float Remap(float v, float a, float b) => Mathf.Abs(b - a) < 1e-8f ? 0f : (v - a) / (b - a);

        // ── プロパティの読み取り（無ければ既定値） ──

        private static bool On(Material m, string name) => name != null && m.HasProperty(name) && m.GetFloat(name) >= 0.5f;
        private static bool Differs(Material m, string name, float value) => name != null && m.HasProperty(name) && Mathf.Abs(m.GetFloat(name) - value) > 1e-4f;
        private static int Int(Material m, string name, int fallback) => name != null && m.HasProperty(name) ? Mathf.RoundToInt(m.GetFloat(name)) : fallback;
        private static float Float(Material m, string name, float fallback) => name != null && m.HasProperty(name) ? m.GetFloat(name) : fallback;
        private static Vector4 Vec(Material m, string name, Vector4 fallback) => name != null && m.HasProperty(name) ? m.GetVector(name) : fallback;
        private static Texture Tex(Material m, string name) => name != null && m.HasProperty(name) ? m.GetTexture(name) : null;

        /// <summary>テクスチャの Tiling (xy) / Offset (zw)</summary>
        private static Vector4 St(Material m, string name)
        {
            var scale = m.GetTextureScale(name);
            var offset = m.GetTextureOffset(name);
            return new Vector4(scale.x, scale.y, offset.x, offset.y);
        }
    }
}
