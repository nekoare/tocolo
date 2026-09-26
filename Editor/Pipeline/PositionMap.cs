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
    /// グラデーション用の位置マップ: あるテクスチャをメインに持つ全スロットのメッシュを UV 空間に広げ、
    /// 各画素に「そこに写る表面の位置（対象ルートのローカル座標）」を書いた ARGBFloat の RT（A = 1 が描いた印、描かれない画素は (0,0,0,0)）。
    /// ColorShift.compute がこれを引いて、箱の中の高さで目標色を混ぜる。
    /// SkinnedMeshRenderer はその時点のポーズで焼く（編集中は Scene の今のポーズ、ビルドではビルド時点のポーズ）。
    /// 描いた後、描かれていない画素を近傍の位置で埋める膨張を N 回掛ける（はみ出し幅の画素も箱の中の高さで色が決まるように）。
    /// 鍵は (対象ルート, 描くスロットの組と各 Transform・ボーンの行列, 大きさ, 膨張回数) で、Capacity 枚まで LRU で持つ。
    /// ヒエラルキーの変更・Undo/Redo と RecolorPreview.ClearCaches（リロード・シーン切替）で全部捨てる
    /// </summary>
    internal static class PositionMap
    {
        /// <summary>キャッシュに残す枚数の上限</summary>
        internal const int Capacity = 4;

        /// <summary>膨張の回数の上限（はみ出し幅の上限 32 ＋ 2）</summary>
        internal const int MaxFillIterations = 34;

        /// <summary>鍵に畳み込む SkinnedMeshRenderer のボーンの本数の上限（多いボーンを毎回全部見ない）</summary>
        private const int MaxHashedBones = 16;

        private const int FillThreadGroupSize = 8;

        private static ComputeShader s_fill;
        private static int s_kernelFill;
        private static bool s_warnedFillUnavailable;

        private static readonly int FillInId = Shader.PropertyToID("In");
        private static readonly int FillOutId = Shader.PropertyToID("Out");
        private static readonly int FillWidthId = Shader.PropertyToID("Width");
        private static readonly int FillHeightId = Shader.PropertyToID("Height");

        private static readonly int RootWorldToLocalId = Shader.PropertyToID("_RootWorldToLocal");
        private static readonly int UvScaleOffsetId = Shader.PropertyToID("_UvScaleOffset");

        /// <summary>位置マップに描くスロット 1 つ（Renderer・サブメッシュ・マテリアルの Tiling/Offset）</summary>
        internal readonly struct Slot
        {
            public readonly Renderer renderer;
            public readonly int submesh;
            public readonly Vector2 uvScale;
            public readonly Vector2 uvOffset;

            public Slot(Renderer renderer, int submesh, Vector2 uvScale, Vector2 uvOffset)
            {
                this.renderer = renderer;
                this.submesh = submesh;
                this.uvScale = uvScale;
                this.uvOffset = uvOffset;
            }
        }

        private readonly struct Key : IEquatable<Key>
        {
            private readonly int _rootId;
            private readonly int _slotsHash;
            private readonly int _width;
            private readonly int _height;
            private readonly int _fillIterations;

            public Key(int rootId, int slotsHash, int width, int height, int fillIterations)
            {
                _rootId = rootId;
                _slotsHash = slotsHash;
                _width = width;
                _height = height;
                _fillIterations = fillIterations;
            }

            public bool Equals(Key other) =>
                _rootId == other._rootId && _slotsHash == other._slotsHash && _width == other._width && _height == other._height
                && _fillIterations == other._fillIterations;

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode()
            {
                unchecked { return (((_rootId * 397 ^ _slotsHash) * 397 ^ _width) * 397 ^ _height) * 397 ^ _fillIterations; }
            }
        }

        private sealed class Entry
        {
            public Key key;
            public RenderTexture texture;
        }

        /// <summary>古い順（末尾が最近使ったもの）</summary>
        private static readonly List<Entry> s_entries = new List<Entry>();

        private static bool s_warnedUnavailable;

        /// <summary>キャッシュ中の枚数（テスト用）</summary>
        internal static int Count => s_entries.Count;

        /// <summary>位置マップを描けるか（ARGBFloat の RT・シェーダーが使える）</summary>
        internal static bool IsAvailable
        {
            get
            {
                if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)) return false;
                var shader = ShaderAssets.LoadShader(ShaderAssets.PositionMapGuid);
                return shader != null && shader.isSupported;
            }
        }

        [InitializeOnLoadMethod]
        private static void RegisterCacheClear()
        {
            // 姿勢・移動・親子の変更の後に古い位置で塗らないよう、ヒエラルキーの変更と Undo/Redo で捨てる
            // （鍵にも行列を入れているが、鍵に入らないボーン（17 本目以降）の変化もここで拾う）
            EditorApplication.hierarchyChanged += ClearCache;
            Undo.undoRedoPerformed += ClearCache;
        }

        /// <summary>
        /// texture をメインに持つ renderers のスロットの位置マップ（width×height）を返す（無ければ作る）。
        /// fillIterations 回だけ膨張を掛ける（0 なら掛けない。MaxFillIterations で切り詰める）。
        /// 返す RT はキャッシュ所有なので呼び出し側で解放しないこと。描くスロットが無い・描けない環境なら null（警告は 1 回だけ）
        /// </summary>
        internal static RenderTexture GetOrBuild(
            Transform root, IReadOnlyList<Renderer> renderers, Texture2D texture, int width, int height, int fillIterations = 0)
        {
            if (root == null || renderers == null || texture == null || width <= 0 || height <= 0) return null;
            var slots = CollectSlots(renderers, texture);
            if (slots.Count == 0) return null;
            fillIterations = Mathf.Clamp(fillIterations, 0, MaxFillIterations);

            var key = new Key(root.GetInstanceID(), SlotsHash(root, slots), width, height, fillIterations);
            for (int i = 0; i < s_entries.Count; i++)
            {
                var entry = s_entries[i];
                if (!entry.key.Equals(key)) continue;
                if (entry.texture != null && entry.texture.IsCreated())
                {
                    // 最近使ったものを末尾へ
                    s_entries.RemoveAt(i);
                    s_entries.Add(entry);
                    return entry.texture;
                }
                // 描画デバイスのリセット等で中身を失った RT は捨てて作り直す
                Destroy(entry.texture);
                s_entries.RemoveAt(i);
                break;
            }

            var rt = Build(root, slots, width, height);
            if (rt == null) return null;
            rt = Fill(rt, fillIterations);
            s_entries.Add(new Entry { key = key, texture = rt });
            while (s_entries.Count > Capacity)
            {
                Destroy(s_entries[0].texture);
                s_entries.RemoveAt(0);
            }
            return rt;
        }

        /// <summary>キャッシュした RT をすべて解放する（GetOrBuild が返した RT はこれ以降使えない）</summary>
        internal static void ClearCache()
        {
            foreach (var entry in s_entries) Destroy(entry.texture);
            s_entries.Clear();
        }

        /// <summary>
        /// renderers の中で texture をメインに持つスロット（Renderer の順・サブメッシュの順）。
        /// Tiling が (0,0) なら (1,1) として扱う（RecolorPreview.CollectUsers と同じ）。メッシュの無い Renderer・
        /// メッシュのサブメッシュ数を超えるスロット・SkinnedMeshRenderer / MeshRenderer 以外は含めない
        /// </summary>
        internal static List<Slot> CollectSlots(IReadOnlyList<Renderer> renderers, Texture2D texture)
        {
            var result = new List<Slot>();
            if (renderers == null || texture == null) return result;
            var seen = new HashSet<(Renderer, int)>();
            foreach (var renderer in renderers)
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                var mesh = RendererMeshAccess.GetSharedMesh(renderer);
                if (mesh == null) continue;
                var materials = renderer.sharedMaterials;
                if (materials == null) continue;

                int slots = Mathf.Min(materials.Length, mesh.subMeshCount);
                for (int i = 0; i < slots; i++)
                {
                    if (!MaterialTextureResolver.TryGetMainTexture(materials[i], out var info)) continue;
                    if (info.texture != texture) continue;
                    if (mesh.GetTopology(i) != MeshTopology.Triangles) continue;
                    if (!seen.Add((renderer, i))) continue;
                    var scale = info.scale == Vector2.zero ? Vector2.one : info.scale;
                    result.Add(new Slot(renderer, i, scale, info.offset));
                }
            }
            return result;
        }

        /// <summary>
        /// スロットの組（順序込み）の畳み込み。Renderer・メッシュ・サブメッシュ・Tiling/Offset、ルートと各 Renderer の行列、
        /// SkinnedMeshRenderer の rootBone と最初の MaxHashedBones 本のボーンの行列が変わったら別の鍵になる
        /// </summary>
        private static int SlotsHash(Transform root, List<Slot> slots)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + root.localToWorldMatrix.GetHashCode();
                Renderer previous = null;
                foreach (var slot in slots)
                {
                    if (slot.renderer != previous)
                    {
                        // 行列は Renderer ごとに 1 回（同じ Renderer のスロットは続けて並ぶ）
                        previous = slot.renderer;
                        h = h * 31 + TransformHash(slot.renderer);
                    }
                    h = h * 31 + slot.renderer.GetInstanceID();
                    var mesh = RendererMeshAccess.GetSharedMesh(slot.renderer);
                    h = h * 31 + (mesh != null ? mesh.GetInstanceID() : 0);
                    h = h * 31 + slot.submesh;
                    h = h * 31 + slot.uvScale.GetHashCode();
                    h = h * 31 + slot.uvOffset.GetHashCode();
                }
                return h;
            }
        }

        /// <summary>Renderer の行列（SkinnedMeshRenderer は rootBone と最初の MaxHashedBones 本のボーンの行列も）の畳み込み</summary>
        private static int TransformHash(Renderer renderer)
        {
            unchecked
            {
                int h = renderer.transform.localToWorldMatrix.GetHashCode();
                if (!(renderer is SkinnedMeshRenderer smr)) return h;
                h = h * 31 + (smr.rootBone != null ? smr.rootBone.localToWorldMatrix.GetHashCode() : 0);
                var bones = smr.bones;
                if (bones == null) return h;
                int count = Mathf.Min(bones.Length, MaxHashedBones);
                for (int i = 0; i < count; i++)
                {
                    h = h * 31 + (bones[i] != null ? bones[i].localToWorldMatrix.GetHashCode() : 0);
                }
                return h;
            }
        }

        /// <summary>
        /// slots を位置マップに描いた新しい RT を返す（呼び出し側の所有。使い終わったら Destroy）。描けない環境なら null。
        /// Renderer ごとにメッシュを 1 回だけ取り（SkinnedMeshRenderer は BakeMesh で今のポーズを焼く）、スロットのサブメッシュを描く
        /// </summary>
        internal static RenderTexture Build(Transform root, IReadOnlyList<Slot> slots, int width, int height)
        {
            if (root == null || slots == null || width <= 0 || height <= 0) return null;
            if (!IsAvailable)
            {
                if (!s_warnedUnavailable)
                {
                    s_warnedUnavailable = true;
                    Debug.LogWarning("[Tocolo] この環境ではグラデーションの位置の計算が使えないため、グラデーションは「色 1」だけで塗られます");
                }
                return null;
            }

            var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBFloat, 0)
            {
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
                // 膨張（PositionFill.compute）で ping-pong の書き込み先にもする
                enableRandomWrite = SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBFloat),
            };
            var rt = new RenderTexture(desc)
            {
                name = "ClickRecolor_PositionMap",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!rt.Create())
            {
                Object.DestroyImmediate(rt);
                return null;
            }

            var material = new Material(ShaderAssets.LoadShader(ShaderAssets.PositionMapGuid)) { hideFlags = HideFlags.HideAndDontSave };
            var previous = RenderTexture.active;
            try
            {
                Graphics.SetRenderTarget(rt);
                // 描かれない画素は A = 0（ColorShift.compute が t = 0 として扱う）
                GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
                material.SetMatrix(RootWorldToLocalId, root.worldToLocalMatrix);
                GL.PushMatrix();
                try
                {
                    DrawSlots(material, slots);
                }
                finally
                {
                    GL.PopMatrix();
                }
            }
            catch
            {
                Destroy(rt);
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(material);
            }
            return rt;
        }

        /// <summary>
        /// 描いた位置マップ map に膨張を iterations 回掛けた RT を返す（map の所有権を受け取る。返すのは map かもう 1 枚で、使わなかった方は破棄する）。
        /// 膨張が使えない環境（compute・ARGBFloat への書き込み・シェーダー）では map をそのまま返す（警告は 1 回だけ）
        /// </summary>
        internal static RenderTexture Fill(RenderTexture map, int iterations)
        {
            if (map == null || iterations <= 0) return map;
            var shader = FillShader;
            if (shader == null || !SystemInfo.supportsComputeShaders || !map.enableRandomWrite)
            {
                if (!s_warnedFillUnavailable)
                {
                    s_warnedFillUnavailable = true;
                    Debug.LogWarning("[Tocolo] この環境では位置マップの膨張が使えないため、はみ出し幅の部分はグラデーションの「色 1」側で塗られます");
                }
                return map;
            }

            var spare = new RenderTexture(map.descriptor)
            {
                name = "ClickRecolor_PositionMap",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!spare.Create())
            {
                Object.DestroyImmediate(spare);
                return map;
            }

            var current = map;
            shader.SetInt(FillWidthId, map.width);
            shader.SetInt(FillHeightId, map.height);
            int groupsX = (map.width + FillThreadGroupSize - 1) / FillThreadGroupSize;
            int groupsY = (map.height + FillThreadGroupSize - 1) / FillThreadGroupSize;
            for (int i = 0; i < iterations; i++)
            {
                shader.SetTexture(s_kernelFill, FillInId, current);
                shader.SetTexture(s_kernelFill, FillOutId, spare);
                shader.Dispatch(s_kernelFill, groupsX, groupsY, 1);
                (current, spare) = (spare, current);
            }
            Destroy(spare);
            return current;
        }

        private static ComputeShader FillShader
        {
            get
            {
                if (s_fill == null)
                {
                    s_fill = ShaderAssets.Load(ShaderAssets.PositionFillGuid);
                    if (s_fill != null) s_kernelFill = s_fill.FindKernel("CSFill");
                }
                return s_fill;
            }
        }

        /// <summary>slots を順に描く。同じ Renderer が続くスロットは焼いたメッシュを使い回す</summary>
        private static void DrawSlots(Material material, IReadOnlyList<Slot> slots)
        {
            Renderer current = null;
            Mesh mesh = null;
            Matrix4x4 localToWorld = Matrix4x4.identity;
            bool ownsMesh = false;
            try
            {
                foreach (var slot in slots)
                {
                    if (slot.renderer == null) continue;
                    if (slot.renderer != current)
                    {
                        if (ownsMesh && mesh != null) Object.DestroyImmediate(mesh);
                        current = slot.renderer;
                        // SkinnedMeshRenderer は BakeMesh（今のポーズ、Transform のスケール込み）＋位置と回転の行列、MeshRenderer は共有メッシュ＋localToWorldMatrix。
                        // レイ判定（ScenePicker）と同じ取り方にして、Scene で見える形と位置マップの形を揃える
                        if (!MeshRaycaster.TryGetMeshData(slot.renderer, out mesh, out localToWorld, out ownsMesh))
                        {
                            mesh = null;
                            ownsMesh = false;
                        }
                    }
                    if (mesh == null || slot.submesh >= mesh.subMeshCount) continue;
                    material.SetVector(UvScaleOffsetId, new Vector4(slot.uvScale.x, slot.uvScale.y, slot.uvOffset.x, slot.uvOffset.y));
                    material.SetPass(0);
                    Graphics.DrawMeshNow(mesh, localToWorld, slot.submesh);
                }
            }
            finally
            {
                if (ownsMesh && mesh != null) Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>Build で得た RT を解放して破棄する（null は無視）</summary>
        internal static void Destroy(RenderTexture rt)
        {
            if (rt == null) return;
            if (RenderTexture.active == rt) RenderTexture.active = null;
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }
}
