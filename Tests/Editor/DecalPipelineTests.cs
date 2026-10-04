using System;
using System.Collections.Generic;
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
    /// 「画像を入れる」をパイプライン（PrepareJob → Run）に通す GPU テスト:
    /// 画像は選択範囲の内側だけに貼られ、色の設定は画像にだけ掛かり、α は元のまま、画像が無ければ従来どおり
    /// </summary>
    public class DecalPipelineTests
    {
        private const int Size = 64;
        /// <summary>sRGB 0.5 の線形値</summary>
        private const float Gray = 0.214f;
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
            Assert.That(RecolorPipeline.IsShiftAvailable, Is.True, "ColorShift.computeが読み込めません");
        }

        [TearDown]
        public void TearDown()
        {
            MaskCache.ClearCache();
            DecalLayerCache.ClearCache();
            PositionMap.ClearCache();
            ToolSession.Clear();
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private T Track<T>(T obj) where T : Object
        {
            _cleanup.Add(obj);
            return obj;
        }

        /// <summary>1 辺 1 の四角形（XY 平面、中心が原点、三角形の向きは +Z）。uv (0,0) が (−0.5, −0.5)、(1,1) が (0.5, 0.5)</summary>
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
            return mesh;
        }

        /// <summary>ルート（ClickRecolor 付き）の直下に、texture をメインに持つ四角形の MeshRenderer（変換なし）を置く</summary>
        private (ClickRecolor component, MeshRenderer renderer) MakeScene(Texture2D texture)
        {
            var root = Track(new GameObject("Root"));
            var component = root.AddComponent<ClickRecolor>();
            var child = new GameObject("Quad");
            child.transform.SetParent(root.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = MakeQuad();
            var renderer = child.AddComponent<MeshRenderer>();
            var material = Track(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            renderer.sharedMaterial = material;
            return (component, renderer);
        }

        /// <summary>
        /// 元テクスチャ（灰色、sRGB）。作業解像度 Size は上限なので、結果を Size×Size で読むためにテクスチャも Size×Size にする
        /// </summary>
        private Texture2D MakeSource(float alpha = 1f, int width = Size, int height = Size)
        {
            var texture = Track(new Texture2D(width, height, TextureFormat.RGBA32, false));
            var pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(0.5f, 0.5f, 0.5f, alpha);
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
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

        private sealed class Setup
        {
            public Texture2D texture;
            public ClickRecolor component;
            public MeshRenderer renderer;
            public RecolorEdit edit;
            public List<Renderer> renderers;
            public List<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users;
            public MaskContext context;
        }

        /// <summary>灰色の四角形 1 枚の場面と、島モード・画像（2×2 の赤）入りの編集。色は未決定</summary>
        private Setup MakeSetup(float sourceAlpha = 1f, int width = Size, int height = Size)
        {
            var texture = MakeSource(sourceAlpha, width, height);
            var (component, renderer) = MakeScene(texture);
            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = texture;
            edit.seedRenderer = renderer;
            edit.seedSubmesh = 0;
            edit.seedTriangle = 0;
            edit.seedUv = new Vector2(0.5f, 0.5f);
            edit.mode = SelectionMode.Island;
            edit.padding = 0;
            edit.decalEnabled = true;
            edit.decalTexture = MakeImage(2, 2, (x, y) => Color.red);
            edit.decalBoxSize = new Vector3(1f, 1f, 2f);
            component.AddEdit(edit);
            var renderers = new List<Renderer> { renderer };
            return new Setup
            {
                texture = texture,
                component = component,
                renderer = renderer,
                edit = edit,
                renderers = renderers,
                users = RecolorPreview.CollectUsers(renderers, texture),
                context = MaskContext.For(component, renderers),
            };
        }

        private static EditJob Prepare(Setup s, int size = Size) =>
            RecolorPipeline.PrepareJob(s.edit, s.texture, size, s.users, context: s.context);

        /// <summary>job を Run して線形で読み戻す</summary>
        private static Color[] RunAndRead(Setup s, EditJob job, int size = Size)
        {
            var result = RecolorPipeline.Run(new PipelineInput
            {
                sourceAsset = s.texture,
                workingSize = size,
                jobs = new[] { job },
                root = s.component.transform,
                renderers = s.renderers,
            });
            Assert.That(result, Is.Not.Null);
            try
            {
                Assert.That(result.width, Is.EqualTo(size), "前提: 結果の長辺は size");
                return ReadLinear(result);
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(result);
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

        private static Color At(Color[] pixels, int x, int y) => pixels[y * Size + x];

        private static void AssertGray(Color c, string message)
        {
            Assert.That(c.r, Is.EqualTo(Gray).Within(0.02f), message + " (r)");
            Assert.That(c.g, Is.EqualTo(Gray).Within(0.02f), message + " (g)");
            Assert.That(c.b, Is.EqualTo(Gray).Within(0.02f), message + " (b)");
        }

        [Test]
        public void 焼き込む画像の編集は最後に適用し_それ以外の順番は保つ()
        {
            // 画像の後にふつうの色変えを掛けると、範囲に入る貼った画像まで色が変わるため
            var image = new Texture2D(4, 4);
            try
            {
                var plainA = new RecolorEdit { name = "A" };
                var baked = new RecolorEdit { name = "Baked", decalEnabled = true, decalTexture = image };
                var plainB = new RecolorEdit { name = "B" };
                var overlay = new RecolorEdit { name = "Overlay", decalEnabled = true, decalTexture = image };
                var jobs = new List<EditJob>
                {
                    new EditJob { edit = plainA },
                    new EditJob { edit = baked },
                    new EditJob { edit = overlay, overlay = true },
                    new EditJob { edit = plainB },
                };

                var ordered = RecolorPipeline.OrderForApply(jobs);

                var names = ordered.ConvertAll(j => j.edit.name);
                Assert.That(names, Is.EqualTo(new[] { "A", "Overlay", "B", "Baked" }), "重ね貼りはテクスチャに何もしないので順番を変えない");
            }
            finally
            {
                Object.DestroyImmediate(image);
            }
        }

        [Test]
        public void 色が未決定でも画像が貼られる()
        {
            var s = MakeSetup();
            s.edit.hasTarget = false;

            var job = Prepare(s);
            Assert.That(job, Is.Not.Null);
            Assert.That(job.decal, Is.Not.Null, "画像入りの編集はデカール層を持つ");

            var c = At(RunAndRead(s, job), 32, 32);
            Assert.That(c.r, Is.GreaterThan(0.9f));
            Assert.That(c.g, Is.LessThan(0.05f));
        }

        [Test]
        public void αは元のまま()
        {
            var s = MakeSetup(sourceAlpha: 0.5f);
            s.edit.hasTarget = false;

            var c = At(RunAndRead(s, Prepare(s)), 32, 32);
            Assert.That(c.a, Is.EqualTo(0.5f).Within(0.02f));
        }

        [Test]
        public void 不透明度で薄くなる()
        {
            var s = MakeSetup();
            s.edit.hasTarget = false;
            s.edit.strength = 0.5f;

            var c = At(RunAndRead(s, Prepare(s)), 32, 32);
            Assert.That(c.r, Is.EqualTo(Mathf.Lerp(Gray, 1f, 0.5f)).Within(0.05f));
        }

        [Test]
        public void 新しい色は画像だけに掛かり範囲の色は変えない()
        {
            var s = MakeSetup();
            s.edit.hasTarget = true;
            s.edit.targetColor = Color.blue;
            s.edit.decalBoxSize = new Vector3(0.5f, 0.5f, 2f);

            var pixels = RunAndRead(s, Prepare(s));
            var center = At(pixels, 32, 32);
            Assert.That(center.b, Is.GreaterThan(center.r), "画像は新しい色（青）へ寄る");
            AssertGray(At(pixels, 4, 32), "箱の外・選択範囲の中は元の灰色のまま");
        }

        [Test]
        public void 選択範囲の外には貼られない()
        {
            var s = MakeSetup();
            s.edit.hasTarget = false;
            // 箱の中: 四角形の左半分（x ∈ [−0.5, 0]）だけを選ぶ。画像の箱は四角形全体
            s.edit.mode = SelectionMode.Box;
            s.edit.boxPosition = new Vector3(-0.25f, 0f, 0f);
            s.edit.boxSize = new Vector3(0.5f, 2f, 2f);
            s.edit.feather = 0f;

            var pixels = RunAndRead(s, Prepare(s));
            var left = At(pixels, 16, 32);
            Assert.That(left.r, Is.GreaterThan(0.9f), "選択範囲の中は画像（赤）");
            Assert.That(left.g, Is.LessThan(0.05f), "選択範囲の中は画像（赤）");
            AssertGray(At(pixels, 48, 32), "選択範囲の外は元の灰色のまま");
        }

        [Test]
        public void 画像が無ければ従来どおり()
        {
            var s = MakeSetup();
            s.edit.hasTarget = false;
            s.edit.decalTexture = null;

            Assert.That(RecolorPreview.IsPreviewTarget(s.edit), Is.False);
            var job = Prepare(s);
            Assert.That(job, Is.Not.Null);
            Assert.That(job.decal, Is.Null);
            AssertGray(At(RunAndRead(s, job), 32, 32), "色未決定・画像なしは素通し");
        }

        [Test]
        public void 画像入りなら種が複数でも部分は1つ()
        {
            var s = MakeSetup();
            s.edit.hasTarget = true;
            s.edit.targetColor = Color.blue;
            // 同じ四角形の三角形 1 を追加の種にし、種ごとに色を揃える
            s.edit.extraSeeds.Add(new RecolorSeed { renderer = s.renderer, submesh = 0, triangle = 1, uv = new Vector2(0.75f, 0.75f) });
            s.edit.perSeedStats = true;

            var job = Prepare(s);
            Assert.That(job?.decal, Is.Not.Null);
            Assert.That(job.parts.Count, Is.EqualTo(1), "統計は画像から取るので種ごとの部分は作らない");
            Assert.That(job.parts[0].mask, Is.SameAs(job.mask));
        }

        [Test]
        public void キャッシュに当たれば同じ層が返る()
        {
            var s = MakeSetup();
            s.edit.hasTarget = false;

            var first = Prepare(s);
            var second = Prepare(s);
            Assert.That(first?.decal, Is.Not.Null);
            Assert.That(second.decal, Is.SameAs(first.decal));
        }

        [Test]
        public void ドラッグ中は層が低解像度で作られ_離すとフル解像度に戻る()
        {
            // Size（64）は DragMaxSize より小さく縮まないので、このテストだけ上限より少し大きい非正方のテクスチャを使う
            // （長辺 520 → 512 に縮めると 512×256。比率を保つこと、層とマスクの大きさが違っても Shift と合成が通ることを見る）
            const int width = DecalLayerCache.DragMaxSize + 8, height = width / 2;
            var s = MakeSetup(width: width, height: height);
            s.edit.hasTarget = true;
            s.edit.targetColor = Color.blue;

            ToolSession.BeginDecalBoxDrag(s.edit);
            var dragging = Prepare(s, width);
            Assert.That(dragging?.decal, Is.Not.Null);
            Assert.That(dragging.mask.width, Is.EqualTo(width), "前提: 選択マスクは作業解像度のまま");
            Assert.That(dragging.decal.width, Is.EqualTo(DecalLayerCache.DragMaxSize), "ドラッグ中は長辺 DragMaxSize に縮める");
            Assert.That(dragging.decal.height, Is.EqualTo(DecalLayerCache.DragMaxSize / 2), "比率を保つ");
            var c = RunAndRead(s, dragging, width)[height / 2 * width + width / 2];
            Assert.That(c.b, Is.GreaterThan(c.r), "低解像度の層でも画像が新しい色（青）へ寄って貼られる");

            ToolSession.EndDecalBoxDrag();
            var released = Prepare(s, width);
            Assert.That(released?.decal, Is.Not.Null);
            Assert.That(released.decal.width, Is.EqualTo(width), "離したらフル解像度");
            Assert.That(released.decal.height, Is.EqualTo(height));
        }

        [Test]
        public void 別の編集の箱をドラッグ中でもこの編集の層はフル解像度()
        {
            // ドラッグ中の印は編集ごと。関係ない画像入りの編集まで低解像度で作り直さない
            const int width = DecalLayerCache.DragMaxSize + 8, height = width / 2;
            var s = MakeSetup(width: width, height: height);
            var other = new RecolorEdit { name = "other" };
            try
            {
                ToolSession.BeginDecalBoxDrag(other);
                var job = Prepare(s, width);
                Assert.That(job?.decal, Is.Not.Null);
                Assert.That(job.decal.width, Is.EqualTo(width));
            }
            finally
            {
                ToolSession.EndDecalBoxDrag();
            }
        }

        [Test]
        public void 画像の箱を動かしても選択マスクは作り直されない()
        {
            var s = MakeSetup();
            s.edit.hasTarget = false;

            var first = Prepare(s);
            Assert.That(first?.decal, Is.Not.Null);
            s.edit.decalBoxPosition = new Vector3(0.1f, 0f, 0f);
            var second = Prepare(s);

            Assert.That(second?.decal, Is.Not.Null);
            Assert.That(second.mask, Is.SameAs(first.mask), "選択マスクは MaskCache から同じものが返る");
            Assert.That(second.decal, Is.Not.SameAs(first.decal), "層は箱に合わせて作り直す");
        }

        [Test]
        public void 層が作れなければ範囲の色も変えない()
        {
            var s = MakeSetup();
            s.edit.hasTarget = true;
            s.edit.targetColor = Color.blue;

            // 文脈を渡さない: 島モードのマスクは作れるが、デカール層は作れない
            var job = RecolorPipeline.PrepareJob(s.edit, s.texture, Size, s.users);
            Assert.That(job?.mask, Is.Not.Null, "前提: マスクは作れる");
            Assert.That(job.decal, Is.Null, "前提: 層は作れない");

            AssertGray(At(RunAndRead(s, job), 32, 32), "画像入りの編集は層が無ければ素通し");
        }

        [Test]
        public void マスクだけ書き出しの重みは画像のαを掛ける()
        {
            var s = MakeSetup();
            s.edit.hasTarget = false;
            // 左列だけ不透明・右列は透明
            s.edit.decalTexture = MakeImage(2, 2, (x, y) => x == 0 ? Color.red : new Color(1f, 0f, 0f, 0f));

            var job = Prepare(s);
            Assert.That(job?.decal, Is.Not.Null);
            var accumulate = MaskTextures.Create(Size, Size, "DecalPipelineTests_Weight");
            try
            {
                UvRasterizer.Clear(accumulate);
                Assert.That(RecolorPipeline.AccumulateWeights(accumulate, new[] { job }), Is.True);
                var weights = FloodFill.ReadR8(accumulate);
                // +Z から見ると −X が画面の右なので、画像の左列（不透明）は四角形の右に来る
                Assert.That(weights[32 * Size + 48] / 255f, Is.GreaterThan(0.9f), "画像の左列（不透明）");
                Assert.That(weights[32 * Size + 16] / 255f, Is.LessThan(0.1f), "画像の右列（透明）");
            }
            finally
            {
                MaskTextures.Destroy(accumulate);
            }
        }
    }
}
