using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.NDMF;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// グラデーションの箱（RecolorEdit.gradientBox*。対象ルート＝ClickRecolor の GameObject のローカル座標）の既定値と ON/OFF
    /// </summary>
    internal static class GradientBox
    {
        /// <summary>既定の箱の各軸の大きさの下限（平たいメッシュでも箱が潰れないように）</summary>
        internal const float MinSize = 0.05f;

        /// <summary>選択範囲から作る箱の各軸の大きさの下限</summary>
        internal const float SelectionMinSize = 0.02f;

        /// <summary>選択範囲から作る箱を各軸に広げる割合（選択の端の色も箱の端まで届かせる）</summary>
        internal const float SelectionMargin = 0.1f;

        /// <summary>選択範囲から箱を作るのに要る画素の数の下限（これ未満なら Renderer の bounds にする）</summary>
        internal const int MinSelectionTexels = 16;

        /// <summary>選択範囲の位置を集計するときに読み戻す大きさ（長辺）の上限</summary>
        private const int MaxReadbackSize = 256;

        private const float MaskThreshold = 0.5f;

        /// <summary>
        /// 既定の箱: 現在の選択範囲（マスク &gt; 0.5 のテクセル。連結なら全メンバーの分）に写る表面の位置（対象ルートのローカル）の AABB の中心、
        /// 大きさは AABB を各軸 SelectionMargin だけ広げたもの（各軸 SelectionMinSize 以上）、回転なし。
        /// centroid は同じ画素の位置の平均（箱の中心には使わない）。
        /// 有効な画素が MinSelectionTexels 未満・マスクや位置マップが作れない・compute が使えないときは FallbackFor（Renderer の bounds）
        /// </summary>
        internal static (Vector3 position, Quaternion rotation, Vector3 size, Vector3 centroid) DefaultFor(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null) return (Vector3.zero, Quaternion.identity, Vector3.one, Vector3.zero);
            if (TryGetSelectionBox(component, edit, out var position, out var size, out var centroid))
            {
                return (position, Quaternion.identity, size, centroid);
            }
            var fallback = FallbackFor(component, edit);
            return (fallback.position, fallback.rotation, fallback.size, fallback.position);
        }

        /// <summary>
        /// 選択範囲の位置から箱を作る。作れなければ false。
        /// 連結なら全メンバー（EditGroups.Members）の選択範囲を合わせる（テクスチャごとにマスクと位置マップを作って集計する）。
        /// PrepareJob の約束どおり、マスクを作ったら使い終えた後に MaskCache.Trim を 1 回呼ぶ（メンバーごと）
        /// </summary>
        private static bool TryGetSelectionBox(
            ClickRecolor component, RecolorEdit edit, out Vector3 position, out Vector3 size, out Vector3 centroid)
        {
            position = Vector3.zero;
            size = Vector3.one;
            centroid = Vector3.zero;
            if (edit == null) return false;
            if (!SystemInfo.supportsComputeShaders || !PositionMap.IsAvailable) return false;

            var root = component.gameObject.transform;
            var renderers = RecolorSceneTool.CollectPreviewRenderers(component.gameObject);

            Vector3 min = Vector3.positiveInfinity;
            Vector3 max = Vector3.negativeInfinity;
            Vector3 sum = Vector3.zero;
            int count = 0;
            foreach (var member in EditGroups.Members(component, edit))
            {
                Accumulate(root, renderers, component, member, ref min, ref max, ref sum, ref count);
            }
            if (count < MinSelectionTexels) return false;

            var extent = (max - min) * (1f + SelectionMargin);
            position = (min + max) * 0.5f;
            size = new Vector3(
                Mathf.Max(extent.x, SelectionMinSize),
                Mathf.Max(extent.y, SelectionMinSize),
                Mathf.Max(extent.z, SelectionMinSize));
            centroid = sum / count;
            return true;
        }

        /// <summary>
        /// 編集 edit の選択範囲（マスク &gt; 0.5 のテクセル）に写る表面の位置（対象ルートのローカル）を min / max / sum / count に足す。
        /// マスクや位置マップが作れなければ何もしない
        /// </summary>
        private static void Accumulate(
            Transform root, System.Collections.Generic.List<Renderer> renderers, ClickRecolor component, RecolorEdit edit,
            ref Vector3 min, ref Vector3 max, ref Vector3 sum, ref int count)
        {
            var texture = edit?.sourceTexture;
            if (texture == null) return;
            var users = RecolorPreview.CollectUsers(renderers, texture);
            // このテクスチャを使うスロットが無ければ位置マップも描けない
            if (users.Count == 0) return;

            var job = RecolorPipeline.PrepareJob(edit, texture, (int)component.previewResolution, users,
                context: MaskContext.For(component, renderers));
            try
            {
                var mask = job?.mask;
                if (mask == null) return;
                // 位置マップはキャッシュ所有なので解放しない。膨張は掛けない（描かれた画素＝実際の表面だけを数える）
                var positions = PositionMap.GetOrBuild(root, renderers, texture, mask.width, mask.height, fillIterations: 0);
                if (positions == null) return;

                ReadbackDownscaled(positions, mask, MaxReadbackSize, out var positionPixels, out var maskValues);

                int n = Mathf.Min(positionPixels.Length, maskValues.Length);
                for (int i = 0; i < n; i++)
                {
                    var p = positionPixels[i];
                    // A = 0 は表面が描かれていない画素（パディングで広げた縁など）
                    if (!(maskValues[i] > MaskThreshold) || !(p.a > 0f)) continue;
                    var local = new Vector3(p.r, p.g, p.b);
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                    sum += local;
                    count++;
                }
            }
            finally
            {
                if (job != null) MaskCache.Trim();
            }
        }

        /// <summary>
        /// positions（ARGBFloat の位置マップ）と mask（R8）を、長辺 maxSize 以下の同じ大きさに縮めて CPU へ読み戻す
        /// （SelectionStats.ReadbackDownscaled と同じ流儀。位置は精度を落とさないよう Float のまま読む）。
        /// 位置マップは Point フィルタなので、縮めても描いた画素と描いていない画素の値が混ざらない
        /// </summary>
        private static void ReadbackDownscaled(
            RenderTexture positions, RenderTexture mask, int maxSize, out Color[] positionPixels, out float[] maskValues)
        {
            float scale = Mathf.Min(1f, maxSize / (float)Mathf.Max(positions.width, positions.height));
            int w = Mathf.Max(1, Mathf.RoundToInt(positions.width * scale));
            int h = Mathf.Max(1, Mathf.RoundToInt(positions.height * scale));

            var positionDesc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGBFloat, 0)
            {
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
            };
            var positionRt = RenderTexture.GetTemporary(positionDesc);
            var maskRt = MaskTextures.GetTemporary(w, h);
            var positionTex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            // Blit / ReadPixels は RenderTexture.active を切り替えたまま戻さないので、最後に元へ戻す
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(positions, positionRt);
                Graphics.Blit(mask, maskRt);

                RenderTexture.active = positionRt;
                positionTex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                positionTex.Apply(false);
                positionPixels = positionTex.GetPixels();

                var maskBytes = FloodFill.ReadR8(maskRt);
                maskValues = new float[maskBytes.Length];
                for (int i = 0; i < maskBytes.Length; i++) maskValues[i] = maskBytes[i] / 255f;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(positionTex);
                RenderTexture.ReleaseTemporary(positionRt);
                RenderTexture.ReleaseTemporary(maskRt);
            }
        }

        /// <summary>
        /// 従来の既定の箱: 主種の Renderer の bounds（ワールドの AABB）を対象ルートのローカルに変換した AABB の中心と大きさ（各軸 MinSize 以上）、回転なし。
        /// 主種の Renderer が無い（「アバター全体」の連結の他テクスチャ分など）ときは、ルート配下の Renderer 全部の bounds を使う。
        /// それも無ければ原点・大きさ 1
        /// </summary>
        internal static (Vector3 position, Quaternion rotation, Vector3 size) FallbackFor(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null) return (Vector3.zero, Quaternion.identity, Vector3.one);
            if (!TryGetWorldBounds(component, edit, out var world)) return (Vector3.zero, Quaternion.identity, Vector3.one);

            // ワールドの AABB の 8 隅をルートのローカルへ写し、その AABB を取る（ルートが回っていても箱が全体を囲む）
            var root = component.transform;
            Vector3 min = Vector3.positiveInfinity;
            Vector3 max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? world.min.x : world.max.x,
                    (i & 2) == 0 ? world.min.y : world.max.y,
                    (i & 4) == 0 ? world.min.z : world.max.z);
                var local = root.InverseTransformPoint(corner);
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }
            var size = max - min;
            size = new Vector3(Mathf.Max(size.x, MinSize), Mathf.Max(size.y, MinSize), Mathf.Max(size.z, MinSize));
            return ((min + max) * 0.5f, Quaternion.identity, size);
        }

        private static bool TryGetWorldBounds(ClickRecolor component, RecolorEdit edit, out Bounds bounds)
        {
            if (edit != null && edit.seedRenderer != null)
            {
                bounds = edit.seedRenderer.bounds;
                return true;
            }
            bounds = default;
            bool found = false;
            foreach (var renderer in component.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer || renderer is MeshRenderer)) continue;
                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            return found;
        }

        /// <summary>
        /// グラデーションの ON/OFF を変える（Undo 記録つき）。初めて ON にしたときだけ初期値（色 2・箱は既定 DefaultFor）を入れ、
        /// OFF→ON では前の内容のまま（ユーザー要望 2026-10-05）。
        /// 「アバター全体」の連結なら連結の全編集に同じ値を入れる（箱は現在の編集から決める）
        /// </summary>
        internal static void SetEnabled(ClickRecolor component, RecolorEdit edit, bool enabled)
        {
            if (component == null || edit == null || edit.gradientEnabled == enabled) return;
            Undo.RecordObject(component, "Tocolo: グラデーションを切り替え");
            bool initialize = enabled && !edit.gradientInitialized;
            var box = initialize ? DefaultFor(component, edit) : default;
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.gradientEnabled = enabled;
                // OFF にした編集も初期化済みにする（この印が無い版で ON にした編集を、次の ON で初期化し直さない）
                e.gradientInitialized = true;
                if (initialize) Initialize(e, box);
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>パネルの［リセット］: 初めて ON にしたときと同じ初期値（色 2・箱）に戻す（Undo 記録つき。連結なら連結の全編集）</summary>
        internal static void ResetSettings(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null) return;
            Undo.RecordObject(component, "Tocolo: グラデーションをリセット");
            var box = DefaultFor(component, edit);
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.gradientInitialized = true;
                Initialize(e, box);
            });
            EditorUtility.SetDirty(component);
        }

        private static void Initialize(RecolorEdit e, (Vector3 position, Quaternion rotation, Vector3 size, Vector3 centroid) box)
        {
            // 終了色の初期値は「元の色」（ユーザー要望 2026-09-25。新しい色→元の色へ戻るグラデーションが既定）
            e.gradientColor = e.seedColor;
            // 色 2 の陰影の暗さ・強さは色 1 の値から始める（ON にした直後の見た目を変えない）
            e.gradientDarkEndRatio = e.darkEndRatio;
            e.gradientGamma = e.gamma;
            e.gradientStrength = e.strength;
            e.gradientBoxPosition = box.position;
            e.gradientBoxRotation = box.rotation;
            e.gradientBoxSize = box.size;
        }
    }
}
