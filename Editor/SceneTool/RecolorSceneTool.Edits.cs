using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Localization;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.NDMF;
using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>編集の操作（クリック・Ctrl＋クリック・矩形での作成・選択・種の足し引き・仮の編集の破棄）</summary>
    internal sealed partial class RecolorSceneTool
    {
        /// <summary>
        /// edit の連結に 0.5.0 までの「全体」の印が付いたメンバーがあれば、パーツの集まりに置き換える（印のままだと、足した島が
        /// 見た目に効かない種として溜まり、影響範囲を選び直したときに急に効く）。置き換えたら Undo に記録する
        /// </summary>
        private static void ConvertLegacyWholeTextureInGroup(ClickRecolor component, RecolorEdit edit)
        {
            bool recorded = false;
            foreach (var member in EditGroups.Members(component, edit))
            {
                if (member == null || !member.wholeTexture) continue;
                if (!recorded)
                {
                    Undo.RecordObject(component, "Tocolo: テクスチャ全体をパーツに置き換え");
                    recorded = true;
                }
                RemainingParts.ConvertLegacyWholeTexture(component, member);
            }
            if (recorded) EditorUtility.SetDirty(component);
        }

        /// <summary>
        /// 矩形で集めた島 islands を編集 edit（と、その島の連結のメンバー）の種として足す。島モードの編集だけ（色モードなら一時メッセージで何もしない）。
        /// 「アバター全体」の連結も何もしない（Ctrl＋クリックと同じ一時メッセージ）。
        /// edit と同じテクスチャの島は edit の追加の種に、別のテクスチャの島はそのテクスチャの連結のメンバーの追加の種に足す。
        /// メンバーが無ければ、そのテクスチャで最初の島を主種にしてメンバーを作る（EditGroups.LinkSeedBased）。
        /// 既に含まれる島（主種・追加の種と同じ島。IsSameIsland）は無視する（外さない）。テクスチャの無い島・資産でないテクスチャの島は黙って無視。
        /// Undo 記録は足す島があるときに 1 回
        /// </summary>
        internal static RectSeedResult ApplyRectSeeds(ClickRecolor component, RecolorEdit edit, IReadOnlyList<RectIsland> islands)
        {
            var result = new RectSeedResult();
            if (component == null || edit == null || islands == null) return result;
            if (edit.mode != SelectionMode.Island)
            {
                ToolSession.ShowTransientNotice("Scene:Panel:RectColorModeNotice");
                result.rejected = true;
                return result;
            }
            if (EditGroups.IsWholeAvatarGroup(edit))
            {
                ToolSession.ShowTransientNotice("Scene:Panel:CtrlGroupNotice");
                result.rejected = true;
                return result;
            }
            ConvertLegacyWholeTextureInGroup(component, edit);

            // テクスチャごとに、足す先（既存の編集。無ければ null＝メンバーを作る）と足す種をまとめる（最初に出てきた順）
            var plans = new List<(Texture2D texture, RecolorEdit target, List<RecolorSeed> seeds)>();
            int skippedOtherTexture = 0;
            foreach (var island in islands)
            {
                if (island.renderer == null || island.texture == null) continue;
                // 「マテリアルをまたいで選ぶ」が OFF なら別テクスチャの島は足さない（数だけ知らせる）
                if (!ToolSession.CrossTextureEnabled && island.texture != edit.sourceTexture) { skippedOtherTexture++; continue; }
                int index = plans.FindIndex(p => p.texture == island.texture);
                if (index < 0)
                {
                    RecolorEdit target;
                    if (island.texture == edit.sourceTexture) target = edit;
                    else
                    {
                        if (!TryGetSourceTexture(island.texture, out _)) continue;
                        target = EditGroups.FindMemberForTexture(component, edit, island.texture);
                    }
                    plans.Add((island.texture, target, new List<RecolorSeed>()));
                    index = plans.Count - 1;
                }
                var plan = plans[index];
                if (ContainsIsland(plan.target, plan.seeds, island)) continue;
                plan.seeds.Add(new RecolorSeed
                {
                    renderer = island.renderer,
                    submesh = island.submesh,
                    triangle = island.triangle,
                    // マテリアルの Tiling/Offset 適用後の UV（主種と同じ座標）
                    uv = island.uv,
                });
                result.added++;
            }
            if (result.added == 0) return result;

            Undo.RecordObject(component, "Tocolo: 範囲内の島を追加");
            foreach (var (texture, target, seeds) in plans)
            {
                if (seeds.Count == 0) continue;
                var into = target;
                int from = 0;
                if (into == null)
                {
                    // このテクスチャのメンバーが無い: 最初の島を主種にしてメンバーを作る
                    var head = seeds[0];
                    into = EditGroups.LinkSeedBased(component, edit, texture, head.renderer, head.submesh, head.triangle, head.uv,
                        SampleSwatchColor(texture, head.uv, SwatchWorkingSize(component)));
                    if (into == null)
                    {
                        result.added -= seeds.Count;
                        continue;
                    }
                    result.membersCreated++;
                    from = 1;
                }
                if (into.extraSeeds == null) into.extraSeeds = new List<RecolorSeed>();
                for (int i = from; i < seeds.Count; i++) into.extraSeeds.Add(seeds[i]);
            }
            EditorUtility.SetDirty(component);
            // 連結のメンバーが増えたら、ハイライトする編集の集合を取り直す
            if (result.membersCreated > 0) ToolSession.PublishHighlight();
            if (skippedOtherTexture > 0) ToolSession.ShowTransientNotice("Scene:Panel:CrossTextureOffRect", skippedOtherTexture);
            return result;
        }

        /// <summary>island が edit（null なら無し）の主種・追加の種、またはこれから足す種 pending のどれかと同じ島か</summary>
        private static bool ContainsIsland(RecolorEdit edit, List<RecolorSeed> pending, in RectIsland island)
        {
            if (edit != null)
            {
                if (IsSameIsland(edit.seedRenderer, edit.seedSubmesh, edit.seedTriangle, island.renderer, island.submesh, island.triangle)) return true;
                if (edit.extraSeeds != null)
                {
                    foreach (var seed in edit.extraSeeds)
                    {
                        if (seed != null && IsSameIsland(seed.renderer, seed.submesh, seed.triangle, island.renderer, island.submesh, island.triangle)) return true;
                    }
                }
            }
            foreach (var seed in pending)
            {
                if (IsSameIsland(seed.renderer, seed.submesh, seed.triangle, island.renderer, island.submesh, island.triangle)) return true;
            }
            return false;
        }

        /// <summary>
        /// クリックで対象にする編集を決める。forceNew = false で、色を決めた既存の編集の範囲に hit が入っていれば
        /// その編集を選んで返す（直前の色未設定の編集は DiscardPendingEdit で消し、CurrentEditId をその編集にする。新しい編集は作らない）。
        /// それ以外（forceNew = true・既存に当たらない）は CreateEditFromHit で新しい編集を作って返す
        /// </summary>
        internal static RecolorEdit ResolveEdit(ClickRecolor component, in PickHit hit, Color? seedColor, bool forceNew)
        {
            if (component == null) return null;
            var existing = forceNew ? null : FindEditContaining(component, hit);
            if (existing == null) return CreateEditFromHit(component, hit, seedColor);

            // 同じ編集の再クリックなら何もしない（色未設定の編集は選ばれないので、消すべき保留中の編集も無い）
            SelectEdit(existing.id);
            return existing;
        }

        /// <summary>
        /// Ctrl＋クリック: 現在の編集 edit に hit の島（種）を足すか、既に含まれていれば外す。
        /// edit が「アバター全体」の連結なら何もしない（一時メッセージを出す。別のテクスチャなら CtrlOtherTexture、同じなら CtrlGroupNotice）。
        /// hit のテクスチャが edit と違うときは、edit の連結にそのテクスチャのメンバーがあればそのメンバーに対して足す／外す。
        /// 無ければ hit を主種にしてメンバーを作り、島の連結に足す（EditGroups.LinkSeedBased。元の色は seedColor）。
        /// どちらも CurrentEditId は操作したメンバーに移す（パネルは連結全体に効くので、種の表示のため）。
        /// 含まれているかは、島モードなら主種か追加の種と同じ (メッシュ, サブメッシュ, チャート)、
        /// 色モードなら追加の種と同じ (メッシュ, サブメッシュ, チャート) か（色モードの主種は外さない）。
        /// 主種を外すときは追加の種の先頭を主種にし、種が 0 になるなら編集を削除する（色を決めた編集でも削除。CurrentEditId も外す）。
        /// 島の連結のメンバーなら、そのメンバーだけ削除し（連結の残りが 1 件なら単独の編集に戻す）、CurrentEditId を連結の残りに移す。
        /// 変更は Undo 記録つき
        /// </summary>
        internal static SeedToggleResult ResolveSeedToggle(ClickRecolor component, RecolorEdit edit, in PickHit hit, Color? seedColor = null)
        {
            if (component == null || edit == null) return SeedToggleResult.None;
            var texture = hit.hasMainTexture ? hit.mainTexture.texture : null;
            if (texture == null) return SeedToggleResult.None;
            bool wholeAvatar = EditGroups.IsWholeAvatarGroup(edit);
            if (texture != edit.sourceTexture)
            {
                if (wholeAvatar)
                {
                    ToolSession.ShowTransientNotice("Scene:Panel:CtrlOtherTexture");
                    return SeedToggleResult.OtherTexture;
                }
                if (!ToolSession.CrossTextureEnabled)
                {
                    ToolSession.ShowTransientNotice("Scene:Panel:CrossTextureOff");
                    return SeedToggleResult.OtherTexture;
                }
                // 資産でないテクスチャ（実行時に作られたものなど）は編集にできない（警告は TryGetSourceTexture が 1 回だけ出す）
                if (!TryGetSourceTexture(texture, out texture)) return SeedToggleResult.None;

                var member = EditGroups.FindMemberForTexture(component, edit, texture);
                if (member == null)
                {
                    Undo.RecordObject(component, "Tocolo: 別のテクスチャの島を追加");
                    member = EditGroups.LinkSeedBased(component, edit, texture,
                        hit.renderer, hit.subMeshIndex, hit.triangleIndex, hit.uv, seedColor);
                    if (member == null) return SeedToggleResult.None;
                    EditorUtility.SetDirty(component);
                    ToolSession.CurrentEditId = member.id;
                    ToolSession.SetEditHasTwin(member.id, HasUvTwin(hit.renderer, hit.subMeshIndex, hit.triangleIndex));
                    return SeedToggleResult.MemberAdded;
                }
                edit = member;
                if (ToolSession.CurrentEditId != edit.id) ToolSession.CurrentEditId = edit.id;
            }
            else if (wholeAvatar)
            {
                ToolSession.ShowTransientNotice("Scene:Panel:CtrlGroupNotice");
                return SeedToggleResult.Grouped;
            }

            Undo.RecordObject(component, "Tocolo: 種を追加／外す");
            // 0.5.0 までの「全体」の印は種に関係なく全体を選ぶので、そのままだと足し引きしても見た目が変わらない。先にパーツの集まりに置き換える
            RemainingParts.ConvertLegacyWholeTexture(component, edit);
            if (edit.extraSeeds == null) edit.extraSeeds = new List<RecolorSeed>();

            // 島モードは主種の島もクリックで外せる
            if (edit.mode == SelectionMode.Island
                && IsSameIsland(edit.seedRenderer, edit.seedSubmesh, edit.seedTriangle, hit.renderer, hit.subMeshIndex, hit.triangleIndex))
            {
                if (edit.extraSeeds.Count == 0)
                {
                    if (EditGroups.IsGrouped(edit))
                    {
                        // 島の連結のメンバーの最後の種 = そのメンバーだけ削除（連結の残りは残す）
                        var rest = EditGroups.RemoveMember(component, edit);
                        EditorUtility.SetDirty(component);
                        ToolSession.CurrentEditId = rest?.id;
                        return rest != null ? SeedToggleResult.MemberRemoved : SeedToggleResult.EditRemoved;
                    }
                    // 最後の種を外す = 編集を削除
                    EditGroups.RemoveGroup(component, edit);
                    EditorUtility.SetDirty(component);
                    ToolSession.CurrentEditId = null;
                    return SeedToggleResult.EditRemoved;
                }
                var head = edit.extraSeeds[0];
                edit.extraSeeds.RemoveAt(0);
                edit.seedRenderer = head.renderer;
                edit.seedSubmesh = head.submesh;
                edit.seedTriangle = head.triangle;
                edit.seedUv = head.uv;
                EditorUtility.SetDirty(component);
                return SeedToggleResult.Removed;
            }

            for (int i = 0; i < edit.extraSeeds.Count; i++)
            {
                var seed = edit.extraSeeds[i];
                if (seed == null) continue;
                if (!IsSameIsland(seed.renderer, seed.submesh, seed.triangle, hit.renderer, hit.subMeshIndex, hit.triangleIndex)) continue;
                edit.extraSeeds.RemoveAt(i);
                EditorUtility.SetDirty(component);
                return SeedToggleResult.Removed;
            }

            edit.extraSeeds.Add(new RecolorSeed
            {
                renderer = hit.renderer,
                submesh = hit.subMeshIndex,
                triangle = hit.triangleIndex,
                // マテリアルの Tiling/Offset 適用後の UV（主種と同じ座標）
                uv = hit.uv,
            });
            EditorUtility.SetDirty(component);
            return SeedToggleResult.Added;
        }

        /// <summary>
        /// 2 つの (Renderer, サブメッシュ, 三角形) が同じ島か: 同じメッシュ・同じサブメッシュで、三角形の UV チャートが同じ。
        /// メッシュかチャート表が取れなければ、同じ Renderer・サブメッシュ・三角形のときだけ同じとみなす
        /// </summary>
        private static bool IsSameIsland(Renderer a, int submeshA, int triangleA, Renderer b, int submeshB, int triangleB)
        {
            if (a == null || b == null || submeshA != submeshB) return false;
            var meshA = RendererMeshAccess.GetSharedMesh(a);
            var meshB = RendererMeshAccess.GetSharedMesh(b);
            if (meshA == null || meshB == null) return a == b && triangleA == triangleB;
            if (meshA != meshB) return false;
            var table = UvChartDetector.GetOrBuild(meshA, submeshA);
            if (table == null
                || triangleA < 0 || triangleA >= table.chartOfTriangle.Length
                || triangleB < 0 || triangleB >= table.chartOfTriangle.Length)
            {
                return triangleA == triangleB;
            }
            return table.chartOfTriangle[triangleA] == table.chartOfTriangle[triangleB];
        }

        /// <summary>
        /// クリック結果から編集を 1 件作って component に追加し、ToolSession.CurrentEditId をその編集にする。
        /// 直前の編集（CurrentEditId）がまだ色未設定（hasTarget=false）なら、同じ Undo 記録の中で先に削除する
        /// （直前の編集が別のコンポーネントにあっても消す。DiscardPendingEdit）。
        /// 対象テクスチャが無い・資産でないときは何もせず null。
        /// 新しい色の初期値は元の色（hasTarget は false のまま）。既定の白のままだと、白を最初に選んだとき変化として拾えない。
        /// 選択のモード・しきい値・ぼかし・範囲は影響範囲のプリセット（ToolSession.Preset）で決める（ApplyPreset）。
        /// 色モードで範囲が「アバター全体」なら、対象ルート配下の他のメインテクスチャにも同じ設定の編集を作って連結する
        /// （EditGroups.Link。同じ Undo 記録の中。CurrentEditId はクリックしたテクスチャの編集）
        /// </summary>
        internal static RecolorEdit CreateEditFromHit(ClickRecolor component, in PickHit hit, Color? seedColor)
        {
            if (component == null || !TryGetSourceTexture(hit, out var texture)) return null;
            // マテリアルの Tiling/Offset 適用後の UV（島マスクの計算が前提とする座標）
            return CreateEditFromSeed(component, hit.renderer, hit.subMeshIndex, hit.triangleIndex, hit.uv, texture, seedColor);
        }

        /// <summary>
        /// 種の情報（Renderer・サブメッシュ・サブメッシュ内の三角形・Tiling/Offset 適用後の UV・対象テクスチャ）から編集を 1 件作る。
        /// CreateEditFromHit と Ctrl＋ドラッグの矩形選択が共用する。作成の規則は CreateEditFromHit の説明のとおり。
        /// texture が無い・資産でないときは何もせず null
        /// </summary>
        internal static RecolorEdit CreateEditFromSeed(ClickRecolor component, Renderer renderer, int submesh, int triangle, Vector2 uv, Texture2D texture, Color? seedColor)
        {
            if (component == null || !TryGetSourceTexture(texture, out texture)) return null;

            DiscardPendingEdit();
            Undo.RecordObject(component, "Tocolo: 編集を追加");

            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = texture;
            edit.seedRenderer = renderer;
            edit.seedSubmesh = submesh;
            edit.seedTriangle = triangle;
            edit.seedUv = uv;
            edit.seedColor = seedColor ?? Color.gray;
            edit.targetColor = edit.seedColor;
            edit.gradientColor = edit.seedColor; // 終了色の初期値も元の色
            ApplyPreset(edit, ToolSession.Preset);
            edit.padding = 8;
            component.AddEdit(edit);
            // 「箱の中」で作った編集は、クリックしたパーツを囲む箱から始める（AddEdit の後: 箱の既定は編集のマスクから決める）。
            // 「マテリアルをまたいで選ぶ」ON なら箱に触れる他テクスチャのメンバーも作る
            if (edit.mode == SelectionMode.Box)
            {
                SelectionBox.ResetToDefault(component, edit);
                SelectionBox.SyncMembers(component, edit);
            }
            if (edit.mode == SelectionMode.Color && edit.scope == ColorScope.WholeAvatar) EditGroups.Link(component, edit);

            EditorUtility.SetDirty(component);
            ToolSession.CurrentEditId = edit.id;
            // 双子判定は覚えるだけ（パネルの案内は撤去済み。将来の表示や警告に使えるよう残す）
            ToolSession.SetEditHasTwin(edit.id, HasUvTwin(renderer, submesh, triangle));
            return edit;
        }

        /// <summary>
        /// (renderer, submesh, triangle) の三角形のチャートに、同じ UV を使う別のパーツ（3D 位置が違う同じ UV の三角形）があるか。
        /// 編集作成時にパネルの注記（島モードのとき表示）のために 1 回だけ呼ぶ。判定できなければ false
        /// </summary>
        private static bool HasUvTwin(Renderer renderer, int submesh, int triangle)
        {
            var mesh = RendererMeshAccess.GetSharedMesh(renderer);
            var table = UvChartDetector.GetOrBuild(mesh, submesh);
            if (table == null || triangle < 0 || triangle >= table.chartOfTriangle.Length) return false;
            return UvTwinDetector.HasTwin(mesh, submesh, table.chartOfTriangle[triangle], table);
        }

        /// <summary>
        /// hit の位置を選択範囲に含む編集を返す（無ければ null）。対象は目標色を決めた（hasTarget）編集で、
        /// 対象テクスチャが hit のメインテクスチャと同じもの。後ろの編集ほど上に重なって効くので末尾から探す。
        /// 判定は編集の選択マスク（プレビューと同じ作業解像度。MaskCache にあればそれ、無ければ PrepareJob で作る）の
        /// hit.uv の画素が 0.5 を超えるか。マスクが作れない編集は飛ばす。
        /// 画像入り（HasDecal）の編集は、範囲に入っていても画像の上（IsOnDecalImage）でなければ選ばない（画像の外のクリックはふつうの色変えにする）。
        /// 画像の上なら、一覧で後ろにあるふつうの編集より優先する（画像はふつうの色変えより上に見えるため。ユーザー要望 2026-10-04）
        /// </summary>
        internal static RecolorEdit FindEditContaining(ClickRecolor component, in PickHit hit)
        {
            if (component == null || component.edits == null || !hit.hasMainTexture) return null;
            var texture = hit.mainTexture.texture;
            if (texture == null) return null;

            List<(Mesh, int, Vector2, Vector2)> users = null;
            List<Renderer> renderers = null;
            int prepared = 0;
            // 範囲に入ったふつうの編集のうち最も後ろのもの（画像の上の編集が見つからなければこれを返す）
            RecolorEdit plain = null;
            try
            {
                for (int i = component.edits.Count - 1; i >= 0; i--)
                {
                    var edit = component.edits[i];
                    // 無効化した編集は拾わない（選んでも縞が出ず、色を変えても見た目が変わらないため。レビュー指摘 2026-09-25）
                    // 色の指定を取り消した編集（confirmed）も選び直せるようにする
                if (edit == null || !edit.enabled || !edit.IsKept || edit.sourceTexture != texture) continue;
                    // ふつうの編集が見つかった後は、画像入りの編集だけ調べる（マスクを作る手間を省く）
                    if (plain != null && !edit.HasDecal) continue;

                    // 利用者はプレビューと同じ集め方にする（マスクの鍵が揃い、プレビューが作ったマスクに当たる）
                    renderers ??= CollectPreviewRenderers(component.gameObject);
                    users ??= RecolorPreview.CollectUsers(renderers, texture);
                    var job = RecolorPipeline.PrepareJob(edit, texture, (int)component.previewResolution, users,
                        context: MaskContext.For(component, renderers));
                    if (job?.mask == null) continue;
                    prepared++;
                    if (!IsSelectedAt(job.mask, hit.uv)) continue;
                    if (!edit.HasDecal) plain = edit;
                    else if (IsOnDecalImage(component.transform, edit, hit)) return edit;
                }
                return plain;
            }
            finally
            {
                // PrepareJob の約束どおり、使い終わったら 1 回だけ減らす
                if (prepared > 0) MaskCache.Trim();
            }
        }

        /// <summary>画像の上とみなす画像の不透明度の下限（これ未満の透明な所は画像の外としてふつうの色変えにする）</summary>
        private const float DecalImageAlphaThreshold = 0.5f;

        /// <summary>
        /// hit が画像入りの編集 edit の画像の上か: 箱の中で、箱の正面（+Z）を向いた面で、画像の長方形の中。置き方が箱なら、さらに投影した画像の位置の
        /// 不透明度が DecalImageAlphaThreshold 以上（シールは枠の中ならどこでも）。
        /// root は箱の座標の基準（編集を持つコンポーネントの Transform）。画像を CPU で読めなければ（形式など）画像の長方形の中なら画像の上とみなす
        /// </summary>
        internal static bool IsOnDecalImage(Transform root, RecolorEdit edit, in PickHit hit)
        {
            if (root == null || edit == null || !edit.HasDecal) return false;
            // シールは面に沿った展開で割り付けている（重ね貼りも焼き込みも）ので、当たった所の展開の座標で見る（枠の中ならどこでも画像の上）
            if (edit.decalSticker)
            {
                return Decal.DecalMappings.TryImageUvAt(edit, root, hit, out var stickerUv)
                    && stickerUv.x >= 0f && stickerUv.x <= 1f && stickerUv.y >= 0f && stickerUv.y <= 1f;
            }
            var worldToBox = BoxMaskBuilder.RootToBox(edit.decalBoxPosition, edit.decalBoxRotation) * root.worldToLocalMatrix;
            var p = worldToBox.MultiplyPoint3x4(hit.worldPosition);
            var safeSize = DecalLayerBuilder.SafeSize(edit.decalBoxSize);
            var half = new Vector3(Mathf.Abs(safeSize.x), Mathf.Abs(safeSize.y), Mathf.Abs(safeSize.z)) * 0.5f;
            if (Mathf.Abs(p.x) > half.x || Mathf.Abs(p.y) > half.y) return false;
            // 奥行きは、焼き込みでは点ごとに箱で切るので点で見る。重ね貼り（なめらかに貼る）は箱にかかった三角形を丸ごと描き、
            // 奥行きの外にはみ出した所にも画像が見えるので、点ではなく当たった三角形が箱にかかるかで見る（見えている画像をクリックして選べるように）
            if (Mathf.Abs(p.z) > half.z)
            {
                var component = root.GetComponent<ClickRecolor>();
                if (component == null || !Decal.DecalOverlayMaterial.UseOverlayForUi(component, edit) || !HitTriangleTouchesBox(worldToBox, hit, half))
                {
                    return false;
                }
            }
            // 箱の正面を向いた面にだけ貼られる（DecalProjection.IsFrontFacing）。hit の法線はカメラ側を向けてあるので、見えている面の向き
            if (worldToBox.MultiplyVector(hit.worldNormal).z <= 0f) return false;
            var uv = Decal.DecalProjection.ProjectUv(p, safeSize, DecalLayerBuilder.FitScale(edit.decalTexture, edit.decalBoxSize, edit.decalKeepAspect));
            if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) return false;
            // シールは枠（画像の外形）の中ならどこでも画像の上にする（枠で見えている形と判定をそろえる。透明な所に色変えを作るなら Shift＋クリック）
            if (edit.decalSticker) return true;
            // 箱は透明な所を画像の外にする（一面に貼ったロゴのすき間で、下の色を変えられるように）
            var alpha = SampleDecalAlpha(edit.decalTexture, uv);
            return !alpha.HasValue || alpha.Value >= DecalImageAlphaThreshold;
        }

        /// <summary>
        /// 画像の不透明度を読む大きさ（長辺）。見本色と同じ 256 で読むと、細い線や文字が縮小でぼけて、線の上でも透明と判定されていた
        /// </summary>
        private const int DecalHitSampleSize = 1024;
        /// <summary>
        /// 細い線や縁の少し外でも掴めるように太らせて見る幅（長辺を DecalHitSampleSize にしたときの画素）。
        /// 読んだ画像の画素で数えない: 小さい画像は縮めずに元の大きさで読むので、8px の画像なら幅の半分まで太り、透明な所も画像の上になる
        /// </summary>
        private const int DecalHitDilate = 4;
        /// <summary>太らせて見る点の間隔（DecalHitDilate と同じ単位）。DecalHitDilate より粗くすると、点の間に 1px の線が落ちて取りこぼす</summary>
        private const int DecalHitStep = 2;

        /// <summary>画像 image の uv のあたりの不透明度（まわり DecalHitDilate 画素を DecalHitStep 画素おきに見た最大）。CPU で読めなければ null</summary>
        private static float? SampleDecalAlpha(Texture2D image, Vector2 uv)
        {
            if (image == null) return null;
            using (var source = SourceTextureLoader.Acquire(image, DecalHitSampleSize))
            {
                if (source == null || !source.IsValid) return null;
                var readable = source.Texture;
                if (!source.IsDirectRead
                    && (!readable.isReadable || UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsCrunchFormat(readable.format)))
                {
                    return null;
                }
                // 長辺を DecalHitSampleSize にしたときの 1 画素を uv にする（縦横の画素の大きさはそろえる）
                float longSide = Mathf.Max(readable.width, readable.height, 1);
                float stepU = DecalHitStep * longSide / (DecalHitSampleSize * (float)Mathf.Max(readable.width, 1));
                float stepV = DecalHitStep * longSide / (DecalHitSampleSize * (float)Mathf.Max(readable.height, 1));
                const int reach = DecalHitDilate / DecalHitStep;
                float best = 0f;
                for (int dy = -reach; dy <= reach; dy++)
                {
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        // 端で反対側の画素を拾わないよう 0..1 に丸める（画像の繰り返し設定に関係なく）
                        var at = new Vector2(Mathf.Clamp01(uv.x + dx * stepU), Mathf.Clamp01(uv.y + dy * stepV));
                        best = Mathf.Max(best, readable.GetPixelBilinear(at.x, at.y).a);
                    }
                }
                return best;
            }
        }

        /// <summary>hit の三角形（今のポーズで焼いた頂点）が、worldToBox で写した箱（中心原点・半分の大きさ half）と交わるか（DecalProjection.TriangleTouchesBox）</summary>
        private static bool HitTriangleTouchesBox(Matrix4x4 worldToBox, in PickHit hit, Vector3 half)
        {
            if (hit.renderer == null || !MeshRaycaster.TryGetMeshData(hit.renderer, out var mesh, out var localToWorld, out bool owns) || mesh == null) return false;
            try
            {
                if (!mesh.isReadable || hit.subMeshIndex < 0 || hit.subMeshIndex >= mesh.subMeshCount) return false;
                var indices = mesh.GetTriangles(hit.subMeshIndex);
                int i = hit.triangleIndex * 3;
                if (i < 0 || i + 2 >= indices.Length) return false;
                var vertices = mesh.vertices;
                var toBox = worldToBox * localToWorld;
                return Decal.DecalProjection.TriangleTouchesBox(
                    toBox.MultiplyPoint3x4(vertices[indices[i]]), toBox.MultiplyPoint3x4(vertices[indices[i + 1]]), toBox.MultiplyPoint3x4(vertices[indices[i + 2]]), half);
            }
            finally
            {
                if (owns) Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>root 配下の、プレビューが対象にする Renderer（MeshRenderer / SkinnedMeshRenderer、Hierarchy 上で有効なもの）</summary>
        internal static List<Renderer> CollectPreviewRenderers(GameObject root)
        {
            var result = new List<Renderer>();
            var component = root.GetComponent<ClickRecolor>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                if (!renderer.gameObject.activeInHierarchy) continue;
                result.Add(renderer);
            }
            return result;
        }

        /// <summary>
        /// マスク（R8）の uv の画素が 0.5 を超えるか。
        /// 部分矩形の ReadPixels は DirectX で y の原点が全体読みと逆になる（実機で確認）ので使わず、
        /// CopyTexture で 1 画素を 1×1 の RT に写してから全体を読む（SeedColorSampler と同じ流儀）
        /// </summary>
        private static bool IsSelectedAt(RenderTexture mask, Vector2 uv) => MaskValueAt(mask, uv) > 127;

        /// <summary>マスク（R8）の uv の画素の値（0〜255）。読み方は IsSelectedAt の説明のとおり</summary>
        private static byte MaskValueAt(RenderTexture mask, Vector2 uv)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(uv.x * mask.width), 0, mask.width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(uv.y * mask.height), 0, mask.height - 1);

            if ((SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.Basic) == 0)
            {
                // CopyTexture が使えない環境は全体を読む（遅いが向きは正しい）
                var all = Nekoare.ClickRecolor.Editor.Masks.FloodFill.ReadR8(mask);
                return all[y * mask.width + x];
            }

            var desc = new RenderTextureDescriptor(1, 1, mask.format, 0)
            {
                sRGB = false, useMipMap = false, autoGenerateMips = false, msaaSamples = 1,
            };
            var region = RenderTexture.GetTemporary(desc);
            if (!region.IsCreated()) region.Create();
            var texel = new Texture2D(1, 1, TextureFormat.R8, false, true);
            var previous = RenderTexture.active;
            try
            {
                Graphics.CopyTexture(mask, 0, 0, x, y, 1, 1, region, 0, 0, 0, 0);
                RenderTexture.active = region;
                texel.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                return texel.GetRawTextureData()[0];
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(texel);
                RenderTexture.ReleaseTemporary(region);
            }
        }

        /// <summary>
        /// 影響範囲のプリセットを編集の選択仕様にする。
        /// 同じ色 = 色モード（しきい値 0.08・ぼかし 0.05）、似た色 = 色モード（0.30・0.15）、島 = 島モード。
        /// 色モードの範囲は最後にパネルで選んだ範囲（ToolSession.ColorScopePreference。既定はつながった範囲）。
        /// 「アバター全体」になったときの連結（EditGroups.Link）は呼び出し側で行う
        /// </summary>
        internal static void ApplyPreset(RecolorEdit edit, RangePreset preset)
        {
            // モードを選び直したら「テクスチャの残りを選択」で広げた範囲は戻す（範囲を選び直す意思とみなす）
            edit.wholeTexture = false;
            switch (preset)
            {
                case RangePreset.SameColor:
                    edit.mode = SelectionMode.Color;
                    edit.threshold = 0.08f;
                    edit.feather = 0.05f;
                    edit.scope = ToolSession.ColorScopePreference;
                    break;
                case RangePreset.SimilarColor:
                    edit.mode = SelectionMode.Color;
                    edit.threshold = 0.30f;
                    edit.feather = 0.15f;
                    edit.scope = ToolSession.ColorScopePreference;
                    break;
                case RangePreset.Box:
                    edit.mode = SelectionMode.Box;
                    edit.feather = 0f; // 面のぼかしは既定 0（面でぱきっと切る。ユーザー要望 2026-09-29）
                    break;
                default:
                    edit.mode = SelectionMode.Island;
                    break;
            }
        }

        /// <summary>
        /// id の編集を選ぶ。別の編集に切り替えるときは、色未設定の仮の編集を捨ててから選ぶ（DiscardPendingEdit。同じ編集なら何もしない）。
        /// Scene の再クリック・Inspector の一覧・小窓の編集一覧で同じ動きにする
        /// </summary>
        internal static void SelectEdit(string id)
        {
            if (ToolSession.CurrentEditId == id) return;
            DiscardPendingEdit();
            ToolSession.CurrentEditId = id;
        }

        /// <summary>
        /// 現在の編集（ToolSession.CurrentEditId）がまだ色未設定（hasTarget=false）なら削除し（Undo 記録つき。「アバター全体」の連結なら連結ごと）、
        /// その後 CurrentEditId を外す（色を決めた編集は残し、選択だけ外す）。
        /// 対象ルートを切り替えた後でも消せるよう、編集はシーンの全 ClickRecolor から探す
        /// </summary>
        internal static void DiscardPendingEdit()
        {
            string id = ToolSession.CurrentEditId;
            if (id == null) return;

            foreach (var component in Object.FindObjectsOfType<ClickRecolor>(true))
            {
                if (component == null) continue;
                var edit = component.FindEdit(id);
                if (edit == null) continue;
                // 一度色を決めた編集（confirmed）は、色の指定を取り消していても捨てない
                if (!edit.IsKept)
                {
                    Undo.RecordObject(component, "Tocolo: 色未設定の編集を削除");
                    EditGroups.RemoveGroup(component, edit);
                    EditorUtility.SetDirty(component);
                }
                break;
            }
            ToolSession.CurrentEditId = null;
        }
    }
}
