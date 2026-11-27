using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Pipeline
{
    /// <summary>
    /// グラデーション「毛先から」の頂点ごとの値: 毛先からの距離（面に沿った最短距離、m）と、その頂点が属する房の長さ（m）。
    /// 房 = 三角形でつながった頂点のまとまり（同じ位置の頂点は 1 つとみなす）。
    /// 毛先 = 房の中で「てっぺん」から一番遠い頂点と、それより TipBand 以内しか近くない頂点。房の長さ = 房の中の毛先からの距離の最大
    /// </summary>
    internal static class TipDistance
    {
        /// <summary>
        /// 毛先とみなす幅（m）。毛先の縁は幅のある辺なので、一番遠い 1 点だけから測ると縁の反対側が「毛先から幅ぶん離れた所」になる
        /// </summary>
        internal const float TipBand = 0.005f;

        /// <summary>同じ位置とみなす格子の細かさ（1/m）。UV の継ぎ目や法線の分かれ目で分かれた頂点をつなぐ</summary>
        private const float WeldScale = 1e5f;

        internal readonly struct Result
        {
            /// <summary>元の頂点ごとの毛先からの距離（m）。三角形に使われていない頂点は 0</summary>
            public readonly float[] distance;

            /// <summary>元の頂点ごとの房の長さ（m）。三角形に使われていない頂点は 0</summary>
            public readonly float[] length;

            public Result(float[] distance, float[] length)
            {
                this.distance = distance;
                this.length = length;
            }
        }

        /// <summary>positions（任意の座標系）と triangles（3 つずつの頂点番号）から、top を「てっぺん」として頂点ごとの値を求める</summary>
        internal static Result Compute(IReadOnlyList<Vector3> positions, IReadOnlyList<int> triangles, Vector3 top)
        {
            int n = positions.Count;
            var distance = new float[n];
            var length = new float[n];
            if (n == 0 || triangles == null || triangles.Count < 3) return new Result(distance, length);

            var weld = Weld(positions);
            var graph = BuildGraph(positions, triangles, weld);
            var island = Islands(graph, out int islandCount);

            var welded = new List<int>(graph.Count);
            foreach (var v in graph.Keys) welded.Add(v);

            // 房ごとのてっぺんから一番遠い距離
            var farthest = new float[islandCount];
            foreach (var v in welded)
            {
                float r = Vector3.Distance(positions[v], top);
                if (r > farthest[island[v]]) farthest[island[v]] = r;
            }

            var dist = new Dictionary<int, float>(welded.Count);
            var heap = new MinHeap();
            foreach (var v in welded)
            {
                bool tip = Vector3.Distance(positions[v], top) >= farthest[island[v]] - TipBand;
                dist[v] = tip ? 0f : float.PositiveInfinity;
                if (tip) heap.Push(0f, v);
            }
            // 房どうしはつながっていないので、全房の毛先から同時に測っても各房の中だけで広がる
            while (heap.Count > 0)
            {
                var (d, v) = heap.Pop();
                if (d > dist[v]) continue;
                foreach (var edge in graph[v])
                {
                    float nd = d + edge.length;
                    if (nd < dist[edge.to])
                    {
                        dist[edge.to] = nd;
                        heap.Push(nd, edge.to);
                    }
                }
            }

            var islandLength = new float[islandCount];
            foreach (var v in welded)
            {
                if (dist[v] > islandLength[island[v]]) islandLength[island[v]] = dist[v];
            }

            for (int i = 0; i < n; i++)
            {
                int v = weld[i];
                if (!dist.TryGetValue(v, out var d)) continue;
                distance[i] = d;
                length[i] = islandLength[island[v]];
            }
            return new Result(distance, length);
        }

        /// <summary>頂点ごとの代表の頂点番号（同じ位置の頂点のうち最初のもの）</summary>
        private static int[] Weld(IReadOnlyList<Vector3> positions)
        {
            var weld = new int[positions.Count];
            var first = new Dictionary<Vector3Int, int>(positions.Count);
            for (int i = 0; i < positions.Count; i++)
            {
                var p = positions[i];
                var key = new Vector3Int(Mathf.RoundToInt(p.x * WeldScale), Mathf.RoundToInt(p.y * WeldScale), Mathf.RoundToInt(p.z * WeldScale));
                if (!first.TryGetValue(key, out var rep))
                {
                    rep = i;
                    first[key] = i;
                }
                weld[i] = rep;
            }
            return weld;
        }

        private readonly struct Edge
        {
            public readonly int to;
            public readonly float length;

            public Edge(int to, float length)
            {
                this.to = to;
                this.length = length;
            }
        }

        /// <summary>代表の頂点どうしの辺（三角形の辺、重複あり）。三角形に使われた代表の頂点だけがキーになる</summary>
        private static Dictionary<int, List<Edge>> BuildGraph(IReadOnlyList<Vector3> positions, IReadOnlyList<int> triangles, int[] weld)
        {
            var graph = new Dictionary<int, List<Edge>>();
            int n = positions.Count;
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if ((uint)a >= (uint)n || (uint)b >= (uint)n || (uint)c >= (uint)n) continue;
                a = weld[a];
                b = weld[b];
                c = weld[c];
                AddEdge(graph, positions, a, b);
                AddEdge(graph, positions, b, c);
                AddEdge(graph, positions, c, a);
            }
            return graph;
        }

        private static void AddEdge(Dictionary<int, List<Edge>> graph, IReadOnlyList<Vector3> positions, int a, int b)
        {
            if (!graph.TryGetValue(a, out var la)) graph[a] = la = new List<Edge>();
            if (!graph.TryGetValue(b, out var lb)) graph[b] = lb = new List<Edge>();
            if (a == b) return;
            float d = Vector3.Distance(positions[a], positions[b]);
            la.Add(new Edge(b, d));
            lb.Add(new Edge(a, d));
        }

        /// <summary>代表の頂点 → 房の番号（0..count-1）</summary>
        private static Dictionary<int, int> Islands(Dictionary<int, List<Edge>> graph, out int count)
        {
            var island = new Dictionary<int, int>(graph.Count);
            var stack = new Stack<int>();
            count = 0;
            foreach (var start in graph.Keys)
            {
                if (island.ContainsKey(start)) continue;
                island[start] = count;
                stack.Push(start);
                while (stack.Count > 0)
                {
                    int v = stack.Pop();
                    foreach (var edge in graph[v])
                    {
                        if (island.ContainsKey(edge.to)) continue;
                        island[edge.to] = count;
                        stack.Push(edge.to);
                    }
                }
                count++;
            }
            return island;
        }

        /// <summary>距離の小さい順に取り出す二分ヒープ（同じ頂点を何度入れてもよい。古い値は取り出した側で読み飛ばす）</summary>
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
                    if (_items[parent].key <= _items[i].key) break;
                    (_items[parent], _items[i]) = (_items[i], _items[parent]);
                    i = parent;
                }
            }

            public (float key, int value) Pop()
            {
                var top = _items[0];
                int last = _items.Count - 1;
                _items[0] = _items[last];
                _items.RemoveAt(last);
                int i = 0;
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, smallest = i;
                    if (l < _items.Count && _items[l].key < _items[smallest].key) smallest = l;
                    if (r < _items.Count && _items[r].key < _items[smallest].key) smallest = r;
                    if (smallest == i) break;
                    (_items[smallest], _items[i]) = (_items[i], _items[smallest]);
                    i = smallest;
                }
                return top;
            }
        }
    }
}
