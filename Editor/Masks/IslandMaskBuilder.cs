using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>島マスクの入力</summary>
    internal sealed class IslandRequest
    {
        public Mesh mesh;
        public int submesh;
        /// <summary>クリックした三角形（サブメッシュ内ローカル番号 = PickHit.triangleIndex）</summary>
        public int seedTriangle;
        /// <summary>
        /// クリック位置のテクスチャ座標（0..1）。4 連結の種画素に使う。
        /// マテリアルの Tiling/Offset 適用後の値（PickHit.uv）を渡す前提（生の UV0 ではない）。
        /// PickHit.uv は [0,1) に折り返されているが、チャートのラスタライズは折り返さないので、
        /// Tiling/Offset 後に 0..1 をはみ出すチャートでは種がチャートから外れることがある（そのときは連結で絞らない）
        /// </summary>
        public Vector2 seedUv;
        /// <summary>マテリアルの Tiling（テクスチャ座標 = uv * uvScale + uvOffset）</summary>
        public Vector2 uvScale = Vector2.one;
        /// <summary>マテリアルの Offset</summary>
        public Vector2 uvOffset = Vector2.zero;
        /// <summary>どのチャートにも属さない画素へ広げる幅（px、マスク解像度）</summary>
        public int padding;
        /// <summary>オープンの半径（ゴマ塩除去。0 なら掛けない）</summary>
        public int cleanupRadius;
        public int width;
        public int height;
        /// <summary>全チャート被覆 A（CoverageMask）。width×height と同じ大きさで作ったものを渡す。null ならパディングしない</summary>
        public RenderTexture coverage;
    }

    /// <summary>
    /// 島モードの選択マスクを作る（設計 §6.2）:
    /// チャート三角形のラスタライズ → クローズ(1) → オープン(cleanupRadius) → 種から 4 連結で到達しない画素を落とす
    /// → 被覆の外へだけパディング → 1px ぼかし
    /// </summary>
    internal static class IslandMaskBuilder
    {
        /// <summary>
        /// 島マスク（R8・Linear、width×height、0..1）を返す。返した RT は呼び出し側の所有なので、
        /// 使い終わったら MaskTextures.Destroy すること。
        /// GPU が使えない・メッシュが読めない・三角形番号が範囲外なら null
        /// </summary>
        internal static RenderTexture Build(IslandRequest r)
        {
            if (r == null || r.mesh == null || r.width <= 0 || r.height <= 0) return null;
            if (!UvRasterizer.IsAvailable || !Morphology.IsAvailable) return null;

            var table = UvChartDetector.GetOrBuild(r.mesh, r.submesh);
            if (table == null) return null;
            if (r.seedTriangle < 0 || r.seedTriangle >= table.chartOfTriangle.Length) return null;

            // 1. クリックした三角形のチャートを塗る
            var chartTriangles = new List<int>();
            table.CollectTriangles(table.chartOfTriangle[r.seedTriangle], chartTriangles);
            var uv = r.mesh.uv;
            var triangles = r.mesh.GetTriangles(r.submesh);
            var verts = new List<Vector2>(chartTriangles.Count * 3);
            foreach (int t in chartTriangles)
            {
                UvRasterizer.AppendTriangle(verts, uv, triangles, t, r.width, r.height, r.uvScale, r.uvOffset);
            }

            var rt = MaskTextures.Create(r.width, r.height, "ClickRecolor_IslandMask");
            try
            {
                UvRasterizer.Clear(rt);
                UvRasterizer.Draw(rt, verts);

                // 2. ラスタライズの 1px 穴埋め
                Morphology.Close(rt, 1);

                // 3. ゴマ塩除去（既定 0 = 掛けない）
                Morphology.Open(rt, r.cleanupRadius);

                // 4. 安全網: 種画素から 4 連結で到達できない画素を落とす
                var seed = new Vector2Int(
                    Mathf.Clamp(Mathf.FloorToInt(r.seedUv.x * r.width), 0, r.width - 1),
                    Mathf.Clamp(Mathf.FloorToInt(r.seedUv.y * r.height), 0, r.height - 1));
                RestrictToConnected(rt, seed);

                // 5. チャートを意識したパディング（どのチャートにも属さない画素へだけ、1px ずつ padding 回広げる）→ 1px ぼかし。
                // 被覆はマスクと同じ大きさで作る前提（縁が画素単位で揃うので、島とパディングの間に塗られない輪が残らない）
                if (r.padding > 0 && r.coverage != null)
                {
                    Morphology.DilateInto(rt, r.coverage, r.padding, invertAllowed: true);
                }
                // ぼかしも被覆の外（とマスク内）にだけ書く。制約なしだと隣のチャートの縁 1px へにじむ
                if (r.coverage != null) Morphology.Blur1(rt, r.coverage, invertAllowed: true);
                else Morphology.Blur1(rt);
                return rt;
            }
            catch
            {
                // 途中で失敗したら作った RT を捨ててから投げ直す（リーク防止）
                MaskTextures.Destroy(rt);
                throw;
            }
        }

        /// <summary>
        /// 種画素から 4 連結で届かない画素を落とす。大きいテクスチャでも CPU の探索が重くならないよう、
        /// 長辺 ColorMaskBuilder.MaxReachSize 以下に縮めた二値（区画に 1 があれば 1）で到達を求め、最近傍で拡大して掛ける
        /// （色マスクの「つながった範囲」と同じ方式。縮めない大きさなら元の解像度で正確に求める）。
        /// 種の画素が 0 なら何もしない（Tiling/Offset で種がチャートから外れたときに全部消さないため）
        /// </summary>
        private static void RestrictToConnected(RenderTexture rt, Vector2Int seed)
        {
            ColorMaskBuilder.ScaleToFit(rt.width, rt.height, ColorMaskBuilder.MaxReachSize, out int sw, out int sh);
            bool downscale = (sw != rt.width || sh != rt.height) && ColorMaskBuilder.IsAvailable;
            if (!downscale)
            {
                var full = FloodFill.ReadR8(rt);
                for (int i = 0; i < full.Length; i++) full[i] = full[i] >= 128 ? (byte)1 : (byte)0;
                FloodFill.RestrictToConnected(full, rt.width, rt.height, seed);
                for (int i = 0; i < full.Length; i++) full[i] = full[i] != 0 ? (byte)255 : (byte)0;
                FloodFill.WriteR8(full, rt);
                return;
            }

            var small = MaskTextures.GetTemporary(sw, sh);
            var reach = MaskTextures.GetTemporary(rt.width, rt.height);
            var previousFilter = small.filterMode;
            var previousActive = RenderTexture.active;
            try
            {
                ColorMaskBuilder.BinarizeMax(rt, small);
                var pixels = FloodFill.ReadR8(small);
                for (int i = 0; i < pixels.Length; i++) pixels[i] = pixels[i] >= 128 ? (byte)1 : (byte)0;
                var smallSeed = new Vector2Int(
                    Mathf.Clamp(seed.x * sw / rt.width, 0, sw - 1),
                    Mathf.Clamp(seed.y * sh / rt.height, 0, sh - 1));
                if (pixels[smallSeed.y * sw + smallSeed.x] == 0) return;
                FloodFill.RestrictToConnected(pixels, sw, sh, smallSeed);
                for (int i = 0; i < pixels.Length; i++) pixels[i] = pixels[i] != 0 ? (byte)255 : (byte)0;
                FloodFill.WriteR8(pixels, small);

                // 最近傍で元の大きさへ戻して掛ける（区画単位なので、届く塊と同じ区画に掛かる別の塊は残りうる。安全網なので許容）
                small.filterMode = FilterMode.Point;
                Graphics.Blit(small, reach);
                Morphology.Multiply(rt, reach);
            }
            finally
            {
                RenderTexture.active = previousActive;
                small.filterMode = previousFilter;
                RenderTexture.ReleaseTemporary(small);
                RenderTexture.ReleaseTemporary(reach);
            }
        }
    }
}
