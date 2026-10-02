using Nekoare.ClickRecolor.Editor.Masks;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 色モード「島の中」用の島マスクのキャッシュ（IslandMaskCache）のテスト。
    /// 作る関数はダミー（1×1 の R8）を返し、呼ばれた回数を数える
    /// </summary>
    public class IslandMaskCacheTests
    {
        private int _builds;

        [SetUp]
        public void SetUp()
        {
            IslandMaskCache.ClearCache();
            _builds = 0;
        }

        [TearDown]
        public void TearDown()
        {
            IslandMaskCache.ClearCache();
        }

        private RenderTexture BuildDummy()
        {
            _builds++;
            return MaskTextures.Create(1, 1, "IslandMaskCacheTests_Dummy");
        }

        private static IslandMaskCache.Key KeyOf(int fingerprint = 100, int seedTriangle = 3) =>
            new IslandMaskCache.Key(12345, fingerprint, 0, seedTriangle, new Vector2(0.25f, 0.5f), 777, 64, 64);

        [Test]
        public void 同じ鍵なら2回目は作らず同じ_RT_を返す()
        {
            var first = IslandMaskCache.GetOrBuild(KeyOf(), BuildDummy);
            var second = IslandMaskCache.GetOrBuild(KeyOf(), BuildDummy);

            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.SameAs(first));
            Assert.That(_builds, Is.EqualTo(1));
        }

        [Test]
        public void フィンガープリントが違えば別に作る()
        {
            var first = IslandMaskCache.GetOrBuild(KeyOf(fingerprint: 100), BuildDummy);
            var second = IslandMaskCache.GetOrBuild(KeyOf(fingerprint: 200), BuildDummy);

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(_builds, Is.EqualTo(2));
            Assert.That(IslandMaskCache.Count, Is.EqualTo(2));
        }

        [Test]
        public void 上限を超えたら最古を捨てる()
        {
            for (int i = 0; i < IslandMaskCache.Capacity + 1; i++)
            {
                IslandMaskCache.GetOrBuild(KeyOf(seedTriangle: i), BuildDummy);
            }
            Assert.That(IslandMaskCache.Capacity, Is.EqualTo(8));
            Assert.That(IslandMaskCache.Count, Is.EqualTo(8));

            IslandMaskCache.GetOrBuild(KeyOf(seedTriangle: 0), BuildDummy);

            Assert.That(_builds, Is.EqualTo(IslandMaskCache.Capacity + 2), "最古（三角形0）は捨てられているので作り直す");
        }

        [Test]
        public void 作れなければ_null_を返し登録しない()
        {
            var result = IslandMaskCache.GetOrBuild(KeyOf(), () => null);

            Assert.That(result, Is.Null);
            Assert.That(IslandMaskCache.Count, Is.EqualTo(0));
        }
    }
}
