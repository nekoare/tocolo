using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.Decal
{
    /// <summary>重ね貼りの 1 区画: メッシュ（DecalOverlayMesh の出力）と、その重ね貼りサブメッシュの番号、描くときの行列、元スロットの Tiling/Offset</summary>
    internal readonly struct OverlayPart
    {
        public readonly Mesh mesh;
        public readonly int submesh;
        public readonly Matrix4x4 localToWorld;
        public readonly Vector2 uvScale;
        public readonly Vector2 uvOffset;

        public OverlayPart(Mesh mesh, int submesh, Matrix4x4 localToWorld, Vector2 uvScale, Vector2 uvOffset)
        {
            this.mesh = mesh;
            this.submesh = submesh;
            this.localToWorld = localToWorld;
            this.uvScale = uvScale;
            this.uvOffset = uvOffset;
        }
    }

    /// <summary>
    /// 重ね貼りの画像の下地（DecalImageBuilder.BuildBase）。色の設定を掛ける前の画像（ARGBHalf・ミップなし・ストレート α）と位置マップ（無ければ null）、
    /// 返す RT の作り方（ミップ・補間・異方性）、一度取った明るさの統計。プレビューは色・グラデーションだけ変わったとき、これから ApplyLook だけやり直す
    /// </summary>
    internal sealed class DecalImageBase : IDisposable
    {
        public RenderTexture color;
        public RenderTexture positionMap;
        public bool mips;
        public FilterMode filterMode;
        public int anisoLevel;
        public bool hasStats;
        public Stats stats;

        public void Dispose()
        {
            RecolorPipeline.DestroyWorkTexture(color);
            PositionMap.Destroy(positionMap);
            color = null;
            positionMap = null;
        }
    }

    /// <summary>
    /// 「画像を入れる」第 2 段（重ね貼り。設計 docs/plans/2026-10-04-tocolo-decal-overlay-design.md §2）の画像。
    /// 重ね貼りマテリアルは画像を UV0（投影 UV）で直接引くので、元テクスチャの UV 空間（第 1 段の DecalLayerBuilder）ではなく
    /// 画像の空間に「画像 × 選択マスク」を作り、色の設定と不透明度もここで画像に掛けておく（元テクスチャには何もしない）。
    /// 描き方は DecalImage.shader（Pass 0: 色、Pass 1: 位置）、縁埋めと割り戻しは第 1 段と同じ DecalDilate.compute
    /// </summary>
    internal static class DecalImageBuilder
    {
        /// <summary>画像の RT の長辺の上限（ARGBHalf なので 4096² で 128MB。これより大きい画像は比率を保って縮める）</summary>
        internal const int MaxSize = 4096;

        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int MaskId = Shader.PropertyToID("_Mask");
        private static readonly int RootWorldToLocalId = Shader.PropertyToID("_RootWorldToLocal");
        private static readonly int UvScaleOffsetId = Shader.PropertyToID("_UvScaleOffset");
        private static readonly int EdgeTexelId = Shader.PropertyToID("_EdgeTexel");

        /// <summary>透明な画素へ色を滲ませる回数（bilinear で引いたときに境界へ黒が混ざらない幅。ミップの数段ぶんも考えて少し広め）</summary>
        private const int BleedIterations = 4;

        private const int ColorPass = 0;
        private const int PositionPass = 1;
        private const int NormalPass = 2;
        private const int FillPass = 3;
        private const int Bump2ndMaskPass = 4;

        /// <summary>写し直した法線マップの描かれない所（平らな法線）と、写し直した強さマスクの描かれない所（白＝元の強さのまま）</summary>
        private static readonly Color FlatNormal = new Color(0.5f, 0.5f, 1f, 1f);
        private static readonly Color FullMask = Color.white;

        /// <summary>写し直した法線マップの一辺の上限と下限（元の法線マップの密度で決めるが、大きすぎ・小さすぎないように）</summary>
        internal const int NormalMaxSize = 2048;
        internal const int NormalMinSize = 16;

        /// <summary>
        /// 写した法線マップを元の密度の何倍（縦横それぞれ）で作るか。元の密度ちょうどだと、写すときと lilToon が引くときの 2 回の補間で
        /// 2 画素ほどの細かい模様（布の織り目など）が平らになる（実機 2026-10-04: 73×73 で織り目が消えた）
        /// </summary>
        internal const int NormalOversample = 2;

        private static readonly int SourceNormalId = Shader.PropertyToID("_SourceNormal");
        private static readonly int SourceNormalStId = Shader.PropertyToID("_SourceNormalST");
        private static readonly int FillColorId = Shader.PropertyToID("_FillColor");

        /// <summary>DecalImage.shader と、縁埋め（DecalDilate.compute）・色変え（ColorShift.compute）が使えるか</summary>
        internal static bool IsAvailable
        {
            get
            {
                var shader = ShaderAssets.LoadShader(ShaderAssets.DecalImageGuid);
                return shader != null && shader.isSupported && DecalLayerBuilder.IsDilateAvailable && RecolorPipeline.IsShiftAvailable;
            }
        }

        /// <summary>
        /// 画像の大きさ（長辺 maxSize まで縮める）の RT に、parts の重ね貼りサブメッシュを投影 UV の位置に描き、
        /// 「画像 × 選択マスク」を作る。色が決まっていれば画像だけに色変え（Shift）を掛け、不透明度（グラデーションなら位置で補間）を α に掛ける。
        /// 返すのはストレート α の ARGBHalf RT（呼び出し側所有。RecolorPipeline.DestroyWorkTexture で破棄）。描けなければ null。
        /// selectionMask は元テクスチャの UV 空間の選択マスク（EditJob.mask。大きさは画像と違ってよい。元の UV0 × Tiling/Offset で引く）。
        /// 中身は BuildBase（色に関係ない下地）→ CreateResult → ApplyLook（色の段）で、下地はここで捨てる
        /// </summary>
        internal static RenderTexture Build(Transform root, IReadOnlyList<OverlayPart> parts, Texture2D image, RenderTexture selectionMask,
            RecolorEdit edit, int maxSize = MaxSize)
        {
            using (var imageBase = BuildBase(root, parts, image, selectionMask, edit, maxSize))
            {
                if (imageBase == null) return null;
                var result = CreateResult(imageBase);
                try
                {
                    if (ApplyLook(imageBase, edit, result)) return result;
                }
                catch
                {
                    RecolorPipeline.DestroyWorkTexture(result);
                    throw;
                }
                RecolorPipeline.DestroyWorkTexture(result);
                return null;
            }
        }

        /// <summary>
        /// 画像の下地: 投影して選択マスクで切り、縁埋め・外周の消去・透明な画素への色の滲ませまで済ませた画像（色の設定は掛けない）と、
        /// グラデーション・帯の打ち消しに使う位置マップ。色・グラデーションだけ変わったときは、プレビューがこれから ApplyLook だけやり直す
        /// （表示用メッシュ・選択マスクの読み戻し・投影を作り直さない。ユーザー要望 2026-10-06）。描けなければ null。呼び出し側所有（Dispose）
        /// </summary>
        internal static DecalImageBase BuildBase(Transform root, IReadOnlyList<OverlayPart> parts, Texture2D image, RenderTexture selectionMask,
            RecolorEdit edit, int maxSize = MaxSize)
        {
            if (root == null || parts == null || parts.Count == 0 || image == null || selectionMask == null || edit == null) return null;
            if (image.width <= 0 || image.height <= 0) return null;
            var shader = ShaderAssets.LoadShader(ShaderAssets.DecalImageGuid);
            if (shader == null || !shader.isSupported) return null;
            // 割り戻せないと乗算済みのまま返ってしまい、不透明度も掛けられないので、compute が無ければ作らない
            if (!DecalLayerBuilder.IsDilateAvailable || !RecolorPipeline.IsShiftAvailable) return null;

            RecolorPipeline.ScaleToFit(image.width, image.height, maxSize, out int width, out int height);
            // 作業用はミップなし。統計（SelectionStats/BandStats）は内部で Blit 縮小（サンプラー経由）するので、
            // ミップ付きだと未生成のミップを読みうる。返す RT（CreateResult）だけミップ付きで作って mip 0 を写す
            var result = new DecalImageBase
            {
                color = RecolorPipeline.CreateWorkTexture(width, height, "ClickRecolor_DecalImage"),
                // 遠目でちらつかないよう返す RT はミップ付き。縮小版（ミップ）・補間・異方性は貼る画像に合わせる（lilToon のデカールは画像そのものを引くので、
                // ミップ無しの画像なら常に原寸でくっきり見える。こちらだけミップを付けると斜めから見たときにぼやける。実機 2026-10-04）
                mips = image.mipmapCount > 1,
                filterMode = image.filterMode,
                anisoLevel = image.anisoLevel,
            };
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                material.SetTexture(MainTexId, image);
                material.SetTexture(MaskId, selectionMask);
                material.SetMatrix(RootWorldToLocalId, root.worldToLocalMatrix);
                material.SetVector(EdgeTexelId, new Vector4(1f / width, 1f / height, 0f, 0f));
                // 三角形の外の塗り残しは (0,0,0,0)（縁埋めが埋める）
                DrawParts(material, ColorPass, parts, result.color, Color.clear);
                DecalLayerBuilder.DilateAndFinalize(result.color, DecalLayerBuilder.DilateIterations);
                // 三角形が覆わない外周の画素も縁埋めで不透明になる。Clamp で引かれると箱の外へ筋が伸びるので消す（frag の外周判定だけでは足りない）
                DecalLayerBuilder.ClearBorder(result.color);
                // 透明な画素の rgb を近傍の色にする（α は 0 のまま）。Shift は α を触らず不透明度は α × 強さなので、
                // 滲ませた色は後の色変えで一緒に変わり、透明のまま残る
                DecalLayerBuilder.BleedColor(result.color, BleedIterations);

                // グラデーション（色の補間・不透明度の補間）と帯の打ち消しは画素の位置が要るので、同じ描き方で位置も描く。
                // 色が決まっているか（hasTarget）は見た目の側なので条件に入れない（色を決めた直後に下地を作り直さずに済む）
                bool needsPosition = edit.gradientEnabled || edit.flattenBase;
                if (needsPosition) result.positionMap = BuildPositionMap(material, parts, width, height);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }

        /// <summary>imageBase と同じ大きさ・ミップ・補間の、ApplyLook の書き込み先（呼び出し側所有。RecolorPipeline.DestroyWorkTexture で破棄）</summary>
        internal static RenderTexture CreateResult(DecalImageBase imageBase)
        {
            var result = RecolorPipeline.CreateWorkTexture(imageBase.color.width, imageBase.color.height, "ClickRecolor_DecalImage",
                useMipMap: imageBase.mips);
            result.filterMode = imageBase.filterMode;
            result.anisoLevel = imageBase.anisoLevel;
            return result;
        }

        /// <summary>
        /// 下地に今の色の設定を掛けて result（CreateResult で作った RT）に書く: 色が決まっていれば色変え（Shift）、
        /// 不透明度（グラデーションなら位置で補間）を α に掛け、ミップを作る。下地は書き換えないので、何度でも掛け直せる。
        /// 明るさの統計は下地だけで決まるので、最初に取ったものを下地に覚えて使い回す（GPU→CPU の読み戻しを 1 回にする）。大きさが合わなければ false
        /// </summary>
        internal static bool ApplyLook(DecalImageBase imageBase, RecolorEdit edit, RenderTexture result)
        {
            if (imageBase?.color == null || edit == null || result == null) return false;
            int width = imageBase.color.width, height = imageBase.color.height;
            if (result.width != width || result.height != height) return false;

            RenderTexture shifted = null;
            RenderTexture opaque = null;
            RenderTexture white = null;
            try
            {
                Stats stats = default;
                RenderTexture source = imageBase.color;
                if (edit.hasTarget)
                {
                    // 選択は画像の α に入っているので、統計と色変えのマスクは全面 1（統計は α ≥ 0.01 の画素だけ数える）
                    white = MaskTextures.GetTemporary(width, height);
                    FillWhite(white);
                    if (!imageBase.hasStats)
                    {
                        imageBase.stats = SelectionStats.Compute(imageBase.color, white);
                        imageBase.hasStats = true;
                    }
                    stats = imageBase.stats;
                    var p = ShiftParams.FromEdit(edit, stats);
                    // 不透明度は後で α に掛けるので、色変えは全量で掛ける
                    p.strength = 1f;
                    p.strength2 = 1f;
                    if (edit.flattenBase && imageBase.positionMap != null)
                    {
                        p.bands = BandStats.Compute(imageBase.color, white, imageBase.positionMap, BandStats.AxisOf(edit));
                        p.flattenStrength = edit.flattenStrength;
                    }
                    shifted = RecolorPipeline.CreateWorkTexture(width, height, "ClickRecolor_DecalImage");
                    RecolorPipeline.Shift(imageBase.color, white, shifted, p, imageBase.positionMap);
                    source = shifted;
                }

                // 不透明度（強さ）は元の値のまま（グラデーションなら位置で色 1・色 2 を補間）
                opaque = RecolorPipeline.CreateWorkTexture(width, height, "ClickRecolor_DecalImage");
                RecolorPipeline.ApplyOverlayOpacity(source, opaque, ShiftParams.FromEdit(edit, stats), imageBase.positionMap);
                Graphics.CopyTexture(opaque, 0, 0, result, 0, 0);
                if (imageBase.mips)
                {
                    result.GenerateMips();
                    // ミップでも外周を透明に保つ（平均で外周が不透明になると、Clamp で箱の外へ縁の色が伸びる）
                    DecalLayerBuilder.ClearMipBorderAlpha(result);
                }
                return true;
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(shifted);
                RecolorPipeline.DestroyWorkTexture(opaque);
                if (white != null) RenderTexture.ReleaseTemporary(white);
            }
        }

        /// <summary>
        /// 「ノーマルも反映」: parts の重ね貼りサブメッシュを投影 UV の位置に描き、元の法線マップ（normals[i]。parts と同じ並び、null なら平ら）を
        /// 元の UV で引いて画像の座標へ写し直した法線マップ（ARGBHalf・線形・ミップ付き。接空間の法線を 0..1 に詰めたもの。DecalImage.shader Pass 2）を返す。
        /// 大きさは EstimateNormalSize（元の法線マップの密度に合わせる）。法線マップが 1 つも無い・描けなければ null。返した RT は呼び出し側所有
        /// </summary>
        internal static RenderTexture BuildNormal(IReadOnlyList<OverlayPart> parts, IReadOnlyList<(Texture normal, Vector4 tilingOffset)> normals,
            int imageWidth, int imageHeight)
        {
            return BuildRemap(parts, normals, imageWidth, imageHeight, NormalPass, FlatNormal, "ClickRecolor_DecalNormal");
        }

        /// <summary>
        /// 「ノーマルも反映」の 2nd ノーマル: 強さマスク（masks[i]。lilToon の _Bump2ndScaleMask と Tiling/Offset。null ならそのパーツは描かない＝白）を
        /// 画像の座標へ写し直した RT（R にマスク。DecalImage.shader Pass 4）を返す。lilToon は強さマスクをメインの UV（重ね貼りでは画像の座標）で引くため。
        /// 大きさ・ミップ等は BuildNormal と同じ決め方。マスクが 1 つも無い・描けなければ null。返した RT は呼び出し側所有
        /// </summary>
        internal static RenderTexture BuildBump2ndMask(IReadOnlyList<OverlayPart> parts, IReadOnlyList<(Texture normal, Vector4 tilingOffset)> masks,
            int imageWidth, int imageHeight)
        {
            return BuildRemap(parts, masks, imageWidth, imageHeight, Bump2ndMaskPass, FullMask, "ClickRecolor_DecalBump2ndMask");
        }

        /// <summary>
        /// parts の重ね貼りサブメッシュを投影 UV の位置に pass で描き、sources[i] のテクスチャを元の UV で引いて画像の座標へ写し直す（BuildNormal・BuildBump2ndMask の本体）。
        /// 描かれない画素は fill。sources が 1 つも無い・描けなければ null
        /// </summary>
        private static RenderTexture BuildRemap(IReadOnlyList<OverlayPart> parts, IReadOnlyList<(Texture normal, Vector4 tilingOffset)> normals,
            int imageWidth, int imageHeight, int pass, Color fill, string name)
        {
            if (parts == null || normals == null || parts.Count != normals.Count || parts.Count == 0) return null;
            bool any = false;
            foreach (var n in normals) any |= n.normal != null;
            if (!any) return null;
            var shader = ShaderAssets.LoadShader(ShaderAssets.DecalImageGuid);
            if (shader == null || !shader.isSupported) return null;

            var (width, height) = EstimateNormalSize(parts, normals, imageWidth, imageHeight);
            // 縮小版（ミップ）・補間・異方性は元の法線マップ（最初のもの）に合わせる（lilToon が元の法線マップを引くときと同じ見え方にする）
            Texture reference = null;
            foreach (var n in normals)
            {
                if (n.normal != null) { reference = n.normal; break; }
            }
            bool mips = !(reference is Texture2D t2) || t2.mipmapCount > 1;
            var rt = RecolorPipeline.CreateWorkTexture(width, height, name, useMipMap: mips);
            // 画像の座標なので端は Clamp（元の Repeat を継ぐと画像の外で繰り返す）
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.filterMode = reference.filterMode;
            rt.anisoLevel = reference.anisoLevel;
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var previous = RenderTexture.active;
            try
            {
                // 描かれない画素は fill（GL.Clear の色は色空間の変換を受けうるので Blit で埋める）
                // 値のまま渡す（シェーダー側も Vector 型。Color 型だと Linear 色空間で線形化される）
                material.SetVector(FillColorId, fill);
                Graphics.Blit(null, rt, material, FillPass);
                Graphics.SetRenderTarget(rt);
                GL.PushMatrix();
                try
                {
                    for (int i = 0; i < parts.Count; i++)
                    {
                        var part = parts[i];
                        if (normals[i].normal == null) continue;
                        if (part.mesh == null || part.submesh < 0 || part.submesh >= part.mesh.subMeshCount) continue;
                        material.SetVector(UvScaleOffsetId, new Vector4(part.uvScale.x, part.uvScale.y, part.uvOffset.x, part.uvOffset.y));
                        material.SetTexture(SourceNormalId, normals[i].normal);
                        material.SetVector(SourceNormalStId, normals[i].tilingOffset);
                        material.SetPass(pass);
                        Graphics.DrawMeshNow(part.mesh, part.localToWorld, part.submesh);
                    }
                }
                finally
                {
                    GL.PopMatrix();
                }
                RenderTexture.active = previous;
                if (mips) rt.GenerateMips();
                return rt;
            }
            catch
            {
                RecolorPipeline.DestroyWorkTexture(rt);
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(material);
            }
        }

        /// <summary>
        /// 写し直す法線マップの大きさ: 元の法線マップの画素が、画像の空間にどれだけの密度で写るかに合わせる（それ以上細かくしても見た目は変わらない）。
        /// 重ね貼りの三角形について「元の法線マップの UV での面積 × 元の画素数」と「投影 UV での面積」の比から画像全体の画素数を求め、
        /// 縦横 NormalOversample 倍にして、画像の縦横比で分ける。
        /// NormalMinSize..NormalMaxSize に収める。測れなければ画像の大きさ（上限 NormalMaxSize）
        /// </summary>
        internal static (int width, int height) EstimateNormalSize(IReadOnlyList<OverlayPart> parts,
            IReadOnlyList<(Texture normal, Vector4 tilingOffset)> normals, int imageWidth, int imageHeight)
        {
            double projected = 0, texels = 0;
            for (int i = 0; parts != null && normals != null && i < parts.Count && i < normals.Count; i++)
            {
                var part = parts[i];
                var normal = normals[i].normal;
                if (normal == null || part.mesh == null || part.submesh < 0 || part.submesh >= part.mesh.subMeshCount) continue;
                var uv0 = new List<Vector2>();
                var uv1 = new List<Vector2>();
                part.mesh.GetUVs(0, uv0);
                part.mesh.GetUVs(1, uv1);
                if (uv0.Count == 0 || uv1.Count != uv0.Count) continue;
                var st = normals[i].tilingOffset;
                // 元の法線マップの UV = 元の UV0 × メインの Tiling × 法線の Tiling（Offset は面積に効かない）
                var scale = new Vector2(part.uvScale.x * st.x, part.uvScale.y * st.y);
                var triangles = part.mesh.GetTriangles(part.submesh);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                    projected += TriangleArea(uv0[a], uv0[b], uv0[c]);
                    texels += TriangleArea(Vector2.Scale(uv1[a], scale), Vector2.Scale(uv1[b], scale), Vector2.Scale(uv1[c], scale))
                              * normal.width * normal.height;
                }
            }
            int fallbackW = Mathf.Clamp(imageWidth, NormalMinSize, NormalMaxSize);
            int fallbackH = Mathf.Clamp(imageHeight, NormalMinSize, NormalMaxSize);
            if (projected <= 1e-12 || texels <= 0 || imageWidth <= 0 || imageHeight <= 0) return (fallbackW, fallbackH);
            // 画像全体（投影 UV で面積 1）に要る画素数（2 回の補間で細かい模様が消えないよう縦横 NormalOversample 倍）
            double pixels = texels / projected * NormalOversample * NormalOversample;
            double aspect = (double)imageWidth / imageHeight;
            int w = Mathf.Clamp((int)Math.Ceiling(Math.Sqrt(pixels * aspect)), NormalMinSize, NormalMaxSize);
            int h = Mathf.Clamp((int)Math.Ceiling(Math.Sqrt(pixels / aspect)), NormalMinSize, NormalMaxSize);
            return (w, h);
        }

        private static double TriangleArea(Vector2 a, Vector2 b, Vector2 c) =>
            Math.Abs((double)(b.x - a.x) * (c.y - a.y) - (double)(c.x - a.x) * (b.y - a.y)) * 0.5;

        /// <summary>
        /// parts の重ね貼りサブメッシュを pass で target に描く（clear で消してから）。RenderTexture.active は元に戻す。
        /// メッシュが無い・範囲外のサブメッシュは飛ばす
        /// </summary>
        private static void DrawParts(Material material, int pass, IReadOnlyList<OverlayPart> parts, RenderTexture target, Color clear)
        {
            var previous = RenderTexture.active;
            try
            {
                Graphics.SetRenderTarget(target);
                GL.Clear(false, true, clear);
                GL.PushMatrix();
                try
                {
                    foreach (var part in parts)
                    {
                        if (part.mesh == null || part.submesh < 0 || part.submesh >= part.mesh.subMeshCount) continue;
                        material.SetVector(UvScaleOffsetId, new Vector4(part.uvScale.x, part.uvScale.y, part.uvOffset.x, part.uvOffset.y));
                        material.SetPass(pass);
                        Graphics.DrawMeshNow(part.mesh, part.localToWorld, part.submesh);
                    }
                }
                finally
                {
                    GL.PopMatrix();
                }
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        /// <summary>
        /// 画像の空間の位置マップ（ARGBFloat、PositionMap.Build と同じ形式。RGB = 対象ルートのローカル位置、A = 1 が描いた印）。
        /// 色の縁埋め（DilateIterations 回）で広げた画素にも位置があるよう、同じ回数だけ膨張する（PositionMap.Fill）。
        /// 作れなければ null（グラデーションは色 1 のまま、帯の打ち消しは掛からない）。呼び出し側が PositionMap.Destroy する
        /// </summary>
        private static RenderTexture BuildPositionMap(Material material, IReadOnlyList<OverlayPart> parts, int width, int height)
        {
            var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBFloat, 0)
            {
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
                // 膨張（PositionFill.compute）で ping-pong の書き込み先にもする
                enableRandomWrite = SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBFloat),
            };
            var rt = new RenderTexture(desc)
            {
                name = "ClickRecolor_DecalImagePosition",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            if (!rt.Create())
            {
                Object.DestroyImmediate(rt);
                return null;
            }
            try
            {
                // 描かれない画素は A = 0（ColorShift.compute が t = 0・帯の補間なしとして扱う）
                DrawParts(material, PositionPass, parts, rt, new Color(0f, 0f, 0f, 0f));
            }
            catch
            {
                PositionMap.Destroy(rt);
                throw;
            }
            return PositionMap.Fill(rt, DecalLayerBuilder.DilateIterations);
        }

        /// <summary>R8 の一時 RT を 1 で埋める（RenderTexture.active は元に戻す）</summary>
        private static void FillWhite(RenderTexture rt)
        {
            var previous = RenderTexture.active;
            try
            {
                Graphics.SetRenderTarget(rt);
                GL.Clear(false, true, Color.white);
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }
    }
}
