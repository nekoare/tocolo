using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.NDMF;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// NDMF プレビューのうち、ComputeContext を使わない部分（編集のハッシュ・Renderer の抽出・利用者の一覧）のテスト
    /// </summary>
    public class RecolorPreviewTests
    {
        private readonly List<Object> _cleanup = new List<Object>();

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

        private ClickRecolor MakeComponent(params RecolorEdit[] edits)
        {
            var go = Track(new GameObject("Avatar"));
            var component = go.AddComponent<ClickRecolor>();
            foreach (var edit in edits) component.AddEdit(edit);
            return component;
        }

        private static RecolorEdit MakeEdit(Color target, float strength = 1f)
        {
            var edit = RecolorEdit.CreateNew();
            edit.targetColor = target;
            edit.hasTarget = true;
            edit.strength = strength;
            return edit;
        }

        private Material MakeMaterial(Texture2D mainTex)
        {
            var material = Track(new Material(Shader.Find("Standard")));
            if (mainTex != null) material.SetTexture("_MainTex", mainTex);
            return material;
        }

        private MeshRenderer MakeRenderer(string name, Mesh mesh, params Material[] materials)
        {
            var go = Track(new GameObject(name));
            if (mesh != null) go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = materials;
            return renderer;
        }

        private Mesh MakeMesh(int submeshes)
        {
            var mesh = Track(new Mesh());
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up };
            mesh.subMeshCount = submeshes;
            for (int i = 0; i < submeshes; i++) mesh.SetTriangles(new[] { 0, 1, 2 }, i);
            return mesh;
        }

        // ── ComputeEditsHash ──

        [Test]
        public void 編集の内容が変わるとハッシュが変わる()
        {
            var component = MakeComponent(MakeEdit(Color.red), MakeEdit(Color.blue));
            int before = RecolorPreview.ComputeEditsHash(component);

            component.edits[1].strength = 0.5f;

            Assert.That(RecolorPreview.ComputeEditsHash(component), Is.Not.EqualTo(before));
        }

        [Test]
        public void 同じ内容ならハッシュは同じ()
        {
            var component = MakeComponent(MakeEdit(Color.red), MakeEdit(Color.blue));

            Assert.That(RecolorPreview.ComputeEditsHash(component), Is.EqualTo(RecolorPreview.ComputeEditsHash(component)));
        }

        [Test]
        public void 編集の順序を入れ替えるとハッシュが変わる()
        {
            var component = MakeComponent(MakeEdit(Color.red), MakeEdit(Color.blue));
            int before = RecolorPreview.ComputeEditsHash(component);

            (component.edits[0], component.edits[1]) = (component.edits[1], component.edits[0]);

            Assert.That(RecolorPreview.ComputeEditsHash(component), Is.Not.EqualTo(before));
        }

        [Test]
        public void hasTarget_を反転するとハッシュが変わる()
        {
            var component = MakeComponent(MakeEdit(Color.red));
            int before = RecolorPreview.ComputeEditsHash(component);

            component.edits[0].hasTarget = false;

            Assert.That(RecolorPreview.ComputeEditsHash(component), Is.Not.EqualTo(before));
        }

        [Test]
        public void プレビューの解像度と有効切り替えでハッシュが変わる()
        {
            var component = MakeComponent(MakeEdit(Color.red));
            int initial = RecolorPreview.ComputeEditsHash(component);

            component.previewResolution = WorkingResolution.R1024;
            int afterResolution = RecolorPreview.ComputeEditsHash(component);
            component.previewEnabled = false;
            int afterEnabled = RecolorPreview.ComputeEditsHash(component);

            Assert.That(afterResolution, Is.Not.EqualTo(initial));
            Assert.That(afterEnabled, Is.Not.EqualTo(afterResolution));
        }

        /// <summary>
        /// 重ね貼りできるシェーダー（lilToon か Poiyomi Toon）のマテリアルを持つ Renderer を子に置いたアバターに、画像入りの編集を 1 つ足す
        /// （そのシェーダーが無い環境では Ignore）
        /// </summary>
        private (ClickRecolor component, RecolorEdit edit) MakeDecalAvatar(bool smooth, bool poiyomi = false)
        {
            Shader shader;
            if (poiyomi)
            {
                shader = Shader.Find(Editor.Decal.DecalOverlayPoiyomi.ShaderName);
                if (shader == null) Assert.Ignore("Poiyomi Toon が入っていない環境です");
            }
            else
            {
                shader = Shader.Find("lilToon");
                if (shader == null) Assert.Ignore("lilToon が入っていない環境です");
                if (!Editor.Decal.DecalOverlayMaterial.IsApiAvailable) Assert.Ignore("lilToon のエディタ API が見つからない環境です");
            }
            var texture = Track(new Texture2D(4, 4));
            var material = Track(new Material(shader));
            material.SetTexture("_MainTex", texture);
            var renderer = MakeRenderer("Body", MakeMesh(1), material);
            var edit = MakeEdit(Color.red);
            edit.sourceTexture = texture;
            edit.decalEnabled = true;
            edit.decalTexture = Track(new Texture2D(4, 4));
            edit.decalSmooth = smooth;
            var component = MakeComponent(edit);
            renderer.transform.SetParent(component.transform, false);
            return (component, edit);
        }

        [Test]
        public void 重ね貼りで表示する編集は色や画像の箱を変えてもハッシュが変わらず_範囲を変えると変わる()
        {
            var (component, edit) = MakeDecalAvatar(smooth: true);
            Assert.That(Editor.Decal.DecalOverlayMaterial.UseOverlay(component, edit), Is.True, "前提: lilToon なので重ね貼り");
            int before = RecolorPreview.ComputeEditsHash(component);

            edit.targetColor = Color.blue;
            edit.strength = 0.5f;
            edit.decalBoxPosition = new Vector3(0.1f, 0f, 0f);
            edit.decalKeepAspect = !edit.decalKeepAspect;
            Assert.That(RecolorPreview.ComputeEditsHash(component), Is.EqualTo(before), "重ね貼りの色・画像の箱はこのフィルタの結果を変えないので作り直さない");

            edit.padding += 2;
            Assert.That(RecolorPreview.ComputeEditsHash(component), Is.Not.EqualTo(before), "範囲はハイライトに効くので畳む");
        }

        [Test]
        public void Poiyomiで重ね貼りする編集も色を変えてもハッシュが変わらない()
        {
            var (component, edit) = MakeDecalAvatar(smooth: true, poiyomi: true);
            Assert.That(Editor.Decal.DecalOverlayMaterial.UseOverlay(component, edit), Is.True, "前提: ロックしていない Poiyomi Toon なので重ね貼り");
            int before = RecolorPreview.ComputeEditsHash(component);

            edit.targetColor = Color.blue;
            edit.strength = 0.5f;

            Assert.That(RecolorPreview.ComputeEditsHash(component), Is.EqualTo(before));
        }

        [Test]
        public void 焼き込みで貼る編集は色を変えるとハッシュが変わる()
        {
            var (component, edit) = MakeDecalAvatar(smooth: false);
            int before = RecolorPreview.ComputeEditsHash(component);

            edit.targetColor = Color.blue;

            Assert.That(RecolorPreview.ComputeEditsHash(component), Is.Not.EqualTo(before));
        }

        // ── ComputeTextureHash ──

        [Test]
        public void 色未設定の編集の変更は結果のハッシュを変えない()
        {
            var targeted = MakeEdit(Color.red);
            var untargeted = MakeEdit(Color.blue);
            untargeted.hasTarget = false;
            var edits = new List<RecolorEdit> { targeted, untargeted };
            var users = new List<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)>();
            int before = RecolorPreview.ComputeTextureHash(edits, users);

            // 色未設定の編集のしきい値をドラッグしても、結果 RT は作り直さない
            untargeted.threshold = 0.9f;
            Assert.That(RecolorPreview.ComputeTextureHash(edits, users), Is.EqualTo(before));

            // 色を決めた編集の変更は結果を変える（上の等しさが「何を変えても同じ」ではないことの確認）
            targeted.threshold = 0.9f;
            Assert.That(RecolorPreview.ComputeTextureHash(edits, users), Is.Not.EqualTo(before));
        }

        [Test]
        public void 画像の箱を動かすとテクスチャのハッシュが変わる()
        {
            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = Texture2D.whiteTexture;
            edit.decalEnabled = true;
            edit.decalTexture = Texture2D.blackTexture;
            var users = new List<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)>();
            int before = RecolorPreview.ComputeTextureHash(new List<RecolorEdit> { edit }, users);
            edit.decalBoxPosition += Vector3.up;
            int after = RecolorPreview.ComputeTextureHash(new List<RecolorEdit> { edit }, users);
            Assert.That(after, Is.Not.EqualTo(before));
        }

        // ── IsPreviewTarget（ハイライト込み）──

        [Test]
        public void ハイライト中の色未設定の編集はプレビューの対象になる()
        {
            var edit = MakeEdit(Color.red);
            edit.hasTarget = false;
            edit.sourceTexture = Track(new Texture2D(4, 4));

            Assert.That(RecolorPreview.IsPreviewTarget(edit), Is.False, "前提: 色未設定はビルドと同じ条件では対象外");
            Assert.That(RecolorPreview.IsPreviewTarget(edit, new HighlightState(edit.id, true)), Is.True);
            // ハイライトが無効・別の編集なら対象外のまま
            Assert.That(RecolorPreview.IsPreviewTarget(edit, new HighlightState(edit.id, false)), Is.False);
            Assert.That(RecolorPreview.IsPreviewTarget(edit, new HighlightState("other", true)), Is.False);
        }

        [Test]
        public void 色を決めた編集はハイライトに関係なくプレビューの対象()
        {
            var edit = MakeEdit(Color.red);
            edit.sourceTexture = Track(new Texture2D(4, 4));

            Assert.That(RecolorPreview.IsPreviewTarget(edit, default), Is.True);
        }

        [Test]
        public void 画像入りの編集は色が未決定でもプレビューの対象()
        {
            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = Texture2D.whiteTexture;
            edit.decalEnabled = true;
            edit.decalTexture = Texture2D.blackTexture;
            Assert.That(RecolorPreview.IsPreviewTarget(edit), Is.True);
            edit.decalTexture = null;
            Assert.That(RecolorPreview.IsPreviewTarget(edit), Is.False);
        }

        // ── CollectRenderers ──

        [Test]
        public void CollectRenderers_はメインテクスチャが一致する_Renderer_だけ拾う()
        {
            var target = Track(new Texture2D(4, 4));
            var other = Track(new Texture2D(4, 4));
            var matching = MakeRenderer("Matching", null, MakeMaterial(other), MakeMaterial(target));
            var otherTexture = MakeRenderer("OtherTexture", null, MakeMaterial(other));
            // メイン以外のスロット（ノーマルマップ）に同じテクスチャがあっても拾わない
            var normalOnly = MakeMaterial(null);
            normalOnly.SetTexture("_BumpMap", target);
            var notMain = MakeRenderer("NotMain", null, normalOnly);
            var empty = MakeRenderer("Empty", null, new Material[] { null });

            var result = RecolorPreview.CollectRenderers(
                new Renderer[] { matching, otherTexture, notMain, empty, null },
                new HashSet<Texture2D> { target });

            Assert.That(result, Is.EquivalentTo(new Renderer[] { matching }));
        }

        // ── CollectUsers ──

        [Test]
        public void CollectUsers_は一致するスロットの_メッシュとサブメッシュと_Tiling_Offset_を返す()
        {
            var target = Track(new Texture2D(4, 4));
            var other = Track(new Texture2D(4, 4));
            var mesh = MakeMesh(2);
            var tiled = MakeMaterial(target);
            tiled.SetTextureScale("_MainTex", new Vector2(2f, 3f));
            tiled.SetTextureOffset("_MainTex", new Vector2(0.25f, 0.5f));
            var renderer = MakeRenderer("Renderer", mesh, MakeMaterial(other), tiled);

            var users = RecolorPreview.CollectUsers(new Renderer[] { renderer }, target);

            Assert.That(users.Count, Is.EqualTo(1));
            Assert.That(users[0].Item1, Is.SameAs(mesh));
            Assert.That(users[0].Item2, Is.EqualTo(1));
            Assert.That(users[0].Item3, Is.EqualTo(new Vector2(2f, 3f)));
            Assert.That(users[0].Item4, Is.EqualTo(new Vector2(0.25f, 0.5f)));
        }

        [Test]
        public void CollectUsers_はサブメッシュより多い枠を最後のサブメッシュの利用者として扱う()
        {
            // [_FakeShadow, 髪] のように髪が余った枠にある付け方（Unity は余った枠で最後のサブメッシュを重ね描きする）
            var target = Track(new Texture2D(4, 4));
            var mesh = MakeMesh(1);
            var renderer = MakeRenderer("Renderer", mesh, MakeMaterial(null), MakeMaterial(target));

            var users = RecolorPreview.CollectUsers(new Renderer[] { renderer }, target);

            Assert.That(users.Count, Is.EqualTo(1));
            Assert.That(users[0].Item1, Is.SameAs(mesh));
            Assert.That(users[0].Item2, Is.EqualTo(0), "余った枠は最後のサブメッシュ（0）を描く");
        }

        [Test]
        public void CollectUsers_は_Tiling_が_0_0_なら_1_1_として扱う()
        {
            var target = Track(new Texture2D(4, 4));
            var mesh = MakeMesh(1);
            var material = MakeMaterial(target);
            material.SetTextureScale("_MainTex", Vector2.zero);
            material.SetTextureOffset("_MainTex", new Vector2(0.5f, 0f));
            var renderer = MakeRenderer("Renderer", mesh, material);

            var users = RecolorPreview.CollectUsers(new Renderer[] { renderer }, target);

            Assert.That(users.Count, Is.EqualTo(1));
            Assert.That(users[0].Item3, Is.EqualTo(Vector2.one));
            Assert.That(users[0].Item4, Is.EqualTo(new Vector2(0.5f, 0f)));
        }
    }
}
