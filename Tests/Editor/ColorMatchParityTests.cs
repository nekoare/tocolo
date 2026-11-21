using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// お手本に合わせる（ColorShift.compute の CSMatch）と ColorMatchCpu が同じ式であることの確認。
    /// 8×8 のランダム色 × マスク 0 / 0.5 / 1 で、sRGB に戻した値の差が 2/255 以内
    /// </summary>
    public class ColorMatchParityTests
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
            Assert.That(RecolorPipeline.IsShiftAvailable, Is.True, "ColorShift.computeが読み込めません（.metaのGUIDを確認）");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var rt in _renderTextures) RecolorPipeline.DestroyWorkTexture(rt);
            _renderTextures.Clear();
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private static Color[] RandomLinearPixels(int seed)
        {
            var random = new System.Random(seed);
            var pixels = new Color[Size * Size];
            for (int i = 0; i < pixels.Length; i++)
            {
                var srgb = new Color((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                Vector3 lin = OklabConverter.SRGBToLinear(srgb);
                pixels[i] = new Color(lin.x, lin.y, lin.z, (float)random.NextDouble());
            }
            return pixels;
        }

        private static byte[] MaskBytes()
        {
            var bytes = new byte[Size * Size];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (i % 3) switch { 0 => (byte)0, 1 => (byte)128, _ => (byte)255 };
            return bytes;
        }

        private static MatchProfile Profile(int seed, float lo, float hi, float a0, float aSlope, float b0, float bSlope, float noise)
        {
            var random = new System.Random(seed);
            var labs = new List<Vector3>();
            for (int i = 0; i < 3000; i++)
            {
                float l = lo + (hi - lo) * (float)random.NextDouble();
                labs.Add(new Vector3(l,
                    a0 + aSlope * l + ((float)random.NextDouble() - 0.5f) * noise,
                    b0 + bSlope * l + ((float)random.NextDouble() - 0.5f) * noise));
            }
            return MatchProfile.FromOklab(labs);
        }

        private Texture2D MakeSource(Color[] linearPixels)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels(linearPixels);
            tex.Apply(false);
            _cleanup.Add(tex);
            return tex;
        }

        private Texture2D MakeMask(byte[] bytes)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.R8, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            tex.LoadRawTextureData(bytes);
            tex.Apply(false);
            _cleanup.Add(tex);
            return tex;
        }

        private Color[] RunGpu(Texture2D src, Texture2D mask, in MatchParams p)
        {
            var dst = RecolorPipeline.CreateWorkTexture(Size, Size, "ColorMatchParityTests_Dst");
            _renderTextures.Add(dst);
            RecolorPipeline.Match(src, mask, dst, p);
            var tex = new Texture2D(Size, Size, TextureFormat.RGBAHalf, false, true);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = dst;
                tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);
                tex.Apply(false);
                return tex.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(tex);
            }
        }

        private static void AssertParity(Color[] src, byte[] mask, Color[] gpu, in MatchParams p)
        {
            for (int i = 0; i < src.Length; i++)
            {
                Color cpu = ColorMatchCpu.MatchLinear(src[i], mask[i] / 255f, p);
                Color cpuSrgb = OklabConverter.LinearToSRGB(new Vector3(cpu.r, cpu.g, cpu.b));
                Color gpuSrgb = OklabConverter.LinearToSRGB(new Vector3(gpu[i].r, gpu[i].g, gpu[i].b));
                Assert.That(gpuSrgb.r, Is.EqualTo(cpuSrgb.r).Within(SrgbTolerance), $"画素{i}のR（マスク{mask[i]}）");
                Assert.That(gpuSrgb.g, Is.EqualTo(cpuSrgb.g).Within(SrgbTolerance), $"画素{i}のG（マスク{mask[i]}）");
                Assert.That(gpuSrgb.b, Is.EqualTo(cpuSrgb.b).Within(SrgbTolerance), $"画素{i}のB（マスク{mask[i]}）");
            }
        }

        [Test]
        public void ランダムな色で_GPU_と_CPU_が一致する()
        {
            var src = RandomLinearPixels(1);
            var mask = MaskBytes();
            var target = Profile(1, 0.2f, 0.8f, 0.01f, 0.02f, 0.03f, -0.02f, 0.02f);
            var reference = Profile(2, 0.4f, 0.95f, 0.12f, -0.1f, -0.03f, 0.08f, 0.01f);
            Assert.That(MatchParams.TryBuild(target, reference, RecolorEdit.CreateNew(), out var p), Is.True);
            AssertParity(src, mask, RunGpu(MakeSource(src), MakeMask(mask), p), p);
        }

        [Test]
        public void 微調整を入れても_GPU_と_CPU_が一致する()
        {
            var src = RandomLinearPixels(2);
            var mask = MaskBytes();
            var target = Profile(3, 0.1f, 0.6f, 0f, 0f, 0f, 0f, 0.01f);
            var reference = Profile(4, 0.5f, 0.9f, -0.05f, 0.02f, 0.06f, 0f, 0.03f);
            var edit = RecolorEdit.CreateNew();
            edit.matchLightness = 0.1f;
            edit.matchHue = 60f;
            edit.matchChromaShift = 0.05f;
            edit.matchShadow = 0.6f;
            edit.matchHighlight = -0.4f;
            edit.gamma = 0.7f;
            edit.strength = 0.8f;
            Assert.That(MatchParams.TryBuild(target, reference, edit, out var p), Is.True);
            AssertParity(src, mask, RunGpu(MakeSource(src), MakeMask(mask), p), p);
        }
    }
}
