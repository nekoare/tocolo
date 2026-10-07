using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.NDMF;
using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// シールの基準にする面の上の点: 箱の正面から奥へ（箱の -Z へ）レイを当て、編集（連結ならメンバー）の選択範囲に入っている面のうち一番手前の交点。
    /// 箱の中心は面の上にあるとは限らない（箱モードで作った箱は中心が体の中にある）ので、シールの枠・つまみは中心ではなくこの点に出し、
    /// 触ったときは中心をこの点へ移す。選択範囲で絞るのは、髪のように同じテクスチャの選んでいない面が手前に重なっていると、そちらに乗って
    /// 奥の選んだ面が箱の奥行きから外れてしまうため
    /// </summary>
    internal static class StickerSurface
    {
        /// <summary>手前から順に面を通り抜けて探す回数の上限（重なった毛束など）</summary>
        private const int MaxLayers = 8;
        /// <summary>選択範囲の外の面に当たったとき、次の面を探すために進める距離（ワールド）</summary>
        private const float StepPast = 1e-4f;

        private static ScenePicker s_picker;

        /// <summary>(メンバーの id, テクスチャ) → 選択マスク（R8 を読み戻したもの）と、読んだときの編集の形（RecolorPreview.HashEditShape）</summary>
        private static readonly Dictionary<(string, Texture2D), (int shape, byte[] bytes, int width, int height)> s_masks =
            new Dictionary<(string, Texture2D), (int, byte[], int, int)>();
        /// <summary>s_masks を持っている編集の id（別の編集に移ったら捨てる。マスクは大きいので 1 つの編集の分だけ持つ）</summary>
        private static string s_maskEditId;

        /// <summary>TryFindUnderCenter の前回の結果（箱と範囲が変わらなければ使い回す。Scene の描画のたびにレイを当てないため）</summary>
        private static int s_lastKey;
        private static bool s_lastFound;
        private static Vector3 s_lastPoint;

        /// <summary>持っているマスクと結果を捨てる（ツールの終了時）</summary>
        internal static void Clear()
        {
            s_masks.Clear();
            s_maskEditId = null;
            s_lastKey = 0;
            s_lastFound = false;
        }

        /// <summary>
        /// edit の箱の中心を通る奥行きの線の上で、箱の正面から奥行きの範囲までにある、選択範囲の一番手前の面の点（対象ルートのローカル）。見つからなければ false
        /// </summary>
        internal static bool TryFindUnderCenter(ClickRecolor component, RecolorEdit edit, out Vector3 point)
        {
            point = edit != null ? edit.decalBoxPosition : Vector3.zero;
            if (component == null || edit == null) return false;
            int key;
            unchecked
            {
                key = RecolorPreview.HashEditPlacement(RecolorPreview.HashEditShape(17, edit), edit) * 31 + component.GetInstanceID();
                foreach (var member in EditGroups.Members(component, edit)) key = key * 31 + RecolorPreview.HashEditShape(17, member);
            }
            if (key == s_lastKey)
            {
                if (s_lastFound) point = s_lastPoint;
                return s_lastFound;
            }
            var normal = Quaternion.Normalize(edit.decalBoxRotation) * Vector3.forward;
            float depth = Mathf.Abs(edit.decalBoxSize.z);
            s_lastFound = TryFindFrontSurface(component, edit, edit.decalBoxPosition + normal * (depth * 0.5f), -normal, depth, out s_lastPoint);
            s_lastKey = key;
            if (s_lastFound) point = s_lastPoint;
            return s_lastFound;
        }

        /// <summary>
        /// origin から direction の向き（どちらも対象ルートのローカル）に maxDistance までで、edit（連結ならメンバー）の選択範囲に入っている一番手前の面の点。
        /// 当てるのはメンバーのテクスチャを使う、クリックで当てられる Renderer（除外リスト外）だけ（renderers を渡せばそれを使う）。見つからなければ false。
        /// selectedOnly が false なら、選択範囲の外でもメンバーのテクスチャの面なら採る（画像を範囲の外へ動かしている途中、中心を乗せる面を探すとき）
        /// </summary>
        internal static bool TryFindFrontSurface(ClickRecolor component, RecolorEdit edit, Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point,
            IReadOnlyList<Renderer> renderers = null, bool selectedOnly = true)
        {
            return TryFindFrontSurface(component, edit, origin, direction, maxDistance, out point, out _, renderers, selectedOnly);
        }

        /// <summary>TryFindFrontSurface と同じく探し、当たった面の Renderer も返す</summary>
        internal static bool TryFindFrontSurface(ClickRecolor component, RecolorEdit edit, Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point,
            out Renderer hitRenderer, IReadOnlyList<Renderer> renderers = null, bool selectedOnly = true)
        {
            point = origin;
            hitRenderer = null;
            if (component == null || edit == null || direction.sqrMagnitude < 1e-12f || maxDistance <= 0f) return false;
            var members = new Dictionary<Texture2D, RecolorEdit>();
            foreach (var member in EditGroups.Members(component, edit))
            {
                if (member.sourceTexture != null) members[member.sourceTexture] = member;
            }
            if (renderers == null)
            {
                var collected = new List<Renderer>();
                foreach (var renderer in component.GetComponentsInChildren<Renderer>(true))
                {
                    if (!RecolorSceneTool.IsPickable(renderer) || component.IsExcluded(renderer) || !UsesAnyTexture(renderer, members)) continue;
                    collected.Add(renderer);
                }
                renderers = collected;
            }
            if (renderers.Count == 0) return false;

            var toWorld = component.transform.localToWorldMatrix;
            var toRoot = component.transform.worldToLocalMatrix;
            var worldOrigin = toWorld.MultiplyPoint3x4(origin);
            var worldStep = toWorld.MultiplyVector(direction.normalized * maxDistance);
            float worldMax = worldStep.magnitude;
            var worldDirection = worldStep / Mathf.Max(worldMax, 1e-12f);
            s_picker ??= new ScenePicker();
            float traveled = 0f;
            for (int i = 0; i < MaxLayers; i++)
            {
                if (!s_picker.TryPick(new Ray(worldOrigin + worldDirection * traveled, worldDirection), renderers, null, out var hit)) return false;
                traveled += hit.distance;
                if (traveled > worldMax) return false;
                if (hit.hasMainTexture && members.TryGetValue(hit.mainTexture.texture, out var owner)
                    && (!selectedOnly || IsSelected(component, edit, owner, hit.mainTexture.texture, hit.uv)))
                {
                    point = toRoot.MultiplyPoint3x4(hit.worldPosition);
                    hitRenderer = hit.renderer;
                    return true;
                }
                traveled += StepPast;
            }
            return false;
        }

        internal static bool UsesAnyTexture(Renderer renderer, Dictionary<Texture2D, RecolorEdit> textures)
        {
            foreach (var material in renderer.sharedMaterials)
            {
                if (MaterialTextureResolver.TryGetMainTexture(material, out var info) && info.texture != null && textures.ContainsKey(info.texture)) return true;
            }
            return false;
        }

        /// <summary>
        /// member（edit の連結のメンバー）の texture の選択範囲に uv が入っているか。マスクは edit ごとに持ち、範囲（HashEditShape）が変わったら読み直す
        /// </summary>
        internal static bool IsSelected(ClickRecolor component, RecolorEdit edit, RecolorEdit member, Texture2D texture, Vector2 uv)
        {
            if (component == null || edit == null || member == null || texture == null) return false;
            if (s_maskEditId != edit.id)
            {
                s_masks.Clear();
                s_maskEditId = edit.id;
            }
            int shape = RecolorPreview.HashEditShape(17, member);
            var key = (member.id, texture);
            if (!s_masks.TryGetValue(key, out var mask) || mask.shape != shape)
            {
                var read = ReadMask(component, member, texture);
                mask = (shape, read.bytes, read.width, read.height);
                s_masks[key] = mask;
            }
            return IsSelected(mask.bytes, mask.width, mask.height, uv);
        }

        /// <summary>
        /// マスク（R8 を読み戻したもの。行は下から）の uv の画素が選ばれているか（0.5 を超える）。
        /// uv は 0..1 に丸める（RecolorSceneTool.IsSelectedAt と同じ。範囲の再クリックの判定と揃える）
        /// </summary>
        internal static bool IsSelected(byte[] mask, int width, int height, Vector2 uv)
        {
            if (mask == null || width <= 0 || height <= 0 || mask.Length < width * height) return false;
            int x = Mathf.Clamp(Mathf.FloorToInt(uv.x * width), 0, width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(uv.y * height), 0, height - 1);
            return mask[y * width + x] > 127;
        }

        /// <summary>member の texture の選択マスクを読み戻す（プレビューと同じ作業解像度。作れなければ bytes が null）</summary>
        private static (byte[] bytes, int width, int height) ReadMask(ClickRecolor component, RecolorEdit member, Texture2D texture)
        {
            var renderers = RecolorSceneTool.CollectPreviewRenderers(component.gameObject);
            var users = RecolorPreview.CollectUsers(renderers, texture);
            var job = RecolorPipeline.PrepareJob(member, texture, (int)component.previewResolution, users,
                context: MaskContext.For(component, renderers));
            try
            {
                var mask = job?.mask;
                if (mask == null) return (null, 0, 0);
                return (FloodFill.ReadR8(mask), mask.width, mask.height);
            }
            finally
            {
                // PrepareJob の約束どおり、使い終わったら 1 回だけ減らす
                if (job != null) MaskCache.Trim();
            }
        }
    }
}
