using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Picking;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// 影響範囲「箱の中」の箱（RecolorEdit.box*）の置き直し。
    /// 既定の箱は GradientBox.DefaultFor（現在の選択範囲を囲む）と同じ決め方だが、「箱の中」ではマスク自体が箱で決まるので、
    /// 一時的にパーツモードにしてクリックしたパーツの範囲から求める
    /// </summary>
    internal static class SelectionBox
    {
        /// <summary>edit の箱をクリックしたパーツを囲む既定に置き直す（Undo は呼び出し側で記録する）。連結なら全編集に同じ箱を入れる</summary>
        internal static void ResetToDefault(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null) return;
            var mode = edit.mode;
            var (position, rotation, size, _) = ComputeDefault(component, edit);
            edit.mode = mode;
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.boxPosition = position;
                e.boxRotation = rotation;
                e.boxSize = size;
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>
        /// 「マテリアルをまたいで選ぶ」ON のとき、箱に触れる（メッシュの頂点が 1 つでも箱の内側にある）テクスチャごとに連結のメンバーを揃える:
        /// 無いテクスチャにはメンバーを作り（同じ箱・同じ設定。EditGroups.LinkSeedBased）、箱から外れたテクスチャのメンバーは消す。
        /// 除外リストの Renderer は見ない。ドラッグ終了時・作成時・トグル切替時・除外リスト変更時に呼ぶ（全頂点を見るので毎フレームは呼ばない）。
        /// Undo は呼び出し側で記録する。OFF のときは他テクスチャのメンバーを外して 1 テクスチャに戻す（Phase 2、2026-09-29）
        /// </summary>
        internal static void SyncMembers(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null || edit.mode != SelectionMode.Box) return;
            if (EditGroups.IsWholeAvatarGroup(edit)) return;

            // OFF なら「クリックしたテクスチャだけ」に戻す（他テクスチャのメンバーを外す。ユーザー要望 2026-09-29）
            var found = ToolSession.CrossTextureEnabled
                ? CollectTouchedTextures(component, edit)
                : new List<(Texture2D texture, RecolorSeed seed)>();
            bool changed = false;
            // 足す
            foreach (var (texture, seed) in found)
            {
                if (texture == edit.sourceTexture) continue;
                if (EditGroups.FindMemberForTexture(component, edit, texture) != null) continue;
                if (!RecolorSceneTool.TryGetSourceTexture(texture, out _)) continue;
                var member = EditGroups.LinkSeedBased(component, edit, texture, seed.renderer, seed.submesh, seed.triangle, seed.uv,
                    RecolorSceneTool.SampleSwatchColor(texture, seed.uv));
                if (member != null) changed = true;
            }
            // 外す（箱に触れなくなったテクスチャ。現在の編集は残す）
            foreach (var member in EditGroups.Members(component, edit))
            {
                if (member == edit || member.sourceTexture == null) continue;
                bool touched = false;
                foreach (var (texture, _) in found)
                {
                    if (texture == member.sourceTexture) { touched = true; break; }
                }
                if (touched) continue;
                EditGroups.RemoveMember(component, member);
                changed = true;
            }
            if (changed)
            {
                EditorUtility.SetDirty(component);
                ToolSession.PublishHighlight();
            }
        }

        /// <summary>
        /// 対象ルート配下の Renderer（除外を除く）のうち、箱の内側に頂点を持つサブメッシュのメインテクスチャと、その代表の種
        /// （最初に見つかった三角形。uv はマテリアルの Tiling/Offset 適用後）。SkinnedMeshRenderer は今のポーズで判定する
        /// </summary>
        private static List<(Texture2D texture, RecolorSeed seed)> CollectTouchedTextures(ClickRecolor component, RecolorEdit edit)
        {
            var result = new List<(Texture2D, RecolorSeed)>();
            var worldToRoot = component.transform.worldToLocalMatrix;
            var rootToBox = BoxMaskBuilder.RootToBox(edit.boxPosition, edit.boxRotation);
            var half = new Vector3(Mathf.Abs(edit.boxSize.x), Mathf.Abs(edit.boxSize.y), Mathf.Abs(edit.boxSize.z)) * 0.5f;

            foreach (var renderer in RecolorSceneTool.CollectPreviewRenderers(component.gameObject))
            {
                if (renderer == null || component.IsExcluded(renderer)) continue;
                var materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0) continue;
                if (!MeshRaycaster.TryGetMeshData(renderer, out var mesh, out var localToWorld, out bool ownsMesh)) continue;
                try
                {
                    if (mesh == null || !mesh.isReadable) continue;
                    var toBox = rootToBox * worldToRoot * localToWorld;
                    var vertices = mesh.vertices;
                    var uvs = mesh.uv;
                    // 頂点ごとの内外は 1 回だけ計算する
                    var inside = new bool[vertices.Length];
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        var p = toBox.MultiplyPoint3x4(vertices[i]);
                        inside[i] = Mathf.Abs(p.x) <= half.x && Mathf.Abs(p.y) <= half.y && Mathf.Abs(p.z) <= half.z;
                    }
                    int slots = Mathf.Min(materials.Length, mesh.subMeshCount);
                    for (int s = 0; s < slots; s++)
                    {
                        if (!MaterialTextureResolver.TryGetMainTexture(materials[s], out var info)) continue;
                        if (result.Exists(r => r.Item1 == info.texture)) continue;
                        var triangles = mesh.GetTriangles(s);
                        for (int t = 0; t + 2 < triangles.Length; t += 3)
                        {
                            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                            if (!inside[a] && !inside[b] && !inside[c]) continue;
                            var centroid = uvs.Length > 0 ? (uvs[a] + uvs[b] + uvs[c]) / 3f : Vector2.zero;
                            result.Add((info.texture, new RecolorSeed
                            {
                                renderer = renderer,
                                submesh = s,
                                triangle = t / 3,
                                uv = MaterialTextureResolver.ToTextureCoord(info, centroid),
                            }));
                            break;
                        }
                    }
                }
                finally
                {
                    if (ownsMesh && mesh != null) Object.DestroyImmediate(mesh);
                }
            }
            return result;
        }

        private static (Vector3 position, Quaternion rotation, Vector3 size, Vector3 centroid) ComputeDefault(ClickRecolor component, RecolorEdit edit)
        {
            var mode = edit.mode;
            try
            {
                edit.mode = SelectionMode.Island;
                return GradientBox.DefaultFor(component, edit);
            }
            finally
            {
                edit.mode = mode;
            }
        }
    }
}
