using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 「画像を入れる」の層のキャッシュ（DecalLayerCache）の上限・全消去のテスト。
    /// 層は 4×4 の作業 RT のダミー（中身は見ない）
    /// </summary>
    public class DecalLayerCacheTests
    {
        private int _builds;

        [SetUp]
        public void SetUp()
        {
            DecalLayerCache.ClearCache();
            _builds = 0;
        }

        [TearDown]
        public void TearDown()
        {
            DecalLayerCache.ClearCache();
        }

        private RenderTexture BuildDummy(out Stats stats, out BandStats bands)
        {
            _builds++;
            stats = new Stats { count = _builds };
            bands = null;
            return RecolorPipeline.CreateWorkTexture(4, 4, "DecalLayerCacheTests_Dummy");
        }

        private static DecalLayerCache.Key KeyOf(int decalHash, bool fromExport = false) =>
            new DecalLayerCache.Key("edit-1", 12345, 256, fromExport, decalHash);

        private RenderTexture Get(in DecalLayerCache.Key key) => DecalLayerCache.GetOrBuild(key, BuildDummy, out _, out _);

        [Test]
        public void 同じ鍵なら2回目は作らず同じ層と統計を返す()
        {
            var first = DecalLayerCache.GetOrBuild(KeyOf(1), BuildDummy, out var firstStats, out _);
            var second = DecalLayerCache.GetOrBuild(KeyOf(1), BuildDummy, out var secondStats, out _);

            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.SameAs(first));
            Assert.That(secondStats.count, Is.EqualTo(firstStats.count));
            Assert.That(_builds, Is.EqualTo(1));
        }

        [Test]
        public void 前回のTrimの後に使われた層は上限を超えても残し_使われなくなった古い層から破棄する()
        {
            var layers = new List<RenderTexture>();
            for (int i = 0; i < DecalLayerCache.Capacity + 1; i++) layers.Add(Get(KeyOf(i)));
            Assert.That(DecalLayerCache.Capacity, Is.EqualTo(4));
            Assert.That(DecalLayerCache.Count, Is.EqualTo(5), "GetOrBuildでは減らさない（Runに渡す前の層を捨てない）");

            // 5 枚ともこの回に使ったので残す（画像入りの編集が上限より多いとき、毎回 1 枚作り直さない）
            DecalLayerCache.Trim();
            Assert.That(DecalLayerCache.Count, Is.EqualTo(5));

            // 次の回は 1〜4 だけ使った: 使われなかった最古の 0 を捨てる
            for (int i = 1; i < DecalLayerCache.Capacity + 1; i++) Get(KeyOf(i));
            DecalLayerCache.Trim();

            Assert.That(_builds, Is.EqualTo(5), "2 回目の回では作り直さない");
            Assert.That(DecalLayerCache.Count, Is.EqualTo(4));
            Assert.That(layers[0] == null || !layers[0].IsCreated(), Is.True, "使われなかった最古の層は破棄される");
            Assert.That(layers[1].IsCreated(), Is.True);
            Assert.That(layers[4].IsCreated(), Is.True);
        }

        [Test]
        public void ClearCache_で全部破棄される()
        {
            var layers = new List<RenderTexture>();
            for (int i = 0; i < 3; i++) layers.Add(Get(KeyOf(i)));

            DecalLayerCache.ClearCache();

            Assert.That(DecalLayerCache.Count, Is.EqualTo(0));
            foreach (var layer in layers) Assert.That(layer == null || !layer.IsCreated(), Is.True);
        }

        [Test]
        public void RemoveExportLayers_は書き出し由来だけ捨てる()
        {
            var preview = Get(KeyOf(1, fromExport: false));
            var export = Get(KeyOf(1, fromExport: true));
            Assert.That(export, Is.Not.SameAs(preview), "同じ中身でも書き出し由来は別の鍵");

            DecalLayerCache.RemoveExportLayers();

            Assert.That(DecalLayerCache.Count, Is.EqualTo(1));
            Assert.That(preview.IsCreated(), Is.True);
            Assert.That(export == null || !export.IsCreated(), Is.True);
        }
    }
}
