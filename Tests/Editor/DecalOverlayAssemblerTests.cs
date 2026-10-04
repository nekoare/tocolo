using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Decal;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Pipeline;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// DecalOverlayAssembler.AddEdit の所有権のテスト。マテリアルの作成は呼び出し側に任せる設計なので、Standard を返すスタブを注入して
    /// lilToon 無しで確かめる: Detach 前の Dispose で全部破棄する、マテリアルを作れなかった段は前段の表示用メッシュを保つ
    /// </summary>
    public class DecalOverlayAssemblerTests
    {
        private const int Size = 8;

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

        /// <summary>1 辺 1 の +Z 向き四角形（DecalOverlayPreviewTests と同じ作り）</summary>
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

        /// <summary>全面 1 の選択マスク</summary>
        private RenderTexture MakeFullMask()
        {
            var mask = MaskTextures.Create(Size, Size, "Test_Mask");
            _masks.Add(mask);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(Texture2D.whiteTexture, mask);
            }
            finally
            {
                RenderTexture.active = previous;
            }
            return mask;
        }

        private RecolorEdit MakeDecalEdit(Texture2D source)
        {
            var decal = Track(new Texture2D(Size, Size, TextureFormat.RGBA32, false));
            var pixels = new Color32[Size * Size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
            decal.SetPixels32(pixels);
            decal.Apply(false);

            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = source;
            edit.decalEnabled = true;
            edit.decalTexture = decal;
            edit.decalBoxPosition = Vector3.zero;
            edit.decalBoxRotation = Quaternion.identity;
            edit.decalBoxSize = new Vector3(1f, 1f, 2f);
            return edit;
        }

        /// <summary>ルート（原点）の子に、Standard（メインテクスチャ = source）の四角形を持つ MeshRenderer を置く</summary>
        private (Transform root, MeshRenderer renderer, Mesh mesh) MakeRenderer(Texture2D source)
        {
            var root = Track(new GameObject("Avatar")).transform;
            var child = new GameObject("Quad");
            child.transform.SetParent(root, false);
            var mesh = MakeQuad();
            child.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = child.AddComponent<MeshRenderer>();
            var material = Track(new Material(Shader.Find("Standard")));
            material.mainTexture = source;
            renderer.sharedMaterials = new[] { material };
            return (root, renderer, mesh);
        }

        [Test]
        public void Detachしなければ_Disposeで表示用メッシュ_マテリアル_画像を破棄する()
        {
            var source = Track(new Texture2D(Size, Size));
            var (root, renderer, mesh) = MakeRenderer(source);
            var created = new List<Material>();
            var assembler = new DecalOverlayAssembler(new Renderer[] { renderer },
                (r, overlaySource) =>
                {
                    var m = new Material(Shader.Find("Standard")) { mainTexture = overlaySource.image };
                    created.Add(m);
                    return m;
                },
                null);

            assembler.AddEdit(MakeDecalEdit(source), source, root, MakeFullMask(), 64);

            Assert.That(assembler.Overlays.ContainsKey(renderer), Is.True);
            var overlay = assembler.Overlays[renderer];
            var display = overlay.displayMesh;
            Assert.That(display, Is.Not.SameAs(mesh));
            Assert.That(overlay.materials.Count, Is.EqualTo(1));
            Assert.That(assembler.Images.Count, Is.EqualTo(1));
            var image = assembler.Images[0];

            assembler.Dispose();

            Assert.That(display == null, Is.True, "表示用メッシュが破棄されていません");
            Assert.That(created[0] == null, Is.True, "マテリアルが破棄されていません");
            Assert.That(image == null, Is.True, "画像が破棄されていません");
            Assert.That(mesh != null, Is.True, "元メッシュは破棄しない");
        }

        [Test]
        public void マテリアルを作れなかった段は捨て_前段の表示用メッシュを保つ_Detach後は破棄しない()
        {
            var source = Track(new Texture2D(Size, Size));
            var (root, renderer, mesh) = MakeRenderer(source);
            int calls = 0;
            var warnings = new List<OverlayWarning>();
            var assembler = new DecalOverlayAssembler(new Renderer[] { renderer },
                (r, overlaySource) =>
                {
                    calls++;
                    // 2 段目は作れない
                    return calls == 1 ? Track(new Material(Shader.Find("Standard")) { mainTexture = overlaySource.image }) : null;
                },
                (kind, edit, target) => warnings.Add(kind));
            Mesh display;
            try
            {
                assembler.AddEdit(MakeDecalEdit(source), source, root, MakeFullMask(), 64);
                var first = assembler.Overlays[renderer].displayMesh;
                assembler.AddEdit(MakeDecalEdit(source), source, root, MakeFullMask(), 64);

                var overlay = assembler.Overlays[renderer];
                display = overlay.displayMesh;
                Assert.That(display, Is.SameAs(first), "作れなかった段の表示用メッシュに差し替えない");
                Assert.That(display.subMeshCount, Is.EqualTo(mesh.subMeshCount + 1));
                Assert.That(overlay.materials.Count, Is.EqualTo(1));
                Assert.That(assembler.Images.Count, Is.EqualTo(1), "使われなかった画像は積まない");
                Assert.That(warnings, Is.EqualTo(new[] { OverlayWarning.MaterialFailed }));

                assembler.Detach();
                foreach (var image in assembler.Images) Track(image);
            }
            finally
            {
                assembler.Dispose();
            }
            Assert.That(display != null, Is.True, "Detach 後の Dispose は表示用メッシュを破棄しない");
            Track(display);
        }
    }
}
