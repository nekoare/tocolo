using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Pipeline;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 選択マスクのキャッシュ（MaskCache）の上限・現在の編集の保持・書き出し由来の破棄のテスト。
    /// マスクは 1×1 の R8 のダミー（中身は見ない）
    /// </summary>
    public class MaskCacheTests
    {
        [SetUp]
        public void SetUp()
        {
            MaskCache.ClearCache();
            ToolSession.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            MaskCache.ClearCache();
            ToolSession.Clear();
        }

        private static RecolorEdit NewEdit() => RecolorEdit.CreateNew();

        private static MaskCache.Key KeyOf(RecolorEdit edit, bool fromExport = false) =>
            new MaskCache.Key(edit, null, 256, 0, fromExport);

        private static void AddDummy(in MaskCache.Key key)
        {
            MaskCache.Add(key, MaskTextures.Create(1, 1, "MaskCacheTests_Dummy"), default);
        }

        private static bool Has(in MaskCache.Key key) => MaskCache.TryGet(key, out _, out _);

        [Test]
        public void 上限を超えて入れると_Trim_で最古が消える()
        {
            var keys = new List<MaskCache.Key>();
            for (int i = 0; i < MaskCache.Capacity + 1; i++)
            {
                var key = KeyOf(NewEdit());
                keys.Add(key);
                AddDummy(key);
            }
            Assert.That(MaskCache.Capacity, Is.EqualTo(16));
            Assert.That(MaskCache.Count, Is.EqualTo(17), "Add では減らさない（同じ Run の途中で捨てない）");

            MaskCache.Trim();

            Assert.That(MaskCache.Count, Is.EqualTo(16));
            Assert.That(Has(keys[0]), Is.False, "最古が消える");
            Assert.That(Has(keys[1]), Is.True);
            Assert.That(Has(keys[16]), Is.True);
        }

        [Test]
        public void 使ったものは新しい扱いになり_Trim_で残る()
        {
            var keys = new List<MaskCache.Key>();
            for (int i = 0; i < MaskCache.Capacity + 1; i++)
            {
                var key = KeyOf(NewEdit());
                keys.Add(key);
                AddDummy(key);
            }
            Assert.That(Has(keys[0]), Is.True); // 最古を使う

            MaskCache.Trim();

            Assert.That(Has(keys[0]), Is.True);
            Assert.That(Has(keys[1]), Is.False, "次に古いものが消える");
        }

        [Test]
        public void 現在の編集のマスクは最古でも_Trim_で残る()
        {
            var current = NewEdit();
            var currentKey = KeyOf(current);
            AddDummy(currentKey);
            var others = new List<MaskCache.Key>();
            for (int i = 0; i < MaskCache.Capacity; i++)
            {
                var key = KeyOf(NewEdit());
                others.Add(key);
                AddDummy(key);
            }
            ToolSession.CurrentEditId = current.id;

            MaskCache.Trim();

            Assert.That(MaskCache.Count, Is.EqualTo(16));
            Assert.That(Has(currentKey), Is.True, "現在の編集は捨てない");
            Assert.That(Has(others[0]), Is.False, "代わりに現在の編集以外の最古が消える");
        }

        [Test]
        public void RemoveExportMasks_は書き出し由来だけ捨てる()
        {
            var edit = NewEdit();
            var previewKey = KeyOf(edit, fromExport: false);
            var exportKey = KeyOf(edit, fromExport: true);
            AddDummy(previewKey);
            AddDummy(exportKey);
            Assert.That(MaskCache.Count, Is.EqualTo(2), "同じ編集・同じ解像度でも書き出し由来は別の鍵");

            MaskCache.RemoveExportMasks();

            Assert.That(MaskCache.Count, Is.EqualTo(1));
            Assert.That(Has(previewKey), Is.True);
            Assert.That(Has(exportKey), Is.False);
        }
    }
}
