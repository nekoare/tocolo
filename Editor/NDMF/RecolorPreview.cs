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
    /// そのテクスチャをメインに持つマテリアルの複製に束縛して差し替える。元マテリアル・元テクスチャには書かない。
    /// 担当範囲（RecolorScope）ごとに 1 つずつ置く: 本体は髪ツールより前で今までどおり参照の一致で差し替え、
    /// 髪用は髪ツールの後で、元の Renderer のスロット番号から髪ツールが入れたマテリアルを見つけ、そのメイン（髪ツールの出力）を土台にする
    /// </summary>
    internal class RecolorPreview : IRenderFilter
    {
        private const string LogPrefix = "[Tocolo]";

        private readonly RecolorScope _scope;

        public RecolorPreview() : this(RecolorScope.Main)
        {
        }

        public RecolorPreview(RecolorScope scope)
        {
            _scope = scope;
        }

        /// <summary>髪用は、髪ツールが髪用の入口より前に並ぶと宣言しているときだけ動く（宣言が無ければ本体が髪も今までどおり担当する）</summary>
        public bool IsEnabled(ComputeContext context) => _scope == RecolorScope.Main || HairToolTargets.OrderSupported;

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
        internal sealed class GroupData
        {
            public GameObject avatar;
            public ClickRecolor[] components;
            /// <summary>
            /// 範囲の利用者（被覆・位置マップ・種の Tiling/Offset）にする Renderer: 対象テクスチャを使う全 Renderer（担当範囲で絞る前）。
            /// 本体と髪用で同じ一覧にして、同じ編集の範囲マスクを一致させる（MaskCache も共有される）
            /// </summary>
            public Renderer[] users;
            /// <summary>髪ツールから見た役割（対象でない Renderer は入らない）</summary>
            public Dictionary<Renderer, HairToolRole> roles;

            public static bool Same(GroupData a, GroupData b)
            {
                if (ReferenceEquals(a, b)) return true;
                if (a == null || b == null || a.avatar != b.avatar) return false;
                return SameArray(a.components, b.components) && SameArray(a.users, b.users) && SameRoles(a.roles, b.roles);
            }

            private static bool SameArray<T>(T[] a, T[] b) where T : Object
            {
                if (a == null || b == null) return a == b;
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] != b[i]) return false;
                }
                return true;
            }

            private static bool SameRoles(Dictionary<Renderer, HairToolRole> a, Dictionary<Renderer, HairToolRole> b)
            {
                if (a == null || b == null) return a == b;
                if (a.Count != b.Count) return false;
                foreach (var pair in a)
                {
                    if (!b.TryGetValue(pair.Key, out var role) || role != pair.Value) return false;
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
                    // 髪ツールの対象・統合の切り替えで、本体と髪用の担当が入れ替わる
                    var roles = HairToolTargets.ObserveRoles(context, avatar);

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

                    var users = CollectRenderers(candidates, textures);
                    var renderers = HairToolTargets.FilterScope(users, roles, _scope);
                    if (renderers.Count == 0) continue;

                    var data = new GroupData { avatar = avatar, components = components, users = users.ToArray(), roles = roles };
                    groups.Add(RenderGroup.For(renderers).WithData(data, GroupData.Same));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{LogPrefix}プレビューの対象収集に失敗しました（{(avatar != null ? avatar.name : "?")}）: {ex}");
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
            // 結果 RT を描き直す単位（元テクスチャのハンドルは Entry へ渡すまでここが持つ）
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
                foreach (var (original, proxy) in proxyPairs)
                {
                    if (original == null || proxy == null) continue;
                    pairs.Add((original, proxy));
                }
                var originals = new List<Renderer>();
                if (data?.users != null)
                {
                    foreach (var renderer in data.users)
                    {
                        if (renderer != null) originals.Add(renderer);
                    }
                }
                foreach (var renderer in originals)
                {
                    var materials = context.Observe(renderer, r => r.sharedMaterials, SameMaterialArray);
                    if (materials == null) continue;
                    // 元マテリアルの編集（色・Tiling/Offset 等）に追従させる（複製を作り直す）
                    foreach (var material in materials)
                    {
                        if (material != null) context.Observe(material);
                    }
                }

                // 髪用: 元の Renderer のスロット番号から、髪ツールが入れたマテリアルと土台を決める
                var hairSlots = _scope == RecolorScope.ChmHair ? PlanHairSlots(pairs, plans) : null;
                var units = hairSlots != null ? UnitsOf(hairSlots, order) : UnitsOf(order, pairs);

                // 連結の統計を共有する編集（全テクスチャぶん）。結果 RT の鍵に、同じ連結の他のメンバーの中身も入れる
                var groupMembers = CollectGroupStatsMembers(plans, order);

                // 1 段目: 単位ごとに結果 RT のキャッシュを引き、無いものは PrepareJob まで済ませる
                // （連結の統計は全単位の Job がそろってから共有する）
                var usersOf = new Dictionary<Texture2D, List<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)>>();
                var entryOf = new Dictionary<TextureUnit, PreviewTextureCache.Entry>();
                foreach (var unit in units)
                {
                    var texture = unit.source;
                    var plan = plans[texture];
                    if (!usersOf.TryGetValue(texture, out var users))
                    {
                        // 再インポート（画素の変更）でも作り直す
                        context.Observe(texture, t => t.imageContentsHash);
                        // 「画像を入れる」の画像も、描き直して再インポートしたら作り直す
                        // （HashEdit に畳むだけでは、コンポーネントが変わったときにしか観測されない）
                        foreach (var edit in plan.edits)
                        {
                            if (edit != null && edit.HasDecal) context.Observe(edit.decalTexture, t => t.imageContentsHash);
                        }
                        users = CollectUsers(originals, texture);
                        usersOf[texture] = users;
                        WarnUnreadableUsers(texture, users);
                    }

                    var hash = TextureUnit.FoldBase(ComputeTextureHash(plan.edits, users, groupMembers, plan.overlay), unit.baseTexture);
                    var key = new PreviewTextureCache.Key(texture, hash, plan.size);
                    var entry = PreviewTextureCache.TryAcquire(key);
                    if (entry != null)
                    {
                        entries.Add(entry);
                        entryOf[unit] = entry;
                        continue;
                    }

                    // 元テクスチャのデコード結果を Entry に持たせる。PrepareJob / Run の中の読み込み（同じ大きさ）が
                    // このハンドルのキャッシュに当たる。スライダー操作でノードを作り直すときは、新ノードの Instantiate の間
                    // 旧ノードの Entry がまだハンドルを持っているので参照が途切れず、PNG を再デコードしない
                    // （旧ノードは新ノードができた後に破棄される）
                    var pending = new PendingTexture { unit = unit, plan = plan, key = key };
                    pendings.Add(pending);
                    pending.source = SourceTextureLoader.Acquire(texture, plan.size);
                    pending.jobs = RecolorPipeline.RebaseStats(
                        PrepareJobs(texture, plan, users, originals), unit.baseTexture, texture, plan.size,
                        MaskContext.For(plan.root, originals));
                }

                // 描き直す単位の連結の統計を共有する。キャッシュに当たった単位のメンバーも、統計を取るためだけに PrepareJob する
                // （その単位は Run しない。マスクは MaskCache に当たることが多い）
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
                    foreach (var unit in units)
                    {
                        if (!entryOf.ContainsKey(unit)) continue;
                        var texture = unit.source;
                        var plan = plans[texture];
                        var maskContext = MaskContext.For(plan.root, originals);
                        foreach (var edit in plan.edits)
                        {
                            if (!RecolorPipeline.SharesGroupStats(edit) || !sharedGroups.Contains(edit.groupId)) continue;
                            if (edit.seedRenderer == null && RecolorPipeline.NeedsSeedRenderer(edit)) continue;
                            var job = RecolorPipeline.PrepareJob(edit, texture, plan.size, usersOf[texture], context: maskContext);
                            if (job == null) continue;
                            allJobs.AddRange(RecolorPipeline.RebaseStats(new[] { job }, unit.baseTexture, texture, plan.size, maskContext));
                        }
                    }
                }
                RecolorPipeline.ShareGroupStats(allJobs);

                // 2 段目: 描き直す単位ごとに Run
                foreach (var pending in pendings)
                {
                    var rt = Render(pending.unit, pending.plan, pending.jobs, originals);
                    if (rt == null) continue;
                    var entry = PreviewTextureCache.Add(pending.key, rt, pending.source);
                    pending.source = null; // 所有権を Entry へ渡した（Entry の破棄で Dispose）
                    entries.Add(entry);
                    entryOf[pending.unit] = entry;
                }

                // 単位ごとの結果 RT
                var results = new Dictionary<TextureUnit, RenderTexture>();
                foreach (var unit in units)
                {
                    if (!entryOf.TryGetValue(unit, out var entry)) continue;
                    results[unit] = entry.texture;

                    // 現在の編集がこのテクスチャに効いていれば、結果 RT にハイライトを掛けた別の RT を束縛する
                    var highlighted = CreateHighlighted(highlight, unit.source, plans[unit.source], usersOf[unit.source], entry.texture, originals);
                    if (highlighted != null)
                    {
                        owned.Add(highlighted);
                        results[unit] = highlighted;
                    }
                }

                // マテリアルの差し替え配列（Renderer ごと。OnFrame でアロケーションしないよう先に作る）
                var overrides = hairSlots != null
                    ? ReplaceHairSlots(pairs, hairSlots, results, clones)
                    : ReplaceMainSlots(pairs, results, clones);
                if (overrides.Count == 0) return Task.FromResult<IRenderFilterNode>(new EmptyNode());

                handedOver = true;
                return Task.FromResult<IRenderFilterNode>(new Node(overrides, clones, entries, owned));
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix}プレビューの生成に失敗しました: {ex}");
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
                // 全単位の Run とハイライトでマスクを使い終えたので、ここで 1 回だけ減らす
                // （Run の途中で減らすと、連結の統計のために先に準備した他のテクスチャのマスクを捨ててしまう）。
                // あふれて捨てた編集のマスクは、次にその編集を描くときに PrepareJob が作り直す
                MaskCache.Trim();
            }
        }

        // ── 差し替え ──

        /// <summary>
        /// 本体: 計画のテクスチャのうち、担当する Renderer（pairs）がメインに使っているものごとに 1 単位（土台なし）。
        /// 髪用の入口だけが使うテクスチャの結果は作らない（範囲の利用者には入るので計画には残る）
        /// </summary>
        private static List<TextureUnit> UnitsOf(List<Texture2D> order, List<(Renderer original, Renderer proxy)> pairs)
        {
            var used = new HashSet<Texture2D>();
            foreach (var (_, proxy) in pairs)
            {
                foreach (var material in proxy.sharedMaterials)
                {
                    if (MaterialTextureResolver.TryGetMainTexture(material, out var info)) used.Add(info.texture);
                }
            }
            var units = new List<TextureUnit>(order.Count);
            foreach (var texture in order)
            {
                if (used.Contains(texture)) units.Add(new TextureUnit(texture, null));
            }
            return units;
        }

        /// <summary>髪用: 差し替えるスロットに現れる (元テクスチャ, 土台) の組（計画のテクスチャ順）</summary>
        private static List<TextureUnit> UnitsOf(Dictionary<Renderer, List<HairSlot>> hairSlots, List<Texture2D> order)
        {
            var units = new List<TextureUnit>();
            var seen = new HashSet<TextureUnit>();
            foreach (var texture in order)
            {
                foreach (var slots in hairSlots.Values)
                {
                    foreach (var slot in slots)
                    {
                        if (slot.source != texture) continue;
                        var unit = new TextureUnit(slot.source, slot.baseTexture);
                        if (seen.Add(unit)) units.Add(unit);
                    }
                }
            }
            return units;
        }

        /// <summary>髪用: 元の Renderer のマテリアルと、上流（髪ツール）が差し替えた後の proxy のマテリアルを番号で対応させる</summary>
        private static Dictionary<Renderer, List<HairSlot>> PlanHairSlots(
            List<(Renderer original, Renderer proxy)> pairs, Dictionary<Texture2D, TexturePlan> plans)
        {
            var result = new Dictionary<Renderer, List<HairSlot>>();
            var sources = new HashSet<Texture2D>(plans.Keys);
            foreach (var (original, proxy) in pairs)
            {
                var slots = ChmHairSlots.Plan(original.sharedMaterials, proxy.sharedMaterials, sources, out bool mismatch);
                if (mismatch)
                {
                    WarnOnceByKey($"hair-slots:{original.GetInstanceID()}",
                        $"「{original.name}」のマテリアルの数が他のツールのプレビューで変わったため、色変えをプレビューに出せません");
                }
                if (slots.Count > 0) result[original] = slots;
            }
            return result;
        }

        /// <summary>本体: proxy のメインが計画のテクスチャ（参照の一致）のスロットを、結果 RT を束縛した複製に差し替える</summary>
        private static Dictionary<Renderer, Material[]> ReplaceMainSlots(
            List<(Renderer original, Renderer proxy)> pairs, Dictionary<TextureUnit, RenderTexture> results, List<Material> clones)
        {
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
                    if (!results.TryGetValue(new TextureUnit(info.texture, null), out var rt) || rt == null) continue;

                    if (!cloneOf.TryGetValue(material, out var clone))
                    {
                        clone = CloneWithResult(material, info.propertyName, info.texture, rt);
                        cloneOf.Add(material, clone);
                        clones.Add(clone);
                    }
                    replaced ??= (Material[])materials.Clone();
                    replaced[i] = clone;
                }
                if (replaced != null) overrides[original] = replaced;
            }
            return overrides;
        }

        /// <summary>髪用: 計画したスロットを、今のマテリアル（髪ツールの複製）に結果 RT を束縛した複製に差し替える</summary>
        private static Dictionary<Renderer, Material[]> ReplaceHairSlots(
            List<(Renderer original, Renderer proxy)> pairs,
            Dictionary<Renderer, List<HairSlot>> hairSlots,
            Dictionary<TextureUnit, RenderTexture> results,
            List<Material> clones)
        {
            var cloneOf = new Dictionary<(Material, TextureUnit), Material>();
            var overrides = new Dictionary<Renderer, Material[]>();
            foreach (var (original, proxy) in pairs)
            {
                if (!hairSlots.TryGetValue(original, out var slots)) continue;
                var materials = proxy.sharedMaterials;
                Material[] replaced = null;
                foreach (var slot in slots)
                {
                    if (slot.slot >= materials.Length || materials[slot.slot] != slot.current) continue;
                    var unit = new TextureUnit(slot.source, slot.baseTexture);
                    if (!results.TryGetValue(unit, out var rt) || rt == null) continue;

                    if (!cloneOf.TryGetValue((slot.current, unit), out var clone))
                    {
                        var replacedTexture = slot.baseTexture != null ? slot.baseTexture : slot.source;
                        clone = CloneWithResult(slot.current, slot.propertyName, replacedTexture, rt);
                        cloneOf.Add((slot.current, unit), clone);
                        clones.Add(clone);
                    }
                    replaced ??= (Material[])materials.Clone();
                    replaced[slot.slot] = clone;
                }
                if (replaced != null) overrides[original] = replaced;
            }
            return overrides;
        }

        /// <summary>material の複製（保存しない）。メインと、同じ replacedTexture を参照する他のプロパティに rt を束縛する</summary>
        private static Material CloneWithResult(Material material, string propertyName, Texture replacedTexture, RenderTexture rt)
        {
            var clone = MaterialCloner.Clone(material, " (ClickRecolor)");
            clone.hideFlags = HideFlags.HideAndDontSave;
            // Tiling/Offset は複製で引き継がれる。結果 RT は Linear（シェーダーは線形値として読む）
            clone.SetTexture(propertyName, rt);
            // 同じテクスチャを参照する他のプロパティも差し替える（ビルド・書き出しと同じ）。
            // Poiyomi のロック済みマテリアルで _MainTex を改名してアニメートにすると、シェーダーは _MainTex_<名前> を読むため
            foreach (var name in clone.GetTexturePropertyNames())
            {
                if (name != propertyName && clone.GetTexture(name) == replacedTexture) clone.SetTexture(name, rt);
            }
            return clone;
        }

        // ── テクスチャごとの計画 ──

        private sealed class TexturePlan
        {
            public int size;
            /// <summary>グラデーションの位置の基準（そのテクスチャに最初に効くコンポーネントの Transform）</summary>
            public Transform root;
            public readonly List<RecolorEdit> edits = new List<RecolorEdit>();
            /// <summary>edits のうち重ね貼りで貼る編集（ハイライト中だけ入る。結果 RT を変えないのでハッシュに入れない）</summary>
            public readonly HashSet<RecolorEdit> overlay = new HashSet<RecolorEdit>();
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
                    if (!IsTexturePreviewTarget(component, edit, highlight, out bool overlay)) continue;
                    if (!plans.TryGetValue(edit.sourceTexture, out var plan))
                    {
                        plan = new TexturePlan { size = (int)component.previewResolution, root = component.transform };
                        plans.Add(edit.sourceTexture, plan);
                        order.Add(edit.sourceTexture);
                    }
                    plan.edits.Add(edit);
                    if (overlay) plan.overlay.Add(edit);
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
        /// プレビューに効く編集か: 有効・対象テクスチャあり・目標色あり（または画像入り）。
        /// 目標色が未設定の編集は見た目を変えないので、そのテクスチャは差し替えない（縮小した作業解像度の画像に置き換えない）。
        /// 画像入り（HasDecal）の編集は画像を貼るだけで見た目が変わるので、目標色が未設定でも対象にする。
        /// ビルド（RecolorPass）も同じ条件を使う（プレビューとビルドで効く編集を揃える）
        /// </summary>
        internal static bool IsPreviewTarget(RecolorEdit edit) =>
            edit != null && edit.enabled && (edit.hasTarget || edit.HasDecal) && edit.sourceTexture != null && !edit.HasMissingDecal;

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

        /// <summary>
        /// その編集の対象テクスチャを作業解像度の結果 RT に差し替えるか: IsPreviewTarget(edit, highlight) のうち、
        /// 重ね貼りで貼る編集（DecalOverlayMaterial.UseOverlay）はハイライト中だけ（範囲のハイライトは元テクスチャに出す）。
        /// 重ね貼りはテクスチャに何もしないので、ハイライト中でなければテクスチャを差し替えない（縮小した複製に置き換えない）。
        /// overlay はその編集が重ね貼りか
        /// </summary>
        private static bool IsTexturePreviewTarget(ClickRecolor component, RecolorEdit edit, in HighlightState highlight, out bool overlay)
        {
            overlay = false;
            if (!IsPreviewTarget(edit, highlight)) return false;
            overlay = Decal.DecalOverlayMaterial.UseOverlay(component, edit);
            return !overlay || (highlight.enabled && highlight.Contains(edit.id));
        }

        /// <summary>プレビューで扱う編集（ハイライト中の色未設定の編集を含む。重ね貼りの編集はハイライト中だけ）の対象テクスチャを textures に足す</summary>
        private static void CollectTargetTextures(ClickRecolor component, HashSet<Texture2D> textures, in HighlightState highlight)
        {
            if (!component.previewEnabled || component.edits == null) return;
            foreach (var edit in component.edits)
            {
                if (!IsTexturePreviewTarget(component, edit, highlight, out _)) continue;
                textures.Add(edit.sourceTexture);
            }
        }

        /// <summary>結果 RT を描き直す単位 1 つぶん（Instantiate の 1 段目で作り、2 段目で Run する）</summary>
        private sealed class PendingTexture
        {
            public TextureUnit unit;
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
                        $"編集「{edit.name}」でクリックしたRendererが見つかりません（削除された可能性があります）。この編集はプレビューに反映されません");
                    continue;
                }
                var job = RecolorPipeline.PrepareJob(edit, texture, plan.size, users, context: context);
                if (job == null)
                {
                    WarnOnce(edit, "mask",
                        $"編集「{edit.name}」の選択範囲を作れませんでした（メッシュのRead/Writeが無効、GPUが使えない等）。この編集はプレビューに反映されません");
                    continue;
                }
                jobs.Add(job);
            }
            return jobs;
        }

        /// <summary>
        /// 1 単位ぶんの結果 RT（ミップ付き・ARGBHalf・Linear）を準備済みの jobs から作る（土台があれば土台から始める）。反映できる編集が 1 つも無ければ null
        /// </summary>
        /// <remarks>
        /// 元テクスチャの読み込みは呼び出し側（Instantiate）が同じ大きさで先に取り、結果の Entry に持たせている
        /// </remarks>
        private static RenderTexture Render(
            TextureUnit unit,
            TexturePlan plan,
            List<EditJob> jobs,
            IReadOnlyList<Renderer> renderers)
        {
            if (jobs == null || jobs.Count == 0) return null;

            // Run の結果は最初からミップ付き（遠目でちらつかない）なので、そのまま束縛する
            var result = RecolorPipeline.Run(new PipelineInput
            {
                sourceAsset = unit.source,
                baseTexture = unit.baseTexture,
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

            // グラデーションの「箱の中だけ」が ON なら、箱の外は色が変わらないので縞も箱の中だけにする（ユーザー要望 2026-10-03）。
            // 判定は縞を描くシェーダーの中で、色変えと同じ式で行う
            SelectionHighlight.BoxClip? clip = null;
            if (edit.gradientEnabled && edit.gradientInsideOnly && plan.root != null && renderers != null)
            {
                var positions = PositionMap.GetOrBuild(
                    plan.root, renderers, texture, result.width, result.height, RecolorPipeline.FillIterations(new[] { job }));
                if (positions != null)
                {
                    clip = new SelectionHighlight.BoxClip
                    {
                        positionMap = positions,
                        rootToBox = Matrix4x4.TRS(edit.gradientBoxPosition, Quaternion.Normalize(edit.gradientBoxRotation), Vector3.one).inverse,
                        boxHeight = edit.gradientBoxSize.y,
                    };
                }
            }
            return SelectionHighlight.CreateHighlighted(result, job.mask, clip);
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

                // 余った枠（最後のサブメッシュの重ね描き）も見る。[_FakeShadow, 髪] の順だと髪は余った枠にある
                for (int i = 0; i < materials.Length; i++)
                {
                    int submesh = MaterialTextureResolver.SubmeshOfSlot(i, mesh.subMeshCount);
                    if (submesh < 0) break;
                    if (!MaterialTextureResolver.TryGetMainTexture(materials[i], out var info)) continue;
                    if (info.texture != texture) continue;
                    var scale = info.scale == Vector2.zero ? Vector2.one : info.scale;
                    var user = (mesh, submesh, scale, info.offset);
                    if (seen.Add(user)) result.Add(user);
                }
            }
            return result;
        }

        // ── ハッシュ ──

        /// <summary>
        /// プレビューに影響するコンポーネントの状態のハッシュ（順序依存）。編集の出力に関わる全フィールドと
        /// previewResolution・previewEnabled を含む（表示専用の name・seedColor は含めない）。
        /// 重ね貼りで表示する編集（DecalOverlayMaterial.UseOverlay）は「形」だけ畳む: パイプラインが素通しし、ハイライトも範囲だけなので、
        /// 色の設定はこのフィルタの結果を変えない。畳むと色のスライダー 1 目盛りごとにこのフィルタと下流の重ね貼りのマテリアルが作り直される
        /// （ユーザー要望 2026-10-06。重ね貼りの色の変更は DecalOverlayPreview が画像の色だけ掛け直す）
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
                foreach (var edit in edits)
                {
                    // 重ね貼りの画像の置き方（箱の位置・ドラッグ状態）もこのフィルタの結果を変えない（つかむ・離すたびに作り直さない。2026-10-06 計測）
                    h = Decal.DecalOverlayMaterial.UseOverlay(component, edit) ? HashEditShape(h, edit) : HashEdit(h, edit);
                }
                return h;
            }
        }

        internal static int HashEdit(int h, RecolorEdit edit) => HashEditLook(HashEditPlacement(HashEditShape(h, edit), edit), edit);

        /// <summary>
        /// 編集の「形」: 範囲・画像・貼り方など、色の設定と画像の置き方（HashEditPlacement）以外。
        /// 重ね貼り（DecalOverlayPreview）では、ここが変わると表示用メッシュを作り直す
        /// </summary>
        internal static int HashEditShape(int h, RecolorEdit edit)
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
                h = h * 31 + (edit.wholeTexture ? 1 : 0);
                h = h * 31 + (int)edit.scope;
                h = h * 31 + edit.threshold.GetHashCode();
                h = h * 31 + edit.feather.GetHashCode();
                h = h * 31 + edit.cleanupRadius;
                h = h * 31 + edit.padding;
                h = h * 31 + (edit.perSeedStats ? 1 : 0);
                // 打ち消し・グラデーションの ON/OFF は画像の下地に位置マップが要るかを決める（DecalImageBuilder.BuildBase）ので形の側
                h = h * 31 + (edit.flattenBase ? 1 : 0);
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
                // 画像を入れる: 画像の中身・箱・比率・貼り方で結果が変わる（OFF なら含めない）
                h = h * 31 + (edit.decalEnabled ? 1 : 0);
                if (edit.decalEnabled)
                {
                    h = h * 31 + (edit.decalTexture != null ? edit.decalTexture.GetInstanceID() : 0);
                    h = h * 31 + (edit.decalTexture != null ? edit.decalTexture.imageContentsHash.GetHashCode() : 0);
                    h = h * 31 + (edit.decalSmooth ? 1 : 0);
                    h = h * 31 + (edit.decalNormal ? 1 : 0);
                }
                return h;
            }
        }

        /// <summary>
        /// 編集の画像の「置き方」: 画像の箱の位置・回転・大きさ・比率を保つ・ドラッグ中か（画像を入れていなければ何も畳まない）。
        /// 重ね貼り（DecalOverlayPreview）では、ここだけ変わったときは表示用メッシュを作り直さず、描く三角形・投影 UV・画像だけ置き直す（2026-10-06 計測）
        /// </summary>
        internal static int HashEditPlacement(int h, RecolorEdit edit)
        {
            unchecked
            {
                if (edit == null || !edit.decalEnabled) return h;
                h = h * 31 + (edit.decalKeepAspect ? 1 : 0);
                // 重ね貼りで表示している編集は、ドラッグ中は箱の値を畳まない（DecalOverlayPreview がメッシュを作り直さず投影 UV だけ書き換えて追従させる。
                // 毎フレーム作り直すと追従しない。実機 2026-10-04）。焼き込みの編集はドラッグ中も箱の値で層を作り直す
                if (!(SceneTool.ToolSession.IsDecalBoxDraggingFor(edit) && SceneTool.ToolSession.DecalBoxDragIsOverlay))
                {
                    h = h * 31 + edit.decalBoxPosition.GetHashCode();
                    h = h * 31 + edit.decalBoxRotation.GetHashCode();
                    h = h * 31 + edit.decalBoxSize.GetHashCode();
                }
                // ドラッグ中は層を低解像度で作るので、離した瞬間にフル解像度で作り直せるようドラッグ状態も含める
                h = h * 31 + (SceneTool.ToolSession.IsDecalBoxDraggingFor(edit) ? 1 : 0);
                return h;
            }
        }

        /// <summary>
        /// 編集の「見た目」: 色・陰影・ガンマ・不透明度・打ち消す強さ・グラデーションの色と箱。
        /// 重ね貼り（DecalOverlayPreview）では、ここだけ変わったときは画像の色の段（DecalImageBuilder.ApplyLook）だけやり直す（ユーザー要望 2026-10-06）
        /// </summary>
        internal static int HashEditLook(int h, RecolorEdit edit)
        {
            unchecked
            {
                if (edit == null) return h;
                h = h * 31 + edit.targetColor.GetHashCode();
                h = h * 31 + (edit.hasTarget ? 1 : 0);
                h = h * 31 + edit.darkEndRatio.GetHashCode();
                h = h * 31 + edit.gamma.GetHashCode();
                h = h * 31 + edit.lightnessToTarget.GetHashCode();
                h = h * 31 + edit.chromaToTarget.GetHashCode();
                h = h * 31 + edit.hueRetain.GetHashCode();
                h = h * 31 + edit.strength.GetHashCode();
                h = h * 31 + edit.shadingStretch.GetHashCode();
                if (edit.flattenBase) h = h * 31 + edit.flattenStrength.GetHashCode();
                // グラデーション OFF のときは終了色・箱を変えても結果が変わらないので含めない（無駄に作り直さない）
                if (edit.gradientEnabled)
                {
                    h = h * 31 + edit.gradientColor.GetHashCode();
                    h = h * 31 + edit.gradientBoxPosition.GetHashCode();
                    h = h * 31 + edit.gradientBoxRotation.GetHashCode();
                    h = h * 31 + edit.gradientBoxSize.GetHashCode();
                    h = h * 31 + (edit.gradientInsideOnly ? 1 : 0);
                    h = h * 31 + edit.gradientDarkEndRatio.GetHashCode();
                    h = h * 31 + edit.gradientGamma.GetHashCode();
                    h = h * 31 + edit.gradientStrength.GetHashCode();
                }
                return h;
            }
        }

        /// <summary>
        /// 1 テクスチャの結果を決めるもののハッシュ: そのテクスチャに効く編集（適用順）と利用者の組
        /// （メッシュ・サブメッシュ・Tiling/Offset。はみ出し防止の被覆と種の Tiling/Offset が変わるため）。
        /// 編集は目標色を決めた（hasTarget）もの、または画像入り（HasDecal）のものだけ含める。色未設定で画像も無い編集はパイプラインが素通しするので結果 RT を変えない
        /// （ハイライト中の色未設定の編集のしきい値をドラッグしても結果 RT を作り直さない。ハイライトのマスクは MaskCache が別の鍵で持つ）。
        /// groupMembers（全テクスチャの、連結の統計を共有する編集。CollectGroupStatsMembers）を渡すと、
        /// 統計を共有する編集ごとに、同じ連結の他のメンバーの中身と対象テクスチャの中身も含める
        /// （連結の統計は他のメンバーの選択範囲にも依存するため）。
        /// overlayEdits（重ね貼りで貼る編集）は含めない: パイプラインが素通しして結果 RT を変えない（ハイライト中に画像の箱を動かしても作り直さない）
        /// </summary>
        internal static int ComputeTextureHash(
            List<RecolorEdit> edits, List<(Mesh mesh, int submesh, Vector2 uvScale, Vector2 uvOffset)> users,
            IReadOnlyList<RecolorEdit> groupMembers = null, ICollection<RecolorEdit> overlayEdits = null)
        {
            unchecked
            {
                int h = 17;
                foreach (var edit in edits)
                {
                    if (edit == null || (!edit.hasTarget && !edit.HasDecal)) continue;
                    if (overlayEdits != null && overlayEdits.Contains(edit)) continue;
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
                $"テクスチャ「{texture.name}」を使うメッシュのうち{unreadable}個はRead/Writeが無効なため、" +
                "はみ出し防止の計算に入っていません（隣のパーツに色がはみ出すことがあります）。メッシュのインポート設定でRead/Writeを有効にしてください");
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
        /// プレビュー・マスク・画像の層・島マスク・被覆・位置マップ・チャート表のキャッシュを全部消す。
        /// destroyInUse = false のとき、表示中のノードが使っている結果 RT はキャッシュから外すだけにして、ノードの破棄で解放する
        /// （他のシーンのアバターのプレビューが消えないように）
        /// </summary>
        internal static void ClearCaches(bool destroyInUse)
        {
            PreviewTextureCache.Clear(destroyInUse);
            MaskCache.ClearCache();
            DecalLayerCache.ClearCache();
            IslandMaskCache.ClearCache();
            CoverageMask.ClearCache();
            PositionMap.ClearCache();
            UvChartDetector.ClearCache();
            UvTwinDetector.ClearCache();
            s_warned.Clear();
            DecalOverlayPreview.ClearWarnings();
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
