using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>なぞって調整の画素の値（RecolorEdit.paintAdjustData）</summary>
    internal static class PaintState
    {
        public const byte None = 0;
        public const byte Add = 1;
        public const byte Remove = 2;
    }

    /// <summary>解凍したなぞって調整の画像（テクスチャ座標、y = 0 が下。画素は PaintState）</summary>
    internal sealed class PaintAdjustImage
    {
        /// <summary>調整の画像の長辺の上限（px）。テクスチャの 2 倍がこれより大きければ縦横比を保って縮める</summary>
        internal const int MaxSize = 4096;

        /// <summary>
        /// テクスチャに対する調整の画像の細かさ（縦横それぞれ）。三角形を画素ごとの「入る／入らない」で塗ると境目がテクスチャの画素の階段になるので、
        /// 2 倍の細かさで塗っておき、範囲を作るときに平均で縮めて境目の画素を途中の値にする（箱の中の範囲の 2 倍スーパーサンプリングと同じ考え方）
        /// </summary>
        internal const int Supersample = 2;

        public readonly int width;
        public readonly int height;
        public readonly byte[] states;

        public PaintAdjustImage(int width, int height)
        {
            this.width = width;
            this.height = height;
            states = new byte[width * height];
        }

        public PaintAdjustImage(int width, int height, byte[] states)
        {
            this.width = width;
            this.height = height;
            this.states = states;
        }

        /// <summary>texWidth×texHeight のテクスチャに使う調整の画像の大きさ（縦横 Supersample 倍。長辺 MaxSize に収める）</summary>
        internal static Vector2Int SizeFor(int texWidth, int texHeight)
        {
            int width = Mathf.Max(texWidth, 1) * Supersample;
            int height = Mathf.Max(texHeight, 1) * Supersample;
            int longest = Mathf.Max(width, height);
            if (longest <= MaxSize) return new Vector2Int(width, height);
            return new Vector2Int(
                Mathf.Max(1, Mathf.RoundToInt((float)width * MaxSize / longest)),
                Mathf.Max(1, Mathf.RoundToInt((float)height * MaxSize / longest)));
        }

        public PaintAdjustImage Copy() => new PaintAdjustImage(width, height, (byte[])states.Clone());

        /// <summary>toWidth×toHeight に最近傍で写した画像（テクスチャの大きさが変わったときに、なぞった所を引き継ぐ）</summary>
        public PaintAdjustImage Resampled(int toWidth, int toHeight)
        {
            if (toWidth == width && toHeight == height) return Copy();
            var result = new PaintAdjustImage(toWidth, toHeight);
            for (int y = 0; y < toHeight; y++)
            {
                int sy = Mathf.Min(height - 1, (int)((y + 0.5f) * height / toHeight));
                for (int x = 0; x < toWidth; x++)
                {
                    int sx = Mathf.Min(width - 1, (int)((x + 0.5f) * width / toWidth));
                    result.states[y * toWidth + x] = states[sy * width + sx];
                }
            }
            return result;
        }

        /// <summary>value の画素があるか</summary>
        public bool Contains(byte value)
        {
            foreach (byte s in states)
            {
                if (s == value) return true;
            }
            return false;
        }
    }

    /// <summary>なぞって調整の画像の圧縮・解凍（RecolorEdit.paintAdjustData の中身。Deflate を Base64 にした文字列）</summary>
    internal static class PaintAdjustCodec
    {
        /// <summary>圧縮して Base64 にした文字列。全部「そのまま」なら null（調整なし）</summary>
        internal static string Encode(PaintAdjustImage image)
        {
            if (image == null || !(image.Contains(PaintState.Add) || image.Contains(PaintState.Remove))) return null;
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                {
                    deflate.Write(image.states, 0, image.states.Length);
                }
                return Convert.ToBase64String(output.ToArray());
            }
        }

        /// <summary>
        /// width×height の画像に解凍する。壊れている・大きさが合わない・知らない値が入っている・大きさが上限を超えるなら null
        /// （調整なしとして扱い、範囲はパーツで選んだままにする）
        /// </summary>
        internal static PaintAdjustImage Decode(string text, int width, int height)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (width <= 0 || height <= 0 || width > PaintAdjustImage.MaxSize || height > PaintAdjustImage.MaxSize) return null;
            byte[] data;
            try
            {
                data = Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                return null;
            }
            var states = new byte[width * height];
            try
            {
                using (var input = new MemoryStream(data))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                {
                    int read = 0;
                    while (read < states.Length)
                    {
                        int n = deflate.Read(states, read, states.Length - read);
                        if (n <= 0) return null;
                        read += n;
                    }
                    // 余りがあれば大きさが合っていない
                    if (deflate.ReadByte() >= 0) return null;
                }
            }
            // 壊れたデータの例外は環境で違う（.NET は InvalidDataException、Unity の Mono は IOException「Corrupted data」）
            catch (InvalidDataException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            foreach (byte s in states)
            {
                if (s > PaintState.Remove) return null;
            }
            return new PaintAdjustImage(width, height, states);
        }
    }

    /// <summary>なぞった三角形を調整の画像に描く（CPU。なぞる 1 回で描くのは筆に触れた数個〜数百個の三角形だけ）</summary>
    internal static class PaintAdjustRaster
    {
        /// <summary>
        /// テクスチャ座標（0..1、y = 0 が下。0..1 の外は折り返さない）の三角形 a, b, c を state で塗る。
        /// 画素の中心が三角形の中（辺の上を含む）にある画素を塗り、1 画素も無い細い三角形は重心の画素を塗る
        /// （髪の毛先などの細い三角形を足しても消しても、何も起きないことがないように）。
        /// 塗った画素の値が 1 つでも変わったら true
        /// </summary>
        internal static bool PaintTriangle(PaintAdjustImage image, Vector2 a, Vector2 b, Vector2 c, byte state)
        {
            if (image == null) return false;
            int w = image.width, h = image.height;
            var pa = new Vector2(a.x * w, a.y * h);
            var pb = new Vector2(b.x * w, b.y * h);
            var pc = new Vector2(c.x * w, c.y * h);

            int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(pa.x, Mathf.Min(pb.x, pc.x))));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(pa.y, Mathf.Min(pb.y, pc.y))));
            int x1 = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(pa.x, Mathf.Max(pb.x, pc.x))));
            int y1 = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(pa.y, Mathf.Max(pb.y, pc.y))));

            bool changed = false;
            bool any = false;
            float area = Edge(pa, pb, pc);
            if (Mathf.Abs(area) > 1e-9f)
            {
                // 辺の上の画素も含める（隣の三角形と画素の取り合いで隙間を作らない）。向きに依らないよう面積の符号で揃える
                float sign = area > 0f ? 1f : -1f;
                const float eps = -1e-5f;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        var p = new Vector2(x + 0.5f, y + 0.5f);
                        if (Edge(pa, pb, p) * sign < eps * Mathf.Abs(area)) continue;
                        if (Edge(pb, pc, p) * sign < eps * Mathf.Abs(area)) continue;
                        if (Edge(pc, pa, p) * sign < eps * Mathf.Abs(area)) continue;
                        any = true;
                        changed |= Set(image, x, y, state);
                    }
                }
            }
            if (!any)
            {
                var centroid = (pa + pb + pc) / 3f;
                int cx = Mathf.FloorToInt(centroid.x);
                int cy = Mathf.FloorToInt(centroid.y);
                if (cx >= 0 && cx < w && cy >= 0 && cy < h) changed |= Set(image, cx, cy, state);
            }
            return changed;
        }

        private static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        private static bool Set(PaintAdjustImage image, int x, int y, byte state)
        {
            int i = y * image.width + x;
            if (image.states[i] == state) return false;
            image.states[i] = state;
            return true;
        }
    }

    /// <summary>
    /// 編集ごとのなぞって調整の画像を引く。なぞっている最中の編集は手元の画像（圧縮していない）を返し、
    /// それ以外は RecolorEdit.paintAdjustData を解凍して少しだけ覚えておく。
    /// なぞっている間は編集へ書かず、離したとき（Commit）に圧縮して書く（書くたびにプレビューの範囲と統計の作り直しが走り、1 回 100ms 以上かかる）
    /// </summary>
    internal static class PaintAdjustStore
    {
        private sealed class Live
        {
            public PaintAdjustImage image;
            public int revision;
        }

        private const int DecodedCapacity = 8;

        private static readonly Dictionary<string, Live> s_live = new Dictionary<string, Live>();
        private static readonly List<(string id, int revision, int dataHash, PaintAdjustImage image)> s_decoded =
            new List<(string, int, int, PaintAdjustImage)>();
        private static readonly ConditionalWeakTable<string, object> s_dataHashes = new ConditionalWeakTable<string, object>();
        private static readonly System.Random s_random = new System.Random();

        /// <summary>edit の調整の画像。調整が無い・解凍できなければ null</summary>
        internal static PaintAdjustImage Get(RecolorEdit edit)
        {
            if (edit == null || string.IsNullOrEmpty(edit.id)) return null;
            // 番号が合うときだけ手元の画像を使う（Undo で番号が戻ったら、なぞる前の保存済みの方を見る）
            if (s_live.TryGetValue(edit.id, out var live) && live.revision == edit.paintAdjustRevision) return live.image;
            if (!edit.HasPaintAdjust) return null;
            int hash = DataHash(edit.paintAdjustData);
            for (int i = 0; i < s_decoded.Count; i++)
            {
                var entry = s_decoded[i];
                if (entry.id != edit.id || entry.revision != edit.paintAdjustRevision || entry.dataHash != hash) continue;
                if (entry.image != null && (entry.image.width != edit.paintAdjustWidth || entry.image.height != edit.paintAdjustHeight)) continue;
                s_decoded.RemoveAt(i);
                s_decoded.Add(entry);
                return entry.image;
            }
            // 解凍できなかったことも覚える（壊れたデータの編集で、プレビューを作り直すたびに解凍を試みない）
            var image = PaintAdjustCodec.Decode(edit.paintAdjustData, edit.paintAdjustWidth, edit.paintAdjustHeight);
            Remember(edit.id, edit.paintAdjustRevision, hash, image);
            return image;
        }

        /// <summary>
        /// なぞり始め: edit の手元の画像（width×height）を返す。既にあればそれ、なければ保存済みの調整を写して（大きさが違えば最近傍で写し直して）作る。
        /// 保存済みの画像は Undo で戻すときに使うので、そのまま書き換えない
        /// </summary>
        internal static PaintAdjustImage BeginLive(RecolorEdit edit, int width, int height)
        {
            if (edit == null || string.IsNullOrEmpty(edit.id)) return null;
            if (s_live.TryGetValue(edit.id, out var live) && live.image.width == width && live.image.height == height) return live.image;
            var saved = Get(edit);
            var image = saved != null ? saved.Resampled(width, height) : new PaintAdjustImage(width, height);
            s_live[edit.id] = new Live { image = image, revision = edit.paintAdjustRevision };
            return image;
        }

        /// <summary>
        /// なぞり終わり: 手元の画像を圧縮して編集に書き（Undo.RecordObject は呼び出し側）、手元の画像は解凍済みとして覚えて手放す。
        /// 全部「そのまま」なら調整なしにする
        /// </summary>
        internal static void Commit(RecolorEdit edit)
        {
            if (edit == null || !s_live.TryGetValue(edit.id, out var live)) return;
            s_live.Remove(edit.id);
            SetImage(edit, live.image);
        }

        /// <summary>edit の調整を image にする（null・全部「そのまま」なら調整なし）。Undo.RecordObject は呼び出し側</summary>
        internal static void SetImage(RecolorEdit edit, PaintAdjustImage image)
        {
            if (edit == null) return;
            var data = PaintAdjustCodec.Encode(image);
            edit.paintAdjustData = data;
            edit.paintAdjustWidth = data != null ? image.width : 0;
            edit.paintAdjustHeight = data != null ? image.height : 0;
            edit.paintAdjustRevision = NewRevision();
            if (data != null) Remember(edit.id, edit.paintAdjustRevision, DataHash(data), image);
        }

        /// <summary>なぞっている最中の画像を全部捨てる（保存はしない。ツールの終了・なぞり中の中止）</summary>
        internal static void DiscardLive() => s_live.Clear();

        /// <summary>なぞっている最中の編集があるか</summary>
        internal static bool HasLive => s_live.Count > 0;

        /// <summary>手元の画像と解凍済みの画像を全部忘れる（テスト用）</summary>
        internal static void ClearAll()
        {
            s_live.Clear();
            s_decoded.Clear();
        }

        /// <summary>
        /// 調整を変えるたびに振る番号（0 以外）。1 ずつ増やすと、Undo で戻した後になぞり直したときに、戻す前と同じ番号で中身の違う調整ができ、
        /// キャッシュが古いマスクを返してしまう
        /// </summary>
        internal static int NewRevision()
        {
            int value;
            do value = s_random.Next(int.MinValue, int.MaxValue);
            while (value == 0);
            return value;
        }

        /// <summary>圧縮したバイト列の中身のハッシュ（同じ配列は 1 回だけ数える。プレビューのハッシュで毎回数えないように）</summary>
        internal static int DataHash(string data)
        {
            if (string.IsNullOrEmpty(data)) return 0;
            if (s_dataHashes.TryGetValue(data, out var boxed)) return (int)boxed;
            unchecked
            {
                int h = (int)2166136261;
                foreach (char c in data) h = (h ^ c) * 16777619;
                s_dataHashes.Add(data, h);
                return h;
            }
        }

        private static void Remember(string id, int revision, int dataHash, PaintAdjustImage image)
        {
            s_decoded.RemoveAll(e => e.id == id && e.revision == revision);
            s_decoded.Add((id, revision, dataHash, image));
            while (s_decoded.Count > DecodedCapacity) s_decoded.RemoveAt(0);
        }
    }
}
