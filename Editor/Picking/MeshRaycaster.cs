// 流用元: dev.nekoare.tex-col-adjuster/Editor/Preview/MeshRaycaster.cs
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Picking
{
    /// <summary>
    /// コライダーを使わず、Renderer のメッシュ三角形に直接レイを当てて UV を求める。
    /// SkinnedMeshRenderer は現在のポーズで BakeMesh してから判定する。
    /// Scene ビューのスポイト（クリック位置のテクスチャ色取得）用。
    /// </summary>
    public static class MeshRaycaster
    {
        public struct RaycastHit
        {
            public Renderer renderer;
            /// <summary>ヒットしたサブメッシュ番号（≒マテリアルスロット）</summary>
            public int subMeshIndex;
            /// <summary>サブメッシュ内の三角形番号（GetTriangles(subMeshIndex) のインデックス / 3）</summary>
            public int triangleIndex;
            /// <summary>重心係数 (w0, w1, w2) = (1-u-v, u, v)。三角形の頂点 0,1,2 に対応</summary>
            public Vector3 barycentric;
            /// <summary>UV0 をバリセントリック補間した値</summary>
            public Vector2 uv;
            public float distance;
            /// <summary>ヒット位置（ワールド）</summary>
            public Vector3 worldPosition;
            /// <summary>ヒットした三角形の法線（ワールド・正規化済み。レイ側を向くよう反転済み）</summary>
            public Vector3 worldNormal;
        }

        /// <summary>
        /// Renderer から判定用メッシュと変換行列を取り出す。SkinnedMeshRenderer は現在のポーズで焼く。
        /// ownsMesh が true のとき、呼び出し側が使い終わったら DestroyImmediate すること（ホバー用のキャッシュに使える）。
        /// </summary>
        public static bool TryGetMeshData(Renderer renderer, out Mesh mesh, out Matrix4x4 localToWorld, out bool ownsMesh)
        {
            mesh = null;
            localToWorld = Matrix4x4.identity;
            ownsMesh = false;

            if (renderer is SkinnedMeshRenderer smr)
            {
                if (smr.sharedMesh == null) return false;
                mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                // useScale:false は Transform のスケールを焼き込んだ頂点を返す（Unity の legacy 挙動）ので、
                // 行列は位置と回転のみ。CHM／マスク作成ツールと同じ
                smr.BakeMesh(mesh, false);
                ownsMesh = true;
                var t = smr.transform;
                localToWorld = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                return true;
            }

            if (renderer is MeshRenderer mr)
            {
                var filter = mr.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) return false;
                mesh = filter.sharedMesh;
                localToWorld = mr.transform.localToWorldMatrix;
                return true;
            }

            return false;
        }

        /// <summary>
        /// ワールド空間のレイを renderer のメッシュに当てる。両面判定・最近傍の三角形を返す。
        /// </summary>
        public static bool Raycast(Ray worldRay, Renderer renderer, out RaycastHit hit)
        {
            hit = default;
            if (renderer == null) return false;
            if (!TryGetMeshData(renderer, out var mesh, out var localToWorld, out bool ownsMesh)) return false;

            try
            {
                // 粗い除外: ワールド AABB
                if (!renderer.bounds.IntersectRay(worldRay))
                    return false;

                return RaycastMesh(worldRay, mesh, localToWorld, renderer, out hit);
            }
            finally
            {
                if (ownsMesh) Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// レイ上の交点を「すべて」results に追加する（距離順にはソートしない。呼び出し側でソートすること）。
        /// 髪などカットアウト付きメッシュで、透明部分の交点をスキップして奥の面を拾うために使う。
        /// 戻り値: 追加した交点数。
        /// </summary>
        public static int RaycastMeshAll(Ray ray, Mesh mesh, Matrix4x4 localToWorld, Renderer renderer, System.Collections.Generic.List<RaycastHit> results)
        {
            if (results == null) return 0;
            var arrays = MeshArrays.From(mesh);
            return arrays != null ? RaycastMeshAll(ray, arrays, localToWorld, renderer, results) : 0;
        }

        /// <summary>
        /// メッシュから取り出した頂点・UV・三角形の配列。`mesh.vertices` 等は呼ぶたびに配列をコピーするので、
        /// ホバー判定（秒 25 回）で毎回取り直さないよう、呼び出し側（ScenePicker）がこれをキャッシュする
        /// </summary>
        public sealed class MeshArrays
        {
            public Vector3[] vertices;
            public Vector2[] uv;
            /// <summary>サブメッシュごとの三角形（三角形以外のトポロジは null）</summary>
            public int[][] triangles;

            /// <summary>読み取り不可・UV 無し・頂点無しなら null</summary>
            public static MeshArrays From(Mesh mesh)
            {
                if (mesh == null || !mesh.isReadable) return null;
                var vertices = mesh.vertices;
                var uvs = mesh.uv;
                if (vertices == null || vertices.Length == 0 || uvs == null || uvs.Length != vertices.Length) return null;
                var triangles = new int[mesh.subMeshCount][];
                for (int sub = 0; sub < triangles.Length; sub++)
                {
                    triangles[sub] = mesh.GetTopology(sub) == MeshTopology.Triangles ? mesh.GetTriangles(sub) : null;
                }
                return new MeshArrays { vertices = vertices, uv = uvs, triangles = triangles };
            }
        }

        /// <summary>取り出し済みの配列に対して、レイ上の交点を「すべて」results に追加する（RaycastMeshAll(Mesh…) と同じ）</summary>
        public static int RaycastMeshAll(Ray ray, MeshArrays arrays, Matrix4x4 localToWorld, Renderer renderer, System.Collections.Generic.List<RaycastHit> results)
        {
            if (results == null || arrays == null) return 0;
            var vertices = arrays.vertices;
            var uvs = arrays.uv;

            int added = 0;
            int subMeshCount = arrays.triangles.Length;
            for (int sub = 0; sub < subMeshCount; sub++)
            {
                var triangles = arrays.triangles[sub];
                if (triangles == null) continue;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    Vector3 v0 = localToWorld.MultiplyPoint3x4(vertices[triangles[i]]);
                    Vector3 v1 = localToWorld.MultiplyPoint3x4(vertices[triangles[i + 1]]);
                    Vector3 v2 = localToWorld.MultiplyPoint3x4(vertices[triangles[i + 2]]);

                    if (!IntersectTriangle(ray, v0, v1, v2, out float distance, out float u, out float v))
                        continue;

                    Vector2 uv0 = uvs[triangles[i]];
                    Vector2 uv1 = uvs[triangles[i + 1]];
                    Vector2 uv2 = uvs[triangles[i + 2]];

                    Vector3 normal = UnitNormal(v0, v1, v2);
                    if (Vector3.Dot(normal, ray.direction) > 0f) normal = -normal;

                    results.Add(new RaycastHit
                    {
                        renderer = renderer,
                        subMeshIndex = sub,
                        triangleIndex = i / 3,
                        barycentric = new Vector3(1f - u - v, u, v),
                        uv = uv0 * (1f - u - v) + uv1 * u + uv2 * v,
                        distance = distance,
                        worldPosition = ray.origin + ray.direction * distance,
                        worldNormal = normal,
                    });
                    added++;
                }
            }
            return added;
        }

        /// <summary>取り出し済みメッシュデータに対して判定する（ホバー用にメッシュをキャッシュしたい場合はこちら）。</summary>
        public static bool RaycastMesh(Ray ray, Mesh mesh, Matrix4x4 localToWorld, Renderer renderer, out RaycastHit hit)
        {
            hit = default;
            // 読み取り不可の警告は ScenePicker 側で出す（ここでは無言で外れにする）
            if (!mesh.isReadable) return false;

            var vertices = mesh.vertices;
            var uvs = mesh.uv;
            if (vertices == null || vertices.Length == 0 || uvs == null || uvs.Length != vertices.Length)
                return false;

            float bestDistance = float.MaxValue;
            bool found = false;

            int subMeshCount = mesh.subMeshCount;
            for (int sub = 0; sub < subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                var triangles = mesh.GetTriangles(sub);
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    Vector3 v0 = localToWorld.MultiplyPoint3x4(vertices[triangles[i]]);
                    Vector3 v1 = localToWorld.MultiplyPoint3x4(vertices[triangles[i + 1]]);
                    Vector3 v2 = localToWorld.MultiplyPoint3x4(vertices[triangles[i + 2]]);

                    if (!IntersectTriangle(ray, v0, v1, v2, out float distance, out float u, out float v))
                        continue;
                    if (distance >= bestDistance) continue;

                    Vector2 uv0 = uvs[triangles[i]];
                    Vector2 uv1 = uvs[triangles[i + 1]];
                    Vector2 uv2 = uvs[triangles[i + 2]];

                    Vector3 normal = UnitNormal(v0, v1, v2);
                    if (Vector3.Dot(normal, ray.direction) > 0f) normal = -normal; // レイ側を向ける

                    bestDistance = distance;
                    hit = new RaycastHit
                    {
                        renderer = renderer,
                        subMeshIndex = sub,
                        triangleIndex = i / 3,
                        barycentric = new Vector3(1f - u - v, u, v),
                        uv = uv0 * (1f - u - v) + uv1 * u + uv2 * v,
                        distance = distance,
                        worldPosition = ray.origin + ray.direction * distance,
                        worldNormal = normal,
                    };
                    found = true;
                }
            }

            return found;
        }

        /// <summary>Möller–Trumbore（両面判定）。u, v は v1, v2 のバリセントリック係数。</summary>
        /// <summary>
        /// 三角形（v0, v1, v2）の単位法線（Cross(v1 - v0, v2 - v0) の向き。面積 0 ならゼロ）。
        /// Vector3.normalized は長さが 1e-5 未満だとゼロを返すので、外積（面積の 2 倍）をそのまま正規化すると、
        /// 顔の目元のような面積が数 mm² の三角形で向きが消える。長さで割って求める
        /// </summary>
        public static Vector3 UnitNormal(Vector3 v0, Vector3 v1, Vector3 v2)
        {
            var n = Vector3.Cross(v1 - v0, v2 - v0);
            float length = n.magnitude;
            return length > 0f ? n / length : Vector3.zero;
        }

        public static bool IntersectTriangle(Ray ray, Vector3 v0, Vector3 v1, Vector3 v2,
            out float distance, out float u, out float v)
        {
            distance = 0f; u = 0f; v = 0f;
            const float epsilon = 1e-7f;

            Vector3 edge1 = v1 - v0;
            Vector3 edge2 = v2 - v0;
            Vector3 p = Vector3.Cross(ray.direction, edge2);
            float det = Vector3.Dot(edge1, p);
            if (Mathf.Abs(det) < epsilon) return false; // 平行（両面判定なので det の符号は問わない）

            float invDet = 1f / det;
            Vector3 t = ray.origin - v0;
            u = Vector3.Dot(t, p) * invDet;
            if (u < 0f || u > 1f) return false;

            Vector3 q = Vector3.Cross(t, edge1);
            v = Vector3.Dot(ray.direction, q) * invDet;
            if (v < 0f || u + v > 1f) return false;

            distance = Vector3.Dot(edge2, q) * invDet;
            return distance > epsilon;
        }
    }
}
