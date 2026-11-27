using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>グラデーション「毛先から」の長さを、Scene の髪をクリック・なぞって決める</summary>
    internal sealed partial class RecolorSceneTool
    {
        /// <summary>なぞっている間の Undo のまとまり（-1 なら押していない）</summary>
        private int _tipPickUndoGroup = -1;

        /// <summary>
        /// なぞっている間だけ持つ、Renderer ごとの頂点の毛先の値と、サブメッシュごとの三角形。
        /// 押すたびに作り直す（押している間は姿勢もてっぺんも変わらないので、動かすたびに計算し直さない）
        /// </summary>
        private readonly Dictionary<(Renderer, Texture2D), TipSample> _tipSamples = new Dictionary<(Renderer, Texture2D), TipSample>();

        private sealed class TipSample
        {
            public TipDistance.Result result;
            public int[][] triangles;
        }

        /// <summary>
        /// 長さを選んでいる間の左クリック・ドラッグ・離しを受ける。受けたら true（ほかの処理に回さない）。
        /// 押した時点で Undo のまとまりを始め、なぞっている間の変更を 1 回の Undo にまとめる。離したら選び中を終える
        /// </summary>
        private bool HandleTipPick(Event e, SceneView sceneView, GameObject root, int controlId)
        {
            bool pressing = _tipPickUndoGroup >= 0;
            if (!ToolSession.TipPickActive && !pressing) return false;
            // 編集を選び直した・箱に戻した・グラデーションを切ったら、選び中のボタンだけ押されたまま残らないようにやめる
            if (!pressing && !CurrentEditIsTips(root))
            {
                EndTipPick();
                return false;
            }
            if (ToolSession.TipPickActive)
            {
                Cursor.SetCursor(CrosshairCursor, new Vector2(CrosshairSize * 0.5f, CrosshairSize * 0.5f), CursorMode.Auto);
                EditorGUIUtility.AddCursorRect(new Rect(0f, 0f, sceneView.position.width, sceneView.position.height), MouseCursor.CustomCursor);
            }

            if (!pressing)
            {
                if (e.type != EventType.MouseDown || e.button != 0 || e.alt) return false;
                e.Use();
                GUIUtility.hotControl = controlId;
                s_capturedControl = controlId;
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Tocolo: 毛先からの長さを変更");
                _tipPickUndoGroup = Undo.GetCurrentGroup();
                _tipSamples.Clear();
                ApplyTipPick(root, e.mousePosition);
                sceneView.Repaint();
                return true;
            }

            int pressId = s_capturedControl != 0 ? s_capturedControl : controlId;
            var type = e.GetTypeForControl(pressId);
            if (type == EventType.MouseDrag && GUIUtility.hotControl == pressId)
            {
                e.Use();
                ApplyTipPick(root, e.mousePosition);
                sceneView.Repaint();
                return true;
            }
            // 離し: 先に他の部品が使った（Used）離しも rawType で拾う
            if ((type == EventType.MouseUp || e.rawType == EventType.MouseUp) && e.button == 0)
            {
                if (GUIUtility.hotControl == pressId) e.Use();
                EndTipPick();
                sceneView.Repaint();
                return true;
            }
            return false;
        }

        private static bool CurrentEditIsTips(GameObject root)
        {
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            return edit != null && edit.gradientEnabled && edit.gradientShape == GradientShape.Tips;
        }

        /// <summary>長さ選びを終える（押している途中なら Undo をまとめ、掴んだマウスを放す）</summary>
        private void EndTipPick()
        {
            if (_tipPickUndoGroup >= 0)
            {
                Undo.CollapseUndoOperations(_tipPickUndoGroup);
                if (s_capturedControl != 0 && GUIUtility.hotControl == s_capturedControl) GUIUtility.hotControl = 0;
                s_capturedControl = 0;
            }
            _tipPickUndoGroup = -1;
            _tipSamples.Clear();
            ToolSession.TipPickActive = false;
        }

        /// <summary>
        /// mousePosition の下の髪の「毛先からの距離」と「房の長さ」から、毛先からの長さを決める（GradientBox.ApplyTipPick）。
        /// 今の編集が毛先からでない・当たった髪のテクスチャが編集（の連結）の対象でない・読めないメッシュなら何もしない
        /// </summary>
        private void ApplyTipPick(GameObject root, Vector2 mousePosition)
        {
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            if (edit == null || !edit.gradientEnabled || edit.gradientShape != GradientShape.Tips) return;

            if (_picker == null) _picker = new ScenePicker(); // ドメインリロード直後の保険
            CollectPickRenderers(root);
            var ray = HandleUtility.GUIPointToWorldRay(mousePosition);
            CollectVisibleRenderers(mousePosition, root, _visibleRenderers);
            if (!TryPickVisible(_visibleRenderers, ray, out var hit)) return;
            if (!TryGetSourceTexture(hit, out var texture)) return;
            var member = EditGroups.Members(component, edit).Find(m => m != null && m.sourceTexture == texture);
            if (member == null) return;

            var sample = TipSampleFor(root.transform, hit.renderer, texture, member.gradientTipTop);
            if (sample == null || hit.subMeshIndex < 0 || hit.subMeshIndex >= sample.triangles.Length) return;
            if (!TipMap.TrySampleTriangle(sample.result, sample.triangles[hit.subMeshIndex], hit.triangleIndex, hit.barycentric,
                    out float distance, out float length))
            {
                return;
            }
            GradientBox.ApplyTipPick(component, edit, distance, length);
        }

        /// <summary>renderer のうち texture を使うサブメッシュで房を分けた頂点ごとの値（押している間はキャッシュ）。求められなければ null</summary>
        private TipSample TipSampleFor(Transform root, Renderer renderer, Texture2D texture, Vector3 top)
        {
            if (_tipSamples.TryGetValue((renderer, texture), out var cached)) return cached;
            TipSample sample = null;
            var submeshes = new List<int>();
            foreach (var slot in PositionMap.CollectSlots(new[] { renderer }, texture)) submeshes.Add(slot.submesh);
            if (submeshes.Count > 0
                && TipMap.TryComputeVertexTips(root, renderer, submeshes, top, out var mesh, out _, out bool ownsMesh, out var result))
            {
                var triangles = new int[mesh.subMeshCount][];
                foreach (int submesh in submeshes)
                {
                    if (submesh >= 0 && submesh < triangles.Length) triangles[submesh] = mesh.GetTriangles(submesh);
                }
                if (ownsMesh) Object.DestroyImmediate(mesh);
                sample = new TipSample { result = result, triangles = triangles };
            }
            // 求められなかったことも覚える（なぞっている間に何度も重い取得をしない）
            _tipSamples[(renderer, texture)] = sample;
            return sample;
        }
    }
}
