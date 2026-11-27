using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Picking;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.Pipeline
{
    /// <summary>
    /// グラデーション「毛先から」の毛先マップ: あるテクスチャをメインに持つ全スロットのメッシュを UV 空間に広げ、
    /// 各画素に「毛先からの面に沿った距離（R）」と「房の長さ（G）」（m、対象ルートのローカルの長さ）を書いた ARGBFloat の RT（A = 1 が描いた印）。
    /// 頂点ごとの値は TipDistance で求める（Renderer ごと。そのテクスチャを使うサブメッシュの三角形だけで房を分ける）。
    /// 描いた後は位置マップと同じ膨張（PositionMap.Fill）で、はみ出し幅の画素も埋める。
    /// 鍵は位置マップの中身を決める値（PositionMap.ContextHash）・大きさ・膨張回数・てっぺんで、Capacity 枚まで LRU で持つ。
    /// ヒエラルキーの変更・Undo/Redo と RecolorPreview.ClearCaches で全部捨てる
    /// </summary>
    internal static class TipMap
    {
        /// <summary>キャッシュに残す枚数の上限</summary>
        internal const int Capacity = 4;

        private static readonly int UvScaleOffsetId = Shader.PropertyToID("_UvScaleOffset");

        /// <summary>頂点の値を入れる UV チャネル（TipMap.shader の TEXCOORD3）</summary>
        private const int TipChannel = 3;

        private readonly struct Key : IEquatable<Key>
        {
            private readonly int _rootId;
            private readonly int _contextHash;
            private readonly int _width;
            private readonly int _height;
            private readonly int _fillIterations;
            private readonly Vector3 _top;

            public Key(int rootId, int contextHash, int width, int height, int fillIterations, Vector3 top)
            {
                _rootId = rootId;
                _contextHash = contextHash;
                _width = width;
                _height = height;
                _fillIterations = fillIterations;
                _top = top;
            }

            public bool Equals(Key other) =>
                _rootId == other._rootId && _contextHash == other._contextHash && _width == other._width && _height == other._height
                && _fillIterations == other._fillIterations && _top == other._top;

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((((_rootId * 397 ^ _contextHash) * 397 ^ _width) * 397 ^ _height) * 397 ^ _fillIterations) * 397 ^ _top.GetHashCode();
                }
            }
        }

        private sealed class Entry
        {
            public Key key;
            public RenderTexture texture;
        }

        /// <summary>古い順（末尾が最近使ったもの）</summary>
        private static readonly List<Entry> s_entries = new List<Entry>();

        /// <summary>キャッシュ中の枚数（テスト用）</summary>
        internal static int Count => s_entries.Count;

        [InitializeOnLoadMethod]
        private static void RegisterCacheClear()
        {
            // 姿勢・移動・親子の変更の後に古い毛先で塗らないよう、位置マップと同じ時に捨てる
            EditorApplication.hierarchyChanged += ClearCache;
            Undo.undoRedoPerformed += ClearCache;
        }

        /// <summary>
        /// texture をメインに持つ renderers のスロットの毛先マップ（width×height）を返す（無ければ作る）。top はてっぺん（対象ルートのローカル）。
        /// 返す RT はキャッシュ所有なので呼び出し側で解放しないこと（次に別の鍵で Capacity を超えると破棄されるので、受け取ったらすぐ使う）。
        /// 描くスロットが無い・描けない環境なら null
        /// </summary>
        internal static RenderTexture GetOrBuild(
            Transform root, IReadOnlyList<Renderer> renderers, Texture2D texture, int width, int height, int fillIterations, Vector3 top)
        {
            if (root == null || renderers == null || texture == null || width <= 0 || height <= 0) return null;
            var slots = PositionMap.CollectSlots(renderers, texture);
            if (slots.Count == 0) return null;
            fillIterations = Mathf.Clamp(fillIterations, 0, PositionMap.MaxFillIterations);

            var key = new Key(root.GetInstanceID(), PositionMap.ContextHash(root, renderers, texture), width, height, fillIterations, top);
            for (int i = 0; i < s_entries.Count; i++)
            {
                var entry = s_entries[i];
                if (!entry.key.Equals(key)) continue;
                if (entry.texture != null && entry.texture.IsCreated())
                {
                    s_entries.RemoveAt(i);
                    s_entries.Add(entry);
                    return entry.texture;
                }
                PositionMap.Destroy(entry.texture);
                s_entries.RemoveAt(i);
                break;
            }

            var rt = Build(root, slots, width, height, top);
            if (rt == null) return null;
            rt = PositionMap.Fill(rt, fillIterations);
            s_entries.Add(new Entry { key = key, texture = rt });
            while (s_entries.Count > Capacity)
            {
                PositionMap.Destroy(s_entries[0].texture);
                s_entries.RemoveAt(0);
            }
            return rt;
        }

        /// <summary>キャッシュした RT をすべて解放する</summary>
        internal static void ClearCache()
        {
            foreach (var entry in s_entries) PositionMap.Destroy(entry.texture);
            s_entries.Clear();
        }

        /// <summary>slots を毛先マップに描いた新しい RT を返す（呼び出し側の所有。PositionMap.Destroy で破棄）。描けない環境なら null</summary>
        internal static RenderTexture Build(Transform root, IReadOnlyList<PositionMap.Slot> slots, int width, int height, Vector3 top)
        {
            if (root == null || slots == null || width <= 0 || height <= 0) return null;
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)) return null;
            var shader = ShaderAssets.LoadShader(ShaderAssets.TipMapGuid);
            if (shader == null || !shader.isSupported) return null;

            var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBFloat, 0)
            {
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
                // 膨張（PositionMap.Fill）で ping-pong の書き込み先にもする
                enableRandomWrite = SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBFloat),
            };
            var rt = new RenderTexture(desc)
            {
                name = "ClickRecolor_TipMap",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!rt.Create())
            {
                Object.DestroyImmediate(rt);
                return null;
            }

            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var previous = RenderTexture.active;
            try
            {
                Graphics.SetRenderTarget(rt);
                GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
                GL.PushMatrix();
                try
                {
                    foreach (var group in GroupByRenderer(slots))
                    {
                        DrawRenderer(material, root, group, top);
                    }
                }
                finally
                {
                    GL.PopMatrix();
                }
            }
            catch
            {
                PositionMap.Destroy(rt);
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(material);
            }
            return rt;
        }

        /// <summary>同じ Renderer のスロットをまとめる（Renderer の出てきた順）</summary>
        private static List<List<PositionMap.Slot>> GroupByRenderer(IReadOnlyList<PositionMap.Slot> slots)
        {
            var groups = new List<List<PositionMap.Slot>>();
            var index = new Dictionary<Renderer, int>();
            foreach (var slot in slots)
            {
                if (slot.renderer == null) continue;
                if (!index.TryGetValue(slot.renderer, out int i))
                {
                    i = groups.Count;
                    index[slot.renderer] = i;
                    groups.Add(new List<PositionMap.Slot>());
                }
                groups[i].Add(slot);
            }
            return groups;
        }

        /// <summary>
        /// 1 つの Renderer のスロットを描く。今のポーズのメッシュ（位置マップと同じ取り方）で頂点ごとの値を求め、
        /// メッシュの複製の TEXCOORD3 に入れて描く（共有メッシュは書き換えない）
        /// </summary>
        private static void DrawRenderer(Material material, Transform root, List<PositionMap.Slot> slots, Vector3 top)
        {
            var submeshes = new List<int>();
            foreach (var slot in slots) submeshes.Add(slot.submesh);
            if (!TryComputeVertexTips(root, slots[0].renderer, submeshes, top, out var mesh, out var localToWorld, out bool ownsMesh, out var result))
            {
                return;
            }
            Mesh drawMesh = null;
            try
            {
                drawMesh = ownsMesh ? mesh : Object.Instantiate(mesh);
                drawMesh.hideFlags = HideFlags.HideAndDontSave;
                int vertexCount = result.distance.Length;
                var tips = new List<Vector2>(vertexCount);
                for (int i = 0; i < vertexCount; i++) tips.Add(new Vector2(result.distance[i], result.length[i]));
                drawMesh.SetUVs(TipChannel, tips);

                foreach (var slot in slots)
                {
                    if (slot.submesh >= drawMesh.subMeshCount) continue;
                    material.SetVector(UvScaleOffsetId, new Vector4(slot.uvScale.x, slot.uvScale.y, slot.uvOffset.x, slot.uvOffset.y));
                    material.SetPass(0);
                    Graphics.DrawMeshNow(drawMesh, localToWorld, slot.submesh);
                }
            }
            finally
            {
                if (drawMesh != null && drawMesh != mesh) Object.DestroyImmediate(drawMesh);
                if (ownsMesh && mesh != null) Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// サブメッシュの三角形の並び triangles の triangle 番目の三角形で、重心座標 barycentric の位置の毛先からの距離と房の長さ（頂点の値を混ぜる）。
        /// 番号が範囲外なら false
        /// </summary>
        internal static bool TrySampleTriangle(in TipDistance.Result result, int[] triangles, int triangle, Vector3 barycentric,
            out float distance, out float length)
        {
            distance = 0f;
            length = 0f;
            if (result.distance == null || triangles == null || triangle < 0 || triangle * 3 + 2 >= triangles.Length) return false;
            int a = triangles[triangle * 3], b = triangles[triangle * 3 + 1], c = triangles[triangle * 3 + 2];
            int count = result.distance.Length;
            if (a < 0 || a >= count || b < 0 || b >= count || c < 0 || c >= count) return false;
            distance = result.distance[a] * barycentric.x + result.distance[b] * barycentric.y + result.distance[c] * barycentric.z;
            length = result.length[a] * barycentric.x + result.length[b] * barycentric.y + result.length[c] * barycentric.z;
            return true;
        }

        /// <summary>
        /// renderer の今のポーズのメッシュ（位置マップと同じ取り方）で、submeshes の三角形を房に分けた頂点ごとの毛先からの距離と房の長さを求める。
        /// mesh は ownsMesh なら呼び出し側で破棄する。読み取り不可・頂点無しなら false（mesh は破棄済み）
        /// </summary>
        internal static bool TryComputeVertexTips(Transform root, Renderer renderer, IReadOnlyList<int> submeshes, Vector3 top,
            out Mesh mesh, out Matrix4x4 localToWorld, out bool ownsMesh, out TipDistance.Result result)
        {
            result = default;
            if (!MeshRaycaster.TryGetMeshData(renderer, out mesh, out localToWorld, out ownsMesh)) return false;
            // 頂点を CPU で読むので、読み取り不可のメッシュ（Read/Write 無効の MeshRenderer）は扱わない（毛先マップではその部分は t = 0 ＝色 1）。
            // SkinnedMeshRenderer は BakeMesh の結果なので読める
            var vertices = mesh.isReadable ? mesh.vertices : null;
            if (vertices == null || vertices.Length == 0)
            {
                if (ownsMesh) Object.DestroyImmediate(mesh);
                mesh = null;
                return false;
            }
            var toRoot = root.worldToLocalMatrix * localToWorld;
            var positions = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) positions[i] = toRoot.MultiplyPoint3x4(vertices[i]);

            // このテクスチャを使うサブメッシュの三角形だけで房を分ける（同じメッシュの別の素材の面とつながって房が広がらないように）
            var triangles = new List<int>();
            var seen = new HashSet<int>();
            foreach (int submesh in submeshes)
            {
                if (submesh < 0 || submesh >= mesh.subMeshCount || !seen.Add(submesh)) continue;
                triangles.AddRange(mesh.GetTriangles(submesh));
            }
            result = TipDistance.Compute(positions, triangles, top);
            return true;
        }
    }
}
