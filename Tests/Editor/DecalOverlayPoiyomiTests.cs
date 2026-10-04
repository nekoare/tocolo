using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Decal;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 「画像を入れる」第 2 段（重ね貼り）の Poiyomi Toon 用マテリアル（DecalOverlayPoiyomi）のテスト。
    /// Poiyomi が入っていない環境ではシェーダーを使う部分を Ignore する
    /// </summary>
    public class DecalOverlayPoiyomiTests
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

        private static Shader FindPoiyomiOrIgnore()
        {
            var shader = Shader.Find(DecalOverlayPoiyomi.ShaderName);
            if (shader == null) Assert.Ignore("Poiyomi Toon が入っていない環境です");
            return shader;
        }

        [Test]
        public void 描画モードの説明文から値ごとの書き換えを読む()
        {
            const string description = "Rendering Preset--{on_value_actions:[" +
                "{value:0,actions:[{type:SET_PROPERTY,data:render_queue=2000},{type:SET_PROPERTY,data:_ZWrite=1}]}," +
                "{value:9,actions:[{type:SET_PROPERTY,data:render_queue=2460},{type:SET_PROPERTY,data:render_type=TransparentCutout}, {type:SET_PROPERTY,data:_SrcBlend=5}]}," +
                "{value:90,actions:[{type:SET_PROPERTY,data:_ZWrite=0}]}]}";

            var actions = DecalOverlayPoiyomi.ParseModeActions(description, 9);

            Assert.That(actions, Is.EqualTo(new List<(string, string)>
            {
                ("render_queue", "2460"), ("render_type", "TransparentCutout"), ("_SrcBlend", "5"),
            }));
            Assert.That(DecalOverlayPoiyomi.ParseModeActions(description, 3), Is.Null, "無い値は null");
            Assert.That(DecalOverlayPoiyomi.ParseModeActions(null, 9), Is.Null);
        }

        [Test]
        public void 対象はロックされていない本体だけ()
        {
            Assert.That(DecalOverlayPoiyomi.IsTargetShaderName(".poiyomi/Poiyomi Toon"), Is.True);
            Assert.That(DecalOverlayPoiyomi.IsTargetShaderName("Hidden/Locked/.poiyomi/Poiyomi Toon/abc"), Is.False);
            Assert.That(DecalOverlayPoiyomi.IsTargetShaderName(".poiyomi/Poiyomi Toon Two Pass"), Is.False);
            Assert.That(DecalOverlayPoiyomi.IsTargetShaderName("lilToon"), Is.False);
        }

        [Test]
        public void メインテクスチャをUV0以外で読むマテリアルは対象外()
        {
            var shader = FindPoiyomiOrIgnore();
            var material = Track(new Material(shader));
            Assert.That(DecalOverlayPoiyomi.IsTarget(material), Is.True);
            material.SetFloat("_MainTexUV", 1f);
            Assert.That(DecalOverlayPoiyomi.IsTarget(material), Is.False);
        }

        [Test]
        public void 透過にしてUV0のテクスチャをUV1へ付け替え_メインは画像のまま()
        {
            var shader = FindPoiyomiOrIgnore();
            var decal = Track(new Texture2D(8, 8));
            var original = Track(new Material(shader));
            original.SetTexture("_MainTex", Track(new Texture2D(4, 4)));
            original.SetTextureScale("_MainTex", new Vector2(2f, 2f));
            original.SetFloat("_BumpMapUV", 0f);
            original.SetFloat("_MatcapMaskUV", 2f);
            original.SetVector("_UVSettingsTiling0", new Vector4(3f, 3f, 0f, 0f));
            original.SetFloat("_DecalEnabled", 1f);

            var clone = Track(DecalOverlayPoiyomi.Create(original, decal, applyNormal: true));

            Assert.That(clone, Is.Not.Null);
            Assert.That(clone.GetFloat("_Mode"), Is.EqualTo(9f), "TransClipping");
            Assert.That(clone.renderQueue, Is.GreaterThanOrEqualTo(2450), "透過の順番");
            Assert.That(clone.GetFloat("_Cutoff"), Is.EqualTo(0.001f).Within(1e-6f));
            Assert.That(clone.GetTexture("_MainTex"), Is.SameAs(decal));
            Assert.That(clone.GetTextureScale("_MainTex"), Is.EqualTo(Vector2.one));
            Assert.That(clone.GetFloat("_MainTexUV"), Is.EqualTo(0f), "メインは画像の座標（UV0）のまま");
            Assert.That(clone.GetFloat("_BumpMapUV"), Is.EqualTo(1f), "UV0 で読んでいたものは UV1（元の UV0）へ");
            Assert.That(clone.GetFloat("_MatcapMaskUV"), Is.EqualTo(2f), "UV2 は元のまま");
            Assert.That(clone.GetVector("_UVSettingsTiling1"), Is.EqualTo(new Vector4(3f, 3f, 0f, 0f)), "UV0 の全体設定は UV1 へ");
            Assert.That(clone.GetVector("_UVSettingsTiling0"), Is.EqualTo(new Vector4(1f, 1f, 0f, 0f)), "画像の座標は既定");
            Assert.That(clone.GetFloat("_DecalEnabled"), Is.EqualTo(0f), "服側のデカールは画像に掛けない");
            Assert.That(clone.GetFloat("_BumpScale"), Is.EqualTo(original.GetFloat("_BumpScale")), "ノーマルも反映 ON なら強さは元のまま");
            Assert.That(original.GetFloat("_BumpMapUV"), Is.EqualTo(0f), "元マテリアルは変えない");
        }

        [Test]
        public void ノーマルも反映OFFなら法線の強さを0にする()
        {
            var shader = FindPoiyomiOrIgnore();
            var original = Track(new Material(shader));
            original.SetTexture("_MainTex", Track(new Texture2D(4, 4)));
            original.SetFloat("_BumpScale", 1.5f);

            var clone = Track(DecalOverlayPoiyomi.Create(original, Track(new Texture2D(8, 8)), applyNormal: false));

            Assert.That(clone.GetFloat("_BumpScale"), Is.EqualTo(0f));
            Assert.That(clone.GetFloat("_DetailNormalMapScale"), Is.EqualTo(0f));
        }

        [Test]
        public void 重ね貼り用マテリアルを作れる種類にPoiyomiが入る()
        {
            var shader = FindPoiyomiOrIgnore();
            var material = Track(new Material(shader));
            Assert.That(DecalOverlayMaterial.IsOverlayMaterial(material), Is.True);
            Assert.That(DecalOverlayMaterial.IsOverlayMaterial(Track(new Material(Shader.Find("Standard")))), Is.False);
        }
    }
}
