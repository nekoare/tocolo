using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Picking;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// 「画像を入れる」の ON/OFF と画像の割り当て（RecolorEdit.decal*）。箱の既定は GradientBox.DefaultFor（選択範囲の外接の箱・回転なし）。
    /// グラデーションと同じ置き方にするのは、どちらも「選択範囲に掛ける付属設定＋専用の箱」で、最初に範囲を囲んでいれば迷わないため。
    /// パネルの［箱の向きを合わせる］（AlignToSurface）で、クリックした所の面の向きに合わせて置き直せる（ユーザー要望 2026-10-06）
    /// </summary>
    internal static class DecalBox
    {
        /// <summary>
        /// ON/OFF を変える（Undo 記録つき）。ON にしたときは confirmed にし、初めて ON にしたときだけ箱を既定（選択範囲の外接）に置く
        /// （OFF→ON では前の箱のまま。ユーザー要望 2026-10-05）。連結なら全メンバーに同じ値を入れる（箱は現在の編集から決める）
        /// </summary>
        internal static void SetEnabled(ClickRecolor component, RecolorEdit edit, bool enabled)
        {
            if (component == null || edit == null || edit.decalEnabled == enabled) return;
            Undo.RecordObject(component, "Tocolo: 画像を入れるを切り替え");
            bool initialize = enabled && !edit.decalInitialized;
            var box = initialize ? GradientBox.DefaultFor(component, edit) : default;
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.decalEnabled = enabled;
                // OFF にした編集も初期化済みにする（この印が無い版で ON にした編集を、次の ON で置き直さない）
                e.decalInitialized = true;
                if (!enabled) return;
                // 箱を置いた後に別の場所をクリックして、編集ごと仮の編集として捨てられないようにする
                // （ON にしただけで画像も色も無い空の編集が残るのは許容）
                e.confirmed = true;
                if (initialize) PlaceBox(e, box.position, box.rotation, box.size);
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>パネルの［リセット］: 箱を既定（選択範囲の外接）に置き直す。画像と詳細設定は残す（Undo 記録つき。連結なら全メンバー）</summary>
        internal static void ResetSettings(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null) return;
            Undo.RecordObject(component, "Tocolo: 画像の箱をリセット");
            var box = GradientBox.DefaultFor(component, edit);
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.decalInitialized = true;
                PlaceBox(e, box.position, box.rotation, box.size);
            });
            EditorUtility.SetDirty(component);
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

        // ── 箱の向きを面に合わせる（パネルの［箱の向きを合わせる］）──

        /// <summary>まわりの点を集める広さ（選択範囲の外接の箱の一番長い辺に対する割合）</summary>
        internal const float NeighborhoodRatio = 0.3f;
        /// <summary>当てはめた面の向きがアバターの軸（前後・左右・上下）からこの角度以内なら軸にそろえる（でこぼこで数度だけ傾くのを防ぐ）</summary>
        internal const float SnapAngle = 15f;
        /// <summary>
        /// 平面とみなす条件: 点の広がりの一番小さい方向の分散が、2 番目の方向の分散のこの割合以下（棒状・塊状の点は向きが決まらないので使わない）
        /// </summary>
        internal const float PlanarityRatio = 0.5f;
        /// <summary>面の向きがアバターの上下にこれ以上近い（内積）なら、画像の上をアバターの後ろ向きにする（正面から見て読める向き）</summary>
        private const float VerticalDot = 0.9f;

        /// <summary>
        /// 画像の箱を、クリックした所の面の向きに合わせる（Undo 記録つき。連結なら全メンバー）。向き（箱の +Z ＝画像を投影する面の外向き）は
        /// クリックした三角形のまわりの表面の点に平面を当てはめて決める（TryOrient）。大きさは変えない（ユーザー要望 2026-10-06）。
        /// 位置は面に沿った方向はそのままで、奥行きだけ動かして箱の中心を当てはめた面の上に乗せる（PlaceOnPlane。中心が体の中にあると、
        /// 横に向けた箱の正面が体に埋まって画像が出ないため）。
        /// 範囲の点やクリックした三角形が取れない・平面が決まらないときは箱を動かさずに false（正面向きに戻すと押した意味と逆になるため）
        /// </summary>
        internal static bool AlignToSurface(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null) return false;
            var points = new List<Vector3>();
            GradientBox.DefaultFor(component, edit, points);
            if (points.Count < GradientBox.MinSelectionTexels
                || !TryGetSeedSurface(component, edit, out var anchor, out var anchorNormal)
                || !TryOrient(points, anchor, anchorNormal, out var rotation, out var planePoint))
            {
                return false;
            }
            var position = PlaceOnPlane(edit.decalBoxPosition, rotation * Vector3.forward, planePoint);
            Undo.RecordObject(component, "Tocolo: 画像の箱の向きを合わせる");
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.decalInitialized = true;
                e.decalBoxPosition = position;
                e.decalBoxRotation = rotation;
            });
            EditorUtility.SetDirty(component);
            return true;
        }

        /// <summary>center を normal の向きにだけ動かして、planePoint を通り normal に垂直な面の上に乗せた位置（面に沿った方向は変えない）</summary>
        internal static Vector3 PlaceOnPlane(Vector3 center, Vector3 normal, Vector3 planePoint)
        {
            return center - normal * Vector3.Dot(center - planePoint, normal);
        }

        /// <summary>
        /// points（選択範囲の表面の位置）のうち anchor のまわり（外接の箱の一番長い辺 × NeighborhoodRatio 以内）の点に平面を当てはめ、
        /// その法線（anchorNormal の側を外向き）を +Z にした回転を返す。アバターの軸に近ければ軸にそろえる（SnapToAxis）。
        /// 画像の上はアバターの上を面に沿わせた向き（上下を向いた面ではアバターの後ろ向き）。planePoint は当てはめた点の重心（面の上の点）。
        /// 点が足りない・平面が決まらなければ false
        /// </summary>
        internal static bool TryOrient(IReadOnlyList<Vector3> points, Vector3 anchor, Vector3 anchorNormal, out Quaternion rotation, out Vector3 planePoint)
        {
            rotation = Quaternion.identity;
            planePoint = anchor;
            if (points == null || points.Count < GradientBox.MinSelectionTexels || anchorNormal.sqrMagnitude < 1e-12f) return false;

            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (var p in points)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            var extent = max - min;
            float radius = Mathf.Max(Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z)) * NeighborhoodRatio, GradientBox.SelectionMinSize);
            float radiusSq = radius * radius;
            var near = new List<Vector3>();
            foreach (var p in points)
            {
                if ((p - anchor).sqrMagnitude <= radiusSq) near.Add(p);
            }
            if (near.Count < GradientBox.MinSelectionTexels || !TryFitPlane(near, out var normal)) return false;
            var sum = Vector3.zero;
            foreach (var p in near) sum += p;
            planePoint = sum / near.Count;

            if (Vector3.Dot(normal, anchorNormal) < 0f) normal = -normal;
            normal = SnapToAxis(normal, SnapAngle);
            rotation = RotationFacing(normal);
            return true;
        }

        /// <summary>
        /// points に平面を当てはめた法線（点の広がりの一番小さい方向。単位ベクトル、向きの符号は不定）。
        /// 点が 3 つ未満・広がりが無い・平面とみなせない（PlanarityRatio）なら false
        /// </summary>
        internal static bool TryFitPlane(IReadOnlyList<Vector3> points, out Vector3 normal)
        {
            normal = Vector3.forward;
            if (points == null || points.Count < 3) return false;
            double cx = 0, cy = 0, cz = 0;
            foreach (var p in points)
            {
                cx += p.x;
                cy += p.y;
                cz += p.z;
            }
            cx /= points.Count;
            cy /= points.Count;
            cz /= points.Count;
            var a = new double[3, 3];
            foreach (var p in points)
            {
                double x = p.x - cx, y = p.y - cy, z = p.z - cz;
                a[0, 0] += x * x; a[0, 1] += x * y; a[0, 2] += x * z;
                a[1, 1] += y * y; a[1, 2] += y * z; a[2, 2] += z * z;
            }
            a[1, 0] = a[0, 1];
            a[2, 0] = a[0, 2];
            a[2, 1] = a[1, 2];

            var vectors = new double[3, 3];
            SymmetricEigen(a, vectors);
            // 固有値の小さい順に並べる
            var order = new[] { 0, 1, 2 };
            Array.Sort(order, (i, j) => a[i, i].CompareTo(a[j, j]));
            double smallest = Math.Max(a[order[0], order[0]], 0), middle = a[order[1], order[1]];
            if (!(middle > 1e-12) || smallest > PlanarityRatio * middle) return false;
            int k = order[0];
            normal = new Vector3((float)vectors[0, k], (float)vectors[1, k], (float)vectors[2, k]).normalized;
            return normal.sqrMagnitude > 0.5f;
        }

        /// <summary>
        /// 3×3 の対称行列 a の固有値分解（Jacobi 法）。終わると a の対角が固有値、vectors の各列が対応する固有ベクトル（a は書き換わる）
        /// </summary>
        private static void SymmetricEigen(double[,] a, double[,] vectors)
        {
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) vectors[i, j] = i == j ? 1 : 0;
            }
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
                if (off < 1e-30) return;
                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-30) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = (theta >= 0 ? 1 : -1) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq;
                            a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk;
                            a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = vectors[k, p], vkq = vectors[k, q];
                            vectors[k, p] = c * vkp - s * vkq;
                            vectors[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }
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
                if (normal.sqrMagnitude < 1e-16f) return false;
                normal.Normalize();
                return true;
            }
            finally
            {
                if (owns) Object.DestroyImmediate(mesh);
            }
        }
    }
}
