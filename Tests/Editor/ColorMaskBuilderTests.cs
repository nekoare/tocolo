using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 色モードの選択マスク（GPU）のテスト。8×8 の画像に 2 色を列で塗り分けて使う。
    /// 最後に 1px ぼかすので、色の境目に接する列は中間の値になる。判定は境目から 2px 以上離れた列で行う
    /// </summary>
    public class ColorMaskBuilderTests
    {
        private const int Size = 8;

        private static readonly Color Red = new Color(0.8f, 0.05f, 0.05f, 1f);
        private static readonly Color Blue = new Color(0.05f, 0.1f, 0.8f, 1f);

        private readonly List<Object> _cleanup = new List<Object>();
        private readonly List<RenderTexture> _masks = new List<RenderTexture>();
        private readonly List<RenderTexture> _works = new List<RenderTexture>();

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.R8)
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                Assert.Ignore("この環境は compute shader（R8 / ARGBHalf への書き込み）に対応していません");
            }
            Assert.That(ColorMaskBuilder.IsAvailable, Is.True, "ColorSelect.compute / Morphology.compute が読み込めません（.meta の GUID を確認）");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var rt in _masks) MaskTextures.Destroy(rt);
            _masks.Clear();
            foreach (var rt in _works) RecolorPipeline.DestroyWorkTexture(rt);
            _works.Clear();
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        /// <summary>列 x の色を columnColor(x) にした線形の作業 RT</summary>
        private RenderTexture MakeSource(System.Func<int, Color> columnColor)
        {
            var pixels = new Color[Size * Size];
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) pixels[y * Size + x] = columnColor(x);

            var tex = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _cleanup.Add(tex);
            tex.SetPixels(pixels);
            tex.Apply(false);
            var rt = RecolorPipeline.CreateWorkTexture(Size, Size, "ColorMaskBuilderTests_Source");
            _works.Add(rt);
            var previous = RenderTexture.active;
            try
            {
                // どちらも Linear なので色空間の変換は掛からない
                Graphics.Blit(tex, rt);
            }
            finally
            {
                RenderTexture.active = previous;
            }
            return rt;
        }

        private static Vector3 Lab(Color c) => OklabConverter.LinearRGBToOklab(new Vector3(c.r, c.g, c.b));

        private byte[] Build(RenderTexture source, Color seed, ColorScope scope, float threshold = 0.08f, float feather = 0.05f,
            RenderTexture islandMask = null, int padding = 0, RenderTexture coverage = null, Vector2Int? seedTexel = null)
        {
            var mask = ColorMaskBuilder.Build(new ColorRequest
            {
                sourceLinear = source,
                seedOklab = Lab(seed),
                seedTexel = seedTexel ?? new Vector2Int(1, 4),
                threshold = threshold,
                feather = feather,
                scope = scope,
                cleanupRadius = 0,
                padding = padding,
                islandMask = islandMask,
                coverage = coverage,
            });
            Assert.That(mask, Is.Not.Null);
            _masks.Add(mask);
            Assert.That(mask.width, Is.EqualTo(Size));
            Assert.That(mask.height, Is.EqualTo(Size));
            return FloodFill.ReadR8(mask);
        }

        private static byte At(byte[] pixels, int x, int y) => pixels[y * Size + x];

        /// <summary>x &lt; columns の列だけ 1 のマスク（島マスク・被覆に使う）</summary>
        private RenderTexture MakeColumnMask(int columns, string name)
        {
            var rt = MaskTextures.Create(Size, Size, name);
            _masks.Add(rt);
            var bytes = new byte[Size * Size];
            for (int y = 0; y < Size; y++) for (int x = 0; x < columns; x++) bytes[y * Size + x] = 255;
            FloodFill.WriteR8(bytes, rt);
            return rt;
        }

        [Test]
        public void 同じ色だけが選ばれる()
        {
            // x < 4 が赤、x ≥ 4 が青
            var source = MakeSource(x => x < 4 ? Red : Blue);

            var pixels = Build(source, Red, ColorScope.WholeTexture);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x <= 2; x++) Assert.That(At(pixels, x, y), Is.EqualTo(255), $"赤 ({x},{y})");
                for (int x = 5; x < Size; x++) Assert.That(At(pixels, x, y), Is.EqualTo(0), $"青 ({x},{y})");
            }
        }

        [Test]
        public void しきい値を上げると選ばれる面積は減らない()
        {
            // 列ごとに赤から青へ少しずつ変わる
            var source = MakeSource(x => Color.Lerp(Red, Blue, x / (float)(Size - 1)));

            long previous = -1;
            foreach (float threshold in new[] { 0f, 0.05f, 0.1f, 0.2f, 0.35f, 0.5f, 0.75f, 1f })
            {
                var pixels = Build(source, Red, ColorScope.WholeTexture, threshold, feather: 0.1f);
                long sum = 0;
                foreach (byte b in pixels) sum += b;
                Assert.That(sum, Is.GreaterThanOrEqualTo(previous), $"threshold = {threshold}");
                previous = sum;
            }
        }

        [Test]
        public void つながった範囲では離れた同じ色は選ばれない()
        {
            // 赤: x = 0, 1 と x = 6, 7。間の x = 2..5 は青
            var source = MakeSource(x => x <= 1 || x >= 6 ? Red : Blue);

            var contiguous = Build(source, Red, ColorScope.Contiguous);
            var whole = Build(source, Red, ColorScope.WholeTexture);

            for (int y = 0; y < Size; y++)
            {
                Assert.That(At(contiguous, 0, y), Is.EqualTo(255), $"クリックした側 (0,{y})");
                Assert.That(At(contiguous, 7, y), Is.EqualTo(0), $"離れた赤 (7,{y})");
                // テクスチャ全体なら離れた赤も選ばれる（上の 0 が色の判定のせいではないことの確認）
                Assert.That(At(whole, 7, y), Is.EqualTo(255), $"テクスチャ全体の離れた赤 (7,{y})");
            }
        }

        [Test]
        public void つながった範囲で種が核の外で近くにも核が無ければ空になる()
        {
            // 赤は x ≥ 6 だけ。種 (1,4) は青の上で、半径 2 以内（x ≤ 3）に赤（核）は無い
            var source = MakeSource(x => x >= 6 ? Red : Blue);

            var pixels = Build(source, Red, ColorScope.Contiguous, seedTexel: new Vector2Int(1, 4));

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++) Assert.That(At(pixels, x, y), Is.EqualTo(0), $"({x},{y})");
            }
        }

        [Test]
        public void つながった範囲で種が核の隣_1px_なら隣の核から到達する()
        {
            // 赤: x = 0, 1 と x = 6, 7。種 (2,4) は青の上だが、隣の x = 1 が核
            var source = MakeSource(x => x <= 1 || x >= 6 ? Red : Blue);

            var pixels = Build(source, Red, ColorScope.Contiguous, seedTexel: new Vector2Int(2, 4));

            for (int y = 0; y < Size; y++)
            {
                Assert.That(At(pixels, 0, y), Is.EqualTo(255), $"種の隣の核とつながった赤 (0,{y})");
                Assert.That(At(pixels, 7, y), Is.EqualTo(0), $"離れた赤 (7,{y})");
            }
        }

        [Test]
        public void 島の中では島の外は選ばれない()
        {
            var source = MakeSource(x => Red);
            // 島マスク: x < 4 だけ 1
            var island = MakeColumnMask(4, "ColorMaskBuilderTests_Island");

            var pixels = Build(source, Red, ColorScope.Island, islandMask: island);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x <= 2; x++) Assert.That(At(pixels, x, y), Is.EqualTo(255), $"島の中 ({x},{y})");
                for (int x = 5; x < Size; x++) Assert.That(At(pixels, x, y), Is.EqualTo(0), $"島の外 ({x},{y})");
            }
        }

        [Test]
        public void 島の中でパディングを指定すると島の被覆の外へ広がる()
        {
            // 島マスク（パディング 0）も被覆も x < 4。x ≥ 4 はどのチャートにも属さない隙間
            var source = MakeSource(x => Red);
            var island = MakeColumnMask(4, "ColorMaskBuilderTests_Island");
            var coverage = MakeColumnMask(4, "ColorMaskBuilderTests_Coverage");

            var pixels = Build(source, Red, ColorScope.Island, islandMask: island, padding: 4, coverage: coverage);

            for (int y = 0; y < Size; y++)
            {
                // 島の縁から 4px（x = 4..7）までパディングで塗られる。全部 1 なので 1px ぼかしても 1 のまま
                for (int x = 0; x < Size; x++) Assert.That(At(pixels, x, y), Is.EqualTo(255), $"({x},{y})");
            }
        }

        [Test]
        public void しきい値付近の裾は核の隣で残る()
        {
            // x < 4 が種の赤（w = 1 の核）、x ≥ 4 は w ≈ 0.2 になる色（赤から Oklab の L 方向へ ΔE = T + 0.713F ずらす）
            const float threshold = 0.08f;
            const float feather = 0.5f;
            float t = 0.35f * threshold * threshold;
            float f = 0.20f * feather;
            // 1 − smoothstep(0.713) ≈ 0.2
            var redLab = Lab(Red);
            var tailLin = OklabConverter.OklabToLinearRGB(redLab - new Vector3(t + 0.713f * f, 0f, 0f));
            var tail = new Color(tailLin.x, tailLin.y, tailLin.z, 1f);
            var source = MakeSource(x => x < 4 ? Red : tail);
            // 被覆を全面にすると、ぼかしはマスク内（w > 0）にしか書かない。
            // 裾が 0 に落ちていれば x = 4 は 0 のままになるので、ぼかしのにじみと区別できる
            var coverage = MakeColumnMask(Size, "ColorMaskBuilderTests_Coverage");

            var pixels = Build(source, Red, ColorScope.WholeTexture, threshold, feather, coverage: coverage);

            for (int y = 0; y < Size; y++)
            {
                Assert.That(At(pixels, 4, y), Is.GreaterThan(0), $"核の隣の裾 (4,{y})");
                // 核から 3px 以上離れた裾は消える（核の膨張は半径 2）
                Assert.That(At(pixels, 7, y), Is.EqualTo(0), $"核から離れた裾 (7,{y})");
            }
        }

        [Test]
        public void 透明な画素は選ばれない()
        {
            // 全部赤だが x ≥ 4 は α = 0
            var source = MakeSource(x => x < 4 ? Red : new Color(Red.r, Red.g, Red.b, 0f));

            var pixels = Build(source, Red, ColorScope.WholeTexture);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x <= 2; x++) Assert.That(At(pixels, x, y), Is.EqualTo(255), $"不透明 ({x},{y})");
                for (int x = 5; x < Size; x++) Assert.That(At(pixels, x, y), Is.EqualTo(0), $"透明 ({x},{y})");
            }
        }

        [Test]
        public void 島の中で島マスクが無ければ_null()
        {
            var source = MakeSource(x => Red);

            var mask = ColorMaskBuilder.Build(new ColorRequest
            {
                sourceLinear = source,
                seedOklab = Lab(Red),
                seedTexel = new Vector2Int(1, 4),
                threshold = 0.08f,
                feather = 0.05f,
                scope = ColorScope.Island,
                islandMask = null,
            });

            Assert.That(mask, Is.Null);
        }
    }
}
