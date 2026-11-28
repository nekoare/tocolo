using System.IO;
using Nekoare.ClickRecolor.Editor.Copy;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>コピーの書き出し: 一覧に出す行、連結のまとめ方、画像を 1 つにまとめる、箱を髪基準にする</summary>
    public class CopyExporterTests
    {
        private CopyTestKit _kit;

        [SetUp]
        public void SetUp()
        {
            ToolSession.Clear();
            _kit = new CopyTestKit();
        }

        [TearDown]
        public void TearDown()
        {
            _kit.Dispose();
            ToolSession.Clear();
        }

        private (ClickRecolor component, MeshRenderer hair, MeshRenderer body, Texture2D hairTex, Texture2D bodyTex) MakeAvatar()
        {
            var hairTex = _kit.MakeTextureAsset(Color.red);
            var bodyTex = _kit.MakeTextureAsset(Color.blue);
            var root = _kit.MakeRoot("Avatar");
            var hair = _kit.AddRenderer(root, "Hair", _kit.MakeMesh("Hair"), hairTex);
            var body = _kit.AddRenderer(root, "Body", _kit.MakeMesh("Body"), bodyTex);
            return (root.AddComponent<ClickRecolor>(), hair, body, hairTex, bodyTex);
        }

        [Test]
        public void アバター全体の連結は先頭の1行だけ一覧に出て1件だけ書き出す()
        {
            var (component, hair, _, hairTex, _) = MakeAvatar();
            var head = component.AddEdit(CopyTestKit.MakeEdit(hair, hairTex));
            head.hasSeedOklab = true;
            head.seedOklab = new Vector3(0.5f, 0.1f, 0.1f);
            EditGroups.Link(component, head);
            Assert.AreEqual(2, component.edits.Count, "前提: 体のテクスチャのメンバーが足される");

            var rows = CopyExporter.ListRows(component);
            var file = CopyExporter.Build(component, rows);

            Assert.AreEqual(1, rows.Count);
            Assert.AreSame(head, rows[0]);
            Assert.AreEqual(1, file.edits.Count);
            Assert.IsTrue(file.edits[0].wasGroupHead);
        }

        [Test]
        public void 島の連結は1行に出てメンバー全員を書き出す()
        {
            var (component, hair, body, hairTex, bodyTex) = MakeAvatar();
            var head = component.AddEdit(CopyTestKit.MakeEdit(hair, hairTex));
            EditGroups.LinkSeedBased(component, head, bodyTex, body, 0, 1, new Vector2(0.1f, 0.1f));

            var rows = CopyExporter.ListRows(component);
            var file = CopyExporter.Build(component, rows);

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual(2, file.edits.Count);
            Assert.IsNotEmpty(file.edits[0].islandGroupKey);
            Assert.AreEqual(file.edits[0].islandGroupKey, file.edits[1].islandGroupKey);
            Assert.AreEqual("Body", file.edits[1].seed.meshName);
        }

        [Test]
        public void 色を決めていない仮の編集は一覧に出ない()
        {
            var (component, hair, _, hairTex, _) = MakeAvatar();
            var edit = component.AddEdit(CopyTestKit.MakeEdit(hair, hairTex));
            edit.hasTarget = false;
            edit.confirmed = false;

            CollectionAssert.IsEmpty(CopyExporter.ListRows(component));
        }

        [Test]
        public void 見つからない編集は書き出さない()
        {
            var (component, hair, _, hairTex, _) = MakeAvatar();
            var edit = component.AddEdit(CopyTestKit.MakeEdit(hair, hairTex));
            edit.seedRenderer = null;

            var file = CopyExporter.Build(component, new[] { edit });

            CollectionAssert.IsEmpty(file.edits);
        }

        [Test]
        public void 同じ画像を使う2つの編集でも画像は1つだけ入る()
        {
            var (component, hair, body, hairTex, bodyTex) = MakeAvatar();
            var image = WritePng("Sticker", Color.green);
            var a = component.AddEdit(CopyTestKit.MakeEdit(hair, hairTex, name: "a"));
            var b = component.AddEdit(CopyTestKit.MakeEdit(body, bodyTex, name: "b"));
            a.decalEnabled = b.decalEnabled = true;
            a.decalTexture = b.decalTexture = image;

            var file = CopyExporter.Build(component, new[] { a, b });

            Assert.AreEqual(1, file.images.Count);
            Assert.AreEqual("png", file.images[0].extension);
            Assert.AreEqual(file.images[0].key, file.edits[0].imageKey);
            Assert.AreEqual(file.images[0].key, file.edits[1].imageKey);
            CollectionAssert.AreEqual(File.ReadAllBytes(AssetDatabase.GetAssetPath(image)), System.Convert.FromBase64String(file.images[0].base64));
        }

        [Test]
        public void 箱は髪基準の値で入る()
        {
            var (component, hair, _, hairTex, _) = MakeAvatar();
            hair.transform.localPosition = new Vector3(0, 1, 0);
            var edit = component.AddEdit(CopyTestKit.MakeEdit(hair, hairTex));
            edit.boxPosition = new Vector3(0, 1.2f, 0);
            edit.gradientBoxPosition = new Vector3(0, 1.5f, 0);
            edit.decalBoxPosition = new Vector3(0, 0.9f, 0);
            edit.gradientTipTop = new Vector3(0, 1.3f, 0);

            var entry = CopyExporter.Build(component, new[] { edit }).edits[0];

            Assert.That(Vector3.Distance(entry.boxPosition, new Vector3(0, 0.2f, 0)), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(entry.gradientBoxPosition, new Vector3(0, 0.5f, 0)), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(entry.decalBoxPosition, new Vector3(0, -0.1f, 0)), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(entry.gradientTipTop, new Vector3(0, 0.3f, 0)), Is.LessThan(1e-4f));
        }

        [Test]
        public void 画像を入れられなかった編集は名前で分かる()
        {
            var (component, hair, _, hairTex, _) = MakeAvatar();
            var edit = component.AddEdit(CopyTestKit.MakeEdit(hair, hairTex, name: "シール"));
            edit.decalEnabled = true;
            edit.decalTexture = _kit.Track(new Texture2D(4, 4));
            var missing = new System.Collections.Generic.List<string>();

            var file = CopyExporter.Build(component, new[] { edit }, missing);

            Assert.AreEqual(1, file.edits.Count, "編集そのものは書き出す");
            CollectionAssert.IsEmpty(file.images);
            CollectionAssert.AreEqual(new[] { "シール" }, missing);
        }

        [Test]
        public void アバターのマテリアルのテクスチャを画像に使った編集は分かる()
        {
            var (component, hair, body, hairTex, bodyTex) = MakeAvatar();
            var edit = component.AddEdit(CopyTestKit.MakeEdit(hair, hairTex));
            edit.decalEnabled = true;
            edit.decalTexture = bodyTex;

            var found = CopyExporter.AvatarTexturesIn(component, edit, CopyExporter.CollectAvatarTextures(component));

            CollectionAssert.AreEqual(new[] { bodyTex }, found);
        }

        [Test]
        public void 連結のメンバーの画像も見る()
        {
            var (component, hair, body, hairTex, bodyTex) = MakeAvatar();
            var head = component.AddEdit(CopyTestKit.MakeEdit(hair, hairTex));
            var member = EditGroups.LinkSeedBased(component, head, bodyTex, body, 0, 1, new Vector2(0.1f, 0.1f));
            member.decalEnabled = true;
            member.decalTexture = hairTex;

            var found = CopyExporter.AvatarTexturesIn(component, head, CopyExporter.CollectAvatarTextures(component));

            CollectionAssert.AreEqual(new[] { hairTex }, found);
        }

        [Test]
        public void アバターで使っていない画像は知らせない()
        {
            var (component, hair, _, hairTex, _) = MakeAvatar();
            var edit = component.AddEdit(CopyTestKit.MakeEdit(hair, hairTex));
            edit.decalEnabled = true;
            edit.decalTexture = WritePng("Own", Color.green);

            CollectionAssert.IsEmpty(CopyExporter.AvatarTexturesIn(component, edit, CopyExporter.CollectAvatarTextures(component)));
        }

        private Texture2D WritePng(string name, Color color)
        {
            if (!AssetDatabase.IsValidFolder(CopyTestKit.Folder)) AssetDatabase.CreateFolder("Assets", "__TocoloCopyTests");
            var tex = new Texture2D(4, 4);
            var pixels = new Color[16];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            tex.SetPixels(pixels);
            tex.Apply();
            string path = $"{CopyTestKit.Folder}/{name}.png";
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
