using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 元テクスチャのキャッシュ（SourceTextureLoader）の、使っていない項目の取っておき・古い順の破棄・
    /// 元のミップの続きの版からの縮小版の作成のテスト。資産は作らず鍵で直接引く（AcquireByKey / AcquireScaled）。画像は 64×64 のダミー
    /// （値の一致のテストだけ PNG をメモリ上で作ってデコードする）
    /// </summary>
    public class SourceTextureLoaderTests
    {
        private const int Size = 64;
        private readonly List<Texture2D> _created = new List<Texture2D>();
        private int _loads;

        [SetUp]
        public void SetUp()
        {
            SourceTextureLoader.ReleaseAll();
            _loads = 0;
        }

        [TearDown]
        public void TearDown()
        {
            SourceTextureLoader.ReleaseAll();
            foreach (var texture in _created)
            {
                if (texture == null) continue;
                TextureColorSpaceUtility.UnregisterRuntimeTexture(texture);
                Object.DestroyImmediate(texture);
            }
            _created.Clear();
        }

        /// <summary>key で取得する。無ければダミーを作る（作った回数を _loads に数える）。owns = false はインポート済みの資産そのままの扱い</summary>
        private SourceTexture Acquire(string key, bool owns = true)
        {
            return SourceTextureLoader.AcquireByKey(null, key, 0, () =>
            {
                _loads++;
                var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                _created.Add(texture);
                return new SourceTextureLoader.Entry { texture = texture, ownsTexture = owns, isDirectRead = owns };
            });
        }

        private static long OneBytes => (long)Size * Size * 4 * 2;

        private Texture2D NewMipped(int width = Size, int height = Size)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true, linear: false) { hideFlags = HideFlags.HideAndDontSave };
            _created.Add(texture);
            return texture;
        }

        /// <summary>原寸 fileSize の画像の、元のミップの続きの版（長辺 size、ミップ付き）を key に入れ、使っていない状態にする</summary>
        private Texture2D AddChain(string key, int size, int fileSize, bool isSrgb = true)
        {
            var texture = NewMipped(size, size);
            SourceTextureLoader.AcquireByKey(null, key, size, () => new SourceTextureLoader.Entry
            {
                texture = texture, ownsTexture = true, isDirectRead = true,
                isChain = true, fileWidth = fileSize, fileHeight = fileSize, isSrgb = isSrgb,
            }).Dispose();
            return texture;
        }

        /// <summary>prefix の maxSize の版を取得する。デコードの代わりのダミー作成を _loads に数える</summary>
        private SourceTexture AcquireScaled(string prefix, int maxSize, bool isSrgb = true)
        {
            return SourceTextureLoader.AcquireScaled(null, prefix, maxSize, isSrgb, () =>
            {
                _loads++;
                return new SourceTextureLoader.Entry { texture = NewMipped(), ownsTexture = true, isDirectRead = true, isSrgb = isSrgb };
            });
        }

        [Test]
        public void 使い終わっても取っておき_次の取得で作り直さない()
        {
            var first = Acquire("a");
            var texture = first.Texture;
            first.Dispose();

            Assert.That(SourceTextureLoader.CachedEntryCount, Is.EqualTo(1));
            Assert.That(texture != null, Is.True, "使い終わっても破棄しない");

            using var second = Acquire("a");
            Assert.That(_loads, Is.EqualTo(1), "デコードし直さない");
            Assert.That(second.Texture, Is.SameAs(texture));
        }

        [Test]
        public void インポート済みの資産そのままの項目は使い終わったら外す()
        {
            Acquire("a", owns: false).Dispose();

            Assert.That(SourceTextureLoader.CachedEntryCount, Is.EqualTo(0));
            Assert.That(_created[0] != null, Is.True, "資産は破棄しない");
        }

        [Test]
        public void 上限を超えたら手放したのが古い順に捨てる()
        {
            var a = Acquire("a");
            var b = Acquire("b");
            var c = Acquire("c");
            // 取得の順ではなく、手放した順で古さを決める
            b.Dispose();
            a.Dispose();
            c.Dispose();

            SourceTextureLoader.TrimIdle(OneBytes * 2);

            Assert.That(SourceTextureLoader.CachedEntryCount, Is.EqualTo(2));
            Assert.That(_created[1] == null, Is.True, "最初に手放した b が破棄される");
            Assert.That(_created[0] != null && _created[2] != null, Is.True);
        }

        [Test]
        public void 使用中の項目は上限を超えても捨てない()
        {
            using var inUse = Acquire("a");
            Acquire("b").Dispose();

            SourceTextureLoader.TrimIdle();

            Assert.That(SourceTextureLoader.CachedEntryCount, Is.EqualTo(1));
            Assert.That(inUse.IsValid, Is.True);
            Assert.That(_created[1] == null, Is.True, "使っていない b だけ破棄される");
        }

        [Test]
        public void 取っておいた項目を使い直すと使用中になる()
        {
            Acquire("a").Dispose();
            using var again = Acquire("a");

            SourceTextureLoader.TrimIdle();

            Assert.That(again.IsValid, Is.True);
            Assert.That(SourceTextureLoader.CachedEntryCount, Is.EqualTo(1));
        }

        [Test]
        public void 使い直した後に手放すとまた取っておく()
        {
            Acquire("a").Dispose();
            Acquire("a").Dispose();

            Assert.That(SourceTextureLoader.CachedEntryCount, Is.EqualTo(1));
            Assert.That(_loads, Is.EqualTo(1));
            Assert.That(_created[0] != null, Is.True);
        }

        [Test]
        public void 再インポートされた資産の使っていない項目だけ捨てる()
        {
            Acquire("Assets/x.png|h1|256").Dispose();
            Acquire("Assets/x.png|h1|2048").Dispose();
            Acquire("Assets/x.png2|h1|256").Dispose();
            using var inUse = Acquire("Assets/y.png|h1|256");
            Acquire("Assets/z.png|h1|256").Dispose();

            SourceTextureLoader.DropIdle(new[] { "Assets/x.png", "Assets/y.png" });

            Assert.That(_created[0] == null && _created[1] == null, Is.True, "x.png の 2 つの大きさとも捨てる");
            Assert.That(_created[2] != null, Is.True, "パスの前方一致だけの別資産は残す");
            Assert.That(inUse.IsValid, Is.True, "使用中は残す");
            Assert.That(_created[4] != null, Is.True, "関係ない資産は残す");
            Assert.That(SourceTextureLoader.CachedEntryCount, Is.EqualTo(3));
        }

        [Test]
        public void ReleaseAll_の後の古いハンドルの手放しは何もしない()
        {
            var old = Acquire("a");
            SourceTextureLoader.ReleaseAll();
            using var fresh = Acquire("a");

            old.Dispose();

            Assert.That(fresh.IsValid, Is.True);
            Assert.That(SourceTextureLoader.CachedEntryCount, Is.EqualTo(1));
            SourceTextureLoader.TrimIdle();
            Assert.That(fresh.IsValid, Is.True, "新しい項目は使用中のまま");
        }

        [Test]
        public void 原寸のデコードがあれば縮小版をそこから作る()
        {
            var full = AddChain("p|h|64", Size, Size);

            using var small = AcquireScaled("p|h|", 16);

            Assert.That(_loads, Is.EqualTo(0), "デコードし直さない");
            Assert.That(small.Texture.width, Is.EqualTo(16));
            Assert.That(small.Texture, Is.Not.SameAs(full));
            Assert.That(small.Texture.mipmapCount, Is.GreaterThan(1), "縮めた版も続きにする（ミップ付き）");
            Assert.That(SourceTextureLoader.CachedEntryCount, Is.EqualTo(2));
        }

        [Test]
        public void 作業解像度の版から見本用の版を作る()
        {
            // 原寸 64 の画像を半分に縮めた版（4096 の画像のプレビュー用 2048 にあたる）
            AddChain("p|h|32", 32, Size);

            using var small = AcquireScaled("p|h|", 8);

            Assert.That(_loads, Is.EqualTo(0));
            Assert.That(small.Texture.width, Is.EqualTo(8));
        }

        [Test]
        public void 縮めなくてよいなら原寸のデコードそのものを使う()
        {
            var full = AddChain("p|h|64", Size, Size);

            using var large = AcquireScaled("p|h|", 2048);

            Assert.That(_loads, Is.EqualTo(0));
            Assert.That(large.Texture, Is.SameAs(full));
            Assert.That(SourceTextureLoader.CachedEntryCount, Is.EqualTo(1), "別の鍵の項目を作らない");
        }

        [Test]
        public void ちょうど半分ずつでない大きさは作らない()
        {
            AddChain("p|h|64", Size, Size);

            using var odd = AcquireScaled("p|h|", 48);

            Assert.That(_loads, Is.EqualTo(1), "デコードし直す（値を変えないため）");
        }

        [Test]
        public void 色空間が違う版は使わない()
        {
            AddChain("p|h|64", Size, Size, isSrgb: true);

            using var small = AcquireScaled("p|h|", 16, isSrgb: false);

            Assert.That(_loads, Is.EqualTo(1));
        }

        [Test]
        public void ミップの続きでない版や別の資産からは作らない()
        {
            Acquire("p|h|32").Dispose(); // 縮めただけの版（ミップの続きではない）
            AddChain("q|h|64", Size, Size);

            using var small = AcquireScaled("p|h|", 16);

            Assert.That(_loads, Is.EqualTo(2), "Acquire の 1 回＋デコードし直しの 1 回");
        }

        [TestCase(4096, 4096, 2048, 2048, true)]
        [TestCase(4096, 2048, 512, 256, true)]
        [TestCase(4096, 4096, 4096, 4096, false)] // 縮めていない
        [TestCase(4096, 4096, 3000, 3000, false)]
        [TestCase(3072, 3072, 1024, 1024, false)] // 1/3
        [TestCase(4096, 2048, 2048, 2048, false)] // 縦横で倍率が違う
        public void 縦横ちょうど半分ずつの縮小か(int width, int height, int targetWidth, int targetHeight, bool expected)
        {
            Assert.That(SourceTextureLoader.IsPowerOfTwoReduction(width, height, targetWidth, targetHeight), Is.EqualTo(expected));
        }

        [TestCase(2048, 256, true, false)]   // 4096 の画像のプレビュー用 2048 → 見本用 256
        [TestCase(2048, 2048, true, true)]   // 同じ大きさ
        [TestCase(2048, 4096, false, false)] // 大きくはできない
        public void 原寸4096の版から作れるか(int size, int maxSize, bool expected, bool expectedSame)
        {
            bool can = SourceTextureLoader.CanDeriveFromChain(4096, 4096, size, size, maxSize, out bool same);

            Assert.That(can, Is.EqualTo(expected));
            if (can) Assert.That(same, Is.EqualTo(expectedSame));
        }

        /// <summary>
        /// 見本用の版を「作業解像度の版（ミップを作り直したもの）から縮める」と、今までの「元のデコードから直接縮める」と画素単位で同じになるか。
        /// 4096 の画像 → 2048 → 256 を、512 → 256 → 32 で確かめる（倍率は同じ 1/2 と 1/8、直接は 1/16）
        /// </summary>
        [TestCase(true, 512, 512)]
        [TestCase(false, 512, 512)]
        [TestCase(true, 512, 256)]
        public void 作業解像度の版から縮めた値は元から直接縮めた値と同じ(bool isSrgb, int width, int height)
        {
            var source = new Texture2D(width, height, TextureFormat.RGBA32, false);
            _created.Add(source);
            var random = new System.Random(1);
            var pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // 白黒の細い縞（明暗の境目）と、ばらばらの色・不透明度を混ぜる
                    pixels[y * width + x] = (x / 3 + y / 5) % 4 == 0
                        ? ((x + y) % 2 == 0 ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255))
                        : new Color32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                }
            }
            source.SetPixels32(pixels);
            source.Apply();
            byte[] png = source.EncodeToPNG();

            var full = SourceTextureLoader.DecodeImage(png, "full", true, isSrgb);
            _created.Add(full);
            int smallWidth = width / 16, smallHeight = height / 16;
            var direct = SourceTextureLoader.Downscale(full, smallWidth, smallHeight, isSrgb, withMips: false);
            _created.Add(direct);
            var working = SourceTextureLoader.Downscale(full, width / 2, height / 2, isSrgb, withMips: true);
            _created.Add(working);
            var derived = SourceTextureLoader.Downscale(working, smallWidth, smallHeight, isSrgb, withMips: true);
            _created.Add(derived);

            var expected = direct.GetPixels32();
            var actual = derived.GetPixels32();
            int mismatches = 0, maxDiff = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                int diff = Mathf.Max(
                    Mathf.Max(Mathf.Abs(expected[i].r - actual[i].r), Mathf.Abs(expected[i].g - actual[i].g)),
                    Mathf.Max(Mathf.Abs(expected[i].b - actual[i].b), Mathf.Abs(expected[i].a - actual[i].a)));
                if (diff == 0) continue;
                mismatches++;
                maxDiff = Mathf.Max(maxDiff, diff);
            }
            Assert.That(mismatches, Is.EqualTo(0), $"{expected.Length} 画素中 {mismatches} 画素が違う（最大 {maxDiff}/255）");
        }

        [Test]
        public void メモリの見積もりはミップと読める写しを数える()
        {
            var plain = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            var mipped = new Texture2D(Size, Size, TextureFormat.RGBA32, true);
            _created.Add(plain);
            _created.Add(mipped);

            Assert.That(SourceTextureLoader.EstimateBytes(plain), Is.EqualTo(OneBytes));
            long withMips = (long)Size * Size * 4;
            withMips += withMips / 3;
            Assert.That(SourceTextureLoader.EstimateBytes(mipped), Is.EqualTo(withMips * 2));
            Assert.That(SourceTextureLoader.EstimateBytes(null), Is.EqualTo(0));
        }
    }
}
