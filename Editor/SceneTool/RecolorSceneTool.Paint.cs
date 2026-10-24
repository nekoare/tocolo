using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// なぞって調整（設計 docs/plans/2026-10-25-tocolo-paint-select-design.md）: パーツで選んだ範囲を、Scene でなぞって三角形ごとに足す・消す。
    /// 道具（ToolSession.Brush）を持っている間は、左ドラッグがなぞりになる（パーツの選び直し・新しい編集・矩形選択は効かない。スポイトと同じ）。
    /// なぞっている間はプレビューを作り直さず、なぞった面を Scene に色を付けて重ねて見せ、離したときに 1 回だけ作り直す
    /// （範囲と統計の作り直しは 1 回 100ms 以上かかり、なぞりながら作り直すと筆が止まる）
    /// </summary>
    internal sealed partial class RecolorSceneTool
    {
        private static readonly Color BrushAddColor = new Color(0.45f, 1f, 0.55f, 1f);
        private static readonly Color BrushRemoveColor = new Color(1f, 0.45f, 0.45f, 1f);
        private static readonly Color PaintedAddColor = new Color(0.3f, 1f, 0.4f, 0.45f);
        private static readonly Color PaintedRemoveColor = new Color(1f, 0.3f, 0.3f, 0.45f);
        private const int BrushCircleSegments = 48;

        private const string BrushUndoName = "Tocolo: なぞって範囲を調整";

        private bool _brushStroke;
        private Vector2 _brushLast;
        /// <summary>このなぞりで、今の編集のテクスチャでない面に触れたか（何も塗れなかったら、離したときに知らせる）</summary>
        private bool _brushSkippedOtherTexture;
        private int _brushUndoGroup;
        private ClickRecolor _brushComponent;
        /// <summary>このなぞりで描き換えた編集の id（離したときに保存する）</summary>
        private readonly HashSet<string> _brushEdits = new HashSet<string>();
        /// <summary>このなぞりで触れた面と、最後に塗った値（なぞっている間に Scene に重ねて見せる）</summary>
        private readonly Dictionary<(Renderer renderer, int submesh, int triangle), byte> _brushPainted =
            new Dictionary<(Renderer, int, int), byte>();

        private static Material s_paintedMaterial;

        /// <summary>
        /// なぞって調整ができる編集か: パーツモード（テクスチャ全体に広げていない）で、「アバター全体」の連結でない
        /// （アバター全体は色で選ぶ連結で、パーツの範囲を持たない）
        /// </summary>
        internal static bool CanPaint(RecolorEdit edit) =>
            edit != null && edit.sourceTexture != null && RecolorPipeline.UsesPaintAdjust(edit) && !EditGroups.IsWholeAvatarGroup(edit);

        /// <summary>
        /// 道具を持っている間の Scene の入力。なぞりの押す・動かす・離す、筆の円となぞった面の表示、ホバーの代わりの再描画を受け持つ。
        /// 処理した（以降のクリック・ホバーの処理をしない）なら true
        /// </summary>
        private bool HandleBrush(Event e, SceneView sceneView, GameObject root, int controlId)
        {
            if (ToolSession.Brush == BrushMode.None && !_brushStroke) return false;

            if (!_brushStroke)
            {
                // 選んでいる編集がパーツモードでなくなった・無くなったら道具を置く（Undo・一覧から別の編集を選んだときなど）
                var component = root.GetComponent<ClickRecolor>();
                if (!CanPaint(component != null ? component.FindEdit(ToolSession.CurrentEditId) : null))
                {
                    ToolSession.Brush = BrushMode.None;
                    sceneView.Repaint();
                    return false;
                }
            }

            switch (e.type)
            {
                case EventType.MouseMove:
                    // ホバーの丸の代わりに筆の円を動かす
                    ToolSession.HoverPick = null;
                    sceneView.Repaint();
                    return true;
                case EventType.Repaint:
                    DrawPaintedTriangles();
                    DrawBrushCircle(e.mousePosition, StrokeState(e));
                    // 後に続くクリックの印（LastPick）も描かせる（今どのパーツの編集を調整しているかの目印になる）
                    return false;
                case EventType.MouseDown when e.button == 0 && !e.alt && !_brushStroke:
                    e.Use();
                    BeginBrushStroke(root, controlId, e.mousePosition, StrokeState(e), sceneView);
                    return true;
            }

            if (!_brushStroke) return false;
            int pressId = s_capturedControl != 0 ? s_capturedControl : controlId;
            var type = e.GetTypeForControl(pressId);
            if (type == EventType.MouseDrag && GUIUtility.hotControl == pressId)
            {
                e.Use();
                PaintAt(sceneView, _brushLast, e.mousePosition, ToolSession.BrushRadius, StrokeState(e));
                _brushLast = e.mousePosition;
                sceneView.Repaint();
                return true;
            }
            // 離し: 先に他の部品が使った（Used）離しも rawType で拾う
            if ((type == EventType.MouseUp || e.rawType == EventType.MouseUp) && e.button == 0)
            {
                if (GUIUtility.hotControl == pressId) e.Use();
                FinishBrushStroke();
                sceneView.Repaint();
                return true;
            }
            return false;
        }

        /// <summary>このイベントで塗る値: 道具が「足す」なら足す、Ctrl（Mac は Command）を押している間だけ逆にする</summary>
        private static byte StrokeState(Event e)
        {
            bool add = ToolSession.Brush != BrushMode.Remove;
            if (e.control || e.command) add = !add;
            return add ? PaintState.Add : PaintState.Remove;
        }

        private void BeginBrushStroke(GameObject root, int controlId, Vector2 position, byte state, SceneView sceneView)
        {
            var component = root.GetComponent<ClickRecolor>();
            if (component == null) return;
            if (_picker == null) _picker = new ScenePicker(); // ドメインリロード直後の保険
            CollectPickRenderers(root);
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(BrushUndoName);
            _brushUndoGroup = Undo.GetCurrentGroup();
            _brushComponent = component;
            _brushEdits.Clear();
            _brushPainted.Clear();
            _brushStroke = true;
            _brushLast = position;
            _brushSkippedOtherTexture = false;
            GUIUtility.hotControl = controlId;
            s_capturedControl = controlId;
            PaintAt(sceneView, position, position, ToolSession.BrushRadius, state);
            sceneView.Repaint();
        }

        /// <summary>
        /// 筆が from から to まで動いた帯に触れた見えている三角形のうち、今の編集（島の連結ならそのテクスチャのメンバー）の
        /// テクスチャを使う三角形を、手元の調整の画像に塗る（速く動かしても間が抜けないよう、点ではなく帯で拾う）
        /// </summary>
        private void PaintAt(SceneView sceneView, Vector2 from, Vector2 to, float radius, byte state)
        {
            if (_brushComponent == null || sceneView == null) return;
            var edit = _brushComponent.FindEdit(ToolSession.CurrentEditId);
            if (!CanPaint(edit)) return;
            var members = new List<RecolorEdit>();
            EditGroups.ForEachInGroup(_brushComponent, edit, e => { if (CanPaint(e)) members.Add(e); });

            var hits = IslandRectPicker.PickTriangles(sceneView.camera, from, to, radius, _renderers, _picker);
            foreach (var hit in hits)
            {
                if (!TryGetTriangleTexture(hit, out var info)) continue;
                var member = members.Find(m => m.sourceTexture == info.texture);
                if (member == null)
                {
                    // 別のテクスチャの面は塗らない（調整はテクスチャごとの UV の画像なので、その編集の範囲に入れようがない）。離したときに知らせる
                    _brushSkippedOtherTexture = true;
                    continue;
                }
                if (!_picker.TryGetMeshArrays(hit.renderer, out var arrays, out _)) continue;
                if (hit.submesh < 0 || hit.submesh >= arrays.triangles.Length || arrays.triangles[hit.submesh] == null) continue;
                var triangles = arrays.triangles[hit.submesh];
                int t3 = hit.triangle * 3;
                if (t3 + 2 >= triangles.Length) continue;

                var size = PaintAdjustImage.SizeFor(member.sourceTexture.width, member.sourceTexture.height);
                var image = PaintAdjustStore.BeginLive(member, size.x, size.y);
                if (image == null) continue;
                // テクスチャ座標は折り返さない（範囲のマスク〔UvRasterizer〕と同じ。0..1 の外へ出た部分は塗らない）
                var scale = info.scale == Vector2.zero ? Vector2.one : info.scale;
                var a = Vector2.Scale(arrays.uv[triangles[t3]], scale) + info.offset;
                var b = Vector2.Scale(arrays.uv[triangles[t3 + 1]], scale) + info.offset;
                var c = Vector2.Scale(arrays.uv[triangles[t3 + 2]], scale) + info.offset;
                if (PaintAdjustRaster.PaintTriangle(image, a, b, c, state)) _brushEdits.Add(member.id);
                _brushPainted[(hit.renderer, hit.submesh, hit.triangle)] = state;
            }
        }

        /// <summary>三角形を描くマテリアル枠（余った枠の重ね描きは ScenePicker.ChooseOverdrawSlot と同じ選び方）のメインテクスチャ</summary>
        private static bool TryGetTriangleTexture(in BrushTriangle hit, out MainTextureInfo info)
        {
            info = default;
            if (hit.renderer == null) return false;
            var materials = hit.renderer.sharedMaterials;
            if (materials.Length == 0) return false;
            int slot = ScenePicker.ChooseOverdrawSlot(hit.renderer, hit.submesh, materials, Mathf.Min(hit.submesh, materials.Length - 1));
            return MaterialTextureResolver.TryGetMainTexture(materials[slot], out info) && info.texture != null;
        }

        /// <summary>
        /// なぞりを終える: 描き換えた編集に調整を保存し（Undo 1 回。ここでプレビューが作り直される）、掴んだマウスを放す。
        /// 離したとき・Esc・ツールの終了・掴みをほかに取られたときに呼ぶ（途中で終わっても、そこまでなぞった分は残す）
        /// </summary>
        private void FinishBrushStroke()
        {
            if (!_brushStroke)
            {
                // なぞり中の印が無いのに手元の画像が残っている（ツールのインスタンスが替わった等）なら捨てる。
                // 残すと番号が合う間、保存していない画像をプレビューが使い続ける
                if (PaintAdjustStore.HasLive) PaintAdjustStore.DiscardLive();
                return;
            }
            _brushStroke = false;
            // 髪の縁をなぞれば顔や体（別のテクスチャ）にも筆が掛かるのはふつうなので、何も塗れなかったときだけ理由を知らせる
            if (_brushSkippedOtherTexture && _brushPainted.Count == 0) ToolSession.ShowTransientNotice("Scene:Paint:OtherTexture");
            _brushSkippedOtherTexture = false;
            if (s_capturedControl != 0 && GUIUtility.hotControl == s_capturedControl) GUIUtility.hotControl = 0;
            s_capturedControl = 0;
            var component = _brushComponent;
            _brushComponent = null;
            if (component != null && _brushEdits.Count > 0)
            {
                Undo.RecordObject(component, BrushUndoName);
                foreach (var id in _brushEdits) PaintAdjustStore.Commit(component.FindEdit(id));
                EditorUtility.SetDirty(component);
                Undo.CollapseUndoOperations(_brushUndoGroup);
            }
            _brushEdits.Clear();
            _brushPainted.Clear();
            // 保存しなかった手元の画像（編集が消えた等）は捨てる。残すと、次のなぞりで古い画像から描き始めてしまう
            PaintAdjustStore.DiscardLive();
        }

        /// <summary>
        /// なぞっている間、触れた面を足す＝緑・消す＝赤で重ねて描く（プレビューは離したときに作り直すので、それまでの手応え）。
        /// 奥行きは比べずに前面に描く: 筆が塗るのは見えている面だけで、なぞっている間はカメラも動かないので、隠れた面に色が乗ることはない
        /// </summary>
        private void DrawPaintedTriangles()
        {
            if (!_brushStroke || _brushPainted.Count == 0 || _picker == null) return;
            var material = PaintedMaterial;
            if (material == null) return;
            Renderer current = null;
            MeshRaycaster.MeshArrays arrays = null;
            bool open = false;
            foreach (var pair in _brushPainted)
            {
                var (renderer, submesh, triangle) = pair.Key;
                if (renderer != current)
                {
                    if (open)
                    {
                        GL.End();
                        GL.PopMatrix();
                        open = false;
                    }
                    current = renderer;
                    if (renderer == null || !_picker.TryGetMeshArrays(renderer, out arrays, out var localToWorld))
                    {
                        arrays = null;
                        continue;
                    }
                    GL.PushMatrix();
                    GL.MultMatrix(localToWorld);
                    material.SetPass(0);
                    GL.Begin(GL.TRIANGLES);
                    open = true;
                }
                if (arrays == null || submesh < 0 || submesh >= arrays.triangles.Length) continue;
                var triangles = arrays.triangles[submesh];
                int t3 = triangle * 3;
                if (triangles == null || t3 + 2 >= triangles.Length) continue;
                GL.Color(pair.Value == PaintState.Add ? PaintedAddColor : PaintedRemoveColor);
                GL.Vertex(arrays.vertices[triangles[t3]]);
                GL.Vertex(arrays.vertices[triangles[t3 + 1]]);
                GL.Vertex(arrays.vertices[triangles[t3 + 2]]);
            }
            if (open)
            {
                GL.End();
                GL.PopMatrix();
            }
        }

        /// <summary>なぞった面の重ね描き用（頂点色・半透明・両面・前面）</summary>
        private static Material PaintedMaterial
        {
            get
            {
                if (s_paintedMaterial != null) return s_paintedMaterial;
                var shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) return null;
                s_paintedMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                s_paintedMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                s_paintedMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                s_paintedMaterial.SetInt("_Cull", (int)CullMode.Off);
                s_paintedMaterial.SetInt("_ZWrite", 0);
                s_paintedMaterial.SetInt("_ZTest", (int)CompareFunction.Always);
                return s_paintedMaterial;
            }
        }

        /// <summary>
        /// ブラシの太さのスライダーを動かしている間の円。最後にクリックした所（無ければ Scene の真ん中）のまわりに、ブラシと同じ大きさ・色で描く。
        /// クリックした所がカメラの後ろにあるときは真ん中に出す
        /// </summary>
        private static void DrawBrushSizePreview(SceneView sceneView)
        {
            Vector2 center = sceneView.position.size * 0.5f;
            if (ToolSession.LastPick.HasValue)
            {
                var world = ToolSession.LastPick.Value.worldPosition;
                if (sceneView.camera != null && sceneView.camera.WorldToViewportPoint(world).z > 0f) center = HandleUtility.WorldToGUIPoint(world);
            }
            DrawBrushCircle(center, ToolSession.Brush == BrushMode.Remove ? PaintState.Remove : PaintState.Add);
        }

        /// <summary>筆の円（足すは緑、消すは赤。背景に紛れないよう黒の縁を下に敷く）</summary>
        private static void DrawBrushCircle(Vector2 center, byte state)
        {
            float radius = ToolSession.BrushRadius;
            var points = new Vector3[BrushCircleSegments + 1];
            for (int i = 0; i <= BrushCircleSegments; i++)
            {
                float angle = i * Mathf.PI * 2f / BrushCircleSegments;
                points[i] = new Vector3(center.x + Mathf.Cos(angle) * radius, center.y + Mathf.Sin(angle) * radius, 0f);
            }
            Handles.BeginGUI();
            var previous = Handles.color;
            try
            {
                Handles.color = new Color(0f, 0f, 0f, 0.6f);
                Handles.DrawAAPolyLine(4f, points);
                Handles.color = state == PaintState.Add ? BrushAddColor : BrushRemoveColor;
                Handles.DrawAAPolyLine(2f, points);
            }
            finally
            {
                Handles.color = previous;
                Handles.EndGUI();
            }
        }
    }
}
