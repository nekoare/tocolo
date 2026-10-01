using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Masks;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Pipeline
{
    /// <summary>1 枚のテクスチャに掛ける編集の一覧</summary>
    internal sealed class PipelineInput
    {
        /// <summary>元テクスチャ資産</summary>
        public Texture2D sourceAsset;
        /// <summary>作業解像度（長辺の上限。0 = 縮小しない）。PrepareJob に渡した size と同じ値を渡すこと</summary>
        public int workingSize;
        /// <summary>適用順の編集。null・マスクの無い要素は飛ばす</summary>
        public IReadOnlyList<EditJob> jobs;
        /// <summary>
        /// 対象ルート（ClickRecolor の GameObject）。グラデーションの位置マップの座標の基準。null ならグラデーションは効かない（t = 0）
        /// </summary>
        public Transform root;
        /// <summary>
        /// 位置マップを描く Renderer（sourceAsset をメインに持つスロットだけ描く）。SkinnedMeshRenderer はその時点のポーズで焼く。
        /// null・空ならグラデーションは効かない
        /// </summary>
        public IReadOnlyList<Renderer> renderers;
    }

    /// <summary>
    /// マスク作りに Scene の情報が要るモード（影響範囲「箱の中」）のための文脈。
    /// root は対象ルート（ClickRecolor の GameObject）、renderers は位置マップを描く Renderer、excluded は除外リスト（位置マップに描かない）。
    /// PrepareJob に渡さなければ「箱の中」の編集はマスクを作れない（null）
    /// </summary>
    internal sealed class MaskContext
    {
        public Transform root;
        public IReadOnlyList<Renderer> renderers;
        public IReadOnlyList<Renderer> excluded;

        /// <summary>root の ClickRecolor の除外リストを excluded にした文脈。root が null なら null</summary>
        public static MaskContext For(Transform root, IReadOnlyList<Renderer> renderers)
        {
            if (root == null) return null;
            var component = root.GetComponent<ClickRecolor>();
            return new MaskContext { root = root, renderers = renderers, excluded = component != null ? component.excludedRenderers : null };
        }

        public static MaskContext For(ClickRecolor component, IReadOnlyList<Renderer> renderers) =>
            component == null ? null : new MaskContext { root = component.transform, renderers = renderers, excluded = component.excludedRenderers };

        /// <summary>除外リストを外した Renderer（除外が無ければ renderers そのもの）</summary>
        public IReadOnlyList<Renderer> RenderersWithoutExcluded()
        {
            if (renderers == null) return null;
            if (excluded == null || excluded.Count == 0) return renderers;
            var result = new List<Renderer>(renderers.Count);
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                bool isExcluded = false;
                foreach (var ex in excluded)
                {
                    if (ex == renderer) { isExcluded = true; break; }
                }
                if (!isExcluded) result.Add(renderer);
            }
            return result;
        }
    }

    /// <summary>1 編集ぶんの準備済みデータ</summary>
    internal sealed class EditJob
    {
        /// <summary>色を変える単位 1 つ: マスクと、そのマスクの内側の統計</summary>
        internal struct Part
        {
            /// <summary>この部分の選択マスク（R8・Linear）。MaskCache 所有なので呼び出し側で解放しないこと</summary>
            public RenderTexture mask;
            /// <summary>この部分の内側の統計（元テクスチャ基準）</summary>
            public Stats stats;
            /// <summary>「元のグラデーションを打ち消す」の帯ごとの統計（OFF・作れないときは null）</summary>
            public BandStats bands;
        }

        public RecolorEdit edit;
        /// <summary>
        /// 合成した選択マスク（R8・Linear。全部分の最大値）。再クリックの判定・ハイライト・書き出しの合成に使う。
        /// MaskCache 所有なので呼び出し側で解放しないこと
        /// </summary>
        public RenderTexture mask;
        /// <summary>選択内の統計（元テクスチャ基準）。部分があれば先頭の部分の統計（互換のため残す）</summary>
        public Stats stats;
        /// <summary>
        /// 色を変える部分（適用順）。種ごとに色を揃えない・種が 1 つなら 1 つ（mask と同じ RT、stats と同じ統計）。
        /// 種ごとに色を揃えるなら種ごとに 1 つ。null・空なら (mask, stats) の 1 つとして扱う
        /// </summary>
        public IReadOnlyList<Part> parts;

        /// <summary>色を変える部分（parts が空なら (mask, stats) の 1 つ）</summary>
        internal IReadOnlyList<Part> ShiftParts =>
            parts != null && parts.Count > 0 ? parts : new[] { new Part { mask = mask, stats = stats } };
    }

    /// <summary>
    /// 色変えの唯一の入口: 元テクスチャ → 線形の作業 RT → 編集を順に ColorShift → 結果 RT。
    /// 使い方: 全テクスチャの編集ごとの PrepareJob を先に済ませ、ShareGroupStats(全 Job) で連結の統計を共有してから
    /// テクスチャごとに Run を呼び、マスクを使い終えたら MaskCache.Trim() を 1 回呼ぶ
    /// （Run はマスクのキャッシュを減らさない。Run の途中で減らすと、まだ Run に渡していない編集のマスクを捨ててしまう）
    /// </summary>
    internal static class RecolorPipeline
    {
        private const int ThreadGroupSize = 16;

        private static ComputeShader s_shader;
        private static int s_kernelShift;
        private static bool s_warnedUnavailable;
        private static bool s_warnedLostMask;
        private static bool s_warnedMissingExtraSeed;

        private static readonly int SrcId = Shader.PropertyToID("Src");
        private static readonly int MaskId = Shader.PropertyToID("Mask");
        private static readonly int DstId = Shader.PropertyToID("Dst");
        private static readonly int WidthId = Shader.PropertyToID("Width");
        private static readonly int HeightId = Shader.PropertyToID("Height");
        private static readonly int MaskWidthId = Shader.PropertyToID("MaskWidth");
        private static readonly int MaskHeightId = Shader.PropertyToID("MaskHeight");
        private static readonly int TargetOklchId = Shader.PropertyToID("_TargetOklch");
        private static readonly int DarkEndRatioId = Shader.PropertyToID("_DarkEndRatio");
        private static readonly int LToTargetId = Shader.PropertyToID("_LToTarget");
        private static readonly int ChromaToTargetId = Shader.PropertyToID("_ChromaToTarget");
        private static readonly int HueRetainId = Shader.PropertyToID("_HueRetain");
        private static readonly int StrengthId = Shader.PropertyToID("_Strength");
        private static readonly int DarkEndRatio2Id = Shader.PropertyToID("_DarkEndRatio2");
        private static readonly int Strength2Id = Shader.PropertyToID("_Strength2");
        private static readonly int LP05Id = Shader.PropertyToID("_LP05");
        private static readonly int LP95Id = Shader.PropertyToID("_LP95");
        private static readonly int HDominantId = Shader.PropertyToID("_HDominant");
        private static readonly int ShadingStretchId = Shader.PropertyToID("_ShadingStretch");
        private static readonly int FlattenBandsId = Shader.PropertyToID("FlattenBands");
        private static readonly int FlattenStrengthId = Shader.PropertyToID("FlattenStrength");
        private static readonly int FlattenAxisId = Shader.PropertyToID("FlattenAxis");
        private static readonly int FlattenMinId = Shader.PropertyToID("FlattenMin");
        private static readonly int FlattenMaxId = Shader.PropertyToID("FlattenMax");
        private static readonly int FlattenStatsId = Shader.PropertyToID("_FlattenStats");
        private static readonly int PosMapId = Shader.PropertyToID("PosMap");
        private static readonly int WeightOutId = Shader.PropertyToID("WeightOut");
        private static int s_kernelWeight;
        private static readonly int PosWidthId = Shader.PropertyToID("PosWidth");
        private static readonly int PosHeightId = Shader.PropertyToID("PosHeight");
        private static readonly int UseGradientId = Shader.PropertyToID("UseGradient");
        private static readonly int WorldToBoxId = Shader.PropertyToID("WorldToBox");
        private static readonly int BoxSizeId = Shader.PropertyToID("BoxSize");
        private static readonly int GradientInsideOnlyId = Shader.PropertyToID("GradientInsideOnly");
        private static readonly int TargetOklab2Id = Shader.PropertyToID("TargetOklab2");

        private static ComputeShader Compute
        {
            get
            {
                if (s_shader == null)
                {
                    s_shader = ShaderAssets.Load(ShaderAssets.ColorShiftGuid);
                    if (s_shader != null)
                    {
                        s_kernelShift = s_shader.FindKernel("CSShift");
                        s_kernelWeight = s_shader.FindKernel("CSWeight");
                    }
                }
                return s_shader;
            }
        }

        /// <summary>ColorShift.compute が使えるか（compute 対応・ARGBHalf へ書ける・シェーダーが読める）</summary>
        internal static bool IsShiftAvailable =>
            SystemInfo.supportsComputeShaders
            && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf)
            && Compute != null;

        // ── 編集の準備 ──

        /// <summary>
        /// 編集の選択マスク（島モードは島、色モードは種色との距離＋スコープ）と統計を用意する。
        /// どちらも元テクスチャ基準で、MaskCache に載せて使い回す。
        /// users はそのテクスチャをメインに持つ全スロットの (メッシュ, サブメッシュ, Tiling, Offset)（全チャート被覆 A と、
        /// 種の Tiling/Offset の引き当てに使う）。size は作業解像度（長辺の上限、0 = 縮小しない）。
        /// fromExport は書き出しで作るとき true（MaskCache.RemoveExportMasks で書き出しの後に捨てる）。
        /// 追加の種（edit.extraSeeds）があれば、種ごとに従来の方法でマスクを作って最大値で合成した 1 枚にする
        /// （色モードは種ごとにその種の色で選ぶ。共有の種色 hasSeedOklab の編集は主種だけ）。
        /// 種ごとに色を揃える（edit.perSeedStats）なら、種ごとのマスクとその統計を部分（EditJob.parts）として残し、
        /// 合成したマスクは別の RT に作る。揃えない・種が 1 つなら部分は合成マスクと同じ 1 つ。
        /// 種の Renderer・メッシュが無い、メッシュが読めない、GPU が使えない等で主種のマスクが作れなければ null
        /// （追加の種で作れないものは飛ばす）
        /// </summary>
        internal static EditJob PrepareJob(
            RecolorEdit edit,
            Texture2D sourceAsset,
            int size,
            IReadOnlyList<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users,
            bool fromExport = false,
            MaskContext context = null)
        {
            if (edit == null || sourceAsset == null) return null;
            // 共有の種色で選ぶ編集（アバター全体の連結）は種のメッシュを使わないので、種の Renderer が無くてもよい
            var seedMesh = RendererMeshAccess.GetSharedMesh(edit.seedRenderer);
            if (seedMesh == null && NeedsSeedRenderer(edit)) return null;

            int usersHash = UsersHash(seedMesh, users);
            var seeds = CollectSeeds(edit, seedMesh);
            // 追加の種のメッシュの形が変わったら別の鍵にする（主種のメッシュは usersHash に入っている）
            int foldedHash = FoldExtraSeedMeshes(usersHash, seeds);
            // 「箱の中」は箱と位置マップ（姿勢・除外リスト）で範囲が決まるので鍵に畳み込む
            if (edit.mode == SelectionMode.Box) foldedHash = FoldBox(foldedHash, edit, sourceAsset, context);
            // 「元のグラデーションを打ち消す」は帯の統計を部分に持つので、向きと位置（姿勢）を鍵に畳み込む
            if (edit.flattenBase) foldedHash = FoldFlatten(foldedHash, edit, sourceAsset, context);
            var key = new MaskCache.Key(edit, sourceAsset, size, foldedHash, fromExport);
            if (MaskCache.TryGet(key, out var cachedMask, out var cachedParts))
            {
                return NewJob(edit, cachedMask, cachedParts);
            }

            // マスクの大きさは作業 RT と揃える
            var source = CreateSourceWorkTexture(sourceAsset, size);
            if (source == null) return null;
            // 種ごとに色を揃えるなら種ごとのマスクを残す（合成は最後に別の RT へ）。揃えないなら 1 枚へ順に合成する
            bool perSeed = edit.perSeedStats && seeds.Count > 1;
            var seedMasks = new List<RenderTexture>();
            RenderTexture mask = null;
            try
            {
                // 全チャート被覆 A はマスク（＝作業 RT）と同じ大きさで作る（縁を画素単位で揃える）。
                // 読み取り不可のメッシュの件数は Inspector 側で警告するのでここでは使わない
                var coverage = users != null
                    ? CoverageMask.GetOrBuild(sourceAsset, users, source.width, source.height, out _)
                    : null;

                if (edit.mode == SelectionMode.Box)
                {
                    // 箱の中: 種は使わない（クリック位置はテクスチャの特定と「元の色」のためだけ）。
                    // 「パーツごとに色を揃える」なら箱マスクをパーツ（チャート）ごとに分けて、パーツ複数選択と同じく部分ごとの統計にする
                    mask = BuildBoxMask(edit, sourceAsset, source, context, coverage);
                    if (mask == null) return null;
                    // パーツごとの統計は重いので、箱のドラッグ中は掛けない（離したときに掛ける。ユーザー要望 2026-09-29）
                    if (edit.boxPerPartStats && !SceneTool.ToolSession.BoxDragging)
                    {
                        var partMasks = SplitBoxMaskByChart(mask, users, source, coverage, edit);
                        if (partMasks.Count > 1)
                        {
                            seedMasks.AddRange(partMasks);
                            MaskTextures.Destroy(mask);
                            mask = null;
                        }
                        else
                        {
                            foreach (var partMask in partMasks) MaskTextures.Destroy(partMask);
                        }
                    }
                }
                else
                for (int i = 0; i < seeds.Count; i++)
                {
                    var seed = seeds[i];
                    FindSeedTransform(users, seed.mesh, seed.submesh, out var uvScale, out var uvOffset);
                    var seedMask = edit.mode == SelectionMode.Color
                        ? BuildColorMask(edit, seed, source, uvScale, uvOffset, coverage, usersHash)
                        : BuildIslandMask(seed, source, uvScale, uvOffset, coverage, edit.padding, edit.cleanupRadius);
                    if (seedMask == null)
                    {
                        // 主種のマスクが作れなければ従来どおり反映しない。追加の種は飛ばす
                        if (i == 0) return null;
                        continue;
                    }
                    if (perSeed)
                    {
                        seedMasks.Add(seedMask);
                        continue;
                    }
                    if (mask == null)
                    {
                        mask = seedMask;
                        continue;
                    }
                    // 種ごとのマスクは同じ大きさ（作業 RT）なので、画素ごとの最大値で合成する
                    try
                    {
                        Morphology.Max(mask, seedMask);
                    }
                    finally
                    {
                        MaskTextures.Destroy(seedMask);
                    }
                }

                // 統計は色を変える前の作業 RT から取る（スライダーで揺れない）
                List<EditJob.Part> parts;
                if (seedMasks.Count == 1)
                {
                    // 追加の種がすべて作れなかった: 種が 1 つのときと同じにする
                    mask = seedMasks[0];
                    seedMasks.Clear();
                }
                if (seedMasks.Count > 1)
                {
                    // 種ごとに統計を取り、合成マスク（最大値）は別の RT に作る
                    var first = seedMasks[0];
                    mask = MaskTextures.Create(first.width, first.height, "ClickRecolor_CombinedMask");
                    Graphics.CopyTexture(first, mask);
                    for (int i = 1; i < seedMasks.Count; i++) Morphology.Max(mask, seedMasks[i]);
                    parts = new List<EditJob.Part>(seedMasks.Count);
                    foreach (var seedMask in seedMasks)
                    {
                        parts.Add(new EditJob.Part { mask = seedMask, stats = SelectionStats.Compute(source, seedMask) });
                    }
                }
                else
                {
                    if (mask == null) return null;
                    parts = new List<EditJob.Part> { new EditJob.Part { mask = mask, stats = SelectionStats.Compute(source, mask) } };
                }

                if (edit.flattenBase) AttachBandStats(edit, sourceAsset, source, context, parts);

                MaskCache.Add(key, mask, parts);
                var job = NewJob(edit, mask, parts);
                // 所有権はキャッシュへ移った
                mask = null;
                seedMasks.Clear();
                return job;
            }
            finally
            {
                if (mask != null) MaskTextures.Destroy(mask);
                foreach (var seedMask in seedMasks) MaskTextures.Destroy(seedMask);
                DestroyWorkTexture(source);
            }
        }

        /// <summary>合成マスクと部分から EditJob を作る（stats は互換のため先頭の部分の統計）</summary>
        private static EditJob NewJob(RecolorEdit edit, RenderTexture mask, IReadOnlyList<EditJob.Part> parts)
        {
            return new EditJob
            {
                edit = edit,
                mask = mask,
                stats = parts != null && parts.Count > 0 ? parts[0].stats : default,
                parts = parts,
            };
        }

        /// <summary>
        /// 連結（groupId）の全メンバーで明るさの統計を共有する編集か: 連結あり・各点の色を揃えない（perSeedStats = false）・目標色あり。
        /// 目標色が未設定の編集（ハイライト中だけプレビューに入る）は、ビルドと結果を揃えるため共有に入れない
        /// </summary>
        internal static bool SharesGroupStats(RecolorEdit edit) =>
            edit != null && !string.IsNullOrEmpty(edit.groupId) && !edit.perSeedStats && edit.hasTarget;

        /// <summary>
        /// 連結の統計を共有する: SharesGroupStats の編集の Job を groupId ごとに集め、全部の部分の統計を SelectionStats.Merge で
        /// 1 つにまとめて、その連結の全 Job の部分（と stats）に上書きする。黒い髪と白い髪を一緒に色変えしても、
        /// 黒は暗いまま・白は明るいままで同じ色相になる（メンバーごとに目標の明るさ範囲いっぱいへ写さない）。
        /// 部分の一覧は新しい一覧に差し替える（PrepareJob が返す一覧は MaskCache の持ち物なので書き換えない）。
        /// 使い方: 全テクスチャの PrepareJob を済ませてから、Run の前に全 Job を渡して 1 回呼ぶ。
        /// Job が 1 つだけの連結は変えない
        /// </summary>
        internal static void ShareGroupStats(IReadOnlyList<EditJob> jobs)
        {
            if (jobs == null) return;
            var groups = new Dictionary<string, List<EditJob>>();
            foreach (var job in jobs)
            {
                if (job == null || !SharesGroupStats(job.edit)) continue;
                if (!groups.TryGetValue(job.edit.groupId, out var members))
                {
                    members = new List<EditJob>();
                    groups.Add(job.edit.groupId, members);
                }
                // 同じ Job が二重に渡されても 1 回だけ数える
                if (!members.Contains(job)) members.Add(job);
            }

            var all = new List<Stats>();
            foreach (var members in groups.Values)
            {
                if (members.Count < 2) continue;
                all.Clear();
                foreach (var job in members)
                {
                    foreach (var part in job.ShiftParts) all.Add(part.stats);
                }
                var merged = SelectionStats.Merge(all);
                foreach (var job in members)
                {
                    var source = job.ShiftParts;
                    var parts = new List<EditJob.Part>(source.Count);
                    foreach (var part in source) parts.Add(new EditJob.Part { mask = part.mask, stats = merged, bands = part.bands });
                    job.parts = parts;
                    job.stats = merged;
                }
            }
        }

        /// <summary>
        /// マスクを作るのに種の Renderer（メッシュ）が要るか。色モード・アバター全体・共有の種色あり（hasSeedOklab）の編集だけは要らない
        /// （種色は保存済み、範囲は色だけで選ぶ）。それ以外で種の Renderer が消えていたら、その編集は反映できない
        /// </summary>
        /// <summary>
        /// クリックしたパーツ（種の Renderer）が削除などで無くなり、反映できない編集か。
        /// 自動では消さない（GameObject の削除を Undo したときに編集ごと戻せるように）。Inspector で印と一括削除を出す
        /// </summary>
        internal static bool IsSeedMissing(RecolorEdit edit) =>
            edit != null && edit.seedRenderer == null && NeedsSeedRenderer(edit);

        internal static bool NeedsSeedRenderer(RecolorEdit edit)
        {
            if (edit == null) return true;
            return !(edit.mode == SelectionMode.Color && edit.scope == ColorScope.WholeAvatar && edit.hasSeedOklab);
        }

        /// <summary>
        /// asset の作業 RT（長辺 size 以下・線形）を作り、uv（Tiling/Offset 適用後、0..1）の 5×5 近傍の種色（Oklab）を取る。
        /// 色マスクが作業 RT から種色を取るのと同じ取り方（BuildColorMask）。読めなければ false
        /// </summary>
        internal static bool TrySampleSeedOklab(Texture2D asset, int size, Vector2 uv, out Vector3 oklab)
        {
            oklab = default;
            if (asset == null) return false;
            var source = CreateSourceWorkTexture(asset, size);
            if (source == null) return false;
            try
            {
                oklab = SeedColorSampler.SampleOklab(source, ToSeedTexel(uv, source));
                return true;
            }
            finally
            {
                DestroyWorkTexture(source);
            }
        }

        /// <summary>マスクを作る種 1 つ（主種か追加の種）。mesh は共有の種色で選ぶ編集の主種だけ null のことがある</summary>
        private readonly struct SeedSpec
        {
            public readonly Mesh mesh;
            public readonly int submesh;
            public readonly int triangle;
            public readonly Vector2 uv;

            public SeedSpec(Mesh mesh, int submesh, int triangle, Vector2 uv)
            {
                this.mesh = mesh;
                this.submesh = submesh;
                this.triangle = triangle;
                this.uv = uv;
            }
        }

        /// <summary>
        /// マスクを作る種の一覧（先頭が主種、続いて追加の種）。共有の種色（hasSeedOklab。アバター全体の連結）の編集は主種だけ。
        /// Renderer・メッシュが無い追加の種は飛ばす（警告は 1 セッション 1 回）
        /// </summary>
        private static List<SeedSpec> CollectSeeds(RecolorEdit edit, Mesh seedMesh)
        {
            var seeds = new List<SeedSpec> { new SeedSpec(seedMesh, edit.seedSubmesh, edit.seedTriangle, edit.seedUv) };
            if (edit.hasSeedOklab || edit.extraSeeds == null) return seeds;
            foreach (var extra in edit.extraSeeds)
            {
                if (extra == null) continue;
                var mesh = RendererMeshAccess.GetSharedMesh(extra.renderer);
                if (mesh == null)
                {
                    if (!s_warnedMissingExtraSeed)
                    {
                        s_warnedMissingExtraSeed = true;
                        Debug.LogWarning("[Tocolo] 追加した種の Renderer（メッシュ）が見つからない編集があります。その種は範囲に入りません");
                    }
                    continue;
                }
                seeds.Add(new SeedSpec(mesh, extra.submesh, extra.triangle, extra.uv));
            }
            return seeds;
        }

        /// <summary>usersHash に追加の種（seeds[1..]）のメッシュ（InstanceID と形）を畳み込む。マスクの鍵に使う</summary>
        private static int FoldFlatten(int hash, RecolorEdit edit, Texture2D sourceAsset, MaskContext context)
        {
            unchecked
            {
                int h = hash * 31 + 7919;
                h = h * 31 + BandStats.AxisOf(edit).GetHashCode();
                h = h * 31 + (context != null ? PositionMap.ContextHash(context.root, context.renderers, sourceAsset) : 0);
                return h;
            }
        }

        /// <summary>
        /// 部分ごとに帯の統計（BandStats）を取って parts に入れる。位置マップが描けない（文脈が無い・compute が無い）ときは入れない
        /// （その場合は従来どおり全体 1 つの分布で写す）
        /// </summary>
        private static void AttachBandStats(
            RecolorEdit edit, Texture2D sourceAsset, RenderTexture source, MaskContext context, List<EditJob.Part> parts)
        {
            if (context == null || context.root == null || context.renderers == null) return;
            int fill = Mathf.Min(edit.padding + 2, PositionMap.MaxFillIterations);
            var positionMap = PositionMap.GetOrBuild(context.root, context.renderers, sourceAsset, source.width, source.height, fill);
            if (positionMap == null) return;
            var axis = BandStats.AxisOf(edit);
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                part.bands = BandStats.Compute(source, part.mask, positionMap, axis);
                parts[i] = part;
            }
        }

        /// <summary>「箱の中」の鍵: 箱の位置・回転・大きさ・ぼかしと、位置マップの中身を決める値（姿勢・除外後の Renderer）</summary>
        private static int FoldBox(int hash, RecolorEdit edit, Texture2D sourceAsset, MaskContext context)
        {
            unchecked
            {
                int h = hash;
                h = h * 31 + edit.boxPosition.GetHashCode();
                h = h * 31 + edit.boxRotation.GetHashCode();
                h = h * 31 + edit.boxSize.GetHashCode();
                h = h * 31 + (edit.boxPerPartStats && !SceneTool.ToolSession.BoxDragging ? 1 : 0);
                h = h * 31 + (SceneTool.ToolSession.BoxDragging ? 1 : 0); // ドラッグ中は等倍描画なので別の鍵
                h = h * 31 + (context != null ? PositionMap.ContextHash(context.root, context.RenderersWithoutExcluded(), sourceAsset) : 0);
                return h;
            }
        }

        /// <summary>
        /// 「箱の中」のマスク。除外リストの Renderer を外したスロットを UV 空間に描いて箱の内側を 1 にし、
        /// 島モードと同じくどのチャートにも属さない画素へだけ「はみ出し幅」ぶん広げて縁をならす。
        /// 文脈が無い・描くスロットが無い・シェーダーが無ければ null
        /// </summary>
        private static RenderTexture BuildBoxMask(
            RecolorEdit edit, Texture2D sourceAsset, RenderTexture source, MaskContext context, RenderTexture coverage)
        {
            if (context == null || context.root == null) return null;
            var renderers = context.RenderersWithoutExcluded();
            if (renderers == null || renderers.Count == 0) return null;
            var slots = PositionMap.CollectSlots(renderers, sourceAsset);
            if (slots.Count == 0) return null;
            // スーパーサンプリングは箱のドラッグ中は掛けず、離したときに掛ける（「箱の中の色を揃える」と同じ。ユーザー要望 2026-09-29）
            var mask = BoxMaskBuilder.Build(
                context.root, slots, source.width, source.height, edit.boxPosition, edit.boxRotation, edit.boxSize, edit.feather,
                supersample: !SceneTool.ToolSession.BoxDragging);
            if (mask == null) return null;
            if (coverage != null)
            {
                // GPU の通常描画は三角形の縁の画素（中心が三角形の外）を塗り残すので、まずパーツの内側（被覆あり）へ 1 画素広げて縁を埋める
                // （実機 2026-09-29: UV の縁が元の色の線として残った）。その後は島モードと同じく空きへはみ出し幅ぶん広げる
                Morphology.DilateInto(mask, coverage, 1, invertAllowed: false);
                if (edit.padding > 0) Morphology.DilateInto(mask, coverage, edit.padding, invertAllowed: true);
                else if (edit.padding < 0) Morphology.Erode(mask, -edit.padding); // 負のはみ出し幅: 縁を削る
                Morphology.Blur1(mask, coverage, invertAllowed: true);
            }
            return mask;
        }

        /// <summary>
        /// 箱マスク boxMask をパーツ（UV のチャート）ごとに分ける。箱マスクを読み戻し、テクスチャの利用者の各チャートについて
        /// 三角形の UV 重心の画素が 0.5 を超えるものを「箱に入るパーツ」とし、そのパーツの島マスク（はみ出し幅つき）に箱マスクを掛けたものを部分にする。
        /// 返す RT は呼び出し側の所有。読み取り不可のメッシュのパーツは飛ばす
        /// </summary>
        private static List<RenderTexture> SplitBoxMaskByChart(
            RenderTexture boxMask,
            IReadOnlyList<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users,
            RenderTexture source, RenderTexture coverage, RecolorEdit edit)
        {
            var parts = new List<RenderTexture>();
            if (boxMask == null || users == null) return parts;
            var pixels = FloodFill.ReadR8(boxMask);
            int width = boxMask.width, height = boxMask.height;
            var seen = new HashSet<(int mesh, int submesh, int chart)>();
            foreach (var user in users)
            {
                if (user.mesh == null) continue;
                var table = UvChartDetector.GetOrBuild(user.mesh, user.submesh);
                if (table == null) continue;
                var uv = user.mesh.uv;
                var triangles = user.mesh.GetTriangles(user.submesh);
                var scale = user.uvScale == Vector2.zero ? Vector2.one : user.uvScale;
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int local = t / 3;
                    if (local >= table.chartOfTriangle.Length) break;
                    var key = (user.mesh.GetInstanceID(), user.submesh, table.chartOfTriangle[local]);
                    if (seen.Contains(key)) continue;
                    var centroid = (uv[triangles[t]] + uv[triangles[t + 1]] + uv[triangles[t + 2]]) / 3f;
                    var coord = Vector2.Scale(centroid, scale) + user.uvOffset;
                    coord = new Vector2(coord.x - Mathf.Floor(coord.x), coord.y - Mathf.Floor(coord.y));
                    int px = Mathf.Clamp(Mathf.FloorToInt(coord.x * width), 0, width - 1);
                    int py = Mathf.Clamp(Mathf.FloorToInt(coord.y * height), 0, height - 1);
                    if (pixels[py * width + px] <= 127) continue;
                    seen.Add(key);
                    var seed = new SeedSpec(user.mesh, user.submesh, local, coord);
                    var islandMask = BuildIslandMask(seed, source, user.uvScale, user.uvOffset, coverage, edit.padding, 0);
                    if (islandMask == null) continue;
                    Morphology.Multiply(islandMask, boxMask);
                    parts.Add(islandMask);
                }
            }
            return parts;
        }

        private static int FoldExtraSeedMeshes(int usersHash, List<SeedSpec> seeds)
        {
            unchecked
            {
                int h = usersHash;
                for (int i = 1; i < seeds.Count; i++)
                {
                    h = h * 31 + seeds[i].mesh.GetInstanceID();
                    h = h * 31 + UvChartDetector.Fingerprint(seeds[i].mesh);
                }
                return h;
            }
        }

        /// <summary>
        /// 追加の種（extraSeeds）の中身（Renderer の InstanceID・サブメッシュ・三角形・UV、順序込み）のハッシュ。
        /// マスクの鍵（MaskCache.Key）とプレビューの編集ハッシュに使う。空なら 0
        /// </summary>
        internal static int HashExtraSeeds(RecolorEdit edit)
        {
            if (edit?.extraSeeds == null || edit.extraSeeds.Count == 0) return 0;
            unchecked
            {
                int h = 17;
                foreach (var seed in edit.extraSeeds)
                {
                    if (seed == null)
                    {
                        h = h * 31 + 1;
                        continue;
                    }
                    h = h * 31 + (seed.renderer != null ? seed.renderer.GetInstanceID() : 0);
                    h = h * 31 + seed.submesh;
                    h = h * 31 + seed.triangle;
                    h = h * 31 + seed.uv.GetHashCode();
                }
                return h;
            }
        }

        /// <summary>uv（0..1）を作業 RT のテクセルにする（クランプ）</summary>
        private static Vector2Int ToSeedTexel(Vector2 uv, RenderTexture source)
        {
            return new Vector2Int(
                Mathf.Clamp(Mathf.FloorToInt(uv.x * source.width), 0, source.width - 1),
                Mathf.Clamp(Mathf.FloorToInt(uv.y * source.height), 0, source.height - 1));
        }

        /// <summary>種のチャートの島マスク（作業 RT と同じ大きさ、padding px のパディング込み）。作れなければ null</summary>
        private static RenderTexture BuildIslandMask(
            in SeedSpec seed, RenderTexture source, Vector2 uvScale, Vector2 uvOffset,
            RenderTexture coverage, int padding, int cleanupRadius)
        {
            return IslandMaskBuilder.Build(new IslandRequest
            {
                mesh = seed.mesh,
                submesh = seed.submesh,
                seedTriangle = seed.triangle,
                seedUv = seed.uv,
                uvScale = uvScale,
                uvOffset = uvOffset,
                padding = padding,
                cleanupRadius = cleanupRadius,
                width = source.width,
                height = source.height,
                coverage = coverage,
            });
        }

        /// <summary>
        /// 色モードのマスク（種 seed 1 つぶん）。種色は edit.seedColor（クリック時の見本）ではなく、作業 RT（色を変える前）の
        /// 種テクセルの 5×5 近傍から取り直す（常に元テクスチャ基準にして、選択が揺れないようにする）。
        /// 共有の種色（hasSeedOklab。アバター全体の連結）があれば取り直さずにそれを使う。
        /// スコープが島の中なら島マスク（パディング 0）を掛ける。パディングは色マスク側で掛ける
        /// （パディング込みの島マスクを w に掛けると、隙間の画素は w ≈ 0 なのでパディングが消える）。
        /// 島マスクはしきい値等に依らないので IslandMaskCache から取る（無ければ作って入れる。キャッシュ所有なので解放しない）。
        /// 作れなければ null
        /// </summary>
        private static RenderTexture BuildColorMask(
            RecolorEdit edit, in SeedSpec seed, RenderTexture source, Vector2 uvScale, Vector2 uvOffset, RenderTexture coverage,
            int usersHash)
        {
            RenderTexture island = null;
            if (edit.scope == ColorScope.Island)
            {
                var islandKey = new IslandMaskCache.Key(
                    seed.mesh.GetInstanceID(), UvChartDetector.Fingerprint(seed.mesh), seed.submesh, seed.triangle,
                    seed.uv, usersHash, source.width, source.height);
                // ゴマ塩除去とパディングは色マスク側で掛けるので、島マスクには掛けない（チャートのラスタライズに孤立点は無い）
                // （ラムダに in 引数は取り込めないので写す）
                var islandSeed = seed;
                island = IslandMaskCache.GetOrBuild(islandKey,
                    () => BuildIslandMask(islandSeed, source, uvScale, uvOffset, coverage, padding: 0, cleanupRadius: 0));
                if (island == null) return null;
            }

            var seedTexel = ToSeedTexel(seed.uv, source);
            return ColorMaskBuilder.Build(new ColorRequest
            {
                sourceLinear = source,
                // 共有の種色があればそれを使う（アバター全体の連結は、クリックしたテクスチャ以外に種の画素が無い）
                seedOklab = edit.hasSeedOklab ? edit.seedOklab : SeedColorSampler.SampleOklab(source, seedTexel),
                seedTexel = seedTexel,
                threshold = edit.threshold,
                feather = edit.feather,
                scope = edit.scope,
                cleanupRadius = edit.cleanupRadius,
                padding = edit.padding,
                islandMask = island,
                coverage = coverage,
            });
        }

        /// <summary>users の中から種の (メッシュ, サブメッシュ) の Tiling/Offset を引く。見つからなければ (1,1)/(0,0)</summary>
        private static void FindSeedTransform(
            IReadOnlyList<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users,
            Mesh seedMesh, int seedSubmesh, out Vector2 uvScale, out Vector2 uvOffset)
        {
            if (users != null)
            {
                for (int i = 0; i < users.Count; i++)
                {
                    if (users[i].mesh != seedMesh || users[i].submesh != seedSubmesh) continue;
                    uvScale = users[i].uvScale;
                    uvOffset = users[i].uvOffset;
                    return;
                }
            }
            uvScale = Vector2.one;
            uvOffset = Vector2.zero;
        }

        /// <summary>種のメッシュと利用者の組（順序込み）の畳み込み。メッシュの形・Tiling/Offset が変わったら別の鍵になる</summary>
        private static int UsersHash(
            Mesh seedMesh, IReadOnlyList<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users)
        {
            unchecked
            {
                int h = 17;
                // 共有の種色で選ぶ編集は種のメッシュが無いことがある（NeedsSeedRenderer）
                h = h * 31 + (seedMesh != null ? seedMesh.GetInstanceID() : 0);
                h = h * 31 + (seedMesh != null ? UvChartDetector.Fingerprint(seedMesh) : 0);
                if (users == null) return h;
                for (int i = 0; i < users.Count; i++)
                {
                    var (mesh, submesh, uvScale, uvOffset) = users[i];
                    h = h * 31 + (mesh != null ? mesh.GetInstanceID() : 0);
                    h = h * 31 + submesh;
                    h = h * 31 + UvChartDetector.Fingerprint(mesh);
                    h = h * 31 + uvScale.GetHashCode();
                    h = h * 31 + uvOffset.GetHashCode();
                }
                return h;
            }
        }

        // ── 実行 ──

        /// <summary>
        /// 元テクスチャに jobs を順に適用した結果（ARGBHalf・Linear・ミップ生成済みの RT）を返す。返した RT は呼び出し側の所有なので、
        /// 使い終わったら DestroyWorkTexture すること。元テクスチャが読めない・GPU が使えなければ null
        /// </summary>
        internal static RenderTexture Run(PipelineInput input)
        {
            if (input == null || input.sourceAsset == null) return null;
            // ping-pong のどちらが最後になっても結果をそのまま返せるよう、最初からミップ付きで作る（結果を写し直さない）
            var work = CreateSourceWorkTexture(input.sourceAsset, input.workingSize, useMipMap: true);
            if (work == null) return null;
            // グラデーションの編集があるときだけ位置マップを取る（作業 RT と同じ大きさ。PositionMap のキャッシュ所有なので解放しない）
            RenderTexture positionMap = null;
            if (input.root != null && input.renderers != null && (HasGradient(input.jobs) || HasFlatten(input.jobs)))
            {
                // はみ出し幅（パディング）の画素まで位置を埋める（最大のはみ出し幅＋2 回の膨張）
                positionMap = PositionMap.GetOrBuild(
                    input.root, input.renderers, input.sourceAsset, work.width, work.height, FillIterations(input.jobs));
            }
            return ApplyJobs(work, input.jobs, positionMap);
        }

        /// <summary>位置マップの膨張の回数: jobs の編集のはみ出し幅（padding）の最大値＋2（PositionMap.MaxFillIterations まで）</summary>
        internal static int FillIterations(IReadOnlyList<EditJob> jobs)
        {
            int padding = 0;
            if (jobs != null)
            {
                foreach (var job in jobs)
                {
                    if (job?.edit != null) padding = Mathf.Max(padding, job.edit.padding);
                }
            }
            return Mathf.Min(padding + 2, PositionMap.MaxFillIterations);
        }

        /// <summary>jobs にグラデーション ON の編集が 1 つでもあるか</summary>
        /// <summary>帯の統計を持つ部分がある（「元のグラデーションを打ち消す」で位置マップが要る）か</summary>
        private static bool HasFlatten(IReadOnlyList<EditJob> jobs)
        {
            if (jobs == null) return false;
            foreach (var job in jobs)
            {
                if (job?.edit == null || !job.edit.flattenBase) continue;
                foreach (var part in job.ShiftParts) if (part.bands != null) return true;
            }
            return false;
        }

        private static bool HasGradient(IReadOnlyList<EditJob> jobs)
        {
            if (jobs == null) return false;
            foreach (var job in jobs)
            {
                if (job?.edit != null && job.edit.gradientEnabled) return true;
            }
            return false;
        }

        /// <summary>
        /// work（元テクスチャを写した作業 RT）に jobs を順に適用する。work の所有権を受け取り、結果の RT を返す
        /// （work そのものか、ping-pong のもう 1 枚。使わなかった方はここで破棄する）。
        /// ping-pong のもう 1 枚は work とミップの有無を揃え、ミップ付きなら結果のミップを作ってから返す。
        /// 目標色が未設定・無効・マスクの無い編集は素通し（Dispatch しない）。適用する編集があるのに GPU が使えなければ null。
        /// positionMap はグラデーションの位置マップ（null ならグラデーション ON の編集も t = 0 ＝「新しい色」で塗る）
        /// </summary>
        internal static RenderTexture ApplyJobs(RenderTexture work, IReadOnlyList<EditJob> jobs, Texture positionMap = null)
        {
            if (work == null) return null;
            RenderTexture current = work;
            RenderTexture spare = null;
            bool succeeded = false;
            try
            {
                if (jobs != null)
                {
                    foreach (var job in jobs)
                    {
                        if (!IsActive(job)) continue;
                        if (!IsShiftAvailable)
                        {
                            if (!s_warnedUnavailable)
                            {
                                s_warnedUnavailable = true;
                                Debug.LogWarning("[Tocolo] この環境では色変換（compute shader）が使えないため、色を変えられません");
                            }
                            return null;
                        }

                        if (spare == null)
                        {
                            spare = CreateWorkTexture(current.width, current.height, "ClickRecolor_Work", current, current.useMipMap);
                        }
                        // 部分（種ごとに色を揃えるなら種ごと）を順に、その部分の統計で変換する。
                        // 後の部分が前の部分を上書きしてよい（種ごとのマスクは通常重ならない）
                        foreach (var part in job.ShiftParts)
                        {
                            var p = ShiftParams.FromEdit(job.edit, part.stats);
                            if (job.edit.flattenBase)
                            {
                                p.bands = part.bands;
                                p.flattenStrength = job.edit.flattenStrength;
                            }
                            Shift(current, part.mask, spare, p, positionMap);
                            (current, spare) = (spare, current);
                        }
                    }
                }
                // 遠目でちらつかないようにミップを作る（書き込みは mip 0 だけなので最後に 1 回）
                if (current.useMipMap) current.GenerateMips();
                succeeded = true;
                return current;
            }
            finally
            {
                DestroyWorkTexture(spare);
                if (!succeeded) DestroyWorkTexture(current);
            }
        }

        private static bool IsActive(EditJob job)
        {
            if (job == null || job.edit == null || !job.edit.enabled || !job.edit.hasTarget) return false;
            if (job.mask == null || !job.mask.IsCreated() || !ArePartMasksAlive(job))
            {
                // キャッシュの消去（リロード・シーン切替）の後に古い EditJob を使った等。黙って素通しにはしない
                if (!s_warnedLostMask)
                {
                    s_warnedLostMask = true;
                    Debug.LogWarning("[Tocolo] 選択マスクが解放済みの編集があります。その編集は反映されません（PrepareJob からやり直してください）");
                }
                return false;
            }
            return true;
        }

        private static bool ArePartMasksAlive(EditJob job)
        {
            if (job.parts == null) return true;
            foreach (var part in job.parts)
            {
                if (part.mask == null || !part.mask.IsCreated()) return false;
            }
            return true;
        }

        /// <summary>
        /// ColorShift.compute を 1 回走らせる: dst = src をマスクの内側だけ目標色へ寄せたもの。
        /// src と dst は別のテクスチャで、dst は enableRandomWrite の RT（CreateWorkTexture）であること。
        /// mask は src と別サイズでもよい（正規化座標で対応させる）。
        /// positionMap は位置マップ（PositionMap。これも別サイズでよい）。p.useGradient でも null ならグラデーションは掛けない
        /// </summary>
        internal static void Shift(Texture src, Texture mask, RenderTexture dst, in ShiftParams p, Texture positionMap = null)
        {
            var shader = Compute;
            if (shader == null || src == null || mask == null || dst == null) return;

            shader.SetTexture(s_kernelShift, SrcId, src);
            shader.SetTexture(s_kernelShift, MaskId, mask);
            shader.SetTexture(s_kernelShift, DstId, dst);
            shader.SetInt(WidthId, dst.width);
            shader.SetInt(HeightId, dst.height);
            shader.SetInt(MaskWidthId, mask.width);
            shader.SetInt(MaskHeightId, mask.height);

            shader.SetVector(TargetOklchId, p.targetOklch);
            shader.SetFloat(DarkEndRatioId, p.darkEndRatio);
            shader.SetFloat(LToTargetId, p.lToTarget);
            shader.SetFloat(ChromaToTargetId, p.chromaToTarget);
            shader.SetFloat(HueRetainId, p.hueRetain);
            shader.SetFloat(StrengthId, p.strength);
            shader.SetFloat(LP05Id, p.lP05);
            shader.SetFloat(LP95Id, p.lP95);
            shader.SetFloat(HDominantId, p.hDominant);
            shader.SetFloat(ShadingStretchId, p.shadingStretch);

            // 位置マップはグラデーションを使わないときも何か束縛しておく（未設定のテクスチャがあると Dispatch がエラーになる）
            bool gradient = p.useGradient && positionMap != null;
            bool flatten = p.bands != null && p.bands.values != null && positionMap != null;
            var pos = gradient || flatten ? positionMap : Texture2D.blackTexture;
            shader.SetInt(FlattenBandsId, flatten ? BandStats.Count : 0);
            if (flatten)
            {
                shader.SetFloat(FlattenStrengthId, p.flattenStrength);
                shader.SetVector(FlattenAxisId, p.bands.axis);
                shader.SetFloat(FlattenMinId, p.bands.min);
                shader.SetFloat(FlattenMaxId, p.bands.max);
                shader.SetVectorArray(FlattenStatsId, p.bands.values);
            }
            shader.SetTexture(s_kernelShift, PosMapId, pos);
            shader.SetInt(PosWidthId, pos.width);
            shader.SetInt(PosHeightId, pos.height);
            shader.SetInt(UseGradientId, gradient ? 1 : 0);
            shader.SetMatrix(WorldToBoxId, p.rootToBox);
            shader.SetVector(BoxSizeId, p.boxSize);
            shader.SetInt(GradientInsideOnlyId, gradient && p.gradientInsideOnly ? 1 : 0);
            shader.SetVector(TargetOklab2Id, p.targetOklab2);
            shader.SetFloat(DarkEndRatio2Id, p.darkEndRatio2);
            shader.SetFloat(Strength2Id, p.strength2);

            shader.Dispatch(s_kernelShift, Groups(dst.width), Groups(dst.height), 1);
        }

        private static int Groups(int size) => (size + ThreadGroupSize - 1) / ThreadGroupSize;

        /// <summary>
        /// マスクだけ書き出し用: jobs の各部分が実際に効く重み（マスク × 不透明度。グラデーションは位置で色 1・色 2 の不透明度を補間、
        /// 「箱の中だけ」の外は 0）を、accumulate（R8・enableRandomWrite。呼び出し側が 0 で用意）へ max で積む。
        /// positionMap は Run と同じ位置マップ（null ならグラデーションは t = 0 ＝色 1 の不透明度）。GPU が使えなければ false
        /// </summary>
        internal static bool AccumulateWeights(RenderTexture accumulate, IReadOnlyList<EditJob> jobs, Texture positionMap = null)
        {
            var shader = Compute;
            if (shader == null || accumulate == null || jobs == null) return false;
            foreach (var job in jobs)
            {
                if (!IsActive(job)) continue;
                foreach (var part in job.ShiftParts)
                {
                    if (part.mask == null) continue;
                    var p = ShiftParams.FromEdit(job.edit, part.stats);
                    shader.SetTexture(s_kernelWeight, MaskId, part.mask);
                    shader.SetTexture(s_kernelWeight, WeightOutId, accumulate);
                    shader.SetInt(WidthId, accumulate.width);
                    shader.SetInt(HeightId, accumulate.height);
                    shader.SetInt(MaskWidthId, part.mask.width);
                    shader.SetInt(MaskHeightId, part.mask.height);
                    shader.SetFloat(StrengthId, p.strength);
                    shader.SetFloat(Strength2Id, p.strength2);
                    bool gradient = p.useGradient && positionMap != null;
                    var pos = gradient ? positionMap : Texture2D.blackTexture;
                    shader.SetTexture(s_kernelWeight, PosMapId, pos);
                    shader.SetInt(PosWidthId, pos.width);
                    shader.SetInt(PosHeightId, pos.height);
                    shader.SetInt(UseGradientId, gradient ? 1 : 0);
                    shader.SetMatrix(WorldToBoxId, p.rootToBox);
                    shader.SetVector(BoxSizeId, p.boxSize);
                    shader.SetInt(GradientInsideOnlyId, gradient && p.gradientInsideOnly ? 1 : 0);
                    shader.Dispatch(s_kernelWeight, Groups(accumulate.width), Groups(accumulate.height), 1);
                }
            }
            return true;
        }

        /// <summary>Run と同じ条件で位置マップを取る（グラデーション ON の編集が無ければ null）。PositionMap のキャッシュ所有なので解放しない</summary>
        internal static RenderTexture GetPositionMapIfNeeded(PipelineInput input, int width, int height)
        {
            if (input == null || input.root == null || input.renderers == null || !(HasGradient(input.jobs) || HasFlatten(input.jobs))) return null;
            return PositionMap.GetOrBuild(input.root, input.renderers, input.sourceAsset, width, height, FillIterations(input.jobs));
        }

        // ── 作業 RT ──

        /// <summary>
        /// 元テクスチャを読み込み（PNG/JPG は直読み）、長辺 workingSize 以下（縦横比を保つ・元より大きくしない）の
        /// 線形の作業 RT へ写したものを返す。直読みできない形式（TGA/PSD 等）はインポート済みの資産を Blit で縮める。
        /// sRGB の資産は Blit のサンプリングで線形になる（作業 RT は Linear）。読めなければ null
        /// </summary>
        private static RenderTexture CreateSourceWorkTexture(Texture2D asset, int workingSize, bool useMipMap = false)
        {
            var handle = SourceTextureLoader.Acquire(asset, workingSize);
            try
            {
                if (handle == null || !handle.IsValid) return null;
                var texture = handle.Texture;
                ScaleToFit(texture.width, texture.height, workingSize, out int width, out int height);
                var rt = CreateWorkTexture(width, height, "ClickRecolor_Work", asset, useMipMap);
                // Blit は RenderTexture.active を切り替えたまま戻さないので元へ戻す
                var previous = RenderTexture.active;
                try
                {
                    Graphics.Blit(texture, rt);
                }
                catch
                {
                    DestroyWorkTexture(rt);
                    throw;
                }
                finally
                {
                    RenderTexture.active = previous;
                }
                return rt;
            }
            finally
            {
                handle?.Dispose();
            }
        }

        /// <summary>
        /// width×height を、縦横比を保って長辺が maxSize 以下になるよう縮めた大きさ（maxSize ≤ 0 か、既に収まっていればそのまま）。
        /// 丸め方は SourceTextureLoader の縮小と同じ
        /// </summary>
        private static void ScaleToFit(int width, int height, int maxSize, out int scaledWidth, out int scaledHeight)
        {
            scaledWidth = width;
            scaledHeight = height;
            if (maxSize <= 0 || Mathf.Max(width, height) <= maxSize) return;
            float scale = (float)maxSize / Mathf.Max(width, height);
            scaledWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
            scaledHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));
        }

        /// <summary>
        /// 作業 RT（ARGBHalf・Linear・enableRandomWrite）を作る。samplerFrom があればフィルタ・異方性・繰り返しの設定を写す
        /// （結果をマテリアルに束縛したときに元テクスチャと同じ見え方にする）。
        /// useMipMap なら mip 付きで作る（自動生成はしない。書き終えたら GenerateMips）。使い終わったら DestroyWorkTexture
        /// </summary>
        internal static RenderTexture CreateWorkTexture(
            int width, int height, string name, Texture samplerFrom = null, bool useMipMap = false)
        {
            var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBHalf, 0)
            {
                sRGB = false,
                enableRandomWrite = true,
                useMipMap = useMipMap,
                autoGenerateMips = false,
                msaaSamples = 1,
            };
            var rt = new RenderTexture(desc)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = samplerFrom != null ? samplerFrom.filterMode : FilterMode.Bilinear,
                wrapModeU = samplerFrom != null ? samplerFrom.wrapModeU : TextureWrapMode.Clamp,
                wrapModeV = samplerFrom != null ? samplerFrom.wrapModeV : TextureWrapMode.Clamp,
                anisoLevel = samplerFrom != null ? samplerFrom.anisoLevel : 1,
            };
            rt.Create();
            return rt;
        }

        /// <summary>CreateWorkTexture・Run で得た RT を解放して破棄する（null は無視）</summary>
        internal static void DestroyWorkTexture(RenderTexture rt)
        {
            if (rt == null) return;
            if (RenderTexture.active == rt) RenderTexture.active = null;
            rt.Release();
            Object.DestroyImmediate(rt);
        }
    }
}
