using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Picking;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// 「画像を入れる」の置き方（パネルの［シール｜箱］。編集ごとに RecolorEdit.decalSticker で持つ。新しく ON にする編集はシール）。
    /// 中身はどちらも同じ箱（RecolorEdit.decalBox*）で、変わるのは Scene に出す操作部品と、ON・［リセット］で箱を置く場所
    /// </summary>
    internal enum DecalPlacement
    {
        /// <summary>クリックした所に、面の向き・標準サイズで置く。Scene には面の上の枠と大きさ・回転のつまみを出す</summary>
        Sticker = 0,
        /// <summary>選択範囲を囲む正面向きの箱に置く。Scene には箱とギズモ・面のつまみを出す</summary>
        Box = 1,
    }

    /// <summary>
    /// 「画像を入れる」の ON/OFF と画像の割り当て（RecolorEdit.decal*）。ON（初回）・［リセット］で箱を置く場所は置き方（DecalPlacement）で決まる:
    /// シールはクリックした所に面の向き・標準サイズ（StickerDefaultFor）、箱は GradientBox.DefaultFor（選択範囲の外接の箱・回転なし。
    /// グラデーションと同じく、最初に範囲を囲んでいれば迷わない）
    /// </summary>
    internal static class DecalBox
    {
        /// <summary>
        /// ON/OFF を変える（Undo 記録つき）。ON にしたときは confirmed にし、初めて ON にしたときだけ置き方を placement にして、箱をその既定の場所に置く
        /// （OFF→ON では前の箱と置き方のまま。ユーザー要望 2026-10-05）。連結なら全メンバーに同じ値を入れる（箱は現在の編集から決める）
        /// </summary>
        internal static void SetEnabled(ClickRecolor component, RecolorEdit edit, bool enabled, DecalPlacement placement)
        {
            if (component == null || edit == null || edit.decalEnabled == enabled) return;
            Undo.RecordObject(component, "Tocolo: 画像を入れるを切り替え");
            bool initialize = enabled && !edit.decalInitialized;
            var box = initialize ? DefaultFor(component, edit, placement) : default;
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.decalEnabled = enabled;
                // OFF にした編集も初期化済みにする（この印が無い版で ON にした編集を、次の ON で置き直さない）
                e.decalInitialized = true;
                if (!enabled) return;
                // 箱を置いた後に別の場所をクリックして、編集ごと仮の編集として捨てられないようにする
                // （ON にしただけで画像も色も無い空の編集が残るのは許容）
                e.confirmed = true;
                if (!initialize) return;
                e.decalSticker = placement == DecalPlacement.Sticker;
                PlaceBox(e, box.position, box.rotation, box.size);
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>編集の置き方（RecolorEdit.decalSticker）</summary>
        internal static DecalPlacement PlacementOf(RecolorEdit edit) => edit != null && edit.decalSticker ? DecalPlacement.Sticker : DecalPlacement.Box;

        /// <summary>
        /// パネルの［シール｜箱］: 編集（連結なら全メンバー）の置き方を変える（Undo 記録つき）。箱からシールに変えたときは、箱をシールの前提に整える
        /// （ConvertToSticker。箱で置いた箱は中心が体の中・正面向き・奥行きが範囲全体で、そのままシールのつまみで触ると面から外れて回ったり消えたりする）。
        /// シールから箱は、箱はどんな箱でも扱えるのでそのまま
        /// </summary>
        internal static void SetPlacement(ClickRecolor component, RecolorEdit edit, DecalPlacement placement)
        {
            if (component == null || edit == null || PlacementOf(edit) == placement) return;
            Undo.RecordObject(component, "Tocolo: 画像の置き方を変更");
            bool convert = placement == DecalPlacement.Sticker;
            var box = convert ? ConvertToSticker(component, edit) : default;
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.decalSticker = placement == DecalPlacement.Sticker;
                if (convert) PlaceBox(e, box.position, box.rotation, box.size);
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>
        /// edit の箱をシールの前提に整えた箱: 中心を、中心を通る奥行きの線上の選択範囲の一番手前の面へ（StickerSurface）、向きをそのまわりの面に合わせ
        /// （画像の面内の傾きは保つ）、大きさは見えている画像の大きさのまま奥行きを長い辺にそろえる（StickerFromBox）。面が見つからなければ向きと位置はそのまま
        /// </summary>
        private static (Vector3 position, Quaternion rotation, Vector3 size) ConvertToSticker(ClickRecolor component, RecolorEdit edit)
        {
            var rotation = Quaternion.Normalize(edit.decalBoxRotation);
            var forward = rotation * Vector3.forward;
            float depth = Mathf.Abs(edit.decalBoxSize.z);
            var fit = DecalLayerBuilder.FitScale(edit.decalTexture, edit.decalBoxSize, edit.decalKeepAspect);
            bool found = StickerSurface.TryFindFrontSurface(component, edit, edit.decalBoxPosition + forward * (depth * 0.5f), -forward, depth, out var point, out var renderer);
            var normal = forward;
            if (found)
            {
                var triangles = SurfaceTriangles.Collect(renderer, component.transform.worldToLocalMatrix, GroupTextures(component, edit));
                normal = SurfaceNormal(triangles, point, forward, ImageFootprintRadius(edit.decalBoxSize, fit));
            }
            return StickerFromBox(edit.decalBoxPosition, rotation, edit.decalBoxSize, fit, found, point, normal);
        }

        /// <summary>
        /// 箱（position・rotation・size、比率を保つ縮み fit）をシールの前提に整えた箱。found なら中心を面の上の点 surfacePoint へ、向きを surfaceNormal に
        /// （画像の面内の傾き TwistOf は保つ）。大きさは縦横を見えている画像の大きさ（size ÷ fit。反転は保つ）にし、奥行きをその長い辺にそろえる
        /// （縦横を画像に合わせても、比率を保つなら画像は同じ大きさに見える）
        /// </summary>
        internal static (Vector3 position, Quaternion rotation, Vector3 size) StickerFromBox(
            Vector3 position, Quaternion rotation, Vector3 size, Vector2 fit, bool found, Vector3 surfacePoint, Vector3 surfaceNormal)
        {
            rotation = Quaternion.Normalize(rotation);
            if (found)
            {
                rotation = FacingWithTwist(surfaceNormal, TwistOf(rotation));
                position = surfacePoint;
            }
            float width = Mathf.Abs(size.x) / Mathf.Max(fit.x, 1e-6f);
            float height = Mathf.Abs(size.y) / Mathf.Max(fit.y, 1e-6f);
            var sticker = new Vector3(
                Mathf.Sign(size.x == 0f ? 1f : size.x) * width,
                Mathf.Sign(size.y == 0f ? 1f : size.y) * height,
                Mathf.Sign(size.z == 0f ? 1f : size.z) * Mathf.Max(width, height));
            return (position, rotation, sticker);
        }

        /// <summary>パネルの［リセット］: 箱を編集の置き方の既定の場所に置き直す。画像と「▸ 設定」の中身は残す（Undo 記録つき。連結なら全メンバー）</summary>
        internal static void ResetSettings(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null) return;
            Undo.RecordObject(component, "Tocolo: 画像の箱をリセット");
            var box = DefaultFor(component, edit, PlacementOf(edit));
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.decalInitialized = true;
                PlaceBox(e, box.position, box.rotation, box.size);
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>placement の既定の箱（対象ルートのローカル）。シールの場所が決まらなければ箱の既定（選択範囲の外接）</summary>
        internal static (Vector3 position, Quaternion rotation, Vector3 size) DefaultFor(ClickRecolor component, RecolorEdit edit, DecalPlacement placement)
        {
            var points = placement == DecalPlacement.Sticker ? new List<Vector3>() : null;
            var box = GradientBox.DefaultFor(component, edit, points);
            if (placement == DecalPlacement.Sticker && TryGetClickSurface(component, edit, out var anchor, out var normal, out var renderer))
            {
                var triangles = SurfaceTriangles.Collect(renderer, component.transform.worldToLocalMatrix, GroupTextures(component, edit));
                return StickerFor(points, triangles, anchor, normal, AvatarHeight(component));
            }
            return (box.position, box.rotation, box.size);
        }

        private static void PlaceBox(RecolorEdit e, Vector3 position, Quaternion rotation, Vector3 size)
        {
            e.decalBoxPosition = position;
            e.decalBoxRotation = rotation;
            e.decalBoxSize = size;
        }

        /// <summary>
        /// 画像を割り当てる（Undo 記録つき）。連結なら全メンバーに同じ画像を入れる。
        /// 一度でも画像を入れた編集は confirmed にして、色の取り消しや画像 OFF で自動で捨てないようにする:
        /// 画像を OFF に戻した後に別の場所をクリックすると IsKept が false になり、仮の編集として捨てられて画像と箱が消えてしまう。
        /// 色を決めたときと同じ「一度決めたら残す」流儀
        /// </summary>
        internal static void SetTexture(ClickRecolor component, RecolorEdit edit, Texture2D texture)
        {
            if (component == null || edit == null) return;
            Undo.RecordObject(component, "Tocolo: 画像を変更");
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.decalTexture = texture;
                if (texture != null) e.confirmed = true;
            });
            EditorUtility.SetDirty(component);
        }

        // ── シールとして置く ──

        /// <summary>標準サイズ: アバターの身長に対する割合</summary>
        internal const float StickerHeightRatio = 0.1f;
        /// <summary>標準サイズ: 選択範囲の面に沿った短い辺に対する割合（小さいパーツでは範囲にほぼ収まる大きさにする）</summary>
        internal const float StickerSelectionRatio = 0.8f;
        /// <summary>標準サイズの下限（とても小さいパーツでも、つまみを掴める大きさは残す）</summary>
        internal const float StickerMinSize = 0.01f;

        /// <summary>
        /// シールの既定の箱: 面の上の点 anchor（表の向き anchorNormal。どちらも対象ルートのローカル）を中心に、面の向きで、標準サイズの正方形（奥行きも同じ）。
        /// 標準サイズは avatarHeight × StickerHeightRatio と、選択範囲の点 points を面に沿った 2 方向に並べた広がりの短い辺 × StickerSelectionRatio の小さいほう。
        /// 向きは標準サイズの半分の広さの三角形 triangles の向きの平均（SurfaceNormal）。
        /// 箱の中心を面の上に置くのは、画像をつかんで動かすときと同じく、面が手前にも奥にも曲がっていても奥行きの半分までは届くようにするため
        /// </summary>
        internal static (Vector3 position, Quaternion rotation, Vector3 size) StickerFor(
            IReadOnlyList<Vector3> points, IReadOnlyList<SurfaceTriangle> triangles, Vector3 anchor, Vector3 anchorNormal, float avatarHeight)
        {
            float size = Mathf.Max(avatarHeight * StickerHeightRatio, StickerMinSize);
            var rotation = RotationFacing(SurfaceNormal(triangles, anchor, anchorNormal, size * 0.5f));
            if (points != null && points.Count >= GradientBox.MinSelectionTexels)
            {
                var right = rotation * Vector3.right;
                var up = rotation * Vector3.up;
                float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
                foreach (var p in points)
                {
                    float x = Vector3.Dot(p, right), y = Vector3.Dot(p, up);
                    minX = Mathf.Min(minX, x);
                    maxX = Mathf.Max(maxX, x);
                    minY = Mathf.Min(minY, y);
                    maxY = Mathf.Max(maxY, y);
                }
                size = Mathf.Min(size, Mathf.Min(maxX - minX, maxY - minY) * StickerSelectionRatio);
            }
            size = Mathf.Max(size, StickerMinSize);
            return (anchor, rotation, new Vector3(size, size, size));
        }

        /// <summary>
        /// シールの四隅のつまみで箱を factor 倍にした大きさ: 縦横を等倍（比率と反転を保つ。長い辺が StickerMinSize の半分より小さくはしない）にし、
        /// 奥行きは縦横の長い辺にそろえる（符号は保つ）。中心は面の上なので、面がその半分まで手前・奥に曲がっていても届く。
        /// 奥行きも縦横と同じ割合で縮めると、箱モードで作った浅い箱や面から外れた中心のとき、面が奥行きから外れて画像が消えていく
        /// </summary>
        internal static Vector3 ScaleSticker(Vector3 size, float factor)
        {
            float longest = Mathf.Max(Mathf.Abs(size.x), Mathf.Abs(size.y));
            if (longest > 0f) factor = Mathf.Max(factor, StickerMinSize * 0.5f / longest);
            var scaled = new Vector3(size.x * factor, size.y * factor, size.z);
            scaled.z = Mathf.Sign(size.z == 0f ? 1f : size.z) * Mathf.Max(Mathf.Abs(scaled.x), Mathf.Abs(scaled.y));
            return scaled;
        }

        /// <summary>
        /// レイ（origin・direction）がシールの枠（center を中心に rotation の向きで、縦横の半分が half の長方形）の中を通るか。通るなら交点を point に入れる。
        /// 枠は前面に描くので、裏から見ていても通れば当たりにする。レイが枠の面と平行・枠が後ろにあるなら false
        /// </summary>
        internal static bool TryHitStickerFrame(Vector3 origin, Vector3 direction, Vector3 center, Quaternion rotation, Vector2 half, out Vector3 point)
        {
            point = center;
            var normal = rotation * Vector3.forward;
            float denominator = Vector3.Dot(direction, normal);
            if (Mathf.Abs(denominator) < 1e-8f) return false;
            float t = Vector3.Dot(center - origin, normal) / denominator;
            if (t < 0f) return false;
            var hit = origin + direction * t;
            var local = Quaternion.Inverse(rotation) * (hit - center);
            if (Mathf.Abs(local.x) > half.x || Mathf.Abs(local.y) > half.y) return false;
            point = hit;
            return true;
        }

        /// <summary>
        /// シールを置く点と表の向き（対象ルートのローカル）と、その点の Renderer: 直前のクリック（ToolSession.LastPick）がこの編集（連結ならメンバー）の
        /// テクスチャに当たっていればその点、無ければ主種の三角形の中心（TryGetSeedSurface）。どちらも取れなければ false
        /// </summary>
        private static bool TryGetClickSurface(ClickRecolor component, RecolorEdit edit, out Vector3 point, out Vector3 normal, out Renderer renderer)
        {
            point = Vector3.zero;
            normal = Vector3.zero;
            renderer = null;
            if (component == null || edit == null) return false;
            if (ToolSession.LastPick is PickHit hit && hit.renderer != null && hit.hasMainTexture
                && hit.renderer.transform.IsChildOf(component.transform) && GroupTextures(component, edit).Contains(hit.mainTexture.texture))
            {
                var toRoot = component.transform.worldToLocalMatrix;
                point = toRoot.MultiplyPoint3x4(hit.worldPosition);
                // hit の法線はカメラ側を向けてあるので、見えている側の向き
                normal = toRoot.MultiplyVector(hit.worldNormal);
                if (normal.sqrMagnitude > 1e-12f)
                {
                    normal.Normalize();
                    renderer = hit.renderer;
                    return true;
                }
            }
            renderer = edit.seedRenderer;
            return TryGetSeedSurface(component, edit, out point, out normal);
        }

        /// <summary>この編集（連結なら全メンバー）の元テクスチャ</summary>
        internal static HashSet<Texture2D> GroupTextures(ClickRecolor component, RecolorEdit edit)
        {
            var result = new HashSet<Texture2D>();
            foreach (var member in EditGroups.Members(component, edit))
            {
                if (member.sourceTexture != null) result.Add(member.sourceTexture);
            }
            return result;
        }

        /// <summary>アバターの身長の代わり: 対象ルート配下の Renderer の外接の箱（対象ルートのローカル）の高さ。Renderer が無ければ 1</summary>
        internal static float AvatarHeight(ClickRecolor component)
        {
            if (component == null) return 1f;
            var toRoot = component.transform.worldToLocalMatrix;
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            foreach (var renderer in component.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                var b = renderer.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3(
                        (i & 1) == 0 ? b.min.x : b.max.x,
                        (i & 2) == 0 ? b.min.y : b.max.y,
                        (i & 4) == 0 ? b.min.z : b.max.z);
                    float y = toRoot.MultiplyPoint3x4(corner).y;
                    min = Mathf.Min(min, y);
                    max = Mathf.Max(max, y);
                }
            }
            return max > min ? max - min : 1f;
        }

        // ── 面の向きに合わせる（シールの置き方・画像をつかんで動かすとき）──

        /// <summary>面の向きがアバターの軸（前後・左右・上下）からこの角度以内なら軸にそろえる（でこぼこで数度だけ傾くのを防ぐ）</summary>
        internal const float SnapAngle = 15f;
        /// <summary>面の向きがアバターの上下にこれ以上近い（内積）なら、画像の上をアバターの後ろ向きにする（正面から見て読める向き）</summary>
        private const float VerticalDot = 0.9f;

        /// <summary>
        /// anchor（面の上の点）での面の向き: radius 以内の三角形の表の向きの平均（SurfaceTriangles.TryAverageNormal。見えている側 anchorNormal と同じ側だけ）。
        /// まわりに三角形が無ければ anchorNormal（クリックした三角形の向き）。アバターの軸から SnapAngle 以内なら軸にそろえる
        /// </summary>
        internal static Vector3 SurfaceNormal(IReadOnlyList<SurfaceTriangle> triangles, Vector3 anchor, Vector3 anchorNormal, float radius)
        {
            if (!SurfaceTriangles.TryAverageNormal(triangles, anchor, anchorNormal, radius, out var normal))
            {
                normal = anchorNormal.sqrMagnitude > 1e-12f ? anchorNormal.normalized : Vector3.forward;
            }
            return SnapToAxis(normal, SnapAngle);
        }

        /// <summary>normal がアバターの軸（±X・±Y・±Z）から maxAngle 度以内ならその軸、そうでなければ normal のまま</summary>
        internal static Vector3 SnapToAxis(Vector3 normal, float maxAngle)
        {
            Vector3 best = normal;
            float bestAngle = maxAngle;
            foreach (var axis in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
            {
                float angle = Vector3.Angle(normal, axis);
                if (angle > bestAngle) continue;
                bestAngle = angle;
                best = axis;
            }
            return best;
        }

        /// <summary>
        /// +Z を normal（画像を投影する面の外向き）に向けた回転。画像の上はアバターの上を面に沿わせた向き。
        /// 上下を向いた面ではアバターの後ろ向きを上にする（正面から見て読める向き）
        /// </summary>
        internal static Quaternion RotationFacing(Vector3 normal)
        {
            var up = Mathf.Abs(Vector3.Dot(normal.normalized, Vector3.up)) > VerticalDot ? Vector3.back : Vector3.up;
            return Quaternion.LookRotation(normal, up);
        }

        /// <summary>
        /// rotation の画像の面内の傾き（度）: 同じ正面の向きで RotationFacing が決める上から、rotation の上が正面の向きのまわりに何度回っているか。
        /// 画像をつかんで別の向きの面へ動かしても、ギズモの輪で回した傾きを保つために使う（FacingWithTwist で戻す）
        /// </summary>
        internal static float TwistOf(Quaternion rotation)
        {
            var forward = rotation * Vector3.forward;
            return Vector3.SignedAngle(RotationFacing(forward) * Vector3.up, rotation * Vector3.up, forward);
        }

        /// <summary>正面（+Z）を normal に向け、画像の上を RotationFacing の上から normal のまわりに twist 度回した回転（TwistOf の逆）</summary>
        internal static Quaternion FacingWithTwist(Vector3 normal, float twist)
        {
            return Quaternion.AngleAxis(twist, normal.normalized) * RotationFacing(normal);
        }

        /// <summary>
        /// 画像が覆う広さの半径（対象ルートのローカル）: 箱の縦横から比率を保つ縮み（DecalLayerBuilder.FitScale）を除いた画像の縦横の、長い辺の半分。
        /// 小さすぎると平面を当てはめる点が足りないので GradientBox.SelectionMinSize 以上
        /// </summary>
        internal static float ImageFootprintRadius(Vector3 boxSize, Vector2 fit)
        {
            float width = Mathf.Abs(boxSize.x) / Mathf.Max(fit.x, 1e-6f);
            float height = Mathf.Abs(boxSize.y) / Mathf.Max(fit.y, 1e-6f);
            return Mathf.Max(Mathf.Max(width, height) * 0.5f, GradientBox.SelectionMinSize);
        }

        /// <summary>
        /// クリックした三角形（edit の主種）の中心と、表の向き（どちらも対象ルート＝component のローカル）。
        /// 主種の Renderer・メッシュ・三角形が取れなければ false
        /// </summary>
        private static bool TryGetSeedSurface(ClickRecolor component, RecolorEdit edit, out Vector3 center, out Vector3 normal)
        {
            center = Vector3.zero;
            normal = Vector3.zero;
            if (component == null || edit == null || edit.seedRenderer == null || edit.seedTriangle < 0) return false;
            if (!MeshRaycaster.TryGetMeshData(edit.seedRenderer, out var mesh, out var localToWorld, out bool owns) || mesh == null) return false;
            try
            {
                if (!mesh.isReadable || edit.seedSubmesh < 0 || edit.seedSubmesh >= mesh.subMeshCount) return false;
                var indices = mesh.GetTriangles(edit.seedSubmesh);
                int i = edit.seedTriangle * 3;
                if (i + 2 >= indices.Length) return false;
                var vertices = mesh.vertices;
                var toRoot = component.transform.worldToLocalMatrix * localToWorld;
                var a = toRoot.MultiplyPoint3x4(vertices[indices[i]]);
                var b = toRoot.MultiplyPoint3x4(vertices[indices[i + 1]]);
                var c = toRoot.MultiplyPoint3x4(vertices[indices[i + 2]]);
                center = (a + b + c) / 3f;
                // Unity の表の面は時計回りで、法線は Cross(b - a, c - a)。鏡像（負のスケール）なら巻き方が裏返るので反転する
                normal = Vector3.Cross(b - a, c - a);
                if (toRoot.determinant < 0f) normal = -normal;
                // 外積の長さは面積の 2 倍で、細かい三角形では Vector3.Normalize（長さ 1e-5 未満はゼロ）だと向きが消えるので長さで割る
                float length = normal.magnitude;
                if (!(length > 0f)) return false;
                normal /= length;
                return true;
            }
            finally
            {
                if (owns) Object.DestroyImmediate(mesh);
            }
        }
    }
}
