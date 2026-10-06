using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    /// <summary>
    /// 髪用の入口のための、ビルド開始時（Resolving）の Renderer の状態: マテリアル配列・メッシュ・髪ツールから見た役割。
    /// 髪用の入口は髪ツールの後に走るので、そのときの材質（髪ツールの複製。メインは髪ツールの出力）からは
    /// 元テクスチャがどのスロットにあったかが分からない。記録と番号で対応させる（ChmHairSlots）。
    /// BuildContext.GetState で 1 ビルドに 1 つ
    /// </summary>
    internal sealed class ChmHairRecord
    {
        internal sealed class Entry
        {
            public Material[] materials;
            public Mesh mesh;
        }

        /// <summary>記録パスが記録したか（髪ツールの宣言が無い・本ツールのコンポーネントが無いときは記録しない）</summary>
        public bool recorded;
        public readonly Dictionary<Renderer, Entry> entries = new Dictionary<Renderer, Entry>();
        public Dictionary<Renderer, HairToolRole> roles = new Dictionary<Renderer, HairToolRole>();
        /// <summary>
        /// 本体の入口が焼いたテクスチャ（元 → 焼いた物）。髪ツールがメインを変えなかった髪のスロットは本体と同じ結果になるので使い回す
        /// （同じ画素のテクスチャを 2 枚アップロードしない）
        /// </summary>
        public readonly Dictionary<Texture2D, Texture2D> mainBaked = new Dictionary<Texture2D, Texture2D>();

        /// <summary>avatarRoot 配下の SkinnedMeshRenderer / MeshRenderer（非アクティブも含む）の今の状態と役割を記録する</summary>
        internal void Record(GameObject avatarRoot) => Record(avatarRoot, HairToolTargets.OrderSupported);

        /// <summary>Record の、髪ツールの宣言の有無を渡せる版（テスト用）</summary>
        internal void Record(GameObject avatarRoot, bool orderSupported)
        {
            entries.Clear();
            mainBaked.Clear();
            if (avatarRoot == null) return;
            foreach (var renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                entries[renderer] = new Entry { materials = renderer.sharedMaterials, mesh = RendererMeshAccess.GetSharedMesh(renderer) };
            }
            roles = HairToolTargets.RolesIn(avatarRoot, orderSupported);
            recorded = true;
        }

        /// <summary>
        /// renderers のうち記録のあるものの材質とメッシュを、Dispose まで記録時に戻す（Dispose で今の状態に戻す）。
        /// 範囲マスク・被覆・位置マップは「元テクスチャをメインに持つスロット」と元のメッシュで作る作りなので、
        /// 髪ツール（や本体）が差し替えた後のままでは、本体・プレビューと同じ範囲にならない
        /// </summary>
        internal IDisposable RestoreRecorded(IEnumerable<Renderer> renderers) => new Restorer(this, renderers);

        private sealed class Restorer : IDisposable
        {
            private readonly List<(Renderer renderer, Material[] materials, Mesh mesh)> _saved =
                new List<(Renderer, Material[], Mesh)>();

            public Restorer(ChmHairRecord record, IEnumerable<Renderer> renderers)
            {
                if (renderers == null) return;
                foreach (var renderer in renderers)
                {
                    if (renderer == null || !record.entries.TryGetValue(renderer, out var entry)) continue;
                    _saved.Add((renderer, renderer.sharedMaterials, RendererMeshAccess.GetSharedMesh(renderer)));
                    renderer.sharedMaterials = entry.materials;
                    RendererMeshAccess.SetSharedMesh(renderer, entry.mesh);
                }
            }

            public void Dispose()
            {
                foreach (var (renderer, materials, mesh) in _saved)
                {
                    if (renderer == null) continue;
                    renderer.sharedMaterials = materials;
                    RendererMeshAccess.SetSharedMesh(renderer, mesh);
                }
                _saved.Clear();
            }
        }
    }

    /// <summary>髪用の入口の記録パス（Resolving）。髪ツールが宣言していて、本ツールのコンポーネントがあるときだけ記録する</summary>
    public class ChmHairRecordPass : Pass<ChmHairRecordPass>
    {
        public override string DisplayName => "ClickRecolor: Record before Chimera Hair Master";

        protected override void Execute(BuildContext context)
        {
            if (!HairToolTargets.OrderSupported) return;
            var root = context.AvatarRootObject;
            if (root == null || root.GetComponentsInChildren<ClickRecolor>(true).Length == 0) return;
            context.GetState<ChmHairRecord>().Record(root);
        }
    }

    /// <summary>
    /// 髪用の入口の適用パス（髪ツールの後）。焼き込みは有料版が RecolorBuildHook.BakeScoped に登録する。
    /// 登録が無いとき（体験版・古い有料版）は何もしない: 体験版の案内と古い有料版の警告は本体の RecolorPass が出す
    /// </summary>
    public class ChmHairPass : Pass<ChmHairPass>
    {
        public override string DisplayName => "ClickRecolor: Recolor over Chimera Hair Master";

        protected override void Execute(BuildContext context)
        {
            if (!HairToolTargets.OrderSupported) return;
            var bake = RecolorBuildHook.BakeScoped;
            if (bake == null) return;
            int total = bake(context, RecolorScope.ChmHair);
            if (total > 0) Debug.Log($"[Tocolo] キメラヘアマスターの髪の{total}枚のテクスチャに反映");
        }
    }
}
