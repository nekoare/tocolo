// 流用元: dev.nekoare.tex-col-adjuster/Editor/TexColAdjusterWindow.ScenePick.cs
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.Picking
{
    public struct PickHit
    {
        public Renderer renderer;
        public int subMeshIndex;
        public int materialSlot;
        public int triangleIndex;
        public Vector3 barycentric;
        public Vector2 uv0;
        /// <summary>Tiling/Offset 適用後（hasMainTexture のときだけ有効）</summary>
        public Vector2 uv;
        public Vector2Int texel;
        public bool hasMainTexture;
        public MainTextureInfo mainTexture;
        public Material material;
        public Vector3 worldPosition;
        public Vector3 worldNormal;
        public float distance;
    }

    /// <summary>
    /// アバター配下の Renderer 群にレイを当て、最も手前の「見えている」面を返す。
    /// メインテクスチャの無いマテリアルの面は色を変えられないので素通りして奥の面を拾う（FakeShadow など、前髪と同じ形を重ねて描く
    /// テクスチャ無しのマテリアルが手前と判定されて前髪を選べなかった。透過で作られたものもあるので名前でなくテクスチャの有無で見る。ユーザー判断 2026-10-04）。
    /// Scene ビューには依存しない（レイと Renderer を渡す）。
    /// SkinnedMeshRenderer の BakeMesh は重いので短時間キャッシュする。
    /// </summary>
    public sealed class ScenePicker : IDisposable
    {
        /// <summary>透明とみなすアルファのしきい値。これ未満の交点はスキップして奥の面を拾う（カットアウトの髪など）</summary>
        public const float AlphaThreshold = 0.5f;
        private const double BakeCacheSeconds = 0.5;

        private sealed class CacheEntry
        {
            public Mesh mesh;
            public Matrix4x4 localToWorld;
            public bool ownsMesh;
            public double bakedTime;
            /// <summary>判定用の配列（焼いたメッシュはここに、MeshRenderer のメッシュは _meshArrays に持つ）</summary>
            public MeshRaycaster.MeshArrays arrays;
        }

        private readonly Dictionary<Renderer, CacheEntry> _cache = new Dictionary<Renderer, CacheEntry>();
        /// <summary>
        /// MeshRenderer の共有メッシュから取り出した配列（鍵: メッシュの InstanceID、形の指紋が変わったら取り直す）。
        /// `mesh.vertices` 等は毎回コピーを返すので、ホバー判定のたびに取り直すと GC の山になる（レビュー指摘 2026-09-25）
        /// </summary>
        private readonly Dictionary<int, (int fingerprint, MeshRaycaster.MeshArrays arrays)> _meshArrays =
            new Dictionary<int, (int, MeshRaycaster.MeshArrays)>();

        public ScenePicker()
        {
            // ドメインリロードでは所有する焼きメッシュ（HideAndDontSave）が捨てられないので、ここで片付ける
            AssemblyReloadEvents.beforeAssemblyReload += ClearCache;
        }
        private readonly List<MeshRaycaster.RaycastHit> _hits = new List<MeshRaycaster.RaycastHit>();
        /// <summary>_hits の添字（= 追加順）。距離が同じヒットは追加順で並べて結果を決定的にする（共有エッジ上のクリックなど）</summary>
        private readonly List<int> _order = new List<int>();
        /// <summary>読み取り不可の警告を出済みの Renderer（InstanceID）。クリックのたびに同じ警告を出さないため</summary>
        private readonly HashSet<int> _warnedUnreadable = new HashSet<int>();

        /// <summary>直前の TryPick で、メインテクスチャの無い面を素通りしたか（何も拾えなかったときの案内用）</summary>
        public bool LastSkippedTextureless { get; private set; }

        /// <summary>直前の TryPick で素通りしたテクスチャ無しの面のうち、最も手前の距離（素通りしていなければ +∞）</summary>
        public float LastSkippedTexturelessDistance { get; private set; } = float.PositiveInfinity;

        /// <param name="alphaSampler">ヒット位置のテクスチャ α を返す（null なら透明スキップをしない）</param>
        public bool TryPick(Ray ray, IReadOnlyList<Renderer> renderers, Func<PickHit, float> alphaSampler, out PickHit result)
        {
            result = default;
            LastSkippedTextureless = false;
            LastSkippedTexturelessDistance = float.PositiveInfinity;
            _hits.Clear();
            for (int i = 0; i < renderers.Count; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || !renderer.bounds.IntersectRay(ray)) continue;
                var entry = GetMeshData(renderer);
                if (entry == null) continue;
                MeshRaycaster.RaycastMeshAll(ray, GetArrays(entry), entry.localToWorld, renderer, _hits);
            }
            if (_hits.Count == 0) return false;
            _order.Clear();
            for (int i = 0; i < _hits.Count; i++) _order.Add(i);
            _order.Sort((a, b) =>
            {
                int c = _hits[a].distance.CompareTo(_hits[b].distance);
                return c != 0 ? c : a.CompareTo(b);
            });

            foreach (int index in _order)
            {
                var candidate = Build(_hits[index]);
                // lilToon の付属（FakeShadow・Overlay・FurOnly）は本体に重ねて描く物なので、選ぶ対象にも「手前のテクスチャ無しの物」にもしない
                // （少し手前にずらして置いた FakeShadow で前髪のクリックが止まらないように。実機報告 2026-10-05）
                if (candidate.material != null && Decal.DecalOverlayMaterial.IsIgnorableLilToonAuxiliary(candidate.material)) continue;
                if (!candidate.hasMainTexture)
                {
                    if (!LastSkippedTextureless) LastSkippedTexturelessDistance = candidate.distance;
                    LastSkippedTextureless = true;
                    continue;
                }
                if (alphaSampler != null && candidate.hasMainTexture && alphaSampler(candidate) < AlphaThreshold) continue;
                result = candidate;
                return true;
            }
            return false;
        }

        private static PickHit Build(in MeshRaycaster.RaycastHit hit)
        {
            var materials = hit.renderer.sharedMaterials;
            // Unity は余ったサブメッシュを最後のマテリアルで描くので Min で丸める
            int slot = materials.Length == 0 ? -1 : Mathf.Min(hit.subMeshIndex, materials.Length - 1);
            slot = ChooseOverdrawSlot(hit.renderer, hit.subMeshIndex, materials, slot);
            var pick = new PickHit
            {
                renderer = hit.renderer,
                subMeshIndex = hit.subMeshIndex,
                materialSlot = slot,
                triangleIndex = hit.triangleIndex,
                barycentric = hit.barycentric,
                uv0 = hit.uv,
                worldPosition = hit.worldPosition,
                worldNormal = hit.worldNormal,
                distance = hit.distance,
                material = slot >= 0 ? materials[slot] : null,
            };
            if (pick.material != null && MaterialTextureResolver.TryGetMainTexture(pick.material, out var info))
            {
                pick.hasMainTexture = true;
                pick.mainTexture = info;
                pick.uv = MaterialTextureResolver.ToTextureCoord(info, hit.uv);
                pick.texel = MaterialTextureResolver.ToTexel(info, pick.uv);
            }
            return pick;
        }

        /// <summary>
        /// マテリアル枠がサブメッシュより多いと、Unity は余った枠で最後のサブメッシュを重ね描きする（lilToon の FakeShadow をよくこう付ける）。
        /// 最後のサブメッシュに当たったときは、その三角形を描いている枠（サブメッシュ番号の枠〜最後の枠）のうち、
        /// lilToon の付属でなくメインテクスチャのある最初の枠を採る。無ければ slot のまま。
        /// 枠が [_FakeShadow, 髪] の順だと、サブメッシュ番号の枠だけを見て FakeShadow を選び、前髪が選べなかった（実機報告 2026-10-05）
        /// </summary>
        internal static int ChooseOverdrawSlot(Renderer renderer, int subMeshIndex, Material[] materials, int slot)
        {
            var mesh = RendererMeshAccess.GetSharedMesh(renderer);
            if (mesh == null) return slot;
            int subMeshCount = mesh.subMeshCount;
            if (subMeshCount <= 0 || subMeshIndex != subMeshCount - 1 || materials.Length <= subMeshCount) return slot;
            for (int s = subMeshIndex; s < materials.Length; s++)
            {
                var material = materials[s];
                if (material == null || Decal.DecalOverlayMaterial.IsIgnorableLilToonAuxiliary(material)) continue;
                if (MaterialTextureResolver.TryGetMainTexture(material, out _)) return s;
            }
            return slot;
        }

        /// <summary>
        /// 判定用メッシュを返す。SkinnedMeshRenderer の焼きメッシュだけ短時間キャッシュする。
        /// MeshRenderer は sharedMesh の差し替えを検知できないのでキャッシュせず毎回取り直す（参照するだけで所有しない）。
        /// </summary>
        private CacheEntry GetMeshData(Renderer renderer)
        {
            // SMR は焼いた一時 Mesh が常に読み取り可能なので、元メッシュで Read/Write を判定する
            var source = RendererMeshAccess.GetSharedMesh(renderer);
            if (source != null && !source.isReadable)
            {
                if (_warnedUnreadable.Add(renderer.GetInstanceID()))
                {
                    Debug.LogWarning($"[Tocolo] '{renderer.name}'のメッシュはRead/Writeが無効なためSceneでクリックできません。メッシュのインポート設定でRead/Writeを有効にしてください", renderer);
                }
                return null;
            }

            double now = EditorApplication.timeSinceStartup;
            if (_cache.TryGetValue(renderer, out var entry))
            {
                if (entry.mesh != null && now - entry.bakedTime < BakeCacheSeconds) return entry;
                if (entry.ownsMesh && entry.mesh != null) Object.DestroyImmediate(entry.mesh);
                _cache.Remove(renderer);
            }
            if (!MeshRaycaster.TryGetMeshData(renderer, out var mesh, out var localToWorld, out bool ownsMesh)) return null;
            entry = new CacheEntry { mesh = mesh, localToWorld = localToWorld, ownsMesh = ownsMesh, bakedTime = now };
            if (ownsMesh) _cache[renderer] = entry;
            return entry;
        }

        /// <summary>判定用の配列。焼いたメッシュは項目に 1 回だけ取り出し、共有メッシュは _meshArrays から引く</summary>
        private MeshRaycaster.MeshArrays GetArrays(CacheEntry entry)
        {
            if (entry.ownsMesh)
            {
                return entry.arrays ??= MeshRaycaster.MeshArrays.From(entry.mesh);
            }
            int id = entry.mesh.GetInstanceID();
            int fingerprint = Masks.UvChartDetector.Fingerprint(entry.mesh);
            if (_meshArrays.TryGetValue(id, out var cached) && cached.fingerprint == fingerprint && cached.arrays != null)
            {
                return cached.arrays;
            }
            var arrays = MeshRaycaster.MeshArrays.From(entry.mesh);
            _meshArrays[id] = (fingerprint, arrays);
            return arrays;
        }

        /// <summary>
        /// 判定用と同じメッシュ（SkinnedMeshRenderer は焼いたもの。短時間キャッシュを共用）と変換行列を返す。
        /// 矩形選択の島 ID 描画（IslandRectPicker）で、レイ判定と同じ形・同じ三角形の並びで描くために使う。
        /// mesh は ScenePicker が所有するので、呼び出し側で破棄しないこと。読み取り不可のメッシュは false
        /// </summary>
        internal bool TryGetMeshData(Renderer renderer, out Mesh mesh, out Matrix4x4 localToWorld)
        {
            var entry = renderer != null ? GetMeshData(renderer) : null;
            mesh = entry?.mesh;
            localToWorld = entry?.localToWorld ?? Matrix4x4.identity;
            return mesh != null;
        }

        /// <summary>判定用の配列（TryGetMeshData と同じメッシュ・同じ三角形の並び）と変換行列。読み取り不可なら false</summary>
        internal bool TryGetMeshArrays(Renderer renderer, out MeshRaycaster.MeshArrays arrays, out Matrix4x4 localToWorld)
        {
            var entry = renderer != null ? GetMeshData(renderer) : null;
            arrays = entry != null ? GetArrays(entry) : null;
            localToWorld = entry?.localToWorld ?? Matrix4x4.identity;
            return arrays != null;
        }

        public void ClearCache()
        {
            foreach (var entry in _cache.Values)
            {
                if (entry.ownsMesh && entry.mesh != null) Object.DestroyImmediate(entry.mesh);
            }
            _cache.Clear();
            _meshArrays.Clear();
            _warnedUnreadable.Clear();
        }

        public void Dispose()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= ClearCache;
            ClearCache();
        }
    }
}
