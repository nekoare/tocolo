using System.Linq;
using Nekoare.ClickRecolor.Editor.Masks;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>なぞって調整の画像（保存の形・三角形の塗り方・なぞり中の画像の引き方）</summary>
    public class PaintAdjustTests
    {
        [TearDown]
        public void TearDown() => PaintAdjustStore.ClearAll();

        private static int Count(PaintAdjustImage image, byte state) => image.states.Count(s => s == state);

        // ── 保存の形 ──

        [Test]
        public void 圧縮して解凍すると同じ画像に戻る()
        {
            var image = new PaintAdjustImage(8, 4);
            image.states[3] = PaintState.Add;
            image.states[20] = PaintState.Remove;

            var data = PaintAdjustCodec.Encode(image);
            var decoded = PaintAdjustCodec.Decode(data, 8, 4);

            Assert.That(decoded, Is.Not.Null);
            Assert.That(decoded.states, Is.EqualTo(image.states));
        }

        [Test]
        public void 全部そのままなら調整なしとして保存しない()
        {
            Assert.That(PaintAdjustCodec.Encode(new PaintAdjustImage(4, 4)), Is.Null);
        }

        [Test]
        public void 壊れたデータや大きさの合わないデータは調整なしとして扱う()
        {
            var image = new PaintAdjustImage(4, 4);
            image.states[0] = PaintState.Add;
            var data = PaintAdjustCodec.Encode(image);

            Assert.That(PaintAdjustCodec.Decode(System.Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 }), 4, 4), Is.Null);
            Assert.That(PaintAdjustCodec.Decode("Base64ではない文字列@@", 4, 4), Is.Null);
            Assert.That(PaintAdjustCodec.Decode(data, 4, 5), Is.Null);
            Assert.That(PaintAdjustCodec.Decode(data, 4, 3), Is.Null);
            Assert.That(PaintAdjustCodec.Decode(data, 0, 4), Is.Null);
        }

        [Test]
        public void 知らない値が入ったデータは調整なしとして扱う()
        {
            var image = new PaintAdjustImage(2, 2);
            image.states[1] = 7;
            Assert.That(PaintAdjustCodec.Decode(PaintAdjustCodec.Encode(image), 2, 2), Is.Null);
        }

        [Test]
        public void 調整の画像はテクスチャの縦横2倍で長辺4096に収め縦横比を保つ()
        {
            Assert.That(PaintAdjustImage.SizeFor(1024, 512), Is.EqualTo(new Vector2Int(2048, 1024)));
            Assert.That(PaintAdjustImage.SizeFor(2048, 2048), Is.EqualTo(new Vector2Int(4096, 4096)));
            Assert.That(PaintAdjustImage.SizeFor(4096, 2048), Is.EqualTo(new Vector2Int(4096, 2048)));
            Assert.That(PaintAdjustImage.SizeFor(8192, 8192), Is.EqualTo(new Vector2Int(4096, 4096)));
        }

        [Test]
        public void 大きさの違う画像へ写し直すとなぞった所が同じ場所に残る()
        {
            var image = new PaintAdjustImage(4, 4);
            image.states[1 * 4 + 2] = PaintState.Add; // (2,1)
            var big = image.Resampled(8, 8);

            Assert.That(big.states[2 * 8 + 4], Is.EqualTo(PaintState.Add));
            Assert.That(big.states[3 * 8 + 5], Is.EqualTo(PaintState.Add));
            Assert.That(Count(big, PaintState.Add), Is.EqualTo(4));
        }

        // ── 三角形の塗り方 ──

        [Test]
        public void 画素の中心が三角形の中か辺の上にある画素だけ塗る()
        {
            var image = new PaintAdjustImage(4, 4);
            // 左下の直角三角形（x + y <= 1）。中心 ((i+0.5)/4, (j+0.5)/4) が入るのは i + j <= 3 の 10 画素
            bool changed = PaintAdjustRaster.PaintTriangle(image, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), PaintState.Add);

            Assert.That(changed, Is.True);
            Assert.That(Count(image, PaintState.Add), Is.EqualTo(10));
            Assert.That(image.states[3 * 4 + 3], Is.EqualTo(PaintState.None));
        }

        [Test]
        public void 三角形の向きが逆でも同じ画素を塗る()
        {
            var a = new PaintAdjustImage(4, 4);
            var b = new PaintAdjustImage(4, 4);
            PaintAdjustRaster.PaintTriangle(a, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), PaintState.Add);
            PaintAdjustRaster.PaintTriangle(b, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 0f), PaintState.Add);

            Assert.That(b.states, Is.EqualTo(a.states));
        }

        [Test]
        public void 画素の中心を1つも含まない細い三角形は重心の画素を塗る()
        {
            var image = new PaintAdjustImage(4, 4);
            PaintAdjustRaster.PaintTriangle(image, new Vector2(0.30f, 0.30f), new Vector2(0.45f, 0.301f), new Vector2(0.30f, 0.302f), PaintState.Remove);

            Assert.That(Count(image, PaintState.Remove), Is.EqualTo(1));
            Assert.That(image.states[1 * 4 + 1], Is.EqualTo(PaintState.Remove));
        }

        [Test]
        public void 同じ値で塗り直しても変わらなかったと返す()
        {
            var image = new PaintAdjustImage(4, 4);
            PaintAdjustRaster.PaintTriangle(image, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), PaintState.Add);

            Assert.That(PaintAdjustRaster.PaintTriangle(image, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), PaintState.Add), Is.False);
        }

        [Test]
        public void テクスチャの外にはみ出した所は塗らない()
        {
            var image = new PaintAdjustImage(4, 4);
            PaintAdjustRaster.PaintTriangle(image, new Vector2(-1f, -1f), new Vector2(0.5f, -1f), new Vector2(-1f, 0.5f), PaintState.Add);

            // x + y <= -0.5 の中心は 0..1 の中に無い。外の画素へは書かない（例外も出さない）
            Assert.That(Count(image, PaintState.Add), Is.EqualTo(0));
        }

        // ── なぞり中の画像の引き方 ──

        [Test]
        public void なぞっている最中は手元の画像を使い終わったら保存した調整を使う()
        {
            var edit = RecolorEdit.CreateNew();
            var live = PaintAdjustStore.BeginLive(edit, 4, 4);
            live.states[0] = PaintState.Add;

            Assert.That(PaintAdjustStore.Get(edit), Is.SameAs(live));
            Assert.That(edit.HasPaintAdjust, Is.False, "離すまでは編集に書かない");

            PaintAdjustStore.Commit(edit);

            Assert.That(edit.HasPaintAdjust, Is.True);
            var saved = PaintAdjustStore.Get(edit);
            Assert.That(saved.states[0], Is.EqualTo(PaintState.Add));
        }

        [Test]
        public void Undoで番号と中身が戻されたらなぞる前の調整を使う()
        {
            var edit = RecolorEdit.CreateNew();
            var before = new PaintAdjustImage(4, 4);
            before.states[5] = PaintState.Remove;
            PaintAdjustStore.SetImage(edit, before);
            int savedRevision = edit.paintAdjustRevision;
            var savedData = edit.paintAdjustData;

            var live = PaintAdjustStore.BeginLive(edit, 4, 4);
            live.states[5] = PaintState.Add;
            PaintAdjustStore.Commit(edit);
            Assert.That(PaintAdjustStore.Get(edit).states[5], Is.EqualTo(PaintState.Add));
            // Undo で番号と中身が戻った
            edit.paintAdjustRevision = savedRevision;
            edit.paintAdjustData = savedData;

            Assert.That(PaintAdjustStore.Get(edit).states[5], Is.EqualTo(PaintState.Remove));
        }

        [Test]
        public void なぞり始めは保存済みの調整を写し元の画像は書き換えない()
        {
            var edit = RecolorEdit.CreateNew();
            var saved = new PaintAdjustImage(4, 4);
            saved.states[2] = PaintState.Add;
            PaintAdjustStore.SetImage(edit, saved);
            var decoded = PaintAdjustStore.Get(edit);

            var live = PaintAdjustStore.BeginLive(edit, 4, 4);
            live.states[2] = PaintState.Remove;

            Assert.That(live, Is.Not.SameAs(decoded));
            Assert.That(decoded.states[2], Is.EqualTo(PaintState.Add));
        }

        [Test]
        public void 全部そのままに戻して離したら調整なしになる()
        {
            var edit = RecolorEdit.CreateNew();
            var saved = new PaintAdjustImage(4, 4);
            saved.states[2] = PaintState.Add;
            PaintAdjustStore.SetImage(edit, saved);

            var live = PaintAdjustStore.BeginLive(edit, 4, 4);
            live.states[2] = PaintState.None;
            PaintAdjustStore.Commit(edit);

            Assert.That(edit.HasPaintAdjust, Is.False);
            Assert.That(PaintAdjustStore.Get(edit), Is.Null);
        }
    }
}
