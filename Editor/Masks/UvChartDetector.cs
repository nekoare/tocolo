// 流用元: com.nekoare.mask-creation-tool/Editor/Processing/IslandSelector.cs（GetUVChartTriangles と UV 量子化・エッジキー）
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>
    /// サブメッシュ内ローカル三角形番号 → UV チャート番号 の表。
    /// チャート番号は「そのチャートで最も小さい三角形番号」の昇順に 0 から振る（決定的）
    /// </summary>
    internal sealed class ChartTable
    {
        public readonly int[] chartOfTriangle;
        public readonly int chartCount;

        public ChartTable(int[] chartOfTriangle, int chartCount)
        {
            this.chartOfTriangle = chartOfTriangle;
            this.chartCount = chartCount;
        }

        /// <summary>chartId に属する三角形番号を昇順で result に追加する</summary>
        public void CollectTriangles(int chartId, List<int> result)
        {
            for (int t = 0; t < chartOfTriangle.Length; t++)
            {
                if (chartOfTriangle[t] == chartId) result.Add(t);
            }
        }
    }

    /// <summary>
    /// UV エッジを共有する三角形どうしを連結した「UV チャート」を求める。
    /// 頂点番号ではなく UV 座標（1e-5 で量子化）で辺を比べるので、同じ頂点でも UV が別なら別チャート、
    /// 頂点が複製されていても UV が一致していれば同じチャートになる
    /// </summary>
    internal static class UvChartDetector
    {
        private const float UvQuant = 100000f;

        private struct CacheEntry
        {
            public int fingerprint;
            public ChartTable table;
        }

        // キーは (メッシュの InstanceID, サブメッシュ)。フィンガープリントは値側で照合する
        // （メッシュ破棄後に InstanceID が再利用されても、形が違えば作り直す。古い形の表を溜め込まない）
        private static readonly Dictionary<(int meshId, int submesh), CacheEntry> s_cache =
            new Dictionary<(int meshId, int submesh), CacheEntry>();

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            AssemblyReloadEvents.beforeAssemblyReload += ClearCache;
            EditorSceneManager.sceneClosing += (scene, removingScene) => ClearCache();
        }

        internal static void ClearCache() => s_cache.Clear();

        /// <summary>
        /// 全三角形のチャート番号を一括で求める。uv はメッシュの UV0、triangles はサブメッシュの GetTriangles 結果。
        /// </summary>
        internal static ChartTable Build(Vector2[] uv, int[] triangles)
        {
            if (uv == null) throw new ArgumentNullException(nameof(uv));
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));

            int triCount = triangles.Length / 3;
            var parent = new int[triCount];
            for (int t = 0; t < triCount; t++) parent[t] = t;

            // UV エッジ → 最初にその辺を持った三角形。2 つ目以降は最初の三角形と結合する
            var firstTriangleOfEdge = new Dictionary<(long lo, long hi), int>(triCount * 3);
            for (int t = 0; t < triCount; t++)
            {
                int t3 = t * 3;
                Vector2 a = uv[triangles[t3]];
                Vector2 b = uv[triangles[t3 + 1]];
                Vector2 c = uv[triangles[t3 + 2]];
                LinkEdge(firstTriangleOfEdge, parent, EdgeKey(a, b), t);
                LinkEdge(firstTriangleOfEdge, parent, EdgeKey(b, c), t);
                LinkEdge(firstTriangleOfEdge, parent, EdgeKey(c, a), t);
            }

            // 三角形番号順に走査し、初めて出てきた根に次の番号を振る
            var chartOfRoot = new Dictionary<int, int>();
            var chartOfTriangle = new int[triCount];
            for (int t = 0; t < triCount; t++)
            {
                int root = Find(parent, t);
                if (!chartOfRoot.TryGetValue(root, out int chart))
                {
                    chart = chartOfRoot.Count;
                    chartOfRoot.Add(root, chart);
                }
                chartOfTriangle[t] = chart;
            }
            return new ChartTable(chartOfTriangle, chartOfRoot.Count);
        }

        /// <summary>
        /// キャッシュ鍵に使うメッシュの形の指紋（頂点数・サブメッシュ数・三角形総数・UV 配列長）。
        /// 読み取り不可のメッシュでも計算できる（配列を取り出さない）
        /// </summary>
        internal static int Fingerprint(Mesh m)
        {
            if (m == null) return 0;
            long triangleTotal = 0;
            for (int i = 0; i < m.subMeshCount; i++) triangleTotal += (long)m.GetIndexCount(i) / 3;
            int uvLength = m.HasVertexAttribute(VertexAttribute.TexCoord0) ? m.vertexCount : 0;
            unchecked
            {
                int h = 17;
                h = h * 31 + m.vertexCount;
                h = h * 31 + m.subMeshCount;
                h = h * 31 + (int)triangleTotal;
                h = h * 31 + (int)(triangleTotal >> 32);
                h = h * 31 + uvLength;
                return h;
            }
        }

        /// <summary>
        /// (mesh, submesh) のチャート表をキャッシュから返す（無ければ作る）。
        /// 読み取り不可・範囲外のサブメッシュ・三角形以外のトポロジー・UV 無しなら null
        /// </summary>
        internal static ChartTable GetOrBuild(Mesh mesh, int submesh)
        {
            if (mesh == null || !mesh.isReadable) return null;
            if (submesh < 0 || submesh >= mesh.subMeshCount) return null;
            if (mesh.GetTopology(submesh) != MeshTopology.Triangles) return null;

            var key = (mesh.GetInstanceID(), submesh);
            int fingerprint = Fingerprint(mesh);
            if (s_cache.TryGetValue(key, out var entry) && entry.fingerprint == fingerprint) return entry.table;

            var uv = mesh.uv;
            if (uv == null || uv.Length != mesh.vertexCount) return null;
            var table = Build(uv, mesh.GetTriangles(submesh));
            s_cache[key] = new CacheEntry { fingerprint = fingerprint, table = table };
            return table;
        }

        private static long QuantizeUv(Vector2 uv)
        {
            // UV を 10 万倍して int に丸め、2 つを 32bit ずつ詰めて 64bit キーにする
            int qx = Mathf.RoundToInt(uv.x * UvQuant);
            int qy = Mathf.RoundToInt(uv.y * UvQuant);
            return ((long)(uint)qx << 32) | (uint)qy;
        }

        /// <summary>
        /// 向きに依存しない辺のキー。流用元はハッシュ合成で 64bit に畳んでいたが、
        /// 衝突すると無関係なチャートが繋がるので 2 端点をそのまま組で持つ
        /// </summary>
        private static (long lo, long hi) EdgeKey(Vector2 a, Vector2 b)
        {
            long ka = QuantizeUv(a);
            long kb = QuantizeUv(b);
            return ka < kb ? (ka, kb) : (kb, ka);
        }

        private static void LinkEdge(Dictionary<(long lo, long hi), int> firstTriangleOfEdge, int[] parent, (long lo, long hi) key, int triangle)
        {
            if (firstTriangleOfEdge.TryGetValue(key, out int other))
            {
                Union(parent, other, triangle);
            }
            else
            {
                firstTriangleOfEdge.Add(key, triangle);
            }
        }

        private static int Find(int[] parent, int x)
        {
            // 経路半減（再帰しないので巨大メッシュでもスタックを使わない）
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a);
            int rb = Find(parent, b);
            if (ra == rb) return;
            // 小さい番号を根にする（番号の振り方は後段の走査で決まるので、ここは決定的であれば何でもよい）
            if (ra < rb) parent[rb] = ra;
            else parent[ra] = rb;
        }
    }
}
