using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Picking;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>lilToon の 2nd／3rd の重なりの判定（LilToonLayerOverlap）</summary>
    public class LilToonLayerOverlapTests
    {
        private static LilToonLayerOverlap.Result Evaluate(params (string, float, int)[] layers) =>
            LilToonLayerOverlap.Evaluate(new List<(string, float, int)>(layers));

        [Test]
        public void 通常の層が半分以上覆えば_変わりにくい()
        {
            var r = Evaluate(("2nd", 0.6f, 0));
            Assert.That(r.kind, Is.EqualTo(LilToonLayerOverlap.Kind.Covers));
            Assert.That(r.layers, Is.EqualTo("2nd"));
        }

        [Test]
        public void 通常の層が2つなら合わせた覆いで判定し_両方の名前を出す()
        {
            // 1 − (1 − 0.4)(1 − 0.3) = 0.58
            var r = Evaluate(("2nd", 0.4f, 0), ("3rd", 0.3f, 0));
            Assert.That(r.kind, Is.EqualTo(LilToonLayerOverlap.Kind.Covers));
            Assert.That(r.layers, Is.EqualTo("2nd/3rd"));
        }

        [Test]
        public void 薄い通常の層だけなら案内しない()
        {
            Assert.That(Evaluate(("3rd", 0.3f, 0)).kind, Is.EqualTo(LilToonLayerOverlap.Kind.None));
            Assert.That(Evaluate().kind, Is.EqualTo(LilToonLayerOverlap.Kind.None));
        }

        [Test]
        public void 乗算_加算の層は混ざる案内_通常の覆いが優先()
        {
            var mix = Evaluate(("2nd", 0.5f, 3));
            Assert.That(mix.kind, Is.EqualTo(LilToonLayerOverlap.Kind.Mixes));
            Assert.That(mix.layers, Is.EqualTo("2nd"));

            var both = Evaluate(("2nd", 0.8f, 0), ("3rd", 0.8f, 1));
            Assert.That(both.kind, Is.EqualTo(LilToonLayerOverlap.Kind.Covers));
            Assert.That(both.layers, Is.EqualTo("2nd"));
        }

        [Test]
        public void 層のUVは_Tiling_Offset_のあと中心回りに回る()
        {
            var uv = LilToonLayerOverlap.LayerUv(new Vector2(0.25f, 0.5f), new Vector4(2f, 1f, 0f, 0f), 0f);
            Assert.That(uv.x, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(uv.y, Is.EqualTo(0.5f).Within(1e-5f));

            // 中心 (0.5, 0.5) から右へ 0.25 の点を 90 度回すと上へ 0.25
            var rotated = LilToonLayerOverlap.LayerUv(new Vector2(0.75f, 0.5f), new Vector4(1f, 1f, 0f, 0f), Mathf.PI * 0.5f);
            Assert.That(rotated.x, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(rotated.y, Is.EqualTo(0.75f).Within(1e-5f));
        }

        [Test]
        public void lilToon_でないマテリアルは案内しない()
        {
            var material = new Material(Shader.Find("Standard"));
            try
            {
                Assert.That(LilToonLayerOverlap.Check(material, Vector2.zero, Vector2.zero).kind, Is.EqualTo(LilToonLayerOverlap.Kind.None));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }
    
        [Test]
        public void メインの色が白以外なら掛け合わせの案内が出る()
        {
            var material = new Material(Shader.Find("Standard"));
            try
            {
                material.SetColor("_Color", Color.white);
                Assert.That(LilToonLayerOverlap.Check(material, Vector2.zero, Vector2.zero).mainColorTint, Is.False, "白なら出さない");
                material.SetColor("_Color", new Color(0.4f, 0.6f, 1f, 1f));
                Assert.That(LilToonLayerOverlap.Check(material, Vector2.zero, Vector2.zero).mainColorTint, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }
    
        [Test]
        public void lilToon_の色調補正は既定値なら効かず_変えれば効く()
        {
            var identity = new Vector4(0f, 1f, 1f, 1f);
            Assert.That(LilToonLayerOverlap.IsLilToneCorrectionActive(identity, 0f, false), Is.False);
            Assert.That(LilToonLayerOverlap.IsLilToneCorrectionActive(new Vector4(0.1f, 1f, 1f, 1f), 0f, false), Is.True, "色相");
            Assert.That(LilToonLayerOverlap.IsLilToneCorrectionActive(new Vector4(0f, 0.5f, 1f, 1f), 0f, false), Is.True, "彩度");
            Assert.That(LilToonLayerOverlap.IsLilToneCorrectionActive(identity, 0.5f, true), Is.True, "グラデーションマップ");
            Assert.That(LilToonLayerOverlap.IsLilToneCorrectionActive(identity, 0.5f, false), Is.False, "テクスチャが無ければ効かない");
        }
    }
}
