using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Picking;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// パネルの［テクスチャの残りを選択］: 編集のテクスチャを使うパーツのうち、まだ点（種）に無いものを全部、追加の点として足す。
    /// 編集に「全体」の印を付けない: 印だと Ctrl＋クリックで外せず、マテリアルをまたいで足したメンバーにも写ってテクスチャ全体を選んでいた
    /// </summary>
    internal static class RemainingParts
    {
        /// <summary>
        /// edit のテクスチャを使うパーツ（除外リストの Renderer を除く）のうち、主種・追加の種と同じパーツでないものを 1 パーツ 1 種で足す。
        /// パーツはメッシュ・サブメッシュ・チャートで同一視する（同じメッシュを使う別の Renderer は足さない）。足した数を返す。
        /// Undo.RecordObject・SetDirty は呼び出し側
        /// </summary>
        internal static int Add(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null || edit.sourceTexture == null) return 0;
            if (edit.extraSeeds == null) edit.extraSeeds = new List<RecolorSeed>();

            var known = new HashSet<(int mesh, int submesh, int chart)>();
            AddKnown(known, edit.seedRenderer, edit.seedSubmesh, edit.seedTriangle);
            foreach (var seed in edit.extraSeeds)
            {
                if (seed != null) AddKnown(known, seed.renderer, seed.submesh, seed.triangle);
            }

            int added = 0;
            foreach (var renderer in RecolorSceneTool.CollectPreviewRenderers(component.gameObject))
            {
                if (component.excludedRenderers != null && component.excludedRenderers.Contains(renderer)) continue;
                var mesh = RendererMeshAccess.GetSharedMesh(renderer);
                if (mesh == null || !mesh.isReadable) continue;
                var materials = renderer.sharedMaterials;
                Vector2[] uv = null;
                // 余った枠（最後のサブメッシュの重ね描き）も見る（テクスチャの利用者の集め方と同じ）
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    int submesh = MaterialTextureResolver.SubmeshOfSlot(slot, mesh.subMeshCount);
                    if (submesh < 0) break;
                    if (!MaterialTextureResolver.TryGetMainTexture(materials[slot], out var info) || info.texture != edit.sourceTexture) continue;
                    var table = UvChartDetector.GetOrBuild(mesh, submesh);
                    if (table == null) continue;
                    uv ??= mesh.uv;
                    int[] triangles = null;
                    for (int t = 0; t < table.chartOfTriangle.Length; t++)
                    {
                        if (!known.Add((mesh.GetInstanceID(), submesh, table.chartOfTriangle[t]))) continue;
                        triangles ??= mesh.GetTriangles(submesh);
                        var center = (uv[triangles[t * 3]] + uv[triangles[t * 3 + 1]] + uv[triangles[t * 3 + 2]]) / 3f;
                        edit.extraSeeds.Add(new RecolorSeed
                        {
                            renderer = renderer,
                            submesh = submesh,
                            triangle = t,
                            // クリックした点と同じく、マテリアルの Tiling/Offset 適用後の 0..1
                            uv = MaterialTextureResolver.ToTextureCoord(info, center),
                        });
                        added++;
                    }
                }
            }
            return added;
        }

        /// <summary>
        /// 0.5.0 までの［テクスチャの残りを選択］で付いた「全体」の印を、同じ範囲のパーツの集まりに置き換える（触ったときに 1 回だけ）。
        /// 印を外し、影響範囲をパーツにし、全パーツを足して、各点の色を揃えるを OFF にする（全体で 1 つの分布だった見た目を保つ）。
        /// 印が無ければ何もしない。Undo.RecordObject・SetDirty は呼び出し側
        /// </summary>
        internal static void ConvertLegacyWholeTexture(ClickRecolor component, RecolorEdit edit)
        {
            if (edit == null || !edit.wholeTexture) return;
            edit.wholeTexture = false;
            edit.mode = SelectionMode.Island;
            edit.perSeedStats = false;
            Add(component, edit);
        }

        private static void AddKnown(HashSet<(int, int, int)> known, Renderer renderer, int submesh, int triangle)
        {
            var mesh = RendererMeshAccess.GetSharedMesh(renderer);
            if (mesh == null) return;
            var table = UvChartDetector.GetOrBuild(mesh, submesh);
            if (table == null || triangle < 0 || triangle >= table.chartOfTriangle.Length) return;
            known.Add((mesh.GetInstanceID(), submesh, table.chartOfTriangle[triangle]));
        }
    }
}
