using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>
    /// 全チャート被覆 A: あるテクスチャを参照する全 (メッシュ, サブメッシュ) の全三角形を UV 空間に塗った合成マスク。
    /// 1 - A が「どのチャートにも属さないテクセル」＝パディングで広げてよい画素になる。テクスチャ単位でキャッシュする
    /// </summary>
    internal static class CoverageMask
    {
        private readonly struct Key : System.IEquatable<Key>
        {
            public readonly int textureId;
            public readonly int usersHash;
            public readonly int width;
            public readonly int height;

            public Key(int textureId, int usersHash, int width, int height)
            {
                this.textureId = textureId;
                this.usersHash = usersHash;
                this.width = width;
                this.height = height;
            }

            public bool Equals(Key other) =>
                textureId == other.textureId && usersHash == other.usersHash && width == other.width && height == other.height;
            public override bool Equals(object obj) => obj is Key other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return ((textureId * 397 ^ usersHash) * 397 ^ width) * 397 ^ height; }
            }
        }

        private static readonly Dictionary<Key, RenderTexture> s_cache = new Dictionary<Key, RenderTexture>();
        private static readonly List<Key> s_staleKeys = new List<Key>();

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            AssemblyReloadEvents.beforeAssemblyReload += ClearCache;
            EditorSceneManager.sceneClosing += (scene, removingScene) => ClearCache();
        }

        /// <summary>キャッシュした RT をすべて解放する（GetOrBuild が返した RT はこれ以降使えない）</summary>
        internal static void ClearCache()
        {
            foreach (var rt in s_cache.Values) MaskTextures.Destroy(rt);
            s_cache.Clear();
        }

        /// <summary>
        /// texture の全チャート被覆 A を返す（無ければ作る）。大きさは texture の縦横比を保ち、
        /// 長辺が size を超えないように縮める（元より大きくはしない）。それ以外は下の width×height 版と同じ
        /// </summary>
        internal static RenderTexture GetOrBuild(
            Texture2D texture,
            IReadOnlyList<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users,
            int size,
            out int skippedUnreadable)
        {
            skippedUnreadable = 0;
            if (texture == null || size <= 0) return null;
            float scale = Mathf.Min(1f, size / (float)Mathf.Max(texture.width, texture.height));
            int width = Mathf.Max(1, Mathf.RoundToInt(texture.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(texture.height * scale));
            return GetOrBuild(texture, users, width, height, out skippedUnreadable);
        }

        /// <summary>
        /// texture の全チャート被覆 A を width×height で返す（無ければ作る）。返す RT はキャッシュ所有なので呼び出し側で解放しないこと。
        /// 選択マスクと同じ大きさで作ると、マスクの縁と被覆の縁が画素単位で揃う（間に塗られない輪が残らない）。
        /// 各利用者の三角形はその利用者のマテリアルの Tiling/Offset（uv * uvScale + uvOffset）を掛けて塗る（折り返さない）。
        /// 読み取り不可・三角形以外のサブメッシュは塗らない。GPU が使えなければ null。
        /// skippedUnreadable には読み取り不可（Read/Write 無効）で塗れなかった利用者の数を返す（キャッシュを返すときも数える）
        /// </summary>
        internal static RenderTexture GetOrBuild(
            Texture2D texture,
            IReadOnlyList<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users,
            int width,
            int height,
            out int skippedUnreadable)
        {
            skippedUnreadable = 0;
            if (texture == null || users == null || width <= 0 || height <= 0) return null;
            if (!UvRasterizer.IsAvailable) return null;

            for (int i = 0; i < users.Count; i++)
            {
                var mesh = users[i].mesh;
                if (mesh != null && !mesh.isReadable) skippedUnreadable++;
            }

            var key = new Key(texture.GetInstanceID(), UsersHash(users), width, height);
            if (s_cache.TryGetValue(key, out var cached))
            {
                if (cached != null && cached.IsCreated()) return cached;
                MaskTextures.Destroy(cached);
                s_cache.Remove(key);
            }

            // 同じテクスチャ・同じ大きさで利用者の組が変わった古い被覆は、もう引かれないので捨てる
            s_staleKeys.Clear();
            foreach (var k in s_cache.Keys)
            {
                if (k.textureId == key.textureId && k.width == key.width && k.height == key.height) s_staleKeys.Add(k);
            }
            foreach (var k in s_staleKeys)
            {
                MaskTextures.Destroy(s_cache[k]);
                s_cache.Remove(k);
            }

            var rt = MaskTextures.Create(width, height, "ClickRecolor_Coverage");
            UvRasterizer.Clear(rt);
            var verts = new List<Vector2>();
            foreach (var (mesh, submesh, uvScale, uvOffset) in users)
            {
                if (mesh == null || !mesh.isReadable) continue;
                if (submesh < 0 || submesh >= mesh.subMeshCount) continue;
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                var uv = mesh.uv;
                if (uv == null || uv.Length != mesh.vertexCount) continue;
                var triangles = mesh.GetTriangles(submesh);

                // 利用者ごとに描く。塗りは「当たった画素に 1」だけなので同じ RT に重ねると max 合成になる
                verts.Clear();
                int triCount = triangles.Length / 3;
                for (int t = 0; t < triCount; t++)
                {
                    UvRasterizer.AppendTriangle(verts, uv, triangles, t, width, height, uvScale, uvOffset);
                }
                UvRasterizer.Draw(rt, verts);
            }
            s_cache[key] = rt;
            return rt;
        }

        /// <summary>利用者の組（順序込み）の畳み込み。メッシュの形や Tiling/Offset が変わったら別の鍵になる</summary>
        private static int UsersHash(IReadOnlyList<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users)
        {
            unchecked
            {
                int h = 17;
                for (int i = 0; i < users.Count; i++)
                {
                    var (mesh, submesh, uvScale, uvOffset) = users[i];
                    h = h * 31 + (mesh != null ? mesh.GetInstanceID() : 0);
                    h = h * 31 + submesh;
                    h = h * 31 + UvChartDetector.Fingerprint(mesh);
                    h = h * 31 + uvScale.x.GetHashCode();
                    h = h * 31 + uvScale.y.GetHashCode();
                    h = h * 31 + uvOffset.x.GetHashCode();
                    h = h * 31 + uvOffset.y.GetHashCode();
                }
                return h;
            }
        }
    }
}
