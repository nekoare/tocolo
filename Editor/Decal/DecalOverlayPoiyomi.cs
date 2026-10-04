using System;
using System.Collections.Generic;
using System.Globalization;
using Nekoare.ClickRecolor.Editor.NDMF;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.Decal
{
    /// <summary>
    /// 「画像を入れる」第 2 段（重ね貼り）の Poiyomi Toon 用のマテリアル（設計 docs/plans/2026-10-04-tocolo-decal-poiyomi-design.md）。
    /// 重ね貼りの面は UV0 が画像の座標、UV1 が元の UV0 なので、UV0 で読んでいたテクスチャを UV1 に付け替えれば、
    /// ノーマル・影・Matcap などが服と同じ位置で画像の上にも効く（Poiyomi はテクスチャごとに UV を選べる）。
    /// Tocolo は Poiyomi にコンパイル時に依存しないので、プロパティ名と属性・説明文から読み取る。読めなければ null（呼び出し側が焼き込みに戻す）
    /// </summary>
    internal static class DecalOverlayPoiyomi
    {
        /// <summary>対象のシェーダー名（ロックされていない本体だけ。派生の Two Pass 等は確かめていないので除く）</summary>
        internal const string ShaderName = ".poiyomi/Poiyomi Toon";

        /// <summary>テクスチャの UV の選択の属性の頭（ThryWideEnum(UV0, 0, UV1, 1, UV2, 2, UV3, 3, ...)）</summary>
        private const string UvSelectorAttributePrefix = "ThryWideEnum(UV0, 0, UV1, 1";

        private const string MainTexUv = "_MainTexUV";

        /// <summary>描画モード TransClipping（半透明で深度も書く。lilToon の透過と同じ扱い）の _Mode の値</summary>
        private const int TransClippingMode = 9;

        /// <summary>透過のカットオフ。縁が切れないよう lilToon の重ね貼りと同じ値にする</summary>
        private const float TransparentCutoff = 0.001f;

        /// <summary>透過・カットアウトとみなす renderQueue の下限（Unity の AlphaTest）</summary>
        private const int AlphaTestQueue = 2450;

        /// <summary>UV のチャンネルごとの全体設定（n = 0..3 の番号が後ろに付く）。0 番を 1 番へ写す</summary>
        private static readonly string[] UvSettingVectors = { "_UVSettingsTiling", "_UVSettingsOffset", "_UVSettingsPan" };
        private static readonly string[] UvSettingFloats = { "_UVSettingsAngle", "_UVSettingsRotate" };

        /// <summary>
        /// 画像の色を変えてしまう機能のトグル（切る）。服の色調補正・RGB マスク・服側のデカールなどが画像の上に掛かると、選んだ画像の見た目にならない
        /// （lilToon の重ね貼りで 2nd/3rd・色調補正を切るのと同じ）
        /// </summary>
        private static readonly string[] ColorChangingToggles =
        {
            "_MainColorAdjustToggle", "_ColorGradingToggle", "_RGBMaskEnabled",
            "_DecalEnabled", "_DecalEnabled1", "_DecalEnabled2", "_DecalEnabled3", "_EnableALDecal",
            "_MainVertexColoringEnabled", "_EnableFlipbook", "_BackFaceEnabled",
        };

        /// <summary>「ノーマルも反映」OFF のとき 0 にする法線の強さ（メイン・2nd・Detail）</summary>
        private static readonly string[] NormalStrengths = { "_BumpScale", "_Bump2ndScale", "_DetailNormalMapScale" };

        /// <summary>シェーダー名で、重ね貼りの対象にできる Poiyomi かを判定する</summary>
        internal static bool IsTargetShaderName(string shaderName) => shaderName == ShaderName;

        /// <summary>重ね貼りの対象にできる Poiyomi のマテリアルか（ロックされていない本体で、メインテクスチャを UV0 で読む）</summary>
        internal static bool IsTarget(Material m)
        {
            if (m == null || m.shader == null || !IsTargetShaderName(m.shader.name)) return false;
            // Tocolo の範囲は UV0 が前提。メインテクスチャを別の UV で読むマテリアルは扱えない
            return !m.HasProperty(MainTexUv) || Mathf.RoundToInt(m.GetFloat(MainTexUv)) == 0;
        }

        /// <summary>
        /// 重ね貼り用のマテリアルを作る（元マテリアルは変えない）。applyNormal（「ノーマルも反映」）が false なら法線マップを効かせない。
        /// 失敗したら null。返したマテリアルは HideAndDontSave なので呼び出し側が破棄する
        /// </summary>
        internal static Material Create(Material original, Texture decalTexture, bool applyNormal)
        {
            if (!IsTarget(original)) return null;
            int originalQueue = original.renderQueue;
            var clone = MaterialCloner.Clone(original, "_decal");
            clone.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                var shader = clone.shader;
                if (!ApplyRenderingMode(clone, shader, TransClippingMode))
                {
                    Object.DestroyImmediate(clone);
                    return null;
                }
                if (clone.HasProperty("_Cutoff")) clone.SetFloat("_Cutoff", TransparentCutoff);
                // 元が透過・カットアウト（髪など）なら同じ queue だと順番が入れ替わるので 1 つ後ろにする（lilToon の重ね貼りと同じ）
                if (originalQueue >= AlphaTestQueue) clone.renderQueue = Math.Max(clone.renderQueue, originalQueue + 1);

                RemapUvSelectors(clone, shader);
                MoveUvSettingsToUv1(clone);

                clone.SetTexture("_MainTex", decalTexture);
                clone.SetTextureScale("_MainTex", Vector2.one);
                clone.SetTextureOffset("_MainTex", Vector2.zero);
                SetVectorIfExists(clone, "_MainTexPan", Vector4.zero);
                SetFloatIfExists(clone, "_MainTexStochastic", 0f);
                SetFloatIfExists(clone, "_MainIgnoreTexAlpha", 0f);
                if (clone.HasProperty("_Color")) clone.SetColor("_Color", Color.white);

                // 輪郭線は重ね貼りに要らない（二重に描かれる）。Early Z は透過と合わない
                SetToggle(clone, shader, "_EnableOutlines", false);
                SetToggle(clone, shader, "_RenderingEarlyZEnabled", false);

                foreach (var name in ColorChangingToggles) SetToggle(clone, shader, name, false);
                // Detail は色だけ切る（Detail の法線は布目などなので残す）。Tint は既定（白・強さ 0）に戻す
                SetFloatIfExists(clone, "_DetailTexIntensity", 0f);
                if (clone.HasProperty("_MainTintColor")) clone.SetColor("_MainTintColor", new Color(1f, 1f, 1f, 0f));
                if (!applyNormal)
                {
                    foreach (var name in NormalStrengths) SetFloatIfExists(clone, name, 0f);
                }
                return clone;
            }
            catch (Exception e)
            {
                if (!_createWarned)
                {
                    _createWarned = true;
                    Debug.LogWarning($"[Tocolo] Poiyomi の重ね貼り用のマテリアルを作れませんでした（焼き込みで貼ります）: {e.Message}");
                }
                Object.DestroyImmediate(clone);
                return null;
            }
        }

        private static bool _createWarned;

        /// <summary>
        /// 描画モード mode を、_Mode の説明文にある ThryEditor の on_value_actions（その値のときに書き換えるプロパティ・render_queue・render_type）の通りに当てる。
        /// 版ごとに値を持たずに済むよう説明文から読む。読めなければ false
        /// </summary>
        internal static bool ApplyRenderingMode(Material material, Shader shader, int mode)
        {
            int index = shader.FindPropertyIndex("_Mode");
            if (index < 0) return false;
            var actions = ParseModeActions(shader.GetPropertyDescription(index), mode);
            if (actions == null || actions.Count == 0) return false;
            material.SetFloat("_Mode", mode);
            foreach (var (key, value) in actions)
            {
                if (key == "render_queue")
                {
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int queue)) material.renderQueue = queue;
                }
                else if (key == "render_type")
                {
                    material.SetOverrideTag("RenderType", value);
                }
                else if (material.HasProperty(key)
                         && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                {
                    material.SetFloat(key, number);
                }
            }
            return true;
        }

        /// <summary>
        /// _Mode の説明文から、値 mode の on_value_actions の SET_PROPERTY（data:KEY=VALUE）を取り出す。見つからなければ null。
        /// 書式は「{value:9,actions:[{type:SET_PROPERTY,data:render_queue=2460}, ...]}」
        /// </summary>
        internal static List<(string key, string value)> ParseModeActions(string description, int mode)
        {
            if (string.IsNullOrEmpty(description)) return null;
            string head = $"{{value:{mode},actions:[";
            int start = description.IndexOf(head, StringComparison.Ordinal);
            if (start < 0) return null;
            start += head.Length;
            int end = description.IndexOf("]}", start, StringComparison.Ordinal);
            if (end < 0) return null;
            var block = description.Substring(start, end - start);
            var result = new List<(string, string)>();
            const string DataHead = "data:";
            int at = 0;
            while ((at = block.IndexOf(DataHead, at, StringComparison.Ordinal)) >= 0)
            {
                at += DataHead.Length;
                int close = block.IndexOf('}', at);
                if (close < 0) break;
                var pair = block.Substring(at, close - at);
                int eq = pair.IndexOf('=');
                if (eq > 0) result.Add((pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim()));
                at = close;
            }
            return result;
        }

        /// <summary>
        /// UV の選択（属性が UvSelectorAttributePrefix で始まるプロパティ）のうち UV0 のものを UV1（重ね貼りの面では元の UV0）にする。メインテクスチャは除く。
        /// UV1 を選んでいたものは元の UV1 が無いので元の UV0 で読まれる（まれなので許容）
        /// </summary>
        internal static void RemapUvSelectors(Material material, Shader shader)
        {
            foreach (var (name, type) in UvSelectorsOf(shader))
            {
                if (name == MainTexUv) continue;
                if (type == ShaderPropertyType.Int)
                {
                    if (material.GetInteger(name) == 0) material.SetInteger(name, 1);
                }
                else if (Mathf.RoundToInt(material.GetFloat(name)) == 0)
                {
                    material.SetFloat(name, 1f);
                }
            }
        }

        /// <summary>
        /// シェーダーごとの UV の選択のプロパティ（名前と型）。Poiyomi はプロパティが数千あり、属性の取得は配列を作るので、
        /// 重ね貼りマテリアルを作るたび（上流の色スライダーの 1 目盛りごと）に数え直さないようシェーダーごとに覚える（ドメインリロードで消えてよい）
        /// </summary>
        private static List<(string name, ShaderPropertyType type)> UvSelectorsOf(Shader shader)
        {
            if (s_uvSelectors.TryGetValue(shader, out var list)) return list;
            list = new List<(string, ShaderPropertyType)>();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                var type = shader.GetPropertyType(i);
                if (type != ShaderPropertyType.Int && type != ShaderPropertyType.Float && type != ShaderPropertyType.Range) continue;
                if (IsUvSelector(shader, i)) list.Add((shader.GetPropertyName(i), type));
            }
            s_uvSelectors[shader] = list;
            return list;
        }

        private static readonly Dictionary<Shader, List<(string name, ShaderPropertyType type)>> s_uvSelectors =
            new Dictionary<Shader, List<(string, ShaderPropertyType)>>();

        private static bool IsUvSelector(Shader shader, int index)
        {
            foreach (var attribute in shader.GetPropertyAttributes(index))
            {
                if (attribute.StartsWith(UvSelectorAttributePrefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>UV のチャンネルごとの全体設定を 0 番から 1 番へ写し、0 番（画像の座標）は既定に戻す</summary>
        private static void MoveUvSettingsToUv1(Material material)
        {
            foreach (var head in UvSettingVectors)
            {
                string from = head + "0", to = head + "1";
                if (!material.HasProperty(from) || !material.HasProperty(to)) continue;
                material.SetVector(to, material.GetVector(from));
                material.SetVector(from, head == "_UVSettingsTiling" ? new Vector4(1f, 1f, 0f, 0f) : Vector4.zero);
            }
            foreach (var head in UvSettingFloats)
            {
                string from = head + "0", to = head + "1";
                if (!material.HasProperty(from) || !material.HasProperty(to)) continue;
                material.SetFloat(to, material.GetFloat(from));
                material.SetFloat(from, 0f);
            }
        }

        /// <summary>
        /// トグルのプロパティを変え、属性 ThryToggle(KEYWORD) があればキーワードも合わせる（キーワードで機能を切り替えるものがあるため）
        /// </summary>
        internal static void SetToggle(Material material, Shader shader, string name, bool on)
        {
            int index = shader.FindPropertyIndex(name);
            if (index < 0) return;
            material.SetFloat(name, on ? 1f : 0f);
            foreach (var attribute in shader.GetPropertyAttributes(index))
            {
                const string Head = "ThryToggle(";
                if (!attribute.StartsWith(Head, StringComparison.Ordinal) || !attribute.EndsWith(")", StringComparison.Ordinal)) continue;
                var keyword = attribute.Substring(Head.Length, attribute.Length - Head.Length - 1).Split(',')[0].Trim();
                if (string.IsNullOrEmpty(keyword)) continue;
                if (on) material.EnableKeyword(keyword);
                else material.DisableKeyword(keyword);
            }
        }

        private static void SetFloatIfExists(Material material, string name, float value)
        {
            if (material.HasProperty(name)) material.SetFloat(name, value);
        }

        private static void SetVectorIfExists(Material material, string name, Vector4 value)
        {
            if (material.HasProperty(name)) material.SetVector(name, value);
        }
    }
}
