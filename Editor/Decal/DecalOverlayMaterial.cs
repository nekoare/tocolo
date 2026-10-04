using System;
using System.Collections.Generic;
using System.Reflection;
using Nekoare.ClickRecolor.Editor.NDMF;
using Nekoare.ClickRecolor.Editor.Picking;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.Decal
{
    /// <summary>
    /// 「画像を入れる」第 2 段（重ね貼り。設計 docs/plans/2026-10-04-tocolo-decal-overlay-design.md）の、貼り方の判定と重ね貼り用マテリアル。
    /// 焼き込みは貼る場所のテクスチャ密度より細かくなれないので、lilToon のマテリアルでは元マテリアルの複製（透過）で画像を直接サンプリングする。
    /// Tocolo は lilToon にコンパイル時に依存しない（lilToon が無いプロジェクトでも動かすため）ので、lilToon のエディタ API はリフレクションで呼ぶ。
    /// 見つからない・失敗したときは呼び出し側が焼き込みに戻す
    /// </summary>
    internal static class DecalOverlayMaterial
    {
        /// <summary>他のシェーダーのロック済み版（最適化ツールが生成する固定シェーダー）の名前の頭。中身が固定なので透過版へ切り替えられない</summary>
        private const string LockedPrefix = "Hidden/Locked/";

        /// <summary>透過・カットアウトとみなす renderQueue の下限（Unity の AlphaTest）</summary>
        private const int AlphaTestQueue = 2450;

        // lilToon の RenderingMode.Transparent / TransparentMode.Normal / _TransparentMode（Multi）の値
        private const string RenderingModeTransparent = "Transparent";
        private const string TransparentModeNormal = "Normal";
        private const float MultiTransparentMode = 2f;

        /// <summary>
        /// UV0 依存のテクスチャ。重ね貼りでは UV0 が投影 UV に変わるので、元の位置を引けず誤った場所の値になる。
        /// null にしてシェーダーの既定値（多くは white / bump）で一様に効かせる
        /// </summary>
        private static readonly string[] Uv0Textures =
        {
            "_ShadowColorTex", "_ShadowStrengthMask", "_Shadow2ndColorTex", "_Shadow3rdColorTex",
            "_MatCapBlendMask", "_RimColorTex", "_AlphaMask", "_MainColorAdjustMask",
            // 以下は指示の一覧に無かったが lts.shader で UV0（または UV0 が既定）で引くもの
            "_Bump2ndScaleMask", "_AnisotropyTangentMap", "_AnisotropyScaleMask", "_AnisotropyShiftNoiseMask",
            "_BacklightColorTex", "_ShadowBorderMask", "_ShadowBlurMask", "_RimShadeMask",
            "_SmoothnessTex", "_MetallicGlossMap", "_ReflectionColorTex",
            "_MatCapBumpMap", "_MatCap2ndBlendMask", "_MatCap2ndBumpMap", "_GlitterColorTex",
            "_ParallaxMap", "_AudioLinkMask",
            "_DissolveMask", "_DissolveNoiseMask",
        };

        // 発光のマスク（_EmissionMap 等）はここに入れない。既定が white なので null にすると重ね貼り全体が均一に光る。DisableMaskedEmission で扱う

        /// <summary>透過のカットオフ。lilToon の Inspector が Transparent に切り替えたときと同じ値（lilPropertyGroupDrawerBaseSetting.cs）</summary>
        private const float TransparentCutoff = 0.001f;

        /// <summary>発光の UV モードのうち Rim（4）。UV0 を使わないのでマスク付きでも残せる</summary>
        private const int EmissionUvModeRim = 4;

        /// <summary>UV0 依存の機能のトグル（0 で OFF）</summary>
        private static readonly string[] Uv0Toggles =
        {
            "_UseBumpMap", "_UseMain2ndTex", "_UseMain3rdTex", "_AlphaMaskMode", "_MainGradationStrength",
            // 指示の一覧に無かったもの: 2nd 法線マップ・視差（UV0 で法線・高さを引く）
            "_UseBump2ndMap", "_UseParallax",
        };

        /// <summary>重ね貼りの対象にできる lilToon のマテリアルか（IsLilToonShaderName）</summary>
        internal static bool IsLilToon(Material m)
        {
            return m != null && m.shader != null && IsLilToonShaderName(m.shader.name);
        }

        /// <summary>
        /// 対象テクスチャの利用者として無視してよい lilToon の付属（FakeShadow・Overlay・FurOnly）か。
        /// 本体の上に足す付属の描画なので、これがあっても本体を重ね貼りすれば見た目は揃う
        /// </summary>
        internal static bool IsIgnorableLilToonAuxiliary(Material m)
        {
            return m != null && m.shader != null && IsIgnorableLilToonAuxiliaryShaderName(m.shader.name);
        }

        /// <summary>
        /// シェーダー名で、重ね貼りの対象にできる lilToon かを判定する。テストでシェーダーを作らずに確かめるため名前で受ける。
        /// ロック済み・Gem・Refraction は透過版にすると見た目が別物になる（Gem/Refraction は専用パス）ので除く。
        /// FakeShadow・Overlay・FurOnly も対象にはしない（IsIgnorableLilToonAuxiliaryShaderName で無視する）。Fur は透過版で本体の見た目が保てるので対象
        /// </summary>
        internal static bool IsLilToonShaderName(string shaderName)
        {
            if (!IsUnlockedLilToonShaderName(shaderName)) return false;
            if (ContainsAfterLastSeparator(shaderName, "Gem")) return false;
            if (ContainsAfterLastSeparator(shaderName, "Refraction")) return false;
            return !IsAuxiliaryName(shaderName);
        }

        internal static bool IsIgnorableLilToonAuxiliaryShaderName(string shaderName)
        {
            return IsUnlockedLilToonShaderName(shaderName) && IsAuxiliaryName(shaderName);
        }

        private static bool IsUnlockedLilToonShaderName(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            if (shaderName.StartsWith(LockedPrefix, StringComparison.Ordinal)) return false;
            return shaderName.Contains("lilToon");
        }

        /// <summary>lilShaderUtils.IsFakeShadowShaderName / IsOverlayShaderName と同じ判定＋ファーだけを描く FurOnly（本体は別マテリアル）</summary>
        private static bool IsAuxiliaryName(string shaderName)
        {
            return ContainsAfterLastSeparator(shaderName, "FakeShadow") || ContainsAfterLastSeparator(shaderName, "Overlay")
                || ContainsAfterLastSeparator(shaderName, "FurOnly");
        }

        // ── lilToon のエディタ API（リフレクション。1 回だけ探してキャッシュ）──
        private static bool _apiResolved;
        /// <summary>SetupMaterialWithRenderingMode(Material, RenderingMode, TransparentMode, bool isoutl, bool islite, bool istess, bool ismulti)</summary>
        private static MethodInfo _setupMethod;
        private static object _renderingTransparent;
        private static object _transparentNormal;
        /// <summary>lilMaterialUtils.SetupMultiMaterial(Material)（Multi の keyword を付け直す）。無くても単体の lilToon は扱える</summary>
        private static MethodInfo _setupMultiMethod;

        /// <summary>lilToon の透過化 API がリフレクションで見つかるか（キャッシュ。ドメインリロードで作り直される）</summary>
        internal static bool IsApiAvailable
        {
            get
            {
                ResolveApi();
                return _setupMethod != null;
            }
        }

        /// <summary>
        /// 透過化の API を探す。lilToonInspector の 3 引数版は、Inspector の静的フィールド（isOutl / isLite / isTess / isMulti）を
        /// 使い、さらに Inspector が複数シェーダーのマテリアルを同時に表示中（isMultiVariants）だと何もしないので、外から安全に呼べない。
        /// そのため lilToon 自身がプリセット適用で使う lilMaterialUtils の 7 引数版（internal）を第一にし、輪郭線・Lite・テッセレーション・Multi を
        /// こちらで元シェーダー名から決めて渡す。internal は改名されうるので、見つからなければ lilToonInspector の公開 7 引数版
        /// （isMultiVariants の判定だけ挟む同じ処理）に落とす
        /// </summary>
        private static void ResolveApi()
        {
            if (_apiResolved) return;
            _apiResolved = true;
            try
            {
                var renderingModeType = FindType("lilToon.RenderingMode");
                var transparentModeType = FindType("lilToon.TransparentMode");
                if (renderingModeType == null || !renderingModeType.IsEnum
                    || transparentModeType == null || !transparentModeType.IsEnum) return;
                if (!Enum.IsDefined(renderingModeType, RenderingModeTransparent)
                    || !Enum.IsDefined(transparentModeType, TransparentModeNormal)) return;

                var parameterTypes = new[]
                {
                    typeof(Material), renderingModeType, transparentModeType,
                    typeof(bool), typeof(bool), typeof(bool), typeof(bool),
                };
                const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var utils = FindType("lilToon.lilMaterialUtils");
                var setup = utils?.GetMethod("SetupMaterialWithRenderingMode", Flags, null, parameterTypes, null);
                if (setup == null)
                {
                    setup = FindType("lilToon.lilToonInspector")?.GetMethod("SetupMaterialWithRenderingMode",
                        BindingFlags.Static | BindingFlags.Public, null, parameterTypes, null);
                }
                if (setup == null) return;

                _renderingTransparent = Enum.Parse(renderingModeType, RenderingModeTransparent);
                _transparentNormal = Enum.Parse(transparentModeType, TransparentModeNormal);
                _setupMultiMethod = utils?.GetMethod("SetupMultiMaterial", BindingFlags.Static | BindingFlags.Public,
                    null, new[] { typeof(Material) }, null);
                _setupMethod = setup;
            }
            catch (Exception e)
            {
                // 型の読み込みに失敗するアセンブリがあっても重ね貼りを諦めるだけ（焼き込みに戻る）
                Debug.LogWarning($"[Tocolo] lilToon の API を探せませんでした: {e.Message}");
                _setupMethod = null;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try { type = assembly.GetType(fullName, false); }
                catch { continue; }
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>
        /// t の属するアバターのルート（NDMF の RuntimeUtil.FindAvatarInParents。VRCAvatarDescriptor・NDMFAvatarRoot 等）。
        /// 見つからなければ t そのもの（アバターの外に置いたコンポーネントは自分の子だけを見る）
        /// </summary>
        internal static Transform FindAvatarRoot(Transform t)
        {
            if (t == null) return null;
            var root = nadena.dev.ndmf.runtime.RuntimeUtil.FindAvatarInParents(t);
            return root != null ? root : t;
        }

        /// <summary>
        /// edit を edits に持つ（参照が一致する）ClickRecolor を、anyInAvatar の属するアバターの中から探す（非アクティブも含む）。無ければ null。
        /// 重ね貼りの判定（UseOverlay）の基準を、呼び手によらず常に編集を持つコンポーネントに揃えるために使う
        /// </summary>
        internal static ClickRecolor FindOwner(RecolorEdit edit, Transform anyInAvatar)
        {
            if (edit == null || anyInAvatar == null) return null;
            var root = FindAvatarRoot(anyInAvatar);
            foreach (var component in root.GetComponentsInChildren<ClickRecolor>(true))
            {
                if (component != null && component.edits != null && component.edits.Contains(edit)) return component;
            }
            return null;
        }

        /// <summary>
        /// この編集を重ね貼りにできるか: アバター内の全マテリアルのうち対象テクスチャ（edit.sourceTexture）を
        /// メインテクスチャに使うものが 1 つ以上あって、それがすべて重ね貼り用マテリアルを作れる種類（IsOverlayMaterial: lilToon か Poiyomi Toon。混在してよい）。
        /// 1 つでもそれ以外（Gem・Refraction・ロック済みを含む）が混ざると、
        /// そのマテリアルだけ焼き込みが要り、貼り方が揃わないので焼き込みにする。lilToon の付属（FakeShadow・Overlay・FurOnly）は無視する。
        /// 走査範囲はアバター全体（FindAvatarRoot）: 衣装 Prefab に付けたコンポーネントの編集でも、ルート直下の Renderer が同じテクスチャを使えば
        /// ビルドではそこにも貼るので、判定に入れないと貼れない Renderer が出る。
        /// 非アクティブの Renderer も見る: 非アクティブな衣装に同じテクスチャの lilToon 以外が混ざっていると、ビルドではその Renderer に
        /// 重ね貼りマテリアルを作れず、ゲーム内で衣装を出したときに画像が無くなる。そのときは編集ごと焼き込みにする。
        /// 除外リストは component（編集を持つコンポーネント。UseOverlay が揃える）のもの
        /// </summary>
        internal static bool CanOverlay(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null || edit.sourceTexture == null) return false;
            bool any = false;
            foreach (var renderer in FindAvatarRoot(component.transform).GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                if (component.IsExcluded(renderer)) continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (!MaterialTextureResolver.TryGetMainTexture(material, out var info)) continue;
                    if (info.texture != edit.sourceTexture) continue;
                    if (IsIgnorableLilToonAuxiliary(material)) continue;
                    if (!IsOverlayMaterial(material)) return false;
                    any = true;
                }
            }
            return any;
        }

        /// <summary>
        /// 重ね貼り用マテリアルを作れる種類か: lilToon（透過化の API が見つかるときだけ）か、Poiyomi Toon（DecalOverlayPoiyomi.IsTarget）
        /// </summary>
        internal static bool IsOverlayMaterial(Material material)
        {
            if (IsLilToon(material)) return IsApiAvailable;
            return DecalOverlayPoiyomi.IsTarget(material);
        }

        /// <summary>画面表示用の判定を覚えておく時間（秒）。マテリアルを差し替えてもこの時間のうちに反映される</summary>
        private const double UiCacheSeconds = 0.25;

        private static readonly Dictionary<(int component, string edit, bool use), (double time, bool value)> s_uiCache =
            new Dictionary<(int, string, bool), (double, bool)>();

        /// <summary>
        /// 画面表示（パネル・Inspector の書き出し欄）用の CanOverlay / UseOverlay。描画のたび（マウスを動かすだけでも）にアバター全体を
        /// 走査しないよう、UiCacheSeconds のあいだ結果を覚える（レビュー指摘 2026-10-04）。プレビュー・ビルド・書き出しの処理では使わないこと
        /// </summary>
        internal static bool CanOverlayForUi(ClickRecolor component, RecolorEdit edit) => CachedForUi(component, edit, false);

        /// <inheritdoc cref="CanOverlayForUi"/>
        internal static bool UseOverlayForUi(ClickRecolor component, RecolorEdit edit) => CachedForUi(component, edit, true);

        private static bool CachedForUi(ClickRecolor component, RecolorEdit edit, bool use)
        {
            if (component == null || edit == null) return false;
            var key = (component.GetInstanceID(), edit.id, use);
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (s_uiCache.TryGetValue(key, out var cached) && now - cached.time < UiCacheSeconds) return cached.value;
            bool value = use ? UseOverlay(component, edit) : CanOverlay(component, edit);
            if (s_uiCache.Count > 256) s_uiCache.Clear();
            s_uiCache[key] = (now, value);
            return value;
        }

        /// <summary>
        /// この編集を重ね貼りで表示するか（画像あり・なめらかに貼る ON・重ね貼りできる）。false なら焼き込み。
        /// component が edit を持っていなければ、component の属するアバターから持ち主（FindOwner）を探して基準にする
        /// （パネル・プレビュー・パイプライン・ビルドのどこから呼んでも、除外リストが同じコンポーネントのものになるように）
        /// </summary>
        internal static bool UseOverlay(ClickRecolor component, RecolorEdit edit)
        {
            if (edit == null || !edit.HasDecal || !edit.decalSmooth || component == null) return false;
            if (component.edits == null || !component.edits.Contains(edit))
            {
                component = FindOwner(edit, component.transform);
                if (component == null) return false;
            }
            return CanOverlay(component, edit);
        }

        /// <summary>
        /// material の法線マップ（lilToon の _UseBumpMap が ON で _BumpMap がある）と、その Tiling/Offset（xy = Tiling、zw = Offset）。
        /// 「ノーマルも反映」で画像の座標へ写し直す元。無ければ false
        /// </summary>
        internal static bool TryGetNormalSource(Material material, out Texture normal, out Vector4 tilingOffset)
        {
            normal = null;
            tilingOffset = new Vector4(1f, 1f, 0f, 0f);
            if (material == null || !material.HasProperty("_UseBumpMap") || !material.HasProperty("_BumpMap")) return false;
            if (material.GetFloat("_UseBumpMap") < 0.5f) return false;
            normal = material.GetTexture("_BumpMap");
            if (normal == null) return false;
            var scale = material.GetTextureScale("_BumpMap");
            var offset = material.GetTextureOffset("_BumpMap");
            tilingOffset = new Vector4(scale.x, scale.y, offset.x, offset.y);
            return true;
        }

        /// <summary>lilToon の _Bump2ndMap_UVMode の UV1。重ね貼りの頂点では UV1 が元の UV0 に置き換わっているので、元が UV1 の 2nd ノーマルは再現できない</summary>
        private const int Bump2ndUvModeUv1 = 1;

        /// <summary>
        /// material の 2nd ノーマル（lilToon の _UseBump2ndMap が ON で _Bump2ndMap がある）を重ね貼りでも残せるか。
        /// 残せるのは UV モードが UV0・UV2・UV3 のとき（UV0 は重ね貼りでは UV1 に入っている元の UV0 で引き直す）。
        /// mask は強さマスク（_Bump2ndScaleMask。メインの UV で引かれるので画像の座標へ写し直す元。無ければ null）と、その Tiling/Offset
        /// </summary>
        internal static bool TryGetBump2ndSource(Material material, out Texture mask, out Vector4 maskTilingOffset)
        {
            mask = null;
            maskTilingOffset = new Vector4(1f, 1f, 0f, 0f);
            if (material == null || !material.HasProperty("_UseBump2ndMap") || !material.HasProperty("_Bump2ndMap")) return false;
            if (material.GetFloat("_UseBump2ndMap") < 0.5f || material.GetTexture("_Bump2ndMap") == null) return false;
            if (material.HasProperty("_Bump2ndMap_UVMode") && Mathf.RoundToInt(material.GetFloat("_Bump2ndMap_UVMode")) == Bump2ndUvModeUv1) return false;
            if (material.HasProperty("_Bump2ndScaleMask"))
            {
                mask = material.GetTexture("_Bump2ndScaleMask");
                var scale = material.GetTextureScale("_Bump2ndScaleMask");
                var offset = material.GetTextureOffset("_Bump2ndScaleMask");
                maskTilingOffset = new Vector4(scale.x, scale.y, offset.x, offset.y);
            }
            return true;
        }

        /// <summary>
        /// 重ね貼り用のマテリアルを作る: 元の複製を lilToon の透過版（輪郭線なし）にし、UV0 依存の機能を外して _MainTex に画像を入れる。
        /// normalTexture（画像の座標へ写し直した法線マップ。「ノーマルも反映」）を渡し、元の法線マップが ON なら、それを _BumpMap に入れて法線マップを ON に戻す
        /// （_BumpScale は元のまま）。applyNormal（「ノーマルも反映」）なら、残せる 2nd ノーマル（TryGetBump2ndSource）も ON に戻し、UV0 で引いていたものは UV1（元の UV0）で引かせる。
        /// 強さマスクがある元では bump2ndMask（画像の座標へ写し直したマスク）が要り、無ければ 2nd ノーマルは外したまま。
        /// 元マテリアルは変えない。失敗したら null（呼び出し側が焼き込みに戻す）。返したマテリアルは HideAndDontSave なので呼び出し側が破棄する
        /// </summary>
        internal static Material Create(Material original, Texture decalTexture, Texture normalTexture = null,
            bool applyNormal = false, Texture bump2ndMask = null)
        {
            // Poiyomi は UV の付け替えで元の位置を読めるので、法線の写し直しは使わない
            if (DecalOverlayPoiyomi.IsTarget(original)) return DecalOverlayPoiyomi.Create(original, decalTexture, applyNormal);
            if (original == null || original.shader == null || !IsLilToon(original)) return null;
            if (!IsApiAvailable) return null;

            var originalShaderName = original.shader.name;
            // getter は m_CustomRenderQueue が -1 ならシェーダーの値を返す
            int originalQueue = original.renderQueue;

            // MaterialCloner は keyword 欠落（ピンク）対策で shader / keywords / renderQueue を明示的に写す
            var clone = MaterialCloner.Clone(original, "_decal");
            clone.hideFlags = HideFlags.HideAndDontSave;
            Material scratch = null;

            try
            {
                // lilToon の判定（lilShaderUtils.IsLiteShaderName 等）を写したもの。輪郭線は重ね貼りに要らないので常に外す
                bool isLite = IsLiteShaderName(originalShaderName);
                bool isTess = IsTessellationShaderName(originalShaderName);
                bool isMulti = ContainsAfterLastSeparator(originalShaderName, "Multi");
                if (isMulti)
                {
                    // Multi は引数の RenderingMode を見ず、マテリアルの _TransparentMode（2 = Transparent）で決める
                    if (_setupMultiMethod == null) { Object.DestroyImmediate(clone); return null; }
                    clone.SetFloat("_TransparentMode", MultiTransparentMode);
                }

                // lilToon の Setup は中で Undo.RecordObject(material) する。その記録がユーザーの操作（色の変更など）と同じ Undo の束に入ると、
                // Ctrl+Z で透過化だけが取り消され、画像の透明な所が黒い不透明で描かれる（実機 2026-10-04。ClearUndo で消し損ねることがあった）。
                // そこで Setup は使い捨ての複製（scratch）に掛け、その結果を複製し直したものを使う（使うマテリアルは Undo に一度も記録されない）。
                // scratch の記録は確定させて消してから破棄する（消し損ねても、破棄した物の記録は Undo で何もしない）
                scratch = clone;
                _setupMethod.Invoke(null, new[]
                {
                    scratch, _renderingTransparent, _transparentNormal,
                    (object)false, isLite, isTess, isMulti,
                });
                Undo.FlushUndoRecordObjects();
                Undo.ClearUndo(scratch);
                if (scratch.shader == null || !IsLilToon(scratch)) return null;
                clone = MaterialCloner.Clone(scratch, "");
                clone.name = scratch.name;
                clone.hideFlags = HideFlags.HideAndDontSave;
                Object.DestroyImmediate(scratch);
                scratch = null;

                // 透過パスは clip(a - _Cutoff) するので、既定 0.5 のままだと画像の縁が切れてなめらかにならない
                if (clone.HasProperty("_Cutoff")) clone.SetFloat("_Cutoff", TransparentCutoff);
                DisableMaskedEmission(original, clone, "_UseEmission", "_EmissionMap", "_EmissionBlendMask", "_EmissionMap_UVMode");
                DisableMaskedEmission(original, clone, "_UseEmission2nd", "_Emission2ndMap", "_Emission2ndBlendMask", "_Emission2ndMap_UVMode");
                DisableUv0Features(clone);
                clone.SetTexture("_MainTex", decalTexture);
                clone.SetTextureScale("_MainTex", Vector2.one);
                clone.SetTextureOffset("_MainTex", Vector2.zero);
                if (normalTexture != null && TryGetNormalSource(original, out _, out _))
                {
                    // 写し直した法線マップは画像の座標なので Tiling/Offset は使わない
                    clone.SetFloat("_UseBumpMap", 1f);
                    clone.SetTexture("_BumpMap", normalTexture);
                    clone.SetTextureScale("_BumpMap", Vector2.one);
                    clone.SetTextureOffset("_BumpMap", Vector2.zero);
                }
                if (applyNormal) RestoreBump2nd(original, clone, bump2ndMask);

                // Multi は機能の ON/OFF を keyword で持つので、プロパティを変えた後に付け直す
                if (isMulti) _setupMultiMethod.Invoke(null, new object[] { clone });

                // Setup は元の m_CustomRenderQueue を書き戻すので、不透明の元からは不透明の queue（2000 等）が残る。
                // 透過版シェーダーの既定（lilToon は AlphaTest+10）に戻して元より後ろに描かせる
                if (clone.renderQueue < AlphaTestQueue) clone.renderQueue = -1;
                // 元が透過・カットアウト（髪など）なら同じ queue だと順番が入れ替わるので 1 つ後ろにする
                if (originalQueue >= AlphaTestQueue) clone.renderQueue = Math.Max(clone.renderQueue, originalQueue + 1);
                return clone;
            }
            catch (Exception e)
            {
                // プレビューの作り直しのたびに出ると溢れるので、ドメインリロードまで 1 回だけ
                if (!_createWarned)
                {
                    _createWarned = true;
                    Debug.LogWarning($"[Tocolo] 重ね貼り用のマテリアルを作れませんでした（焼き込みで貼ります）: {(e is TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : e.Message)}");
                }
                if (clone != null) Object.DestroyImmediate(clone);
                return null;
            }
            finally
            {
                // Setup に失敗して途中で抜けたときの使い捨ての複製（作り直した後は null にしてある）
                if (scratch != null) Object.DestroyImmediate(scratch);
            }
        }

        private static bool _createWarned;

        /// <summary>
        /// 2nd ノーマル（布の織り目などの繰り返し模様が多い）を重ね貼りでも効かせる。画像の座標へ写すと繰り返しの細かさに解像度が足りないので、
        /// 元の画像・Tiling のまま元の UV で引かせる: UV0 は重ね貼りの頂点では UV1 に入っているので UV モードを UV1 にする（UV2・UV3 は元のまま）
        /// </summary>
        private static void RestoreBump2nd(Material original, Material clone, Texture bump2ndMask)
        {
            if (!TryGetBump2ndSource(original, out var mask, out _)) return;
            if (mask != null && bump2ndMask == null) return;
            clone.SetFloat("_UseBump2ndMap", 1f);
            if (clone.HasProperty("_Bump2ndMap_UVMode") && Mathf.RoundToInt(clone.GetFloat("_Bump2ndMap_UVMode")) == 0)
            {
                clone.SetFloat("_Bump2ndMap_UVMode", Bump2ndUvModeUv1);
            }
            if (mask != null)
            {
                // 写し直したマスクは画像の座標なので Tiling/Offset は使わない
                clone.SetTexture("_Bump2ndScaleMask", bump2ndMask);
                clone.SetTextureScale("_Bump2ndScaleMask", Vector2.one);
                clone.SetTextureOffset("_Bump2ndScaleMask", Vector2.zero);
            }
        }

        /// <summary>
        /// マスク（テクスチャ）付きの発光は UV0 で位置を引くので重ね貼りでは再現できない。マスクを null にすると既定の white で
        /// 重ね貼り全体が光るため、発光そのものを OFF にする。マスクの無い一様な発光と、ブレンドマスクが無い Rim モード（発光のテクスチャが UV0 を使わない）は残す
        /// </summary>
        private static void DisableMaskedEmission(Material original, Material clone, string toggle, string map, string blendMask, string uvMode)
        {
            if (!original.HasProperty(toggle) || original.GetFloat(toggle) != 1f) return;
            // ブレンドマスクは UV モードに関係なく常にメインの UV で引く（lil_common_frag.hlsl）ので、あれば必ず OFF
            bool blendMasked = original.HasProperty(blendMask) && original.GetTexture(blendMask) != null;
            // 発光のテクスチャは Rim モードなら UV0 を使わないので残せる
            bool mapMasked = original.HasProperty(map) && original.GetTexture(map) != null
                && !(original.HasProperty(uvMode) && Mathf.RoundToInt(original.GetFloat(uvMode)) == EmissionUvModeRim);
            if (blendMasked || mapMasked) clone.SetFloat(toggle, 0f);
        }

        /// <summary>UV0 が投影 UV に変わるので、元の UV0 で引いていた機能を外す（プロパティがあるものだけ）</summary>
        private static void DisableUv0Features(Material m)
        {
            foreach (var name in Uv0Toggles)
            {
                if (m.HasProperty(name)) m.SetFloat(name, 0f);
            }
            foreach (var name in Uv0Textures)
            {
                if (m.HasProperty(name)) m.SetTexture(name, null);
            }
            // 色調補正（HSVG）は画像に掛けない。メインテクスチャの UV スクロールは画像を流してしまうので止める
            if (m.HasProperty("_MainTexHSVG")) m.SetVector("_MainTexHSVG", new Vector4(0f, 1f, 1f, 1f));
            if (m.HasProperty("_MainTex_ScrollRotate")) m.SetVector("_MainTex_ScrollRotate", Vector4.zero);
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            // UDIM のタイル破棄は UV0 のタイルで面を捨てる。投影 UV は箱からはみ出した三角形で 0..1 の外になるので、重ね貼りが欠けないよう切る
            // ピクセルモード（_UDIMDiscardMode = 1）の破棄は _UDIMDiscardCompile を見ないので、頂点モード（0）に戻してから Compile = 0 で止める
            if (m.HasProperty("_UDIMDiscardMode")) m.SetFloat("_UDIMDiscardMode", 0f);
            if (m.HasProperty("_UDIMDiscardCompile")) m.SetFloat("_UDIMDiscardCompile", 0f);
            // ラメの UV モード 1 は UV1 で引くが、重ね貼りでは UV1 が元の UV0 に置き換わっているので正しい位置に出ない。ラメごと外す
            if (m.HasProperty("_GlitterUVMode") && Mathf.RoundToInt(m.GetFloat("_GlitterUVMode")) == 1 && m.HasProperty("_UseGlitter"))
            {
                m.SetFloat("_UseGlitter", 0f);
            }
        }

        // ── lilToon の lilShaderUtils から写したシェーダー名の判定（lilToon に依存しないため）──

        private static bool IsLiteShaderName(string shaderName)
        {
            var separatorIndex = shaderName.LastIndexOf('/');
            if (separatorIndex == -1 || separatorIndex + 1 == shaderName.Length) return false;
            if (shaderName.IndexOf("Lite", separatorIndex + 1, StringComparison.Ordinal) != -1) return true;
            // Hidden/*LIL_SHADER_NAME*/Lite/Cutout などのカスタムシェーダー名
            var partIndex = shaderName.LastIndexOf("/Lite/", StringComparison.Ordinal);
            return partIndex != -1 && partIndex + 5 == separatorIndex;
        }

        private static bool IsTessellationShaderName(string shaderName)
        {
            var separatorIndex = shaderName.LastIndexOf('/');
            if (separatorIndex == -1 || separatorIndex + 1 == shaderName.Length) return false;
            if (shaderName.IndexOf("Tessellation", separatorIndex + 1, StringComparison.Ordinal) > 0) return true;
            // Hidden/*LIL_SHADER_NAME*/Tessellation/Opaque などのカスタムシェーダー名
            var partIndex = shaderName.LastIndexOf("/Tessellation/", StringComparison.Ordinal);
            return partIndex != -1 && partIndex + 13 == separatorIndex;
        }

        private static bool ContainsAfterLastSeparator(string shaderName, string subName)
        {
            var separatorIndex = shaderName.LastIndexOf('/');
            if (separatorIndex == -1 || separatorIndex + 1 == shaderName.Length) return false;
            return shaderName.IndexOf(subName, separatorIndex + 1, StringComparison.Ordinal) != -1;
        }
    }
}
