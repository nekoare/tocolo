using Nekoare.ClickRecolor.Editor.Picking;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>Poiyomi の Decal の位置計算と判定（PoiyomiLayerOverlap。シェーダー無しで確かめられる部分）</summary>
    public class PoiyomiLayerOverlapTests
    {
        [Test]
        public void 位置05_大きさ1_回転なしのデカールはUVそのまま()
        {
            var uv = PoiyomiLayerOverlap.DecalUv(new Vector2(0.3f, 0.7f), new Vector2(0.5f, 0.5f), 0f, Vector2.one, Vector4.zero, 0);
            Assert.That(uv.x, Is.EqualTo(0.3f).Within(1e-5f));
            Assert.That(uv.y, Is.EqualTo(0.7f).Within(1e-5f));
        }

        [Test]
        public void 小さなデカールは枠の外が0から1の外になる()
        {
            // 中心 (0.25, 0.25)、大きさ 0.2 → 枠は [0.15, 0.35]
            var inside = PoiyomiLayerOverlap.DecalUv(new Vector2(0.25f, 0.25f), new Vector2(0.25f, 0.25f), 0f, new Vector2(0.2f, 0.2f), Vector4.zero, 0);
            Assert.That(inside.x, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(inside.y, Is.EqualTo(0.5f).Within(1e-5f));
            var outside = PoiyomiLayerOverlap.DecalUv(new Vector2(0.8f, 0.8f), new Vector2(0.25f, 0.25f), 0f, new Vector2(0.2f, 0.2f), Vector4.zero, 0);
            Assert.That(outside.x, Is.GreaterThan(1f));
        }

        [Test]
        public void 回転は中心回りで_対称は右半分に折り返す()
        {
            // 中心 (0.5, 0.5) から右へ 0.25 の点を 90 度回すと上へ 0.25 → 大きさ 1 の枠では (0.5, 0.75)
            var rotated = PoiyomiLayerOverlap.DecalUv(new Vector2(0.75f, 0.5f), new Vector2(0.5f, 0.5f), 90f, Vector2.one, Vector4.zero, 0);
            Assert.That(rotated.x, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(rotated.y, Is.EqualTo(0.75f).Within(1e-4f));

            var mirrored = PoiyomiLayerOverlap.DecalUv(new Vector2(0.2f, 0.5f), new Vector2(0.5f, 0.5f), 0f, Vector2.one, Vector4.zero, 1);
            Assert.That(mirrored.x, Is.EqualTo(0.8f).Within(1e-5f), "Copy: x = |x − 0.5| + 0.5");
        }

        [Test]
        public void 縦横比の補正()
        {
            var shrink = PoiyomiLayerOverlap.ApplyAspect(Vector2.one, 1, 200, 100);
            Assert.That(shrink, Is.EqualTo(new Vector2(1f, 0.5f)));
            var grow = PoiyomiLayerOverlap.ApplyAspect(Vector2.one, 2, 200, 100);
            Assert.That(grow, Is.EqualTo(new Vector2(2f, 1f)));
        }

        [Test]
        public void Poiyomi_でないマテリアルは対象外()
        {
            var material = new Material(Shader.Find("Standard"));
            try
            {
                Assert.That(PoiyomiLayerOverlap.IsPoiyomi(material), Is.False);
                Assert.That(PoiyomiLayerOverlap.Check(material, Vector2.zero).kind, Is.EqualTo(LilToonLayerOverlap.Kind.None));
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }
    }
}
