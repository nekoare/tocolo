using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 島マスク（GPU）のテスト。64×64 の UV に 2 つの長方形チャートを置く:
    ///   チャート A: x = 4..28 px, y = 4..60 px（三角形 0, 1）
    ///   チャート B: x = 32..60 px, y = 4..60 px（三角形 2, 3）
    /// 間の隙間は 4px。辺の 1px 許容でそれぞれ 1px 太って塗られるので、どのチャートにも属さない画素は x = 29, 30 の 2 列だけ
    /// </summary>
    public class IslandMaskBuilderTests
    {
        private const int Size = 64;

        private readonly List<Object> _cleanup = new List<Object>();
        private readonly List<RenderTexture> _masks = new List<RenderTexture>();

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.R8))
            {
                Assert.Ignore("この環境は compute shader（R8 への書き込み）に対応していません");
            }
            Assert.That(UvRasterizer.IsAvailable, Is.True, "Rasterize.compute が読み込めません（.meta の GUID を確認）");
            Assert.That(Morphology.IsAvailable, Is.True, "Morphology.compute が読み込めません（.meta の GUID を確認）");
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

        private Mesh MakeTwoChartMesh()
        {
            var mesh = new Mesh();
            _cleanup.Add(mesh);
            // 位置は判定に使わないので UV と同じ配置にしておく
            var uv = new[]
            {
                Px(4, 4), Px(28, 4), Px(28, 60), Px(4, 60),
                Px(32, 4), Px(60, 4), Px(60, 60), Px(32, 60),
            };
            var vertices = new Vector3[uv.Length];
            for (int i = 0; i < uv.Length; i++) vertices[i] = uv[i];
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            return mesh;
        }

        private RenderTexture BuildIsland(Mesh mesh, int padding, bool withCoverage)
        {
            RenderTexture coverage = null;
            if (withCoverage)
            {
                var texture = new Texture2D(Size, Size);
                _cleanup.Add(texture);
                coverage = CoverageMask.GetOrBuild(
                    texture,
                    new List<(Mesh, int, Vector2, Vector2)> { (mesh, 0, Vector2.one, Vector2.zero) },
                    Size,
                    out int skippedUnreadable);
                Assert.That(coverage, Is.Not.Null);
                Assert.That(skippedUnreadable, Is.EqualTo(0));
            }
            var result = IslandMaskBuilder.Build(new IslandRequest
            {
                mesh = mesh,
                submesh = 0,
                seedTriangle = 0,
                seedUv = Px(16, 32),
                padding = padding,
                cleanupRadius = 0,
                width = Size,
                height = Size,
                coverage = coverage,
            });
            Assert.That(result, Is.Not.Null);
            _masks.Add(result);
            return result;
        }

        private static byte At(byte[] pixels, int x, int y) => pixels[y * Size + x];

        [Test]
        public void 片方のチャートをクリックすると_もう片方の画素は0()
        {
            var mesh = MakeTwoChartMesh();

            var pixels = FloodFill.ReadR8(BuildIsland(mesh, padding: 0, withCoverage: false));

            Assert.That(At(pixels, 16, 32), Is.EqualTo(255), "クリックしたチャートの内側");
            for (int y = 6; y <= 58; y++)
            {
                for (int x = 34; x <= 58; x++)
                {
                    Assert.That(At(pixels, x, y), Is.EqualTo(0), $"もう片方のチャート ({x},{y})");
                }
            }
        }

        [Test]
        public void パディングは被覆の外にだけ広がる()
        {
            var mesh = MakeTwoChartMesh();

            var pixels = FloodFill.ReadR8(BuildIsland(mesh, padding: 4, withCoverage: true));

            // 被覆の外（x = 29, 30）は埋まる（ぼかし後も 0.5 以上）
            Assert.That(At(pixels, 29, 32), Is.GreaterThanOrEqualTo(128));
            Assert.That(At(pixels, 30, 32), Is.GreaterThanOrEqualTo(128));
            // 隣のチャート B（1px 縁を含めて x ≥ 31）へは広がらない。
            // x = 31 は被覆の内側なので、ぼかしもにじませない（制約なしのぼかしだと 3/9 が漏れる列）
            Assert.That(At(pixels, 31, 32), Is.EqualTo(0));
            // x = 32 は制約なしなら 4px 膨張で 1 になり、ぼかし後も 0 にならない列
            Assert.That(At(pixels, 32, 32), Is.EqualTo(0));
            Assert.That(At(pixels, 40, 32), Is.EqualTo(0));
        }

        [Test]
        public void 細い隣チャート越しにはパディングが滲まない()
        {
            // チャート A（島）: x = 4..20 px, y = 4..60 px → 1px 縁込みで画素 x = 3..20
            // チャート B（幅 2px）: x = 25..27 px, 縦は画像の外まで → 1px 縁込みで画素 x = 24..27、全行
            // 被覆の外: 間の x = 21..23 と、B の向こう側 x = 28..63（B が全行を塞ぐので、A 側からは B を越えないと届かない）
            var mesh = new Mesh();
            _cleanup.Add(mesh);
            var uv = new[]
            {
                Px(4, 4), Px(20, 4), Px(20, 60), Px(4, 60),
                Px(25, -2), Px(27, -2), Px(27, 66), Px(25, 66),
            };
            var vertices = new Vector3[uv.Length];
            for (int i = 0; i < uv.Length; i++) vertices[i] = uv[i];
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };

            var pixels = FloodFill.ReadR8(BuildIsland(mesh, padding: 8, withCoverage: true));

            // 間の被覆の外は埋まる
            Assert.That(At(pixels, 21, 32), Is.GreaterThanOrEqualTo(128));
            Assert.That(At(pixels, 23, 32), Is.GreaterThanOrEqualTo(128));
            // 細いチャート B には広がらない
            for (int x = 24; x <= 27; x++) Assert.That(At(pixels, x, 32), Is.EqualTo(0), $"チャート B ({x},32)");
            // B の向こう側の被覆の外へは、島から 8px 以内（x = 28 は島の縁 x = 20 から 8px）でも滲まない
            for (int y = 0; y < Size; y++)
            {
                for (int x = 28; x <= 36; x++)
                {
                    Assert.That(At(pixels, x, y), Is.EqualTo(0), $"B の向こう側 ({x},{y})");
                }
            }
        }

        [Test]
        public void 隣接三角形の共有エッジに穴が無い()
        {
            var mesh = MakeTwoChartMesh();

            var pixels = FloodFill.ReadR8(BuildIsland(mesh, padding: 0, withCoverage: false));

            // チャート A の内側（縁のぼかしがかからない範囲）はすべて 1
            for (int y = 6; y <= 58; y++)
            {
                for (int x = 6; x <= 26; x++)
                {
                    Assert.That(At(pixels, x, y), Is.EqualTo(255), $"({x},{y})");
                }
            }
        }

        [Test]
        public void ラスタライズ単体でも対角線上の画素中心に穴が無い()
        {
            // 対角線 y = x が画素中心 (i+0.5, i+0.5) をちょうど通る 2 三角形のクアッド
            var uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            var triangles = new[] { 0, 1, 2, 0, 2, 3 };
            var verts = new List<Vector2>();
            UvRasterizer.AppendTriangle(verts, uv, triangles, 0, Size, Size, Vector2.one, Vector2.zero);
            UvRasterizer.AppendTriangle(verts, uv, triangles, 1, Size, Size, Vector2.one, Vector2.zero);
            var rt = MaskTextures.Create(Size, Size, "Test");
            _masks.Add(rt);

            UvRasterizer.Clear(rt);
            UvRasterizer.Draw(rt, verts);
            var pixels = FloodFill.ReadR8(rt);

            for (int i = 0; i < pixels.Length; i++)
            {
                Assert.That(pixels[i], Is.EqualTo(255), $"({i % Size},{i / Size})");
            }
        }

        [Test]
        public void UV面積ゼロの三角形は_全面化せず_その線分の近傍だけ塗る()
        {
            // 他ツールの事例（2026-09-25）: 重心判定の分母が 0 になる退化三角形で全画素が内側扱いになり、マスクが全面化した。
            // 外接矩形（±1px）で全面化は防げるが、3 点共線では矩形の中が丸ごと塗られていた（22×22=484 画素）。
            // 分母ガードで線分の 1px 近傍だけになることを、2 頂点同一 / 3 頂点同一 / 3 点共線 の 3 通りで確かめる
            var cases = new (string name, Vector2[] uv)[]
            {
                ("2頂点同一", new[] { Px(10, 10), Px(10, 10), Px(30, 10) }),
                ("3頂点同一", new[] { Px(20, 20), Px(20, 20), Px(20, 20) }),
                ("3点共線", new[] { Px(10, 10), Px(20, 20), Px(30, 30) }),
            };
            var triangles = new[] { 0, 1, 2 };
            foreach (var (name, uv) in cases)
            {
                var verts = new List<Vector2>();
                UvRasterizer.AppendTriangle(verts, uv, triangles, 0, Size, Size, Vector2.one, Vector2.zero);
                var rt = MaskTextures.Create(Size, Size, "Test");
                _masks.Add(rt);
                UvRasterizer.Clear(rt);
                UvRasterizer.Draw(rt, verts);
                var pixels = FloodFill.ReadR8(rt);

                int painted = 0;
                for (int i = 0; i < pixels.Length; i++) if (pixels[i] != 0) painted++;
                // 線分の長さ ≤ 29px × 幅 3px 程度。全面（4096）とは桁が違う
                Assert.That(painted, Is.GreaterThan(0).And.LessThan(200), name);
                // 外接矩形の 2px 外は塗られない
                Assert.That(At(pixels, 50, 50), Is.EqualTo(0), name);
                Assert.That(At(pixels, 2, 2), Is.EqualTo(0), name);
            }
        }

        [Test]
        public void UVのyが大きい側を塗ると_ReadR8でも行番号の大きい側が塗られる()
        {
            // compute の Out[x, y] と ReadPixels の行（y = 0 が下）の向きが一致していることを固定する。
            // 下辺 y = 33 px の大きな三角形で y ≥ 33 を覆う。辺の 1px 許容で行 32（中心 32.5）までは塗られ、
            // 行 31（中心 31.5、辺から 1.5px）は塗られない
            float edge = 33f / Size;
            var uv = new[] { new Vector2(-1f, edge), new Vector2(3f, edge), new Vector2(1f, 5f) };
            var triangles = new[] { 0, 1, 2 };
            var verts = new List<Vector2>();
            UvRasterizer.AppendTriangle(verts, uv, triangles, 0, Size, Size, Vector2.one, Vector2.zero);
            var rt = MaskTextures.Create(Size, Size, "Test");
            _masks.Add(rt);

            UvRasterizer.Clear(rt);
            UvRasterizer.Draw(rt, verts);
            var pixels = FloodFill.ReadR8(rt);

            for (int y = 0; y < Size; y++)
            {
                byte expected = y >= 32 ? (byte)255 : (byte)0;
                for (int x = 0; x < Size; x++)
                {
                    Assert.That(At(pixels, x, y), Is.EqualTo(expected), $"({x},{y})");
                }
            }
        }

        [Test]
        public void WriteR8してReadR8すると同じ値に戻り_アクティブなRTも元に戻る()
        {
            // 縦横で大きさを変え、上下で非対称な模様にする（行の取り違え・上下反転を検出する）
            const int w = 48;
            const int h = 32;
            var data = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    data[y * w + x] = (y == 5 || (x + 2 * y) % 7 == 0) ? (byte)255 : (byte)0;
                }
            }
            var rt = MaskTextures.Create(w, h, "Test");
            _masks.Add(rt);
            var other = MaskTextures.Create(4, 4, "TestActive");
            _masks.Add(other);

            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = other;
                FloodFill.WriteR8(data, rt);
                Assert.That(RenderTexture.active, Is.EqualTo(other), "WriteR8 がアクティブな RT を書き換えたまま戻していない");
            }
            finally
            {
                RenderTexture.active = previous;
            }
            var read = FloodFill.ReadR8(rt);

            Assert.That(read, Is.EqualTo(data));
        }

        [Test]
        public void TilingとOffsetを指定すると_その分ずれた位置に島が塗られる()
        {
            // UV 上は x = 0.05..0.25, y = 0.1..0.3 の四角。Tiling (2,2) / Offset (0.25,0) を掛けると
            // x = 0.35..0.75, y = 0.2..0.6（px では x = 22.4..48, y = 12.8..38.4）へ移る
            var mesh = new Mesh();
            _cleanup.Add(mesh);
            var uv = new[] { new Vector2(0.05f, 0.1f), new Vector2(0.25f, 0.1f), new Vector2(0.25f, 0.3f), new Vector2(0.05f, 0.3f) };
            var vertices = new Vector3[uv.Length];
            for (int i = 0; i < uv.Length; i++) vertices[i] = uv[i];
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };

            var result = IslandMaskBuilder.Build(new IslandRequest
            {
                mesh = mesh,
                submesh = 0,
                seedTriangle = 0,
                // Tiling/Offset 適用後のクリック位置（PickHit.uv 相当）
                seedUv = new Vector2(0.55f, 0.4f),
                uvScale = new Vector2(2f, 2f),
                uvOffset = new Vector2(0.25f, 0f),
                padding = 0,
                cleanupRadius = 0,
                width = Size,
                height = Size,
                coverage = null,
            });
            Assert.That(result, Is.Not.Null);
            _masks.Add(result);
            var pixels = FloodFill.ReadR8(result);

            Assert.That(At(pixels, 35, 25), Is.EqualTo(255), "ずらした先の内側");
            Assert.That(At(pixels, 9, 12), Is.EqualTo(0), "生 UV の位置（ずらす前）");
        }
    }

    /// <summary>FloodFill.RestrictToConnected（CPU のみ・GPU 不要）のテスト</summary>
    public class FloodFillTests
    {
        private const int W = 8;
        private const int H = 6;

        private static byte[] Parse(params string[] rowsTopToBottom)
        {
            // 見た目どおりに書けるよう、先頭の文字列を一番上の行（y = H - 1）とする
            var mask = new byte[W * H];
            for (int r = 0; r < H; r++)
            {
                int y = H - 1 - r;
                for (int x = 0; x < W; x++) mask[y * W + x] = rowsTopToBottom[r][x] == '#' ? (byte)1 : (byte)0;
            }
            return mask;
        }

        [Test]
        public void 種から4連結で届かない塊と対角だけで接する画素は消える()
        {
            var mask = Parse(
                "##......",
                "##......",
                "..#.....",
                "......##",
                "......##",
                "........");
            var expected = Parse(
                "##......",
                "##......",
                "........",
                "........",
                "........",
                "........");

            FloodFill.RestrictToConnected(mask, W, H, new Vector2Int(0, H - 1));

            Assert.That(mask, Is.EqualTo(expected));
        }

        [Test]
        public void 種の画素が0なら何も変えない()
        {
            var mask = Parse(
                "##......",
                "##......",
                "........",
                "......##",
                "......##",
                "........");
            var before = (byte[])mask.Clone();

            FloodFill.RestrictToConnected(mask, W, H, new Vector2Int(4, 2));

            Assert.That(mask, Is.EqualTo(before));
        }
    }
}
