using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Decal;
using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>
    /// 「画像を入れる」のデカール層。対象テクスチャを使うスロットのメッシュのうち、箱の +Z 側を向く三角形だけを UV 空間に描き、
    /// 箱の XY で引いた画像の色を書く（DecalLayer.shader）。選択マスクと合成するのは RecolorPipeline。
    /// 箱の内外の判定・座標の取り方は BoxMaskBuilder と同じ（出力が 1 でなく画像の色になる）。
    /// 裏面カリングを C# 側で行うのは、UV 空間に広げて描くと画面上の向きが元の面の向きと無関係になり、GPU のカリングが使えないため
    /// </summary>
    internal static class DecalLayerBuilder
    {
        /// <summary>縁埋めの回数（三角形の縁の塗り残しを埋める。はみ出し幅の広げは選択マスク側で行う）</summary>
        internal const int DilateIterations = 2;

        /// <summary>
        /// スーパーサンプリングする一時 RT の一辺の上限（これを超える大きさでは等倍で描く）。層は ARGBHalf（8 バイト/画素）なので、
        /// R8 の BoxMaskBuilder（8192）より低くする: 4096² で 128MB。元 2048 までは 2 倍、4096 の書き出しは等倍（4096 なら密度が高く階段は目立ちにくい）
        /// </summary>
        private const int SupersampleMaxSize = 4096;

        /// <summary>箱の大きさの各成分の絶対値の下限（0 だとシェーダーの割り算と sign が壊れる）</summary>
        private const float MinBoxExtent = 1e-5f;

        private const int ThreadGroupSize = 16;

        private static ComputeShader s_dilate;
        private static int s_kernelDilate;
        private static int s_kernelFinalize;
        private static int s_kernelDownsample2x;
        private static int s_kernelClearBorder;
        private static int s_kernelBleedColor;
        private static int s_kernelClearBorderAlpha;

        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int RootWorldToLocalId = Shader.PropertyToID("_RootWorldToLocal");
        private static readonly int RootToBoxId = Shader.PropertyToID("_RootToBox");
        private static readonly int BoxSizeId = Shader.PropertyToID("_BoxSize");
        private static readonly int FitScaleId = Shader.PropertyToID("_FitScale");
        private static readonly int UvScaleOffsetId = Shader.PropertyToID("_UvScaleOffset");
        private static readonly int UseImageUvId = Shader.PropertyToID("_UseImageUv");

        private static readonly int DilateInId = Shader.PropertyToID("In");
        private static readonly int DilateOutId = Shader.PropertyToID("Out");
        private static readonly int DilateWidthId = Shader.PropertyToID("Width");
        private static readonly int DilateHeightId = Shader.PropertyToID("Height");

        private static ComputeShader DilateShader
        {
            get
            {
                if (s_dilate == null)
                {
                    s_dilate = ShaderAssets.Load(ShaderAssets.DecalDilateGuid);
                    if (s_dilate != null)
                    {
                        s_kernelDilate = s_dilate.FindKernel("CSDilate");
                        s_kernelFinalize = s_dilate.FindKernel("CSFinalize");
                        s_kernelDownsample2x = s_dilate.FindKernel("CSDownsample2x");
                        s_kernelClearBorder = s_dilate.FindKernel("CSClearBorder");
                        s_kernelBleedColor = s_dilate.FindKernel("CSBleedColor");
                        s_kernelClearBorderAlpha = s_dilate.FindKernel("CSClearBorderAlpha");
                    }
                }
                return s_dilate;
            }
        }

        /// <summary>デカール層のシェーダー（DecalLayer.shader）と割り戻し・縁埋め（DecalDilate.compute）が両方使えるか</summary>
        internal static bool IsAvailable
        {
            get
            {
                var shader = ShaderAssets.LoadShader(ShaderAssets.DecalLayerGuid);
                return shader != null && shader.isSupported && IsDilateAvailable;
            }
        }

        /// <summary>縁埋め・割り戻しの compute（DecalDilate.compute）が使えるか（重ね貼りの画像 DecalImageBuilder も使う）</summary>
        internal static bool IsDilateAvailable =>
            SystemInfo.supportsComputeShaders
            && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf)
            && DilateShader != null;

        /// <summary>
        /// root 配下の slots（PositionMap.CollectSlots）のうち、箱の +Z 側を向く三角形だけを UV 空間に描いて、
        /// 画像を箱の XY で引いたデカール層（ARGBHalf・Linear、width×height、ストレート α・α ≤ 1）を作る。
        /// 描画は乗算済みで重ね（UV が重なっても α が 1 を超えない）、乗算済みのまま縁埋めしてから最後に割り戻す。
        /// 返した RT は呼び出し側の所有（RecolorPipeline.DestroyWorkTexture）。
        /// 描くスロットが無い・画像が無い・シェーダー（割り戻し・縁埋めの compute を含む）が無ければ null。
        /// 読み取り不可（Read/Write 無効）のメッシュは三角形の向きを見られないので描かない（案内は呼び出し側で出す）。
        /// supersample が false なら等倍で描く（箱のドラッグ中の軽さ優先。離したときに掛け直す）。
        /// sticker（置き方がシールの編集。箱の値はこの編集のもの）を渡すと、箱から投影せず面に沿った展開（DecalMappings）で画像の座標を頂点ごとに決め、
        /// 展開の届いた三角形だけを描く（重ね貼りと同じ割り付けにする）。selectionMask（R8 を読み戻した選択マスク、selectionWidth×selectionHeight）を渡すと、
        /// 展開の種を選択範囲の三角形から選ぶ（重ね貼りと同じ種にする）
        /// </summary>
        internal static RenderTexture Build(
            Transform root, IReadOnlyList<PositionMap.Slot> slots, int width, int height, Texture2D image,
            Vector3 boxPosition, Quaternion boxRotation, Vector3 boxSize, bool keepAspect, bool supersample = true, RecolorEdit sticker = null,
            byte[] selectionMask = null, int selectionWidth = 0, int selectionHeight = 0)
        {
            if (root == null || slots == null || slots.Count == 0 || width <= 0 || height <= 0 || image == null) return null;
            var shader = ShaderAssets.LoadShader(ShaderAssets.DecalLayerGuid);
            if (shader == null || !shader.isSupported) return null;
            // 割り戻せないと乗算済みのまま返ってしまうので、compute が無ければ層は作らない
            if (!IsDilateAvailable) return null;

            var rt = RecolorPipeline.CreateWorkTexture(width, height, "ClickRecolor_DecalLayer");
            // 画像の縁がテクセル単位の階段にならないよう、2 倍の解像度で描いて平均で縮める（スーパーサンプリング。上限 SupersampleMaxSize）。
            // 描画中は乗算済みなので、乗算済みのまま平均で縮めれば縁に色が滲まない（割り戻しは等倍側の CSFinalize で 1 回だけ）。
            // 縮小は CSDownsample2x（2×2 のうち描かれた α > 0 のサブ画素だけの平均）。縮めた後の α:
            // - 画像の縁・箱の縁をまたいで「描いた印（α = CoverageFloor）」と不透明が混ざる画素は 0.25..0.75 などの中間値になる（意図どおりの縁のなだらかさ）
            // - サブ画素がすべて印なら平均は CoverageFloor（< CoverageThreshold）で、CSFinalize が 0 に落とす
            // - 三角形の外（未描画、α = 0）のサブ画素は平均に入れないので、チャートの縁の画素が薄まらない。4 つとも未描画なら 0 のままで、等倍の縁埋めが埋める
            supersample = supersample && width * 2 <= SupersampleMaxSize && height * 2 <= SupersampleMaxSize;
            RenderTexture target = rt;
            try
            {
                if (supersample) target = RecolorPipeline.CreateWorkTexture(width * 2, height * 2, "ClickRecolor_DecalLayer2x");
                var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                var previous = RenderTexture.active;
                try
                {
                    var rootWorldToLocal = root.worldToLocalMatrix;
                    var rootToBox = BoxMaskBuilder.RootToBox(boxPosition, boxRotation);
                    material.SetTexture(MainTexId, image);
                    material.SetMatrix(RootWorldToLocalId, rootWorldToLocal);
                    material.SetMatrix(RootToBoxId, rootToBox);
                    material.SetVector(BoxSizeId, SafeSize(boxSize));
                    material.SetVector(FitScaleId, FitScale(image, boxSize, keepAspect));
                    material.SetFloat(UseImageUvId, sticker != null ? 1f : 0f);

                    Graphics.SetRenderTarget(target);
                    GL.Clear(false, true, Color.clear);
                    GL.PushMatrix();
                    try
                    {
                        DrawFrontFacingSlots(material, slots, root, rootWorldToLocal, rootToBox, Abs(SafeSize(boxSize)) * 0.5f, sticker,
                            selectionMask, selectionWidth, selectionHeight);
                    }
                    finally
                    {
                        GL.PopMatrix();
                    }
                }
                finally
                {
                    RenderTexture.active = previous;
                    Object.DestroyImmediate(material);
                }
                if (supersample) Downsample2x(target, rt);
                DilateAndFinalize(rt, DilateIterations);
            }
            catch
            {
                RecolorPipeline.DestroyWorkTexture(rt);
                throw;
            }
            finally
            {
                if (target != rt) RecolorPipeline.DestroyWorkTexture(target);
            }
            return rt;
        }

        /// <summary>
        /// 箱の大きさ（符号付き）の各成分を、絶対値が MinBoxExtent 以上になるようにする（符号は保つ。0 は +MinBoxExtent）。
        /// シェーダーは sign(_BoxSize) で反転を見るので、0 の成分があると大きさ 0 で割ってしまう
        /// </summary>
        internal static Vector3 SafeSize(Vector3 size) =>
            new Vector3(SafeExtent(size.x), SafeExtent(size.y), SafeExtent(size.z));

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private static float SafeExtent(float v)
        {
            if (Mathf.Abs(v) >= MinBoxExtent) return v;
            return v < 0f ? -MinBoxExtent : MinBoxExtent;
        }

        /// <summary>
        /// 比率を保つための画像 UV の拡大（シェーダーの _FitScale）。画像の縦横比 ia と箱の縦横比 ba（|x| / |y|）を比べ、
        /// 箱が横長なら u を ba / ia 倍、縦長なら v を ia / ba 倍に広げる（はみ出た所は描かない＝長辺に合わせて内接する）。
        /// keepAspect が false・画像や箱の辺が 0 以下なら (1,1)（箱いっぱいに引き伸ばす）
        /// </summary>
        internal static Vector2 FitScale(Texture2D image, Vector3 boxSize, bool keepAspect)
        {
            if (!keepAspect || image == null || image.width <= 0 || image.height <= 0) return Vector2.one;
            float bx = Mathf.Abs(boxSize.x);
            float by = Mathf.Abs(boxSize.y);
            if (bx <= 0f || by <= 0f) return Vector2.one;
            float ia = (float)image.width / image.height;
            float ba = bx / by;
            return ba > ia ? new Vector2(ba / ia, 1f) : new Vector2(1f, ia / ba);
        }

        /// <summary>
        /// slots を順に、箱の +Z 側を向く三角形だけ描く（PositionMap.DrawSlots と同じ流れ。同じ Renderer が続くスロットは焼いたメッシュと配列を使い回す）。
        /// 読み取り不可のメッシュ・範囲外のサブメッシュ・三角形でないトポロジは飛ばす（三角形の向きを見られないため）。
        /// メッシュの境界箱が箱と交わらないスロットも飛ばす（三角形を絞る手間を省く）。
        /// sticker を渡すと、向きでは絞らず、Renderer ごとに面に沿った展開を作って届いた三角形を描く（画像の座標は頂点の TEXCOORD1 に入れる）
        /// </summary>
        private static void DrawFrontFacingSlots(
            Material material, IReadOnlyList<PositionMap.Slot> slots, Transform root, Matrix4x4 rootWorldToLocal, Matrix4x4 rootToBox, Vector3 boxHalf,
            RecolorEdit sticker, byte[] selectionMask, int selectionWidth, int selectionHeight)
        {
            // 将来案: 表裏の判定は箱の回転（とポーズ）にしか依存しないので、箱の移動・拡縮だけのときはキャッシュできる
            var worldToBox = rootToBox * rootWorldToLocal;
            Renderer current = null;
            Mesh mesh = null;
            MeshRaycaster.MeshArrays arrays = null;
            Matrix4x4 localToWorld = Matrix4x4.identity;
            bool ownsMesh = false;
            bool flip = false;
            Bounds localBounds = default;
            IDecalMapping mapping = null;
            try
            {
                foreach (var slot in slots)
                {
                    if (slot.renderer == null) continue;
                    if (slot.renderer != current)
                    {
                        if (ownsMesh && mesh != null) Object.DestroyImmediate(mesh);
                        current = slot.renderer;
                        arrays = null;
                        // レイ判定・位置マップと同じ取り方（SkinnedMeshRenderer は今のポーズで焼く）
                        if (!MeshRaycaster.TryGetMeshData(slot.renderer, out mesh, out localToWorld, out ownsMesh))
                        {
                            mesh = null;
                            ownsMesh = false;
                        }
                        // 頂点・UV・三角形は Renderer が変わったときに 1 回だけ取る（mesh.vertices 等は呼ぶたびに配列をコピーする）。読み取り不可なら null
                        arrays = GetArrays(mesh);
                        // 箱との交差の粗い判定に使う境界箱は頂点から求める（焼いたメッシュの bounds が今のポーズで更新されるかに頼らない）
                        if (arrays != null) localBounds = VertexBounds(arrays.vertices);
                        // 鏡像（行列式が負）の Renderer は Unity がカリングを反転して描くので、見えている側は幾何法線の逆になる。
                        // SkinnedMeshRenderer は BakeMesh がスケール（鏡像も）を頂点に焼き込み、TryGetMeshData の行列はスケール無しなので、
                        // toBox の行列式では気づけない。Renderer の Transform の行列式で見る
                        flip = worldToBox.determinant * slot.renderer.transform.localToWorldMatrix.determinant < 0f;
                        mapping = null;
                        if (sticker != null && arrays != null)
                        {
                            // 展開はこの Renderer の、このテクスチャのスロットすべての三角形をたどる（スロットごとに切ると、サブメッシュの境目で止まる）
                            var rendererSlots = new List<int>();
                            // 選択範囲の三角形（この Renderer のスロットの Tiling/Offset のどれかで選択マスクに入るもの）。展開の種をここから選ぶ
                            var filters = new List<System.Func<int, int, int, bool>>();
                            foreach (var other in slots)
                            {
                                if (other.renderer != current) continue;
                                rendererSlots.Add(other.submesh);
                                var filter = DecalOverlayAssembler.SelectionFilter(selectionMask, selectionWidth, selectionHeight, arrays.uv, other.uvScale, other.uvOffset);
                                if (filter != null) filters.Add(filter);
                            }
                            System.Func<int, int, int, bool> selected = null;
                            if (filters.Count > 0)
                            {
                                selected = (a, b, c) =>
                                {
                                    foreach (var f in filters)
                                    {
                                        if (f(a, b, c)) return true;
                                    }
                                    return false;
                                };
                            }
                            SurfaceUnfoldGraph graph = null;
                            var bakedMesh = mesh;
                            mapping = DecalMappings.For(sticker, root, arrays.vertices, localToWorld, flip,
                                () => DecalMappings.TrianglesOf(bakedMesh, rendererSlots), ref graph, selected: selected);
                        }
                    }
                    if (arrays == null)
                    {
                        WarnUnreadable(slot.renderer);
                        continue;
                    }
                    if (slot.submesh < 0 || slot.submesh >= arrays.triangles.Length) continue;
                    var triangles = arrays.triangles[slot.submesh];
                    if (triangles == null) continue;

                    var toBox = worldToBox * localToWorld;
                    if (!BoundsOverlapBox(localBounds, toBox, boxHalf)) continue;
                    var front = mapping != null ? BuildMappedMesh(mesh, arrays, triangles, mapping) : BuildFrontFacingMesh(mesh, arrays, triangles, toBox, flip);
                    if (front == null) continue;
                    try
                    {
                        material.SetVector(UvScaleOffsetId, new Vector4(slot.uvScale.x, slot.uvScale.y, slot.uvOffset.x, slot.uvOffset.y));
                        material.SetPass(0);
                        Graphics.DrawMeshNow(front, localToWorld, 0);
                    }
                    finally
                    {
                        Object.DestroyImmediate(front);
                    }
                }
            }
            finally
            {
                if (ownsMesh && mesh != null) Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>Read/Write 無効で画像を描けなかったメッシュ（InstanceID）。同じメッシュに何度も警告しない</summary>
        private static readonly HashSet<int> s_warnedUnreadable = new HashSet<int>();

        /// <summary>
        /// renderer のメッシュが Read/Write 無効なら、画像を貼れないことを 1 回だけ警告する（空の層になり、警告なしに画像が出ないため。レビュー指摘 2026-10-04）
        /// </summary>
        private static void WarnUnreadable(Renderer renderer)
        {
            var source = RendererMeshAccess.GetSharedMesh(renderer);
            if (source == null || source.isReadable || !s_warnedUnreadable.Add(source.GetInstanceID())) return;
            Debug.LogWarning($"[Tocolo] '{renderer.name}'のメッシュはRead/Writeが無効なため、画像を貼れません。メッシュのインポート設定でRead/Writeを有効にしてください", renderer);
        }

        private static MeshRaycaster.MeshArrays GetArrays(Mesh mesh) => mesh != null ? MeshRaycaster.MeshArrays.From(mesh) : null;

        /// <summary>頂点（メッシュのローカル）の境界箱。MeshArrays.From が頂点 0 個を null にするので、vertices は 1 個以上</summary>
        private static Bounds VertexBounds(Vector3[] vertices)
        {
            var min = vertices[0];
            var max = vertices[0];
            for (int i = 1; i < vertices.Length; i++)
            {
                min = Vector3.Min(min, vertices[i]);
                max = Vector3.Max(max, vertices[i]);
            }
            var bounds = new Bounds();
            bounds.SetMinMax(min, max);
            return bounds;
        }

        /// <summary>メッシュの境界箱の 8 隅を toBox で箱のローカルへ移し、その AABB が箱（中心原点・半分の大きさ boxHalf）と交わるか</summary>
        private static bool BoundsOverlapBox(Bounds bounds, Matrix4x4 toBox, Vector3 boxHalf)
        {
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            var c = bounds.center;
            var e = bounds.extents;
            for (int k = 0; k < 8; k++)
            {
                var corner = new Vector3(
                    c.x + ((k & 1) != 0 ? e.x : -e.x),
                    c.y + ((k & 2) != 0 ? e.y : -e.y),
                    c.z + ((k & 4) != 0 ? e.z : -e.z));
                var p = toBox.MultiplyPoint3x4(corner);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return min.x <= boxHalf.x && max.x >= -boxHalf.x
                && min.y <= boxHalf.y && max.y >= -boxHalf.y
                && min.z <= boxHalf.z && max.z >= -boxHalf.z;
        }

        /// <summary>
        /// triangles（mesh のサブメッシュ 1 つ分）のうち、toBox（メッシュのローカル → 箱のローカル）で見た幾何法線（インデックス順の外積）の
        /// z が正（flip なら負）の三角形だけを持つ一時メッシュ。頂点・UV は arrays のまま、三角形だけ絞る。
        /// 表の三角形が 1 つも無ければ null。呼び出し側が DestroyImmediate すること
        /// </summary>
        private static Mesh BuildFrontFacingMesh(Mesh mesh, MeshRaycaster.MeshArrays arrays, int[] triangles, Matrix4x4 toBox, bool flip)
        {
            var vertices = arrays.vertices;
            var kept = new List<int>(triangles.Length);
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                var v0 = toBox.MultiplyPoint3x4(vertices[i0]);
                var v1 = toBox.MultiplyPoint3x4(vertices[i1]);
                var v2 = toBox.MultiplyPoint3x4(vertices[i2]);
                if (DecalProjection.IsFrontFacing(v0, v1, v2, flip))
                {
                    kept.Add(i0);
                    kept.Add(i1);
                    kept.Add(i2);
                }
            }
            if (kept.Count == 0) return null;

            // 頂点数が 65535 を超えるメッシュもあるので、頂点を入れる前に元のインデックス形式に揃える
            var front = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = mesh.indexFormat };
            front.vertices = vertices;
            front.uv = arrays.uv;
            front.SetTriangles(kept, 0);
            return front;
        }

        /// <summary>
        /// triangles（mesh のサブメッシュ 1 つ分）のうち mapping が描く三角形だけを持ち、頂点の TEXCOORD1 に mapping の画像の座標を入れた一時メッシュ
        /// （シールの面に沿った展開）。描く三角形が 1 つも無ければ null。呼び出し側が DestroyImmediate すること
        /// </summary>
        private static Mesh BuildMappedMesh(Mesh mesh, MeshRaycaster.MeshArrays arrays, int[] triangles, IDecalMapping mapping)
        {
            var kept = new List<int>(triangles.Length);
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                if (!mapping.Draws(triangles[t], triangles[t + 1], triangles[t + 2])) continue;
                kept.Add(triangles[t]);
                kept.Add(triangles[t + 1]);
                kept.Add(triangles[t + 2]);
            }
            if (kept.Count == 0) return null;
            var imageUvs = new Vector2[arrays.vertices.Length];
            for (int v = 0; v < imageUvs.Length; v++) imageUvs[v] = mapping.Uv(v);
            var mapped = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = mesh.indexFormat };
            mapped.vertices = arrays.vertices;
            mapped.uv = arrays.uv;
            mapped.uv2 = imageUvs;
            mapped.SetTriangles(kept, 0);
            return mapped;
        }

        /// <summary>
        /// 乗算済みで描いた層の α = 0 の画素（描かれていない塗り残し）を近傍の色で埋める縁埋めを iterations 回掛け（CSDilate）、
        /// 最後にストレート α に割り戻す（CSFinalize を 1 回。描いた印の α 下限もここで消す）。どちらも DecalDilate.compute で、一時 RT と ping-pong。
        /// 画像の透明な部分は描いた印の α（CoverageFloor）を持つので縁埋めされず、輪郭が太らない。結果は rt に残す。
        /// compute が使えるかは Build の冒頭で確かめてある（ここでの判定は念のため）。重ね貼りの画像（DecalImageBuilder）も共用する
        /// </summary>
        internal static void DilateAndFinalize(RenderTexture rt, int iterations)
        {
            if (rt == null || !rt.enableRandomWrite || !IsDilateAvailable) return;
            var shader = DilateShader;
            var temp = RecolorPipeline.CreateWorkTexture(rt.width, rt.height, "ClickRecolor_DecalDilate");
            try
            {
                shader.SetInt(DilateWidthId, rt.width);
                shader.SetInt(DilateHeightId, rt.height);
                var src = rt;
                var dst = temp;
                for (int i = 0; i < iterations; i++) Run(shader, s_kernelDilate, ref src, ref dst);
                Run(shader, s_kernelFinalize, ref src, ref dst);
                // 走らせた回数が奇数なら最後の結果は temp 側にある。
                // rt がミップ付き（重ね貼りの画像）でも写せるよう mip 0 だけ写す（全体の写しはミップ数が揃っていないと失敗する）
                if (src != rt) Graphics.CopyTexture(src, 0, 0, rt, 0, 0);
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(temp);
            }
        }

        /// <summary>
        /// rt（ストレート α、enableRandomWrite）の外周 1 テクセルを (0,0,0,0) にする（CSClearBorder。mip 0 をその場で書き換える）。
        /// 重ね貼りの画像（DecalImageBuilder）用: 画像を Clamp で引く重ね貼りマテリアルで、外周の色が箱の外へ伸びないようにする
        /// </summary>
        internal static void ClearBorder(RenderTexture rt)
        {
            if (rt == null || !rt.enableRandomWrite || !IsDilateAvailable) return;
            var shader = DilateShader;
            shader.SetInt(DilateWidthId, rt.width);
            shader.SetInt(DilateHeightId, rt.height);
            // In は使わないが、念のため何か束縛しておく
            shader.SetTexture(s_kernelClearBorder, DilateInId, Texture2D.blackTexture);
            shader.SetTexture(s_kernelClearBorder, DilateOutId, rt);
            shader.Dispatch(s_kernelClearBorder, Groups(rt.width), Groups(rt.height), 1);
        }

        /// <summary>
        /// ミップ付きの rt（enableRandomWrite、ミップは生成済み）の mip 1 以降の外周 1 テクセルの α を 0 にする（CSClearBorderAlpha）。
        /// 重ね貼りの画像用: mip 0 の外周は ClearBorder で透明にしてあるが、ミップを作ると内側と平均されて透明でなくなり、
        /// Clamp で引かれる箱の外へ縁の色が伸びる。各ミップの外周を透明に戻す（遠目では縁が最大でそのミップの 1 テクセルぶん内側に寄る）
        /// </summary>
        internal static void ClearMipBorderAlpha(RenderTexture rt)
        {
            if (rt == null || !rt.useMipMap || !rt.enableRandomWrite || !IsDilateAvailable) return;
            var shader = DilateShader;
            int mipCount = rt.mipmapCount;
            shader.SetTexture(s_kernelClearBorderAlpha, DilateInId, Texture2D.blackTexture);
            for (int mip = 1; mip < mipCount; mip++)
            {
                int w = Mathf.Max(1, rt.width >> mip);
                int h = Mathf.Max(1, rt.height >> mip);
                shader.SetInt(DilateWidthId, w);
                shader.SetInt(DilateHeightId, h);
                shader.SetTexture(s_kernelClearBorderAlpha, DilateOutId, rt, mip);
                shader.Dispatch(s_kernelClearBorderAlpha, Groups(w), Groups(h), 1);
            }
        }

        /// <summary>
        /// rt（ストレート α、enableRandomWrite）の α = 0 の画素の rgb を近傍の色で埋める（CSBleedColor を iterations 回。α は 0 のまま）。
        /// 重ね貼りの画像（DecalImageBuilder）用: bilinear・ストレート α で引かれたとき、透明との境界に黒が混ざって暗い縁が出ないようにする。結果は rt の mip 0 に残す
        /// </summary>
        internal static void BleedColor(RenderTexture rt, int iterations)
        {
            if (rt == null || iterations <= 0 || !rt.enableRandomWrite || !IsDilateAvailable) return;
            var shader = DilateShader;
            var temp = RecolorPipeline.CreateWorkTexture(rt.width, rt.height, "ClickRecolor_DecalBleed");
            try
            {
                shader.SetInt(DilateWidthId, rt.width);
                shader.SetInt(DilateHeightId, rt.height);
                var src = rt;
                var dst = temp;
                for (int i = 0; i < iterations; i++) Run(shader, s_kernelBleedColor, ref src, ref dst);
                if (src != rt) Graphics.CopyTexture(src, 0, 0, rt, 0, 0);
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(temp);
            }
        }

        /// <summary>
        /// 2 倍で描いた乗算済みの層 src（dst の縦横 2 倍）を dst に縮める（CSDownsample2x。2×2 のうち α > 0 のサブ画素だけの平均）。
        /// compute が使えるかは Build の冒頭で確かめてある
        /// </summary>
        private static void Downsample2x(RenderTexture src, RenderTexture dst)
        {
            var shader = DilateShader;
            shader.SetInt(DilateWidthId, dst.width);
            shader.SetInt(DilateHeightId, dst.height);
            shader.SetTexture(s_kernelDownsample2x, DilateInId, src);
            shader.SetTexture(s_kernelDownsample2x, DilateOutId, dst);
            shader.Dispatch(s_kernelDownsample2x, Groups(dst.width), Groups(dst.height), 1);
        }

        /// <summary>kernel を src → dst で 1 回走らせ、src と dst を入れ替える（結果が src 側に来る）</summary>
        private static void Run(ComputeShader shader, int kernel, ref RenderTexture src, ref RenderTexture dst)
        {
            shader.SetTexture(kernel, DilateInId, src);
            shader.SetTexture(kernel, DilateOutId, dst);
            shader.Dispatch(kernel, Groups(src.width), Groups(src.height), 1);
            (src, dst) = (dst, src);
        }

        private static int Groups(int size) => (size + ThreadGroupSize - 1) / ThreadGroupSize;
    }
}
