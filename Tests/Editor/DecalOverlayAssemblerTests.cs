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

        /// <summary>重ね貼りサブメッシュ（末尾）の三角形の頂点番号の数</summary>
        private static int OverlayIndexCount(Mesh display) => display.GetIndices(display.subMeshCount - 1).Length;

        private DecalOverlayAssembler MakeAssembler(Renderer renderer, bool forPreview)
        {
            return new DecalOverlayAssembler(new[] { renderer },
                (r, overlaySource) => Track(new Material(Shader.Find("Standard")) { mainTexture = overlaySource.image }), null)
            {
                ForPreview = forPreview,
            };
        }

        [Test]
        public void プレビュー用は箱を動かしても表示用メッシュを作り直さず_描く三角形と画像だけ置き直す()
        {
            var source = Track(new Texture2D(Size, Size));
            var (root, renderer, _) = MakeRenderer(source);
            var edit = MakeDecalEdit(source);
            var assembler = MakeAssembler(renderer, forPreview: true);
            try
            {
                assembler.AddEdit(edit, source, root, MakeFullMask(), 64, maskSize: Size);
                var overlay = assembler.Overlays[renderer];
                var display = overlay.displayMesh;
                int vertexCount = display.vertexCount;
                Assert.That(OverlayIndexCount(display), Is.EqualTo(6), "前提: 箱が四角形全体にかかる");
                Assert.That(assembler.Placements.Count, Is.EqualTo(1));
                var placement = assembler.Placements[0];
                Assert.That(placement.replaceable, Is.True);

                // 左下の三角形だけにかかる小さい箱へ
                edit.decalBoxPosition = new Vector3(-0.35f, -0.35f, 0f);
                edit.decalBoxSize = new Vector3(0.3f, 0.3f, 2f);
                bool rebuilt = DecalOverlayAssembler.RebuildPlacement(placement, edit, r => overlay.displayMesh, MakeFullMask(), false, 64,
                    out var image, out var imageBase, out var normal, out var bump2ndMask);
                try
                {
                    Assert.That(rebuilt, Is.True);
                    Assert.That(overlay.displayMesh, Is.SameAs(display), "表示用メッシュは作り直さない");
                    Assert.That(display.vertexCount, Is.EqualTo(vertexCount));
                    Assert.That(OverlayIndexCount(display), Is.EqualTo(3), "今の箱にかかる三角形だけ描く");
                    Assert.That(image, Is.Not.Null);
                    Assert.That(imageBase, Is.Not.Null, "休止中は色の掛け直し用の下地も作る");
                }
                finally
                {
                    RecolorPipeline.DestroyWorkTexture(image);
                    imageBase?.Dispose();
                    RecolorPipeline.DestroyWorkTexture(normal);
                    RecolorPipeline.DestroyWorkTexture(bump2ndMask);
                }
            }
            finally
            {
                assembler.Dispose();
            }
        }

        [Test]
        public void プレビュー用が今の箱で描く三角形は_ビルド用の組み立てと同じ()
        {
            var source = Track(new Texture2D(Size, Size));
            var (root, renderer, _) = MakeRenderer(source);
            var edit = MakeDecalEdit(source);
            edit.decalBoxPosition = new Vector3(-0.35f, -0.35f, 0f);
            edit.decalBoxSize = new Vector3(0.3f, 0.3f, 2f);
            var preview = MakeAssembler(renderer, forPreview: true);
            var build = MakeAssembler(renderer, forPreview: false);
            try
            {
                preview.AddEdit(edit, source, root, MakeFullMask(), 64, maskSize: Size);
                build.AddEdit(edit, source, root, MakeFullMask(), 64);

                var previewMesh = preview.Overlays[renderer].displayMesh;
                var buildMesh = build.Overlays[renderer].displayMesh;
                Assert.That(OverlayIndexCount(previewMesh), Is.EqualTo(OverlayIndexCount(buildMesh)));
                Assert.That(OverlayIndexCount(previewMesh), Is.EqualTo(3));
            }
            finally
            {
                preview.Dispose();
                build.Dispose();
            }
        }

        [Test]
        public void シールは面に沿って奥行きの外へ回り込む分まで複製した範囲に収まるかで見る()
        {
            // 作ったときの箱（原点・正面向き・0.2 四方・奥行き 0.2）を各軸 2 倍に広げた範囲
            var segment = new DecalOverlayAssembler.OverlaySegment
            {
                regionRootToBox = BoxMaskBuilder.RootToBox(Vector3.zero, Quaternion.identity),
                regionHalf = new Vector3(0.2f, 0.2f, 0.2f),
            };
            var sticker = MakePlacedEdit(sticker: true, new Vector3(0.2f, 0.2f, 0.2f));
            var box = MakePlacedEdit(sticker: false, new Vector3(0.2f, 0.2f, 0.2f));
            Assert.That(DecalOverlayAssembler.CoversBox(segment, sticker), Is.True, "つかんだだけなら範囲の中");

            // 奥行きの向きに 0.06 ずらす: 箱（奥行きの半分 0.1）は収まるが、シールは届く距離（対角線の半分 × 1.15 ≒ 0.163）まで回り込むので外
            sticker.decalBoxPosition = box.decalBoxPosition = new Vector3(0f, 0f, 0.06f);
            Assert.That(DecalOverlayAssembler.CoversBox(segment, box), Is.True);
            Assert.That(DecalOverlayAssembler.CoversBox(segment, sticker), Is.False);
        }

        [Test]
        public void 横長のシールもつかんだだけなら複製した範囲の中()
        {
            // 0.4 × 0.1 のシール（奥行きは長い辺の 0.4）。届く距離（≒ 0.237）は縦の範囲（0.1）より大きいが、縦横は箱のまま見る
            var segment = new DecalOverlayAssembler.OverlaySegment
            {
                regionRootToBox = BoxMaskBuilder.RootToBox(Vector3.zero, Quaternion.identity),
                regionHalf = new Vector3(0.4f, 0.1f, 0.4f),
            };

            Assert.That(DecalOverlayAssembler.CoversBox(segment, MakePlacedEdit(sticker: true, new Vector3(0.4f, 0.1f, 0.4f))), Is.True);
        }

        private static RecolorEdit MakePlacedEdit(bool sticker, Vector3 size)
        {
            var edit = RecolorEdit.CreateNew();
            edit.decalEnabled = true;
            edit.decalSticker = sticker;
            edit.decalBoxPosition = Vector3.zero;
            edit.decalBoxRotation = Quaternion.identity;
            edit.decalBoxSize = size;
            return edit;
        }

        [Test]
        public void 箱が複製した範囲の外まで動いたら置き直さない()
        {
            var source = Track(new Texture2D(Size, Size));
            var (root, renderer, _) = MakeRenderer(source);
            var edit = MakeDecalEdit(source);
            // 左下の小さい箱で作る（複製するのは箱を各軸 2 倍に広げた範囲にかかる左下の三角形だけ）
            edit.decalBoxPosition = new Vector3(-0.35f, -0.35f, 0f);
            edit.decalBoxSize = new Vector3(0.3f, 0.3f, 2f);
            var assembler = MakeAssembler(renderer, forPreview: true);
            try
            {
                assembler.AddEdit(edit, source, root, MakeFullMask(), 64, maskSize: Size);
                var overlay = assembler.Overlays[renderer];
                var placement = assembler.Placements[0];
                var segment = placement.steps[0].segment;

                edit.decalBoxPosition = new Vector3(-0.3f, -0.3f, 0f);
                Assert.That(DecalOverlayAssembler.CoversBox(segment, edit), Is.True, "少し動かしただけなら範囲の中");

                // 右上へ大きく動かす: 右上の三角形は複製していない
                edit.decalBoxPosition = new Vector3(0.35f, 0.35f, 0f);
                Assert.That(DecalOverlayAssembler.CoversBox(segment, edit), Is.False);
                bool rebuilt = DecalOverlayAssembler.RebuildPlacement(placement, edit, r => overlay.displayMesh, MakeFullMask(), false, 64,
                    out var image, out var imageBase, out var normal, out var bump2ndMask);
                Assert.That(rebuilt, Is.False, "作り直す（置き直さない）");
                Assert.That(image, Is.Null);
                Assert.That(imageBase, Is.Null);
            }
            finally
            {
                assembler.Dispose();
            }
        }

        [Test]
        public void 置き直しは今の姿勢で描く三角形を選ぶ()
        {
            var source = Track(new Texture2D(Size, Size));
            var (root, renderer, _) = MakeRenderer(source);
            var edit = MakeDecalEdit(source);
            var assembler = MakeAssembler(renderer, forPreview: true);
            try
            {
                // 四角形全体にかかる箱で作る（両方の三角形を複製してある）
                assembler.AddEdit(edit, source, root, MakeFullMask(), 64, maskSize: Size);
                var overlay = assembler.Overlays[renderer];
                var placement = assembler.Placements[0];
                var segment = placement.steps[0].segment;

                // 作った後で四角形を左下へ動かし、左下の小さい箱にする: 今の位置なら右上の三角形（頂点 1・2・3）にかかる
                // （作ったときの位置のままだと左下の三角形（頂点 0・1・2）を選んでしまう）
                renderer.transform.localPosition = new Vector3(-0.6f, -0.6f, 0f);
                edit.decalBoxPosition = new Vector3(-0.35f, -0.35f, 0f);
                edit.decalBoxSize = new Vector3(0.3f, 0.3f, 2f);
                bool rebuilt = DecalOverlayAssembler.RebuildPlacement(placement, edit, r => overlay.displayMesh, MakeFullMask(), false, 64,
                    out var image, out var imageBase, out var normal, out var bump2ndMask);
                try
                {
                    Assert.That(rebuilt, Is.True);
                    var indices = overlay.displayMesh.GetIndices(segment.submesh);
                    var sources = new HashSet<int>();
                    foreach (int d in indices) sources.Add(segment.sources[d - segment.start]);
                    Assert.That(sources, Is.EquivalentTo(new[] { 1, 2, 3 }));
                }
                finally
                {
                    RecolorPipeline.DestroyWorkTexture(image);
                    imageBase?.Dispose();
                    RecolorPipeline.DestroyWorkTexture(normal);
                    RecolorPipeline.DestroyWorkTexture(bump2ndMask);
                }
            }
            finally
            {
                assembler.Dispose();
            }
        }

        [Test]
        public void 画像の差し替えは写し直した法線マップのプロパティだけ置き換える()
        {
            var material = Track(new Material(Shader.Find("Standard")));
            var originalNormal = Track(new Texture2D(4, 4));
            material.SetTexture("_BumpMap", originalNormal);
            var image = new RenderTexture(4, 4, 0);
            var normal = new RenderTexture(4, 4, 0);
            try
            {
                DecalOverlayMaterial.SetImages(material, image, normal, null);
                Assert.That(material.GetTexture("_MainTex"), Is.SameAs(image));
                Assert.That(material.GetTexture("_BumpMap"), Is.SameAs(originalNormal), "元の法線マップのままのプロパティは触らない");

                var remapped = new RenderTexture(4, 4, 0);
                material.SetTexture("_BumpMap", remapped);
                DecalOverlayMaterial.SetImages(material, null, normal, null);
                Assert.That(material.GetTexture("_BumpMap"), Is.SameAs(normal), "写し直した法線マップは差し替える");
                Object.DestroyImmediate(remapped);
            }
            finally
            {
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(normal);
            }
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
