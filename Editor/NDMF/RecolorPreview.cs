// 流用元: dev.nekoare.tex-col-adjuster/Editor/NDMF/TexColorAdjusterPreview.cs（観測・マテリアル差し替えノード）
//         dev.nekoare.uv-side-splitter/Editor/NDMF/UvSideSplitPreview.cs（グループのデータ比較・設定ハッシュ）
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    /// <summary>
    /// 選択範囲のハイライトの状態（設計 §8.3）。ツール（ToolSession）が RecolorPreview.Highlight に publish し、
    /// プレビューが観測する。値で比べる（同じ値の publish ではノードを作り直さない）。
    /// ハイライトする編集は id の集合（連結なら全メンバー。テクスチャごとに 1 つずつ効く）
    /// </summary>
    internal readonly struct HighlightState : IEquatable<HighlightState>
    {
        /// <summary>ハイライトする編集の id（連結なら全メンバー・edits の並び順）。無ければ null</summary>
        public readonly string[] editIds;
        /// <summary>ハイライトを出すか（ツール有効・［範囲を表示］ON・編集あり）</summary>
        public readonly bool enabled;

        /// <summary>編集 1 件だけをハイライトする状態（editId が null なら編集なし）</summary>
        public HighlightState(string editId, bool enabled)
            : this(editId != null ? new[] { editId } : null, enabled)
        {
        }

        public HighlightState(string[] editIds, bool enabled)
        {
            this.editIds = editIds != null && editIds.Length > 0 ? editIds : null;
            this.enabled = enabled;
        }

        /// <summary>
        /// edit とその連結の全メンバー（Scene ツールの EditGroups.Members）の id でハイライトする状態。edit が null なら編集なし
        /// </summary>
        public static HighlightState For(ClickRecolor component, RecolorEdit edit, bool enabled = true)
        {
            if (edit == null) return new HighlightState((string[])null, enabled);
            var members = SceneTool.EditGroups.Members(component, edit);
            var ids = new List<string>(members.Count);
            foreach (var m in members)
            {
                if (m != null && m.id != null && !ids.Contains(m.id)) ids.Add(m.id);
            }
            return new HighlightState(ids.ToArray(), enabled);
        }

        /// <summary>id がハイライトする編集に入っているか（enabled は見ない）</summary>
        public bool Contains(string id)
        {
            if (id == null || editIds == null) return false;
            foreach (var e in editIds)
            {
                if (e == id) return true;
            }
            return false;
        }

        /// <summary>順序込みで比べる（同じ連結でも並びが変わったら別の値）</summary>
        public bool Equals(HighlightState other)
        {
            if (enabled != other.enabled) return false;
            if (ReferenceEquals(editIds, other.editIds)) return true;
            if (editIds == null || other.editIds == null || editIds.Length != other.editIds.Length) return false;
            for (int i = 0; i < editIds.Length; i++)
            {
                if (editIds[i] != other.editIds[i]) return false;
            }
            return true;
        }

        public override bool Equals(object obj) => obj is HighlightState other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = enabled ? 1 : 0;
                if (editIds != null)
                {
                    foreach (var id in editIds) h = h * 397 ^ (id != null ? id.GetHashCode() : 0);
                }
                return h;
            }
        }

        public override string ToString() =>
            $"HighlightState([{(editIds != null ? string.Join(",", editIds) : "")}], {enabled})";
    }

    /// <summary>
    /// 色変えの結果をシーンに即時反映する NDMF プレビュー（設計 §8）。
    /// アバターごとに 1 グループ。テクスチャごとに RecolorPipeline で結果 RT を作り、
    /// そのテクスチャをメインに持つマテリアルの複製に束縛して差し替える。元マテリアル・元テクスチャには書かない
    /// </summary>
    internal class RecolorPreview : IRenderFilter
    {
        private const string LogPrefix = "[Tocolo]";

        /// <summary>警告を出し済みの「編集 id + 種類」。同じ警告を Instantiate のたびに並べない</summary>
        private static readonly HashSet<string> s_warned = new HashSet<string>();

        /// <summary>
        /// 選択範囲のハイライトの状態（ToolSession が書く）。変わると Instantiate がやり直され、
        /// 結果 RT とマスクはキャッシュから取り直してハイライトの合成だけやり直す
        /// </summary>
        internal static readonly PublishedValue<HighlightState> Highlight =
            new PublishedValue<HighlightState>(default, "ClickRecolor/Highlight");

        // ── グループ ──

        /// <summary>GetTargetGroups から Instantiate へ渡すデータ。比較は参照（要素ごと）で行う</summary>
        private sealed class GroupData
        {
            public GameObject avatar;
            public ClickRecolor[] components;

            public static bool Same(GroupData a, GroupData b)
            {
                if (ReferenceEquals(a, b)) return true;
                if (a == null || b == null || a.avatar != b.avatar) return false;
                if (a.components == null || b.components == null) return a.components == b.components;
                if (a.components.Length != b.components.Length) return false;
                for (int i = 0; i < a.components.Length; i++)
                {
                    if (a.components[i] != b.components[i]) return false;
                }
                return true;
            }
        }

        public ImmutableList<RenderGroup> GetTargetGroups(ComputeContext context)
        {
            var groups = ImmutableList.CreateBuilder<RenderGroup>();
            // ハイライト中の色未設定の編集も対象にするので、ハイライトの切り替えで対象の Renderer が変わる
            var highlight = context.Observe(Highlight);
            foreach (var avatar in context.GetAvatarRoots())
            {
                try
                {
                    var components = context.GetComponentsInChildren<ClickRecolor>(avatar, true);
                    var textures = new HashSet<Texture2D>();
                    foreach (var component in components)
                    {
                        if (component == null) continue;
                        // List は参照比較なので、編集の中身のハッシュで変化を拾う
                        context.Observe(component, ComputeEditsHash);
                        CollectTargetTextures(component, textures, highlight);
                    }
                    if (textures.Count == 0) continue;

                    var candidates = new List<Renderer>();
                    foreach (var renderer in context.GetComponentsInChildren<Renderer>(avatar, true))
                    {
                        if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                        if (!context.ActiveInHierarchy(renderer.gameObject)) continue;
                        // マテリアル差し替え・メインテクスチャの差し替えでグループを作り直す
                        var materials = context.Observe(renderer, r => r.sharedMaterials, SameMaterialArray);
                        if (materials == null) continue;
                        foreach (var material in materials)
                        {
                            if (material != null) context.Observe(material, MainTextureOf);
                        }
                        candidates.Add(renderer);
                    }

                    var renderers = CollectRenderers(candidates, textures);
                    if (renderers.Count == 0) continue;

                    var data = new GroupData { avatar = avatar, components = components };
                    groups.Add(RenderGroup.For(renderers).WithData(data, GroupData.Same));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{LogPrefix} プレビューの対象収集に失敗しました（{(avatar != null ? avatar.name : "?")}）: {ex}");
                }
            }
            return groups.ToImmutable();
        }

        public Task<IRenderFilterNode> Instantiate(
            RenderGroup group,
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            var entries = new List<PreviewTextureCache.Entry>();
            // 結果 RT を描き直すテクスチャ（元テクスチャのハンドルは Entry へ渡すまでここが持つ）
            var pendings = new List<PendingTexture>();
            var clones = new List<Material>();
            // ハイライトを掛けた RT（ノード所有。キャッシュしない）
            var owned = new List<RenderTexture>();
            bool handedOver = false;
            try
            {
                var data = group.GetData<GroupData>();
                // ハイライトの状態の変化（編集の切り替え・［範囲を表示］・ツールの終了）でもやり直す。
                // 色未設定の編集もハイライト中はプレビューの対象にするので、計画を作る前に取る
                var highlight = context.Observe(Highlight);
                var plans = BuildPlans(data, context, highlight, out var order);
                if (plans.Count == 0) return Task.FromResult<IRenderFilterNode>(new EmptyNode());

                var pairs = new List<(Renderer original, Renderer proxy)>();
                var originals = new List<Renderer>();
                foreach (var (original, proxy) in proxyPairs)
                {
                    if (original == null || proxy == null) continue;
                    var materials = context.Observe(original, r => r.sharedMaterials, SameMaterialArray);
                    if (materials != null)
                    {
                        // 元マテリアルの編集（色・Tiling/Offset 等）に追従させる（複製を作り直す）
                        foreach (var material in materials)
                        {
                            if (material != null) context.Observe(material);
                        }
                    }
                    pairs.Add((original, proxy));
                    originals.Add(original);
                }

                // 連結の統計を共有する編集（全テクスチャぶん）。結果 RT の鍵に、同じ連結の他のメンバーの中身も入れる
                var groupMembers = CollectGroupStatsMembers(plans, order);

                // 1 段目: テクスチャごとに結果 RT のキャッシュを引き、無いものは PrepareJob まで済ませる
                // （連結の統計は全テクスチャの Job がそろってから共有する）
                var usersOf = new Dictionary<Texture2D, List<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)>>();
                var entryOf = new Dictionary<Texture2D, PreviewTextureCache.Entry>();
                foreach (var texture in order)
                {
                    // 再インポート（画素の変更）でも作り直す
                    context.Observe(texture, t => t.imageContentsHash);
                    var plan = plans[texture];
                    var users = CollectUsers(originals, texture);
                    usersOf[texture] = users;
                    WarnUnreadableUsers(texture, users);

                    var key = new PreviewTextureCache.Key(texture, ComputeTextureHash(plan.edits, users, groupMembers), plan.size);
                    var entry = PreviewTextureCache.TryAcquire(key);
                    if (entry != null)
                    {
                        entries.Add(entry);
                        entryOf[texture] = entry;
                        continue;
                    }

                    // 元テクスチャのデコード結果を Entry に持たせる。PrepareJob / Run の中の読み込み（同じ大きさ）が
                    // このハンドルのキャッシュに当たる。スライダー操作でノードを作り直すときは、新ノードの Instantiate の間
                    // 旧ノードの Entry がまだハンドルを持っているので参照が途切れず、PNG を再デコードしない
                    // （旧ノードは新ノードができた後に破棄される）
                    var pending = new PendingTexture { texture = texture, plan = plan, key = key };
                    pendings.Add(pending);
                    pending.source = SourceTextureLoader.Acquire(texture, plan.size);
                    pending.jobs = PrepareJobs(texture, plan, users, originals);
                }

                // 描き直すテクスチャの連結の統計を共有する。キャッシュに当たったテクスチャのメンバーも、統計を取るためだけに PrepareJob する
                // （そのテクスチャは Run しない。マスクは MaskCache に当たることが多い）
                var allJobs = new List<EditJob>();
                var sharedGroups = new HashSet<string>();
                foreach (var pending in pendings)
                {
                    foreach (var job in pending.jobs)
                    {
                        allJobs.Add(job);
                        if (RecolorPipeline.SharesGroupStats(job.edit)) sharedGroups.Add(job.edit.groupId);
                    }
                }
                if (sharedGroups.Count > 0)
                {
                    foreach (var texture in order)
                    {
                        if (!entryOf.ContainsKey(texture)) continue;
                        var plan = plans[texture];
                        foreach (var edit in plan.edits)
                        {
                            if (!RecolorPipeline.SharesGroupStats(edit) || !sharedGroups.Contains(edit.groupId)) continue;
                            if (edit.seedRenderer == null && RecolorPipeline.NeedsSeedRenderer(edit)) continue;
                            var job = RecolorPipeline.PrepareJob(edit, texture, plan.size, usersOf[texture], context: MaskContext.For(plan.root, originals));
                            if (job != null) allJobs.Add(job);
                        }
                    }
                }
                RecolorPipeline.ShareGroupStats(allJobs);

                // 2 段目: 描き直すテクスチャごとに Run
                foreach (var pending in pendings)
                {
                    var rt = Render(pending.texture, pending.plan, pending.jobs, originals);
                    if (rt == null) continue;
                    var entry = PreviewTextureCache.Add(pending.key, rt, pending.source);
                    pending.source = null; // 所有権を Entry へ渡した（Entry の破棄で Dispose）
                    entries.Add(entry);
                    entryOf[pending.texture] = entry;
                }

                // テクスチャごとの結果 RT
                var results = new Dictionary<Texture2D, RenderTexture>();
                foreach (var texture in order)
                {
                    if (!entryOf.TryGetValue(texture, out var entry)) continue;
                    results[texture] = entry.texture;

                    // 現在の編集がこのテクスチャに効いていれば、結果 RT にハイライトを掛けた別の RT を束縛する
                    var highlighted = CreateHighlighted(highlight, texture, plans[texture], usersOf[texture], entry.texture, originals);
                    if (highlighted != null)
                    {
                        owned.Add(highlighted);
                        results[texture] = highlighted;
                    }
                }

                // マテリアルの差し替え配列（Renderer ごと。OnFrame でアロケーションしないよう先に作る）
                var cloneOf = new Dictionary<Material, Material>();
                var overrides = new Dictionary<Renderer, Material[]>();
                foreach (var (original, proxy) in pairs)
                {
                    // 上流のフィルタが差し替えた結果を入力にする
                    var materials = proxy.sharedMaterials;
                    Material[] replaced = null;
                    for (int i = 0; i < materials.Length; i++)
                    {
                        var material = materials[i];
                        if (!MaterialTextureResolver.TryGetMainTexture(material, out var info)) continue;
                        if (!results.TryGetValue(info.texture, out var rt) || rt == null) continue;

                        if (!cloneOf.TryGetValue(material, out var clone))
                        {
                            clone = MaterialCloner.Clone(material, " (ClickRecolor)");
                            clone.hideFlags = HideFlags.HideAndDontSave;
                            // Tiling/Offset は複製で引き継がれる。結果 RT は Linear（シェーダーは線形値として読む）
                            clone.SetTexture(info.propertyName, rt);
                            cloneOf.Add(material, clone);
                            clones.Add(clone);
                        }
                        replaced ??= (Material[])materials.Clone();
                        replaced[i] = clone;
                    }
                    if (replaced != null) overrides[original] = replaced;
                }

                if (overrides.Count == 0) return Task.FromResult<IRenderFilterNode>(new EmptyNode());

                handedOver = true;
                return Task.FromResult<IRenderFilterNode>(new Node(overrides, clones, entries, owned));
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix} プレビューの生成に失敗しました: {ex}");
                return Task.FromResult<IRenderFilterNode>(new EmptyNode());
            }
            finally
            {
                if (!handedOver)
                {
                    foreach (var clone in clones)
                    {
                        if (clone != null) Object.DestroyImmediate(clone);
                    }
                    foreach (var entry in entries) PreviewTextureCache.Release(entry);
                    foreach (var rt in owned) RecolorPipeline.DestroyWorkTexture(rt);
                }
                // Entry へ渡せなかった（Run できなかった・例外）元テクスチャのハンドルを手放す
                foreach (var pending in pendings) pending.source?.Dispose();
                // 全テクスチャの Run とハイライトでマスクを使い終えたので、ここで 1 回だけ減らす
                // （Run の途中で減らすと、連結の統計のために先に準備した他のテクスチャのマスクを捨ててしまう）。
                // あふれて捨てた編集のマスクは、次にその編集を描くときに PrepareJob が作り直す
                MaskCache.Trim();
            }
        }

        // ── テクスチャごとの計画 ──

        private sealed class TexturePlan
        {
            public int size;
            /// <summary>グラデーションの位置の基準（そのテクスチャに最初に効くコンポーネントの Transform）</summary>
            public Transform root;
            public readonly List<RecolorEdit> edits = new List<RecolorEdit>();
        }

        /// <summary>
        /// プレビューに効く編集をテクスチャごとに適用順でまとめる。解像度はそのテクスチャに最初に効くコンポーネントのもの。
        /// ハイライト中の色未設定の編集も入れる（色は変わらず、ハイライトだけ乗る）。
        /// ついでに各コンポーネントの編集ハッシュを観測する
        /// </summary>
        private static Dictionary<Texture2D, TexturePlan> BuildPlans(
            GroupData data, ComputeContext context, in HighlightState highlight, out List<Texture2D> order)
        {
            var plans = new Dictionary<Texture2D, TexturePlan>();
            order = new List<Texture2D>();
            if (data?.components == null) return plans;

            foreach (var component in data.components)
            {
                if (component == null) continue;
                context.Observe(component, ComputeEditsHash);
                if (!component.previewEnabled || component.edits == null) continue;

                foreach (var edit in component.edits)
                {
                    if (!IsPreviewTarget(edit, highlight)) continue;
                    if (!plans.TryGetValue(edit.sourceTexture, out var plan))
                    {
                        plan = new TexturePlan { size = (int)component.previewResolution, root = component.transform };
                        plans.Add(edit.sourceTexture, plan);
                        order.Add(edit.sourceTexture);
                    }
                    plan.edits.Add(edit);
                }
            }
            return plans;
        }

        /// <summary>全テクスチャの計画のうち、連結の統計を共有する編集（RecolorPipeline.SharesGroupStats。適用順）</summary>
        private static List<RecolorEdit> CollectGroupStatsMembers(Dictionary<Texture2D, TexturePlan> plans, List<Texture2D> order)
        {
            var result = new List<RecolorEdit>();
            foreach (var texture in order)
            {
                foreach (var edit in plans[texture].edits)
                {
                    if (RecolorPipeline.SharesGroupStats(edit)) result.Add(edit);
                }
            }
            return result;
        }

        /// <summary>
        /// プレビューに効く編集か: 有効・対象テクスチャあり・目標色あり。
        /// 目標色が未設定の編集は見た目を変えないので、そのテクスチャは差し替えない（縮小した作業解像度の画像に置き換えない）。
        /// ビルド（RecolorPass）も同じ条件を使う（プレビューとビルドで効く編集を揃える）
        /// </summary>
        internal static bool IsPreviewTarget(RecolorEdit edit) =>
            edit != null && edit.enabled && edit.hasTarget && edit.sourceTexture != null;

        /// <summary>
        /// プレビューで扱う編集か: IsPreviewTarget に加えて、ハイライト中の編集なら目標色が未設定でも対象にする
        /// （クリック直後から選択範囲を見せる。パイプラインは目標色が未設定の編集を素通しするので、色は変わらずハイライトだけ乗る）。
        /// ビルドはハイライトを見ないので IsPreviewTarget(edit) のまま
        /// </summary>
        internal static bool IsPreviewTarget(RecolorEdit edit, in HighlightState highlight)
        {
            if (IsPreviewTarget(edit)) return true;
            return edit != null && edit.enabled && edit.sourceTexture != null
                && highlight.enabled && highlight.Contains(edit.id);
        }

        /// <summary>プレビューで扱う編集（ハイライト中の色未設定の編集を含む）の対象テクスチャを textures に足す</summary>
        private static void CollectTargetTextures(ClickRecolor component, HashSet<Texture2D> textures, in HighlightState highlight)
        {
            if (!component.previewEnabled || component.edits == null) return;
            foreach (var edit in component.edits)
            {
                if (!IsPreviewTarget(edit, highlight)) continue;
                textures.Add(edit.sourceTexture);
            }
        }

        /// <summary>結果 RT を描き直すテクスチャ 1 枚ぶん（Instantiate の 1 段目で作り、2 段目で Run する）</summary>
        private sealed class PendingTexture
        {
            public Texture2D texture;
            public TexturePlan plan;
            public PreviewTextureCache.Key key;
            /// <summary>元テクスチャのハンドル（Entry へ渡したら null）</summary>
            public SourceTexture source;
            public List<EditJob> jobs;
        }

        /// <summary>
        /// 1 テクスチャぶんの編集を PrepareJob する（適用順）。種の Renderer が無い・マスクが作れない編集は警告して飛ばす
        /// </summary>
        private static List<EditJob> PrepareJobs(
            Texture2D texture,
            TexturePlan plan,
            IReadOnlyList<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users,
            IReadOnlyList<Renderer> renderers)
        {
            var jobs = new List<EditJob>();
            var context = MaskContext.For(plan.root, renderers);
            foreach (var edit in plan.edits)
            {
                if (edit.seedRenderer == null && RecolorPipeline.NeedsSeedRenderer(edit))
                {
                    WarnOnce(edit, "seed",
                        $"編集「{edit.name}」でクリックした Renderer が見つかりません（削除された可能性があります）。この編集はプレビューに反映されません");
                    continue;
                }
                var job = RecolorPipeline.PrepareJob(edit, texture, plan.size, users, context: context);
                if (job == null)
                {
                    WarnOnce(edit, "mask",
                        $"編集「{edit.name}」の選択範囲を作れませんでした（メッシュの Read/Write が無効、GPU が使えない等）。この編集はプレビューに反映されません");
                    continue;
                }
                jobs.Add(job);
            }
            return jobs;
        }

        /// <summary>
        /// 1 テクスチャぶんの結果 RT（ミップ付き・ARGBHalf・Linear）を準備済みの jobs から作る。反映できる編集が 1 つも無ければ null
        /// </summary>
        /// <remarks>
        /// 元テクスチャの読み込みは呼び出し側（Instantiate）が同じ大きさで先に取り、結果の Entry に持たせている
        /// </remarks>
        private static RenderTexture Render(
            Texture2D texture,
            TexturePlan plan,
            List<EditJob> jobs,
            IReadOnlyList<Renderer> renderers)
        {
            if (jobs == null || jobs.Count == 0) return null;

            // Run の結果は最初からミップ付き（遠目でちらつかない）なので、そのまま束縛する
            var result = RecolorPipeline.Run(new PipelineInput
            {
                sourceAsset = texture,
                workingSize = plan.size,
                jobs = jobs,
                // グラデーションの位置の基準はそのテクスチャに最初に効くコンポーネントの GameObject（対象ルート）
                root = plan.root,
                renderers = renderers,
            });
            return result;
        }

        /// <summary>
        /// ハイライトが有効で、その編集（連結ならどれかのメンバー）が texture に効く（plan に入っている）なら、result にハイライトを掛けた新しい RT を返す（呼び出し側の所有）。
        /// マスクは PrepareJob で取る（結果 RT を描いたときに準備済みならキャッシュに当たる。結果 RT がキャッシュに当たって
        /// マスクが追い出されていた場合はここで作り直す）。対象外・マスクが作れない・GPU が使えなければ null
        /// </summary>
        private static RenderTexture CreateHighlighted(
            HighlightState highlight,
            Texture2D texture,
            TexturePlan plan,
            IReadOnlyList<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users,
            RenderTexture result,
            IReadOnlyList<Renderer> renderers)
        {
            if (!highlight.enabled || highlight.editIds == null || result == null) return null;

            // ハイライトする編集（連結ならメンバー）のうち、このテクスチャに効く最初のもの
            RecolorEdit edit = null;
            foreach (var candidate in plan.edits)
            {
                if (!highlight.Contains(candidate.id)) continue;
                edit = candidate;
                break;
            }
            if (edit == null || (edit.seedRenderer == null && RecolorPipeline.NeedsSeedRenderer(edit))) return null;

            var job = RecolorPipeline.PrepareJob(edit, texture, plan.size, users, context: MaskContext.For(plan.root, renderers));
            if (job?.mask == null) return null;
            return SelectionHighlight.CreateHighlighted(result, job.mask);
        }

        // ── 抽出（テスト用に ComputeContext を使わない静的関数に分けてある）──

        /// <summary>candidates のうち、メインテクスチャが textures のどれかであるスロットを持つ Renderer（順序は保つ）</summary>
        internal static List<Renderer> CollectRenderers(IEnumerable<Renderer> candidates, HashSet<Texture2D> textures)
        {
            var result = new List<Renderer>();
            if (candidates == null || textures == null || textures.Count == 0) return result;
            foreach (var renderer in candidates)
            {
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials;
                if (materials == null) continue;
                foreach (var material in materials)
                {
                    if (!MaterialTextureResolver.TryGetMainTexture(material, out var info)) continue;
                    if (!textures.Contains(info.texture)) continue;
                    result.Add(renderer);
                    break;
                }
            }
            return result;
        }

        /// <summary>
        /// texture をメインに持つ全スロットの (メッシュ, サブメッシュ, Tiling, Offset)。重複は 1 つにまとめる。
        /// Tiling が (0,0) のときは (1,1) として扱う（MaterialTextureResolver.ToTextureCoord と同じ）。
        /// メッシュの無い Renderer・メッシュのサブメッシュ数を超えるスロットは含めない
        /// </summary>
        internal static List<(Mesh, int, Vector2, Vector2)> CollectUsers(IEnumerable<Renderer> renderers, Texture2D texture)
        {
            var result = new List<(Mesh, int, Vector2, Vector2)>();
            if (renderers == null || texture == null) return result;
            var seen = new HashSet<(Mesh, int, Vector2, Vector2)>();
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var mesh = RendererMeshAccess.GetSharedMesh(renderer);
                if (mesh == null) continue;
                var materials = renderer.sharedMaterials;
                if (materials == null) continue;

                int slots = Mathf.Min(materials.Length, mesh.subMeshCount);
                for (int i = 0; i < slots; i++)
                {
                    if (!MaterialTextureResolver.TryGetMainTexture(materials[i], out var info)) continue;
                    if (info.texture != texture) continue;
                    var scale = info.scale == Vector2.zero ? Vector2.one : info.scale;
                    var user = (mesh, i, scale, info.offset);
                    if (seen.Add(user)) result.Add(user);
                }
            }
            return result;
        }

        // ── ハッシュ ──

        /// <summary>
        /// プレビューに影響するコンポーネントの状態のハッシュ（順序依存）。編集の出力に関わる全フィールドと
        /// previewResolution・previewEnabled を含む（表示専用の name・seedColor は含めない）
        /// </summary>
        internal static int ComputeEditsHash(ClickRecolor component)
        {
            if (component == null) return 0;
            unchecked
            {
                int h = 17;
                h = h * 31 + (int)component.previewResolution;
                h = h * 31 + (component.previewEnabled ? 1 : 0);
                // 除外リストは「箱の中」のマスク（位置を描く Renderer）を変えるので含める（ユーザー要望 2026-09-29）
                if (component.excludedRenderers != null)
                {
                    h = h * 31 + component.excludedRenderers.Count;
                    foreach (var renderer in component.excludedRenderers) h = h * 31 + (renderer != null ? renderer.GetInstanceID() : 0);
                }
                var edits = component.edits;
                if (edits == null) return h;
                h = h * 31 + edits.Count;
                foreach (var edit in edits) h = HashEdit(h, edit);
                return h;
            }
        }

        private static int HashEdit(int h, RecolorEdit edit)
        {
            unchecked
            {
                if (edit == null) return h * 31 + 1;
                h = h * 31 + (edit.id != null ? edit.id.GetHashCode() : 0);
                h = h * 31 + (edit.enabled ? 1 : 0);
                h = h * 31 + (edit.sourceTexture != null ? edit.sourceTexture.GetInstanceID() : 0);
                h = h * 31 + (edit.seedRenderer != null ? edit.seedRenderer.GetInstanceID() : 0);
                h = h * 31 + edit.seedSubmesh;
                h = h * 31 + edit.seedTriangle;
                h = h * 31 + edit.seedUv.GetHashCode();
                h = h * 31 + RecolorPipeline.HashExtraSeeds(edit);
                h = h * 31 + (edit.hasSeedOklab ? 1 : 0);
                h = h * 31 + edit.seedOklab.GetHashCode();
                h = h * 31 + (edit.groupId != null ? edit.groupId.GetHashCode() : 0);
                h = h * 31 + (int)edit.mode;
                h = h * 31 + (int)edit.scope;
                h = h * 31 + edit.threshold.GetHashCode();
                h = h * 31 + edit.feather.GetHashCode();
                h = h * 31 + edit.cleanupRadius;
                h = h * 31 + edit.padding;
                h = h * 31 + (edit.perSeedStats ? 1 : 0);
                h = h * 31 + edit.targetColor.GetHashCode();
                h = h * 31 + (edit.hasTarget ? 1 : 0);
                h = h * 31 + edit.darkEndRatio.GetHashCode();
                h = h * 31 + edit.lightnessToTarget.GetHashCode();
                h = h * 31 + edit.chromaToTarget.GetHashCode();
                h = h * 31 + edit.hueRetain.GetHashCode();
                h = h * 31 + edit.strength.GetHashCode();
                h = h * 31 + edit.shadingStretch.GetHashCode();
                h = h * 31 + (edit.flattenBase ? 1 : 0);
                if (edit.flattenBase) h = h * 31 + edit.flattenStrength.GetHashCode();
                h = h * 31 + (edit.gradientEnabled ? 1 : 0);
                // 「箱の中」は箱で範囲が決まる（他のモードでは箱を変えても結果が変わらないので含めない）
                if (edit.mode == SelectionMode.Box)
                {
                    h = h * 31 + edit.boxPosition.GetHashCode();
                    h = h * 31 + edit.boxRotation.GetHashCode();
                    h = h * 31 + edit.boxSize.GetHashCode();
                    // ドラッグ中はパーツごとの統計とスーパーサンプリングを掛けないので、離した瞬間に作り直せるようドラッグ状態も含める
                    h = h * 31 + (edit.boxPerPartStats ? 1 : 0);
                    h = h * 31 + (SceneTool.ToolSession.BoxDragging ? 1 : 0);
                }
                // グラデーション OFF のときは終了色・箱を変えても結果が変わらないので含めない（無駄に作り直さない）
                if (edit.gradientEnabled)
                {
                    h = h * 31 + edit.gradientColor.GetHashCode();
                    h = h * 31 + edit.gradientBoxPosition.GetHashCode();
                    h = h * 31 + edit.gradientBoxRotation.GetHashCode();
                    h = h * 31 + edit.gradientBoxSize.GetHashCode();
                    h = h * 31 + (edit.gradientInsideOnly ? 1 : 0);
                    h = h * 31 + edit.gradientDarkEndRatio.GetHashCode();
                    h = h * 31 + edit.gradientStrength.GetHashCode();
                }
                return h;
            }
        }

        /// <summary>
        /// 1 テクスチャの結果を決めるもののハッシュ: そのテクスチャに効く編集（適用順）と利用者の組
        /// （メッシュ・サブメッシュ・Tiling/Offset。はみ出し防止の被覆と種の Tiling/Offset が変わるため）。
        /// 編集は目標色を決めた（hasTarget）ものだけ含める。色未設定の編集はパイプラインが素通しするので結果 RT を変えない
        /// （ハイライト中の色未設定の編集のしきい値をドラッグしても結果 RT を作り直さない。ハイライトのマスクは MaskCache が別の鍵で持つ）。
        /// groupMembers（全テクスチャの、連結の統計を共有する編集。CollectGroupStatsMembers）を渡すと、
        /// 統計を共有する編集ごとに、同じ連結の他のメンバーの中身と対象テクスチャの中身も含める
        /// （連結の統計は他のメンバーの選択範囲にも依存するため）
        /// </summary>
        internal static int ComputeTextureHash(
            List<RecolorEdit> edits, List<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users,
            IReadOnlyList<RecolorEdit> groupMembers = null)
        {
            unchecked
            {
                int h = 17;
                foreach (var edit in edits)
                {
                    if (edit == null || !edit.hasTarget) continue;
                    h = HashEdit(h, edit);
                    if (groupMembers == null || !RecolorPipeline.SharesGroupStats(edit)) continue;
                    foreach (var member in groupMembers)
                    {
                        if (member == null || member == edit || member.groupId != edit.groupId) continue;
                        h = HashEdit(h, member);
                        h = h * 31 + (member.sourceTexture != null ? member.sourceTexture.imageContentsHash.GetHashCode() : 0);
                    }
                }
                foreach (var (mesh, submesh, uvScale, uvOffset) in users)
                {
                    h = h * 31 + (mesh != null ? mesh.GetInstanceID() : 0);
                    h = h * 31 + UvChartDetector.Fingerprint(mesh);
                    h = h * 31 + submesh;
                    h = h * 31 + uvScale.GetHashCode();
                    h = h * 31 + uvOffset.GetHashCode();
                }
                return h;
            }
        }

        private static Texture2D MainTextureOf(Material material) =>
            MaterialTextureResolver.TryGetMainTexture(material, out var info) ? info.texture : null;

        /// <summary>sharedMaterials の Observe 用比較（配列は毎回新規インスタンスが返るので要素で比べる）</summary>
        private static bool SameMaterialArray(Material[] a, Material[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        // ── 警告 ──

        /// <summary>
        /// 読み取り不可（Read/Write 無効）のメッシュは全チャート被覆に塗れない（CoverageMask の skippedUnreadable と同じ数え方）。
        /// その分だけはみ出し防止が効かなくなるので、テクスチャごとに 1 回だけ知らせる（同じテクスチャの編集の数だけ並べない）
        /// </summary>
        private static void WarnUnreadableUsers(
            Texture2D texture, List<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users)
        {
            if (texture == null) return;
            int unreadable = 0;
            foreach (var user in users)
            {
                if (user.mesh != null && !user.mesh.isReadable) unreadable++;
            }
            if (unreadable == 0) return;
            WarnOnceByKey($"texture:{texture.GetInstanceID()}:unreadable",
                $"テクスチャ「{texture.name}」を使うメッシュのうち {unreadable} 個は Read/Write が無効なため、" +
                "はみ出し防止の計算に入っていません（隣のパーツに色がはみ出すことがあります）。メッシュのインポート設定で Read/Write を有効にしてください");
        }

        private static void WarnOnce(RecolorEdit edit, string kind, string message) =>
            WarnOnceByKey($"{edit.id}:{kind}", message);

        private static void WarnOnceByKey(string key, string message)
        {
            if (!s_warned.Add(key)) return;
            Debug.LogWarning($"{LogPrefix} {message}");
        }

        // ── キャッシュの消去 ──

        [InitializeOnLoadMethod]
        private static void RegisterCacheClear()
        {
            AssemblyReloadEvents.beforeAssemblyReload += () => ClearCaches(destroyInUse: true);
            EditorSceneManager.sceneClosing += (scene, removingScene) => ClearCaches(destroyInUse: false);
            EditorSceneManager.sceneOpened += (scene, mode) => ClearCaches(destroyInUse: false);
        }

        /// <summary>
        /// プレビュー・マスク・島マスク・被覆・位置マップ・チャート表のキャッシュを全部消す。
        /// destroyInUse = false のとき、表示中のノードが使っている結果 RT はキャッシュから外すだけにして、ノードの破棄で解放する
        /// （他のシーンのアバターのプレビューが消えないように）
        /// </summary>
        internal static void ClearCaches(bool destroyInUse)
        {
            PreviewTextureCache.Clear(destroyInUse);
            MaskCache.ClearCache();
            IslandMaskCache.ClearCache();
            CoverageMask.ClearCache();
            PositionMap.ClearCache();
            UvChartDetector.ClearCache();
            UvTwinDetector.ClearCache();
            s_warned.Clear();
        }

        // ── ノード ──

        private sealed class Node : IRenderFilterNode, IDisposable
        {
            private readonly Dictionary<Renderer, Material[]> _overrides;
            private readonly List<Material> _clones;
            private readonly List<PreviewTextureCache.Entry> _entries;
            /// <summary>ハイライトを掛けた RT（このノードの所有）</summary>
            private readonly List<RenderTexture> _owned;
            private bool _disposed;

            public RenderAspects WhatChanged => RenderAspects.Material;

            public Node(
                Dictionary<Renderer, Material[]> overrides,
                List<Material> clones,
                List<PreviewTextureCache.Entry> entries,
                List<RenderTexture> owned)
            {
                _overrides = overrides;
                _clones = clones;
                _entries = entries;
                _owned = owned;
            }

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (_disposed || original == null || proxy == null) return;
                if (_overrides.TryGetValue(original, out var materials)) proxy.sharedMaterials = materials;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var clone in _clones)
                {
                    if (clone != null) Object.DestroyImmediate(clone);
                }
                _clones.Clear();
                foreach (var entry in _entries) PreviewTextureCache.Release(entry);
                _entries.Clear();
                foreach (var rt in _owned) RecolorPipeline.DestroyWorkTexture(rt);
                _owned.Clear();
            }
        }

        private sealed class EmptyNode : IRenderFilterNode
        {
            public RenderAspects WhatChanged => 0;

            public void OnFrame(Renderer original, Renderer proxy)
            {
            }
        }
    }

    /// <summary>
    /// プレビューの結果 RT のキャッシュ（参照カウント付き）。鍵は
    /// (テクスチャ, そのテクスチャに効く編集と利用者のハッシュ, 作業解像度, テクスチャの中身のハッシュ)。
    /// 参照が 0 になった RT はその場で破棄する（ノードの作り直しの間は新旧で共有される）。
    /// 各 Entry は元テクスチャのハンドル（SourceTexture）も持ち、RT と同時に手放す。
    /// これで表示中のノードがある間はデコード結果が残り、編集の変更で RT を作り直すたびに元画像を読み直さない
    /// </summary>
    internal static class PreviewTextureCache
    {
        internal readonly struct Key : IEquatable<Key>
        {
            private readonly int _textureId;
            private readonly int _editsHash;
            private readonly int _size;
            private readonly Hash128 _contentsHash;

            internal Key(Texture2D texture, int editsHash, int size)
            {
                _textureId = texture != null ? texture.GetInstanceID() : 0;
                _editsHash = editsHash;
                _size = size;
                _contentsHash = texture != null ? texture.imageContentsHash : default;
            }

            public bool Equals(Key other) =>
                _textureId == other._textureId
                && _editsHash == other._editsHash
                && _size == other._size
                && _contentsHash.Equals(other._contentsHash);

            public override bool Equals(object obj) => obj is Key other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = _textureId;
                    h = h * 397 ^ _editsHash;
                    h = h * 397 ^ _size;
                    h = h * 397 ^ _contentsHash.GetHashCode();
                    return h;
                }
            }
        }

        internal sealed class Entry
        {
            public Key key;
            public RenderTexture texture;
            /// <summary>元テクスチャのハンドル（Entry が所有。Entry の破棄で Dispose）。無ければ null</summary>
            public SourceTexture source;
            public int refCount;
        }

        private static readonly Dictionary<Key, Entry> s_entries = new Dictionary<Key, Entry>();

        /// <summary>キャッシュ中の件数（テスト用）</summary>
        internal static int Count => s_entries.Count;

        /// <summary>鍵が一致し RT が生きていれば参照を 1 つ増やして返す。無ければ null</summary>
        internal static Entry TryAcquire(in Key key)
        {
            if (!s_entries.TryGetValue(key, out var entry)) return null;
            if (entry.texture == null || !entry.texture.IsCreated())
            {
                // 描画デバイスのリセット等で中身を失った RT は外して作り直させる（持っているノードは破棄時に Release する）
                s_entries.Remove(key);
                return null;
            }
            entry.refCount++;
            return entry;
        }

        /// <summary>RT と元テクスチャのハンドル（null 可）の所有権を受け取って登録し、参照 1 で返す</summary>
        internal static Entry Add(in Key key, RenderTexture texture, SourceTexture source = null)
        {
            // 同じ鍵が残っていれば外すだけ（持っているノードの破棄で解放される）
            s_entries.Remove(key);
            var entry = new Entry { key = key, texture = texture, source = source, refCount = 1 };
            s_entries.Add(key, entry);
            return entry;
        }

        /// <summary>参照を 1 つ減らし、0 になったら RT を破棄する</summary>
        internal static void Release(Entry entry)
        {
            if (entry == null) return;
            entry.refCount--;
            if (entry.refCount > 0) return;

            if (s_entries.TryGetValue(entry.key, out var current) && ReferenceEquals(current, entry))
            {
                s_entries.Remove(entry.key);
            }
            DestroyEntry(entry);
        }

        /// <summary>RT を破棄し、元テクスチャのハンドルを手放す</summary>
        private static void DestroyEntry(Entry entry)
        {
            RecolorPipeline.DestroyWorkTexture(entry.texture);
            entry.texture = null;
            entry.source?.Dispose();
            entry.source = null;
        }

        /// <summary>
        /// キャッシュを空にする。destroyInUse = true なら参照中の RT も破棄する（ドメインリロード前）。
        /// false なら参照中の RT は外すだけで、持っているノードの Release で破棄される
        /// </summary>
        internal static void Clear(bool destroyInUse)
        {
            if (destroyInUse)
            {
                foreach (var entry in s_entries.Values) DestroyEntry(entry);
            }
            s_entries.Clear();
        }
    }
}
