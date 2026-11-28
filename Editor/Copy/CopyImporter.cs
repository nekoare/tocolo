using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.SceneTool;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Copy
{
    internal enum ImportMode
    {
        /// <summary>移す先の編集の後ろに足す</summary>
        Add,
        /// <summary>入った編集と同じテクスチャの、移す先の編集だけを置き換える（他のテクスチャの編集は残す）</summary>
        ReplaceSameTexture,
    }

    internal sealed class ImportResult
    {
        public readonly List<RecolorEdit> edits = new List<RecolorEdit>();
        public readonly List<(string name, AnchorFailure reason)> failed = new List<(string name, AnchorFailure reason)>();
        /// <summary>足した種（Ctrl＋クリック）の一部を移せなかった編集の名前</summary>
        public readonly List<string> partial = new List<string>();
        /// <summary>「アバター全体」の連結を移す先で作り直した編集の名前（メンバーごとの設定は既定に戻ることがある）</summary>
        public readonly List<string> regrouped = new List<string>();
        /// <summary>画像を作れなかった編集の名前</summary>
        public readonly List<string> missingImages = new List<string>();
        /// <summary>コンポーネントを付けられなかった（Project の Prefab など）</summary>
        public bool noComponent;
    }

    /// <summary>コピーのファイルを移す先のアバターに読み込む</summary>
    internal static class CopyImporter
    {
        private sealed class Planned
        {
            public CopyEntry entry;
            public RecolorEdit edit;
        }

        internal static ImportResult Apply(GameObject targetRoot, CopyFile file, ImportMode mode)
        {
            var result = new ImportResult();
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Tocolo: プロファイルを読み込む");

            var component = TargetResolver.GetOrAddComponent(targetRoot);
            if (component == null)
            {
                result.noComponent = true;
                return result;
            }
            if (component.edits == null) component.edits = new List<RecolorEdit>();

            var planned = Plan(file, targetRoot.transform, result);
            // 画像は資産を作るので RecordObject より前に（作った画像ファイルは Undo では消えない）
            foreach (var p in planned)
            {
                if (string.IsNullOrEmpty(p.entry.imageKey)) continue;
                var image = file.images.Find(i => i.key == p.entry.imageKey);
                p.edit.decalTexture = CopiedImageStore.GetOrCreate(image);
                if (p.edit.decalTexture == null) result.missingImages.Add(p.edit.name);
            }

            Undo.RecordObject(component, "Tocolo: プロファイルを読み込む");
            var existing = new List<RecolorEdit>(component.edits);

            var islandGroups = new Dictionary<string, List<RecolorEdit>>();
            foreach (var p in planned)
            {
                component.edits.Add(p.edit);
                result.edits.Add(p.edit);
                if (!string.IsNullOrEmpty(p.entry.islandGroupKey))
                {
                    if (!islandGroups.TryGetValue(p.entry.islandGroupKey, out var list)) islandGroups[p.entry.islandGroupKey] = list = new List<RecolorEdit>();
                    list.Add(p.edit);
                }
            }
            foreach (var list in islandGroups.Values)
            {
                if (list.Count < 2) continue;
                string groupId = Guid.NewGuid().ToString("N");
                foreach (var e in list) e.groupId = groupId;
            }
            foreach (var p in planned)
            {
                if (!p.entry.wasGroupHead) continue;
                // 共有の種色は元のテクスチャの色なので捨て、移す先のテクスチャから取り直させる（色違いの衣装で範囲が外れる）
                p.edit.hasSeedOklab = false;
                p.edit.seedOklab = default;
                EditGroups.Link(component, p.edit);
                result.regrouped.Add(p.edit.name);
            }

            if (mode == ImportMode.ReplaceSameTexture)
            {
                var textures = new HashSet<Texture2D>();
                foreach (var p in planned) if (p.edit.sourceTexture != null) textures.Add(p.edit.sourceTexture);
                RemoveSameTexture(component, existing, textures);
            }

            EditorUtility.SetDirty(component);
            Undo.CollapseUndoOperations(undoGroup);
            return result;
        }

        /// <summary>
        /// existing（読み込む前からあった編集）のうち、ファイルから入った編集（場所を持つもの）と同じテクスチャのものを消す。
        /// 全部は消さない: 素体や衣装が違うと移せない編集が出るので、関係ない編集まで消えると代わりが入らない。
        /// 「アバター全体」を作り直したメンバーのテクスチャは数えない（アバターの全テクスチャに付くので、数えると素体の編集まで消える）
        /// </summary>
        private static void RemoveSameTexture(ClickRecolor component, List<RecolorEdit> existing, HashSet<Texture2D> added)
        {
            foreach (var e in existing)
            {
                if (e == null || e.sourceTexture == null || !added.Contains(e.sourceTexture)) continue;
                if (EditGroups.IsGrouped(e)) EditGroups.RemoveMember(component, e);
                else component.edits.Remove(e);
            }
        }

        private static List<Planned> Plan(CopyFile file, Transform targetRoot, ImportResult result)
        {
            var planned = new List<Planned>();
            foreach (var entry in file.edits)
            {
                if (entry == null || string.IsNullOrEmpty(entry.editJson)) continue;
                RecolorEdit edit;
                try
                {
                    edit = CopyFileIO.EditFromJson(entry.editJson);
                }
                catch (ArgumentException)
                {
                    continue;
                }
                edit.groupId = null;

                var failure = CopyAnchor.Resolve(entry.seed, targetRoot, out var renderer, out var texture);
                if (failure != AnchorFailure.None)
                {
                    result.failed.Add((edit.name, failure));
                    continue;
                }
                edit.seedRenderer = renderer;
                edit.sourceTexture = texture;
                edit.seedSubmesh = entry.seed.submesh;
                edit.seedTriangle = entry.seed.triangle;
                edit.seedUv = entry.seed.uv;

                edit.extraSeeds = new List<RecolorSeed>();
                bool partial = false;
                foreach (var anchor in entry.extraSeeds)
                {
                    // 主の種と別のテクスチャに付いた種は使えない（その編集のテクスチャの外を指す）
                    if (CopyAnchor.Resolve(anchor, targetRoot, out var r, out var seedTexture) != AnchorFailure.None || seedTexture != texture)
                    {
                        partial = true;
                        continue;
                    }
                    edit.extraSeeds.Add(new RecolorSeed { renderer = r, submesh = anchor.submesh, triangle = anchor.triangle, uv = anchor.uv });
                }
                if (partial) result.partial.Add(edit.name);

                var basisRenderer = renderer;
                if (entry.basis != null && CopyAnchor.ResolveRenderer(entry.basis, targetRoot, out var b) == AnchorFailure.None) basisRenderer = b;
                var m = HairSpace.RootToBasis(targetRoot, HairSpace.BasisOf(basisRenderer));
                edit.gradientBoxPosition = entry.gradientBoxPosition;
                edit.gradientBoxRotation = entry.gradientBoxRotation;
                edit.gradientBoxSize = entry.gradientBoxSize;
                HairSpace.FromBasis(m, ref edit.gradientBoxPosition, ref edit.gradientBoxRotation, ref edit.gradientBoxSize);
                edit.boxPosition = entry.boxPosition;
                edit.boxRotation = entry.boxRotation;
                edit.boxSize = entry.boxSize;
                HairSpace.FromBasis(m, ref edit.boxPosition, ref edit.boxRotation, ref edit.boxSize);
                edit.decalBoxPosition = entry.decalBoxPosition;
                edit.decalBoxRotation = entry.decalBoxRotation;
                edit.decalBoxSize = entry.decalBoxSize;
                HairSpace.FromBasis(m, ref edit.decalBoxPosition, ref edit.decalBoxRotation, ref edit.decalBoxSize);
                edit.gradientTipTop = HairSpace.PointFromBasis(m, entry.gradientTipTop);
                float scale = HairSpace.ScaleOf(m);
                if (scale > 1e-6f) edit.gradientTipLength = Mathf.Clamp(entry.gradientTipLength / scale, 0.005f, 0.5f);

                planned.Add(new Planned { entry = entry, edit = edit });
            }
            return planned;
        }
    }
}
