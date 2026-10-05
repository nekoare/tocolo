using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using nadena.dev.ndmf.preview;
using Nekoare.ClickRecolor.Editor.Decal;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    /// <summary>
    /// 「画像を入れる」第 2 段（重ね貼り。設計 docs/plans/2026-10-04-tocolo-decal-overlay-design.md）の NDMF プレビュー。
    /// NDMF プレビューは Renderer を足せないので、プロキシのメッシュを「元の複製＋重ね貼りサブメッシュ（DecalOverlayMesh）」に差し替え、
    /// マテリアル配列の末尾に重ね貼りマテリアル（DecalOverlayMaterial。_MainTex = DecalImageBuilder の画像）を足す。
    /// RecolorPreview の後段に登録するので、マテリアルの複製元は色変え・ハイライト後のプロキシのマテリアルになる。
    /// Renderer ごとの組み立て（スロット・表示用メッシュ・画像・連鎖）は有料版のビルドと共用の DecalOverlayAssembler にあり、ここは観測とノードだけを持つ。
    /// 上流（RecolorPreview）が作り直されただけ（色スライダー等）なら Refresh で表示用メッシュと画像を引き継ぎ、マテリアルだけ作り直す
    /// （顔メッシュのブレンドシェイプ複製を毎回走らせないため）。Refresh の updatedAspects は上流が Create されるたびに Everything になるので使わず、
    /// プロキシのメッシュが元の sharedMesh のままか（上流が本当にメッシュを差し替えていないか）で引き継げるかを決める。
    /// 重ね貼りの編集の色・グラデーションだけ変わったとき（編集の「見た目」RecolorPreview.HashEditLook）も Refresh で引き継ぎ、
    /// 画像の下地（DecalImageBase）から色の段だけ掛け直す。表示用メッシュ・選択マスクの読み戻し・投影は「形」（HashEditShape）が変わったときだけ作り直す
    /// （ユーザー要望 2026-10-06「画像を入れるの色変更・グラデーションが重い」）。
    ///
    /// 既知の制限:
    /// - 姿勢（ボーン・Transform）は観測しない。アニメーションでポーズを変えた後は、別の変更が起きるまで三角形の選び方・投影は古いまま
    ///   （表示用メッシュはバインドポーズに足しているので、スキニング・ブレンドシェイプでの追従はする）
    /// - 上流のフィルタがメッシュを差し替えている Renderer には貼らない（CHM の FakeShadow と同時に使うと、影を足した髪には重ね貼りが出ない）
    /// - マテリアル数がサブメッシュ数より少ない（描かれないサブメッシュがある）Renderer には貼らない。
    ///   多い（余分なマテリアルで最後のサブメッシュを重ね描きする）Renderer は、表示用メッシュで最後のサブメッシュを複製して数を揃えてから貼る
    /// - 1 つの Renderer で対象テクスチャを使うスロットが複数あり Tiling/Offset が違うとき、画像の選択マスクは最初のスロットの Tiling/Offset で引く
    ///   （DecalOverlayMesh.Build はスロットを 1 つのサブメッシュにまとめるため）
    /// - 画像の箱のドラッグ中（ToolSession.IsDecalBoxDraggingFor）は作り直さない: ドラッグを始めたときに 1 回だけ対象スロットの三角形を向きに関係なくすべて複製し、
    ///   以後は OnFrame で、今の箱で「表を向き、箱と交わる」三角形の並びと投影 UV（UV0）だけを書き換えて追従させる（ふだんと同じ判定なので回転しても見切れない。
    ///   実機 2026-10-04「追従性が悪すぎる」「回転で見切れる」）。そのため HashEdit はドラッグ中の重ね貼りの編集では箱の値を畳まない（ToolSession.DecalBoxDragIsOverlay）。
    ///   ドラッグ中の近似: 画像は選択範囲で切らない画像全体（色変え・不透明度は掛ける。グラデーションは色 1 側）。離すと箱の値がハッシュに戻り、正確に作り直す。
    ///   元の UV0 が 2 成分でないメッシュでは書き換えず、離すまで始めた位置のまま
    /// </summary>
    internal class DecalOverlayPreview : IRenderFilter
    {
        private const string LogPrefix = "[Tocolo]";

        /// <summary>警告を出し済みのキー。Instantiate・OnFrame のたびに同じ警告を並べない</summary>
        private static readonly HashSet<string> s_warned = new HashSet<string>();

        /// <summary>
        /// このフィルタが作った表示用メッシュ（重ね貼り追記版）→ 元メッシュ（InstanceID）。
        /// 他のフィルタが「自分のメッシュに書き戻す」ガードで参照できるようにする（CHM の FakeShadowPreview と同じ形）
        /// </summary>
        private static readonly Dictionary<int, int> s_appendedSources = new Dictionary<int, int>();

        /// <summary>current が source から作った重ね貼りの追記版か</summary>
        internal static bool IsOverlayAppendedVariant(Mesh current, Mesh source)
        {
            if (current == null || source == null) return false;
            return s_appendedSources.TryGetValue(current.GetInstanceID(), out var sourceId) && sourceId == source.GetInstanceID();
        }

        /// <summary>警告の記録を消す（RecolorPreview.ClearCaches から呼ぶ）</summary>
        internal static void ClearWarnings()
        {
            s_warned.Clear();
        }

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
            foreach (var avatar in context.GetAvatarRoots())
            {
                try
                {
                    var components = context.GetComponentsInChildren<ClickRecolor>(avatar, true);
                    bool anyCandidate = false;
                    foreach (var component in components)
                    {
                        if (component == null) continue;
                        // 重ね貼りに関わる編集の「形」だけのハッシュ（色の設定ではグループは変わらない。他の編集の色スライダーでも変わらない）
                        context.Observe(component, ComputeOverlayShapeHash);
                        if (HasOverlayCandidate(component)) anyCandidate = true;
                    }
                    // 「なめらかに貼る」の画像入り編集が無ければ、マテリアルを変えても重ね貼りにはならない
                    if (!anyCandidate) continue;

                    // UseOverlay はマテリアルのシェーダー（lilToon か）で変わるので、判定の前に全 Renderer のマテリアルを観測する。
                    // 判定（CanOverlay）は非アクティブの Renderer も見るので、非アクティブも観測する（貼る候補はアクティブだけ）
                    var candidates = new List<Renderer>();
                    foreach (var renderer in context.GetComponentsInChildren<Renderer>(avatar, true))
                    {
                        if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                        bool active = context.ActiveInHierarchy(renderer.gameObject);
                        var materials = context.Observe(renderer, r => r.sharedMaterials, SameMaterialArray);
                        if (materials == null) continue;
                        foreach (var material in materials)
                        {
                            if (material != null) context.Observe(material, ShaderAndMainTexture);
                        }
                        if (active) candidates.Add(renderer);
                    }

                    var textures = new HashSet<Texture2D>();
                    foreach (var component in components)
                    {
                        if (component == null) continue;
                        foreach (var edit in CollectOverlayEdits(component)) textures.Add(edit.sourceTexture);
                    }
                    if (textures.Count == 0) continue;

                    // 除外リストの Renderer もグループに入れる（Instantiate で貼らない）。選択マスクの利用者・文脈を RecolorPreview と揃え、
                    // MaskCache の鍵を共有させるため（除外は貼る対象の判定だけに使う）
                    var renderers = RecolorPreview.CollectRenderers(candidates, textures);
                    if (renderers.Count == 0) continue;

                    var data = new GroupData { avatar = avatar, components = components };
                    groups.Add(RenderGroup.For(renderers).WithData(data, GroupData.Same));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{LogPrefix}重ね貼りプレビューの対象収集に失敗しました（{(avatar != null ? avatar.name : "?")}）: {ex}");
                }
            }
            return groups.ToImmutable();
        }

        /// <summary>重ね貼りになりうる編集（プレビュー対象・画像入り・なめらかに貼る ON）か。シェーダーの判定はしない</summary>
        private static bool IsOverlayCandidate(RecolorEdit edit) =>
            RecolorPreview.IsPreviewTarget(edit) && edit.HasDecal && edit.decalSmooth;

        private static bool HasOverlayCandidate(ClickRecolor component)
        {
            if (!component.previewEnabled || component.edits == null) return false;
            foreach (var edit in component.edits)
            {
                if (IsOverlayCandidate(edit)) return true;
            }
            return false;
        }

        /// <summary>component の、重ね貼りで表示する編集（適用順）</summary>
        private static List<RecolorEdit> CollectOverlayEdits(ClickRecolor component)
        {
            var result = new List<RecolorEdit>();
            if (component == null || !component.previewEnabled || component.edits == null) return result;
            foreach (var edit in component.edits)
            {
                if (!IsOverlayCandidate(edit)) continue;
                if (!DecalOverlayMaterial.UseOverlay(component, edit)) continue;
                result.Add(edit);
            }
            return result;
        }

        /// <summary>
        /// 重ね貼りの表示用メッシュと画像の下地を決めるコンポーネントの状態のハッシュ（「形」）。RecolorPreview.ComputeEditsHash を丸ごと観測すると、
        /// 他の編集の色スライダーでも作り直しになるので、重ね貼りになりうる編集の形（HashEditShape。画像の箱・ドラッグ状態を含む）と、
        /// 解像度・基準の決め方に効く「各編集がどのテクスチャのプレビュー対象か」・除外リストだけを畳む。色の設定は ComputeOverlayLookHash
        /// </summary>
        internal static int ComputeOverlayShapeHash(ClickRecolor component)
        {
            if (component == null) return 0;
            unchecked
            {
                int h = 17;
                h = h * 31 + (int)component.previewResolution;
                h = h * 31 + (component.previewEnabled ? 1 : 0);
                if (component.excludedRenderers != null)
                {
                    h = h * 31 + component.excludedRenderers.Count;
                    foreach (var renderer in component.excludedRenderers) h = h * 31 + (renderer != null ? renderer.GetInstanceID() : 0);
                }
                if (component.edits == null) return h;
                foreach (var edit in component.edits)
                {
                    bool target = RecolorPreview.IsPreviewTarget(edit);
                    h = h * 31 + (target ? edit.sourceTexture.GetInstanceID() : 0);
                    if (target && IsOverlayCandidate(edit)) h = RecolorPreview.HashEditShape(h, edit);
                }
                return h;
            }
        }

        /// <summary>重ね貼りになりうる編集の「見た目」（色・グラデーション等。RecolorPreview.HashEditLook）のハッシュ。変わったら画像の色の段だけ掛け直す</summary>
        internal static int ComputeOverlayLookHash(ClickRecolor component)
        {
            if (component == null || component.edits == null) return 0;
            unchecked
            {
                int h = 17;
                foreach (var edit in component.edits)
                {
                    if (IsOverlayCandidate(edit)) h = RecolorPreview.HashEditLook(h, edit);
                }
                return h;
            }
        }

        // ── ノードの生成 ──

        public Task<IRenderFilterNode> Instantiate(
            RenderGroup group,
            IEnumerable<(Renderer, Renderer)> proxyPairs,
            ComputeContext context)
        {
            DecalOverlayAssembler assembler = null;
            try
            {
                var data = group.GetData<GroupData>();
                if (data?.components == null) return Task.FromResult<IRenderFilterNode>(new EmptyNode());

                var proxyOf = new Dictionary<Renderer, Renderer>();
                var originals = new List<Renderer>();
                foreach (var (original, proxy) in proxyPairs)
                {
                    if (original == null || proxy == null) continue;
                    proxyOf[original] = proxy;
                    originals.Add(original);
                }
                // Refresh で同じ観測を新しい context に張り直して比べるので、観測は 1 か所（ObserveInputs）にまとめる
                int signature = ObserveInputs(context, data, originals, out int lookSignature);

                // テクスチャごとの解像度と基準（RecolorPreview の TexturePlan と同じ: そのテクスチャに最初に効くコンポーネント）。
                // 重ね貼りの編集もまとめて決めるので、色変えの編集と同じ大きさ・同じ文脈の選択マスクになり MaskCache を共有できる
                var sizeOf = new Dictionary<Texture2D, int>();
                var rootOf = new Dictionary<Texture2D, Transform>();
                var edits = new List<RecolorEdit>();
                foreach (var component in data.components)
                {
                    if (component == null || !component.previewEnabled || component.edits == null) continue;
                    foreach (var edit in component.edits)
                    {
                        if (!RecolorPreview.IsPreviewTarget(edit)) continue;
                        if (!sizeOf.ContainsKey(edit.sourceTexture))
                        {
                            sizeOf.Add(edit.sourceTexture, (int)component.previewResolution);
                            rootOf.Add(edit.sourceTexture, component.transform);
                        }
                    }
                    edits.AddRange(CollectOverlayEdits(component));
                }
                if (edits.Count == 0) return Task.FromResult<IRenderFilterNode>(new EmptyNode());

                // 上流（RecolorPreview）が差し替えた後のマテリアルを複製元にする（色変え・ハイライトを引き継ぐ）
                assembler = new DecalOverlayAssembler(
                    originals,
                    (renderer, source) => proxyOf.TryGetValue(renderer, out var proxy) ? CreateOverlayMaterial(proxy, source) : null,
                    WarnAssembler)
                {
                    // 色・グラデーションだけ変わったときに色の段だけ掛け直せるよう、画像の下地を残す
                    KeepImageBases = true,
                };
                bool anyDragging = false;
                foreach (var edit in edits)
                {
                    // ドラッグ中なのは掴んでいる箱の編集（と連結）だけ。他の画像の編集はふだんどおり作る
                    bool dragging = SceneTool.ToolSession.IsDecalBoxDraggingFor(edit);
                    if (dragging) anyDragging = true;
                    int maxSize = dragging ? DecalLayerCache.DragMaxSize : DecalImageBuilder.MaxSize;
                    var texture = edit.sourceTexture;
                    BuildEdit(assembler, edit, texture, sizeOf[texture], rootOf[texture], originals, maxSize, dragging);
                }

                var nodeOverlays = new Dictionary<Renderer, NodeOverlay>();
                var meshes = new List<(Mesh display, Mesh source)>();
                foreach (var pair in assembler.Overlays)
                {
                    var o = pair.Value;
                    if (o.materials.Count == 0 || o.displayMesh == o.sourceMesh) continue;
                    var sources = o.sources.ToArray();
                    nodeOverlays.Add(pair.Key, new NodeOverlay(o.sourceMesh, o.displayMesh, o.baseSlotCount, sources, o.materials.ToArray(),
                        SourceMaterialsOf(proxyOf[pair.Key], sources), new DragUvs(o.displayMesh, o.segments)));
                    meshes.Add((o.displayMesh, o.sourceMesh));
                }
                if (nodeOverlays.Count == 0) return Task.FromResult<IRenderFilterNode>(new EmptyNode());

                // ドラッグ中に作った画像は下地を持たない（画像全体の近似）ので、色だけの変更でも作り直す
                var shared = new SharedResources(meshes, new List<RenderTexture>(assembler.Images),
                    new List<DecalOverlayAssembler.ImageLook>(assembler.ImageLooks), canReapplyLooks: !anyDragging);
                var node = new Node(data, signature, lookSignature, nodeOverlays, shared, RenderAspects.Mesh | RenderAspects.Material);
                assembler.Detach();
                return Task.FromResult<IRenderFilterNode>(node);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix}重ね貼りプレビューの生成に失敗しました: {ex}");
                return Task.FromResult<IRenderFilterNode>(new EmptyNode());
            }
            finally
            {
                // ノードへ渡していなければ（Detach していなければ）作ったメッシュ・マテリアル・画像も破棄される。判定用のメッシュは常に破棄
                assembler?.Dispose();
                // 全編集の画像を作り終えたので、マスクの参照をここで 1 回だけ減らす（既存の流儀）
                MaskCache.Trim();
            }
        }

        /// <summary>
        /// このフィルタの結果を決める入力を context で観測し、その値を畳んだ署名を返す（Instantiate と Refresh で同じ観測を張るため 1 か所にまとめる）:
        /// 返り値は「形」の署名（各コンポーネントの ComputeOverlayShapeHash、重ね貼りになりうる編集の元テクスチャと画像の中身、
        /// 元 Renderer のマテリアル配列と各マテリアルの中身）、lookSignature は「見た目」の署名（ComputeOverlayLookHash）。
        /// Refresh は自分の観測が無効になっても、この 2 つを比べて作り直すか・色の段だけ掛け直すかを決めるので、観測するものは必ず値を署名に畳む
        /// </summary>
        private static int ObserveInputs(ComputeContext context, GroupData data, List<Renderer> originals, out int lookSignature)
        {
            unchecked
            {
                int h = 17;
                int look = 17;
                if (data?.components != null)
                {
                    foreach (var component in data.components)
                    {
                        if (component == null) continue;
                        h = h * 31 + context.Observe(component, ComputeOverlayShapeHash);
                        look = look * 31 + context.Observe(component, ComputeOverlayLookHash);
                        if (!component.previewEnabled || component.edits == null) continue;
                        foreach (var edit in component.edits)
                        {
                            if (!IsOverlayCandidate(edit)) continue;
                            // 元テクスチャを再インポートしたら選択マスクを作り直す
                            h = h * 31 + context.Observe(edit.sourceTexture, t => t.imageContentsHash).GetHashCode();
                            // 画像を描き直して再インポートしたら作り直す（HashEdit に畳むだけではコンポーネントの変更時にしか観測されない）
                            h = h * 31 + context.Observe(edit.decalTexture, t => t.imageContentsHash).GetHashCode();
                        }
                    }
                }
                foreach (var original in originals)
                {
                    var materials = context.Observe(original, r => r.sharedMaterials, SameMaterialArray);
                    if (materials == null) continue;
                    foreach (var material in materials)
                    {
                        // 元マテリアルの編集（シェーダー・Tiling/Offset・lilToon の設定）で作り直す（中身の CRC を署名に畳む）
                        h = h * 31 + (material != null ? material.GetInstanceID() : 0);
                        if (material != null) h = h * 31 + context.Observe(material, m => m.ComputeCRC());
                    }
                }
                lookSignature = look;
                return h;
            }
        }

        /// <summary>
        /// 1 編集ぶん: 選択マスクを取り、組み立て（DecalOverlayAssembler.AddEdit）に渡す。
        /// 表示用メッシュ・画像・マテリアルの確定は組み立て側（マテリアルが作れた Renderer だけ確定する）
        /// </summary>
        private static void BuildEdit(
            DecalOverlayAssembler assembler, RecolorEdit edit, Texture2D texture, int size, Transform root,
            List<Renderer> originals, int maxSize, bool dragging)
        {
            if (edit.seedRenderer == null && RecolorPipeline.NeedsSeedRenderer(edit))
            {
                WarnOnce($"{edit.id}:seed",
                    $"編集「{edit.name}」でクリックしたRendererが見つかりません（削除された可能性があります）。この編集の画像はプレビューに反映されません");
                return;
            }
            var users = RecolorPreview.CollectUsers(originals, texture);
            var job = RecolorPipeline.PrepareJob(edit, texture, size, users, context: MaskContext.For(root, originals));
            if (job?.mask == null)
            {
                WarnOnce($"{edit.id}:mask",
                    $"編集「{edit.name}」の選択範囲を作れませんでした（メッシュのRead/Writeが無効、GPUが使えない等）。この編集の画像はプレビューに反映されません");
                return;
            }
            assembler.AddEdit(edit, texture, root, job.mask, maxSize, dragging);
        }

        /// <summary>組み立ての警告をプレビューの文言で 1 回だけ出す（鍵と文言は組み立てを分ける前と同じ）</summary>
        private static void WarnAssembler(OverlayWarning kind, RecolorEdit edit, Object target)
        {
            switch (kind)
            {
                case OverlayWarning.UnreadableMesh:
                    WarnOnce($"unreadable:{(target != null ? target.GetInstanceID() : 0)}",
                        $"メッシュ「{(target != null ? target.name : "?")}」はRead/Writeが無効なため、画像のなめらかな貼り付けをプレビューに出せません。メッシュのインポート設定でRead/Writeを有効にしてください");
                    break;
                case OverlayWarning.FewerMaterials:
                    WarnOnce($"slots:{(target != null ? target.GetInstanceID() : 0)}",
                        $"「{(target != null ? target.name : "?")}」はマテリアルの数がサブメッシュの数より少ないため、画像のなめらかな貼り付けをプレビューに出せません");
                    break;
                case OverlayWarning.ImageFailed:
                    WarnOnce($"{edit.id}:image",
                        $"編集「{edit.name}」の画像を作れませんでした（GPUが使えない等）。この編集の画像はプレビューに反映されません");
                    break;
                case OverlayWarning.MaterialFailed:
                    WarnOnce($"{edit.id}:material:{(target != null ? target.GetInstanceID() : 0)}",
                        $"「{(target != null ? target.name : "?")}」の重ね貼り用マテリアルを作れませんでした。編集「{edit.name}」の画像はこのRendererに出ません");
                    break;
            }
        }

        /// <summary>プロキシのマテリアル配列の source.firstSlot 番から重ね貼りマテリアルを作る（範囲外・失敗なら null）</summary>
        private static Material CreateOverlayMaterial(Renderer proxy, DecalOverlayAssembler.OverlaySource source)
        {
            var proxyMaterials = proxy.sharedMaterials;
            if (source.firstSlot < 0 || source.firstSlot >= proxyMaterials.Length) return null;
            return DecalOverlayMaterial.Create(proxyMaterials[source.firstSlot], source.image, source.normal, source.applyNormal, source.bump2ndMask);
        }

        /// <summary>重ね貼りマテリアルの複製元（プロキシのマテリアル配列の各 source.firstSlot 番。範囲外は null）</summary>
        private static Material[] SourceMaterialsOf(Renderer proxy, DecalOverlayAssembler.OverlaySource[] sources)
        {
            var proxyMaterials = proxy.sharedMaterials;
            var result = new Material[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                int slot = sources[i].firstSlot;
                result[i] = slot >= 0 && slot < proxyMaterials.Length ? proxyMaterials[slot] : null;
            }
            return result;
        }

        /// <summary>
        /// プロキシのマテリアル配列 current の末尾に overlays を足した配列。OnFrame で毎フレーム呼ばれるので冪等にする:
        /// すでに足してある（長さが baseCount + overlays の数で末尾が一致）なら null（何もしない）。
        /// current の長さが baseCount でない（上流が配列を変えた）ときも、重ね貼りサブメッシュと対応が崩れるので null
        /// </summary>
        internal static Material[] AppendOverlayMaterials(IReadOnlyList<Material> current, int baseCount, Material[] overlays)
        {
            if (current == null || overlays == null || overlays.Length == 0) return null;
            if (current.Count == baseCount + overlays.Length)
            {
                bool applied = true;
                for (int i = 0; i < overlays.Length; i++)
                {
                    if (current[baseCount + i] != overlays[i]) { applied = false; break; }
                }
                if (applied) return null;
            }
            if (current.Count != baseCount) return null;
            var result = new Material[baseCount + overlays.Length];
            for (int i = 0; i < baseCount; i++) result[i] = current[i];
            Array.Copy(overlays, 0, result, baseCount, overlays.Length);
            return result;
        }

        private static (Shader, Texture2D) ShaderAndMainTexture(Material material) =>
            (material.shader, MaterialTextureResolver.TryGetMainTexture(material, out var info) ? info.texture : null);

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

        private static void WarnOnce(string key, string message)
        {
            if (!s_warned.Add(key)) return;
            Debug.LogWarning($"{LogPrefix} {message}");
        }

        // ── ノード ──

        /// <summary>
        /// 表示用メッシュとデカール画像とその下地（参照カウント付き）。Refresh で作った新ノードと旧ノードが共有し、両方が破棄されたときに解放する
        /// （NDMF の Refresh の約束: 共有した資源は新旧どちらのノードも破棄されるまで解放しない。旧ノードは新しいパイプラインが揃うまで描かれ続ける）。
        /// 色の段の掛け直し（ReapplyLooks）は画像をその場で書き換えるので、旧ノードの表示も同時に新しい色になる
        /// </summary>
        private sealed class SharedResources
        {
            private readonly List<(Mesh display, Mesh source)> _meshes;
            private readonly List<RenderTexture> _images;
            private readonly List<DecalOverlayAssembler.ImageLook> _looks;
            private readonly bool _canReapplyLooks;
            private int _refCount = 1;

            public SharedResources(List<(Mesh display, Mesh source)> meshes, List<RenderTexture> images,
                List<DecalOverlayAssembler.ImageLook> looks, bool canReapplyLooks)
            {
                _meshes = meshes;
                _images = images;
                _looks = looks;
                _canReapplyLooks = canReapplyLooks;
                foreach (var (display, source) in meshes) s_appendedSources[display.GetInstanceID()] = source.GetInstanceID();
            }

            /// <summary>
            /// 各画像に今の見た目（色・グラデーション等）を掛け直す（見た目が変わった編集だけ）。changed は 1 枚でも掛け直したか。
            /// ドラッグ中に作った（下地が無い）・編集が見つからない・掛けられないときは false（作り直させる）
            /// </summary>
            public bool ReapplyLooks(GroupData data, out bool changed)
            {
                changed = false;
                if (!_canReapplyLooks) return false;
                foreach (var look in _looks)
                {
                    var edit = FindEdit(data, look.editId);
                    if (edit == null) return false;
                    int hash = RecolorPreview.HashEditLook(17, edit);
                    if (hash == look.lookHash) continue;
                    if (!DecalImageBuilder.ApplyLook(look.imageBase, edit, look.image)) return false;
                    look.lookHash = hash;
                    changed = true;
                }
                return true;
            }

            private static RecolorEdit FindEdit(GroupData data, string id)
            {
                if (data?.components == null || id == null) return null;
                foreach (var component in data.components)
                {
                    var edit = component != null ? component.FindEdit(id) : null;
                    if (edit != null) return edit;
                }
                return null;
            }

            public void Acquire() => _refCount++;

            public void Release()
            {
                if (--_refCount > 0) return;
                foreach (var (display, _) in _meshes)
                {
                    if (display == null) continue;
                    s_appendedSources.Remove(display.GetInstanceID());
                    Object.DestroyImmediate(display);
                }
                _meshes.Clear();
                foreach (var image in _images) RecolorPipeline.DestroyWorkTexture(image);
                _images.Clear();
                foreach (var look in _looks) look.imageBase.Dispose();
                _looks.Clear();
            }
        }

        /// <summary>ノードが持つ 1 Renderer ぶんの重ね貼り</summary>
        private sealed class NodeOverlay
        {
            public readonly Mesh sourceMesh;
            public readonly Mesh displayMesh;
            public readonly int baseSlotCount;
            public readonly DecalOverlayAssembler.OverlaySource[] sources;
            /// <summary>このノードが所有する重ね貼りマテリアル（sources と同じ並び）</summary>
            public readonly Material[] materials;
            /// <summary>
            /// materials の複製元にした、上流のプロキシのマテリアル（sources と同じ並び）。Refresh で上流が同じマテリアルのままなら、
            /// 重ね貼りマテリアルを作り直さずにこのノードを使い続ける
            /// </summary>
            public readonly Material[] sourceMaterials;
            /// <summary>前フレームの入力（プロキシのマテリアル）と結果。同じ入力なら配列を作り直さない</summary>
            public Material[] lastInput;
            public Material[] lastResult;
            /// <summary>ドラッグ中の投影 UV の書き換え（表示用メッシュと同じく Refresh の新旧ノードで共有する）</summary>
            public readonly DragUvs dragUvs;

            public NodeOverlay(Mesh sourceMesh, Mesh displayMesh, int baseSlotCount, DecalOverlayAssembler.OverlaySource[] sources, Material[] materials,
                Material[] sourceMaterials, DragUvs dragUvs)
            {
                this.sourceMesh = sourceMesh;
                this.displayMesh = displayMesh;
                this.baseSlotCount = baseSlotCount;
                this.sources = sources;
                this.materials = materials;
                this.sourceMaterials = sourceMaterials;
                this.dragUvs = dragUvs;
            }

            /// <summary>proxy の複製元のスロットが、materials を作ったときと同じマテリアルか（上流が作り直されていないか）</summary>
            public bool HasSameSources(Renderer proxy)
            {
                var proxyMaterials = proxy.sharedMaterials;
                for (int i = 0; i < sources.Length; i++)
                {
                    int slot = sources[i].firstSlot;
                    var current = slot >= 0 && slot < proxyMaterials.Length ? proxyMaterials[slot] : null;
                    if (current == null || current != sourceMaterials[i]) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// ドラッグ中に作った表示用メッシュの投影 UV と描く三角形を、編集の箱が変わったフレームだけ書き換える（メッシュは作り直さない。
        /// DecalOverlayAssembler.UpdateSegment）。箱の値（位置・回転・大きさ・比率・画像）を段ごとに覚えておき、変わった段だけ計算する
        /// </summary>
        private sealed class DragUvs
        {
            private readonly Mesh _mesh;
            private readonly DecalOverlayAssembler.OverlaySegment[] _segments;
            private readonly int[] _lastState;
            private readonly List<int> _triangles = new List<int>();
            private List<Vector2> _uvs;
            private bool _unsupported;

            public DragUvs(Mesh mesh, IReadOnlyList<DecalOverlayAssembler.OverlaySegment> segments)
            {
                _mesh = mesh;
                _segments = segments != null ? new List<DecalOverlayAssembler.OverlaySegment>(segments).ToArray() : Array.Empty<DecalOverlayAssembler.OverlaySegment>();
                _lastState = new int[_segments.Length];
                // 作った直後はその時の箱で計算済み
                for (int i = 0; i < _segments.Length; i++) _lastState[i] = StateOf(_segments[i].edit);
            }

            public void Update()
            {
                if (_segments.Length == 0 || _unsupported || _mesh == null) return;
                bool uvChanged = false;
                for (int i = 0; i < _segments.Length; i++)
                {
                    int state = StateOf(_segments[i].edit);
                    if (state == _lastState[i]) continue;
                    if (_uvs == null)
                    {
                        // UV0 が 2 成分のメッシュだけ書き換える（成分数が違うと SetUVs で頂点の形式ごと変わってしまう）
                        if (_mesh.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord0) != 2)
                        {
                            _unsupported = true;
                            return;
                        }
                        _uvs = new List<Vector2>(_mesh.vertexCount);
                        _mesh.GetUVs(0, _uvs);
                    }
                    if (DecalOverlayAssembler.UpdateSegment(_segments[i], _uvs, _triangles))
                    {
                        _mesh.SetTriangles(_triangles, _segments[i].submesh, false);
                        uvChanged = true;
                    }
                    _lastState[i] = state;
                }
                if (uvChanged) _mesh.SetUVs(0, _uvs);
            }

            private static int StateOf(RecolorEdit edit)
            {
                if (edit == null) return 0;
                unchecked
                {
                    int h = edit.decalBoxPosition.GetHashCode();
                    h = h * 31 + edit.decalBoxRotation.GetHashCode();
                    h = h * 31 + edit.decalBoxSize.GetHashCode();
                    h = h * 31 + (edit.decalKeepAspect ? 1 : 0);
                    h = h * 31 + (edit.decalTexture != null ? edit.decalTexture.GetInstanceID() : 0);
                    return h;
                }
            }
        }

        private sealed class Node : IRenderFilterNode, IDisposable
        {
            private readonly GroupData _data;
            /// <summary>このノードを作ったときの「形」と「見た目」の署名（ObserveInputs）。Refresh で観測し直した値と比べる</summary>
            private readonly int _signature;
            private int _lookSignature;
            private readonly Dictionary<Renderer, NodeOverlay> _overlays;
            private readonly SharedResources _shared;
            private readonly List<Material> _buffer = new List<Material>();
            private bool _disposed;

            public RenderAspects WhatChanged { get; private set; }

            public Node(GroupData data, int signature, int lookSignature, Dictionary<Renderer, NodeOverlay> overlays,
                SharedResources shared, RenderAspects whatChanged)
            {
                _data = data;
                _signature = signature;
                _lookSignature = lookSignature;
                _overlays = overlays;
                _shared = shared;
                WhatChanged = whatChanged;
            }

            /// <summary>
            /// 上流がマテリアルだけ変えた（RecolorPreview の作り直し等）なら、表示用メッシュと画像を引き継ぎ、重ね貼りマテリアルだけ
            /// 新しいプロキシのマテリアルから作り直した新ノードを返す。自分の観測が無効になっても（色・グラデーションの変更など）すぐには作り直さず、
            /// 観測し直した「形」の署名が同じなら引き継ぎ、「見た目」の署名が違えば画像の色の段だけ掛け直す（SharedResources.ReapplyLooks）。
            /// 上流のマテリアルが作ったときと同じ（上流が作り直されていない）なら、マテリアルも作り直さずこのノード自身を返す。
            /// 上流がメッシュを変えた・形の署名が違う・色の段を掛け直せない・マテリアルが作れないときは null（NDMF が Instantiate からやり直す）。
            /// 新しい context には Instantiate と同じ観測（ObserveInputs）を張り直す（NDMF は新ノードの無効化をこの context で見る）。
            /// 旧ノードは新しいパイプラインに切り替わるまで描かれるので、旧マテリアルはここでは壊さず旧ノードの Dispose で破棄する
            /// </summary>
            public Task<IRenderFilterNode> Refresh(
                IEnumerable<(Renderer, Renderer)> proxyPairs, ComputeContext context, RenderAspects updatedAspects)
            {
                // updatedAspects のビットでは判断しない: 上流の RecolorPreview は Refresh を持たず毎回作り直されるので、
                // NDMF は Everything（Mesh を含む）を立てる。上流が本当にメッシュを差し替えたかは下のループでプロキシのメッシュを見て判断する
                if (_disposed) return Task.FromResult<IRenderFilterNode>(null);
                var created = new List<Material>();
                try
                {
                    var pairs = new List<(Renderer original, Renderer proxy)>();
                    var originals = new List<Renderer>();
                    foreach (var (original, proxy) in proxyPairs)
                    {
                        if (original == null || proxy == null) continue;
                        pairs.Add((original, proxy));
                        originals.Add(original);
                    }
                    if (ObserveInputs(context, _data, originals, out int lookSignature) != _signature) return Task.FromResult<IRenderFilterNode>(null);

                    // 上流の OnFrame は反映済み。メッシュが元のままでなければ表示用メッシュの前提が崩れているので作り直させる
                    int matched = 0;
                    bool sameUpstream = true;
                    foreach (var (original, proxy) in pairs)
                    {
                        if (!_overlays.TryGetValue(original, out var o)) continue;
                        if (RendererMeshAccess.GetSharedMesh(proxy) != o.sourceMesh) return Task.FromResult<IRenderFilterNode>(null);
                        if (!o.HasSameSources(proxy)) sameUpstream = false;
                        matched++;
                    }
                    if (matched != _overlays.Count) return Task.FromResult<IRenderFilterNode>(null);

                    // 色・グラデーションだけ変わった: 画像の色の段だけ掛け直す（画像はその場で書き換わるので、旧ノードも新ノードも同じ画像を使う）
                    bool lookChanged = false;
                    if (lookSignature != _lookSignature && !_shared.ReapplyLooks(_data, out lookChanged))
                    {
                        return Task.FromResult<IRenderFilterNode>(null);
                    }

                    // 上流が作り直されていない（重ね貼りの編集の色だけ変えた等。RecolorPreview.ComputeEditsHash はその見た目を畳まない）:
                    // マテリアル（lilToon の透過設定を含む）も作り直さず、このノードを使い続ける（NDMF は同じノードを返すと参照を足して使い回す）
                    if (sameUpstream)
                    {
                        _lookSignature = lookSignature;
                        WhatChanged = lookChanged ? RenderAspects.Texture : 0;
                        return Task.FromResult<IRenderFilterNode>(this);
                    }

                    var overlays = new Dictionary<Renderer, NodeOverlay>();
                    foreach (var (original, proxy) in pairs)
                    {
                        if (!_overlays.TryGetValue(original, out var o)) continue;
                        var materials = new Material[o.sources.Length];
                        for (int i = 0; i < materials.Length; i++)
                        {
                            var material = CreateOverlayMaterial(proxy, o.sources[i]);
                            if (material == null) return Task.FromResult<IRenderFilterNode>(null);
                            created.Add(material);
                            materials[i] = material;
                        }
                        overlays.Add(original, new NodeOverlay(o.sourceMesh, o.displayMesh, o.baseSlotCount, o.sources, materials,
                            SourceMaterialsOf(proxy, o.sources), o.dragUvs));
                    }
                    if (overlays.Count != _overlays.Count) return Task.FromResult<IRenderFilterNode>(null);

                    var aspects = lookChanged ? RenderAspects.Material | RenderAspects.Texture : RenderAspects.Material;
                    var node = new Node(_data, _signature, lookSignature, overlays, _shared, aspects);
                    _shared.Acquire();
                    created.Clear(); // 所有権を新ノードへ渡した
                    return Task.FromResult<IRenderFilterNode>(node);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{LogPrefix}重ね貼りプレビューの更新に失敗しました: {ex}");
                    return Task.FromResult<IRenderFilterNode>(null);
                }
                finally
                {
                    foreach (var material in created)
                    {
                        if (material != null) Object.DestroyImmediate(material);
                    }
                }
            }

            public void OnFrame(Renderer original, Renderer proxy)
            {
                if (_disposed || original == null || proxy == null) return;
                if (!_overlays.TryGetValue(original, out var o)) return;
                try
                {
                    // プロキシは毎フレーム元の状態に戻されるので、毎フレーム差し替える
                    var current = RendererMeshAccess.GetSharedMesh(proxy);
                    if (current != o.displayMesh)
                    {
                        if (current != o.sourceMesh)
                        {
                            // 上流のフィルタがメッシュを変えている。足したマテリアルが別のサブメッシュを描かないよう、マテリアルも足さない
                            WarnOnce($"upstream-mesh:{original.GetInstanceID()}",
                                $"「{original.name}」のメッシュを他のツールのプレビューが差し替えているため、画像のなめらかな貼り付けをプレビューに出せません");
                            return;
                        }
                        RendererMeshAccess.SetSharedMesh(proxy, o.displayMesh);
                    }
                    // ドラッグ中に作った表示用メッシュなら、箱が動いたフレームだけ投影 UV を書き換える
                    o.dragUvs?.Update();

                    proxy.GetSharedMaterials(_buffer);
                    if (o.lastInput != null && o.lastResult != null && SameMaterials(_buffer, o.lastInput))
                    {
                        proxy.sharedMaterials = o.lastResult;
                        return;
                    }
                    var result = AppendOverlayMaterials(_buffer, o.baseSlotCount, o.materials);
                    if (result == null) return;
                    o.lastInput = _buffer.ToArray();
                    o.lastResult = result;
                    proxy.sharedMaterials = result;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"{LogPrefix}重ね貼りプレビューの反映に失敗しました: {ex}");
                }
            }

            private static bool SameMaterials(List<Material> a, Material[] b)
            {
                if (a.Count != b.Length) return false;
                for (int i = 0; i < b.Length; i++)
                {
                    if (a[i] != b[i]) return false;
                }
                return true;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var o in _overlays.Values)
                {
                    foreach (var material in o.materials)
                    {
                        if (material != null) Object.DestroyImmediate(material);
                    }
                }
                _overlays.Clear();
                _shared.Release();
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
}
