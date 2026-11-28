using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Copy
{
    /// <summary>
    /// 箱・点・長さを、対象ルート基準と髪（種の Renderer）基準のあいだで直す。
    /// 編集は箱を対象ルートのローカルで持つが、素体が違うと同じ髪でも位置や大きさが変わるので、髪を基準にして運ぶ
    /// </summary>
    internal static class HairSpace
    {
        /// <summary>
        /// 髪の基準にする Transform。SkinnedMeshRenderer の Transform はアバターの直下に置かれていて
        /// 頭の位置と関係ないことが多いので、ルートボーンがあればそちらを使う
        /// </summary>
        internal static Transform BasisOf(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr && smr.rootBone != null) return smr.rootBone;
            return renderer != null ? renderer.transform : null;
        }

        internal static Matrix4x4 RootToBasis(Transform root, Transform basis) => basis.worldToLocalMatrix * root.localToWorldMatrix;

        internal static void ToBasis(Matrix4x4 rootToBasis, ref Vector3 position, ref Quaternion rotation, ref Vector3 size)
        {
            Apply(rootToBasis, ref position, ref rotation, ref size);
        }

        internal static void FromBasis(Matrix4x4 rootToBasis, ref Vector3 position, ref Quaternion rotation, ref Vector3 size)
        {
            Apply(rootToBasis.inverse, ref position, ref rotation, ref size);
        }

        internal static Vector3 PointToBasis(Matrix4x4 rootToBasis, Vector3 point) => rootToBasis.MultiplyPoint3x4(point);
        internal static Vector3 PointFromBasis(Matrix4x4 rootToBasis, Vector3 point) => rootToBasis.inverse.MultiplyPoint3x4(point);

        /// <summary>
        /// 行列の一様な倍率（各軸の長さの平均）。箱は回転した軸ごとの不均一な拡大を表せないので、
        /// 髪の拡大は一様とみなす（アバターの髪で軸ごとに違う拡大はまず無い）
        /// </summary>
        internal static float ScaleOf(Matrix4x4 m)
        {
            float x = ((Vector3)m.GetColumn(0)).magnitude;
            float y = ((Vector3)m.GetColumn(1)).magnitude;
            float z = ((Vector3)m.GetColumn(2)).magnitude;
            return (x + y + z) / 3f;
        }

        private static void Apply(Matrix4x4 m, ref Vector3 position, ref Quaternion rotation, ref Vector3 size)
        {
            position = m.MultiplyPoint3x4(position);
            float scale = ScaleOf(m);
            var forward = m.MultiplyVector(rotation * Vector3.forward);
            var up = m.MultiplyVector(rotation * Vector3.up);
            if (forward.sqrMagnitude > 1e-12f && up.sqrMagnitude > 1e-12f) rotation = Quaternion.LookRotation(forward, up);
            size *= scale;
        }
    }
}
