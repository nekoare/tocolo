using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Pipeline;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 編集のハッシュ（EditHash）が編集の出力に関わる全フィールドを拾うこと。
    /// フィールドを足してハッシュに入れ忘れると、値を変えてもプレビューが作り直されない
    /// </summary>
    public class EditHashTests
    {
        /// <summary>出力を変えないのでハッシュに入れないフィールド（表示専用と状態の印）</summary>
        private static readonly HashSet<string> NotHashed = new HashSet<string>
        {
            nameof(RecolorEdit.name),
            nameof(RecolorEdit.seedColor),
            nameof(RecolorEdit.confirmed),
            nameof(RecolorEdit.gradientInitialized),
            nameof(RecolorEdit.decalInitialized),
        };

        private readonly List<Object> _created = new List<Object>();
        private FieldMutator _mutator;

        [SetUp]
        public void SetUp()
        {
            _mutator = new FieldMutator(o =>
            {
                _created.Add(o);
                return o;
            });
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created)
            {
                if (o != null) Object.DestroyImmediate(o);
            }
            _created.Clear();
        }

        /// <summary>条件付きで畳む区分（箱の中・グラデーション・画像・打ち消し）がすべて効く編集</summary>
        private RecolorEdit NewFullEdit()
        {
            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = (Texture2D)_mutator.Mutate(null, typeof(Texture2D));
            edit.seedRenderer = _mutator.NewRenderer();
            edit.mode = SelectionMode.Box;
            edit.gradientEnabled = true;
            edit.decalEnabled = true;
            edit.decalTexture = (Texture2D)_mutator.Mutate(null, typeof(Texture2D));
            edit.flattenBase = true;
            edit.hasTarget = true;
            edit.extraSeeds.Add(new RecolorSeed { renderer = _mutator.NewRenderer() });
            return edit;
        }

        [Test]
        public void 出力に関わるフィールドはどれを変えてもハッシュが変わる()
        {
            var failures = new List<string>();
            foreach (var field in FieldMutator.SerializedFields(typeof(RecolorEdit)))
            {
                if (NotHashed.Contains(field.Name)) continue;
                var edit = NewFullEdit();
                int before = EditHash.All(17, edit);
                var mutated = _mutator.Mutate(field.GetValue(edit), field.FieldType);
                if (mutated == null)
                {
                    failures.Add($"{field.Name}: 知らない型 {field.FieldType.Name}（FieldMutator に足すか NotHashed に入れる）");
                    continue;
                }
                field.SetValue(edit, mutated);
                if (EditHash.All(17, edit) == before) failures.Add($"{field.Name}: 変えてもハッシュが変わらない（EditHash のどれかの区分に入れる）");
            }
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        [Test]
        public void 追加の種のフィールドはどれを変えてもハッシュが変わる()
        {
            var failures = new List<string>();
            foreach (var field in FieldMutator.SerializedFields(typeof(RecolorSeed)))
            {
                var edit = NewFullEdit();
                var seed = edit.extraSeeds[0];
                int before = EditHash.All(17, edit);
                var mutated = _mutator.Mutate(field.GetValue(seed), field.FieldType);
                if (mutated == null)
                {
                    failures.Add($"{field.Name}: 知らない型 {field.FieldType.Name}");
                    continue;
                }
                field.SetValue(seed, mutated);
                if (EditHash.All(17, edit) == before) failures.Add($"{field.Name}: 変えてもハッシュが変わらない（RecolorPipeline.HashExtraSeeds に入れる）");
            }
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        [Test]
        public void 表示専用のフィールドを変えてもハッシュは変わらない()
        {
            var edit = NewFullEdit();
            int before = EditHash.All(17, edit);

            edit.name = "別の名前";
            edit.seedColor = Color.red;
            edit.confirmed = !edit.confirmed;

            Assert.That(EditHash.All(17, edit), Is.EqualTo(before));
        }
    }
}
