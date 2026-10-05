using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.Decal
{
    /// <summary>重ね貼りの組み立てで出る警告の種類（文言と出し方は呼び出し側が決める。プレビューとビルドで言い方が違うため）</summary>
    internal enum OverlayWarning
    {
        /// <summary>メッシュの Read/Write が無効（target はメッシュ）</summary>
        UnreadableMesh,
        /// <summary>マテリアルの数がサブメッシュの数より少ない（target は Renderer）</summary>
        FewerMaterials,
        /// <summary>デカール画像を作れなかった（target は null）</summary>
        ImageFailed,
        /// <summary>重ね貼り用マテリアルを作れなかった（target は Renderer）</summary>
        MaterialFailed,
    }

    /// <summary>
    /// 「画像を入れる」第 2 段（重ね貼り。設計 docs/plans/2026-10-04-tocolo-decal-overlay-design.md）の、Renderer ごとの組み立て。
    /// NDMF プレビュー（DecalOverlayPreview）と有料版のビルド（ProBuild）が同じ結果を作るよう、ComputeContext に依存しない形でここにまとめる。
    /// 編集ごとに AddEdit を呼ぶと、対象の Renderer ごとに重ね貼りサブメッシュを 1 段足した表示用メッシュ（前段に連鎖）と、
    /// 画像の空間のデカール画像 RT（DecalImageBuilder）を作り、マテリアルが作れた Renderer だけ確定する。
    /// マテリアルの作成は呼び出し側に任せる（プレビューはプロキシのマテリアル、ビルドは焼き込み後のマテリアルから作るため）。
    /// 選択マスク（PrepareJob の結果）も呼び出し側が用意する（プレビューとビルドで解像度・文脈が違うため）。
    /// 作ったメッシュ・マテリアル・画像は Detach するまでこのインスタンスの所有で、Dispose で破棄する（判定用のメッシュは常に Dispose で破棄）
    /// </summary>
    internal sealed class DecalOverlayAssembler : IDisposable
    {
        /// <summary>重ね貼りの頂点を法線方向に浮かせる量（対象ルートのローカル単位で 0.1mm。Z ファイティング避け。設計 §3）</summary>
        internal const float OverlayOffset = 0.0001f;

        /// <summary>1 Renderer ぶんの重ね貼り（AddEdit のたびに積み上げる）</summary>
        internal sealed class RendererOverlay
        {
            /// <summary>元の sharedMesh</summary>
            public Mesh sourceMesh;
            /// <summary>今の表示用メッシュ（重ね貼りを足していなければ sourceMesh）</summary>
            public Mesh displayMesh;
            /// <summary>元のマテリアル数（≧ 元のサブメッシュ数）。重ね貼りマテリアルはこの位置から並ぶ</summary>
            public int baseSlotCount;
            public readonly List<Material> materials = new List<Material>();
            /// <summary>materials と同じ並びの、複製元のスロットと画像（マテリアルだけ作り直すため）</summary>
            public readonly List<OverlaySource> sources = new List<OverlaySource>();
            /// <summary>
            /// 画像の箱のドラッグ中に作った（dragging）重ね貼りの、投影 UV と描く三角形を書き換えるための情報。
            /// ドラッグ中でなければ空
            /// </summary>
            public readonly List<OverlaySegment> segments = new List<OverlaySegment>();
        }

        /// <summary>
        /// ドラッグ中の重ね貼り 1 段ぶん: 対象スロットの三角形を向きに関係なくすべて複製してあり（表示用メッシュの start 番からの頂点が、元の sources[k] 番の頂点の複製）、
        /// 箱が動くたびに、今の箱で「表を向き、箱と交わる」三角形だけを submesh 番のサブメッシュに入れ直し、投影 UV を計算し直す（UpdateSegment）。
        /// 判定はふだんの組み立て（DecalOverlayMesh.Build）と同じなので、回転しても見切れない
        /// </summary>
        internal sealed class OverlaySegment
        {
            public RecolorEdit edit;
            public Transform root;
            public int start;
            public int[] sources;
            public Vector3[] judgeVertices;
            public Matrix4x4 judgeLocalToWorld;
            /// <summary>重ね貼りサブメッシュの番号（表示用メッシュの中）</summary>
            public int submesh;
            /// <summary>対象スロットの三角形（元の頂点番号、3 つずつ）</summary>
            public int[] triangles;
            /// <summary>元の頂点番号 → 表示用メッシュの複製頂点の番号（複製していなければ −1）</summary>
            public int[] remap;
            /// <summary>鏡像の Renderer（箱の回転では変わらない）</summary>
            public bool flip;
            /// <summary>箱のローカルの位置の作業用（sources と同じ並び）</summary>
            public Vector3[] boxPositions;
        }

        /// <summary>
        /// 重ね貼りマテリアル 1 枚の作り方: マテリアル配列の firstSlot 番を複製し、image を貼る。
        /// normal は画像の座標へ写し直した法線マップ（「ノーマルも反映」。無ければ null）。
        /// applyNormal は「ノーマルも反映」を効かせるか（ドラッグ中は false）。2nd ノーマルは元の画像のまま引き直すので、写したものが無くても効かせる。
        /// bump2ndMask は画像の座標へ写し直した 2nd ノーマルの強さマスク（元に無ければ null）。DecalOverlayMaterial.Create に渡す
        /// </summary>
        internal readonly struct OverlaySource
        {
            public readonly int firstSlot;
            public readonly RenderTexture image;
            public readonly RenderTexture normal;
            public readonly bool applyNormal;
            public readonly RenderTexture bump2ndMask;

            public OverlaySource(int firstSlot, RenderTexture image, RenderTexture normal = null, bool applyNormal = false,
                RenderTexture bump2ndMask = null)
            {
                this.firstSlot = firstSlot;
                this.image = image;
                this.normal = normal;
                this.applyNormal = applyNormal;
                this.bump2ndMask = bump2ndMask;
            }
        }

        /// <summary>重ね貼りを 1 段足す候補（画像ができてマテリアルが作れたら確定する）</summary>
        private sealed class PendingStep
        {
            public Renderer renderer;
            public int firstSlot;
            public Mesh display;
            /// <summary>画像用に別に作った一時メッシュ（SkinnedMeshRenderer の今のポーズ。MeshRenderer は null＝display を使う）</summary>
            public Mesh partMesh;
            /// <summary>ドラッグ中なら投影 UV を書き換えるための情報（そうでなければ null）</summary>
            public OverlaySegment segment;
            /// <summary>このスロットの元の法線マップと Tiling/Offset（「ノーマルも反映」で写し直す元。無ければ normal が null）</summary>
            public (Texture normal, Vector4 tilingOffset) normalSource;
            /// <summary>このスロットの 2nd ノーマルの強さマスクと Tiling/Offset（写し直す元。無ければ normal が null）</summary>
            public (Texture normal, Vector4 tilingOffset) bump2ndMaskSource;
        }

        private readonly IReadOnlyList<Renderer> _renderers;
        private readonly Func<Renderer, OverlaySource, Material> _createMaterial;
        private readonly Func<Renderer, Material[]> _slotMaterialsOf;
        private readonly Action<OverlayWarning, RecolorEdit, Object> _warn;
        private readonly Dictionary<Renderer, RendererGeometry> _geometries = new Dictionary<Renderer, RendererGeometry>();
        private readonly Dictionary<Renderer, RendererOverlay> _overlays = new Dictionary<Renderer, RendererOverlay>();
        private readonly List<RenderTexture> _images = new List<RenderTexture>();
        private readonly List<ImageLook> _looks = new List<ImageLook>();
        private bool _detached;

        /// <summary>
        /// true なら画像の下地（DecalImageBuilder.BuildBase）を捨てずに ImageLooks に残す（プレビュー用。色・グラデーションだけ変わったとき
        /// ApplyLook だけやり直すため）。ビルドは一度しか作らないので false（既定）
        /// </summary>
        internal bool KeepImageBases { get; set; }

        /// <summary>
        /// 1 編集の画像とその下地（KeepImageBases のときだけ積む。ドラッグ中に作った画像は下地を持たないので積まない）。
        /// image は Images にも入っている同じ RT（破棄は Images 側）。imageBase は Dispose で破棄する
        /// </summary>
        internal sealed class ImageLook
        {
            public readonly string editId;
            public readonly DecalImageBase imageBase;
            public readonly RenderTexture image;
            /// <summary>image に最後に掛けた見た目（RecolorPreview.HashEditLook）。同じなら掛け直さない</summary>
            public int lookHash;

            public ImageLook(string editId, DecalImageBase imageBase, RenderTexture image, int lookHash)
            {
                this.editId = editId;
                this.imageBase = imageBase;
                this.image = image;
                this.lookHash = lookHash;
            }
        }

        /// <summary>
        /// renderers は貼る候補の Renderer（除外リストは AddEdit で編集を持つコンポーネントのもので弾く）。
        /// createMaterial(renderer, source) は重ね貼りマテリアルを作る（失敗なら null。source のスロット・画像・写し直した法線マップ等で DecalOverlayMaterial.Create を呼ぶ）。
        /// slotMaterialsOf はスロットを探すときのマテリアル配列（null なら renderer.sharedMaterials。
        /// ビルドでは焼き込みで差し替える前の配列を渡し、元テクスチャのスロットを見つけられるようにする）。
        /// warn は警告（同じ警告を何度も出さないのは呼び出し側の責任）
        /// </summary>
        internal DecalOverlayAssembler(
            IReadOnlyList<Renderer> renderers,
            Func<Renderer, OverlaySource, Material> createMaterial,
            Action<OverlayWarning, RecolorEdit, Object> warn,
            Func<Renderer, Material[]> slotMaterialsOf = null)
        {
            _renderers = renderers ?? Array.Empty<Renderer>();
            _createMaterial = createMaterial;
            _warn = warn;
            _slotMaterialsOf = slotMaterialsOf;
        }

        /// <summary>Renderer ごとの重ね貼り（マテリアルが 0 枚の Renderer も入りうる。そのときは displayMesh == sourceMesh）</summary>
        internal IReadOnlyDictionary<Renderer, RendererOverlay> Overlays => _overlays;

        /// <summary>
        /// 使われたデカール画像（ストレート α の ARGBHalf・ミップ付き）と、写し直した法線マップ（「ノーマルも反映」）。
        /// どちらも RecolorPipeline.DestroyWorkTexture で破棄する
        /// </summary>
        internal IReadOnlyList<RenderTexture> Images => _images;

        /// <summary>使われた画像の下地（KeepImageBases のときだけ）。Detach したら呼び出し側が imageBase を Dispose する</summary>
        internal IReadOnlyList<ImageLook> ImageLooks => _looks;

        /// <summary>作ったメッシュ・マテリアル・画像（と下地）の所有権を呼び出し側へ渡す（以降の Dispose は判定用のメッシュだけ破棄する）</summary>
        internal void Detach() => _detached = true;

        public void Dispose()
        {
            if (!_detached)
            {
                foreach (var o in _overlays.Values)
                {
                    if (o.displayMesh != null && o.displayMesh != o.sourceMesh) Object.DestroyImmediate(o.displayMesh);
                    foreach (var material in o.materials)
                    {
                        if (material != null) Object.DestroyImmediate(material);
                    }
                }
                foreach (var image in _images) RecolorPipeline.DestroyWorkTexture(image);
                foreach (var look in _looks) look.imageBase.Dispose();
                _overlays.Clear();
                _images.Clear();
                _looks.Clear();
            }
            foreach (var geometry in _geometries.Values) geometry?.Dispose();
            _geometries.Clear();
        }

        /// <summary>
        /// 1 編集ぶん: 対象の Renderer ごとに重ね貼りを 1 段足した表示用メッシュと画像用の区画を作り、
        /// 画像を作って、マテリアルが作れた Renderer だけ確定する。画像は 1 枚でも使われたときだけ Images に積む。
        /// texture は対象テクスチャ、root はグラデーションの位置の基準（除外リストは編集を持つコンポーネントのもの）、mask は texture の UV 空間の選択マスク（EditJob.mask）、
        /// maxSize は画像の長辺の上限。dragging が true（画像の箱のドラッグ中）なら、表示用メッシュは箱との交わりを見ずに表の三角形をすべて複製し、
        /// 投影 UV を書き換えるための情報（RendererOverlay.segments）を残す（ドラッグ中はメッシュを作り直さず UV だけ書き換えて追従させる）
        /// </summary>
        internal void AddEdit(RecolorEdit edit, Texture2D texture, Transform root, RenderTexture mask, int maxSize, bool dragging = false)
        {
            if (edit == null || texture == null || mask == null) return;

            // 除外リストは編集を持つコンポーネントのもの（重ね貼りの判定 DecalOverlayMaterial.CanOverlay と揃える）。
            // 持ち主が見つからなければ基準（対象ルート）のコンポーネントのもの。UnityEngine.Object に ?. は使わない
            var owner = DecalOverlayMaterial.FindOwner(edit, root);
            if (owner == null && root != null) owner = root.GetComponent<ClickRecolor>();
            var excluded = owner != null ? owner.excludedRenderers : null;
            var steps = new List<PendingStep>();
            var parts = new List<OverlayPart>();
            RenderTexture image = null;
            DecalImageBase imageBase = null;
            RenderTexture normal = null;
            RenderTexture bump2ndMask = null;
            bool imageUsed = false;
            bool normalUsed = false;
            bool bump2ndMaskUsed = false;
            bool applyNormal = edit.decalNormal && !dragging;
            // 選択マスクを 1 回だけ読み、選択範囲の外の三角形を重ねないようにする（SelectionFilter）
            var maskBytes = FloodFill.ReadR8(mask);
            try
            {
                foreach (var renderer in _renderers)
                {
                    if (renderer == null) continue;
                    if (excluded != null && excluded.Contains(renderer)) continue;
                    var sourceMesh = RendererMeshAccess.GetSharedMesh(renderer);
                    if (sourceMesh == null) continue;
                    var materials = _slotMaterialsOf != null ? _slotMaterialsOf(renderer) : renderer.sharedMaterials;
                    var slots = CollectOverlaySlots(materials, sourceMesh, texture, out int firstSlot);
                    if (slots.Count == 0) continue;
                    if (!sourceMesh.isReadable)
                    {
                        _warn?.Invoke(OverlayWarning.UnreadableMesh, edit, sourceMesh);
                        continue;
                    }
                    if (materials.Length < sourceMesh.subMeshCount)
                    {
                        _warn?.Invoke(OverlayWarning.FewerMaterials, edit, renderer);
                        continue;
                    }

                    if (!_geometries.TryGetValue(renderer, out var geometry))
                    {
                        geometry = RendererGeometry.Create(renderer);
                        _geometries.Add(renderer, geometry);
                    }
                    if (geometry == null) continue;

                    if (!_overlays.TryGetValue(renderer, out var overlay))
                    {
                        overlay = new RendererOverlay { sourceMesh = sourceMesh, displayMesh = sourceMesh, baseSlotCount = materials.Length };
                        _overlays.Add(renderer, overlay);
                    }

                    MaterialTextureResolver.TryGetMainTexture(materials[firstSlot], out var info);
                    var uvScale = info.scale == Vector2.zero ? Vector2.one : info.scale;
                    // 前段までに足したマテリアルも数に入れる（前段の結果はサブメッシュ数＝マテリアル数なので、複製は 1 段目だけ起きる）
                    int materialCount = overlay.baseSlotCount + overlay.materials.Count;
                    var include = SelectionFilter(maskBytes, mask.width, mask.height, overlay.displayMesh.uv, uvScale, info.offset);
                    if (!BuildRendererOverlay(root, geometry, overlay.displayMesh, edit, slots, uvScale, info.offset, materialCount,
                            out var display, out var part, out var partMesh, dragging, out var segment, include))
                    {
                        continue;
                    }
                    // 「ノーマルも反映」: 元の法線マップを画像の座標へ写し直す（ドラッグ中は面の対応が動くので写さない。離したら写す）
                    (Texture, Vector4) normalSource = default;
                    (Texture, Vector4) bump2ndMaskSource = default;
                    if (applyNormal)
                    {
                        if (DecalOverlayMaterial.TryGetNormalSource(materials[firstSlot], out var normalTexture, out var normalSt))
                        {
                            normalSource = (normalTexture, normalSt);
                        }
                        // 2nd ノーマル本体は元の画像のまま引き直す。メインの UV で引かれる強さマスクだけ写し直す
                        if (DecalOverlayMaterial.TryGetBump2ndSource(materials[firstSlot], out var maskTexture, out var maskSt) && maskTexture != null)
                        {
                            bump2ndMaskSource = (maskTexture, maskSt);
                        }
                    }
                    steps.Add(new PendingStep
                    {
                        renderer = renderer, firstSlot = firstSlot, display = display, partMesh = partMesh, segment = segment,
                        normalSource = normalSource, bump2ndMaskSource = bump2ndMaskSource,
                    });
                    parts.Add(part);
                }
                if (steps.Count == 0) return;

                // ドラッグ中は画像全体を使う（箱を動かす・回すと開始時に面が無かった所も見えるため。選択範囲では切らない＝離したら切る）
                if (dragging) image = BuildFullImage(root, edit, maxSize);
                else if (KeepImageBases) image = BuildWithBase(root, parts, mask, edit, maxSize, out imageBase);
                else image = DecalImageBuilder.Build(root, parts, edit.decalTexture, mask, edit, maxSize);
                if (image == null)
                {
                    _warn?.Invoke(OverlayWarning.ImageFailed, edit, null);
                    return;
                }
                if (applyNormal)
                {
                    var normals = new List<(Texture normal, Vector4 tilingOffset)>(steps.Count);
                    var masks = new List<(Texture normal, Vector4 tilingOffset)>(steps.Count);
                    foreach (var step in steps)
                    {
                        normals.Add(step.normalSource);
                        masks.Add(step.bump2ndMaskSource);
                    }
                    normal = DecalImageBuilder.BuildNormal(parts, normals, image.width, image.height);
                    bump2ndMask = DecalImageBuilder.BuildBump2ndMask(parts, masks, image.width, image.height);
                }

                foreach (var step in steps)
                {
                    var stepNormal = step.normalSource.normal != null ? normal : null;
                    var stepMask = step.bump2ndMaskSource.normal != null ? bump2ndMask : null;
                    var source = new OverlaySource(step.firstSlot, image, stepNormal, applyNormal, stepMask);
                    var material = _createMaterial != null ? _createMaterial(step.renderer, source) : null;
                    if (material == null)
                    {
                        _warn?.Invoke(OverlayWarning.MaterialFailed, edit, step.renderer);
                        continue;
                    }
                    var overlay = _overlays[step.renderer];
                    if (overlay.displayMesh != overlay.sourceMesh) Object.DestroyImmediate(overlay.displayMesh);
                    overlay.displayMesh = step.display;
                    overlay.materials.Add(material);
                    overlay.sources.Add(source);
                    if (stepNormal != null) normalUsed = true;
                    if (stepMask != null) bump2ndMaskUsed = true;
                    if (step.segment != null) overlay.segments.Add(step.segment);
                    step.display = null; // 所有権を overlay へ渡した
                    imageUsed = true;
                }
            }
            finally
            {
                foreach (var step in steps)
                {
                    if (step.partMesh != null) Object.DestroyImmediate(step.partMesh);
                    if (step.display != null) Object.DestroyImmediate(step.display);
                }
                if (image != null)
                {
                    if (imageUsed) _images.Add(image);
                    else RecolorPipeline.DestroyWorkTexture(image);
                }
                if (imageBase != null)
                {
                    if (imageUsed) _looks.Add(new ImageLook(edit.id, imageBase, image, NDMF.RecolorPreview.HashEditLook(17, edit)));
                    else imageBase.Dispose();
                }
                if (normal != null)
                {
                    if (normalUsed) _images.Add(normal);
                    else RecolorPipeline.DestroyWorkTexture(normal);
                }
                if (bump2ndMask != null)
                {
                    if (bump2ndMaskUsed) _images.Add(bump2ndMask);
                    else RecolorPipeline.DestroyWorkTexture(bump2ndMask);
                }
            }
        }

        /// <summary>画像を下地つきで作る（KeepImageBases）。作れなければ null（imageBase も null）</summary>
        private static RenderTexture BuildWithBase(Transform root, List<OverlayPart> parts, RenderTexture mask, RecolorEdit edit, int maxSize,
            out DecalImageBase imageBase)
        {
            imageBase = DecalImageBuilder.BuildBase(root, parts, edit.decalTexture, mask, edit, maxSize);
            if (imageBase == null) return null;
            RenderTexture image = null;
            try
            {
                image = DecalImageBuilder.CreateResult(imageBase);
                if (DecalImageBuilder.ApplyLook(imageBase, edit, image)) return image;
            }
            catch
            {
                RecolorPipeline.DestroyWorkTexture(image);
                imageBase.Dispose();
                imageBase = null;
                throw;
            }
            RecolorPipeline.DestroyWorkTexture(image);
            imageBase.Dispose();
            imageBase = null;
            return null;
        }

        /// <summary>
        /// ドラッグ中の重ね貼り 1 段を、編集の今の箱で計算し直す: 投影 UV を uvs（表示用メッシュの UV0 全体）の該当範囲に書き、
        /// 「表を向き、箱と交わる」三角形（表示用メッシュの頂点番号）を triangles に入れる（ふだんの組み立てと同じ判定）。計算できたら true
        /// </summary>
        internal static bool UpdateSegment(OverlaySegment segment, List<Vector2> uvs, List<int> triangles)
        {
            if (segment?.edit == null || segment.root == null || segment.sources == null || segment.judgeVertices == null
                || segment.triangles == null || segment.remap == null || uvs == null || triangles == null) return false;
            if (segment.start + segment.sources.Length > uvs.Count) return false;
            var edit = segment.edit;
            var judgeToBox = BoxMaskBuilder.RootToBox(edit.decalBoxPosition, edit.decalBoxRotation) * segment.root.worldToLocalMatrix
                             * segment.judgeLocalToWorld;
            var safeSize = DecalLayerBuilder.SafeSize(edit.decalBoxSize);
            var half = new Vector3(Mathf.Abs(safeSize.x), Mathf.Abs(safeSize.y), Mathf.Abs(safeSize.z)) * 0.5f;
            var fit = DecalLayerBuilder.FitScale(edit.decalTexture, edit.decalBoxSize, edit.decalKeepAspect);
            if (segment.boxPositions == null || segment.boxPositions.Length != segment.sources.Length)
            {
                segment.boxPositions = new Vector3[segment.sources.Length];
            }
            var box = segment.boxPositions;
            for (int k = 0; k < segment.sources.Length; k++)
            {
                int s = segment.sources[k];
                box[k] = s >= 0 && s < segment.judgeVertices.Length ? judgeToBox.MultiplyPoint3x4(segment.judgeVertices[s]) : Vector3.zero;
                uvs[segment.start + k] = DecalProjection.ProjectUv(box[k], safeSize, fit);
            }

            triangles.Clear();
            var t = segment.triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                int d0 = segment.remap[t[i]], d1 = segment.remap[t[i + 1]], d2 = segment.remap[t[i + 2]];
                if (d0 < 0 || d1 < 0 || d2 < 0) continue;
                var p0 = box[d0 - segment.start];
                var p1 = box[d1 - segment.start];
                var p2 = box[d2 - segment.start];
                if (!DecalProjection.IsFrontFacing(p0, p1, p2, segment.flip)) continue;
                if (!DecalProjection.TriangleTouchesBox(p0, p1, p2, half)) continue;
                triangles.Add(d0);
                triangles.Add(d1);
                triangles.Add(d2);
            }
            return true;
        }

        /// <summary>segment を今の箱で計算し直して mesh に書く（UV0 と重ね貼りサブメッシュの三角形）。UV0 が 2 成分でなければ何もしない</summary>
        internal static bool ApplySegment(Mesh mesh, OverlaySegment segment, List<Vector2> uvs, List<int> triangles)
        {
            if (mesh == null || segment == null) return false;
            if (mesh.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.TexCoord0) != 2) return false;
            mesh.GetUVs(0, uvs);
            if (!UpdateSegment(segment, uvs, triangles)) return false;
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, segment.submesh, false);
            return true;
        }

        /// <summary>
        /// ドラッグ中に使う画像: 選択範囲で切らず、画像全体に色変え・不透明度だけを掛けたもの（画像の空間いっぱいの四角形 1 枚を描く。
        /// 選択マスクは全面 1、グラデーションの位置は全画素同じなので、ドラッグ中のグラデーションは色 1 側で近似される）
        /// </summary>
        private static RenderTexture BuildFullImage(Transform root, RecolorEdit edit, int maxSize)
        {
            var quad = new Mesh { name = "ClickRecolor_DecalFullImage", hideFlags = HideFlags.HideAndDontSave };
            RenderTexture white = null;
            var previous = RenderTexture.active;
            try
            {
                quad.vertices = new Vector3[4];
                quad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) };
                quad.SetUVs(1, new List<Vector2> { new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f) });
                quad.triangles = new[] { 0, 1, 2, 2, 1, 3 };
                white = MaskTextures.Create(4, 4, "ClickRecolor_DecalFullMask");
                Graphics.SetRenderTarget(white);
                GL.Clear(false, true, Color.white);
                RenderTexture.active = previous;
                return DecalImageBuilder.Build(root, new[] { new OverlayPart(quad, 0, Matrix4x4.identity, Vector2.one, Vector2.zero) },
                    edit.decalTexture, white, edit, maxSize);
            }
            finally
            {
                RenderTexture.active = previous;
                if (white != null) MaskTextures.Destroy(white);
                Object.DestroyImmediate(quad);
            }
        }

        /// <summary>
        /// 選択マスク（R8 を読み戻したもの。行 0 が v = 0）で三角形を選ぶ関数: 元の頂点番号 3 つを受け、3 頂点・重心・3 辺の中点の
        /// どこかで選択マスクが 0.5 を超えれば true。uv は元の UV0、uvScale / uvOffset はスロットのマテリアルの Tiling/Offset（選択マスクの座標。繰り返しは折り返す）。
        /// 画像は画像の空間で 1 枚なので、選択範囲で切るのを画像のマスクだけに任せると、箱の正面から見て前後に重なる選択外の面が同じ画素を引いて画像が出てしまう
        /// （実機 2026-10-04「網掛けの外にはみ出る」）。三角形の途中にある境界（色で選んだとき）は、これまでどおり画像のマスクが切る。
        /// マスクや UV が無ければ null（絞らない）
        /// </summary>
        internal static Func<int, int, int, bool> SelectionFilter(byte[] mask, int width, int height, Vector2[] uv, Vector2 uvScale, Vector2 uvOffset)
        {
            if (mask == null || width <= 0 || height <= 0 || mask.Length < width * height || uv == null || uv.Length == 0) return null;
            bool Selected(Vector2 p)
            {
                var q = Vector2.Scale(p, uvScale) + uvOffset;
                float u = q.x - Mathf.Floor(q.x);
                float v = q.y - Mathf.Floor(q.y);
                int x = Mathf.Clamp((int)(u * width), 0, width - 1);
                int y = Mathf.Clamp((int)(v * height), 0, height - 1);
                return mask[y * width + x] > 127;
            }
            return (a, b, c) =>
            {
                if (a < 0 || b < 0 || c < 0 || a >= uv.Length || b >= uv.Length || c >= uv.Length) return true;
                var ua = uv[a];
                var ub = uv[b];
                var uc = uv[c];
                return Selected((ua + ub + uc) / 3f) || Selected(ua) || Selected(ub) || Selected(uc)
                       || Selected((ua + ub) * 0.5f) || Selected((ub + uc) * 0.5f) || Selected((uc + ua) * 0.5f);
            };
        }



        // ── 組み立て（テスト用に静的関数に分けてある）──

        /// <summary>
        /// 判定用のメッシュと行列（Renderer ごとに 1 回だけ取る）。SkinnedMeshRenderer は今のポーズで BakeMesh したもの
        /// （MeshRaycaster.TryGetMeshData。頂点順は元と同じ・スケールは頂点に焼かれ行列はスケール無し）、MeshRenderer は sharedMesh
        /// </summary>
        internal sealed class RendererGeometry : IDisposable
        {
            public Renderer renderer;
            public Mesh judgeMesh;
            public bool ownsJudgeMesh;
            public Vector3[] judgeVertices;
            public Matrix4x4 judgeLocalToWorld;

            /// <summary>判定用のメッシュが取れない・読み取り不可なら null</summary>
            public static RendererGeometry Create(Renderer renderer)
            {
                if (!MeshRaycaster.TryGetMeshData(renderer, out var mesh, out var localToWorld, out bool owns)) return null;
                if (mesh == null || !mesh.isReadable)
                {
                    if (owns && mesh != null) Object.DestroyImmediate(mesh);
                    return null;
                }
                return new RendererGeometry
                {
                    renderer = renderer,
                    judgeMesh = mesh,
                    ownsJudgeMesh = owns,
                    judgeVertices = mesh.vertices,
                    judgeLocalToWorld = localToWorld,
                };
            }

            public void Dispose()
            {
                if (ownsJudgeMesh && judgeMesh != null) Object.DestroyImmediate(judgeMesh);
                judgeMesh = null;
            }
        }

        /// <summary>
        /// materials のうち、メインテクスチャが texture の枠が描くサブメッシュ（三角形のみ。lilToon の付属は除く。重複なし）。
        /// 余った枠（最後のサブメッシュの重ね描き。MaterialTextureResolver.SubmeshOfSlot）も見る。firstSlot は最初のマテリアル枠（無ければ -1）
        /// </summary>
        internal static List<int> CollectOverlaySlots(Material[] materials, Mesh mesh, Texture2D texture, out int firstSlot)
        {
            firstSlot = -1;
            var result = new List<int>();
            if (materials == null || mesh == null || texture == null) return result;
            for (int slot = 0; slot < materials.Length; slot++)
            {
                int i = MaterialTextureResolver.SubmeshOfSlot(slot, mesh.subMeshCount);
                if (i < 0) break;
                var material = materials[slot];
                if (!MaterialTextureResolver.TryGetMainTexture(material, out var info) || info.texture != texture) continue;
                // 付属（FakeShadow・Overlay・FurOnly）は本体の上に足す描画なので、本体に貼れば足りる
                if (DecalOverlayMaterial.IsIgnorableLilToonAuxiliary(material)) continue;
                if (mesh.GetTopology(i) != MeshTopology.Triangles) continue;
                if (firstSlot < 0) firstSlot = slot;
                if (!result.Contains(i)) result.Add(i);
            }
            return result;
        }

        /// <summary>
        /// 浮かせ量 OverlayOffset（対象ルートのローカル単位）を、meshToRoot で写るメッシュのローカル単位に換算する
        /// （行列の 3 列の長さの平均で割る。不均一なスケールでは近似）
        /// </summary>
        internal static float OffsetInLocal(Matrix4x4 meshToRoot)
        {
            float scale = (((Vector3)meshToRoot.GetColumn(0)).magnitude
                           + ((Vector3)meshToRoot.GetColumn(1)).magnitude
                           + ((Vector3)meshToRoot.GetColumn(2)).magnitude) / 3f;
            return scale > 1e-8f ? OverlayOffset / scale : OverlayOffset;
        }

        /// <summary>
        /// 判定用の頂点を count 個に延長する（2 段目以降の重ね貼りは、前段が足した頂点の分だけ元メッシュより頂点が多い）。
        /// 足した頂点は前段の重ね貼りサブメッシュからしか参照されず、判定に使うスロット（元のサブメッシュ）は元の頂点しか指さないので、値は使われない（0 で埋める）
        /// </summary>
        internal static Vector3[] PadJudgeVertices(Vector3[] judge, int count)
        {
            if (judge == null || judge.Length == count) return judge;
            if (judge.Length > count) return null;
            var result = new Vector3[count];
            Array.Copy(judge, result, judge.Length);
            return result;
        }

        /// <summary>
        /// 1 Renderer・1 編集ぶんの重ね貼りを組み立てる。current（元の sharedMesh か、前段の表示用メッシュ）に重ね貼りサブメッシュを 1 つ足した
        /// 表示用メッシュ display（バインドポーズに足すのでスキニング・ブレンドシェイプで追従する）と、画像（DecalImageBuilder）に渡す区画 part を返す。
        /// materialCount は表示する Renderer の今のマテリアル数（前段で足した重ね貼りマテリアルを含む。DecalOverlayMesh.Build が余分なマテリアルの分のサブメッシュを揃える）。
        /// part は今のポーズで描く: SkinnedMeshRenderer は焼いたメッシュを元に別に作った一時メッシュ partMesh（呼び出し側が破棄）、
        /// MeshRenderer は display そのもの（partMesh は null）。重ね貼りサブメッシュが無い（箱にかかる表の三角形が無い）・作れなければ false
        /// </summary>
        internal static bool BuildRendererOverlay(
            Transform root, RendererGeometry geometry, Mesh current, RecolorEdit edit, IReadOnlyList<int> slots,
            Vector2 uvScale, Vector2 uvOffset, int materialCount, out Mesh display, out OverlayPart part, out Mesh partMesh)
        {
            return BuildRendererOverlay(root, geometry, current, edit, slots, uvScale, uvOffset, materialCount,
                out display, out part, out partMesh, false, out _);
        }

        /// <summary>
        /// dragging なら表示用メッシュは表の三角形をすべて複製し（箱との交わりを見ない）、投影 UV を書き換えるための segment を返す。
        /// 画像（part）はドラッグ中でも箱にかかる三角形だけで描く（画像はドラッグを始めたときのものを使い回すので、ふだんと同じ中身にする）
        /// </summary>
        internal static bool BuildRendererOverlay(
            Transform root, RendererGeometry geometry, Mesh current, RecolorEdit edit, IReadOnlyList<int> slots,
            Vector2 uvScale, Vector2 uvOffset, int materialCount, out Mesh display, out OverlayPart part, out Mesh partMesh,
            bool dragging, out OverlaySegment segment, Func<int, int, int, bool> include = null)
        {
            display = null;
            partMesh = null;
            part = default;
            segment = null;
            if (root == null || geometry?.renderer == null || current == null || edit == null || slots == null) return false;

            var renderer = geometry.renderer;
            var worldToBox = BoxMaskBuilder.RootToBox(edit.decalBoxPosition, edit.decalBoxRotation) * root.worldToLocalMatrix;
            var judgeToBox = worldToBox * geometry.judgeLocalToWorld;
            // 鏡像の Renderer はカリングが反転する（DecalLayerBuilder と同じ式。SkinnedMeshRenderer は焼いた行列にスケールが無いので Transform で見る）
            bool flip = worldToBox.determinant * renderer.transform.localToWorldMatrix.determinant < 0f;
            var fit = DecalLayerBuilder.FitScale(edit.decalTexture, edit.decalBoxSize, edit.decalKeepAspect);
            var rootWorldToLocal = root.worldToLocalMatrix;
            // 表示用メッシュの頂点は Renderer のローカル（SkinnedMeshRenderer はバインドポーズ≒Transform のローカル）なので Transform の行列で換算する
            float displayOffset = OffsetInLocal(rootWorldToLocal * renderer.transform.localToWorldMatrix);

            var judge = PadJudgeVertices(geometry.judgeVertices, current.vertexCount);
            if (judge == null) return false;
            var duplicated = dragging ? new List<int>() : null;
            display = DecalOverlayMesh.Build(current, judge, slots, judgeToBox, flip, edit.decalBoxSize, fit, displayOffset, materialCount,
                allTriangles: dragging, duplicatedSources: duplicated, include: include);
            if (display == null) return false;

            if (dragging)
            {
                // ドラッグ中: 対象スロットの三角形をすべて複製してあるので、今の箱で描く三角形を選び直し UV を計算する（以後は毎フレーム同じ処理）
                var triangles = new List<int>();
                foreach (var slot in slots)
                {
                    var slotTriangles = current.GetTriangles(slot);
                    for (int t = 0; t + 2 < slotTriangles.Length; t += 3)
                    {
                        // 選択範囲の外の三角形は複製していない（DecalOverlayMesh.Build の include と同じ条件）
                        if (include != null && !include(slotTriangles[t], slotTriangles[t + 1], slotTriangles[t + 2])) continue;
                        triangles.Add(slotTriangles[t]);
                        triangles.Add(slotTriangles[t + 1]);
                        triangles.Add(slotTriangles[t + 2]);
                    }
                }
                var remap = new int[current.vertexCount];
                for (int i = 0; i < remap.Length; i++) remap[i] = -1;
                for (int k = 0; k < duplicated.Count; k++) remap[duplicated[k]] = current.vertexCount + k;
                segment = new OverlaySegment
                {
                    edit = edit,
                    root = root,
                    start = current.vertexCount,
                    sources = duplicated.ToArray(),
                    // 判定に使う元の頂点（2 段目以降で延長した分は複製元にならない）
                    judgeVertices = geometry.judgeVertices,
                    judgeLocalToWorld = geometry.judgeLocalToWorld,
                    submesh = display.subMeshCount - 1,
                    triangles = triangles.ToArray(),
                    remap = remap,
                    flip = flip,
                };
                ApplySegment(display, segment, new List<Vector2>(display.vertexCount), new List<int>());
                // 画像は画像全体で作る（AddEdit の BuildFullImage）ので区画は要らない
                return true;
            }

            if (renderer is SkinnedMeshRenderer)
            {
                // グラデーションの位置を今のポーズにするため、画像は焼いたメッシュから作った一時メッシュで描く（焼いたメッシュの単位で浮かせる。Renderer に付けないので materialCount は 0）
                float bakedOffset = OffsetInLocal(rootWorldToLocal * geometry.judgeLocalToWorld);
                partMesh = DecalOverlayMesh.Build(geometry.judgeMesh, geometry.judgeVertices, slots, judgeToBox, flip, edit.decalBoxSize, fit, bakedOffset,
                    include: include);
                if (partMesh == null)
                {
                    Object.DestroyImmediate(display);
                    display = null;
                    return false;
                }
                part = new OverlayPart(partMesh, partMesh.subMeshCount - 1, geometry.judgeLocalToWorld, uvScale, uvOffset);
            }
            else
            {
                part = new OverlayPart(display, display.subMeshCount - 1, geometry.judgeLocalToWorld, uvScale, uvOffset);
            }
            return true;
        }
    }
}
