using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Decal
{
    /// <summary>
    /// 「画像を入れる」の三角形単位の判定（箱のローカル座標で見る）。第 1 段の焼き込み（DecalLayerBuilder）と
    /// 第 2 段の重ね貼りメッシュ（DecalOverlayMesh）で同じ判定を使い、両者で「どの面に貼るか」がずれないようにする
    /// </summary>
    internal static class DecalProjection
    {
        /// <summary>
        /// 箱のローカルに移した三角形（インデックス順 v0, v1, v2）の幾何法線 cross(v1 − v0, v2 − v0) の z が正（flip なら負）か＝箱の +Z 側を向くか。
        /// flip は鏡像の Renderer 用（Unity はカリングを反転して描くので、見えている側が幾何法線の逆になる）
        /// </summary>
        internal static bool IsFrontFacing(Vector3 v0, Vector3 v1, Vector3 v2, bool flip)
        {
            var n = Vector3.Cross(v1 - v0, v2 - v0);
            return (flip ? -n.z : n.z) > 0f;
        }

        /// <summary>
        /// 箱のローカルに移した三角形が箱（中心原点・半分の大きさ half）と交わるか（分離軸判定。境界で接するだけでも交わる扱い）。
        /// 頂点が箱の中にあるかだけで見ると、ポリゴンの少ない面に小さく貼ったとき（箱が三角形の内側に収まり、どの頂点も箱の外）に何も拾えないため、
        /// 三角形と AABB の分離軸 13 本（箱の 3 軸・三角形の法線・三角形の辺 × 箱の軸 9 本）で判定する
        /// </summary>
        internal static bool TriangleTouchesBox(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 half)
        {
            // 箱の 3 軸（三角形の AABB と箱が重なるか）
            if (Mathf.Min(v0.x, Mathf.Min(v1.x, v2.x)) > half.x || Mathf.Max(v0.x, Mathf.Max(v1.x, v2.x)) < -half.x) return false;
            if (Mathf.Min(v0.y, Mathf.Min(v1.y, v2.y)) > half.y || Mathf.Max(v0.y, Mathf.Max(v1.y, v2.y)) < -half.y) return false;
            if (Mathf.Min(v0.z, Mathf.Min(v1.z, v2.z)) > half.z || Mathf.Max(v0.z, Mathf.Max(v1.z, v2.z)) < -half.z) return false;

            // 三角形の辺 × 箱の軸（9 本）
            var e0 = v1 - v0;
            var e1 = v2 - v1;
            var e2 = v0 - v2;
            if (Separated(Vector3.Cross(Vector3.right, e0), v0, v1, v2, half)) return false;
            if (Separated(Vector3.Cross(Vector3.right, e1), v0, v1, v2, half)) return false;
            if (Separated(Vector3.Cross(Vector3.right, e2), v0, v1, v2, half)) return false;
            if (Separated(Vector3.Cross(Vector3.up, e0), v0, v1, v2, half)) return false;
            if (Separated(Vector3.Cross(Vector3.up, e1), v0, v1, v2, half)) return false;
            if (Separated(Vector3.Cross(Vector3.up, e2), v0, v1, v2, half)) return false;
            if (Separated(Vector3.Cross(Vector3.forward, e0), v0, v1, v2, half)) return false;
            if (Separated(Vector3.Cross(Vector3.forward, e1), v0, v1, v2, half)) return false;
            if (Separated(Vector3.Cross(Vector3.forward, e2), v0, v1, v2, half)) return false;

            // 三角形の法線（三角形の平面と箱が交わるか）。大きさ 0 の軸（退化した三角形）は分離しない扱いになる
            var n = Vector3.Cross(e0, e1);
            float d = Vector3.Dot(n, v0);
            float r = half.x * Mathf.Abs(n.x) + half.y * Mathf.Abs(n.y) + half.z * Mathf.Abs(n.z);
            return Mathf.Abs(d) <= r;
        }

        /// <summary>軸 axis に三角形と箱を射影し、区間が離れているか（接するだけなら離れていない）</summary>
        private static bool Separated(Vector3 axis, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 half)
        {
            float p0 = Vector3.Dot(axis, v0);
            float p1 = Vector3.Dot(axis, v1);
            float p2 = Vector3.Dot(axis, v2);
            float r = half.x * Mathf.Abs(axis.x) + half.y * Mathf.Abs(axis.y) + half.z * Mathf.Abs(axis.z);
            return Mathf.Min(p0, Mathf.Min(p1, p2)) > r || Mathf.Max(p0, Mathf.Max(p1, p2)) < -r;
        }

        /// <summary>
        /// 箱のローカルの位置 p から画像の UV（DecalLayer.shader と同じ式）。safeSize は符号付きで 0 でない箱の大きさ（DecalLayerBuilder.SafeSize）。
        /// 正面（+Z 側）から見て正しい向きになるよう u は反転する（u = 0.5 − x / size.x, v = y / size.y + 0.5）。
        /// 比率を保つ拡大 fit（DecalLayerBuilder.FitScale）を中心基準で掛ける。0..1 の外もそのまま返す
        /// </summary>
        internal static Vector2 ProjectUv(Vector3 p, Vector3 safeSize, Vector2 fit)
        {
            var uv = new Vector2(0.5f - p.x / safeSize.x, p.y / safeSize.y + 0.5f);
            return new Vector2((uv.x - 0.5f) * fit.x + 0.5f, (uv.y - 0.5f) * fit.y + 0.5f);
        }
    }
}
