using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// ColorShift.compute（GPU）と ColorShiftCpu（CPU）が同じ式であることの確認。
    /// 8×8 のランダム色 × マスク 0 / 0.5 / 1 で、sRGB に戻した値の差が 2/255 以内
    /// </summary>
    public class ColorShiftParityTests
    {
        private const int Size = 8;
        private const float SrgbTolerance = 2f / 255f;
        // 出力 RT は ARGBHalf なので α は半精度で丸まる（1/2048 程度）
        private const float HalfTolerance = 1e-3f;

        private readonly List<Object> _cleanup = new List<Object>();
        private readonly List<RenderTexture> _renderTextures = new List<RenderTexture>();

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                Assert.Ignore("この環境は compute shader（ARGBHalf への書き込み）に対応していません");
            }
            Assert.That(RecolorPipeline.IsShiftAvailable, Is.True, "ColorShift.compute が読み込めません（.meta の GUID を確認）");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var rt in _renderTextures) RecolorPipeline.DestroyWorkTexture(rt);
            _renderTextures.Clear();
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        // ── 入力 ──

        /// <summary>ランダムな sRGB 色を線形にした値（α もランダム）。種固定</summary>
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

        /// <summary>マスク 0 / 0.5 / 1 を順に並べる（R8 なので 0.5 は 128/255）</summary>
        private static byte[] MaskBytes()
        {
            var bytes = new byte[Size * Size];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (i % 3) switch { 0 => (byte)0, 1 => (byte)128, _ => (byte)255 };
            return bytes;
        }

        private Texture2D MakeSource(Color[] linearPixels)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.SetPixels(linearPixels);
            tex.Apply(false);
            _cleanup.Add(tex);
            return tex;
        }

        private Texture2D MakeMask(byte[] bytes)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.R8, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            tex.LoadRawTextureData(bytes);
            tex.Apply(false);
            _cleanup.Add(tex);
            return tex;
        }

        private static ShiftParams Params(Color targetSrgb, float lP05, float lP95)
        {
            Vector3 lch = OklabConverter.SRGBToOklch(targetSrgb);
            return new ShiftParams
            {
                targetOklch = new Vector4(lch.x, lch.y, lch.z, 0f),
                darkEndRatio = 0.7f,
                darkEndRatio2 = 0.7f,
                lToTarget = 1f,
                chromaToTarget = 1f,
                hueRetain = 0.25f,
                strength = 1f,
                strength2 = 1f,
                lP05 = lP05,
                lP95 = lP95,
                hDominant = 0.5f,
            };
        }

        // ── 実行と読み戻し ──

        private Color[] RunGpu(Texture2D src, Texture2D mask, in ShiftParams p, Texture positionMap = null)
        {
            var dst = RecolorPipeline.CreateWorkTexture(Size, Size, "ColorShiftParityTests_Dst");
            _renderTextures.Add(dst);
            RecolorPipeline.Shift(src, mask, dst, p, positionMap);
            return ReadLinear(dst);
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

        private static void AssertParity(Color[] src, byte[] mask, Color[] gpu, in ShiftParams p)
        {
            for (int i = 0; i < src.Length; i++)
            {
                Color cpu = ColorShiftCpu.ShiftLinear(src[i], mask[i] / 255f, p);
                Color cpuSrgb = OklabConverter.LinearToSRGB(new Vector3(cpu.r, cpu.g, cpu.b));
                Color gpuSrgb = OklabConverter.LinearToSRGB(new Vector3(gpu[i].r, gpu[i].g, gpu[i].b));
                Assert.That(gpuSrgb.r, Is.EqualTo(cpuSrgb.r).Within(SrgbTolerance), $"画素 {i} の R（マスク {mask[i]}）");
                Assert.That(gpuSrgb.g, Is.EqualTo(cpuSrgb.g).Within(SrgbTolerance), $"画素 {i} の G（マスク {mask[i]}）");
                Assert.That(gpuSrgb.b, Is.EqualTo(cpuSrgb.b).Within(SrgbTolerance), $"画素 {i} の B（マスク {mask[i]}）");
            }
        }

        // ── テスト ──

        private static readonly Color[] Targets =
        {
            new Color(0.9f, 0.1f, 0.15f), // ビビッドな赤
            Color.white,                  // 白（暗部の明るさが効く）
            new Color(0.1f, 0.15f, 0.4f), // 暗い青
        };

        [Test]
        public void 広いレンジで_GPU_と_CPU_が一致する()
        {
            var src = RandomLinearPixels(1);
            var mask = MaskBytes();
            var srcTex = MakeSource(src);
            var maskTex = MakeMask(mask);

            foreach (var target in Targets)
            {
                var p = Params(target, 0.25f, 0.85f);
                AssertParity(src, mask, RunGpu(srcTex, maskTex, p), p);
            }
        }

        [Test]
        public void 狭いレンジで_GPU_と_CPU_が一致する()
        {
            var src = RandomLinearPixels(2);
            var mask = MaskBytes();
            var srcTex = MakeSource(src);
            var maskTex = MakeMask(mask);

            foreach (var target in Targets)
            {
                // range 0.02（リマップと加算オフセットの混合）と range 0（加算オフセットのみ）
                var mixed = Params(target, 0.5f, 0.52f);
                AssertParity(src, mask, RunGpu(srcTex, maskTex, mixed), mixed);
                var offsetOnly = Params(target, 0.5f, 0.5f);
                AssertParity(src, mask, RunGpu(srcTex, maskTex, offsetOnly), offsetOnly);
            }
        }

        [Test]
        public void 強さを下げても_GPU_と_CPU_が一致する()
        {
            var src = RandomLinearPixels(3);
            var mask = MaskBytes();
            var p = Params(new Color(0.2f, 0.8f, 0.3f), 0.25f, 0.85f);
            p.strength = 0.6f;

            AssertParity(src, mask, RunGpu(MakeSource(src), MakeMask(mask), p), p);
        }

        [Test]
        public void 狭いレンジは傾き1の加算オフセットになる()
        {
            // 灰色（L = 0.6）を、選択の L が 0.5 に集まっている（range 0）ときに灰色の目標へ寄せる
            // 加算オフセット: L' = L − 中央 + L_t = 0.6 − 0.5 + L_t
            const float sourceL = 0.6f;
            float v = sourceL * sourceL * sourceL;
            var src = new Color[Size * Size];
            for (int i = 0; i < src.Length; i++) src[i] = new Color(v, v, v, 1f);
            var mask = new byte[Size * Size];
            for (int i = 0; i < mask.Length; i++) mask[i] = 255;

            var target = new Color(0.5f, 0.5f, 0.5f);
            float targetL = OklabConverter.SRGBToOklch(target).x;
            var p = Params(target, 0.5f, 0.5f);
            p.hueRetain = 0f;
            float expectedL = OklabConverter.SoftClip01(sourceL - 0.5f + targetL, 0.05f);

            Color cpu = ColorShiftCpu.ShiftLinear(src[0], 1f, p);
            float cpuL = OklabConverter.LinearRGBToOklab(new Vector3(cpu.r, cpu.g, cpu.b)).x;
            Assert.That(cpuL, Is.EqualTo(expectedL).Within(2e-3f), "CPU");

            var gpu = RunGpu(MakeSource(src), MakeMask(mask), p);
            float gpuL = OklabConverter.LinearRGBToOklab(new Vector3(gpu[0].r, gpu[0].g, gpu[0].b)).x;
            Assert.That(gpuL, Is.EqualTo(expectedL).Within(2e-3f), "GPU");

            // 広いレンジならリマップ側になって値が変わる（テストが切替を見分けられていることの確認）
            var wide = Params(target, 0.3f, 0.9f);
            wide.hueRetain = 0f;
            Color remapped = ColorShiftCpu.ShiftLinear(src[0], 1f, wide);
            float remappedL = OklabConverter.LinearRGBToOklab(new Vector3(remapped.r, remapped.g, remapped.b)).x;
            Assert.That(Mathf.Abs(remappedL - expectedL), Is.GreaterThan(0.05f));
        }

        [Test]
        public void 陰影の強調を最大にすると狭いレンジでも目標範囲いっぱいに写る()
        {
            // 灰色 2 種（L = 0.50 と 0.52）を半々に並べ、p05 = 0.50・p95 = 0.52（range 0.02 の狭いレンジ）とする
            const float lowL = 0.50f;
            const float highL = 0.52f;
            float vLow = lowL * lowL * lowL;
            float vHigh = highL * highL * highL;
            var src = new Color[Size * Size];
            for (int i = 0; i < src.Length; i++)
            {
                float v = i % 2 == 0 ? vLow : vHigh;
                src[i] = new Color(v, v, v, 1f);
            }
            var mask = new byte[Size * Size];
            for (int i = 0; i < mask.Length; i++) mask[i] = 255;

            var target = new Color(0.5f, 0.5f, 0.5f);
            float targetL = OklabConverter.SRGBToOklch(target).x;
            var p = Params(target, lowL, highL);
            p.hueRetain = 0f;
            p.shadingStretch = 1f;
            // リマップのみ: p05 の画素 → L_t × 暗部の明るさ、p95 の画素 → L_t
            float expectedLow = OklabConverter.SoftClip01(targetL * p.darkEndRatio, 0.05f);
            float expectedHigh = OklabConverter.SoftClip01(targetL, 0.05f);

            Color cpuLow = ColorShiftCpu.ShiftLinear(src[0], 1f, p);
            Color cpuHigh = ColorShiftCpu.ShiftLinear(src[1], 1f, p);
            Assert.That(OklabConverter.LinearRGBToOklab(new Vector3(cpuLow.r, cpuLow.g, cpuLow.b)).x,
                Is.EqualTo(expectedLow).Within(2e-3f), "CPU p05");
            Assert.That(OklabConverter.LinearRGBToOklab(new Vector3(cpuHigh.r, cpuHigh.g, cpuHigh.b)).x,
                Is.EqualTo(expectedHigh).Within(2e-3f), "CPU p95");

            var gpu = RunGpu(MakeSource(src), MakeMask(mask), p);
            Assert.That(OklabConverter.LinearRGBToOklab(new Vector3(gpu[0].r, gpu[0].g, gpu[0].b)).x,
                Is.EqualTo(expectedLow).Within(2e-3f), "GPU p05");
            Assert.That(OklabConverter.LinearRGBToOklab(new Vector3(gpu[1].r, gpu[1].g, gpu[1].b)).x,
                Is.EqualTo(expectedHigh).Within(2e-3f), "GPU p95");
            AssertParity(src, mask, gpu, p);

            // 強調 0 なら加算オフセット寄りで、p05 の画素は L_t × 暗部の明るさから離れる（テストが強調を見分けられていることの確認）
            p.shadingStretch = 0f;
            Color plain = ColorShiftCpu.ShiftLinear(src[0], 1f, p);
            float plainL = OklabConverter.LinearRGBToOklab(new Vector3(plain.r, plain.g, plain.b)).x;
            Assert.That(Mathf.Abs(plainL - expectedLow), Is.GreaterThan(0.05f));
        }

        [Test]
        public void アルファは変えない()
        {
            var src = RandomLinearPixels(4);
            var mask = MaskBytes();
            var p = Params(new Color(0.9f, 0.1f, 0.15f), 0.25f, 0.85f);

            var gpu = RunGpu(MakeSource(src), MakeMask(mask), p);

            for (int i = 0; i < src.Length; i++)
            {
                Assert.That(ColorShiftCpu.ShiftLinear(src[i], mask[i] / 255f, p).a, Is.EqualTo(src[i].a), $"CPU 画素 {i}");
                Assert.That(gpu[i].a, Is.EqualTo(src[i].a).Within(HalfTolerance), $"GPU 画素 {i}");
            }
        }

        [Test]
        public void マスク0の画素は元のまま()
        {
            var src = RandomLinearPixels(5);
            var mask = MaskBytes();
            var p = Params(new Color(0.9f, 0.1f, 0.15f), 0.25f, 0.85f);

            var gpu = RunGpu(MakeSource(src), MakeMask(mask), p);

            for (int i = 0; i < src.Length; i++)
            {
                if (mask[i] != 0) continue;
                Assert.That(ColorShiftCpu.ShiftLinear(src[i], 0f, p), Is.EqualTo(src[i]), $"CPU 画素 {i}");
                Assert.That(gpu[i].r, Is.EqualTo(src[i].r).Within(HalfTolerance), $"GPU 画素 {i}");
                Assert.That(gpu[i].g, Is.EqualTo(src[i].g).Within(HalfTolerance), $"GPU 画素 {i}");
                Assert.That(gpu[i].b, Is.EqualTo(src[i].b).Within(HalfTolerance), $"GPU 画素 {i}");
            }
        }

        [Test]
        public void 目標色が未設定の編集は素通しする()
        {
            var src = RandomLinearPixels(6);
            var srcTex = MakeSource(src);

            var mask = MaskTextures.Create(Size, Size, "ColorShiftParityTests_Mask");
            _renderTextures.Add(mask);
            var full = new byte[Size * Size];
            for (int i = 0; i < full.Length; i++) full[i] = 255;
            FloodFill.WriteR8(full, mask);

            var edit = RecolorEdit.CreateNew();
            edit.targetColor = new Color(0.9f, 0.1f, 0.15f);
            edit.hasTarget = false;
            var job = new EditJob { edit = edit, mask = mask, stats = new Stats { lP05 = 0.25f, lP95 = 0.85f } };

            var passthrough = ApplyTo(srcTex, job);
            for (int i = 0; i < src.Length; i++)
            {
                Assert.That(passthrough[i].r, Is.EqualTo(src[i].r).Within(HalfTolerance), $"画素 {i}");
                Assert.That(passthrough[i].g, Is.EqualTo(src[i].g).Within(HalfTolerance), $"画素 {i}");
                Assert.That(passthrough[i].b, Is.EqualTo(src[i].b).Within(HalfTolerance), $"画素 {i}");
                Assert.That(passthrough[i].a, Is.EqualTo(src[i].a).Within(HalfTolerance), $"画素 {i}");
            }

            // 目標色を入れると変わる（素通しが「何もしない経路」だったことの確認）
            edit.hasTarget = true;
            var shifted = ApplyTo(srcTex, job);
            bool anyChanged = false;
            for (int i = 0; i < src.Length; i++)
            {
                if (Mathf.Abs(shifted[i].r - src[i].r) > 0.01f || Mathf.Abs(shifted[i].g - src[i].g) > 0.01f
                    || Mathf.Abs(shifted[i].b - src[i].b) > 0.01f)
                {
                    anyChanged = true;
                }
            }
            Assert.That(anyChanged, Is.True);
        }

        [Test]
        public void グラデーションは箱の下端から上端へ目標1から目標2に近づき_GPU_と_CPU_が一致する()
        {
            // 灰色一面・マスク全面。位置マップは行 y ごとに箱のローカル y = y/(Size-1) − 0.5（下端 −0.5 → 上端 +0.5）、
            // 左端の列だけ A = 0（描かれていない画素 → t = 0）
            var src = new Color[Size * Size];
            float grey = 0.2f;
            for (int i = 0; i < src.Length; i++) src[i] = new Color(grey, grey, grey, 1f);
            var mask = new byte[Size * Size];
            for (int i = 0; i < mask.Length; i++) mask[i] = 255;
            var positions = new Vector4[Size * Size];
            var posPixels = new Color[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var position = new Vector4(0.3f, y / (float)(Size - 1) - 0.5f, -0.2f, x == 0 ? 0f : 1f);
                    positions[y * Size + x] = position;
                    posPixels[y * Size + x] = new Color(position.x, position.y, position.z, position.w);
                }
            }
            var posMap = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            posMap.SetPixels(posPixels);
            posMap.Apply(false);
            _cleanup.Add(posMap);

            var start = new Color(0.9f, 0.1f, 0.15f);
            var end = new Color(0.1f, 0.2f, 0.9f);
            var p = Params(start, 0.25f, 0.85f);
            p.hueRetain = 0f;
            p.useGradient = true;
            p.rootToBox = Matrix4x4.identity;
            p.boxSize = Vector3.one;
            p.targetOklab2 = OklabConverter.LinearRGBToOklab(OklabConverter.SRGBToLinear(end));

            var gpu = RunGpu(MakeSource(src), MakeMask(mask), p, posMap);

            // GPU と CPU（同じ位置を渡す）が一致する
            for (int i = 0; i < src.Length; i++)
            {
                Color cpu = ColorShiftCpu.ShiftLinear(src[i], 1f, p, positions[i]);
                Color cpuSrgb = OklabConverter.LinearToSRGB(new Vector3(cpu.r, cpu.g, cpu.b));
                Color gpuSrgb = OklabConverter.LinearToSRGB(new Vector3(gpu[i].r, gpu[i].g, gpu[i].b));
                Assert.That(gpuSrgb.r, Is.EqualTo(cpuSrgb.r).Within(SrgbTolerance), $"画素 {i} の R");
                Assert.That(gpuSrgb.g, Is.EqualTo(cpuSrgb.g).Within(SrgbTolerance), $"画素 {i} の G");
                Assert.That(gpuSrgb.b, Is.EqualTo(cpuSrgb.b).Within(SrgbTolerance), $"画素 {i} の B");
            }

            // 色相を残さない（hueRetain = 0）ので、結果の色相は混ぜた目標の色相になる: 下端は目標 1、上端は目標 2
            float startHue = OklabConverter.SRGBToOklch(start).z;
            float endHue = OklabConverter.SRGBToOklch(end).z;
            float Hue(int x, int y)
            {
                var c = gpu[y * Size + x];
                return OklabConverter.OklabToOklch(OklabConverter.LinearRGBToOklab(new Vector3(c.r, c.g, c.b))).z;
            }
            Assert.That(HueDistance(Hue(4, 0), startHue), Is.LessThan(0.05f), "下端の行は目標 1");
            Assert.That(HueDistance(Hue(4, Size - 1), endHue), Is.LessThan(0.05f), "上端の行は目標 2");
            Assert.That(HueDistance(Hue(0, Size - 1), startHue), Is.LessThan(0.05f), "描かれていない画素は t = 0（目標 1）");
            // 途中の行は両端の間（目標 1 から離れていく）
            Assert.That(HueDistance(Hue(4, Size / 2), startHue), Is.GreaterThan(HueDistance(Hue(4, 1), startHue)));
        }

        [Test]
        public void 箱の中だけ_ON_ならグラデーション方向の箱外は元の色のままで横方向は切らず_GPU_と_CPU_が一致する()
        {
            // 灰色一面・マスク全面。位置マップは列 x ごとに箱のローカル y = x/(Size-1)×2 − 1（−1 → +1。|y| > 0.5 が箱の上下外）、
            // x = 0.9（箱の横外。横方向は切らないので影響しないことの確認）、z = 0
            var src = new Color[Size * Size];
            float grey = 0.2f;
            for (int i = 0; i < src.Length; i++) src[i] = new Color(grey, grey, grey, 1f);
            var mask = new byte[Size * Size];
            for (int i = 0; i < mask.Length; i++) mask[i] = 255;
            var positions = new Vector4[Size * Size];
            var posPixels = new Color[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var position = new Vector4(0.9f, x / (float)(Size - 1) * 2f - 1f, 0f, 1f);
                    positions[y * Size + x] = position;
                    posPixels[y * Size + x] = new Color(position.x, position.y, position.z, position.w);
                }
            }
            var posMap = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            posMap.SetPixels(posPixels);
            posMap.Apply(false);
            _cleanup.Add(posMap);

            var start = new Color(0.9f, 0.1f, 0.15f);
            var end = new Color(0.1f, 0.2f, 0.9f);
            var p = Params(start, 0.25f, 0.85f);
            p.hueRetain = 0f;
            p.useGradient = true;
            p.gradientInsideOnly = true;
            p.rootToBox = Matrix4x4.identity;
            p.boxSize = Vector3.one;
            p.targetOklab2 = OklabConverter.LinearRGBToOklab(OklabConverter.SRGBToLinear(end));

            var gpu = RunGpu(MakeSource(src), MakeMask(mask), p, posMap);

            // GPU と CPU（同じ位置を渡す）が一致する
            for (int i = 0; i < src.Length; i++)
            {
                Color cpu = ColorShiftCpu.ShiftLinear(src[i], 1f, p, positions[i]);
                Color cpuSrgb = OklabConverter.LinearToSRGB(new Vector3(cpu.r, cpu.g, cpu.b));
                Color gpuSrgb = OklabConverter.LinearToSRGB(new Vector3(gpu[i].r, gpu[i].g, gpu[i].b));
                Assert.That(gpuSrgb.r, Is.EqualTo(cpuSrgb.r).Within(SrgbTolerance), $"画素 {i} の R");
                Assert.That(gpuSrgb.g, Is.EqualTo(cpuSrgb.g).Within(SrgbTolerance), $"画素 {i} の G");
                Assert.That(gpuSrgb.b, Is.EqualTo(cpuSrgb.b).Within(SrgbTolerance), $"画素 {i} の B");
            }

            // 箱の上下外（左右端の列、|y| = 1）は色変えしない＝元の灰色のまま。箱の中（y ≈ ±0.14、横は箱外でも切らない）は色が付く
            float startHue = OklabConverter.SRGBToOklch(start).z;
            float Hue(int x, int y)
            {
                var c = gpu[y * Size + x];
                return OklabConverter.OklabToOklch(OklabConverter.LinearRGBToOklab(new Vector3(c.r, c.g, c.b))).z;
            }
            Assert.That(gpu[3 * Size + 0].r, Is.EqualTo(grey).Within(SrgbTolerance), "下端より下は元の色のまま");
            Assert.That(gpu[3 * Size + (Size - 1)].r, Is.EqualTo(grey).Within(SrgbTolerance), "上端より上は元の色のまま");
            Assert.That(HueDistance(Hue(Size / 2, 3), startHue), Is.GreaterThan(0.5f), "箱の中はグラデーション（目標 1 から離れる）");
        }

        [Test]
        public void 色2の強さ0なら上端は元の色のままで下端は色1になり_GPU_と_CPU_が一致する()
        {
            // 灰色一面・マスク全面。位置マップは行 y ごとに箱のローカル y = y/(Size-1) − 0.5（下端 t = 0 → 上端 t = 1）
            var src = new Color[Size * Size];
            float grey = 0.2f;
            for (int i = 0; i < src.Length; i++) src[i] = new Color(grey, grey, grey, 1f);
            var mask = new byte[Size * Size];
            for (int i = 0; i < mask.Length; i++) mask[i] = 255;
            var positions = new Vector4[Size * Size];
            var posPixels = new Color[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var position = new Vector4(0f, y / (float)(Size - 1) - 0.5f, 0f, 1f);
                    positions[y * Size + x] = position;
                    posPixels[y * Size + x] = new Color(position.x, position.y, position.z, position.w);
                }
            }
            var posMap = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            posMap.SetPixels(posPixels);
            posMap.Apply(false);
            _cleanup.Add(posMap);

            var start = new Color(0.9f, 0.1f, 0.15f);
            var end = new Color(0.1f, 0.2f, 0.9f);
            var p = Params(start, 0.25f, 0.85f);
            p.hueRetain = 0f;
            p.useGradient = true;
            p.rootToBox = Matrix4x4.identity;
            p.boxSize = Vector3.one;
            p.targetOklab2 = OklabConverter.LinearRGBToOklab(OklabConverter.SRGBToLinear(end));
            p.strength = 1f;
            p.strength2 = 0f;

            var gpu = RunGpu(MakeSource(src), MakeMask(mask), p, posMap);

            // GPU と CPU（同じ位置を渡す）が一致する
            for (int i = 0; i < src.Length; i++)
            {
                Color cpu = ColorShiftCpu.ShiftLinear(src[i], 1f, p, positions[i]);
                Color cpuSrgb = OklabConverter.LinearToSRGB(new Vector3(cpu.r, cpu.g, cpu.b));
                Color gpuSrgb = OklabConverter.LinearToSRGB(new Vector3(gpu[i].r, gpu[i].g, gpu[i].b));
                Assert.That(gpuSrgb.r, Is.EqualTo(cpuSrgb.r).Within(SrgbTolerance), $"画素 {i} の R");
                Assert.That(gpuSrgb.g, Is.EqualTo(cpuSrgb.g).Within(SrgbTolerance), $"画素 {i} の G");
                Assert.That(gpuSrgb.b, Is.EqualTo(cpuSrgb.b).Within(SrgbTolerance), $"画素 {i} の B");
            }

            // 上端の行（t = 1、強さ 0）は元の色のまま
            for (int x = 0; x < Size; x++)
            {
                int i = (Size - 1) * Size + x;
                Assert.That(ColorShiftCpu.ShiftLinear(src[i], 1f, p, positions[i]), Is.EqualTo(src[i]), $"CPU 上端 {x}");
                Assert.That(gpu[i].r, Is.EqualTo(src[i].r).Within(HalfTolerance), $"GPU 上端 {x} の R");
                Assert.That(gpu[i].g, Is.EqualTo(src[i].g).Within(HalfTolerance), $"GPU 上端 {x} の G");
                Assert.That(gpu[i].b, Is.EqualTo(src[i].b).Within(HalfTolerance), $"GPU 上端 {x} の B");
            }

            // 下端の行（t = 0、強さ 1）は色 1 の色相に変わる
            float startHue = OklabConverter.SRGBToOklch(start).z;
            var bottom = gpu[4];
            float bottomHue = OklabConverter.OklabToOklch(OklabConverter.LinearRGBToOklab(new Vector3(bottom.r, bottom.g, bottom.b))).z;
            Assert.That(HueDistance(bottomHue, startHue), Is.LessThan(0.05f), "下端の行は色 1");
        }

        /// <summary>色相（ラジアン）の差の大きさ（0..π）</summary>
        private static float HueDistance(float a, float b) => Mathf.Abs(OklabConverter.WrapHueRadians(a - b));

        /// <summary>作業 RT に src を写して ApplyJobs を通し、結果を読み戻す</summary>
        private Color[] ApplyTo(Texture2D src, EditJob job)
        {
            var work = RecolorPipeline.CreateWorkTexture(Size, Size, "ColorShiftParityTests_Work");
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(src, work);
            }
            finally
            {
                RenderTexture.active = previous;
            }
            // ApplyJobs は work の所有権を受け取り、結果（work かもう 1 枚）を返す
            var result = RecolorPipeline.ApplyJobs(work, new[] { job });
            Assert.That(result, Is.Not.Null);
            _renderTextures.Add(result);
            return ReadLinear(result);
        }
    }
}
