using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>変換の流れ（RecolorPipeline.ApplyJobs）の中での「お手本に合わせる」編集</summary>
    public class ColorMatchPipelineTests
    {
        private const int Size = 8;
        private const float SrgbTolerance = 2f / 255f;
        private readonly List<Object> _cleanup = new List<Object>();
        private readonly List<RenderTexture> _renderTextures = new List<RenderTexture>();

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                Assert.Ignore("この環境はcompute shader（ARGBHalfへの書き込み）に対応していません");
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var rt in _renderTextures) RecolorPipeline.DestroyWorkTexture(rt);
            _renderTextures.Clear();
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private static Color[] Pixels()
        {
            var random = new System.Random(5);
            var pixels = new Color[Size * Size];
            for (int i = 0; i < pixels.Length; i++)
            {
                Vector3 lin = OklabConverter.SRGBToLinear(new Color((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble()));
                pixels[i] = new Color(lin.x, lin.y, lin.z, 1f);
            }
            return pixels;
        }

        /// <summary>左半分だけ 1 のマスク</summary>
        private RenderTexture LeftHalfMask()
        {
            var tex = new Texture2D(Size, Size, TextureFormat.R8, false, true) { filterMode = FilterMode.Point };
            var bytes = new byte[Size * Size];
            for (int y = 0; y < Size; y++) for (int x = 0; x < Size; x++) bytes[y * Size + x] = x < Size / 2 ? (byte)255 : (byte)0;
            tex.LoadRawTextureData(bytes);
            tex.Apply(false);
            _cleanup.Add(tex);
            var rt = MaskTextures.Create(Size, Size, "ColorMatchPipelineTests_Mask");
            var previous = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = previous;
            return rt;
        }

        private RenderTexture Work(Color[] pixels)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point };
            tex.SetPixels(pixels);
            tex.Apply(false);
            _cleanup.Add(tex);
            var rt = RecolorPipeline.CreateWorkTexture(Size, Size, "ColorMatchPipelineTests_Work");
            var previous = RenderTexture.active;
            Graphics.Blit(tex, rt);
            RenderTexture.active = previous;
            return rt;
        }

        private static Color[] Read(RenderTexture rt)
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

        private static MatchProfile Profile(int seed, float lo, float hi, float a)
        {
            var random = new System.Random(seed);
            var labs = new List<Vector3>();
            for (int i = 0; i < 2000; i++) labs.Add(new Vector3(lo + (hi - lo) * (float)random.NextDouble(), a, 0.01f));
            return MatchProfile.FromOklab(labs);
        }

        private static RecolorEdit MatchEdit()
        {
            var edit = RecolorEdit.CreateNew();
            edit.matchEnabled = true;
            edit.hasTarget = true;
            return edit;
        }

        [Test]
        public void お手本に合わせる編集は範囲の中だけ変える()
        {
            var pixels = Pixels();
            var mask = LeftHalfMask();
            var edit = MatchEdit();
            Assert.That(MatchParams.TryBuild(Profile(1, 0.2f, 0.9f, 0f), Profile(2, 0.4f, 0.8f, 0.08f), edit, out var p), Is.True);
            var job = new EditJob { edit = edit, mask = mask, match = p };

            var result = RecolorPipeline.ApplyJobs(Work(pixels), new[] { job });
            _renderTextures.Add(result);
            _renderTextures.Add(mask);
            var output = Read(result);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    Color expected = x < Size / 2 ? ColorMatchCpu.MatchLinear(pixels[i], 1f, p) : pixels[i];
                    Color e = OklabConverter.LinearToSRGB(new Vector3(expected.r, expected.g, expected.b));
                    Color o = OklabConverter.LinearToSRGB(new Vector3(output[i].r, output[i].g, output[i].b));
                    Assert.That(o.r, Is.EqualTo(e.r).Within(SrgbTolerance), $"({x},{y})のR");
                    Assert.That(o.g, Is.EqualTo(e.g).Within(SrgbTolerance), $"({x},{y})のG");
                    Assert.That(o.b, Is.EqualTo(e.b).Within(SrgbTolerance), $"({x},{y})のB");
                }
            }
        }

        [Test]
        public void 写し方の無いお手本に合わせる編集は素通し()
        {
            var pixels = Pixels();
            var mask = LeftHalfMask();
            _renderTextures.Add(mask);
            // 目標色が決まっていても、お手本に合わせる編集は目標色の式に化けない
            var edit = MatchEdit();
            edit.targetColor = Color.red;
            var job = new EditJob { edit = edit, mask = mask };

            var result = RecolorPipeline.ApplyJobs(Work(pixels), new[] { job });
            _renderTextures.Add(result);
            var output = Read(result);
            for (int i = 0; i < pixels.Length; i++)
            {
                Assert.That(output[i].r, Is.EqualTo(pixels[i].r).Within(1e-3f), $"画素{i}");
                Assert.That(output[i].g, Is.EqualTo(pixels[i].g).Within(1e-3f), $"画素{i}");
            }
        }
    }
}
