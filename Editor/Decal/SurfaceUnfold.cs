using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Decal
{
    /// <summary>
    /// 展開に使う面のつながり（設計 docs/plans/2026-10-07-tocolo-sticker-surface-unfold-design.md）。元の頂点（UV の切れ目で分かれている）を、
    /// 位置が同じで法線が同じ側を向くものだけ 1 つにまとめ、三角形の辺でつないだもの。法線の向きも見るのは、髪の裏側のように同じ位置に逆向きの面を
    /// 重ねたメッシュで、表から裏へ回り込まないようにするため。
    /// 辺でつながっていない別のパーツ（毛束どうし）でも、すき間が小さく法線がだいたい同じ向きなら橋を架けてつなぐ（AddBridges。毛束ごとに
    /// 止まると、髪に貼ったシールが 1 本の毛束にしか出ない）。位置は呼び出し側の座標（対象ルートのローカル。メートル単位のアバターを前提にすき間の上限を決める）
    /// </summary>
    internal sealed class SurfaceUnfoldGraph
    {
        /// <summary>位置を同じとみなす丸めの単位（対象ルートのローカル。0.01mm）</summary>
        private const float WeldUnit = 1e-5f;

        /// <summary>元の頂点 → まとめた頂点（つながりに入っていなければ −1）</summary>
        internal readonly int[] weldOf;
        internal readonly List<Vector3> positions = new List<Vector3>();
        /// <summary>まとめた頂点の法線（面積重みつきの和を正規化。向きはメッシュの巻き方のまま）</summary>
        internal readonly List<Vector3> normals = new List<Vector3>();
        internal readonly List<List<int>> neighbors = new List<List<int>>();
        /// <summary>つながりに入れた三角形（元の頂点番号、3 つずつ）。種の三角形を探すのに使う</summary>
        internal readonly List<int> triangles = new List<int>();

        private SurfaceUnfoldGraph(int vertexCount)
        {
            weldOf = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++) weldOf[i] = -1;
        }

        /// <summary>
        /// positions（元の頂点の位置）と triangles（元の頂点番号、3 つずつ）からつながりを作る。include が false の三角形は入れない
        /// </summary>
        internal static SurfaceUnfoldGraph Build(IReadOnlyList<Vector3> positions, IReadOnlyList<int> triangles, Func<int, int, int, bool> include = null)
        {
            var graph = new SurfaceUnfoldGraph(positions.Count);
            // 元の頂点ごとの法線（その頂点を含む三角形の面積重みつきの和）
            var sourceNormals = new Dictionary<int, Vector3>();
            var used = new List<int>();
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= positions.Count || b >= positions.Count || c >= positions.Count) continue;
                if (include != null && !include(a, b, c)) continue;
                var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                AddNormal(sourceNormals, a, n);
                AddNormal(sourceNormals, b, n);
                AddNormal(sourceNormals, c, n);
                graph.triangles.Add(a);
                graph.triangles.Add(b);
                graph.triangles.Add(c);
            }

            // 位置で丸めた鍵 → その位置のまとめた頂点（法線の向きごとに別）
            var buckets = new Dictionary<(long, long, long), List<int>>();
            var normalSums = new List<Vector3>();
            foreach (var pair in sourceNormals)
            {
                int v = pair.Key;
                var p = positions[v];
                var key = ((long)Mathf.Round(p.x / WeldUnit), (long)Mathf.Round(p.y / WeldUnit), (long)Mathf.Round(p.z / WeldUnit));
                if (!buckets.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    buckets[key] = list;
                }
                int welded = -1;
                foreach (int candidate in list)
                {
                    // 法線が同じ側（面積が 0 の頂点はどれとでもまとめる）
                    if (Vector3.Dot(normalSums[candidate], pair.Value) >= 0f) { welded = candidate; break; }
                }
                if (welded < 0)
                {
                    welded = graph.positions.Count;
                    graph.positions.Add(p);
                    graph.neighbors.Add(new List<int>());
                    normalSums.Add(Vector3.zero);
                    list.Add(welded);
                }
                normalSums[welded] += pair.Value;
                graph.weldOf[v] = welded;
            }
            foreach (var sum in normalSums)
            {
                float length = sum.magnitude;
                graph.normals.Add(length > 0f ? sum / length : Vector3.zero);
            }

            for (int t = 0; t + 2 < graph.triangles.Count; t += 3)
            {
                int a = graph.weldOf[graph.triangles[t]], b = graph.weldOf[graph.triangles[t + 1]], c = graph.weldOf[graph.triangles[t + 2]];
                Link(graph.neighbors, a, b);
                Link(graph.neighbors, b, c);
                Link(graph.neighbors, c, a);
            }
            graph.AddBridges();
            return graph;
        }

        /// <summary>橋を架けるすき間の上限の、辺の長さの中央値に対する倍率</summary>
        internal const float BridgeEdgeRatio = 1.5f;
        /// <summary>橋を架けるすき間の上限の下限と上限（対象ルートのローカル。3mm〜1.5cm。髪と顔のような大きなすき間には架けない）</summary>
        internal const float MinBridgeGap = 0.003f;
        internal const float MaxBridgeGap = 0.015f;
        /// <summary>橋を架ける 2 頂点の法線の向きのそろい具合（内積）の下限（60° 以内。裏向きに重ねた面や、向きの違う面には架けない）</summary>
        internal const float BridgeNormalDot = 0.5f;
        /// <summary>1 つの頂点から架ける橋の数の上限（近い順。重なりの多い所で辺が増えすぎないように）</summary>
        private const int MaxBridgesPerVertex = 4;

        /// <summary>
        /// 辺でつながっていない別のパーツの頂点のうち、すき間（辺の長さの中央値 × BridgeEdgeRatio、MinBridgeGap〜MaxBridgeGap）より近く、
        /// 法線がそろったものへ辺を足す。同じパーツの中には足さない（近道ができて、面に沿った距離が縮んでしまう）
        /// </summary>
        private void AddBridges()
        {
            int count = positions.Count;
            if (count < 2) return;
            var component = new int[count];
            for (int i = 0; i < count; i++) component[i] = i;
            int Find(int x)
            {
                while (component[x] != x)
                {
                    component[x] = component[component[x]];
                    x = component[x];
                }
                return x;
            }
            var lengths = new List<float>();
            for (int a = 0; a < count; a++)
            {
                foreach (int b in neighbors[a])
                {
                    if (b <= a) continue;
                    lengths.Add((positions[a] - positions[b]).magnitude);
                    int ra = Find(a), rb = Find(b);
                    if (ra != rb) component[ra] = rb;
                }
            }
            if (lengths.Count == 0) return;
            lengths.Sort();
            float gap = Mathf.Clamp(lengths[lengths.Count / 2] * BridgeEdgeRatio, MinBridgeGap, MaxBridgeGap);
            var roots = new int[count];
            for (int i = 0; i < count; i++) roots[i] = Find(i);

            var cells = new Dictionary<(int, int, int), List<int>>();
            for (int v = 0; v < count; v++)
            {
                var key = CellOf(positions[v], gap);
                if (!cells.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    cells[key] = list;
                }
                list.Add(v);
            }
            var candidates = new List<(float distance, int vertex)>();
            float gapSq = gap * gap;
            for (int v = 0; v < count; v++)
            {
                if (normals[v].sqrMagnitude < 0.25f) continue;
                candidates.Clear();
                var (cx, cy, cz) = CellOf(positions[v], gap);
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (!cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                            foreach (int u in list)
                            {
                                if (roots[u] == roots[v]) continue;
                                float d = (positions[u] - positions[v]).sqrMagnitude;
                                if (d > gapSq || Vector3.Dot(normals[u], normals[v]) < BridgeNormalDot) continue;
                                candidates.Add((d, u));
                            }
                        }
                    }
                }
                if (candidates.Count == 0) continue;
                candidates.Sort((x, y) => x.distance.CompareTo(y.distance));
                for (int k = 0; k < candidates.Count && k < MaxBridgesPerVertex; k++) Link(neighbors, v, candidates[k].vertex);
            }
        }

        private static (int, int, int) CellOf(Vector3 p, float size) =>
            (Mathf.FloorToInt(p.x / size), Mathf.FloorToInt(p.y / size), Mathf.FloorToInt(p.z / size));

        private static void AddNormal(Dictionary<int, Vector3> normals, int v, Vector3 n)
        {
            normals[v] = normals.TryGetValue(v, out var sum) ? sum + n : n;
        }

        private static void Link(List<List<int>> neighbors, int a, int b)
        {
            if (a == b || a < 0 || b < 0) return;
            if (!neighbors[a].Contains(b)) neighbors[a].Add(b);
            if (!neighbors[b].Contains(a)) neighbors[b].Add(a);
        }
    }

    /// <summary>
    /// 面に沿った展開（離散指数写像）: 種の点から面の上を頂点づたいにたどり、頂点ごとに「種の接平面での、面に沿った距離と方向」の座標を求める。
    /// 平らな面では種の接平面への平行投影と同じ座標になり、曲がった面では面に沿って回り込む（180° を超えても届く）
    /// </summary>
    internal static class SurfaceUnfold
    {
        /// <summary>
        /// graph の上で、seed（面の上の点）を中心に、frame（右 = 座標の x、上 = 座標の y、正面 = 面の外向き）で展開する。
        /// coords（元の頂点数の長さ）に元の頂点ごとの座標を入れる（届かない頂点は NaN）。種から maxDistance より遠い頂点は広げない
        /// （そのすぐ外側の 1 周までは座標を入れる。画像の縁にかかる三角形が欠けないように）。
        /// 展開は seed に一番近い三角形から始める。seed はその面の上に無くてもよく（画像の中心が選択範囲の外へ出たとき）、
        /// 座標はいつも seed から測る（種の三角形の頂点は seed からのずれを接平面に写した座標）。seedFilter（元の頂点番号 3 つ）を渡すと、
        /// それを満たす三角形から先に探す（選択範囲の三角形。範囲の外の別のパーツから始めると、範囲につながっていなくて何も描かれない）。
        /// seed から seedTolerance より近い三角形が無ければ false
        /// </summary>
        internal static bool TryUnfold(SurfaceUnfoldGraph graph, Vector3 seed, Quaternion frame, float maxDistance, float seedTolerance, Vector2[] coords,
            Func<int, int, int, bool> seedFilter = null)
        {
            for (int i = 0; i < coords.Length; i++) coords[i] = new Vector2(float.NaN, float.NaN);
            if (graph == null || graph.positions.Count == 0 || graph.triangles.Count < 3) return false;
            frame = Quaternion.Normalize(frame);
            var right = frame * Vector3.right;
            var up = frame * Vector3.up;
            var forward = frame * Vector3.forward;
            // 見えている側（frame の正面）を表にした三角形を先に探す。同じ位置に逆向きの面を重ねたメッシュでは、距離が同じなので向きで選ばないと裏の面から展開してしまう
            int seedTriangle = -1;
            bool found = seedFilter != null
                && (TryFindSeedTriangle(graph, seed, seedTolerance, forward, true, seedFilter, out seedTriangle)
                    || TryFindSeedTriangle(graph, seed, seedTolerance, forward, false, seedFilter, out seedTriangle));
            if (!found
                && !TryFindSeedTriangle(graph, seed, seedTolerance, forward, true, null, out seedTriangle)
                && !TryFindSeedTriangle(graph, seed, seedTolerance, forward, false, null, out seedTriangle))
            {
                return false;
            }
            int count = graph.positions.Count;
            int[] seedVertices =
            {
                graph.weldOf[graph.triangles[seedTriangle]], graph.weldOf[graph.triangles[seedTriangle + 1]], graph.weldOf[graph.triangles[seedTriangle + 2]],
            };
            // メッシュの巻き方の表が、見えている側（frame の正面）と逆なら法線を裏返して使う（鏡像の Renderer・裏から見ている板）
            var seedNormal = graph.normals[seedVertices[0]] + graph.normals[seedVertices[1]] + graph.normals[seedVertices[2]];
            float side = Vector3.Dot(seedNormal, forward) < 0f ? -1f : 1f;

            var distance = new float[count];
            var finalized = new bool[count];
            var parent = new int[count];
            var coord = new Vector2[count];
            var axisX = new Vector3[count];
            var axisY = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                distance[i] = float.PositiveInfinity;
                parent[i] = -1;
            }
            var heap = new MinHeap();
            foreach (int s in seedVertices)
            {
                if (finalized[s]) continue;
                var offset = graph.positions[s] - seed;
                distance[s] = offset.magnitude;
                coord[s] = new Vector2(Vector3.Dot(offset, right), Vector3.Dot(offset, up));
                Transport(right, forward, NormalOf(graph, s, side, forward), out axisX[s], out axisY[s]);
                finalized[s] = true;
            }
            foreach (int s in seedVertices) Relax(graph, s, distance, finalized, parent, heap);

            while (heap.Count > 0)
            {
                heap.Pop(out float d, out int w);
                if (finalized[w] || d > distance[w]) continue;
                // 遠すぎる頂点は、親が範囲の中のときだけ座標を入れ（1 周ぶん）、そこから先へは広げない
                bool beyond = d > maxDistance;
                if (beyond && distance[parent[w]] > maxDistance) continue;
                finalized[w] = true;
                var normal = NormalOf(graph, w, side, forward);
                int p = parent[w];
                Transport(axisX[p], NormalOf(graph, p, side, forward), normal, out axisX[w], out axisY[w]);
                // 確定済みの隣それぞれから見た座標を、距離の逆数で平均する（親 1 つから決めるより、たどる道筋による揺れが小さい）
                var sum = Vector2.zero;
                float weight = 0f;
                var pw = graph.positions[w];
                foreach (int r in graph.neighbors[w])
                {
                    if (!finalized[r] || r == w) continue;
                    var delta = pw - graph.positions[r];
                    float length = delta.magnitude;
                    if (!(length > 0f)) continue;
                    float k = 1f / length;
                    sum += (coord[r] + new Vector2(Vector3.Dot(delta, axisX[r]), Vector3.Dot(delta, axisY[r]))) * k;
                    weight += k;
                }
                coord[w] = weight > 0f ? sum / weight : coord[p];
                if (!beyond) Relax(graph, w, distance, finalized, parent, heap);
            }

            for (int v = 0; v < coords.Length; v++)
            {
                int w = v < graph.weldOf.Length ? graph.weldOf[v] : -1;
                if (w >= 0 && finalized[w]) coords[v] = coord[w];
            }
            return true;
        }

        private static Vector3 NormalOf(SurfaceUnfoldGraph graph, int w, float side, Vector3 fallback)
        {
            var n = graph.normals[w] * side;
            return n.sqrMagnitude > 0.25f ? n : fallback;
        }

        private static void Relax(SurfaceUnfoldGraph graph, int v, float[] distance, bool[] finalized, int[] parent, MinHeap heap)
        {
            var pv = graph.positions[v];
            foreach (int w in graph.neighbors[v])
            {
                if (finalized[w]) continue;
                float d = distance[v] + (graph.positions[w] - pv).magnitude;
                if (d >= distance[w]) continue;
                distance[w] = d;
                parent[w] = v;
                heap.Push(d, w);
            }
        }

        /// <summary>
        /// 接平面の向き（x 軸 fromX、法線 fromNormal）を、法線が toNormal の頂点へ受け渡す: 法線の違いだけ回し、toNormal に直交させ直す。
        /// y 軸は Cross(法線, x 軸)（Unity の左手系で、右 × 上 = 正面になる並び）
        /// </summary>
        internal static void Transport(Vector3 fromX, Vector3 fromNormal, Vector3 toNormal, out Vector3 x, out Vector3 y)
        {
            x = Quaternion.FromToRotation(fromNormal, toNormal) * fromX;
            x -= toNormal * Vector3.Dot(x, toNormal);
            float length = x.magnitude;
            x = length > 1e-12f ? x / length : Vector3.Cross(toNormal, Mathf.Abs(toNormal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            y = Vector3.Cross(toNormal, x);
        }

        /// <summary>
        /// seed に一番近い三角形（graph.triangles の先頭からの位置）。facingOnly なら表（Cross(b - a, c - a)）が forward の側を向く三角形だけ、
        /// filter を渡せばそれを満たす三角形だけ見る。seedTolerance より遠ければ false
        /// </summary>
        private static bool TryFindSeedTriangle(SurfaceUnfoldGraph graph, Vector3 seed, float seedTolerance, Vector3 forward, bool facingOnly,
            Func<int, int, int, bool> filter, out int triangle)
        {
            triangle = -1;
            float best = seedTolerance * seedTolerance;
            for (int t = 0; t + 2 < graph.triangles.Count; t += 3)
            {
                if (filter != null && !filter(graph.triangles[t], graph.triangles[t + 1], graph.triangles[t + 2])) continue;
                var a = graph.positions[graph.weldOf[graph.triangles[t]]];
                var b = graph.positions[graph.weldOf[graph.triangles[t + 1]]];
                var c = graph.positions[graph.weldOf[graph.triangles[t + 2]]];
                if (facingOnly && Vector3.Dot(Vector3.Cross(b - a, c - a), forward) <= 0f) continue;
                float d = (ClosestPointOnTriangle(seed, a, b, c) - seed).sqrMagnitude;
                if (d > best) continue;
                best = d;
                triangle = t;
            }
            return triangle >= 0;
        }

        /// <summary>点 p に一番近い三角形 abc の上の点（Ericson「Real-Time Collision Detection」5.1.5 の方法）</summary>
        internal static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float denominator = 1f / (va + vb + vc);
            return a + ab * (vb * denominator) + ac * (vc * denominator);
        }

        /// <summary>ダイクストラ用の最小ヒープ（距離, 頂点）</summary>
        private sealed class MinHeap
        {
            private readonly List<(float key, int value)> _items = new List<(float, int)>();
            public int Count => _items.Count;

            public void Push(float key, int value)
            {
                _items.Add((key, value));
                int i = _items.Count - 1;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_items[parent].key <= key) break;
                    _items[i] = _items[parent];
                    i = parent;
                }
                _items[i] = (key, value);
            }

            public void Pop(out float key, out int value)
            {
                (key, value) = _items[0];
                var last = _items[_items.Count - 1];
                _items.RemoveAt(_items.Count - 1);
                if (_items.Count == 0) return;
                int i = 0;
                while (true)
                {
                    int left = i * 2 + 1;
                    if (left >= _items.Count) break;
                    int child = left + 1 < _items.Count && _items[left + 1].key < _items[left].key ? left + 1 : left;
                    if (_items[child].key >= last.key) break;
                    _items[i] = _items[child];
                    i = child;
                }
                _items[i] = last;
            }
        }
    }
}
