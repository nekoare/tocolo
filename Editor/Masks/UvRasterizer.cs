// 流用元: com.nekoare.mask-creation-tool/Editor/Processing/IslandSelector.cs
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>
    /// Rasterize.compute の C# ラッパー。UV 三角形をマスク RT に「1」で塗る（何度呼んでも和集合）。
    /// 三角形ごとの外接矩形（辺の 1px 許容ぶん広げる）を 8×8 のタイルに分けて CPU で列挙し、
    /// 1 グループ = 1 タイルで Dispatch する（画素 × 三角形の総当たりをしない）
    /// </summary>
    internal static class UvRasterizer
    {
        private const int ThreadGroupSize = 16;
        /// <summary>CSRasterizeTri のタイルの一辺（numthreads(8, 8, 1) と揃える）</summary>
        private const int TileSize = 8;
        /// <summary>1 回の Dispatch のグループ数の上限（x 方向）</summary>
        private const int MaxGroupsPerDispatch = 65535;

        private static ComputeShader s_shader;
        private static int s_kernelRasterize;
        private static int s_kernelClear;

        private static readonly int VertsId = Shader.PropertyToID("Verts");
        private static readonly int TriTilesId = Shader.PropertyToID("TriTiles");
        private static readonly int TileStartId = Shader.PropertyToID("TileStart");
        private static readonly int WidthId = Shader.PropertyToID("Width");
        private static readonly int HeightId = Shader.PropertyToID("Height");
        private static readonly int OutId = Shader.PropertyToID("Out");

        private static ComputeShader Compute
        {
            get
            {
                if (s_shader == null)
                {
                    s_shader = ShaderAssets.Load(ShaderAssets.RasterizeGuid);
                    if (s_shader != null)
                    {
                        s_kernelRasterize = s_shader.FindKernel("CSRasterizeTri");
                        s_kernelClear = s_shader.FindKernel("CSClear");
                    }
                }
                return s_shader;
            }
        }

        internal static bool IsAvailable =>
            SystemInfo.supportsComputeShaders
            && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.R8)
            && Compute != null;

        /// <summary>target を 0 で埋める</summary>
        internal static void Clear(RenderTexture target)
        {
            var shader = Compute;
            if (shader == null || target == null) return;
            shader.SetInt(WidthId, target.width);
            shader.SetInt(HeightId, target.height);
            shader.SetTexture(s_kernelClear, OutId, target);
            shader.Dispatch(s_kernelClear, Groups(target.width), Groups(target.height), 1);
        }

        /// <summary>
        /// uv と triangles（サブメッシュの GetTriangles 結果）の三角形 tri に、マテリアルの Tiling/Offset
        /// （uv * uvScale + uvOffset）を掛けてから width×height のテクセル座標に直し、dst に 3 頂点追加する。
        /// 0..1 の外は折り返さない（テクスチャの外へ出た部分は塗られない）
        /// </summary>
        internal static void AppendTriangle(
            List<Vector2> dst, Vector2[] uv, int[] triangles, int tri, int width, int height, Vector2 uvScale, Vector2 uvOffset)
        {
            int t3 = tri * 3;
            dst.Add(ToTexel(uv[triangles[t3]], width, height, uvScale, uvOffset));
            dst.Add(ToTexel(uv[triangles[t3 + 1]], width, height, uvScale, uvOffset));
            dst.Add(ToTexel(uv[triangles[t3 + 2]], width, height, uvScale, uvOffset));
        }

        private static Vector2 ToTexel(Vector2 uv, int width, int height, Vector2 uvScale, Vector2 uvOffset)
        {
            Vector2 t = Vector2.Scale(uv, uvScale) + uvOffset;
            return new Vector2(t.x * width, t.y * height);
        }

        /// <summary>texelVerts（テクセル座標、3 頂点/三角形）の三角形を target に 1 で塗る。消去はしない</summary>
        internal static void Draw(RenderTexture target, List<Vector2> texelVerts)
        {
            var shader = Compute;
            if (shader == null || target == null || texelVerts == null) return;
            int triangleCount = texelVerts.Count / 3;
            if (triangleCount == 0) return;

            var tiles = BuildTriangleTiles(texelVerts, triangleCount, target.width, target.height, out int tileCount);
            if (tileCount == 0) return;

            var vertBuffer = new ComputeBuffer(triangleCount * 3, sizeof(float) * 2);
            var tileBuffer = new ComputeBuffer(tileCount, sizeof(int) * 4);
            try
            {
                vertBuffer.SetData(texelVerts, 0, 0, triangleCount * 3);
                tileBuffer.SetData(tiles, 0, 0, tileCount * 4);
                shader.SetInt(WidthId, target.width);
                shader.SetInt(HeightId, target.height);
                shader.SetBuffer(s_kernelRasterize, VertsId, vertBuffer);
                shader.SetBuffer(s_kernelRasterize, TriTilesId, tileBuffer);
                shader.SetTexture(s_kernelRasterize, OutId, target);
                for (int start = 0; start < tileCount; start += MaxGroupsPerDispatch)
                {
                    shader.SetInt(TileStartId, start);
                    shader.Dispatch(s_kernelRasterize, Mathf.Min(MaxGroupsPerDispatch, tileCount - start), 1, 1);
                }
            }
            finally
            {
                vertBuffer.Release();
                tileBuffer.Release();
            }
        }

        /// <summary>
        /// 各三角形の外接矩形（辺の 1px 許容ぶん ±1 広げ、画素の丸めの誤差ぶんさらに 1px 余裕を持たせる）に掛かる
        /// 8×8 タイルを (三角形番号, タイル x, タイル y, 0) の 4 int ずつ並べて返す。画像の外に出る部分は切り詰め、
        /// 画像に掛からない三角形・座標が有限でない三角形は含めない（どちらも総当たりのときに塗られなかったもの）。
        /// 厳密な内外判定はシェーダー側で行うので、ここは広めに取ってよい
        /// </summary>
        private static int[] BuildTriangleTiles(List<Vector2> texelVerts, int triangleCount, int width, int height, out int tileCount)
        {
            // 1 巡目で数え、2 巡目で詰める（大きい配列を伸ばし直さない）
            long total = 0;
            for (int t = 0; t < triangleCount; t++)
            {
                if (TryGetTileRange(texelVerts, t, width, height, out var lo, out var hi))
                {
                    total += (long)(hi.x - lo.x + 1) * (hi.y - lo.y + 1);
                }
            }
            // バッファ（16 byte/タイル）が 2GB を超える量は扱わない。通常は 4K・10 万三角形でも数十万タイル
            if (total > int.MaxValue / 16)
            {
                throw new System.InvalidOperationException($"ラスタライズするタイルが多すぎます（{total}）");
            }
            tileCount = (int)total;

            var tiles = new int[tileCount * 4];
            int n = 0;
            for (int t = 0; t < triangleCount; t++)
            {
                if (!TryGetTileRange(texelVerts, t, width, height, out var lo, out var hi)) continue;
                for (int ty = lo.y; ty <= hi.y; ty++)
                {
                    for (int tx = lo.x; tx <= hi.x; tx++)
                    {
                        int k = n * 4;
                        tiles[k] = t;
                        tiles[k + 1] = tx;
                        tiles[k + 2] = ty;
                        tiles[k + 3] = 0;
                        n++;
                    }
                }
            }
            return tiles;
        }

        /// <summary>三角形 t の外接矩形に掛かるタイルの範囲（両端含む）。画像に掛からない・座標が有限でなければ false</summary>
        private static bool TryGetTileRange(
            List<Vector2> texelVerts, int t, int width, int height, out Vector2Int lo, out Vector2Int hi)
        {
            lo = hi = default;
            var a = texelVerts[t * 3];
            var b = texelVerts[t * 3 + 1];
            var c = texelVerts[t * 3 + 2];
            if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c)) return false;

            // 画素中心 (x + 0.5) が [min - 1, max + 1] に入る画素を含む範囲。丸めの差を見込んで 1px 余分に取る
            float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - 2f;
            float minY = Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - 2f;
            float maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x)) + 2f;
            float maxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y)) + 2f;
            if (maxX < 0f || maxY < 0f || minX >= width || minY >= height) return false;

            // 巨大な座標で int があふれないよう、画像の範囲に切り詰めてから整数にする
            int x0 = Mathf.FloorToInt(Mathf.Clamp(minX, 0f, width - 1));
            int y0 = Mathf.FloorToInt(Mathf.Clamp(minY, 0f, height - 1));
            int x1 = Mathf.FloorToInt(Mathf.Clamp(maxX, 0f, width - 1));
            int y1 = Mathf.FloorToInt(Mathf.Clamp(maxY, 0f, height - 1));
            lo = new Vector2Int(x0 / TileSize, y0 / TileSize);
            hi = new Vector2Int(x1 / TileSize, y1 / TileSize);
            return true;
        }

        private static bool IsFinite(Vector2 v) =>
            !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y);

        private static int Groups(int size) => (size + ThreadGroupSize - 1) / ThreadGroupSize;
    }
}
