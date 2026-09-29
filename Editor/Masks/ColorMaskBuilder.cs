using Nekoare.ClickRecolor.Editor.Colors;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>色モードのマスクの入力</summary>
    internal sealed class ColorRequest
    {
        /// <summary>元テクスチャを写した線形の作業 RT（色を変える前）。マスクはこれと同じ大きさで作る</summary>
        public RenderTexture sourceLinear;
        /// <summary>種色（Oklab）。SeedColorSampler で sourceLinear から取ったもの</summary>
        public Vector3 seedOklab;
        /// <summary>クリック位置のテクセル（sourceLinear の画素座標、y = 0 が下）。つながった範囲の種に使う</summary>
        public Vector2Int seedTexel;
        /// <summary>しきい値（0..1）。T = max(1e-3, 0.35 × threshold²)</summary>
        public float threshold;
        /// <summary>ぼかし（0..1）。F = 0.20 × feather</summary>
        public float feather;
        public ColorScope scope;
        /// <summary>オープンの半径（ゴマ塩除去。0 なら掛けない）</summary>
        public int cleanupRadius;
        /// <summary>どのチャートにも属さない画素へ広げる幅（px）。つながった範囲・島の中のときだけ使う（テクスチャ全体は広げない）</summary>
        public int padding;
        /// <summary>
        /// スコープが島の中のときの島マスク（IslandMaskBuilder の結果、sourceLinear と同じ大きさ）。
        /// パディングは 0 で作ったものを渡すこと。パディング込みの島マスクを w に掛けると、隙間の画素は w ≈ 0
        /// （元テクスチャの隙間は種色と違う色のことが多い）なのでパディングが消える。パディングは色マスク側で 1 回だけ掛ける
        /// </summary>
        public RenderTexture islandMask;
        /// <summary>全チャート被覆 A（sourceLinear と同じ大きさ）。null ならパディングせず、ぼかしも制約なし</summary>
        public RenderTexture coverage;
    }

    /// <summary>
    /// 色モードの選択マスクを作る（設計 §6.3）:
    /// 種色との Oklab 距離からソフトな重み w → スコープで絞る → 二値化（w &gt; 0.5）にオープン／クローズを掛け、
    /// 半径 2 で膨張してから再乗算 → （つながった範囲・島の中）被覆の外へだけパディング → 1px ぼかし
    /// </summary>
    internal static class ColorMaskBuilder
    {
        /// <summary>つながった範囲の到達判定を CPU で行う大きさ（長辺）の上限</summary>
        internal const int MaxReachSize = 1024;

        private const int ThreadGroupSize = 16;

        /// <summary>ゴマ塩除去の核の周りで w の裾（w ≤ 0.5）を残す幅（px）</summary>
        private const int CoreFeatherRadius = 2;

        /// <summary>つながった範囲で、種の画素が核（w &gt; 0.5）に入らないとき核を探す半径（縮小後の px。5×5）</summary>
        private const int SeedSearchRadius = 2;

        /// <summary>しきい値 T の下限。threshold = 0 でも種画素自身（ΔE ≈ 0）が選ばれるようにする</summary>
        private const float MinT = 1e-3f;

        /// <summary>「種の近くに核が無い」警告を出したか（1 セッション 1 回だけ出す）</summary>
        private static bool s_warnedSeedOutsideCore;

        private static ComputeShader s_shader;
        private static int s_kernelSelect;
        private static int s_kernelBinarize;

        private static readonly int SrcId = Shader.PropertyToID("Src");
        private static readonly int MaskInId = Shader.PropertyToID("MaskIn");
        private static readonly int OutId = Shader.PropertyToID("Out");
        private static readonly int WidthId = Shader.PropertyToID("Width");
        private static readonly int HeightId = Shader.PropertyToID("Height");
        private static readonly int InWidthId = Shader.PropertyToID("InWidth");
        private static readonly int InHeightId = Shader.PropertyToID("InHeight");
        private static readonly int SeedOklabId = Shader.PropertyToID("SeedOklab");
        private static readonly int TId = Shader.PropertyToID("T");
        private static readonly int FId = Shader.PropertyToID("F");

        private static ComputeShader Compute
        {
            get
            {
                if (s_shader == null)
                {
                    s_shader = ShaderAssets.Load(ShaderAssets.ColorSelectGuid);
                    if (s_shader != null)
                    {
                        s_kernelSelect = s_shader.FindKernel("CSColorSelect");
                        s_kernelBinarize = s_shader.FindKernel("CSBinarizeMax");
                    }
                }
                return s_shader;
            }
        }

        internal static bool IsAvailable =>
            SystemInfo.supportsComputeShaders
            && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.R8)
            && Compute != null
            && Morphology.IsAvailable;

        /// <summary>
        /// 色モードのマスク（R8・Linear、sourceLinear と同じ大きさ、0..1）を返す。返した RT は呼び出し側の所有なので、
        /// 使い終わったら MaskTextures.Destroy すること。
        /// GPU が使えない・作業 RT が無い・島の中なのに島マスクが無い（大きさが違う）なら null
        /// </summary>
        internal static RenderTexture Build(ColorRequest r)
        {
            if (r == null || r.sourceLinear == null) return null;
            if (!IsAvailable) return null;
            int width = r.sourceLinear.width;
            int height = r.sourceLinear.height;
            if (r.scope == ColorScope.Island
                && (r.islandMask == null || r.islandMask.width != width || r.islandMask.height != height))
            {
                return null;
            }

            var rt = MaskTextures.Create(width, height, "ClickRecolor_ColorMask");
            try
            {
                // 1-2. 種色との距離からソフトな重み w（スライダーは低域を細かくするため二乗で写す）
                float threshold = Mathf.Clamp01(r.threshold);
                float feather = Mathf.Clamp01(r.feather);
                // T は下限 MinT（0 だと浮動小数の誤差で種画素自身が外れうる）
                SelectByColor(r.sourceLinear, rt, r.seedOklab, Mathf.Max(MinT, 0.35f * threshold * threshold), 0.20f * feather);

                // 3. スコープ
                switch (r.scope)
                {
                    case ColorScope.Contiguous:
                        RestrictToReach(rt, r.seedTexel);
                        break;
                    case ColorScope.Island:
                        Morphology.Multiply(rt, r.islandMask);
                        break;
                    // テクスチャ全体・アバター全体は絞らない
                }

                // 4. 二値化（w > 0.5）にオープン(cleanupRadius)／クローズ(1) を掛け、半径 2 で膨張して再乗算（ゴマ塩・小穴）
                CleanUp(rt, r.cleanupRadius);

                // 5. つながった範囲・島の中は、被覆の外へパディングする（島マスクはパディング 0 で受け取るので、ここで 1 回だけ）。
                // テクスチャ全体・アバター全体は色で選ぶだけなので広げない
                if (r.scope != ColorScope.WholeTexture && r.scope != ColorScope.WholeAvatar && r.padding > 0 && r.coverage != null)
                {
                    Morphology.DilateInto(rt, r.coverage, r.padding, invertAllowed: true);
                }
                // 負のはみ出し幅: 縁を |padding| px 削る（範囲にかかわらず）
                else if (r.padding < 0)
                {
                    Morphology.Erode(rt, -r.padding);
                }
                // ぼかしは被覆の外（とマスク内）にだけ書く。制約なしだと隣のチャートの縁 1px へにじむ
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

        private static void SelectByColor(RenderTexture source, RenderTexture dst, Vector3 seedOklab, float t, float f)
        {
            var shader = Compute;
            shader.SetTexture(s_kernelSelect, SrcId, source);
            shader.SetTexture(s_kernelSelect, OutId, dst);
            shader.SetInt(WidthId, dst.width);
            shader.SetInt(HeightId, dst.height);
            shader.SetVector(SeedOklabId, seedOklab);
            shader.SetFloat(TId, t);
            shader.SetFloat(FId, f);
            shader.Dispatch(s_kernelSelect, Groups(dst.width), Groups(dst.height), 1);
        }

        /// <summary>
        /// src の各区画に 0.5 を超える画素があれば 1 にして dst（src 以下の大きさ）へ縮める。
        /// 同じ大きさなら単なる二値化（w &gt; 0.5）
        /// </summary>
        internal static void BinarizeMax(RenderTexture src, RenderTexture dst)
        {
            var shader = Compute;
            shader.SetTexture(s_kernelBinarize, MaskInId, src);
            shader.SetTexture(s_kernelBinarize, OutId, dst);
            shader.SetInt(WidthId, dst.width);
            shader.SetInt(HeightId, dst.height);
            shader.SetInt(InWidthId, src.width);
            shader.SetInt(InHeightId, src.height);
            shader.Dispatch(s_kernelBinarize, Groups(dst.width), Groups(dst.height), 1);
        }

        /// <summary>
        /// つながった範囲: w &gt; 0.5 の画素上で種から 4 連結で届く範囲だけを残す（M = w × reach）。
        /// 大きいテクスチャでも CPU の探索が重くならないよう、長辺 MaxReachSize 以下に縮めた二値で到達を求め、
        /// 最近傍で拡大して掛ける。縮小は「区画に 0.5 を超える画素があれば 1」なので、到達は元の解像度の到達を必ず含む
        /// （区画の端で少し広めになる分は w を掛けるので色の外へは出ない）。
        /// 種の画素が核（w &gt; 0.5）に入らなければ、縮小した二値の中で種から半径 SeedSearchRadius 以内の最も近い核の画素を種にする。
        /// それでも無ければ空（全 0）にする（絞らずに残すと、クリックと関係ない同じ色がテクスチャ全体で選ばれる）
        /// </summary>
        private static void RestrictToReach(RenderTexture rt, Vector2Int seedTexel)
        {
            ScaleToFit(rt.width, rt.height, MaxReachSize, out int sw, out int sh);
            var small = MaskTextures.GetTemporary(sw, sh);
            var reach = MaskTextures.GetTemporary(rt.width, rt.height);
            var previousFilter = small.filterMode;
            // Blit は RenderTexture.active を切り替えたまま戻さないので元へ戻す
            var previousActive = RenderTexture.active;
            try
            {
                BinarizeMax(rt, small);

                var pixels = FloodFill.ReadR8(small);
                for (int i = 0; i < pixels.Length; i++) pixels[i] = pixels[i] >= 128 ? (byte)1 : (byte)0;
                var seed = new Vector2Int(
                    Mathf.Clamp(seedTexel.x * sw / rt.width, 0, sw - 1),
                    Mathf.Clamp(seedTexel.y * sh / rt.height, 0, sh - 1));
                if (TryFindCoreSeed(pixels, sw, sh, seed, out var coreSeed))
                {
                    FloodFill.RestrictToConnected(pixels, sw, sh, coreSeed);
                }
                else
                {
                    WarnSeedOutsideCoreOnce();
                    System.Array.Clear(pixels, 0, pixels.Length);
                }
                for (int i = 0; i < pixels.Length; i++) pixels[i] = pixels[i] != 0 ? (byte)255 : (byte)0;
                FloodFill.WriteR8(pixels, small);

                // 最近傍で元の大きさへ戻す（バイリニアだと区画の境目が 0.5 前後の中間値になる）
                small.filterMode = FilterMode.Point;
                Graphics.Blit(small, reach);
                // 到達域は w>0.5 の二値なので、そのまま掛けると縁の裾（w≤0.5）が消える。CleanUp と同じく 2px 広げて裾を残す
                Morphology.Dilate(reach, CoreFeatherRadius);
                Morphology.Multiply(rt, reach);
            }
            finally
            {
                RenderTexture.active = previousActive;
                // 一時 RT は使い回されるので、フィルタを戻してから返す
                small.filterMode = previousFilter;
                RenderTexture.ReleaseTemporary(small);
                RenderTexture.ReleaseTemporary(reach);
            }
        }

        /// <summary>
        /// pixels（0/1、行優先・y = 0 が下）で、seed が核なら seed、そうでなければ seed から半径 SeedSearchRadius（5×5）以内で
        /// 最も近い（距離の二乗が最小、同じなら走査順で先の）核の画素を返す。見つからなければ false
        /// </summary>
        private static bool TryFindCoreSeed(byte[] pixels, int w, int h, Vector2Int seed, out Vector2Int coreSeed)
        {
            coreSeed = seed;
            int best = int.MaxValue;
            for (int dy = -SeedSearchRadius; dy <= SeedSearchRadius; dy++)
            {
                int y = seed.y + dy;
                if (y < 0 || y >= h) continue;
                for (int dx = -SeedSearchRadius; dx <= SeedSearchRadius; dx++)
                {
                    int x = seed.x + dx;
                    if (x < 0 || x >= w) continue;
                    if (pixels[y * w + x] == 0) continue;
                    int d = dx * dx + dy * dy;
                    if (d >= best) continue;
                    best = d;
                    coreSeed = new Vector2Int(x, y);
                }
            }
            return best != int.MaxValue;
        }

        private static void WarnSeedOutsideCoreOnce()
        {
            if (s_warnedSeedOutsideCore) return;
            s_warnedSeedOutsideCore = true;
            Debug.LogWarning("[Tocolo] つながった範囲: クリックした位置の近くに、しきい値の内側の色が見つかりませんでした。" +
                "選択範囲は空になります（しきい値を上げるか、塗りたい色の上をクリックし直してください）");
        }

        /// <summary>
        /// ゴマ塩除去: 二値化（w &gt; 0.5）にオープン(cleanupRadius)／クローズ(1) を掛けて「核」を作り、
        /// 核を半径 CoreFeatherRadius で膨張したものを rt に掛ける。
        /// クローズで埋まった 1px の穴は w の値のまま残る（0 にしない）。オープンで消えた孤立点は 0 になる
        /// </summary>
        private static void CleanUp(RenderTexture rt, int cleanupRadius)
        {
            var binary = MaskTextures.GetTemporary(rt.width, rt.height);
            try
            {
                BinarizeMax(rt, binary);
                Morphology.Open(binary, cleanupRadius);
                Morphology.Close(binary, 1);
                // 核（w > 0.5）をそのまま掛けると w ≤ 0.5 の裾が全部 0 になり、ぼかし（F）が縁に効かない。
                // 核の周囲 CoreFeatherRadius px までは w の裾を残す（ゴマ塩は核に入らないので、核から離れた所では消えたまま）
                Morphology.Dilate(binary, CoreFeatherRadius);
                Morphology.Multiply(rt, binary);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(binary);
            }
        }

        /// <summary>縦横比を保って長辺が maxSize 以下になる大きさ（既に収まっていればそのまま）</summary>
        internal static void ScaleToFit(int width, int height, int maxSize, out int scaledWidth, out int scaledHeight)
        {
            scaledWidth = width;
            scaledHeight = height;
            if (Mathf.Max(width, height) <= maxSize) return;
            float scale = (float)maxSize / Mathf.Max(width, height);
            scaledWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
            scaledHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));
        }

        private static int Groups(int size) => (size + ThreadGroupSize - 1) / ThreadGroupSize;
    }
}
