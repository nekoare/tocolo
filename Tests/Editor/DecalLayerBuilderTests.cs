using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 「画像を入れる」のデカール層（DecalLayerBuilder）の GPU テスト:
    /// 箱の +Z 側から見て画像が正しい向きで貼られ、箱の外・裏向きの面・比率で余った所は透明になること
    /// </summary>
    public class DecalLayerBuilderTests
    {
        private const int Size = 64;
        private readonly List<Object> _cleanup = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders
                || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                Assert.Ignore("この環境はcompute shader（ARGBHalfへの書き込み）に対応していません");
            }
            Assert.That(DecalLayerBuilder.IsAvailable, Is.True, "DecalLayer.shaderかDecalDilate.computeが読み込めません（.metaのGUIDを確認）");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private T Track<T>(T obj) where T : Object
        {
            _cleanup.Add(obj);
            return obj;
        }

        /// <summary>
        /// 1 辺 1 の四角形（XY 平面、中心が原点）。uv (0,0) が (−0.5, −0.5)、(1,1) が (0.5, 0.5)。
        /// faceForward なら三角形の向き（幾何法線）が +Z、そうでなければ −Z（Unity の既定の Quad と同じ）。
        /// uvInset > 0 なら UV だけを内側に縮める（uvInset..1−uvInset。頂点の位置はそのまま）
        /// </summary>
        private Mesh MakeQuad(bool faceForward, float uvInset = 0f)
        {
            var mesh = Track(new Mesh());
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            };
            float lo = uvInset, hi = 1f - uvInset;
            mesh.uv = new[] { new Vector2(lo, lo), new Vector2(hi, lo), new Vector2(lo, hi), new Vector2(hi, hi) };
            mesh.triangles = faceForward ? new[] { 0, 1, 2, 2, 1, 3 } : new[] { 0, 2, 1, 2, 3, 1 };
            return mesh;
        }

        /// <summary>ルート（ClickRecolor 付き）の直下に、texture をメインに持つ四角形の MeshRenderer（変換なし）を置く</summary>
        private (ClickRecolor component, MeshRenderer renderer) MakeScene(
            Texture2D texture, bool faceForward, Vector3? localScale = null, float uvInset = 0f)
        {
            var root = Track(new GameObject("Root"));
            var component = root.AddComponent<ClickRecolor>();
            var child = new GameObject("Quad");
            child.transform.SetParent(root.transform, false);
            if (localScale.HasValue) child.transform.localScale = localScale.Value;
            child.AddComponent<MeshFilter>().sharedMesh = MakeQuad(faceForward, uvInset);
            var renderer = child.AddComponent<MeshRenderer>();
            var material = Track(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            renderer.sharedMaterial = material;
            return (component, renderer);
        }

        /// <summary>
        /// w×h の画像を pixel で埋める。シェーダーがサンプラーを Clamp に固定しているので、画像側のフィルタ・繰り返しの設定は効かない
        /// （2×2 でも列の中心より外側は端の色のまま読める）
        /// </summary>
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

        /// <summary>左列が赤・右列が青の 2×2</summary>
        private Texture2D MakeRedLeftBlueRight() =>
            MakeImage(2, 2, (x, y) => x == 0 ? Color.red : Color.blue);

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

        /// <summary>四角形 1 枚の場面を作って層を焼き、読み戻す</summary>
        private Color[] BuildLayer(
            Texture2D image, bool faceForward, Quaternion rotation, Vector3 size, bool keepAspect,
            Vector3? localScale = null, float uvInset = 0f, bool supersample = true)
        {
            var texture = Track(new Texture2D(4, 4));
            var (component, renderer) = MakeScene(texture, faceForward, localScale, uvInset);
            var slots = PositionMap.CollectSlots(new Renderer[] { renderer }, texture);
            var layer = DecalLayerBuilder.Build(component.transform, slots, Size, Size, image, Vector3.zero, rotation, size, keepAspect, supersample);
            try
            {
                Assert.That(layer, Is.Not.Null);
                return ReadLinear(layer);
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(layer);
            }
        }

        private static void AssertRed(Color c, string message)
        {
            Assert.That(c.r, Is.GreaterThan(0.9f), message + " (r)");
            Assert.That(c.b, Is.LessThan(0.1f), message + " (b)");
            Assert.That(c.a, Is.GreaterThan(0.99f), message + " (a)");
        }

        private static void AssertBlue(Color c, string message)
        {
            Assert.That(c.b, Is.GreaterThan(0.9f), message + " (b)");
            Assert.That(c.r, Is.LessThan(0.1f), message + " (r)");
            Assert.That(c.a, Is.GreaterThan(0.99f), message + " (a)");
        }

        [Test]
        public void 正面から見た向きで画像が貼られ_左右が反転していない()
        {
            var pixels = BuildLayer(MakeRedLeftBlueRight(), true, Quaternion.identity, new Vector3(1f, 1f, 2f), false);
            // +Z から見ると −X が画面の右なので、四角形の x = −0.37 には画像の右（青）が来る
            AssertBlue(At(pixels, 8, 32), "四角形の x=−0.37 は画像の右列");
            AssertRed(At(pixels, 56, 32), "四角形の x=+0.37 は画像の左列");
        }

        [Test]
        public void 箱の外は透明()
        {
            var pixels = BuildLayer(MakeRedLeftBlueRight(), true, Quaternion.identity, new Vector3(0.5f, 0.5f, 2f), false);
            Assert.That(At(pixels, 4, 32).a, Is.LessThan(0.01f), "x=−0.44 は箱の外");
            Assert.That(At(pixels, 32, 32).a, Is.GreaterThan(0.99f), "中央は箱の中");
        }

        [Test]
        public void 箱の縁は太らない()
        {
            // 箱の x の大きさ 0.5 → 箱の縁は層の x=16 / 48。箱の外は「描いた印」なので、縁埋めで画像が外へ広がらない
            var image = MakeImage(2, 2, (x, y) => Color.red);
            var pixels = BuildLayer(image, true, Quaternion.identity, new Vector3(0.5f, 1f, 2f), false);
            AssertRed(At(pixels, 32, 32), "箱の中は画像の赤");
            Assert.That(At(pixels, 13, 32).a, Is.LessThan(0.01f), "箱の縁の 3 画素外（左）は透明");
            Assert.That(At(pixels, 51, 32).a, Is.LessThan(0.01f), "箱の縁の 3 画素外（右）は透明");
        }

        [Test]
        public void 裏向きの面には描かない()
        {
            var pixels = BuildLayer(MakeRedLeftBlueRight(), false, Quaternion.identity, new Vector3(1f, 1f, 2f), false);
            Assert.That(At(pixels, 32, 32).a, Is.LessThan(0.01f), "−Z 向きの面は箱の +Z から見て裏");
        }

        [Test]
        public void 箱をYで180度回すと裏向きの面に描ける()
        {
            var pixels = BuildLayer(MakeRedLeftBlueRight(), false, Quaternion.Euler(0f, 180f, 0f), new Vector3(1f, 1f, 2f), false);
            Assert.That(At(pixels, 32, 32).a, Is.GreaterThan(0.99f), "箱を回すと −Z 向きの面が表になる");
            // −Z 側から見ると +X が画面の右なので、正面の場合と左右が逆になる
            AssertRed(At(pixels, 8, 32), "四角形の x=−0.37 は画像の左列");
            AssertBlue(At(pixels, 56, 32), "四角形の x=+0.37 は画像の右列");
        }

        [Test]
        public void 比率を保つと長辺に合わせて内接する()
        {
            var image = MakeImage(4, 2, (x, y) => Color.red);

            var fitted = BuildLayer(image, true, Quaternion.identity, new Vector3(1f, 1f, 2f), true);
            Assert.That(At(fitted, 32, 32).a, Is.GreaterThan(0.99f), "中央は画像の中");
            Assert.That(At(fitted, 32, 8).a, Is.LessThan(0.01f), "v=0.13 は画像の縦の範囲（0.25..0.75）の外");

            var stretched = BuildLayer(image, true, Quaternion.identity, new Vector3(1f, 1f, 2f), false);
            Assert.That(At(stretched, 32, 8).a, Is.GreaterThan(0.99f), "比率を保たなければ箱いっぱいに引き伸ばす");
        }

        [Test]
        public void 箱の大きさが負なら反転する()
        {
            var pixels = BuildLayer(MakeRedLeftBlueRight(), true, Quaternion.identity, new Vector3(-1f, 1f, 2f), false);
            AssertRed(At(pixels, 8, 32), "x の大きさが負なら左右が反転する");
            AssertBlue(At(pixels, 56, 32), "x の大きさが負なら左右が反転する");
        }

        [Test]
        public void 半透明の画像はストレートαで返る()
        {
            var image = MakeImage(2, 2, (x, y) => new Color(1f, 0f, 0f, 0.5f));
            var pixels = BuildLayer(image, true, Quaternion.identity, new Vector3(1f, 1f, 2f), false);
            var c = At(pixels, 32, 32);
            Assert.That(c.r, Is.GreaterThan(0.9f), "乗算済みのままなら r ≈ 0.5 になる");
            Assert.That(c.a, Is.EqualTo(0.5f).Within(0.03f));
        }

        [Test]
        public void 画像の透明な縁は太らない()
        {
            // 画像の 1 画素 = 層の 1 画素（64×64）にして、bilinear の滲みが境界の 1 画素に収まるようにする。
            // 中央（16..47）だけ赤・外周は透明。u は反転するが中央の範囲は反転しても同じ（層の x ∈ [16,48) が赤）
            var image = MakeImage(Size, Size, (x, y) =>
                x >= 16 && x < 48 && y >= 16 && y < 48 ? Color.red : new Color(0f, 0f, 0f, 0f));
            var pixels = BuildLayer(image, true, Quaternion.identity, new Vector3(1f, 1f, 2f), false);
            AssertRed(At(pixels, 32, 32), "中央は画像の赤");
            // 画像の透明な部分は「描かれた」画素なので、縁埋めで隣の赤が広がってはいけない
            Assert.That(At(pixels, 14, 32).a, Is.LessThan(0.01f), "赤の 2 画素外（左）は透明のまま");
            Assert.That(At(pixels, 49, 32).a, Is.LessThan(0.01f), "赤の 2 画素外（右）は透明のまま");
        }

        [Test]
        public void 塗り残しは縁埋めで埋まる()
        {
            // UV を 0.02..0.98 に縮めると、層の x=0（u=0.008）は三角形の外で描かれない。縁埋め（2 回）で隣の赤が入る。
            // 2 倍サンプリングでは 2 倍側の x=0..2（u=0.004/0.012/0.020 < 0.02）が描かれない。縮めると等倍の x=0 は 2 倍側 x=0,1 が
            // どちらも未描画なので α=0 のまま → 等倍の縁埋めで x=1 の赤が入る
            var image = MakeImage(2, 2, (x, y) => Color.red);
            var pixels = BuildLayer(image, true, Quaternion.identity, new Vector3(1f, 1f, 2f), false, uvInset: 0.02f);
            AssertRed(At(pixels, 0, 32), "三角形の外 1 画素は縁埋めで赤");
        }

        [Test]
        public void 斜めの縁は2倍サンプリングで中間のαになる()
        {
            // 画像の 1 画素 = 層の 1 画素（64×64）。画像は x + y < 64 が赤・それ以外は透明。
            // u は反転する（画像の x = 63 − 層の x）ので、層では y ≤ x が赤になり、対角線 y = x のすぐ上（31,32）が透明・（32,32）が赤の境目
            var image = MakeImage(Size, Size, (x, y) => x + y < Size ? Color.red : new Color(0f, 0f, 0f, 0f));

            var smooth = BuildLayer(image, true, Quaternion.identity, new Vector3(1f, 1f, 2f), false);
            // 2 倍側の 4 画素が対角線をまたぐので、平均の α は中間値（手計算で（31,32）≈ 0.2、（32,32）≈ 0.8）
            Assert.That(At(smooth, 31, 32).a, Is.InRange(0.1f, 0.9f), "対角線の外側の画素は中間の α");
            Assert.That(At(smooth, 32, 32).a, Is.InRange(0.1f, 0.9f), "対角線の内側の画素は中間の α");

            // 等倍では 1 画素につき画像を 1 回（画素の中心）しか読まないので、0 か 1 の階段になる
            var stepped = BuildLayer(image, true, Quaternion.identity, new Vector3(1f, 1f, 2f), false, supersample: false);
            Assert.That(At(stepped, 31, 32).a, Is.LessThan(0.05f), "等倍では対角線の外側は透明");
            Assert.That(At(stepped, 32, 32).a, Is.GreaterThan(0.95f), "等倍では対角線の内側は不透明");
        }

        [Test]
        public void チャートの縁は2倍サンプリングで薄まらない()
        {
            // UV 0.02..0.98: 等倍 x=1 は 2 倍側 x=2（未描画）と x=3（赤）を含むチャートの縁の画素。未描画を平均に入れないので不透明のまま
            var image = MakeImage(2, 2, (x, y) => Color.red);
            var pixels = BuildLayer(image, true, Quaternion.identity, new Vector3(1f, 1f, 2f), false, uvInset: 0.02f);
            AssertRed(At(pixels, 1, 32), "チャートの縁を含む画素は不透明な赤");
        }

        [Test]
        public void 鏡像の面でも見えている側に描く()
        {
            // X を −1 倍した四角形は、三角形の並びが逆になるが Unity はカリングを反転して描くので +Z から見える
            var pixels = BuildLayer(MakeRedLeftBlueRight(), true, Quaternion.identity, new Vector3(1f, 1f, 2f), false, new Vector3(-1f, 1f, 1f));
            Assert.That(At(pixels, 32, 32).a, Is.GreaterThan(0.99f));
        }

        [Test]
        public void 上下の向きが正しく_大きさのYが負なら上下反転する()
        {
            var image = MakeImage(2, 2, (x, y) => y == 1 ? Color.red : Color.blue);

            // v = box.y / size.y + 0.5: 四角形の上（y=+0.38）は v=0.88 で画像の上段（赤）、下（y=−0.37）は v=0.13 で下段（青）
            var upright = BuildLayer(image, true, Quaternion.identity, new Vector3(1f, 1f, 2f), false);
            AssertRed(At(upright, 32, 56), "四角形の上は画像の上段");
            AssertBlue(At(upright, 32, 8), "四角形の下は画像の下段");

            // size.y が負なら v = 0.5 − box.y / |size.y| になり上下が入れ替わる
            var flipped = BuildLayer(image, true, Quaternion.identity, new Vector3(1f, -1f, 2f), false);
            AssertBlue(At(flipped, 32, 56), "y の大きさが負なら上下が反転する");
            AssertRed(At(flipped, 32, 8), "y の大きさが負なら上下が反転する");
        }
    }
}
