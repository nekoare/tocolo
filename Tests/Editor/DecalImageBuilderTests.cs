using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Decal;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.NDMF;
using Nekoare.ClickRecolor.Editor.Pipeline;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 「画像を入れる」第 2 段（重ね貼り）の画像（DecalImageBuilder）の GPU テスト:
    /// 画像の空間に「画像 × 選択マスク」ができ、色の設定は画像にだけ掛かり、不透明度は α に入ること。
    /// あわせて、重ね貼りの編集はパイプラインでテクスチャを変えないこと
    /// </summary>
    public class DecalImageBuilderTests
    {
        private const int Size = 64;
        /// <summary>sRGB 0.5 の線形値</summary>
        private const float Gray = 0.214f;
        private static readonly Vector3 BoxSize = new Vector3(1f, 1f, 2f);
        private readonly List<Object> _cleanup = new List<Object>();
        private readonly List<RenderTexture> _masks = new List<RenderTexture>();

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                Assert.Ignore("この環境はcompute shader（ARGBHalfへの書き込み）に対応していません");
            }
            Assert.That(DecalImageBuilder.IsAvailable, Is.True, "DecalImage.shader・DecalDilate.compute・ColorShift.computeのどれかが読み込めません（.metaのGUIDを確認）");
        }

        [TearDown]
        public void TearDown()
        {
            MaskCache.ClearCache();
            DecalLayerCache.ClearCache();
            PositionMap.ClearCache();
            ToolSession.Clear();
            foreach (var mask in _masks) MaskTextures.Destroy(mask);
            _masks.Clear();
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private T Track<T>(T obj) where T : Object
        {
            _cleanup.Add(obj);
            return obj;
        }

        /// <summary>1 辺 1 の四角形（XY 平面、中心が原点、三角形の向きは +Z）。uv (0,0) が (−0.5, −0.5)、(1,1) が (0.5, 0.5)（DecalOverlayMeshTests と同じ作り）</summary>
        private Mesh MakeQuad()
        {
            var mesh = Track(new Mesh());
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
            return mesh;
        }

        /// <summary>
        /// 四角形から重ね貼りメッシュを作り、その追加サブメッシュを 1 区画にする（箱は原点・回転なし。既定は四角形がちょうど入る大きさ、
        /// boxSize で四角形より大きくすると画像の外側が覆われない）。比率は保たない（fit = (1,1)）
        /// </summary>
        private OverlayPart[] MakeParts(Vector3? boxSize = null)
        {
            var source = MakeQuad();
            var overlay = DecalOverlayMesh.Build(source, new[] { 0 }, Matrix4x4.identity, false, boxSize ?? BoxSize, Vector2.one, 0f);
            Assert.That(overlay, Is.Not.Null, "前提: 重ね貼りメッシュが作れる");
            Track(overlay);
            return new[] { new OverlayPart(overlay, overlay.subMeshCount - 1, Matrix4x4.identity, Vector2.one, Vector2.zero) };
        }

        private Texture2D MakeImage(int w, int h, Func<int, int, Color> pixel)
        {
            var image = Track(new Texture2D(w, h, TextureFormat.RGBA32, false));
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    image.SetPixel(x, y, pixel(x, y));
                }
            }
            image.Apply();
            return image;
        }

        /// <summary>選択マスク（R8、Size×Size。元テクスチャの UV 空間）を value(u, v) で埋める（u, v は画素中心の 0..1）</summary>
        private RenderTexture MakeMask(Func<float, float, float> value)
        {
            var mask = MaskTextures.Create(Size, Size, "Test_Mask");
            _masks.Add(mask);
            var tex = Track(new Texture2D(Size, Size, TextureFormat.RGBA32, false, true));
            var pixels = new Color[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float v = value((x + 0.5f) / Size, (y + 0.5f) / Size);
                    pixels[y * Size + x] = new Color(v, v, v, 1f);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, mask);
            }
            finally
            {
                RenderTexture.active = previous;
            }
            return mask;
        }

        private Transform MakeRoot() => Track(new GameObject("Root")).transform;

        private static RecolorEdit MakeEdit()
        {
            var edit = RecolorEdit.CreateNew();
            edit.hasTarget = false;
            edit.strength = 1f;
            return edit;
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

        private static Color At(Color[] pixels, int x, int y) => pixels[y * Size + x];

        /// <summary>画像を作って読み戻す</summary>
        private Color[] BuildAndRead(Texture2D image, RenderTexture mask, RecolorEdit edit, Vector3? boxSize = null)
        {
            var rt = DecalImageBuilder.Build(MakeRoot(), MakeParts(boxSize), image, mask, edit);
            try
            {
                Assert.That(rt, Is.Not.Null);
                Assert.That(rt.width, Is.EqualTo(Size), "前提: 画像の大きさで作る");
                Assert.That(rt.height, Is.EqualTo(Size));
                return ReadLinear(rt);
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(rt);
            }
        }

        [Test]
        public void 同じUVの四角形2枚のうち箱の外の1枚は重ね貼りにならず_画像は1枚分で塗られる()
        {
            // 左右で UV を共有する服の片側だけに貼る場面: 同じ UV の四角形が箱の中（原点）と箱の外（x = +5）にある
            var inside = MakeQuad();
            var outside = MakeQuad();
            var insideOverlay = DecalOverlayMesh.Build(inside, new[] { 0 }, Matrix4x4.identity, false, BoxSize, Vector2.one, 0f);
            var outsideOverlay = DecalOverlayMesh.Build(outside, new[] { 0 }, Matrix4x4.Translate(new Vector3(5f, 0f, 0f)), false, BoxSize, Vector2.one, 0f);
            if (insideOverlay != null) Track(insideOverlay);
            if (outsideOverlay != null) Track(outsideOverlay);

            Assert.That(insideOverlay, Is.Not.Null, "箱の中の四角形は重ね貼りメッシュを返す");
            Assert.That(outsideOverlay, Is.Null, "箱の外の四角形は重ね貼りメッシュを返さない");

            var parts = new[] { new OverlayPart(insideOverlay, insideOverlay.subMeshCount - 1, Matrix4x4.identity, Vector2.one, Vector2.zero) };
            var rt = DecalImageBuilder.Build(MakeRoot(), parts, MakeImage(Size, Size, (x, y) => Color.red), MakeMask((u, v) => 1f), MakeEdit());
            try
            {
                Assert.That(rt, Is.Not.Null);
                var pixels = ReadLinear(rt);
                Assert.That(At(pixels, Size / 2, Size / 2).a, Is.EqualTo(1f).Within(0.03f), "中心は 1 枚分の不透明（2 重に塗られない）");
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(rt);
            }
        }

        [Test]
        public void 選択マスクの外はαが0()
        {
            // 元の UV の左半分（u < 0.5）だけ選ぶ。投影 UV は元の UV と左右逆（正面から見て正しい向き）なので、画像では右半分になる
            var mask = MakeMask((u, v) => u < 0.5f ? 1f : 0f);
            var pixels = BuildAndRead(MakeImage(Size, Size, (x, y) => Color.red), mask, MakeEdit());

            // 境界（x = 32）から離した点で見る
            Assert.That(At(pixels, 48, 32).a, Is.GreaterThan(0.95f), "画像の右半分（選択の内側）は不透明");
            Assert.That(At(pixels, 48, 32).r, Is.GreaterThan(0.9f), "画像の色のまま");
            Assert.That(At(pixels, 16, 32).a, Is.LessThan(0.05f), "画像の左半分（選択の外）は透明");
        }

        [Test]
        public void 色が決まっていれば画像だけ色が変わる()
        {
            var mask = MakeMask((u, v) => 1f);
            var edit = MakeEdit();
            edit.hasTarget = true;
            edit.targetColor = Color.blue;
            var pixels = BuildAndRead(MakeImage(Size, Size, (x, y) => Color.red), mask, edit);

            var center = At(pixels, Size / 2, Size / 2);
            Assert.That(center.b, Is.GreaterThan(center.r), "中心は青へ寄る");
            Assert.That(center.a, Is.GreaterThan(0.95f), "色変えで α は変わらない");
        }

        [Test]
        public void 不透明度がαに掛かる()
        {
            var mask = MakeMask((u, v) => 1f);
            var edit = MakeEdit();
            edit.strength = 0.5f;
            var pixels = BuildAndRead(MakeImage(Size, Size, (x, y) => Color.red), mask, edit);

            var center = At(pixels, Size / 2, Size / 2);
            Assert.That(center.a, Is.EqualTo(0.5f).Within(0.03f), "不透明度が α に入る");
            // ストレート α なので色は赤のまま
            Assert.That(center.r, Is.GreaterThan(0.9f), "r");
            Assert.That(center.g, Is.LessThan(0.05f), "g");
            Assert.That(center.b, Is.LessThan(0.05f), "b");
        }

        [Test]
        public void 画像の外周は透明()
        {
            // 重ね貼りマテリアルは画像を Clamp で引くので、外周が不透明だと縁の色が箱の外へ伸びる（DecalOverlayMesh の約束）
            var mask = MakeMask((u, v) => 1f);
            var pixels = BuildAndRead(MakeImage(Size, Size, (x, y) => Color.red), mask, MakeEdit());

            Assert.That(At(pixels, 0, Size / 2).a, Is.LessThan(0.05f), "左端");
            Assert.That(At(pixels, Size - 1, Size / 2).a, Is.LessThan(0.05f), "右端");
            Assert.That(At(pixels, Size / 2, 0).a, Is.LessThan(0.05f), "下端");
            Assert.That(At(pixels, Size / 2, Size - 1).a, Is.LessThan(0.05f), "上端");
            Assert.That(At(pixels, 1, Size / 2).a, Is.GreaterThan(0.95f), "外周の 1 つ内側は画像のまま");
        }

        [Test]
        public void 覆われていない外周も透明()
        {
            // 箱を四角形より少し大きくすると、四角形は画像の u, v = 0.03..0.97 だけを覆い、外周の 2 テクセルは三角形の外になる。
            // 縁埋め（2 回）はそこを不透明に埋めるので、外周を明示的に消していないと Clamp で箱の外へ筋が伸びる
            var box = new Vector3(1f / 0.94f, 1f / 0.94f, 2f);
            var mask = MakeMask((u, v) => 1f);
            var pixels = BuildAndRead(MakeImage(Size, Size, (x, y) => Color.red), mask, MakeEdit(), box);

            Assert.That(At(pixels, 0, Size / 2).a, Is.LessThan(0.05f), "左端");
            Assert.That(At(pixels, Size - 1, Size / 2).a, Is.LessThan(0.05f), "右端");
            Assert.That(At(pixels, Size / 2, 0).a, Is.LessThan(0.05f), "下端");
            Assert.That(At(pixels, Size / 2, Size - 1).a, Is.LessThan(0.05f), "上端");
            Assert.That(At(pixels, Size / 2, Size / 2).a, Is.GreaterThan(0.95f), "前提: 中心は画像のまま");
        }

        [Test]
        public void 透明の境界の色は暗くならない()
        {
            // 元の UV の左半分だけ選ぶ → 画像の左半分は透明。重ね貼りマテリアルは bilinear・ストレート α で引くので、
            // 透明な画素の rgb が黒だと境界に暗い縁が出る。透明な画素にも近傍の不透明な色（赤）が入っていること
            var mask = MakeMask((u, v) => u < 0.5f ? 1f : 0f);
            var pixels = BuildAndRead(MakeImage(Size, Size, (x, y) => Color.red), mask, MakeEdit());

            var edge = At(pixels, 29, Size / 2);
            Assert.That(edge.a, Is.LessThan(0.05f), "前提: 境界のすぐ外は透明");
            Assert.That(edge.r, Is.GreaterThan(0.5f), "透明な画素の色は赤に近い (r)");
            Assert.That(edge.g, Is.LessThan(0.1f), "g");
            Assert.That(edge.b, Is.LessThan(0.1f), "b");
        }

        [Test]
        public void 上下が反転していない()
        {
            // 元の UV の下半分（v < 0.5）だけ選ぶ。投影 UV の v は元の UV と同じ向き（左右だけ反転）なので、画像でも下半分になる
            var mask = MakeMask((u, v) => v < 0.5f ? 1f : 0f);
            var pixels = BuildAndRead(MakeImage(Size, Size, (x, y) => Color.red), mask, MakeEdit());

            Assert.That(At(pixels, Size / 2, 16).a, Is.GreaterThan(0.95f), "画像の下半分（選択の内側）は不透明");
            Assert.That(At(pixels, Size / 2, 48).a, Is.LessThan(0.05f), "画像の上半分（選択の外）は透明");
        }

        [Test]
        public void 返す画像のミップと補間と異方性は貼る画像に合わせる()
        {
            // lilToon のデカールは画像そのものを引く。ミップ付き・異方性 4 の画像ならこちらも同じにして、斜めから見たときの見え方を揃える
            var image = Track(new Texture2D(64, 64, TextureFormat.RGBA32, true));
            var fill = new Color32[64 * 64];
            for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(255, 0, 0, 255);
            image.SetPixels32(fill);
            image.Apply(true);
            image.anisoLevel = 4;
            image.filterMode = FilterMode.Trilinear;
            var rt = DecalImageBuilder.Build(MakeRoot(), MakeParts(), image, MakeMask((u, v) => 1f), MakeEdit());
            try
            {
                Assert.That(rt.useMipMap, Is.True);
                Assert.That(rt.anisoLevel, Is.EqualTo(4));
                Assert.That(rt.filterMode, Is.EqualTo(FilterMode.Trilinear));
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(rt);
            }

            // ミップ無しの画像ならミップ無し
            var flat = MakeImage(64, 64, (x, y) => Color.red);
            var noMip = DecalImageBuilder.Build(MakeRoot(), MakeParts(), flat, MakeMask((u, v) => 1f), MakeEdit());
            try
            {
                Assert.That(noMip.useMipMap, Is.False);
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(noMip);
            }
        }

        [Test]
        public void ミップ付きの画像は縮小版でも外周が透明()
        {
            // ミップを作ると透明な外周と内側の不透明な画素が平均され、Clamp で引かれる箱の外へ縁の色が伸びる（実機 2026-10-04）
            var image = Track(new Texture2D(64, 64, TextureFormat.RGBA32, true));
            var fill = new Color32[64 * 64];
            for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(255, 0, 0, 255);
            image.SetPixels32(fill);
            image.Apply(true);
            var rt = DecalImageBuilder.Build(MakeRoot(), MakeParts(), image, MakeMask((u, v) => 1f), MakeEdit());
            var mip = RecolorPipeline.CreateWorkTexture(rt.width / 2, rt.height / 2, "ClickRecolor_TestMip");
            try
            {
                Assert.That(rt.useMipMap, Is.True);
                Graphics.CopyTexture(rt, 0, 1, mip, 0, 0);
                var pixels = ReadLinear(mip);
                int w = mip.width, h = mip.height;
                Color Pixel(int x, int y) => pixels[y * w + x];
                Assert.That(Pixel(0, h / 2).a, Is.EqualTo(0f).Within(0.01f), "左端");
                Assert.That(Pixel(w - 1, h / 2).a, Is.EqualTo(0f).Within(0.01f), "右端");
                Assert.That(Pixel(w / 2, 0).a, Is.EqualTo(0f).Within(0.01f), "下端");
                Assert.That(Pixel(w / 2, h - 1).a, Is.EqualTo(0f).Within(0.01f), "上端");
                Assert.That(Pixel(w / 2, h / 2).a, Is.GreaterThan(0.95f), "内側は画像のまま");
                Assert.That(Pixel(0, h / 2).r, Is.GreaterThan(0.3f), "外周の色は残す（透明との境界が暗くならないように）");
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(mip);
                RecolorPipeline.DestroyWorkTexture(rt);
            }
        }

        [Test]
        public void 大きな画像でも統計が取れて色変えが掛かる()
        {
            // 2048 の画像は統計の読み戻し（長辺 1024）で縮小される。作業 RT がミップなしなので、縮小は描いた画素だけから正しく取れること
            const int big = 2048;
            var image = MakeImage(big, big, (x, y) => Color.red);
            var mask = MakeMask((u, v) => 1f);
            var rt = DecalImageBuilder.Build(MakeRoot(), MakeParts(), image, mask, MakeEdit());
            try
            {
                Assert.That(rt, Is.Not.Null);
                Assert.That(rt.width, Is.EqualTo(big), "前提: 画像の大きさで作る");
                Assert.That(rt.useMipMap, Is.EqualTo(image.mipmapCount > 1), "ミップの有無は貼る画像に合わせる");
                var white = MaskTextures.GetTemporary(rt.width, rt.height);
                try
                {
                    var previous = RenderTexture.active;
                    try
                    {
                        Graphics.SetRenderTarget(white);
                        GL.Clear(false, true, Color.white);
                    }
                    finally
                    {
                        RenderTexture.active = previous;
                    }
                    var stats = SelectionStats.Compute(rt, white);
                    Assert.That(stats.count, Is.GreaterThan(SelectionStats.MinCount), "画素が数えられる");
                    Assert.That(stats.rep.r, Is.GreaterThan(0.9f), "代表色は画像の色（赤）に近い (r)");
                    Assert.That(stats.rep.g, Is.LessThan(0.1f), "g");
                    Assert.That(stats.rep.b, Is.LessThan(0.1f), "b");
                }
                finally
                {
                    RenderTexture.ReleaseTemporary(white);
                }
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(rt);
            }

            // 色が決まっているとき（Build 内で作業 RT から統計を取る経路）も、2048 で青へ寄る
            var edit = MakeEdit();
            edit.hasTarget = true;
            edit.targetColor = Color.blue;
            var shifted = DecalImageBuilder.Build(MakeRoot(), MakeParts(), image, mask, edit);
            try
            {
                Assert.That(shifted, Is.Not.Null);
                var tex = new Texture2D(1, 1, TextureFormat.RGBAHalf, false, true);
                var previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = shifted;
                    tex.ReadPixels(new Rect(big / 2, big / 2, 1, 1), 0, 0, false);
                    tex.Apply(false);
                    var center = tex.GetPixel(0, 0);
                    Assert.That(center.b, Is.GreaterThan(center.r), "中心は青へ寄る");
                }
                finally
                {
                    RenderTexture.active = previous;
                    Object.DestroyImmediate(tex);
                }
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(shifted);
            }
        }

        [Test]
        public void ノーマルも反映_元の法線マップを画像の座標へ写し直し_画像の外は平らな法線()
        {
            // 箱を四角形の 2 倍にすると、四角形は画像の中央（u, v ∈ [0.25, 0.75]）だけに写る
            var parts = MakeParts(new Vector3(2f, 2f, 2f));
            // 元の法線マップ: 接空間の法線 (0.5, 0, 0.866) を 0..1 に詰めた値で一様に埋める（A = 1 なので「x に w を掛ける」形式でも同じ）
            var source = Track(new Texture2D(4, 4, TextureFormat.RGBA32, false, true));
            var packed = new Color(0.75f, 0.5f, 0.933f, 1f);
            var fill = new Color[16];
            for (int i = 0; i < fill.Length; i++) fill[i] = packed;
            source.SetPixels(fill);
            source.Apply();

            var rt = DecalImageBuilder.BuildNormal(parts, new[] { ((Texture)source, new Vector4(1f, 1f, 0f, 0f)) }, Size, Size);
            Assert.That(rt, Is.Not.Null);
            try
            {
                // 大きさは元の法線マップの密度（4×4 が画像の 1/4 の面積に写る → 64 画素 → 8×8）の縦横 2 倍＝16
                Assert.That(rt.width, Is.EqualTo(8 * DecalImageBuilder.NormalOversample));
                Assert.That(rt.useMipMap, Is.False, "ミップの有無は元の法線マップ（ミップ無し）に合わせる");
                var pixels = ReadLinear(rt);
                Color Pixel(int x, int y) => pixels[y * rt.width + x];
                var center = Pixel(rt.width / 2, rt.height / 2);
                Assert.That(center.r, Is.EqualTo(0.75f).Within(0.03f), "四角形の写った所は元の法線");
                Assert.That(center.g, Is.EqualTo(0.5f).Within(0.03f));
                var corner = Pixel(0, 0);
                Assert.That(corner.r, Is.EqualTo(0.5f).Within(0.02f), "画像の外は平らな法線");
                Assert.That(corner.g, Is.EqualTo(0.5f).Within(0.02f));
                Assert.That(corner.b, Is.EqualTo(1f).Within(0.02f));
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(rt);
            }
        }

        [Test]
        public void ノーマルも反映_2ndノーマルの強さマスクを画像の座標へ写し直し_描かれない所は白()
        {
            var parts = MakeParts(new Vector3(2f, 2f, 2f));
            var source = Track(new Texture2D(4, 4, TextureFormat.RGBA32, false, true));
            var fill = new Color[16];
            for (int i = 0; i < fill.Length; i++) fill[i] = new Color(0.25f, 0f, 0f, 1f);
            source.SetPixels(fill);
            source.Apply();

            var rt = DecalImageBuilder.BuildBump2ndMask(parts, new[] { ((Texture)source, new Vector4(1f, 1f, 0f, 0f)) }, Size, Size);
            Assert.That(rt, Is.Not.Null);
            try
            {
                var pixels = ReadLinear(rt);
                Color Pixel(int x, int y) => pixels[y * rt.width + x];
                Assert.That(Pixel(rt.width / 2, rt.height / 2).r, Is.EqualTo(0.25f).Within(0.03f), "四角形の写った所は元のマスクの R");
                Assert.That(Pixel(0, 0).r, Is.EqualTo(1f).Within(0.02f), "描かれない所は白（元の強さのまま）");
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(rt);
            }
            Assert.That(DecalImageBuilder.BuildBump2ndMask(parts, new[] { ((Texture)null, new Vector4(1f, 1f, 0f, 0f)) }, Size, Size), Is.Null,
                "マスクが無ければ作らない");
        }

        [Test]
        public void ノーマルも反映_法線マップが無ければ作らない()
        {
            var parts = MakeParts();
            Assert.That(DecalImageBuilder.BuildNormal(parts, new[] { ((Texture)null, new Vector4(1f, 1f, 0f, 0f)) }, Size, Size), Is.Null);
        }

        [Test]
        public void EstimateNormalSize_元の法線マップの密度に合わせる()
        {
            // 投影 UV（画像の座標）で面積 1、元の UV で面積 1/16 の四角形。元の法線マップ 256² → 写る画素は 4096 → 64×64 の縦横 2 倍 → 128×128
            var mesh = Track(new Mesh());
            mesh.vertices = new Vector3[4];
            mesh.SetUVs(0, new List<Vector2> { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) });
            mesh.SetUVs(1, new List<Vector2> { new Vector2(0f, 0f), new Vector2(0.25f, 0f), new Vector2(0f, 0.25f), new Vector2(0.25f, 0.25f) });
            mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            var parts = new[] { new OverlayPart(mesh, 0, Matrix4x4.identity, Vector2.one, Vector2.zero) };
            var normal = Track(new Texture2D(256, 256));
            var size = DecalImageBuilder.EstimateNormalSize(parts, new[] { ((Texture)normal, new Vector4(1f, 1f, 0f, 0f)) }, 1024, 1024);
            Assert.That(size, Is.EqualTo((128, 128)));

            // 法線マップの Tiling 2 なら面積は 4 倍 → 256×256。画像が横長（2:1）なら縦横比で分ける
            var tiled = DecalImageBuilder.EstimateNormalSize(parts, new[] { ((Texture)normal, new Vector4(2f, 2f, 0f, 0f)) }, 1024, 1024);
            Assert.That(tiled, Is.EqualTo((256, 256)));
            var wide = DecalImageBuilder.EstimateNormalSize(parts, new[] { ((Texture)normal, new Vector4(1f, 1f, 0f, 0f)) }, 2000, 1000);
            Assert.That(wide.Item1, Is.EqualTo(182));
            Assert.That(wide.Item2, Is.EqualTo(91));
        }

        [Test]
        public void UseOverlay_の編集は_PrepareJob_で_overlay_になり_Run_でテクスチャが変わらない()
        {
            var lilToon = Shader.Find("lilToon");
            if (lilToon == null) Assert.Ignore("lilToon が入っていない環境です");
            if (!DecalOverlayMaterial.IsApiAvailable) Assert.Ignore("lilToon のエディタ API がリフレクションで見つかりません");

            // 灰色の元テクスチャを lilToon のマテリアルで貼った四角形（ルートに ClickRecolor）
            var texture = MakeImage(Size, Size, (x, y) => new Color(0.5f, 0.5f, 0.5f, 1f));
            var root = Track(new GameObject("Root"));
            var component = root.AddComponent<ClickRecolor>();
            var child = new GameObject("Quad");
            child.transform.SetParent(root.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = MakeQuad();
            var renderer = child.AddComponent<MeshRenderer>();
            var material = Track(new Material(lilToon));
            material.SetTexture("_MainTex", texture);
            renderer.sharedMaterial = material;

            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = texture;
            edit.seedRenderer = renderer;
            edit.seedSubmesh = 0;
            edit.seedTriangle = 0;
            edit.seedUv = new Vector2(0.5f, 0.5f);
            edit.mode = SelectionMode.Island;
            edit.padding = 0;
            edit.decalEnabled = true;
            edit.decalSmooth = true;
            edit.decalTexture = MakeImage(2, 2, (x, y) => Color.red);
            edit.decalBoxSize = BoxSize;
            component.AddEdit(edit);
            Assert.That(DecalOverlayMaterial.UseOverlay(component, edit), Is.True, "前提: 重ね貼りで貼る編集");

            var renderers = new List<Renderer> { renderer };
            var users = RecolorPreview.CollectUsers(renderers, texture);
            var job = RecolorPipeline.PrepareJob(edit, texture, Size, users, context: MaskContext.For(component, renderers));
            Assert.That(job, Is.Not.Null);
            Assert.That(job.overlay, Is.True, "重ね貼りの編集");
            Assert.That(job.decal, Is.Null, "デカール層は作らない");
            Assert.That(job.mask, Is.Not.Null, "選択マスクは従来どおり持つ");

            var result = RecolorPipeline.Run(new PipelineInput
            {
                sourceAsset = texture,
                workingSize = Size,
                jobs = new[] { job },
                root = component.transform,
                renderers = renderers,
            });
            Assert.That(result, Is.Not.Null);
            try
            {
                var pixels = ReadLinear(result);
                var center = At(pixels, Size / 2, Size / 2);
                Assert.That(center.r, Is.EqualTo(Gray).Within(0.02f), "元の色のまま (r)");
                Assert.That(center.g, Is.EqualTo(Gray).Within(0.02f), "元の色のまま (g)");
                Assert.That(center.b, Is.EqualTo(Gray).Within(0.02f), "元の色のまま (b)");
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(result);
            }
        }
    }
}
