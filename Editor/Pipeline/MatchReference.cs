using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.NDMF;
using Nekoare.ClickRecolor.Editor.Picking;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Pipeline
{
    /// <summary>お手本を測れたか（測れなかった理由）</summary>
    internal enum MatchReferenceStatus
    {
        Ok,
        /// <summary>お手本の Renderer が無い（未指定・削除された）</summary>
        NoRenderer,
        /// <summary>お手本の枠にメインテクスチャ（プロジェクトのテクスチャ資産）が無い</summary>
        NoTexture,
        /// <summary>お手本が合わせる側と同じテクスチャ（統計の範囲が同じになり意味がない）</summary>
        SameTexture,
        /// <summary>お手本か合わせる側が髪ツール（キメラヘアマスター）の対象（今回は対応しない）</summary>
        ChmHair,
        /// <summary>お手本の色が読めない（メッシュの Read/Write が無効・GPU が使えない等）</summary>
        Unreadable,
        /// <summary>ドラッグ中なので測るのを後回しにした（控えはそのまま。離したら測り直す）</summary>
        Deferred,
    }

    /// <summary>
    /// お手本の髪の色を測り、編集の控え（RecolorEdit.matchProfileData）を書き換える。
    /// お手本の色は「対象アバターの Tocolo の編集のうち、お手本に合わせる以外をお手本のテクスチャに掛けた色」。
    /// お手本に合わせる編集を反映しないのは、A を B に・B を A に合わせる堂々巡りを仕組みで起こさないため
    /// </summary>
    internal static class MatchReference
    {
        private static readonly Dictionary<int, MatchProfile> s_cache = new Dictionary<int, MatchProfile>();
        private const int MaxCache = 16;
        private static readonly Dictionary<string, MatchReferenceStatus> s_lastStatus = new Dictionary<string, MatchReferenceStatus>();

        /// <summary>最後に測ったときの結果（パネルの案内用。まだ測っていなければ null）</summary>
        internal static MatchReferenceStatus? LastStatus(RecolorEdit edit) =>
            edit != null && edit.id != null && s_lastStatus.TryGetValue(edit.id, out var status) ? status : (MatchReferenceStatus?)null;

        /// <summary>お手本の枠のメインテクスチャ（プロジェクトの資産）。無ければ null</summary>
        internal static Texture2D ReferenceTexture(Renderer renderer, int slot)
        {
            if (renderer == null) return null;
            var materials = renderer.sharedMaterials;
            if (materials == null || slot < 0 || slot >= materials.Length) return null;
            if (!MaterialTextureResolver.TryGetMainTexture(materials[slot], out var info) || info.texture == null) return null;
            return AssetDatabase.Contains(info.texture) ? info.texture : null;
        }

        /// <summary>
        /// お手本を指定できるか（測る前の判定）: Renderer とテクスチャがある・合わせる側と別のテクスチャ・どちらも髪ツールの対象でない
        /// </summary>
        internal static MatchReferenceStatus Check(RecolorEdit edit, Renderer renderer, int slot)
        {
            if (renderer == null) return MatchReferenceStatus.NoRenderer;
            var texture = ReferenceTexture(renderer, slot);
            if (texture == null) return MatchReferenceStatus.NoTexture;
            if (edit != null && texture == edit.sourceTexture) return MatchReferenceStatus.SameTexture;
            if (HairToolTargets.RoleOf(renderer) != HairToolRole.None) return MatchReferenceStatus.ChmHair;
            if (edit != null && IsChmTarget(edit)) return MatchReferenceStatus.ChmHair;
            return MatchReferenceStatus.Ok;
        }

        /// <summary>合わせる側（主種・Ctrl＋クリックで足した種）のどれかが髪ツールの対象か</summary>
        private static bool IsChmTarget(RecolorEdit edit)
        {
            if (edit.seedRenderer != null && HairToolTargets.RoleOf(edit.seedRenderer) != HairToolRole.None) return true;
            if (edit.extraSeeds == null) return false;
            foreach (var seed in edit.extraSeeds)
            {
                if (seed?.renderer != null && HairToolTargets.RoleOf(seed.renderer) != HairToolRole.None) return true;
            }
            return false;
        }

        /// <summary>
        /// お手本を作る作業解像度の上限。統計は小さな表なので、プレビューの解像度（2048 など）で作る必要はない（測り直しを軽くする）
        /// </summary>
        internal const int MeasureSize = 512;

        /// <summary>
        /// お手本の色の統計を測る。component は編集を持つ ClickRecolor（対象アバターの基準）。size は作業解像度（MeasureSize で頭打ち）。
        /// deferWhileDragging なら、測り直しが要るのにマウスで何かを掴んでいる間は測らず Deferred を返し、離したら RecolorPreview にやり直させる
        /// （ドラッグの 1 フレームごとに測ると、お手本の色のドラッグが重くなるため）
        /// </summary>
        internal static MatchReferenceStatus Measure(
            ClickRecolor component, RecolorEdit edit, int size, out MatchProfile profile, bool deferWhileDragging = false)
        {
            profile = null;
            if (component == null || edit == null) return MatchReferenceStatus.NoRenderer;
            var renderer = edit.matchRenderer;
            var status = Check(edit, renderer, edit.matchSlot);
            if (status != MatchReferenceStatus.Ok) return status;
            var texture = ReferenceTexture(renderer, edit.matchSlot);
            size = size > 0 ? Mathf.Min(size, MeasureSize) : MeasureSize;

            var avatarRoot = Decal.DecalOverlayMaterial.FindAvatarRoot(component.transform);
            var avatarRenderers = MeshRenderersUnder(avatarRoot);
            // お手本の範囲: お手本がアバターの中ならアバター、外ならお手本の一番外側の親の下（同じ髪の別パーツも数える）
            var scopeRoot = renderer.transform.IsChildOf(avatarRoot) ? avatarRoot : renderer.transform.root;
            var coverageUsers = RecolorPreview.CollectUsers(scopeRoot == avatarRoot ? avatarRenderers : MeshRenderersUnder(scopeRoot), texture);
            if (coverageUsers.Count == 0) return MatchReferenceStatus.Unreadable;

            var edits = ReferenceEdits(avatarRoot, texture, out var owners);
            int key = CacheKey(texture, edits, coverageUsers, size);
            if (s_cache.TryGetValue(key, out var cached))
            {
                profile = cached;
                return profile != null && profile.IsValid ? MatchReferenceStatus.Ok : MatchReferenceStatus.Unreadable;
            }
            if (deferWhileDragging && GUIUtility.hotControl != 0)
            {
                RemeasureAfterDrag();
                return MatchReferenceStatus.Deferred;
            }

            RenderTexture edited = null;
            try
            {
                if (edits.Count > 0)
                {
                    var jobUsers = RecolorPreview.CollectUsers(avatarRenderers, texture);
                    var jobs = new List<EditJob>();
                    for (int i = 0; i < edits.Count; i++)
                    {
                        var e = edits[i];
                        if (e.seedRenderer == null && RecolorPipeline.NeedsSeedRenderer(e)) continue;
                        var job = RecolorPipeline.PrepareJob(e, texture, size, jobUsers, context: MaskContext.For(owners[i], avatarRenderers));
                        if (job != null) jobs.Add(job);
                    }
                    if (jobs.Count > 0)
                    {
                        // 「アバター全体」の連結の統計の共有も、プレビュー・ビルドと同じく掛ける（掛けないと測った色が見た目とずれる）
                        RecolorPipeline.ShareGroupStats(jobs);
                        edited = RecolorPipeline.Run(new PipelineInput
                        {
                            sourceAsset = texture,
                            workingSize = size,
                            jobs = jobs,
                            root = owners[0].transform,
                            renderers = avatarRenderers,
                        });
                    }
                }
                profile = RecolorPipeline.MeasureCoverage(edited != null ? (Texture)edited : texture, texture, size, coverageUsers);
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(edited);
            }

            if (s_cache.Count >= MaxCache) s_cache.Clear();
            s_cache[key] = profile;
            return profile != null && profile.IsValid ? MatchReferenceStatus.Ok : MatchReferenceStatus.Unreadable;
        }

        /// <summary>
        /// お手本を測り直し、控えが変わったら書き換える（Undo には積まない: プレビューのたびに Undo の記録が増えないように）。
        /// 測れなければ控えはそのまま（お手本が消えても同じ色を出す）。結果は LastStatus でも引ける
        /// </summary>
        internal static MatchReferenceStatus Refresh(ClickRecolor component, RecolorEdit edit, int size, bool deferWhileDragging = false)
        {
            var status = Measure(component, edit, size, out var profile, deferWhileDragging);
            if (status == MatchReferenceStatus.Deferred) return status;
            if (edit?.id != null) s_lastStatus[edit.id] = status;
            if (status == MatchReferenceStatus.ChmHair)
            {
                // 髪ツールの髪（お手本・合わせる側）になったら控えを捨てて掛けない: 髪ツールが変える前の色で測った控えで黙って合い続けないように
                if (string.IsNullOrEmpty(edit.matchProfileData)) return status;
                edit.matchProfileData = null;
                MarkDirtyLater(component);
                return status;
            }
            if (status != MatchReferenceStatus.Ok) return status;
            string data = profile.Serialize();
            // 測り直して効くようになった編集（指定したときに測れず、後で Read/Write を有効にした等）は、色を決めた編集として扱う
            bool becameActive = !edit.hasTarget;
            if (data == edit.matchProfileData && !becameActive) return status;
            edit.matchProfileData = data;
            if (becameActive)
            {
                edit.hasTarget = true;
                edit.confirmed = true;
            }
            MarkDirtyLater(component);
            return status;
        }

        private static bool s_waitingForRelease;

        /// <summary>マウスを離したら（掴んでいる部品が無くなったら）プレビューをやり直させ、後回しにしたお手本を測らせる</summary>
        private static void RemeasureAfterDrag()
        {
            if (s_waitingForRelease) return;
            s_waitingForRelease = true;
            EditorApplication.update += PollRelease;
        }

        private static void PollRelease()
        {
            if (GUIUtility.hotControl != 0) return;
            EditorApplication.update -= PollRelease;
            s_waitingForRelease = false;
            RecolorPreview.MatchRemeasure.Value = RecolorPreview.MatchRemeasure.Value + 1;
        }

        /// <summary>
        /// シーンの変更の記録を後回しにする: プレビューの作り直しの途中（NDMF の Instantiate）から呼ばれるため。
        /// Undo には積まない（プレビューのたびに Undo の記録が増えないように）
        /// </summary>
        private static void MarkDirtyLater(ClickRecolor component)
        {
            EditorApplication.delayCall += () =>
            {
                if (component == null) return;
                EditorUtility.SetDirty(component);
                if (PrefabUtility.IsPartOfPrefabInstance(component)) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            };
        }

        /// <summary>avatarRoot の Tocolo の編集のうち、texture に掛かってお手本の色を変えるもの（お手本に合わせる編集を除く）と、それぞれの持ち主</summary>
        private static List<RecolorEdit> ReferenceEdits(Transform avatarRoot, Texture2D texture, out List<ClickRecolor> owners)
        {
            var result = new List<RecolorEdit>();
            owners = new List<ClickRecolor>();
            foreach (var component in avatarRoot.GetComponentsInChildren<ClickRecolor>(true))
            {
                // 「ビルド時に反映」を見る（「プレビュー」は見ない）: 控えはビルドの入力なので、ビルドで焼かれるお手本の色に合わせる
                if (component == null || component.edits == null || !component.applyOnBuild) continue;
                foreach (var edit in component.edits)
                {
                    if (edit == null || edit.sourceTexture != texture || edit.matchEnabled) continue;
                    if (!RecolorPreview.IsPreviewTarget(edit)) continue;
                    result.Add(edit);
                    owners.Add(component);
                }
            }
            return result;
        }

        private static List<Renderer> MeshRenderersUnder(Transform root)
        {
            var result = new List<Renderer>();
            if (root == null) return result;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SkinnedMeshRenderer || renderer is MeshRenderer) result.Add(renderer);
            }
            return result;
        }

        private static int CacheKey(
            Texture2D texture, List<RecolorEdit> edits,
            List<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users, int size)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + texture.GetInstanceID();
                h = h * 31 + texture.imageContentsHash.GetHashCode();
                h = h * 31 + size;
                foreach (var edit in edits) h = EditHash.All(h, edit);
                h = h * 31 + RecolorPreview.ComputeTextureHash(new List<RecolorEdit>(), users);
                return h;
            }
        }
    }
}
