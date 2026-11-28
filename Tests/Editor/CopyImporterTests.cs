using System.IO;
using System.Linq;
using Nekoare.ClickRecolor.Editor.Copy;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>コピーの読み込み: 別アバターへ移る・移せない編集を知らせる・上書き／足す・Undo・連結の作り直し・画像</summary>
    public class CopyImporterTests
    {
        private CopyTestKit _kit;
        private string _savedImageFolder;

        [SetUp]
        public void SetUp()
        {
            ToolSession.Clear();
            Undo.IncrementCurrentGroup();
            _kit = new CopyTestKit();
            _savedImageFolder = CopiedImageStore.Folder;
            CopiedImageStore.Folder = CopyTestKit.CopiedImageStoreFolderForTests;
        }

        [TearDown]
        public void TearDown()
        {
            CopiedImageStore.Folder = _savedImageFolder;
            _kit.Dispose();
            ToolSession.Clear();
        }

        /// <summary>髪・体を持つアバター。meshes を渡すと同じメッシュ資産を使う（同じ髪・同じ素体）</summary>
        private (GameObject root, MeshRenderer hair, MeshRenderer body) MakeAvatar(string name, Mesh hairMesh, Mesh bodyMesh, Texture2D hairTex, Texture2D bodyTex)
        {
            var root = _kit.MakeRoot(name);
            var hair = _kit.AddRenderer(root, "Hair", hairMesh, hairTex);
            var body = _kit.AddRenderer(root, "Body", bodyMesh, bodyTex);
            return (root, hair, body);
        }

        private static CopyFile Export(ClickRecolor component) => CopyExporter.Build(component, CopyExporter.ListRows(component));

        [Test]
        public void 同じ形の別アバターへ編集が移る()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var (a, hairA, _) = MakeAvatar("A", hairMesh, _kit.MakeMesh("BodyA"), tex, _kit.MakeTextureAsset(Color.gray));
            var (b, hairB, _) = MakeAvatar("B", hairMesh, _kit.MakeMesh("BodyB"), tex, _kit.MakeTextureAsset(Color.gray));
            var source = a.AddComponent<ClickRecolor>();
            var original = source.AddEdit(CopyTestKit.MakeEdit(hairA, tex, triangle: 2, name: "髪"));

            var result = CopyImporter.Apply(b, Export(source), ImportMode.Add);

            var target = b.GetComponent<ClickRecolor>();
            Assert.IsNotNull(target, "Tocolo が付いていないアバターにも付ける");
            Assert.AreEqual(1, target.edits.Count);
            var moved = target.edits[0];
            Assert.AreSame(hairB, moved.seedRenderer);
            Assert.AreSame(tex, moved.sourceTexture);
            Assert.AreEqual(2, moved.seedTriangle);
            Assert.AreEqual(original.targetColor, moved.targetColor);
            Assert.AreEqual(original.threshold, moved.threshold);
            Assert.AreNotEqual(original.id, moved.id);
            CollectionAssert.IsEmpty(result.failed);
        }

        [Test]
        public void 色違いのテクスチャでも移る()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var red = _kit.MakeTextureAsset(Color.red);
            var blue = _kit.MakeTextureAsset(Color.blue);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var bodyMesh = _kit.MakeMesh("Body");
            var (a, hairA, _) = MakeAvatar("A", hairMesh, bodyMesh, red, gray);
            var (b, _, _) = MakeAvatar("B", hairMesh, bodyMesh, blue, gray);
            var source = a.AddComponent<ClickRecolor>();
            source.AddEdit(CopyTestKit.MakeEdit(hairA, red));

            CopyImporter.Apply(b, Export(source), ImportMode.Add);

            // AreSame（C# の参照の一致）にしない。同じテクスチャでも C# 側の入れ物が別に作られることがあり
            // （実体の ID は同じ）、参照では食い違う。Unity の == と同じく実体で比べる
            Assert.AreEqual(blue, b.GetComponent<ClickRecolor>().edits[0].sourceTexture);
        }

        [Test]
        public void 移せない編集は名前と理由が一覧に出て他は移る()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var (a, hairA, bodyA) = MakeAvatar("A", hairMesh, _kit.MakeMesh("BodyA"), tex, gray);
            var (b, _, _) = MakeAvatar("B", hairMesh, _kit.MakeMesh("BodyB"), tex, gray);
            var source = a.AddComponent<ClickRecolor>();
            source.AddEdit(CopyTestKit.MakeEdit(hairA, tex, name: "髪"));
            source.AddEdit(CopyTestKit.MakeEdit(bodyA, gray, name: "肌"));

            var result = CopyImporter.Apply(b, Export(source), ImportMode.Add);

            Assert.AreEqual(1, b.GetComponent<ClickRecolor>().edits.Count);
            Assert.AreEqual(1, result.failed.Count);
            Assert.AreEqual("肌", result.failed[0].name);
            Assert.AreEqual(AnchorFailure.MeshNotFound, result.failed[0].reason);
        }

        [Test]
        public void 足した種が一部見つからなければ残りで入り一部欠けとして出る()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var (a, hairA, bodyA) = MakeAvatar("A", hairMesh, _kit.MakeMesh("BodyA"), tex, gray);
            var (b, hairB, _) = MakeAvatar("B", hairMesh, _kit.MakeMesh("BodyB"), tex, gray);
            var source = a.AddComponent<ClickRecolor>();
            var edit = source.AddEdit(CopyTestKit.MakeEdit(hairA, tex, name: "髪"));
            edit.extraSeeds.Add(new RecolorSeed { renderer = hairA, submesh = 0, triangle = 3, uv = new Vector2(0.7f, 0.7f) });
            edit.extraSeeds.Add(new RecolorSeed { renderer = bodyA, submesh = 0, triangle = 1, uv = Vector2.zero });

            var result = CopyImporter.Apply(b, Export(source), ImportMode.Add);

            var moved = b.GetComponent<ClickRecolor>().edits.Single();
            Assert.AreEqual(1, moved.extraSeeds.Count);
            Assert.AreSame(hairB, moved.extraSeeds[0].renderer);
            Assert.AreEqual(3, moved.extraSeeds[0].triangle);
            CollectionAssert.AreEqual(new[] { "髪" }, result.partial);
        }

        [Test]
        public void 上書きでは入ったテクスチャの編集だけ置き換わり他のテクスチャの編集は残る()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var bodyMesh = _kit.MakeMesh("Body");
            var tex = _kit.MakeTextureAsset(Color.red);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var (a, hairA, _) = MakeAvatar("A", hairMesh, bodyMesh, tex, gray);
            var (b, hairB, bodyB) = MakeAvatar("B", hairMesh, bodyMesh, tex, gray);
            var source = a.AddComponent<ClickRecolor>();
            source.AddEdit(CopyTestKit.MakeEdit(hairA, tex, name: "新しい髪"));
            var target = b.AddComponent<ClickRecolor>();
            target.AddEdit(CopyTestKit.MakeEdit(hairB, tex, name: "古い髪"));
            target.AddEdit(CopyTestKit.MakeEdit(bodyB, gray, name: "肌"));

            CopyImporter.Apply(b, Export(source), ImportMode.ReplaceSameTexture);

            CollectionAssert.AreEquivalent(new[] { "肌", "新しい髪" }, target.edits.Select(e => e.name));
        }

        [Test]
        public void 足すでは既存の編集の後ろに足される()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var (a, hairA, _) = MakeAvatar("A", hairMesh, _kit.MakeMesh("Body"), tex, gray);
            var (b, hairB, _) = MakeAvatar("B", hairMesh, _kit.MakeMesh("Body2"), tex, gray);
            var source = a.AddComponent<ClickRecolor>();
            source.AddEdit(CopyTestKit.MakeEdit(hairA, tex, name: "新しい髪"));
            var target = b.AddComponent<ClickRecolor>();
            target.AddEdit(CopyTestKit.MakeEdit(hairB, tex, name: "古い髪"));

            CopyImporter.Apply(b, Export(source), ImportMode.Add);

            CollectionAssert.AreEqual(new[] { "古い髪", "新しい髪" }, target.edits.Select(e => e.name));
        }

        [Test]
        public void 読み込み1回はUndo1回で元に戻る()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var (a, hairA, _) = MakeAvatar("A", hairMesh, _kit.MakeMesh("Body"), tex, gray);
            var (b, hairB, _) = MakeAvatar("B", hairMesh, _kit.MakeMesh("Body2"), tex, gray);
            var source = a.AddComponent<ClickRecolor>();
            source.AddEdit(CopyTestKit.MakeEdit(hairA, tex, name: "新しい髪"));
            source.AddEdit(CopyTestKit.MakeEdit(hairA, tex, triangle: 1, name: "新しい髪2"));
            var target = b.AddComponent<ClickRecolor>();
            target.AddEdit(CopyTestKit.MakeEdit(hairB, tex, name: "古い髪"));
            Undo.IncrementCurrentGroup();

            CopyImporter.Apply(b, Export(source), ImportMode.ReplaceSameTexture);
            Assert.AreEqual(2, target.edits.Count, "前提");
            Undo.PerformUndo();

            CollectionAssert.AreEqual(new[] { "古い髪" }, target.edits.Select(e => e.name));
        }

        [Test]
        public void アバター全体の連結は移す先で作り直される()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var grayA = _kit.MakeTextureAsset(Color.gray);
            var grayB = _kit.MakeTextureAsset(Color.white);
            var (a, hairA, _) = MakeAvatar("A", hairMesh, _kit.MakeMesh("BodyA"), tex, grayA);
            var (b, hairB, _) = MakeAvatar("B", hairMesh, _kit.MakeMesh("BodyB"), tex, grayB);
            var source = a.AddComponent<ClickRecolor>();
            var head = source.AddEdit(CopyTestKit.MakeEdit(hairA, tex, name: "全体"));
            head.hasSeedOklab = true;
            head.seedOklab = new Vector3(0.6f, 0.1f, 0f);
            EditGroups.Link(source, head);

            var result = CopyImporter.Apply(b, Export(source), ImportMode.Add);

            var target = b.GetComponent<ClickRecolor>();
            Assert.AreEqual(2, target.edits.Count);
            Assert.AreSame(hairB, target.edits[0].seedRenderer);
            Assert.AreEqual(target.edits[0].groupId, target.edits[1].groupId);
            Assert.AreSame(grayB, target.edits[1].sourceTexture, "メンバーは移す先のテクスチャで作り直す");
            Assert.AreNotEqual(head.groupId, target.edits[0].groupId);
            CollectionAssert.AreEqual(new[] { "全体" }, result.regrouped);
        }

        [Test]
        public void 島の連結は移せたメンバーどうしでつなぎ直す()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var bodyMesh = _kit.MakeMesh("Body");
            var tex = _kit.MakeTextureAsset(Color.red);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var (a, hairA, bodyA) = MakeAvatar("A", hairMesh, bodyMesh, tex, gray);
            var (b, _, _) = MakeAvatar("B", hairMesh, bodyMesh, tex, gray);
            var source = a.AddComponent<ClickRecolor>();
            var head = source.AddEdit(CopyTestKit.MakeEdit(hairA, tex, name: "島"));
            EditGroups.LinkSeedBased(source, head, gray, bodyA, 0, 1, new Vector2(0.1f, 0.1f));

            CopyImporter.Apply(b, Export(source), ImportMode.Add);

            var target = b.GetComponent<ClickRecolor>();
            Assert.AreEqual(2, target.edits.Count);
            Assert.IsNotEmpty(target.edits[0].groupId);
            Assert.AreEqual(target.edits[0].groupId, target.edits[1].groupId);
            Assert.AreNotEqual(head.groupId, target.edits[0].groupId);
        }

        [Test]
        public void 同じ画像を2回読み込んでも画像ファイルは1つ()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var (a, hairA, _) = MakeAvatar("A", hairMesh, _kit.MakeMesh("Body"), tex, gray);
            var (b, _, _) = MakeAvatar("B", hairMesh, _kit.MakeMesh("Body2"), tex, gray);
            var source = a.AddComponent<ClickRecolor>();
            var edit = source.AddEdit(CopyTestKit.MakeEdit(hairA, tex));
            edit.decalEnabled = true;
            edit.decalTexture = WritePng("Sticker", Color.green);
            var file = Export(source);

            CopyImporter.Apply(b, file, ImportMode.Add);
            CopyImporter.Apply(b, file, ImportMode.Add);

            var edits = b.GetComponent<ClickRecolor>().edits;
            Assert.AreEqual(2, edits.Count);
            Assert.IsNotNull(edits[0].decalTexture);
            Assert.AreSame(edits[0].decalTexture, edits[1].decalTexture);
            Assert.AreNotSame(edit.decalTexture, edits[0].decalTexture, "移す先に作った画像を使う");
            Assert.AreEqual(1, AssetDatabase.FindAssets("t:Texture2D", new[] { CopiedImageStore.Folder }).Length);
        }

        [Test]
        public void 同じセッションで読み込んでも元のアバターを指さない()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var (a, hairA, _) = MakeAvatar("A", hairMesh, _kit.MakeMesh("Body"), tex, gray);
            var (b, _, _) = MakeAvatar("B", hairMesh, _kit.MakeMesh("Body2"), tex, gray);
            var source = a.AddComponent<ClickRecolor>();
            var edit = source.AddEdit(CopyTestKit.MakeEdit(hairA, tex));
            edit.matchEnabled = true;
            edit.matchRenderer = hairA;
            edit.matchProfileData = "profile";
            edit.extraSeeds.Add(new RecolorSeed { renderer = hairA, triangle = 1 });

            CopyImporter.Apply(b, Export(source), ImportMode.Add);

            var moved = b.GetComponent<ClickRecolor>().edits.Single();
            var renderersInA = a.GetComponentsInChildren<Renderer>(true);
            CollectionAssert.DoesNotContain(renderersInA, moved.seedRenderer);
            CollectionAssert.DoesNotContain(renderersInA, moved.extraSeeds[0].renderer);
            Assert.IsNull(moved.matchRenderer);
            Assert.AreEqual("profile", moved.matchProfileData, "色合わせの山の形は運ぶ");
        }

        [Test]
        public void 移す先で髪の位置が違えば箱も付いてくる()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var (a, hairA, _) = MakeAvatar("A", hairMesh, _kit.MakeMesh("Body"), tex, gray);
            var (b, hairB, _) = MakeAvatar("B", hairMesh, _kit.MakeMesh("Body2"), tex, gray);
            hairA.transform.localPosition = new Vector3(0, 1.5f, 0);
            hairB.transform.localPosition = new Vector3(0, 1.6f, 0);
            var source = a.AddComponent<ClickRecolor>();
            var edit = source.AddEdit(CopyTestKit.MakeEdit(hairA, tex));
            edit.gradientEnabled = true;
            edit.gradientBoxPosition = new Vector3(0, 1.4f, 0);

            CopyImporter.Apply(b, Export(source), ImportMode.Add);

            var moved = b.GetComponent<ClickRecolor>().edits.Single();
            Assert.That(Vector3.Distance(moved.gradientBoxPosition, new Vector3(0, 1.5f, 0)), Is.LessThan(1e-4f));
        }

        [Test]
        public void 画像でない拡張子のファイルはプロジェクトに作らない()
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("class X {}");
            var image = new CopyImage { key = CopyExporter.Hash(bytes), name = "Evil", extension = "cs", base64 = System.Convert.ToBase64String(bytes) };

            Assert.IsNull(CopiedImageStore.GetOrCreate(image));
            Assert.IsFalse(AssetDatabase.IsValidFolder(CopiedImageStore.Folder) && Directory.GetFiles(CopiedImageStore.Folder, "*.cs").Length > 0);
        }

        [Test]
        public void 中身とハッシュが合わない画像は作らない()
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("not a png");
            var image = new CopyImage { key = "../../Editor/x", name = "Evil", extension = "png", base64 = System.Convert.ToBase64String(bytes) };

            Assert.IsNull(CopiedImageStore.GetOrCreate(image));
        }

        [Test]
        public void 色違いへ移したアバター全体の連結は移す先の色で選び直す()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var red = _kit.MakeTextureAsset(Color.red);
            var blue = _kit.MakeTextureAsset(Color.blue);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var bodyMesh = _kit.MakeMesh("Body");
            var (a, hairA, _) = MakeAvatar("A", hairMesh, bodyMesh, red, gray);
            var (b, _, _) = MakeAvatar("B", hairMesh, bodyMesh, blue, gray);
            var source = a.AddComponent<ClickRecolor>();
            var head = source.AddEdit(CopyTestKit.MakeEdit(hairA, red, name: "全体"));
            head.hasSeedOklab = true;
            head.seedOklab = new Vector3(0.63f, 0.22f, 0.13f);
            EditGroups.Link(source, head);

            CopyImporter.Apply(b, Export(source), ImportMode.Add);

            var moved = b.GetComponent<ClickRecolor>().edits[0];
            Assert.IsTrue(moved.hasSeedOklab);
            Assert.That(Vector3.Distance(moved.seedOklab, head.seedOklab), Is.GreaterThan(0.05f), "元の赤のまま");
        }

        [Test]
        public void 上書きでアバター全体を読み込んでも主のテクスチャ以外の編集は残る()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var bodyMesh = _kit.MakeMesh("Body");
            var tex = _kit.MakeTextureAsset(Color.red);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var (a, hairA, _) = MakeAvatar("A", hairMesh, bodyMesh, tex, gray);
            var (b, hairB, bodyB) = MakeAvatar("B", hairMesh, bodyMesh, tex, gray);
            var source = a.AddComponent<ClickRecolor>();
            var head = source.AddEdit(CopyTestKit.MakeEdit(hairA, tex, name: "全体"));
            head.hasSeedOklab = true;
            head.seedOklab = new Vector3(0.6f, 0.1f, 0f);
            EditGroups.Link(source, head);
            var target = b.AddComponent<ClickRecolor>();
            target.AddEdit(CopyTestKit.MakeEdit(hairB, tex, name: "古い髪"));
            target.AddEdit(CopyTestKit.MakeEdit(bodyB, gray, name: "肌"));

            CopyImporter.Apply(b, Export(source), ImportMode.ReplaceSameTexture);

            CollectionAssert.Contains(target.edits.Select(e => e.name).ToList(), "肌");
            CollectionAssert.DoesNotContain(target.edits.Select(e => e.name).ToList(), "古い髪");
        }

        [Test]
        public void 島の連結は素体が違っても箱がそろう()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var bodyMesh = _kit.MakeMesh("Body");
            var tex = _kit.MakeTextureAsset(Color.red);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var (a, hairA, bodyA) = MakeAvatar("A", hairMesh, bodyMesh, tex, gray);
            var (b, hairB, bodyB) = MakeAvatar("B", hairMesh, bodyMesh, tex, gray);
            hairA.transform.localPosition = new Vector3(0, 1f, 0);
            hairB.transform.localPosition = new Vector3(0, 1.1f, 0);
            bodyB.transform.localPosition = new Vector3(0, -0.3f, 0);
            var source = a.AddComponent<ClickRecolor>();
            var head = source.AddEdit(CopyTestKit.MakeEdit(hairA, tex, name: "島"));
            head.gradientEnabled = true;
            head.gradientBoxPosition = new Vector3(0, 0.8f, 0);
            var member = EditGroups.LinkSeedBased(source, head, gray, bodyA, 0, 1, new Vector2(0.1f, 0.1f));
            member.gradientEnabled = true;
            member.gradientBoxPosition = head.gradientBoxPosition;

            CopyImporter.Apply(b, Export(source), ImportMode.Add);

            var edits = b.GetComponent<ClickRecolor>().edits;
            Assert.That(Vector3.Distance(edits[0].gradientBoxPosition, new Vector3(0, 0.9f, 0)), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(edits[1].gradientBoxPosition, edits[0].gradientBoxPosition), Is.LessThan(1e-4f));
        }

        [Test]
        public void 足した種が別のテクスチャに付くときは一部欠けにする()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var other = _kit.MakeTextureAsset(Color.green);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var bangMesh = _kit.MakeMesh("Bang");
            var (a, hairA, _) = MakeAvatar("A", hairMesh, _kit.MakeMesh("BodyA"), tex, gray);
            var bangA = _kit.AddRenderer(a, "Bang", bangMesh, tex);
            var (b, _, _) = MakeAvatar("B", hairMesh, _kit.MakeMesh("BodyB"), tex, gray);
            _kit.AddRenderer(b, "Bang", bangMesh, other);
            var source = a.AddComponent<ClickRecolor>();
            var edit = source.AddEdit(CopyTestKit.MakeEdit(hairA, tex, name: "髪"));
            edit.extraSeeds.Add(new RecolorSeed { renderer = bangA, submesh = 0, triangle = 1, uv = Vector2.zero });

            var result = CopyImporter.Apply(b, Export(source), ImportMode.Add);

            CollectionAssert.IsEmpty(b.GetComponent<ClickRecolor>().edits[0].extraSeeds);
            CollectionAssert.AreEqual(new[] { "髪" }, result.partial);
        }

        [Test]
        public void 画像の読み込み設定は種類と圧縮とプラットフォームごとの設定まで写る()
        {
            var hairMesh = _kit.MakeMesh("Hair");
            var tex = _kit.MakeTextureAsset(Color.red);
            var gray = _kit.MakeTextureAsset(Color.gray);
            var (a, hairA, _) = MakeAvatar("A", hairMesh, _kit.MakeMesh("Body"), tex, gray);
            var (b, _, _) = MakeAvatar("B", hairMesh, _kit.MakeMesh("Body2"), tex, gray);
            var png = WritePng("Sprite", Color.green);
            var srcImporter = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(png));
            srcImporter.textureType = TextureImporterType.Sprite;
            srcImporter.textureCompression = TextureImporterCompression.Uncompressed;
            var android = srcImporter.GetPlatformTextureSettings("Android");
            android.overridden = true;
            android.maxTextureSize = 256;
            android.format = TextureImporterFormat.ASTC_8x8;
            srcImporter.SetPlatformTextureSettings(android);
            srcImporter.SaveAndReimport();
            var source = a.AddComponent<ClickRecolor>();
            var edit = source.AddEdit(CopyTestKit.MakeEdit(hairA, tex));
            edit.decalEnabled = true;
            edit.decalTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GetAssetPath(png));

            CopyImporter.Apply(b, Export(source), ImportMode.Add);

            var moved = b.GetComponent<ClickRecolor>().edits.Single().decalTexture;
            var importer = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(moved));
            Assert.AreEqual(TextureImporterType.Sprite, importer.textureType);
            Assert.AreEqual(TextureImporterCompression.Uncompressed, importer.textureCompression);
            var movedAndroid = importer.GetPlatformTextureSettings("Android");
            Assert.IsTrue(movedAndroid.overridden);
            Assert.AreEqual(256, movedAndroid.maxTextureSize);
            Assert.AreEqual(TextureImporterFormat.ASTC_8x8, movedAndroid.format);
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
