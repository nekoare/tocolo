using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.NDMF;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 選択範囲のハイライト（Highlight.compute、GPU）のテスト。
    /// 8×8 の灰色の画像に、左半分がマスク 1・右半分がマスク 0 のマスクを掛ける
    /// </summary>
    public class HighlightTests
    {
        private const int Size = 8;
        // 作業 RT は ARGBHalf なので半精度で丸まる（1/2048 程度）
        private const float HalfTolerance = 1e-3f;

        private static readonly Color Gray = new Color(0.4f, 0.4f, 0.4f, 0.7f);

        private readonly List<Object> _cleanup = new List<Object>();
        private readonly List<RenderTexture> _works = new List<RenderTexture>();
        private readonly List<RenderTexture> _masks = new List<RenderTexture>();

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                Assert.Ignore("この環境は compute shader（ARGBHalf への書き込み）に対応していません");
            }
            Assert.That(SelectionHighlight.IsAvailable, Is.True, "Highlight.compute が読み込めません（.meta の GUID を確認）");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var rt in _works) RecolorPipeline.DestroyWorkTexture(rt);
            _works.Clear();
            foreach (var rt in _masks) MaskTextures.Destroy(rt);
            _masks.Clear();
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private static bool Selected(int x) => x < Size / 2;

        private RenderTexture MakeSource()
        {
            var pixels = new Color[Size * Size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Gray;
            var tex = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _cleanup.Add(tex);
            tex.SetPixels(pixels);
            tex.Apply(false);
            var rt = RecolorPipeline.CreateWorkTexture(Size, Size, "HighlightTests_Source");
            _works.Add(rt);
            Blit(tex, rt);
            return rt;
        }

        private RenderTexture MakeMask()
        {
            var bytes = new byte[Size * Size];
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) bytes[y * Size + x] = Selected(x) ? (byte)255 : (byte)0;
            var tex = new Texture2D(Size, Size, TextureFormat.R8, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            _cleanup.Add(tex);
            tex.LoadRawTextureData(bytes);
            tex.Apply(false);
            var rt = MaskTextures.Create(Size, Size, "HighlightTests_Mask");
            _masks.Add(rt);
            Blit(tex, rt);
            return rt;
        }

        private static void Blit(Texture src, RenderTexture dst)
        {
            var previous = RenderTexture.active;
            try
            {
                // どちらも Linear なので色空間の変換は掛からない
                Graphics.Blit(src, dst);
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        private static Color[] ReadLinear(RenderTexture rt)
        {
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                tex.Apply(false);
                return tex.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(tex);
            }
        }

        private Color[] Run()
        {
            var src = MakeSource();
            var mask = MakeMask();
            var dst = RecolorPipeline.CreateWorkTexture(Size, Size, "HighlightTests_Dst");
            _works.Add(dst);
            SelectionHighlight.Apply(src, mask, dst);
            return ReadLinear(dst);
        }

        [Test]
        public void マスク1の画素は橙の方へ動く()
        {
            var result = Run();
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (!Selected(x)) continue;
                    var c = result[y * Size + x];
                    Assert.That(c.r, Is.GreaterThan(Gray.r + 0.01f), $"({x},{y}) の R が増えていません");
                    Assert.That(c.b, Is.LessThan(Gray.b - 0.01f), $"({x},{y}) の B が減っていません");
                }
            }
        }

        [Test]
        public void マスク0の画素は変わらない()
        {
            var result = Run();
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (Selected(x)) continue;
                    var c = result[y * Size + x];
                    Assert.That(c.r, Is.EqualTo(Gray.r).Within(HalfTolerance), $"({x},{y}) の R");
                    Assert.That(c.g, Is.EqualTo(Gray.g).Within(HalfTolerance), $"({x},{y}) の G");
                    Assert.That(c.b, Is.EqualTo(Gray.b).Within(HalfTolerance), $"({x},{y}) の B");
                }
            }
        }

        [Test]
        public void α_は変わらない()
        {
            var result = Run();
            for (int i = 0; i < result.Length; i++)
            {
                Assert.That(result[i].a, Is.EqualTo(Gray.a).Within(HalfTolerance), $"画素 {i} の α");
            }
        }
    }
}
