using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.NDMF;
using Nekoare.ClickRecolor.Editor.Pipeline;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>お手本に合わせる編集を、PrepareJob → Run の本来の入口から通す GPU テスト</summary>
    public class ColorMatchPrepareJobTests
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

        /// <summary>離れた 2 枚の四角形（別々の UV アイランド）。左は uv.x 0〜0.4、右は 0.6〜1。三角形 0・1 が左</summary>
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

        /// <summary>縦に暗→明の灰色のグラデーション（髪の陰影の代わり）</summary>
        private Texture2D MakeGrayGradient()
        {
            var texture = Track(new Texture2D(Size, Size, TextureFormat.RGBA32, false));
            var pixels = new Color[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                float v = Mathf.Lerp(0.2f, 0.8f, y / (Size - 1f));
                for (int x = 0; x < Size; x++) pixels[y * Size + x] = new Color(v, v, v, 1f);
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        /// <summary>お手本: 明るさ 0.5〜0.8 の赤みの髪（a = 0.1）</summary>
        private static string ReddishReference()
        {
            var random = new System.Random(3);
            var labs = new List<Vector3>();
            for (int i = 0; i < 2000; i++) labs.Add(new Vector3(0.5f + 0.3f * (float)random.NextDouble(), 0.1f, 0.02f));
            return MatchProfile.FromOklab(labs).Serialize();
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

        /// <summary>灰色のグラデーションを使う 2 枚の四角形と、左の島をお手本に合わせる編集</summary>
        private Setup MakeSetup(string profileData)
        {
            var source = MakeGrayGradient();
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
            edit.matchEnabled = true;
            edit.matchProfileData = profileData;
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

        private static Vector3 Lab(Color linear) => OklabConverter.LinearRGBToOklab(new Vector3(linear.r, linear.g, linear.b));

        private RenderTexture Run(Setup s, Texture baseTexture = null)
        {
            var job = RecolorPipeline.PrepareJob(s.edit, s.source, Size, s.users, context: s.context);
            Assert.That(job, Is.Not.Null, "前提: 選択マスクが作れる");
            return RecolorPipeline.Run(new PipelineInput
            {
                sourceAsset = s.source,
                baseTexture = baseTexture,
                workingSize = Size,
                jobs = new[] { job },
                root = s.component.transform,
                renderers = s.renderers,
            });
        }

        [Test]
        public void クリックしたパーツだけがお手本の色になり陰影は残る()
        {
            var s = MakeSetup(ReddishReference());
            var result = Run(s);
            Assert.That(result, Is.Not.Null);
            try
            {
                var pixels = ReadLinear(result);
                var outside = Lab(pixels[(Size / 2) * Size + (int)(Size * 0.8f)]);
                Assert.That(Mathf.Abs(outside.y), Is.LessThan(0.01f), "右の島（範囲の外）は灰色のまま");

                var dark = Lab(pixels[(Size / 8) * Size + (int)(Size * 0.2f)]);
                var light = Lab(pixels[(Size * 7 / 8) * Size + (int)(Size * 0.2f)]);
                Assert.That(dark.y, Is.GreaterThan(0.06f), "左の島はお手本の赤みになる");
                Assert.That(light.y, Is.GreaterThan(0.06f));
                Assert.That(light.x, Is.GreaterThan(dark.x + 0.1f), "元の陰影（下が暗い）は残る");
                Assert.That(dark.x, Is.GreaterThan(0.45f), "明るさはお手本の範囲へ写る");
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(result);
            }
        }

        [Test]
        public void 土台の上にはお手本に合わせる編集を掛けない()
        {
            var s = MakeSetup(ReddishReference());
            var baseTexture = MakeGrayGradient();
            var result = Run(s, baseTexture);
            Assert.That(result, Is.Not.Null);
            try
            {
                var inside = Lab(ReadLinear(result)[(Size / 2) * Size + (int)(Size * 0.2f)]);
                Assert.That(Mathf.Abs(inside.y), Is.LessThan(0.01f), "髪ツールの髪（土台あり）の上では灰色のまま");
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(result);
            }
        }

        [Test]
        public void 控えが無ければ写し方は付かず素通しになる()
        {
            var s = MakeSetup(null);
            var job = RecolorPipeline.PrepareJob(s.edit, s.source, Size, s.users, context: s.context);
            Assert.That(job, Is.Not.Null);
            Assert.That(job.match.HasValue, Is.False);
        }

        [Test]
        public void 控えがあれば写し方が付く()
        {
            var s = MakeSetup(ReddishReference());
            var job = RecolorPipeline.PrepareJob(s.edit, s.source, Size, s.users, context: s.context);
            Assert.That(job, Is.Not.Null);
            Assert.That(job.match.HasValue, Is.True);
        }
    }
}
