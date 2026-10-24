using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// なぞって調整を島マスク（GPU）に掛けるテスト。64×64 の UV に:
    ///   チャート A: 左の四角 x = 4..16（三角形 0, 1）と右の四角 x = 16..28（三角形 2 = 右下, 3 = 左上）。y = 4..60。辺 x = 16 を共有して 1 つのチャート
    ///   チャート B: x = 32..60, y = 4..60（三角形 4, 5）
    /// 右の四角を対角線で分けた片方だけを消すので、上下が逆に掛かると見分けられる
    /// </summary>
    public class PaintAdjustMaskTests
    {
        private const int Size = 64;

        private readonly List<Object> _cleanup = new List<Object>();
        private readonly List<RenderTexture> _masks = new List<RenderTexture>();
        private Mesh _mesh;
        private RenderTexture _coverage;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.R8))
            {
                Assert.Ignore("この環境はcompute shader（R8への書き込み）に対応していません");
            }
            Assert.That(UvRasterizer.IsAvailable, Is.True);
            Assert.That(Morphology.IsAvailable, Is.True, "Morphology.computeが読み込めません（CSSub を足したら .meta の GUID はそのまま）");

            _mesh = new Mesh();
            _cleanup.Add(_mesh);
            var uv = new[]
            {
                Px(4, 4), Px(16, 4), Px(16, 60), Px(4, 60), Px(28, 4), Px(28, 60),
                Px(32, 4), Px(60, 4), Px(60, 60), Px(32, 60),
            };
            var vertices = new Vector3[uv.Length];
            for (int i = 0; i < uv.Length; i++) vertices[i] = uv[i];
            _mesh.vertices = vertices;
            _mesh.uv = uv;
            _mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 1, 4, 5, 1, 5, 2, 6, 7, 8, 6, 8, 9 };

            var texture = new Texture2D(Size, Size);
            _cleanup.Add(texture);
            _coverage = CoverageMask.GetOrBuild(
                texture, new List<(Mesh, int, Vector2, Vector2)> { (_mesh, 0, Vector2.one, Vector2.zero) }, Size, out _);
            Assert.That(_coverage, Is.Not.Null);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var rt in _masks) MaskTextures.Destroy(rt);
            _masks.Clear();
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
            CoverageMask.ClearCache();
            UvChartDetector.ClearCache();
        }

        private static Vector2 Px(float x, float y) => new Vector2(x / Size, y / Size);

        /// <summary>チャート A の、はみ出し幅とぼかしの前の島マスク</summary>
        private RenderTexture BuildRawChartA()
        {
            var rt = IslandMaskBuilder.Build(new IslandRequest
            {
                mesh = _mesh,
                submesh = 0,
                seedTriangle = 0,
                seedUv = Px(10, 32),
                width = Size,
                height = Size,
                coverage = _coverage,
                skipFinish = true,
            });
            Assert.That(rt, Is.Not.Null);
            _masks.Add(rt);
            return rt;
        }

        /// <summary>メッシュの三角形 tri を state で塗った調整の画像に足す</summary>
        private void PaintMeshTriangle(PaintAdjustImage image, int tri, byte state)
        {
            var uv = _mesh.uv;
            var triangles = _mesh.triangles;
            PaintAdjustRaster.PaintTriangle(image, uv[triangles[tri * 3]], uv[triangles[tri * 3 + 1]], uv[triangles[tri * 3 + 2]], state);
        }

        private static byte At(byte[] pixels, int x, int y) => pixels[y * Size + x];

        [Test]
        public void なぞって消した三角形はパーツの範囲から外れ残りはそのまま()
        {
            var mask = BuildRawChartA();
            var image = new PaintAdjustImage(Size, Size);
            PaintMeshTriangle(image, 2, PaintState.Remove);

            PaintAdjustMask.Apply(mask, image, add: true, remove: true);
            IslandMaskBuilder.Finish(mask, _coverage, padding: 4);
            var pixels = FloodFill.ReadR8(mask);

            Assert.That(At(pixels, 26, 10), Is.EqualTo(0), "消した右下の三角形");
            Assert.That(At(pixels, 18, 50), Is.EqualTo(255), "右の四角の残した左上の三角形");
            Assert.That(At(pixels, 10, 32), Is.EqualTo(255), "左の四角");
        }

        [Test]
        public void はみ出し幅は同じパーツの消した三角形へは広がらない()
        {
            var mask = BuildRawChartA();
            var image = new PaintAdjustImage(Size, Size);
            PaintMeshTriangle(image, 2, PaintState.Remove);
            PaintMeshTriangle(image, 3, PaintState.Remove);

            PaintAdjustMask.Apply(mask, image, add: true, remove: true);
            IslandMaskBuilder.Finish(mask, _coverage, padding: 8);
            var pixels = FloodFill.ReadR8(mask);

            for (int x = 18; x <= 27; x++)
            {
                Assert.That(At(pixels, x, 32), Is.EqualTo(0), $"消した右の四角 ({x},32)");
            }
            Assert.That(At(pixels, 2, 32), Is.GreaterThan(0), "UV のすき間（左の外）へは広がる");
        }

        [Test]
        public void なぞって足した所は選んでいないパーツでも範囲に入る()
        {
            var mask = BuildRawChartA();
            var image = new PaintAdjustImage(Size, Size);
            PaintMeshTriangle(image, 4, PaintState.Add);

            PaintAdjustMask.Apply(mask, image, add: true, remove: true);
            IslandMaskBuilder.Finish(mask, _coverage, padding: 0);
            var pixels = FloodFill.ReadR8(mask);

            Assert.That(At(pixels, 56, 10), Is.EqualTo(255), "チャート B の足した右下の三角形");
            Assert.That(At(pixels, 36, 56), Is.EqualTo(0), "チャート B の足していない左上の三角形");
        }

        [Test]
        public void 足した所を掛けない指定なら消した所だけ掛ける()
        {
            var mask = BuildRawChartA();
            var image = new PaintAdjustImage(Size, Size);
            PaintMeshTriangle(image, 4, PaintState.Add);
            PaintMeshTriangle(image, 2, PaintState.Remove);

            PaintAdjustMask.Apply(mask, image, add: false, remove: true);
            var pixels = FloodFill.ReadR8(mask);

            Assert.That(At(pixels, 56, 10), Is.EqualTo(0), "足した所は入れない");
            Assert.That(At(pixels, 26, 10), Is.EqualTo(0), "消した所は外す");
        }

        [Test]
        public void 調整の画像の大きさがマスクと違っても同じ場所に掛かる()
        {
            var mask = BuildRawChartA();
            var image = new PaintAdjustImage(Size * 2, Size * 2);
            PaintMeshTriangle(image, 2, PaintState.Remove);

            PaintAdjustMask.Apply(mask, image, add: true, remove: true);
            var pixels = FloodFill.ReadR8(mask);

            Assert.That(At(pixels, 26, 10), Is.EqualTo(0));
            Assert.That(At(pixels, 18, 50), Is.EqualTo(255));
        }

        [Test]
        public void 細かい調整の画像から作ると境目をまたぐ画素は消した割合だけ残る()
        {
            var mask = BuildRawChartA();
            // 2 倍の細かさ。右下の三角形の対角線は、マスクの画素 (22,34) をちょうど左右半分に分ける（2 倍側の 4 画素のうち右の 2 つだけ三角形の中）
            var image = new PaintAdjustImage(Size * 2, Size * 2);
            PaintMeshTriangle(image, 2, PaintState.Remove);

            PaintAdjustMask.Apply(mask, image, add: true, remove: true);
            var pixels = FloodFill.ReadR8(mask);

            Assert.That(At(pixels, 22, 34), Is.InRange(100, 155), "境目の画素は半分");
            Assert.That(At(pixels, 26, 10), Is.EqualTo(0), "三角形の中");
            Assert.That(At(pixels, 18, 50), Is.EqualTo(255), "三角形の外");
        }

        [Test]
        public void 調整の画像がマスクの4倍以上でも細い三角形を読み飛ばさない()
        {
            var mask = BuildRawChartA();
            // 8 倍の細かさで、マスクの 1 画素ぶん（8×8）の内側の 2×2 だけを消す。一度に縮めると間を読み飛ばして消えない
            var image = new PaintAdjustImage(Size * 8, Size * 8);
            int x0 = 10 * 8 + 3, y0 = 32 * 8 + 3;
            for (int y = y0; y < y0 + 2; y++)
            {
                for (int x = x0; x < x0 + 2; x++) image.states[y * image.width + x] = PaintState.Remove;
            }

            PaintAdjustMask.Apply(mask, image, add: true, remove: true);
            var pixels = FloodFill.ReadR8(mask);

            Assert.That(At(pixels, 10, 32), Is.LessThan(255), "消した 2×2 がマスクの画素に効いている");
        }

        // ── ブラシのはみ出し（paintEdgeOffset）──

        [Test]
        public void ブラシのはみ出しをプラスにすると消した所の縁まで範囲が広がる()
        {
            var mask = BuildRawChartA();
            var image = new PaintAdjustImage(Size, Size);
            PaintMeshTriangle(image, 2, PaintState.Remove);
            PaintMeshTriangle(image, 3, PaintState.Remove);

            PaintAdjustMask.Apply(mask, image, add: true, remove: true, edgeOffset: 3, coverage: _coverage);
            var pixels = FloodFill.ReadR8(mask);

            Assert.That(At(pixels, 17, 32), Is.EqualTo(255), "消した右の四角の、左の四角との境目から 2px 目");
            Assert.That(At(pixels, 23, 32), Is.EqualTo(0), "消した右の四角の真ん中");
        }

        [Test]
        public void ブラシのはみ出しをマイナスにすると残した所の縁が削れる()
        {
            var mask = BuildRawChartA();
            var image = new PaintAdjustImage(Size, Size);
            PaintMeshTriangle(image, 2, PaintState.Remove);
            PaintMeshTriangle(image, 3, PaintState.Remove);

            PaintAdjustMask.Apply(mask, image, add: true, remove: true, edgeOffset: -3, coverage: _coverage);
            var pixels = FloodFill.ReadR8(mask);

            Assert.That(At(pixels, 14, 32), Is.EqualTo(0), "残した左の四角の、境目から 2px 内側");
            Assert.That(At(pixels, 9, 32), Is.EqualTo(255), "残した左の四角の真ん中");
        }

        [Test]
        public void ブラシのはみ出しをプラスにすると足した所が隣の面へ広がる()
        {
            var mask = BuildRawChartA();
            var image = new PaintAdjustImage(Size, Size);
            // チャート B の右下の三角形。y = 32 での対角線は x = 46
            PaintMeshTriangle(image, 4, PaintState.Add);

            PaintAdjustMask.Apply(mask, image, add: true, remove: true, edgeOffset: 2, coverage: _coverage);
            var pixels = FloodFill.ReadR8(mask);

            Assert.That(At(pixels, 44, 32), Is.EqualTo(255), "足していない左上の三角形の、対角線から 2px");
            Assert.That(At(pixels, 40, 32), Is.EqualTo(0), "足していない左上の三角形の奥");
        }

        [Test]
        public void ブラシのはみ出しをマイナスにすると足した所が狭まる()
        {
            var mask = BuildRawChartA();
            var image = new PaintAdjustImage(Size, Size);
            PaintMeshTriangle(image, 4, PaintState.Add);

            PaintAdjustMask.Apply(mask, image, add: true, remove: true, edgeOffset: -2, coverage: _coverage);
            var pixels = FloodFill.ReadR8(mask);

            Assert.That(At(pixels, 46, 32), Is.EqualTo(0), "足した右下の三角形の、対角線のすぐ内側");
            Assert.That(At(pixels, 54, 32), Is.EqualTo(255), "足した右下の三角形の奥");
        }

        [Test]
        public void 足した所の画像は足した画素だけが1になる()
        {
            var image = new PaintAdjustImage(Size, Size);
            PaintMeshTriangle(image, 4, PaintState.Add);

            var added = PaintAdjustMask.CreateStateMask(image, PaintState.Add, Size, Size);
            _masks.Add(added);
            var pixels = FloodFill.ReadR8(added);

            Assert.That(At(pixels, 56, 10), Is.EqualTo(255));
            Assert.That(At(pixels, 10, 32), Is.EqualTo(0));
            Assert.That(PaintAdjustMask.CreateStateMask(image, PaintState.Remove, Size, Size), Is.Null, "消した所が無ければ作らない");
        }
    }
}
