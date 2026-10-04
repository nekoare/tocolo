using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    public class ToolSessionTests
    {
        private GameObject _go;

        [SetUp] public void SetUp() { ToolSession.Clear(); }
        [TearDown] public void TearDown() { ToolSession.Clear(); if (_go != null) Object.DestroyImmediate(_go); }

        [Test]
        public void SetActiveRoot_は_TryGetActiveRoot_で取り出せる()
        {
            _go = new GameObject("avatar");

            ToolSession.SetActiveRoot(_go);

            Assert.That(ToolSession.TryGetActiveRoot(out var found), Is.True);
            Assert.That(found, Is.SameAs(_go));
        }

        [Test]
        public void SetActiveRoot_は_ClickRecolor_を付けない()
        {
            _go = new GameObject("avatar");

            ToolSession.SetActiveRoot(_go);

            Assert.That(_go.GetComponent<ClickRecolor>(), Is.Null);
        }

        [Test]
        public void SetActiveRoot_null_で解除される()
        {
            _go = new GameObject("avatar");
            ToolSession.SetActiveRoot(_go);

            ToolSession.SetActiveRoot(null);

            Assert.That(ToolSession.TryGetActiveRoot(out var found), Is.False);
            Assert.That(found, Is.Null);
        }

        [Test]
        public void 対象が破棄されたら_false()
        {
            _go = new GameObject("avatar");
            ToolSession.SetActiveRoot(_go);

            Object.DestroyImmediate(_go); _go = null;

            Assert.That(ToolSession.TryGetActiveRoot(out _), Is.False);
        }

        [Test]
        public void 既定のプリセットは島で_Mode_も島()
        {
            Assert.That(ToolSession.Preset, Is.EqualTo(RangePreset.Island));
            Assert.That(ToolSession.Mode, Is.EqualTo(SelectionMode.Island));

            ToolSession.Preset = RangePreset.SimilarColor;

            Assert.That(ToolSession.Mode, Is.EqualTo(SelectionMode.Color));
        }

        [Test]
        public void Clear_で_DecalBoxHiddenEditId_が消える()
        {
            ToolSession.DecalBoxHiddenEditId = "edit-1";

            ToolSession.Clear();

            Assert.That(ToolSession.DecalBoxHiddenEditId, Is.Null);
        }

        [Test]
        public void Clear_で_DecalBoxDragging_が_false_になる()
        {
            ToolSession.BeginDecalBoxDrag(new RecolorEdit());

            ToolSession.Clear();

            Assert.That(ToolSession.DecalBoxDragging, Is.False);
        }
    }
}
