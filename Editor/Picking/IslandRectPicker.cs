using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Masks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.Picking
{
    /// <summary>矩形選択で見つかった島 1 つ（(renderer, submesh, chartId) で 1 つ）</summary>
    internal struct RectIsland
    {
        public Renderer renderer;
        public int submesh;
        /// <summary>島の UV チャート番号（UvChartDetector。取れなければ -1）</summary>
        public int chartId;
        /// <summary>代表の三角形（サブメッシュ内ローカル番号。最初に見つかったもの）</summary>
        public int triangle;
        /// <summary>代表の三角形の位置（島 ID 描画なら UV0 の重心、レイ方式ならヒット位置の UV0）</summary>
        public Vector2 uv0;
        /// <summary>uv0 にマテリアルの Tiling/Offset を適用した UV（種の uv。テクスチャが無ければ uv0 のまま）</summary>
        public Vector2 uv;
        /// <summary>島のマテリアルのメインテクスチャ（無ければ null）</summary>
        public Texture2D texture;
    }

    /// <summary>
    /// Ctrl＋ドラッグの矩形の中に見えている島を集める。
    /// 基本は「島 ID 描画」: Scene カメラと同じ視点で、矩形の範囲だけを小さな RT に
    /// (Renderer 番号×256＋サブメッシュ, 三角形番号) として描き、読み戻した画素を島に変換する（奥に隠れた島は拾わない）。
    /// シェーダーが使えない環境では、矩形内に格子状にレイを飛ばす方式にフォールバックする
    /// </summary>
    internal static class IslandRectPicker
    {
        /// <summary>サブメッシュを Renderer 番号と一緒に 1 つの float に詰めるときの桁（サブメッシュはこれ未満だけ扱う）</summary>
        internal const int SubmeshStride = 256;

        /// <summary>フォールバック（格子レイ方式）の間隔（画素）</summary>
        private const float FallbackStepPixels = 8f;

        /// <summary>フォールバックのレイの上限。レイ 1 本ごとに全三角形を調べるので、大きな矩形で固まらないよう間隔を広げる</summary>
        private const int FallbackMaxRays = 1024;

        private static readonly int RendererIndexId = Shader.PropertyToID("_RendererIndex");

        /// <summary>
        /// Scene ビュー（camera）の GUI 座標の矩形 guiRect の中に見えている、renderers の島を集める。
        /// renderers の順番が島 ID の Renderer 番号になる。SkinnedMeshRenderer の焼きメッシュは picker のキャッシュを共用する
        /// </summary>
        public static List<RectIsland> Pick(Camera camera, Rect guiRect, IReadOnlyList<Renderer> renderers, ScenePicker picker)
        {
            if (camera == null || renderers == null || picker == null) return new List<RectIsland>();
            if (TryPickByIdRender(camera, guiRect, renderers, picker, out var islands)) return islands;
            return PickByRays(guiRect, renderers, picker);
        }

        /// <summary>
        /// 貫通選択（「隠れている範囲も選ぶ」）: 奥に隠れているかにかかわらず、三角形の重心か頂点のどれかが矩形に入る三角形の島を全部集める。
        /// 描画はせず、renderers の全三角形をカメラの手前側だけ GUI 座標に投影して判定する（Ctrl＋ドラッグを離したときの 1 回だけなので CPU でよい）。
        /// 同じ (Renderer, サブメッシュ, チャート) は 1 つにまとめ、代表は最初に見つかった三角形（uv0 はその UV0 の重心）
        /// </summary>
        public static List<RectIsland> PickThrough(Camera camera, Rect guiRect, IReadOnlyList<Renderer> renderers, ScenePicker picker)
        {
            var result = new List<RectIsland>();
            if (camera == null || renderers == null || picker == null) return result;

            var seen = new HashSet<(Renderer renderer, int submesh, int chart)>();
            for (int r = 0; r < renderers.Count; r++)
            {
                var renderer = renderers[r];
                if (renderer == null || !picker.TryGetMeshArrays(renderer, out var arrays, out var localToWorld)) continue;
                var sharedMesh = RendererMeshAccess.GetSharedMesh(renderer);
                var uvs = arrays.uv;

                for (int sub = 0; sub < arrays.triangles.Length; sub++)
                {
                    var triangles = arrays.triangles[sub];
                    if (triangles == null) continue;
                    ChartTable table = null;
                    bool tableResolved = false;
                    for (int i = 0; i + 2 < triangles.Length; i += 3)
                    {
                        if (!TriangleTouchesRect(camera, guiRect, arrays.vertices, triangles, i, localToWorld)) continue;

                        if (!tableResolved)
                        {
                            table = UvChartDetector.GetOrBuild(sharedMesh, sub);
                            tableResolved = true;
                        }
                        int local = i / 3;
                        int chart = table != null && local < table.chartOfTriangle.Length ? table.chartOfTriangle[local] : -1;
                        if (!seen.Add((renderer, sub, chart >= 0 ? chart : -1 - local))) continue;

                        Vector2 centroid = (uvs[triangles[i]] + uvs[triangles[i + 1]] + uvs[triangles[i + 2]]) / 3f;
                        var island = new RectIsland
                        {
                            renderer = renderer,
                            submesh = sub,
                            chartId = chart,
                            triangle = local,
                            uv0 = centroid,
                            uv = centroid,
                        };
                        ResolveTexture(ref island);
                        result.Add(island);
                    }
                }
            }
            return result;
        }

        /// <summary>三角形の重心か 3 頂点のどれかが、カメラの手前側で GUI 座標の矩形に入るか</summary>
        private static bool TriangleTouchesRect(Camera camera, Rect guiRect, Vector3[] vertices, int[] triangles, int i, Matrix4x4 localToWorld)
        {
            Vector3 w0 = localToWorld.MultiplyPoint3x4(vertices[triangles[i]]);
            Vector3 w1 = localToWorld.MultiplyPoint3x4(vertices[triangles[i + 1]]);
            Vector3 w2 = localToWorld.MultiplyPoint3x4(vertices[triangles[i + 2]]);
            return PointInRect(camera, guiRect, (w0 + w1 + w2) / 3f)
                || PointInRect(camera, guiRect, w0) || PointInRect(camera, guiRect, w1) || PointInRect(camera, guiRect, w2);
        }

        private static bool PointInRect(Camera camera, Rect guiRect, Vector3 world)
        {
            // カメラの後ろの点は矩形に入れない（投影が裏返る）
            Vector3 view = camera.WorldToViewportPoint(world);
            if (view.z <= 0f) return false;
            Vector2 gui = HandleUtility.WorldToGUIPoint(world);
            return guiRect.Contains(gui);
        }

        /// <summary>
        /// 島 ID 描画で集める。シェーダーが読めない・SV_PrimitiveID が使えない（シェーダーモデル 4.0 未満）・
        /// float の RT や読み戻しが使えない・RT が作れないときは false（呼び出し側がフォールバックする）
        /// </summary>
        private static bool TryPickByIdRender(Camera camera, Rect guiRect, IReadOnlyList<Renderer> renderers, ScenePicker picker, out List<RectIsland> islands)
        {
            islands = null;
            if (SystemInfo.graphicsShaderLevel < 40) return false;
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat)) return false;
            if (!SystemInfo.IsFormatSupported(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.ReadPixels)) return false;
            var shader = ShaderAssets.LoadShader(ShaderAssets.IslandIdGuid);
            if (shader == null || !shader.isSupported) return false;

            // GUI 座標（ポイント・左上原点）→ カメラの画面画素（左下原点）。pixelsPerPoint と y 反転はこの関数が面倒を見る
            Vector2 a = HandleUtility.GUIPointToScreenPixelCoordinate(guiRect.min);
            Vector2 b = HandleUtility.GUIPointToScreenPixelCoordinate(guiRect.max);
            Rect view = camera.pixelRect;
            float x0 = Mathf.Clamp(Mathf.Floor(Mathf.Min(a.x, b.x)), view.xMin, view.xMax);
            float x1 = Mathf.Clamp(Mathf.Ceil(Mathf.Max(a.x, b.x)), view.xMin, view.xMax);
            float y0 = Mathf.Clamp(Mathf.Floor(Mathf.Min(a.y, b.y)), view.yMin, view.yMax);
            float y1 = Mathf.Clamp(Mathf.Ceil(Mathf.Max(a.y, b.y)), view.yMin, view.yMax);
            int width = (int)(x1 - x0);
            int height = (int)(y1 - y0);
            if (width <= 0 || height <= 0 || view.width <= 0f || view.height <= 0f)
            {
                islands = new List<RectIsland>();
                return true;
            }

            // 画面全体を描いて一部を ReadPixels で読むと、DirectX で部分矩形の y の原点が全体読みと逆になる（RecolorSceneTool.IsSelectedAt の注記）。
            // そこで投影行列を矩形の範囲に絞り、矩形と同じ大きさの RT に描いて RT 全体を読む（読むのは矩形の範囲だけ）
            var projection = CropProjection(
                (x0 - view.x) / view.width * 2f - 1f, (x1 - view.x) / view.width * 2f - 1f,
                (y0 - view.y) / view.height * 2f - 1f, (y1 - view.y) / view.height * 2f - 1f) * camera.projectionMatrix;

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                hideFlags = HideFlags.HideAndDontSave,
                useMipMap = false,
                autoGenerateMips = false,
                antiAliasing = 1,
                filterMode = FilterMode.Point,
            };
            if (!rt.Create())
            {
                Object.DestroyImmediate(rt);
                return false;
            }

            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true) { hideFlags = HideFlags.HideAndDontSave };
            var previous = RenderTexture.active;
            float[] pixels;
            try
            {
                Graphics.SetRenderTarget(rt);
                GL.Viewport(new Rect(0, 0, width, height));
                // 何も描かれない画素は (-1, -1)（CollectIslands が無効として飛ばす）
                GL.Clear(true, true, new Color(-1f, -1f, 0f, 1f));
                GL.PushMatrix();
                try
                {
                    GL.LoadProjectionMatrix(projection);
                    GL.modelview = camera.worldToCameraMatrix;
                    DrawIds(material, renderers, picker);
                }
                finally
                {
                    GL.PopMatrix();
                }

                RenderTexture.active = rt;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                pixels = readback.GetRawTextureData<float>().ToArray();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(readback);
                Object.DestroyImmediate(material);
                rt.Release();
                Object.DestroyImmediate(rt);
            }

            islands = CollectIslands(pixels, width, height, renderers);
            return true;
        }

        /// <summary>
        /// クリップ空間で NDC の [x0,x1]×[y0,y1] を [-1,1]² に引き伸ばす行列（投影行列の左から掛ける）。
        /// x' = (x − cx·w) / sx（cx = 中心、sx = 半幅）。y も同様。z と w はそのまま
        /// </summary>
        private static Matrix4x4 CropProjection(float x0, float x1, float y0, float y1)
        {
            float sx = Mathf.Max((x1 - x0) * 0.5f, 1e-6f);
            float sy = Mathf.Max((y1 - y0) * 0.5f, 1e-6f);
            float cx = (x0 + x1) * 0.5f;
            float cy = (y0 + y1) * 0.5f;
            var m = Matrix4x4.identity;
            m.m00 = 1f / sx;
            m.m03 = -cx / sx;
            m.m11 = 1f / sy;
            m.m13 = -cy / sy;
            return m;
        }

        /// <summary>各 Renderer のサブメッシュを島 ID シェーダーで描く。SMR は ScenePicker と同じ焼きメッシュ＋TRS、MeshRenderer は localToWorldMatrix</summary>
        private static void DrawIds(Material material, IReadOnlyList<Renderer> renderers, ScenePicker picker)
        {
            for (int i = 0; i < renderers.Count; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;
                if (!picker.TryGetMeshData(renderer, out var mesh, out var localToWorld)) continue;
                int count = Mathf.Min(mesh.subMeshCount, SubmeshStride);
                for (int submesh = 0; submesh < count; submesh++)
                {
                    if (mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                    material.SetFloat(RendererIndexId, i * SubmeshStride + submesh);
                    material.SetPass(0);
                    Graphics.DrawMeshNow(mesh, localToWorld, submesh);
                }
            }
        }

        /// <summary>島を決めるために Renderer 番号×サブメッシュごとに 1 回だけ取る情報</summary>
        private sealed class SubmeshInfo
        {
            public Mesh mesh;
            public ChartTable table;
            public int[] triangles;
            /// <summary>メッシュの UV0（島の代表 UV の計算用）</summary>
            public Vector2[] uv;
            /// <summary>サブメッシュ内の三角形数</summary>
            public int localCount;
            /// <summary>メッシュ全体の三角形番号でのサブメッシュの先頭（インデックスの開始位置 / 3）</summary>
            public int offset;
        }

        /// <summary>
        /// 読み戻した島 ID 画素（width×height、1 画素 = RGBA の float 4 つ。R = Renderer 番号×256＋サブメッシュ、G = 三角形番号）を
        /// 島の集合に変換する。R か G が負の画素（何も描かれていない）・範囲外の番号・チャート表が作れないメッシュは飛ばす。
        /// 同じ (Renderer, サブメッシュ, チャート) は 1 つにまとめ、代表は最初に見つかった三角形（uv0 はその UV0 の重心）。
        ///
        /// 注意: DrawMeshNow(mesh, matrix, submesh) の SV_PrimitiveID は描画呼び出しごとに 0 から数える（サブメッシュ内の番号）はずだが、
        /// メッシュ全体の番号になる環境があるかもしれない（Unity 実機で要確認）。そこで、サブメッシュの三角形数以上の番号が
        /// 「全体の番号 − サブメッシュの先頭」でちょうど収まる画素が 1 つでもあれば、この読み戻し全体を全体番号として読み替える。
        /// サブメッシュ 0 だけのメッシュや、先頭が小さく区別がつかない場合はサブメッシュ内の番号として扱う
        /// </summary>
        internal static List<RectIsland> CollectIslands(float[] pixels, int width, int height, IReadOnlyList<Renderer> renderers)
        {
            var result = new List<RectIsland>();
            if (pixels == null || renderers == null) return result;
            int count = Mathf.Min(width * height, pixels.Length / 4);
            var infos = new Dictionary<int, SubmeshInfo>();

            // 1 周目: 三角形番号が全体番号か判定する
            bool global = false;
            for (int p = 0; p < count && !global; p++)
            {
                if (!TryDecode(pixels, p, out int code, out int primitive)) continue;
                var info = GetInfo(infos, code, renderers);
                if (info == null || info.offset == 0) continue;
                if (primitive >= info.localCount && primitive - info.offset >= 0 && primitive - info.offset < info.localCount) global = true;
            }

            // 2 周目: (Renderer, サブメッシュ, チャート) ごとに 1 つ
            var seen = new HashSet<(int code, int chart)>();
            for (int p = 0; p < count; p++)
            {
                if (!TryDecode(pixels, p, out int code, out int primitive)) continue;
                var info = GetInfo(infos, code, renderers);
                if (info == null) continue;
                int local = global ? primitive - info.offset : primitive;
                if (local < 0 || local >= info.localCount || local >= info.table.chartOfTriangle.Length) continue;
                int chart = info.table.chartOfTriangle[local];
                if (!seen.Add((code, chart))) continue;

                var uvs = info.uv;
                int t3 = local * 3;
                Vector2 centroid = (uvs[info.triangles[t3]] + uvs[info.triangles[t3 + 1]] + uvs[info.triangles[t3 + 2]]) / 3f;
                var island = new RectIsland
                {
                    renderer = renderers[code / SubmeshStride],
                    submesh = code % SubmeshStride,
                    chartId = chart,
                    triangle = local,
                    uv0 = centroid,
                    uv = centroid,
                };
                ResolveTexture(ref island);
                result.Add(island);
            }
            return result;
        }

        private static bool TryDecode(float[] pixels, int p, out int code, out int primitive)
        {
            float r = pixels[p * 4];
            float g = pixels[p * 4 + 1];
            code = Mathf.RoundToInt(r);
            primitive = Mathf.RoundToInt(g);
            return r >= 0f && g >= 0f;
        }

        private static SubmeshInfo GetInfo(Dictionary<int, SubmeshInfo> infos, int code, IReadOnlyList<Renderer> renderers)
        {
            if (infos.TryGetValue(code, out var cached)) return cached;
            SubmeshInfo info = null;
            int rendererIndex = code / SubmeshStride;
            int submesh = code % SubmeshStride;
            if (rendererIndex < renderers.Count && renderers[rendererIndex] != null)
            {
                var mesh = RendererMeshAccess.GetSharedMesh(renderers[rendererIndex]);
                // チャート表はメッシュの三角形の並びで作る。SMR の焼きメッシュは元メッシュと三角形の並びが同じなので元メッシュで引いてよい
                var table = UvChartDetector.GetOrBuild(mesh, submesh);
                if (table != null)
                {
                    var triangles = mesh.GetTriangles(submesh);
                    info = new SubmeshInfo
                    {
                        mesh = mesh,
                        table = table,
                        triangles = triangles,
                        uv = mesh.uv, // GetOrBuild が UV の長さを確かめ済み
                        localCount = triangles.Length / 3,
                        offset = (int)(mesh.GetIndexStart(submesh) / 3),
                    };
                }
            }
            infos[code] = info; // 取れなかったものも覚えて、画素ごとに作り直さない
            return info;
        }

        /// <summary>
        /// 島のマテリアル（Unity と同じく余ったサブメッシュは最後のマテリアル。枠がサブメッシュより多ければ、最後のサブメッシュを重ね描きする枠の
        /// うちテクスチャのある枠。ScenePicker.ChooseOverdrawSlot）のメインテクスチャと、Tiling/Offset 適用後の UV を入れる
        /// </summary>
        private static void ResolveTexture(ref RectIsland island)
        {
            var materials = island.renderer.sharedMaterials;
            if (materials.Length == 0) return;
            int slot = ScenePicker.ChooseOverdrawSlot(island.renderer, island.submesh, materials, Mathf.Min(island.submesh, materials.Length - 1));
            var material = materials[slot];
            if (!MaterialTextureResolver.TryGetMainTexture(material, out var info)) return;
            island.texture = info.texture;
            island.uv = MaterialTextureResolver.ToTextureCoord(info, island.uv0);
        }

        /// <summary>
        /// フォールバック: 矩形内に 8 画素間隔（大きな矩形では上限までレイ数を減らす）の格子でレイを飛ばし、
        /// 当たった (Renderer, サブメッシュ, チャート) を集める。格子の隙間に収まる小さな島は拾えないことがある
        /// </summary>
        private static List<RectIsland> PickByRays(Rect guiRect, IReadOnlyList<Renderer> renderers, ScenePicker picker)
        {
            var result = new List<RectIsland>();
            float step = FallbackStepPixels / Mathf.Max(EditorGUIUtility.pixelsPerPoint, 1e-3f);
            float columns = guiRect.width / step + 1f;
            float rows = guiRect.height / step + 1f;
            if (columns * rows > FallbackMaxRays) step *= Mathf.Sqrt(columns * rows / FallbackMaxRays);

            var seen = new HashSet<(Renderer renderer, int submesh, int chart)>();
            for (float y = guiRect.yMin; y <= guiRect.yMax; y += step)
            {
                for (float x = guiRect.xMin; x <= guiRect.xMax; x += step)
                {
                    var ray = HandleUtility.GUIPointToWorldRay(new Vector2(x, y));
                    if (!picker.TryPick(ray, renderers, null, out var hit)) continue;
                    var table = UvChartDetector.GetOrBuild(RendererMeshAccess.GetSharedMesh(hit.renderer), hit.subMeshIndex);
                    int chart = table != null && hit.triangleIndex >= 0 && hit.triangleIndex < table.chartOfTriangle.Length
                        ? table.chartOfTriangle[hit.triangleIndex]
                        : -1;
                    // チャートが取れないときは三角形ごとに別扱い（負の番号にして本来のチャート番号と混ざらないようにする）
                    if (!seen.Add((hit.renderer, hit.subMeshIndex, chart >= 0 ? chart : -1 - hit.triangleIndex))) continue;
                    result.Add(new RectIsland
                    {
                        renderer = hit.renderer,
                        submesh = hit.subMeshIndex,
                        chartId = chart,
                        triangle = hit.triangleIndex,
                        uv0 = hit.uv0,
                        uv = hit.hasMainTexture ? hit.uv : hit.uv0,
                        texture = hit.hasMainTexture ? hit.mainTexture.texture : null,
                    });
                }
            }
            return result;
        }
    }
}
