using System.Collections.Generic;
using System.Linq;
using Nekoare.ClickRecolor.Editor.Copy;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>コピーのファイル（JSON）の読み書き。Unity の参照を運ばないこと、形式・版の違うファイルを読まないこと</summary>
    public class CopyFileTests
    {
        private CopyTestKit _kit;

        [SetUp] public void SetUp() => _kit = new CopyTestKit();
        [TearDown] public void TearDown() => _kit.Dispose();

        private static bool IsUnityObject(System.Type t) => typeof(Object).IsAssignableFrom(t);

        [Test]
        public void 書き出して読むと参照以外の項目が全部残る()
        {
            var mutator = new FieldMutator(o => _kit.Track(o));
            var original = RecolorEdit.CreateNew();
            foreach (var field in FieldMutator.SerializedFields(typeof(RecolorEdit)))
            {
                var mutated = mutator.Mutate(field.GetValue(original), field.FieldType);
                Assert.IsNotNull(mutated, $"FieldMutator が {field.FieldType} を知らない");
                field.SetValue(original, mutated);
            }

            var copy = CopyFileIO.EditFromJson(CopyFileIO.EditToJson(original));

            foreach (var field in FieldMutator.SerializedFields(typeof(RecolorEdit)))
            {
                if (field.Name == nameof(RecolorEdit.id) || IsUnityObject(field.FieldType)) continue;
                // 足した場所は CopyEntry.extraSeeds で運ぶ（下のテスト）
                if (field.Name == nameof(RecolorEdit.extraSeeds)) continue;
                var a = field.GetValue(original);
                var b = field.GetValue(copy);
                if (field.FieldType == typeof(List<RecolorGradientStop>))
                {
                    var ga = (List<RecolorGradientStop>)a; var gb = (List<RecolorGradientStop>)b;
                    Assert.AreEqual(ga.Count, gb.Count, field.Name);
                    for (int i = 0; i < ga.Count; i++) Assert.AreEqual(ga[i].color, gb[i].color);
                }
                else Assert.AreEqual(a, b, field.Name);
            }
            Assert.AreNotEqual(original.id, copy.id);
        }

        [Test]
        public void 読み込んだ編集はどのUnityの参照も持たない()
        {
            var tex = _kit.MakeTextureAsset(Color.red);
            var renderer = _kit.AddRenderer(_kit.MakeRoot("A"), "Hair", _kit.MakeMesh("Hair"), tex);
            var edit = CopyTestKit.MakeEdit(renderer, tex);
            edit.decalTexture = tex;
            edit.matchRenderer = renderer;

            var copy = CopyFileIO.EditFromJson(CopyFileIO.EditToJson(edit));

            Assert.IsNull(copy.sourceTexture);
            Assert.IsNull(copy.seedRenderer);
            Assert.IsNull(copy.decalTexture);
            Assert.IsNull(copy.matchRenderer);
        }

        [Test]
        public void 足した場所は編集の設定に二重に入れず元の編集も変えない()
        {
            var tex = _kit.MakeTextureAsset(Color.red);
            var renderer = _kit.AddRenderer(_kit.MakeRoot("A"), "Hair", _kit.MakeMesh("Hair"), tex);
            var edit = CopyTestKit.MakeEdit(renderer, tex);
            edit.extraSeeds.Add(new RecolorSeed { renderer = renderer, triangle = 1 });
            edit.extraSeeds.Add(new RecolorSeed { renderer = renderer, triangle = 2 });

            var json = CopyFileIO.EditToJson(edit);

            CollectionAssert.IsEmpty(JsonUtility.FromJson<RecolorEdit>(json).extraSeeds);
            Assert.AreEqual(2, edit.extraSeeds.Count);
            Assert.AreSame(renderer, edit.extraSeeds[0].renderer);
            CollectionAssert.IsEmpty(CopyFileIO.EditFromJson(json).extraSeeds);
        }

        [Test]
        public void ファイルは字下げせずに書く()
        {
            var file = new CopyFile();
            file.edits.Add(new CopyEntry { editJson = "{}" });

            StringAssert.DoesNotContain("\n", CopyFileIO.Write(file));
        }

        [Test]
        public void RecolorEditのUnity参照のフィールドが増えたら気づく()
        {
            var editRefs = FieldMutator.SerializedFields(typeof(RecolorEdit)).Where(f => IsUnityObject(f.FieldType)).Select(f => f.Name);
            var seedRefs = FieldMutator.SerializedFields(typeof(RecolorSeed)).Where(f => IsUnityObject(f.FieldType)).Select(f => f.Name);
            var stopRefs = FieldMutator.SerializedFields(typeof(RecolorGradientStop)).Where(f => IsUnityObject(f.FieldType)).Select(f => f.Name);

            CollectionAssert.AreEquivalent(CopyFileIO.EditObjectFieldNames, editRefs);
            CollectionAssert.AreEquivalent(CopyFileIO.SeedObjectFieldNames, seedRefs);
            CollectionAssert.IsEmpty(stopRefs);
        }

        [Test]
        public void ファイルを書いて読むと中身が戻る()
        {
            var file = new CopyFile();
            file.edits.Add(new CopyEntry { editJson = "{}", seed = new CopyAnchor { meshName = "Hair", triangle = 3 }, boxSize = Vector3.one * 2 });
            file.images.Add(new CopyImage { key = "abc", name = "img", extension = "png", base64 = "AAAA" });

            Assert.IsTrue(CopyFileIO.TryRead(CopyFileIO.Write(file), out var read, out var error), error);
            Assert.AreEqual("Hair", read.edits[0].seed.meshName);
            Assert.AreEqual(3, read.edits[0].seed.triangle);
            Assert.AreEqual(Vector3.one * 2, read.edits[0].boxSize);
            Assert.AreEqual("abc", read.images[0].key);
        }

        [Test]
        public void 形式の違うファイルは読まない()
        {
            Assert.IsFalse(CopyFileIO.TryRead("{\"format\":\"something\",\"version\":1}", out _, out _));
            Assert.IsFalse(CopyFileIO.TryRead("not json", out _, out _));
            Assert.IsFalse(CopyFileIO.TryRead("", out _, out _));
        }

        [Test]
        public void 先頭だけでコピーのファイルか分かる()
        {
            Assert.IsTrue(CopyFileIO.LooksLikeCopyFile(CopyFileIO.Write(new CopyFile()).Substring(0, 40)));
            Assert.IsFalse(CopyFileIO.LooksLikeCopyFile("{\"name\": \"package\"}"));
        }

        [Test]
        public void 新しい版のファイルは読まない()
        {
            var text = CopyFileIO.Write(new CopyFile { version = CopyFile.CurrentVersion + 1 });
            Assert.IsFalse(CopyFileIO.TryRead(text, out _, out var error));
            Assert.IsNotEmpty(error);
        }
    }
}
