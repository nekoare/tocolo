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
    /// 土台（他のツールが先に加工したテクスチャ）の上に色を掛ける GPU テスト:
    /// 範囲は元テクスチャで選び、色は土台に掛かり、明るさの統計は土台から取り直す
    /// </summary>
    public class BaseTexturePipelineTests
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
            Assert.That(RecolorPipeline.IsShiftAvailable, Is.True, "ColorShift.computeが読み込めません");
        }

        [TearDown]
        public void TearDown()
        {
            MaskCache.ClearCache();
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

        /// <summary>
        /// 離れた 2 枚の四角形（別々の UV アイランド）。左は uv.x 0〜0.4、右は 0.6〜1。三角形 0・1 が左
        /// </summary>
        private Mesh MakeTwoQuads()
        {
            var mesh = Track(new Mesh());
            mesh.vertices = new[]
            {
                new Vector3(-1f, -0.5f, 0f), new Vector3(0f, -0.5f, 0f), new Vector3(-1f, 0.5f, 0f), new Vector3(0f, 0.5f, 0f),
                new Vector3(0.5f, -0.5f, 0f), new Vector3(1.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(1.5f, 0.5f, 0f),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(0.4f, 0f), new Vector2(0f, 1f), new Vector2(0.4f, 1f),
                new Vector2(0.6f, 0f), new Vector2(1f, 0f), new Vector2(0.6f, 1f), new Vector2(1f, 1f),
            };
            mesh.triangles = new[] { 0, 1, 2, 2, 1, 3, 4, 5, 6, 6, 5, 7 };
            return mesh;
        }

        private Texture2D MakeFlat(Color color)
        {
            var texture = Track(new Texture2D(Size, Size, TextureFormat.RGBA32, false));
            var pixels = new Color[Size * Size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private sealed class Setup
        {
            public Texture2D source;
            public ClickRecolor component;
            public RecolorEdit edit;
            public List<Renderer> renderers;
            public List<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users;
            public MaskContext context;
        }

        /// <summary>灰色の元テクスチャを使う 2 枚の四角形と、左の島を青にする編集</summary>
        private Setup MakeSetup()
        {
            var source = MakeFlat(new Color(0.5f, 0.5f, 0.5f, 1f));
            var root = Track(new GameObject("Root"));
            var component = root.AddComponent<ClickRecolor>();
            var child = new GameObject("Quads");
            child.transform.SetParent(root.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = MakeTwoQuads();
            var renderer = child.AddComponent<MeshRenderer>();
            var material = Track(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", source);
            renderer.sharedMaterial = material;

            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = source;
            edit.seedRenderer = renderer;
            edit.seedSubmesh = 0;
            edit.seedTriangle = 0;
            edit.seedUv = new Vector2(0.2f, 0.5f);
            edit.mode = SelectionMode.Island;
            edit.padding = 0;
            edit.targetColor = Color.blue;
            edit.hasTarget = true;
            component.AddEdit(edit);

            var renderers = new List<Renderer> { renderer };
            return new Setup
            {
                source = source,
                component = component,
                edit = edit,
                renderers = renderers,
                users = RecolorPreview.CollectUsers(renderers, source),
                context = MaskContext.For(component, renderers),
            };
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

        [Test]
        public void 土台を渡すと_範囲の外は土台の色のまま_範囲の中だけ色が変わる()
        {
            var s = MakeSetup();
            var baseTexture = MakeFlat(Color.red);
            var job = RecolorPipeline.PrepareJob(s.edit, s.source, Size, s.users, context: s.context);
            Assert.That(job, Is.Not.Null, "前提: 選択マスクが作れる");
            var jobs = RecolorPipeline.RebaseStats(new[] { job }, baseTexture, s.source, Size, s.context);

            var result = RecolorPipeline.Run(new PipelineInput
            {
                sourceAsset = s.source,
                baseTexture = baseTexture,
                workingSize = Size,
                jobs = jobs,
                root = s.component.transform,
                renderers = s.renderers,
            });
            Assert.That(result, Is.Not.Null);
            try
            {
                Assert.That(result.width, Is.EqualTo(Size), "前提: 結果の大きさは元テクスチャの作業サイズ");
                var pixels = ReadLinear(result);
                var outside = pixels[(Size / 2) * Size + (int)(Size * 0.8f)];
                Assert.That(outside.r, Is.EqualTo(1f).Within(0.02f), "右の島（範囲の外）は土台の赤のまま");
                Assert.That(outside.g, Is.EqualTo(0f).Within(0.02f));
                Assert.That(outside.b, Is.EqualTo(0f).Within(0.02f));

                var inside = pixels[(Size / 2) * Size + (int)(Size * 0.2f)];
                Assert.That(inside.b, Is.GreaterThan(inside.r), "左の島（範囲の中）は青に寄る");
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(result);
            }
        }

        [Test]
        public void 土台から統計を取り直すと_明るさは土台のもので_元の統計は書き換えない()
        {
            var s = MakeSetup();
            var brighter = MakeFlat(new Color(0.95f, 0.95f, 0.95f, 1f));
            var job = RecolorPipeline.PrepareJob(s.edit, s.source, Size, s.users, context: s.context);
            Assert.That(job, Is.Not.Null, "前提: 選択マスクが作れる");
            float sourceP95 = job.stats.lP95;

            var rebased = RecolorPipeline.RebaseStats(new[] { job }, brighter, s.source, Size, s.context);

            Assert.That(rebased.Count, Is.EqualTo(1));
            Assert.That(rebased[0].stats.lP05, Is.GreaterThan(sourceP95 + 0.1f), "統計は明るい土台から取られる");
            Assert.That(rebased[0].mask, Is.SameAs(job.mask), "範囲マスクは元テクスチャで選んだもののまま");
            Assert.That(job.stats.lP95, Is.EqualTo(sourceP95), "キャッシュの持ち物の元の Job は書き換えない");
            Assert.That(job.ShiftParts[0].stats.lP95, Is.EqualTo(sourceP95));
        }

        [Test]
        public void 土台が無ければ_統計を取り直さず同じJobを返す()
        {
            var s = MakeSetup();
            var job = RecolorPipeline.PrepareJob(s.edit, s.source, Size, s.users, context: s.context);

            var rebased = RecolorPipeline.RebaseStats(new[] { job }, null, s.source, Size, s.context);

            Assert.That(rebased, Is.EqualTo(new[] { job }));
        }
    }
}
