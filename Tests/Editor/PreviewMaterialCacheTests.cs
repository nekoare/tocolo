using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.NDMF;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Tests
{
    public class PreviewMaterialCacheTests
    {
        private readonly List<Object> _created = new List<Object>();
        private readonly List<PreviewMaterialCache.Lease> _leases = new List<PreviewMaterialCache.Lease>();

        [TearDown]
        public void TearDown()
        {
            foreach (var lease in _leases) PreviewMaterialCache.Release(lease);
            _leases.Clear();
            foreach (var o in _created)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            _created.Clear();
        }

        private T Track<T>(T o) where T : Object
        {
            _created.Add(o);
            return o;
        }

        private (Material material, Texture2D texture) MakeMaterial()
        {
            var texture = Track(new Texture2D(4, 4));
            var material = Track(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            return (material, texture);
        }

        private RenderTexture MakeRt() => Track(new RenderTexture(4, 4, 0));

        private PreviewMaterialCache.Lease Acquire(Material material, Texture2D texture, RenderTexture rt)
        {
            var lease = PreviewMaterialCache.Acquire(material, "_MainTex", texture, rt, " (test)");
            _leases.Add(lease);
            return lease;
        }

        [Test]
        public void 同じ元マテリアルなら複製を使い回し_結果の画像だけ差し替える()
        {
            var (material, texture) = MakeMaterial();
            var first = Acquire(material, texture, MakeRt());
            var rt = MakeRt();

            var second = Acquire(material, texture, rt);

            Assert.That(second.Material, Is.SameAs(first.Material));
            Assert.That(second.Material.GetTexture("_MainTex"), Is.SameAs(rt));
            Assert.That(material.GetTexture("_MainTex"), Is.SameAs(texture), "元のマテリアルは変えない");
        }

        [Test]
        public void 新しいほうを先に返すと_残っているほうの結果の画像に戻る()
        {
            var (material, texture) = MakeMaterial();
            var shown = MakeRt();
            var old = Acquire(material, texture, shown);
            var discarded = Acquire(material, texture, MakeRt());

            // 作り直したノードを表示に使わずに捨てた（表示中の旧ノードが、捨てられる結果の画像を指したままにならない）
            PreviewMaterialCache.Release(discarded);

            Assert.That(old.Material.GetTexture("_MainTex"), Is.SameAs(shown));
        }

        [Test]
        public void 誰も借りていなければ複製を捨てる_同じものを2回返しても数え違えない()
        {
            var (material, texture) = MakeMaterial();
            int before = PreviewMaterialCache.Count;
            var first = Acquire(material, texture, MakeRt());
            var second = Acquire(material, texture, MakeRt());
            var clone = first.Material;

            PreviewMaterialCache.Release(first);
            PreviewMaterialCache.Release(first);
            Assert.That(clone != null, Is.True, "まだ借りているノードがある");

            PreviewMaterialCache.Release(second);
            Assert.That(clone == null, Is.True, "破棄した");
            Assert.That(PreviewMaterialCache.Count, Is.EqualTo(before));
        }

        [Test]
        public void 元マテリアルを編集したら別の複製を作る()
        {
            var (material, texture) = MakeMaterial();
            var before = Acquire(material, texture, MakeRt());

            material.SetColor("_Color", Color.red);
            var after = Acquire(material, texture, MakeRt());

            Assert.That(after.Material, Is.Not.SameAs(before.Material));
            Assert.That(after.Material.GetColor("_Color"), Is.EqualTo(Color.red));
        }

        [Test]
        public void 元のテクスチャを参照する他のプロパティにも結果の画像を差し込む()
        {
            var (material, texture) = MakeMaterial();
            material.SetTexture("_DetailAlbedoMap", texture);
            var rt = MakeRt();

            var lease = Acquire(material, texture, rt);
            Acquire(material, texture, MakeRt());
            PreviewMaterialCache.Release(_leases[1]);

            Assert.That(lease.Material.GetTexture("_DetailAlbedoMap"), Is.SameAs(rt), "差し替え直しても他のプロパティもそろう");
        }
    }
}
