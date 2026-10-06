using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.NDMF;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    public class ChmHairSlotsTests
    {
        private readonly List<Object> _cleanup = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
        }

        private Texture2D Tex(string name)
        {
            var t = new Texture2D(4, 4) { name = name };
            _cleanup.Add(t);
            return t;
        }

        private Material Mat(Texture2D main)
        {
            var m = new Material(Shader.Find("Standard"));
            m.SetTexture("_MainTex", main);
            _cleanup.Add(m);
            return m;
        }

        [Test]
        public void 元のスロットのメインが対象で_今のメインが違えば_今のメインが土台になる()
        {
            var source = Tex("hair");
            var processed = Tex("hair_chm");
            var now = Mat(processed);

            var slots = ChmHairSlots.Plan(new[] { Mat(source) }, new[] { now }, new HashSet<Texture2D> { source }, out bool mismatch);

            Assert.That(mismatch, Is.False);
            Assert.That(slots.Count, Is.EqualTo(1));
            Assert.That(slots[0].slot, Is.EqualTo(0));
            Assert.That(slots[0].source, Is.SameAs(source));
            Assert.That(slots[0].baseTexture, Is.SameAs(processed));
            Assert.That(slots[0].current, Is.SameAs(now));
            Assert.That(slots[0].propertyName, Is.EqualTo("_MainTex"));
        }

        [Test]
        public void 今のメインが元と同じなら_土台はnull()
        {
            var source = Tex("hair");

            var slots = ChmHairSlots.Plan(new[] { Mat(source) }, new[] { Mat(source) }, new HashSet<Texture2D> { source }, out _);

            Assert.That(slots.Count, Is.EqualTo(1));
            Assert.That(slots[0].baseTexture, Is.Null);
        }

        [Test]
        public void 元のメインが対象でないスロットは選ばない()
        {
            var source = Tex("hair");
            var other = Tex("ribbon");

            var slots = ChmHairSlots.Plan(
                new[] { Mat(other), Mat(source) }, new[] { Mat(Tex("ribbon_chm")), Mat(Tex("hair_chm")) },
                new HashSet<Texture2D> { source }, out _);

            Assert.That(slots.Count, Is.EqualTo(1));
            Assert.That(slots[0].slot, Is.EqualTo(1));
        }

        [Test]
        public void 今のマテリアルが元より少なければ_どれも選ばず数が合わないと知らせる()
        {
            var source = Tex("hair");

            var slots = ChmHairSlots.Plan(
                new[] { Mat(source), Mat(Tex("other")) }, new[] { Mat(Tex("hair_chm")) },
                new HashSet<Texture2D> { source }, out bool mismatch);

            Assert.That(mismatch, Is.True);
            Assert.That(slots, Is.Empty);
        }

        [Test]
        public void 対象のスロットが無ければ_数が違っても知らせない()
        {
            var slots = ChmHairSlots.Plan(
                new[] { Mat(Tex("a")), Mat(Tex("b")) }, new[] { Mat(Tex("c")) },
                new HashSet<Texture2D> { Tex("hair") }, out bool mismatch);

            Assert.That(mismatch, Is.False);
            Assert.That(slots, Is.Empty);
        }

        [Test]
        public void 末尾に足されたスロットは見ない()
        {
            var source = Tex("hair");
            var appended = Mat(source);

            var slots = ChmHairSlots.Plan(
                new[] { Mat(source) }, new[] { Mat(Tex("hair_chm")), appended },
                new HashSet<Texture2D> { source }, out _);

            Assert.That(slots.Count, Is.EqualTo(1));
            Assert.That(slots[0].slot, Is.EqualTo(0));
        }
    }
}
