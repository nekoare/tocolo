using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 連結のメンバーが元の編集の設定を全部写していること。フィールドを足して写し忘れると、連結の中でそのメンバーだけ設定が既定に戻る
    /// （Inspector は元の編集の値を出すので気づけない）
    /// </summary>
    public class EditGroupsCopyTests
    {
        /// <summary>メンバーごとに違ってよいフィールド（自分の種・テクスチャ・id と、島の連結で写さないと決めているもの）</summary>
        private static readonly HashSet<string> PerMember = new HashSet<string>
        {
            nameof(RecolorEdit.id),
            nameof(RecolorEdit.sourceTexture),
            nameof(RecolorEdit.seedRenderer),
            nameof(RecolorEdit.seedSubmesh),
            nameof(RecolorEdit.seedTriangle),
            nameof(RecolorEdit.seedUv),
            nameof(RecolorEdit.seedOklab),
            nameof(RecolorEdit.hasSeedOklab),
            nameof(RecolorEdit.extraSeeds),
            nameof(RecolorEdit.wholeTexture),
            // なぞって調整はメンバーのテクスチャの UV の画像なので、別のテクスチャへは写さない
            nameof(RecolorEdit.paintAdjustData),
            nameof(RecolorEdit.paintAdjustWidth),
            nameof(RecolorEdit.paintAdjustHeight),
            nameof(RecolorEdit.paintAdjustRevision),
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

        [Test]
        public void 島の連結に足したメンバーは種以外の設定を全部写す()
        {
            var root = new GameObject("EditGroupsCopyTests");
            _created.Add(root);
            var component = root.AddComponent<ClickRecolor>();
            var anchor = RecolorEdit.CreateNew();
            var defaults = RecolorEdit.CreateNew();
            // 既定と違う値にしておく（既定のままだと写し忘れても一致してしまう）
            foreach (var field in FieldMutator.SerializedFields(typeof(RecolorEdit)))
            {
                if (PerMember.Contains(field.Name)) continue;
                var mutated = _mutator.Mutate(field.GetValue(defaults), field.FieldType);
                Assert.That(mutated, Is.Not.Null, $"{field.Name}: 知らない型（FieldMutator に足す）");
                field.SetValue(anchor, mutated);
            }
            anchor.hasSeedOklab = false; // 共有の種色を持つ編集は島の連結にできない
            component.AddEdit(anchor);

            var member = EditGroups.LinkSeedBased(component, anchor, (Texture2D)_mutator.Mutate(null, typeof(Texture2D)), _mutator.NewRenderer(), 0, 0, Vector2.zero);

            Assert.That(member, Is.Not.Null);
            var failures = new List<string>();
            foreach (var field in FieldMutator.SerializedFields(typeof(RecolorEdit)))
            {
                if (PerMember.Contains(field.Name)) continue;
                if (!Equals(field.GetValue(member), field.GetValue(anchor))) failures.Add($"{field.Name}: 写していない（EditGroups.CopyForTexture に足す）");
            }
            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }
    }
}
