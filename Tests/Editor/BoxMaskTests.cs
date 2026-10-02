using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.NDMF;
using Nekoare.ClickRecolor.Editor.Pipeline;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 影響範囲「箱の中」（BoxMaskBuilder / RecolorPipeline の Box モード）の GPU テスト:
    /// 表面が箱の内側に写る画素が 1、外なら 0 になり、除外リストの Renderer は描かれないこと
    /// </summary>
    public class BoxMaskTests
    {
        private const int Size = 64;
        private readonly List<Object> _cleanup = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            {
                Assert.Ignore("この環境はcompute shaderかARGBFloatのRenderTextureに対応していません");
            }
            Assert.That(BoxMaskBuilder.IsAvailable, Is.True, "BoxMask.shaderが読み込めません（.metaのGUIDを確認）");
        }

        private const string TextureAPath = "Assets/__ClickRecolorBoxMaskTests_A.asset";
        private const string TextureBPath = "Assets/__ClickRecolorBoxMaskTests_B.asset";

        [TearDown]
        public void TearDown()
        {
            ToolSession.Clear();
            MaskCache.ClearCache();
            PositionMap.ClearCache();
            AssetDatabase.DeleteAsset(TextureAPath);
            AssetDatabase.DeleteAsset(TextureBPath);
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private T Track<T>(T obj) where T : Object
        {
            _cleanup.Add(obj);
            return obj;
        }

        /// <summary>1 辺 1 の四角形（XY 平面、中心が原点）。uv (0,0) が (−0.5, −0.5)、(1,1) が (0.5, 0.5)</summary>
        private Mesh MakeQuad()
        {
            var mesh = Track(new Mesh());
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
            mesh.triangles = new[] { 0, 2, 1, 2, 3, 1 };
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

        /// <summary>R8 の読み戻し（0..255）を 0..1 に</summary>
        private static float PixelAt(byte[] pixels, int x, int y) => pixels[y * Size + x] / 255f;

        [Test]
        public void 箱の左半分を囲むと_左の画素が1で右の画素が0()
        {
            var texture = Track(new Texture2D(4, 4));
            var (component, renderer) = MakeScene(texture);
            var slots = PositionMap.CollectSlots(new Renderer[] { renderer }, texture);

            // 箱: 中心 x = −0.25、幅 0.5 → x ∈ [−0.5, 0]（四角形の左半分）。y, z は十分大きく
            var mask = BoxMaskBuilder.Build(component.transform, slots, Size, Size, new Vector3(-0.25f, 0f, 0f), Quaternion.identity, new Vector3(0.5f, 2f, 2f), 0f);
            try
            {
                Assert.That(mask, Is.Not.Null);
                var pixels = FloodFill.ReadR8(mask);
                Assert.That(PixelAt(pixels, 8, 32), Is.GreaterThan(0.99f), "左（u=0.13）は箱の中");
                Assert.That(PixelAt(pixels, 24, 32), Is.GreaterThan(0.99f), "左寄り（u=0.38）は箱の中");
                Assert.That(PixelAt(pixels, 40, 32), Is.LessThan(0.01f), "右寄り（u=0.63）は箱の外");
                Assert.That(PixelAt(pixels, 56, 32), Is.LessThan(0.01f), "右（u=0.88）は箱の外");
            }
            finally
            {
                MaskTextures.Destroy(mask);
            }
        }

        [Test]
        public void 回転した箱でも面で切れる()
        {
            var texture = Track(new Texture2D(4, 4));
            var (component, renderer) = MakeScene(texture);
            var slots = PositionMap.CollectSlots(new Renderer[] { renderer }, texture);

            // 箱を Z 軸まわりに 90 度回して、箱の「幅」を四角形の縦方向に向ける: y ∈ [−0.5, 0]（下半分）
            var mask = BoxMaskBuilder.Build(component.transform, slots, Size, Size, new Vector3(0f, -0.25f, 0f), Quaternion.Euler(0f, 0f, 90f), new Vector3(0.5f, 2f, 2f), 0f);
            try
            {
                var pixels = FloodFill.ReadR8(mask);
                Assert.That(PixelAt(pixels, 32, 8), Is.GreaterThan(0.99f), "下（v=0.13）は箱の中");
                Assert.That(PixelAt(pixels, 32, 56), Is.LessThan(0.01f), "上（v=0.88）は箱の外");
            }
            finally
            {
                MaskTextures.Destroy(mask);
            }
        }

        [Test]
        public void ぼかしで面の近くがなだらかになる()
        {
            var texture = Track(new Texture2D(4, 4));
            var (component, renderer) = MakeScene(texture);
            var slots = PositionMap.CollectSlots(new Renderer[] { renderer }, texture);

            // 幅 1 の箱（x ∈ [−0.5, 0.5]、半分の長さ 0.5）にぼかし 0.5 → 面から 0.25 で 1 になる
            var mask = BoxMaskBuilder.Build(component.transform, slots, Size, Size, Vector3.zero, Quaternion.identity, new Vector3(1f, 2f, 2f), 0.5f);
            try
            {
                var pixels = FloodFill.ReadR8(mask);
                float center = PixelAt(pixels, 32, 32);
                float nearFace = PixelAt(pixels, 2, 32); // x ≈ −0.46（面から 0.04）
                Assert.That(center, Is.GreaterThan(0.99f));
                Assert.That(nearFace, Is.GreaterThan(0.05f).And.LessThan(0.3f), "面の近くは0と1の間");
            }
            finally
            {
                MaskTextures.Destroy(mask);
            }
        }

        [Test]
        public void 同じUVを共有する2面のどちらかが箱の中なら選ばれる()
        {
            // 左右の袖のように同じテクスチャ・同じ UV を使う四角形を 2 つ（x = 0 と x = 2）。箱は x = 2 の方だけを囲む
            var texture = Track(new Texture2D(4, 4));
            var (component, rendererA) = MakeScene(texture);
            var rendererB = AddQuad(component.gameObject, texture, 2f);
            var slots = PositionMap.CollectSlots(new Renderer[] { rendererA, rendererB }, texture);

            var mask = BoxMaskBuilder.Build(component.transform, slots, Size, Size, new Vector3(2f, 0f, 0f), Quaternion.identity, new Vector3(1.2f, 2f, 2f), 0f);
            try
            {
                var pixels = FloodFill.ReadR8(mask);
                Assert.That(PixelAt(pixels, 32, 32), Is.GreaterThan(0.99f), "箱の外の面が同じ画素を使っていても、箱の中の面があれば選ばれる");
            }
            finally
            {
                MaskTextures.Destroy(mask);
            }
        }

        [Test]
        public void PrepareJob_の箱モードは除外リストの_Renderer_を位置マップに描かない()
        {
            var texture = Track(new Texture2D(4, 4));
            var (component, renderer) = MakeScene(texture);
            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = texture;
            edit.seedRenderer = renderer;
            edit.mode = SelectionMode.Box;
            edit.boxPosition = Vector3.zero;
            edit.boxSize = new Vector3(2f, 2f, 2f);
            edit.hasTarget = true;
            component.AddEdit(edit);
            var renderers = new List<Renderer> { renderer };
            var users = RecolorPreview.CollectUsers(renderers, texture);

            var job = RecolorPipeline.PrepareJob(edit, texture, Size, users, context: MaskContext.For(component, renderers));
            Assert.That(job?.mask, Is.Not.Null, "除外なし: 箱の中のマスクが作れる");
            // 作業解像度は上限なので、4×4 のテクスチャのマスクは 4×4 のまま。中央の画素で見る
            var center = FloodFill.ReadR8(job.mask)[(job.mask.height / 2) * job.mask.width + job.mask.width / 2] / 255f;
            Assert.That(center, Is.GreaterThan(0.99f));
            MaskCache.Trim();

            component.excludedRenderers.Add(renderer);
            var excludedJob = RecolorPipeline.PrepareJob(edit, texture, Size, users, context: MaskContext.For(component, renderers));
            Assert.That(excludedJob, Is.Null, "唯一のRendererを除外したら位置マップが描けず、マスクは作れない");

            Assert.That(RecolorPipeline.PrepareJob(edit, texture, Size, users), Is.Null, "文脈が無ければ箱モードは作れない");
        }
    
        private static Texture2D MakeTextureAsset(string path)
        {
            var texture = new Texture2D(4, 4);
            AssetDatabase.CreateAsset(texture, path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>root の直下に、texture をメインに持つ四角形を x = offsetX に置く</summary>
        private MeshRenderer AddQuad(GameObject root, Texture2D texture, float offsetX)
        {
            var child = new GameObject("Quad" + offsetX);
            child.transform.SetParent(root.transform, false);
            child.transform.localPosition = new Vector3(offsetX, 0f, 0f);
            child.AddComponent<MeshFilter>().sharedMesh = MakeQuad();
            var renderer = child.AddComponent<MeshRenderer>();
            var material = Track(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            renderer.sharedMaterial = material;
            return renderer;
        }

        [Test]
        public void マテリアルをまたいで選ぶ_ONなら_箱に触れる他テクスチャのメンバーが増減する()
        {
            var textureA = MakeTextureAsset(TextureAPath);
            var textureB = MakeTextureAsset(TextureBPath);
            var root = Track(new GameObject("Root"));
            var component = root.AddComponent<ClickRecolor>();
            var rendererA = AddQuad(root, textureA, 0f);   // x ∈ [−0.5, 0.5]
            var rendererB = AddQuad(root, textureB, 2f);   // x ∈ [1.5, 2.5]
            ToolSession.CrossTextureEnabled = true;

            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = textureA;
            edit.seedRenderer = rendererA;
            edit.mode = SelectionMode.Box;
            edit.hasTarget = true;
            component.AddEdit(edit);

            // 箱が A だけを囲む: メンバーは増えない
            edit.boxPosition = Vector3.zero;
            edit.boxSize = new Vector3(1.2f, 2f, 2f);
            SelectionBox.SyncMembers(component, edit);
            Assert.That(EditGroups.Members(component, edit).Count, Is.EqualTo(1));

            // 箱を B まで広げる: B 用のメンバーができ、箱と設定が同じ
            edit.boxPosition = new Vector3(1f, 0f, 0f);
            edit.boxSize = new Vector3(3.2f, 2f, 2f);
            SelectionBox.SyncMembers(component, edit);
            var members = EditGroups.Members(component, edit);
            Assert.That(members.Count, Is.EqualTo(2));
            var memberB = EditGroups.FindMemberForTexture(component, edit, textureB);
            Assert.That(memberB, Is.Not.Null);
            Assert.That(memberB.mode, Is.EqualTo(SelectionMode.Box));
            Assert.That(memberB.seedRenderer, Is.SameAs(rendererB));
            Assert.That(memberB.boxSize, Is.EqualTo(edit.boxSize));

            // 除外リストに B を入れて同期: B は箱に触れない扱いになりメンバーが消える
            component.excludedRenderers.Add(rendererB);
            SelectionBox.SyncMembers(component, edit);
            Assert.That(EditGroups.Members(component, edit).Count, Is.EqualTo(1));
            Assert.That(EditGroups.IsGrouped(edit), Is.False, "残り1件なら単独に戻る");

            // OFF なら他テクスチャのメンバーを外して 1 テクスチャに戻る（ON で増やしてから OFF にする）
            component.excludedRenderers.Clear();
            SelectionBox.SyncMembers(component, edit);
            Assert.That(EditGroups.Members(component, edit).Count, Is.EqualTo(2), "前提: ONでBのメンバーが戻る");
            ToolSession.CrossTextureEnabled = false;
            SelectionBox.SyncMembers(component, edit);
            Assert.That(EditGroups.Members(component, edit).Count, Is.EqualTo(1));
        }
    
        [Test]
        public void 連結メンバーを持つ箱の編集を削除すると_編集一覧から全部消える()
        {
            var textureA = MakeTextureAsset(TextureAPath);
            var textureB = MakeTextureAsset(TextureBPath);
            var root = Track(new GameObject("Root"));
            var component = root.AddComponent<ClickRecolor>();
            var rendererA = AddQuad(root, textureA, 0f);
            AddQuad(root, textureB, 2f);
            ToolSession.CrossTextureEnabled = true;

            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = textureA;
            edit.seedRenderer = rendererA;
            edit.mode = SelectionMode.Box;
            edit.hasTarget = true;
            edit.boxPosition = new Vector3(1f, 0f, 0f);
            edit.boxSize = new Vector3(3.2f, 2f, 2f);
            component.AddEdit(edit);
            SelectionBox.SyncMembers(component, edit);
            Assert.That(component.edits.Count, Is.EqualTo(2), "前提: メンバーが1件できている");
            ToolSession.CurrentEditId = edit.id;

            ToolPanelOverlay.DeleteEdit(component, component.FindEdit(edit.id));

            Assert.That(component.edits, Is.Empty);
            Assert.That(ToolSession.CurrentEditId, Is.Null);
        }
    }
}
