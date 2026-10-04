using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Decal;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 「画像を入れる」第 2 段（重ね貼り）の貼り方の判定と重ね貼りマテリアル（DecalOverlayMaterial）のテスト。
    /// lilToon の透過化はリフレクションで呼ぶので、lilToon が入っていない環境ではその部分を Ignore する
    /// </summary>
    public class DecalOverlayMaterialTests
    {
        private readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private T Track<T>(T obj) where T : Object
        {
            _cleanup.Add(obj);
            return obj;
        }

        private static Shader FindLilToonOrIgnore()
        {
            var shader = Shader.Find("lilToon");
            if (shader == null) Assert.Ignore("lilToon が入っていない環境です");
            return shader;
        }

        [Test]
        public void ノーマルも反映_元の法線マップがONなら写し直した法線マップを入れてONにする()
        {
            var shader = FindLilToonOrIgnore();
            var decal = Track(new Texture2D(8, 8));
            var remapped = Track(new Texture2D(8, 8));
            var original = Track(new Material(shader));
            original.SetTexture("_MainTex", Track(new Texture2D(4, 4)));
            original.SetFloat("_UseBumpMap", 1f);
            original.SetTexture("_BumpMap", Track(new Texture2D(4, 4)));
            original.SetTextureScale("_BumpMap", new Vector2(2f, 2f));
            original.SetFloat("_BumpScale", 0.7f);

            var clone = Track(DecalOverlayMaterial.Create(original, decal, remapped));
            Assert.That(clone, Is.Not.Null);
            Assert.That(clone.GetFloat("_UseBumpMap"), Is.EqualTo(1f));
            Assert.That(clone.GetTexture("_BumpMap"), Is.SameAs(remapped));
            Assert.That(clone.GetTextureScale("_BumpMap"), Is.EqualTo(Vector2.one), "写し直した法線マップは画像の座標なので Tiling は使わない");
            Assert.That(clone.GetFloat("_BumpScale"), Is.EqualTo(0.7f).Within(1e-5f), "強さは元のまま");

            // 元が法線マップ OFF なら渡しても OFF のまま
            original.SetFloat("_UseBumpMap", 0f);
            var off = Track(DecalOverlayMaterial.Create(original, decal, remapped));
            Assert.That(off.GetFloat("_UseBumpMap"), Is.EqualTo(0f));
        }

        [Test]
        public void TryGetNormalSource_法線マップがONでテクスチャがあるときだけ()
        {
            var shader = FindLilToonOrIgnore();
            var material = Track(new Material(shader));
            Assert.That(DecalOverlayMaterial.TryGetNormalSource(material, out _, out _), Is.False);
            material.SetFloat("_UseBumpMap", 1f);
            Assert.That(DecalOverlayMaterial.TryGetNormalSource(material, out _, out _), Is.False, "テクスチャが無ければ写さない");
            var bump = Track(new Texture2D(4, 4));
            material.SetTexture("_BumpMap", bump);
            material.SetTextureOffset("_BumpMap", new Vector2(0.25f, 0.5f));
            Assert.That(DecalOverlayMaterial.TryGetNormalSource(material, out var normal, out var st), Is.True);
            Assert.That(normal, Is.SameAs(bump));
            Assert.That(st, Is.EqualTo(new Vector4(1f, 1f, 0.25f, 0.5f)));
        }

        [Test]
        public void ノーマルも反映_2ndノーマルは元の画像のままUV0をUV1に替えて残す()
        {
            var shader = FindLilToonOrIgnore();
            var decal = Track(new Texture2D(8, 8));
            var bump2nd = Track(new Texture2D(4, 4));
            var original = Track(new Material(shader));
            original.SetTexture("_MainTex", Track(new Texture2D(4, 4)));
            original.SetFloat("_UseBump2ndMap", 1f);
            original.SetTexture("_Bump2ndMap", bump2nd);
            original.SetTextureScale("_Bump2ndMap", new Vector2(8f, 8f));
            original.SetFloat("_Bump2ndMap_UVMode", 0f);

            var clone = Track(DecalOverlayMaterial.Create(original, decal, null, applyNormal: true));
            Assert.That(clone, Is.Not.Null);
            Assert.That(clone.GetFloat("_UseBump2ndMap"), Is.EqualTo(1f));
            Assert.That(clone.GetTexture("_Bump2ndMap"), Is.SameAs(bump2nd), "元の画像のまま");
            Assert.That(clone.GetTextureScale("_Bump2ndMap"), Is.EqualTo(new Vector2(8f, 8f)), "Tiling も元のまま");
            Assert.That(clone.GetFloat("_Bump2ndMap_UVMode"), Is.EqualTo(1f), "UV1 に入っている元の UV0 で引く");

            // 「ノーマルも反映」OFF なら外したまま
            var off = Track(DecalOverlayMaterial.Create(original, decal, null, applyNormal: false));
            Assert.That(off.GetFloat("_UseBump2ndMap"), Is.EqualTo(0f));

            // UV2 は元のまま
            original.SetFloat("_Bump2ndMap_UVMode", 2f);
            var uv2 = Track(DecalOverlayMaterial.Create(original, decal, null, applyNormal: true));
            Assert.That(uv2.GetFloat("_UseBump2ndMap"), Is.EqualTo(1f));
            Assert.That(uv2.GetFloat("_Bump2ndMap_UVMode"), Is.EqualTo(2f));

            // 元が UV1 なら UV1 が置き換わっているので再現できず外す
            original.SetFloat("_Bump2ndMap_UVMode", 1f);
            var uv1 = Track(DecalOverlayMaterial.Create(original, decal, null, applyNormal: true));
            Assert.That(uv1.GetFloat("_UseBump2ndMap"), Is.EqualTo(0f));
        }

        [Test]
        public void ノーマルも反映_2ndノーマルの強さマスクは写し直したものが無ければ外す()
        {
            var shader = FindLilToonOrIgnore();
            var decal = Track(new Texture2D(8, 8));
            var remappedMask = Track(new Texture2D(8, 8));
            var original = Track(new Material(shader));
            original.SetTexture("_MainTex", Track(new Texture2D(4, 4)));
            original.SetFloat("_UseBump2ndMap", 1f);
            original.SetTexture("_Bump2ndMap", Track(new Texture2D(4, 4)));
            original.SetTexture("_Bump2ndScaleMask", Track(new Texture2D(4, 4)));
            original.SetTextureScale("_Bump2ndScaleMask", new Vector2(2f, 2f));

            var without = Track(DecalOverlayMaterial.Create(original, decal, null, applyNormal: true));
            Assert.That(without.GetFloat("_UseBump2ndMap"), Is.EqualTo(0f), "マスクを画像の座標へ写せないと位置がずれるので外す");

            var with = Track(DecalOverlayMaterial.Create(original, decal, null, applyNormal: true, bump2ndMask: remappedMask));
            Assert.That(with.GetFloat("_UseBump2ndMap"), Is.EqualTo(1f));
            Assert.That(with.GetTexture("_Bump2ndScaleMask"), Is.SameAs(remappedMask));
            Assert.That(with.GetTextureScale("_Bump2ndScaleMask"), Is.EqualTo(Vector2.one), "写し直したマスクは画像の座標");
        }

        [Test]
        public void TryGetBump2ndSource_ONで画像がありUV1以外のときだけ_マスクも返す()
        {
            var shader = FindLilToonOrIgnore();
            var material = Track(new Material(shader));
            Assert.That(DecalOverlayMaterial.TryGetBump2ndSource(material, out _, out _), Is.False);
            material.SetFloat("_UseBump2ndMap", 1f);
            Assert.That(DecalOverlayMaterial.TryGetBump2ndSource(material, out _, out _), Is.False, "画像が無ければ残さない");
            material.SetTexture("_Bump2ndMap", Track(new Texture2D(4, 4)));
            Assert.That(DecalOverlayMaterial.TryGetBump2ndSource(material, out var none, out _), Is.True);
            Assert.That(none, Is.Null);
            var mask = Track(new Texture2D(4, 4));
            material.SetTexture("_Bump2ndScaleMask", mask);
            material.SetTextureOffset("_Bump2ndScaleMask", new Vector2(0.5f, 0f));
            Assert.That(DecalOverlayMaterial.TryGetBump2ndSource(material, out var got, out var st), Is.True);
            Assert.That(got, Is.SameAs(mask));
            Assert.That(st, Is.EqualTo(new Vector4(1f, 1f, 0.5f, 0f)));
            material.SetFloat("_Bump2ndMap_UVMode", 1f);
            Assert.That(DecalOverlayMaterial.TryGetBump2ndSource(material, out _, out _), Is.False);
        }

        [Test]
        public void Standard_のマテリアルは_lilToon_扱いしない()
        {
            var mat = Track(new Material(Shader.Find("Standard")));
            Assert.That(DecalOverlayMaterial.IsLilToon(mat), Is.False);
            Assert.That(DecalOverlayMaterial.IsLilToon(null), Is.False);
        }

        [Test]
        public void ロック済みの_lilToon_は透過版へ切り替えられないので対象外()
        {
            Assert.That(DecalOverlayMaterial.IsLilToonShaderName("lilToon"), Is.True);
            Assert.That(DecalOverlayMaterial.IsLilToonShaderName("Hidden/lilToonTransparent"), Is.True);
            Assert.That(DecalOverlayMaterial.IsLilToonShaderName("Hidden/Locked/lilToon/abc123"), Is.False);
            Assert.That(DecalOverlayMaterial.IsLilToonShaderName("Standard"), Is.False);
            Assert.That(DecalOverlayMaterial.IsLilToonShaderName(null), Is.False);
        }

        [Test]
        public void Gem_と_Refraction_は対象外で_FakeShadow_と_Overlay_は無視してよい付属()
        {
            Assert.That(DecalOverlayMaterial.IsLilToonShaderName("Hidden/lilToonGem"), Is.False);
            Assert.That(DecalOverlayMaterial.IsLilToonShaderName("Hidden/lilToonRefraction"), Is.False);
            Assert.That(DecalOverlayMaterial.IsLilToonShaderName("_lil/[Optional] lilToonFakeShadow"), Is.False);
            Assert.That(DecalOverlayMaterial.IsLilToonShaderName("_lil/[Optional] lilToonOverlay"), Is.False);
            Assert.That(DecalOverlayMaterial.IsLilToonShaderName("Hidden/lilToonFur"), Is.True, "Fur は対象のまま");

            Assert.That(DecalOverlayMaterial.IsIgnorableLilToonAuxiliaryShaderName("_lil/[Optional] lilToonFakeShadow"), Is.True);
            Assert.That(DecalOverlayMaterial.IsIgnorableLilToonAuxiliaryShaderName("_lil/[Optional] lilToonOverlay"), Is.True);
            Assert.That(DecalOverlayMaterial.IsIgnorableLilToonAuxiliaryShaderName("_lil/[Optional] lilToonFurOnlyTransparent"), Is.True);
            Assert.That(DecalOverlayMaterial.IsLilToonShaderName("_lil/[Optional] lilToonFurOnlyTransparent"), Is.False, "FurOnly は付属");
            Assert.That(DecalOverlayMaterial.IsIgnorableLilToonAuxiliaryShaderName("lilToon"), Is.False);
            Assert.That(DecalOverlayMaterial.IsIgnorableLilToonAuxiliaryShaderName("Hidden/lilToonGem"), Is.False);
            Assert.That(DecalOverlayMaterial.IsIgnorableLilToonAuxiliaryShaderName("Standard"), Is.False);
        }

        [Test]
        public void lilToon_のマテリアルを透過の重ね貼り用に複製する()
        {
            var shader = FindLilToonOrIgnore();
            Assert.That(DecalOverlayMaterial.IsApiAvailable, Is.True, "lilToon のエディタ API がリフレクションで見つかりません");

            var mainTex = Track(new Texture2D(4, 4));
            var decal = Track(new Texture2D(8, 8));
            var original = Track(new Material(shader));
            original.SetTexture("_MainTex", mainTex);
            original.SetFloat("_UseBumpMap", 1f);
            original.SetColor("_Color", Color.red);
            var shadowMask = Track(new Texture2D(4, 4));
            original.SetTexture("_ShadowStrengthMask", shadowMask);
            int originalQueue = original.renderQueue;

            var clone = DecalOverlayMaterial.Create(original, decal);
            Assert.That(clone, Is.Not.Null);
            Track(clone);

            Assert.That(clone.GetTexture("_MainTex"), Is.SameAs(decal));
            Assert.That(clone.GetTextureScale("_MainTex"), Is.EqualTo(Vector2.one));
            Assert.That(clone.GetTextureOffset("_MainTex"), Is.EqualTo(Vector2.zero));
            Assert.That(clone.GetFloat("_UseBumpMap"), Is.EqualTo(0f));
            Assert.That(clone.GetColor("_Color"), Is.EqualTo(Color.white));
            // lilToon の透過版は "AlphaTest+10"（2460）。透過・カットアウトの範囲で、元より後ろに描かれること
            Assert.That(clone.renderQueue, Is.GreaterThanOrEqualTo(2450));
            Assert.That(clone.renderQueue, Is.GreaterThan(originalQueue));
            Assert.That(clone.shader.name, Does.Contain("Transparent"));
            Assert.That(clone.GetFloat("_Cutoff"), Is.LessThan(0.01f), "透過パスの clip で画像の縁が切れない");
            Assert.That(clone.GetTexture("_ShadowStrengthMask"), Is.Null, "UV0 依存のマスクは外す");
            Assert.That(original.GetTexture("_ShadowStrengthMask"), Is.SameAs(shadowMask));

            // 元のマテリアルは変わらない
            Assert.That(original.shader, Is.SameAs(shader));
            Assert.That(original.GetTexture("_MainTex"), Is.SameAs(mainTex));
            Assert.That(original.GetFloat("_UseBumpMap"), Is.EqualTo(1f));
            Assert.That(original.GetColor("_Color"), Is.EqualTo(Color.red));
            Assert.That(original.renderQueue, Is.EqualTo(originalQueue));
        }

        [Test]
        public void UDIM_のタイル破棄と_UV1_のラメを外す()
        {
            var shader = FindLilToonOrIgnore();
            var original = Track(new Material(shader));
            if (!original.HasProperty("_UDIMDiscardCompile") || !original.HasProperty("_GlitterUVMode") || !original.HasProperty("_UseGlitter"))
            {
                Assert.Ignore("この lilToon には UDIM のタイル破棄かラメの UV モードがありません");
            }
            original.SetFloat("_UDIMDiscardCompile", 1f);
            bool hasDiscardMode = original.HasProperty("_UDIMDiscardMode");
            if (hasDiscardMode) original.SetFloat("_UDIMDiscardMode", 1f);
            original.SetFloat("_UseGlitter", 1f);
            original.SetFloat("_GlitterUVMode", 1f);

            var clone = DecalOverlayMaterial.Create(original, Track(new Texture2D(8, 8)));
            Assert.That(clone, Is.Not.Null);
            Track(clone);
            Assert.That(clone.GetFloat("_UDIMDiscardCompile"), Is.EqualTo(0f), "投影 UV が 0..1 の外の三角形を捨てない");
            if (hasDiscardMode)
            {
                Assert.That(clone.GetFloat("_UDIMDiscardMode"), Is.EqualTo(0f), "ピクセルモードの破棄は Compile を見ないので頂点モードに戻す");
            }
            Assert.That(clone.GetFloat("_UseGlitter"), Is.EqualTo(0f), "UV1 は元の UV0 に置き換わっているのでラメは外す");
            Assert.That(original.GetFloat("_UDIMDiscardCompile"), Is.EqualTo(1f), "元は変わらない");
            Assert.That(original.GetFloat("_UseGlitter"), Is.EqualTo(1f));
        }

        [Test]
        public void 透過の元より後ろに描く()
        {
            FindLilToonOrIgnore();
            var transparent = Shader.Find("Hidden/lilToonTransparent");
            if (transparent == null) Assert.Ignore("lilToon の透過版シェーダーが見つかりません");

            var original = Track(new Material(transparent));
            original.renderQueue = 3000;
            var clone = DecalOverlayMaterial.Create(original, Track(new Texture2D(8, 8)));
            Assert.That(clone, Is.Not.Null);
            Track(clone);
            Assert.That(clone.renderQueue, Is.EqualTo(3001));
        }

        [Test]
        public void 輪郭線付きの元から輪郭線なしの透過版を作る()
        {
            FindLilToonOrIgnore();
            var outline = Shader.Find("Hidden/lilToonOutline");
            if (outline == null) Assert.Ignore("lilToon の輪郭線版シェーダーが見つかりません");

            var clone = DecalOverlayMaterial.Create(Track(new Material(outline)), Track(new Texture2D(8, 8)));
            Assert.That(clone, Is.Not.Null);
            Track(clone);
            Assert.That(clone.shader.name, Does.Not.Contain("Outline"));
            Assert.That(clone.shader.name, Does.Contain("Transparent"));
        }

        [Test]
        public void Multi_の元から透過の複製を作る()
        {
            FindLilToonOrIgnore();
            var multi = Shader.Find("_lil/lilToonMulti");
            if (multi == null) Assert.Ignore("lilToonMulti が見つかりません");

            var original = Track(new Material(multi));
            var clone = DecalOverlayMaterial.Create(original, Track(new Texture2D(8, 8)));
            Assert.That(clone, Is.Not.Null);
            Track(clone);
            Assert.That(clone.GetFloat("_TransparentMode"), Is.EqualTo(2f), "Multi は _TransparentMode で透過を決める");
            Assert.That(clone.renderQueue, Is.GreaterThanOrEqualTo(2450));
            Assert.That(original.GetFloat("_TransparentMode"), Is.EqualTo(0f), "元は変わらない");
        }

        [Test]
        public void なめらかに貼るが_OFF_か画像が無ければ重ね貼りしない()
        {
            var go = Track(new GameObject("avatar"));
            var component = go.AddComponent<ClickRecolor>();

            var edit = RecolorEdit.CreateNew();
            edit.decalEnabled = true;
            edit.decalSmooth = true;
            edit.decalTexture = null;
            Assert.That(DecalOverlayMaterial.UseOverlay(component, edit), Is.False, "画像が無い");

            edit.decalTexture = Texture2D.whiteTexture;
            edit.decalSmooth = false;
            Assert.That(DecalOverlayMaterial.UseOverlay(component, edit), Is.False, "なめらかに貼るが OFF");

            Assert.That(DecalOverlayMaterial.UseOverlay(component, null), Is.False);
        }

        [Test]
        public void lilToon_以外のマテリアルが混ざると重ね貼りしない()
        {
            var shader = FindLilToonOrIgnore();
            var tex = Track(new Texture2D(4, 4));
            var go = Track(new GameObject("avatar"));
            var component = go.AddComponent<ClickRecolor>();
            var child = new GameObject("body");
            child.transform.SetParent(go.transform);
            var lil = Track(new Material(shader));
            lil.SetTexture("_MainTex", tex);
            var std = Track(new Material(Shader.Find("Standard")));
            std.SetTexture("_MainTex", tex);
            var renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] { lil };

            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = tex;
            edit.decalEnabled = true;
            edit.decalTexture = Texture2D.whiteTexture;
            // UseOverlay は編集を持つコンポーネントを基準にする（持ち主が見つからない編集は焼き込みに寄せる）ので、編集を登録しておく
            component.AddEdit(edit);
            Assert.That(DecalOverlayMaterial.CanOverlay(component, edit), Is.True, "lilToon だけなら重ね貼りできる");

            edit.decalSmooth = true;
            Assert.That(DecalOverlayMaterial.UseOverlay(component, edit), Is.True, "lilToon だけで なめらかに貼る ON");
            edit.decalSmooth = false;
            Assert.That(DecalOverlayMaterial.UseOverlay(component, edit), Is.False, "なめらかに貼る OFF なら焼き込み");
            edit.decalSmooth = true;

            var fakeShadowShader = Shader.Find("_lil/[Optional] lilToonFakeShadow");
            if (fakeShadowShader != null)
            {
                var fakeShadow = Track(new Material(fakeShadowShader));
                fakeShadow.SetTexture("_MainTex", tex);
                renderer.sharedMaterials = new[] { lil, fakeShadow };
                Assert.That(DecalOverlayMaterial.CanOverlay(component, edit), Is.True, "FakeShadow は無視する");
            }

            var gemShader = Shader.Find("Hidden/lilToonGem");
            if (gemShader != null)
            {
                var gem = Track(new Material(gemShader));
                gem.SetTexture("_MainTex", tex);
                renderer.sharedMaterials = new[] { lil, gem };
                Assert.That(DecalOverlayMaterial.CanOverlay(component, edit), Is.False, "Gem が混ざると焼き込み");
            }

            renderer.sharedMaterials = new[] { lil, std };
            Assert.That(DecalOverlayMaterial.CanOverlay(component, edit), Is.False, "同じテクスチャを使う Standard が混ざる");

            edit.sourceTexture = Track(new Texture2D(4, 4));
            Assert.That(DecalOverlayMaterial.CanOverlay(component, edit), Is.False, "対象テクスチャを使うマテリアルが無い");
        }

        [Test]
        public void 衣装側の編集でもアバター全体を見て_ルート直下のStandardが混ざると重ね貼りしない()
        {
            var shader = FindLilToonOrIgnore();
            if (!DecalOverlayMaterial.IsApiAvailable) Assert.Ignore("lilToon の API が見つからない環境です");
            var tex = Track(new Texture2D(4, 4));
            var avatar = Track(new GameObject("avatar"));
            avatar.AddComponent<nadena.dev.ndmf.runtime.components.NDMFAvatarRoot>();
            var rootComponent = avatar.AddComponent<ClickRecolor>();

            // ルート直下の体（同じテクスチャの Standard）
            var body = new GameObject("body");
            body.transform.SetParent(avatar.transform);
            var std = Track(new Material(Shader.Find("Standard")));
            std.SetTexture("_MainTex", tex);
            var bodyRenderer = body.AddComponent<MeshRenderer>();
            bodyRenderer.sharedMaterials = new[] { std };

            // 衣装（lilToon）と、その衣装に付けたコンポーネントの編集
            var costume = new GameObject("costume");
            costume.transform.SetParent(avatar.transform);
            var lil = Track(new Material(shader));
            lil.SetTexture("_MainTex", tex);
            var mesh = new GameObject("mesh");
            mesh.transform.SetParent(costume.transform);
            mesh.AddComponent<MeshRenderer>().sharedMaterials = new[] { lil };
            var costumeComponent = costume.AddComponent<ClickRecolor>();
            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = tex;
            edit.decalEnabled = true;
            edit.decalTexture = Texture2D.whiteTexture;
            edit.decalSmooth = true;
            costumeComponent.AddEdit(edit);

            Assert.That(DecalOverlayMaterial.FindOwner(edit, avatar.transform), Is.SameAs(costumeComponent));
            Assert.That(DecalOverlayMaterial.CanOverlay(costumeComponent, edit), Is.False, "ルート直下の Standard も判定に入る");
            Assert.That(DecalOverlayMaterial.UseOverlay(rootComponent, edit), Is.False, "どのコンポーネントから呼んでも同じ結果");

            // 体を衣装側のコンポーネントの除外リストに入れれば重ね貼りできる（除外リストは編集を持つコンポーネントのもの）
            costumeComponent.excludedRenderers.Add(bodyRenderer);
            Assert.That(DecalOverlayMaterial.CanOverlay(costumeComponent, edit), Is.True);
            Assert.That(DecalOverlayMaterial.UseOverlay(rootComponent, edit), Is.True, "ルートから呼んでも持ち主の除外リストで判定する");
        }

        [Test]
        public void 非アクティブのRendererにStandardが混ざると重ね貼りしない_除外リストなら見ない()
        {
            var shader = FindLilToonOrIgnore();
            if (!DecalOverlayMaterial.IsApiAvailable) Assert.Ignore("lilToon の API が見つからない環境です");
            var tex = Track(new Texture2D(4, 4));
            var go = Track(new GameObject("avatar"));
            var component = go.AddComponent<ClickRecolor>();
            var body = new GameObject("body");
            body.transform.SetParent(go.transform);
            var lil = Track(new Material(shader));
            lil.SetTexture("_MainTex", tex);
            body.AddComponent<MeshRenderer>().sharedMaterials = new[] { lil };

            // 非アクティブの衣装（同じテクスチャの Standard）
            var costume = new GameObject("costume");
            costume.transform.SetParent(go.transform);
            var std = Track(new Material(Shader.Find("Standard")));
            std.SetTexture("_MainTex", tex);
            var costumeRenderer = costume.AddComponent<MeshRenderer>();
            costumeRenderer.sharedMaterials = new[] { std };
            costume.SetActive(false);

            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = tex;
            edit.decalEnabled = true;
            edit.decalTexture = Texture2D.whiteTexture;
            Assert.That(DecalOverlayMaterial.CanOverlay(component, edit), Is.False, "非アクティブでも lilToon 以外が混ざれば焼き込み");

            component.excludedRenderers.Add(costumeRenderer);
            Assert.That(DecalOverlayMaterial.CanOverlay(component, edit), Is.True, "除外リストの Renderer は見ない");
        }
    }
}
