using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// 連結（RecolorEdit.groupId が同じ編集の組）の操作。連結はテクスチャごとに 1 つずつの編集で、同じ設定を持つ。連結には 2 種類ある:
    /// 「アバター全体」の連結（Link。全員が同じ種色 seedOklab を共有し、hasSeedOklab = true。クリックした編集以外は種の Renderer を持たない）と、
    /// 島の連結（LinkSeedBased。Ctrl＋クリック・矩形で別のテクスチャの島を足したもの。各メンバーが自分の種を持ち、hasSeedOklab = false）。
    /// どの関数も Undo.RecordObject と EditorUtility.SetDirty は呼び出し側の責任（1 操作で 1 回にまとめるため）
    /// </summary>
    internal static class EditGroups
    {
        internal static bool IsGrouped(RecolorEdit edit) => edit != null && !string.IsNullOrEmpty(edit.groupId);

        /// <summary>「アバター全体」の連結（共有の種色 hasSeedOklab を持つ連結）か。島の連結（各メンバーが自分の種を持つ）は false</summary>
        internal static bool IsWholeAvatarGroup(RecolorEdit edit) => IsGrouped(edit) && edit.hasSeedOklab;

        /// <summary>edit と同じ連結の編集（edit 自身を含む・edits の並び順）。単独の編集なら edit だけ</summary>
        internal static List<RecolorEdit> Members(ClickRecolor component, RecolorEdit edit)
        {
            var result = new List<RecolorEdit>();
            if (edit == null) return result;
            if (component == null || component.edits == null || !IsGrouped(edit))
            {
                result.Add(edit);
                return result;
            }
            foreach (var e in component.edits)
            {
                if (e != null && e.groupId == edit.groupId) result.Add(e);
            }
            // Undo で List ごと入れ替わった後の古い参照などで見つからなくても、edit には効かせる
            if (!result.Contains(edit)) result.Add(edit);
            return result;
        }

        /// <summary>
        /// edit と同じ連結の全編集に action を掛ける（単独なら edit だけ）。
        /// Undo.RecordObject(component) は呼び出し側で 1 回だけ呼ぶこと
        /// </summary>
        internal static void ForEachInGroup(ClickRecolor component, RecolorEdit edit, Action<RecolorEdit> action)
        {
            if (edit == null || action == null) return;
            foreach (var e in Members(component, edit)) action(e);
        }

        /// <summary>edit の連結に入っている編集の数（単独なら 1）</summary>
        internal static int Count(ClickRecolor component, RecolorEdit edit) => Members(component, edit).Count;

        /// <summary>edits の中で、その連結の先頭（最初に並ぶ編集）か。単独の編集は常に true</summary>
        internal static bool IsHead(ClickRecolor component, RecolorEdit edit)
        {
            if (!IsGrouped(edit) || component == null || component.edits == null) return true;
            foreach (var e in component.edits)
            {
                if (e != null && e.groupId == edit.groupId) return ReferenceEquals(e, edit);
            }
            return true;
        }

        /// <summary>
        /// 単独の編集 anchor を「アバター全体」の連結にする。anchor の種色（Oklab）が無ければ作業 RT から取り
        /// （取れなければ見本色 seedColor から換算）、対象ルート配下の他のメインテクスチャごとに同じ設定の編集を anchor の直後へ足す。
        /// 足した編集は種の Renderer を持たない（seedRenderer = null・seedTriangle = -1・seedUv = 0）。
        /// 既に連結なら何もしない
        /// </summary>
        internal static void Link(ClickRecolor component, RecolorEdit anchor)
        {
            if (component == null || anchor == null || IsGrouped(anchor)) return;

            if (!anchor.hasSeedOklab)
            {
                if (!RecolorPipeline.TrySampleSeedOklab(anchor.sourceTexture, (int)component.previewResolution, anchor.seedUv, out var oklab))
                {
                    // 元テクスチャが読めないときの保険（見本色も取れていなければ灰色）
                    oklab = OklabConverter.LinearRGBToOklab(OklabConverter.SRGBToLinear(anchor.seedColor));
                }
                anchor.seedOklab = oklab;
                anchor.hasSeedOklab = true;
            }
            anchor.mode = SelectionMode.Color;
            anchor.scope = ColorScope.WholeAvatar;
            anchor.groupId = Guid.NewGuid().ToString("N");

            int insertAt = component.edits.IndexOf(anchor) + 1;
            if (insertAt <= 0) insertAt = component.edits.Count;
            foreach (var texture in CollectAvatarTextures(component.gameObject))
            {
                if (texture == anchor.sourceTexture) continue;
                var member = CopyForTexture(anchor, texture);
                component.edits.Insert(insertAt++, member);
            }
        }

        /// <summary>
        /// 連結を解いて 1 件に戻す。残すのは edit（種の Renderer が無ければ、連結の中で種の Renderer を持つ編集）で、
        /// 他は削除する。残した編集は groupId を空にし、種の Renderer があれば共有の種色も捨てる（以後は単独の編集と同じく作業 RT から取り直す）。
        /// スコープは変えない（呼び出し側で決める）。残した編集を返す
        /// </summary>
        internal static RecolorEdit Unlink(ClickRecolor component, RecolorEdit edit)
        {
            if (edit == null) return null;
            var members = Members(component, edit);
            var keep = edit;
            if (keep.seedRenderer == null)
            {
                foreach (var m in members)
                {
                    if (m.seedRenderer != null) { keep = m; break; }
                }
            }
            if (component != null)
            {
                foreach (var m in members)
                {
                    if (!ReferenceEquals(m, keep)) component.edits.Remove(m);
                }
            }
            keep.groupId = "";
            // 種の Renderer があれば、以後は単独の編集と同じく作業 RT から種色を取り直すので共有の種色は捨てる。
            // 全員が種の Renderer を持たない（クリックした編集が消えた）ときは、取り直す元が無いので共有の種色を残す
            // （消すと色モードの選択が種色を失い、何も選ばれなくなる）
            if (keep.seedRenderer != null)
            {
                keep.hasSeedOklab = false;
                keep.seedOklab = default;
            }
            return keep;
        }

        /// <summary>
        /// edit と同じ連結（単独なら edit だけ）の中で、対象テクスチャが texture の編集（edits の並び順で最初のもの）。無ければ null
        /// </summary>
        internal static RecolorEdit FindMemberForTexture(ClickRecolor component, RecolorEdit edit, Texture2D texture)
        {
            if (edit == null || texture == null) return null;
            foreach (var m in Members(component, edit))
            {
                if (m.sourceTexture == texture) return m;
            }
            return null;
        }

        /// <summary>
        /// 島の連結に texture 用のメンバーを 1 件足して返す。anchor に groupId が無ければ新しく付ける（anchor を連結にする）。
        /// メンバーは anchor の設定（種以外）を写し、主種は与えた種（renderer・submesh・triangle・uv）、追加の種は空。
        /// seedColor（クリック位置の見本色）があれば元の色にし、色未設定の間は新しい色も元の色にする（CreateEditFromSeed と同じ）。
        /// 挿入位置は連結の最後のメンバーの直後（Inspector で連結が並ぶように）。
        /// 「アバター全体」の連結・共有の種色を持つ編集（hasSeedOklab）は対象外で null
        /// </summary>
        internal static RecolorEdit LinkSeedBased(
            ClickRecolor component, RecolorEdit anchor, Texture2D texture,
            Renderer renderer, int submesh, int triangle, Vector2 uv, Color? seedColor = null)
        {
            if (component == null || anchor == null || texture == null || anchor.hasSeedOklab) return null;
            if (component.edits == null) component.edits = new List<RecolorEdit>();
            if (!IsGrouped(anchor)) anchor.groupId = Guid.NewGuid().ToString("N");

            // 連結の最後のメンバーの直後へ入れる（anchor が edits に無ければ末尾）
            int insertAt = -1;
            for (int i = 0; i < component.edits.Count; i++)
            {
                var e = component.edits[i];
                if (e != null && e.groupId == anchor.groupId) insertAt = i + 1;
            }
            if (insertAt < 0) insertAt = component.edits.Count;

            var member = CopyForTexture(anchor, texture);
            // 共有の種色は写さない（各メンバーが自分の種から取る）
            member.hasSeedOklab = false;
            member.seedOklab = default;
            member.seedRenderer = renderer;
            member.seedSubmesh = submesh;
            member.seedTriangle = triangle;
            member.seedUv = uv;
            // CopyForTexture は「アバター全体」用に作ったので写していない設定も揃える
            member.perSeedStats = anchor.perSeedStats;
            if (seedColor.HasValue)
            {
                member.seedColor = seedColor.Value;
                if (!member.hasTarget) member.targetColor = seedColor.Value;
            }
            component.edits.Insert(insertAt, member);
            return member;
        }

        /// <summary>
        /// 連結のメンバー member を 1 件だけ削除する（単独の編集ならそれを削除）。残りが 1 件になったらその groupId を空に戻す（単独の編集にする）。
        /// 連結の残り（edits の並び順で最初のもの）を返す。残りが無ければ null
        /// </summary>
        internal static RecolorEdit RemoveMember(ClickRecolor component, RecolorEdit member)
        {
            if (component == null || member == null) return null;
            var members = Members(component, member);
            component.edits.Remove(member);
            members.Remove(member);
            if (members.Count == 0) return null;
            if (members.Count == 1) members[0].groupId = "";
            return members[0];
        }

        /// <summary>
        /// クリックしたパーツが見つからない編集（RecolorPipeline.IsSeedMissing）を消す。連結のメンバーなら 1 件ずつ外す（RemoveMember）。
        /// 消した件数を返す。Undo.RecordObject は呼び出し側で行う
        /// </summary>
        internal static int RemoveMissing(ClickRecolor component)
        {
            if (component == null || component.edits == null) return 0;
            var missing = component.edits.FindAll(Pipeline.RecolorPipeline.IsSeedMissing);
            foreach (var edit in missing) RemoveMember(component, edit);
            return missing.Count;
        }

        /// <summary>edit の連結ごと削除する（単独なら edit だけ）</summary>
        internal static void RemoveGroup(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null || component.edits == null) return;
            // 参照が古くても（Undo で List が入れ替わった後など）消せるよう、id でも消す
            var ids = new HashSet<string>();
            foreach (var m in Members(component, edit))
            {
                component.edits.Remove(m);
                if (!string.IsNullOrEmpty(m.id)) ids.Add(m.id);
            }
            if (!string.IsNullOrEmpty(edit.groupId))
            {
                component.edits.RemoveAll(e => e != null && e.groupId == edit.groupId);
            }
            component.edits.RemoveAll(e => e != null && ids.Contains(e.id));
        }

        /// <summary>
        /// root 配下の MeshRenderer / SkinnedMeshRenderer（非アクティブも含む）のメインテクスチャ
        /// （プロジェクトの Texture2D 資産だけ・重複なし・見つけた順）。
        /// 非表示の衣装も連結に入れる: ビルドと書き出し（有料版の CollectRenderers）は非アクティブの Renderer も
        /// 対象にするので、「アバター全体」の範囲もそれに揃える
        /// </summary>
        internal static List<Texture2D> CollectAvatarTextures(GameObject root)
        {
            var result = new List<Texture2D>();
            if (root == null) return result;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                var materials = renderer.sharedMaterials;
                if (materials == null) continue;
                foreach (var material in materials)
                {
                    if (!MaterialTextureResolver.TryGetMainTexture(material, out var info)) continue;
                    var texture = info.texture;
                    if (texture == null || !AssetDatabase.Contains(texture) || result.Contains(texture)) continue;
                    result.Add(texture);
                }
            }
            return result;
        }

        /// <summary>anchor の設定をそのまま写した、texture 向けの連結メンバー（種の Renderer なし）</summary>
        private static RecolorEdit CopyForTexture(RecolorEdit anchor, Texture2D texture)
        {
            var member = RecolorEdit.CreateNew();
            member.name = anchor.name;
            member.enabled = anchor.enabled;
            member.sourceTexture = texture;
            member.seedRenderer = null;
            member.seedSubmesh = 0;
            member.seedTriangle = -1;
            member.seedUv = Vector2.zero;
            member.seedColor = anchor.seedColor;
            member.seedOklab = anchor.seedOklab;
            member.hasSeedOklab = anchor.hasSeedOklab;
            member.groupId = anchor.groupId;
            member.mode = anchor.mode;
            member.scope = anchor.scope;
            member.threshold = anchor.threshold;
            member.feather = anchor.feather;
            member.cleanupRadius = anchor.cleanupRadius;
            member.padding = anchor.padding;
            member.targetColor = anchor.targetColor;
            member.hasTarget = anchor.hasTarget;
            member.darkEndRatio = anchor.darkEndRatio;
            member.lightnessToTarget = anchor.lightnessToTarget;
            member.chromaToTarget = anchor.chromaToTarget;
            member.hueRetain = anchor.hueRetain;
            member.strength = anchor.strength;
            member.shadingStretch = anchor.shadingStretch;
            member.gradientEnabled = anchor.gradientEnabled;
            member.gradientColor = anchor.gradientColor;
            member.gradientDarkEndRatio = anchor.gradientDarkEndRatio;
            member.gradientStrength = anchor.gradientStrength;
            member.gradientBoxPosition = anchor.gradientBoxPosition;
            member.gradientBoxRotation = anchor.gradientBoxRotation;
            member.gradientBoxSize = anchor.gradientBoxSize;
            member.gradientInsideOnly = anchor.gradientInsideOnly;
            member.boxPosition = anchor.boxPosition;
            member.boxRotation = anchor.boxRotation;
            member.boxSize = anchor.boxSize;
            member.boxPerPartStats = anchor.boxPerPartStats;
            return member;
        }
    }
}
