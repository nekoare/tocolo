// 流用元: dev.nekoare.uv-side-splitter/Editor/Detection/IslandDetector.cs（DetectByUvOverlap の順序非依存 UV 三角形キー）
using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>
    /// 「同じ UV を使う別のパーツ」（UV 重なりの双子。左右の耳など）の検出。
    /// UV が一致する三角形は UV エッジを共有するので UvChartDetector では同じチャートにまとまる。
    /// そこで種のチャートの中で UV 三角形キーが重複する組を探し、3D 位置が異なる組が 1 つでもあれば双子とする。
    /// 3D 位置まで一致する組は両面メッシュの裏ポリゴンとみなして数えない。
    /// UV キー・位置キーはどちらも 3 頂点を並べ替えた組なので、頂点順・巻き順に依らない（ミラー複製・裏面でも一致する）
    /// </summary>
    internal static class UvTwinDetector
    {
        private const float UvQuant = 100000f;
        private const float PositionQuant = 10000f;

        /// <summary>保持する判定結果の数の上限</summary>
        internal const int Capacity = 16;

        private struct Entry
        {
            public int meshId;
            public int fingerprint;
            public int submesh;
            public int chartId;
            public bool hasTwin;
        }

        // 末尾ほど最近使ったもの（LRU）
        private static readonly List<Entry> s_entries = new List<Entry>();

        /// <summary>キャッシュ中の件数（テスト用）</summary>
        internal static int Count => s_entries.Count;

        /// <summary>判定結果のキャッシュを消す（RecolorPreview.ClearCaches から呼ぶ）</summary>
        internal static void ClearCache() => s_entries.Clear();

        /// <summary>
        /// (mesh, submesh) の chartId のチャートに、UV が同じで 3D 位置が違う三角形の組があれば true。
        /// table は UvChartDetector.GetOrBuild(mesh, submesh) の結果。読み取り不可・範囲外などで判定できなければ false
        /// </summary>
        internal static bool HasTwin(Mesh mesh, int submesh, int chartId, ChartTable table)
        {
            if (mesh == null || table == null || !mesh.isReadable) return false;
            if (submesh < 0 || submesh >= mesh.subMeshCount) return false;
            if (chartId < 0 || chartId >= table.chartCount) return false;

            int meshId = mesh.GetInstanceID();
            int fingerprint = UvChartDetector.Fingerprint(mesh);
            for (int i = 0; i < s_entries.Count; i++)
            {
                var entry = s_entries[i];
                if (entry.meshId != meshId || entry.submesh != submesh || entry.chartId != chartId) continue;
                s_entries.RemoveAt(i);
                // 形が変わっていたら作り直す
                if (entry.fingerprint != fingerprint) break;
                s_entries.Add(entry);
                return entry.hasTwin;
            }

            var uv = mesh.uv;
            var vertices = mesh.vertices;
            if (uv == null || uv.Length != mesh.vertexCount || vertices == null) return false;
            bool hasTwin = HasTwin(vertices, uv, mesh.GetTriangles(submesh), chartId, table);

            s_entries.Add(new Entry
            {
                meshId = meshId, fingerprint = fingerprint, submesh = submesh, chartId = chartId, hasTwin = hasTwin,
            });
            while (s_entries.Count > Capacity) s_entries.RemoveAt(0);
            return hasTwin;
        }

        /// <summary>
        /// 配列版（キャッシュなし）。chartId のチャートの三角形を UV キーで束ね、同じ UV キーの中に
        /// 最初の三角形と 3D 位置キーが違う三角形があれば true（全部同じ位置なら裏ポリゴンなので false）
        /// </summary>
        internal static bool HasTwin(Vector3[] vertices, Vector2[] uv, int[] triangles, int chartId, ChartTable table)
        {
            if (vertices == null || uv == null || triangles == null || table == null) return false;
            int triCount = Mathf.Min(triangles.Length / 3, table.chartOfTriangle.Length);

            // UV 三角形キー → そのキーで最初に出てきた三角形の位置キー
            var firstPositionOfUv = new Dictionary<(long, long, long), PositionKey>();
            for (int t = 0; t < triCount; t++)
            {
                if (table.chartOfTriangle[t] != chartId) continue;
                int v0 = triangles[t * 3];
                int v1 = triangles[t * 3 + 1];
                int v2 = triangles[t * 3 + 2];
                if (!InRange(v0, uv.Length, vertices.Length) || !InRange(v1, uv.Length, vertices.Length)
                    || !InRange(v2, uv.Length, vertices.Length)) continue;

                var uvKey = MakeUvKey(uv[v0], uv[v1], uv[v2]);
                var positionKey = PositionKey.Make(vertices[v0], vertices[v1], vertices[v2]);
                if (!firstPositionOfUv.TryGetValue(uvKey, out var first))
                {
                    firstPositionOfUv.Add(uvKey, positionKey);
                }
                else if (!first.Equals(positionKey))
                {
                    // 同じ UV で別の場所にある三角形（最初のと違えば、少なくとも 1 組は位置が異なる）
                    return true;
                }
            }
            return false;
        }

        private static bool InRange(int v, int uvLength, int vertexLength) => v >= 0 && v < uvLength && v < vertexLength;

        private static (long, long, long) MakeUvKey(Vector2 a, Vector2 b, Vector2 c)
        {
            long ka = PackUv(a);
            long kb = PackUv(b);
            long kc = PackUv(c);

            // 3 値のソート（頂点順・巻き順の違いを吸収）
            if (ka > kb) { (ka, kb) = (kb, ka); }
            if (kb > kc) { (kb, kc) = (kc, kb); }
            if (ka > kb) { (ka, kb) = (kb, ka); }
            return (ka, kb, kc);
        }

        private static long PackUv(Vector2 uv)
        {
            // UvChartDetector と同じ詰め方（10 万倍して丸め、32bit ずつ）
            int qx = Mathf.RoundToInt(uv.x * UvQuant);
            int qy = Mathf.RoundToInt(uv.y * UvQuant);
            return ((long)(uint)qx << 32) | (uint)qy;
        }

        /// <summary>3 頂点の位置（1e-4 で丸め）を辞書順に並べた組。頂点順・巻き順に依らない</summary>
        private readonly struct PositionKey : System.IEquatable<PositionKey>
        {
            private readonly Vector3Int _a;
            private readonly Vector3Int _b;
            private readonly Vector3Int _c;

            private PositionKey(Vector3Int a, Vector3Int b, Vector3Int c)
            {
                _a = a;
                _b = b;
                _c = c;
            }

            internal static PositionKey Make(Vector3 a, Vector3 b, Vector3 c)
            {
                var qa = Quantize(a);
                var qb = Quantize(b);
                var qc = Quantize(c);
                if (Compare(qa, qb) > 0) { (qa, qb) = (qb, qa); }
                if (Compare(qb, qc) > 0) { (qb, qc) = (qc, qb); }
                if (Compare(qa, qb) > 0) { (qa, qb) = (qb, qa); }
                return new PositionKey(qa, qb, qc);
            }

            private static Vector3Int Quantize(Vector3 p) => new Vector3Int(
                Mathf.RoundToInt(p.x * PositionQuant),
                Mathf.RoundToInt(p.y * PositionQuant),
                Mathf.RoundToInt(p.z * PositionQuant));

            private static int Compare(Vector3Int p, Vector3Int q)
            {
                if (p.x != q.x) return p.x.CompareTo(q.x);
                if (p.y != q.y) return p.y.CompareTo(q.y);
                return p.z.CompareTo(q.z);
            }

            public bool Equals(PositionKey other) => _a == other._a && _b == other._b && _c == other._c;
            public override bool Equals(object obj) => obj is PositionKey other && Equals(other);
            public override int GetHashCode() => (_a, _b, _c).GetHashCode();
        }
    }
}
