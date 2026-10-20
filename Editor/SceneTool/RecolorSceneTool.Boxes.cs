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
    /// <summary>Scene の箱（選択の箱・グラデーションの箱・画像の箱とシールの枠）のギズモとドラッグ</summary>
    internal sealed partial class RecolorSceneTool
    {
        /// <summary>
        /// 現在の編集がグラデーション ON なら、Scene に箱（対象ルートのローカル座標）を出す: 移動・回転・拡縮のハンドル、ワイヤーの箱、
        /// 下端（t = 0）の面を「新しい色」、上端（t = 1）の面を「終了色」で薄く塗る。
        /// ハンドルの操作は Undo 1 回にまとめ、「アバター全体」の連結なら連結の全編集に同じ箱を入れる
        /// </summary>
        private static void DrawGradientBox()
        {
            // 離したら Undo のまとめを締める（ハンドルは MouseUp で hotControl を 0 に戻す）
            if (s_boxDrag.IsActive && GUIUtility.hotControl == 0) s_boxDrag.End();

            if (!ToolSession.TryGetActiveRoot(out var root)) return;
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            if (edit == null || !edit.gradientEnabled) return;
            if (s_gradientDragging && GUIUtility.hotControl == 0) s_gradientDragging = false;
            // 小窓「箱を表示」で選んでいなければ箱・つまみ・ギズモ・札を出さない（既定は画像も ON なら出さない。ToolSession.ShowsGradientBox）
            if (!ToolSession.ShowsGradientBox(edit)) return;

            var e = Event.current;
            using (new Handles.DrawingScope(component.transform.localToWorldMatrix))
            {
                Vector3 position = edit.gradientBoxPosition;
                Quaternion rotation = Quaternion.Normalize(edit.gradientBoxRotation);
                Vector3 size = edit.gradientBoxSize;

                EditorGUI.BeginChangeCheck();
                // 全体のギズモに加えて、各面のつまみでも伸縮できる（「箱の中」と同じ。ユーザー要望 2026-09-29）
                DrawBoxWithFaceHandles(ref position, ref rotation, ref size, s_gradientBoxHandle, ref s_gradientDragging, ref s_gradientDragOrigin, SelectionBoxHandleColor);
                if (EditorGUI.EndChangeCheck())
                {
                    if (GUIUtility.hotControl != 0 && !s_boxDrag.IsActive) s_boxDrag.Begin("Tocolo: グラデーションの箱を変更");
                    Undo.RecordObject(component, "Tocolo: グラデーションの箱を変更");
                    EditGroups.ForEachInGroup(component, edit, m =>
                    {
                        m.gradientBoxPosition = position;
                        m.gradientBoxRotation = rotation;
                        m.gradientBoxSize = size;
                    });
                    EditorUtility.SetDirty(component);
                }

                if (e.type == EventType.Repaint) DrawBoxShape(position, rotation, size, edit.targetColor, edit.gradientColor, edit.gradientInsideOnly);
            }
        }

        /// <summary>
        /// 箱のワイヤーと、下端（startColor）・上端（endColor）の面の薄い塗り。insideOnly（箱の中だけ）なら側面 4 面も灰色で薄く塗る。
        /// Handles.matrix（ルートのローカル）の中で呼ぶ
        /// </summary>
        private static void DrawBoxShape(Vector3 position, Quaternion rotation, Vector3 size, Color startColor, Color endColor, bool insideOnly)
        {
            using (new Handles.DrawingScope(Color.white, Handles.matrix * Matrix4x4.TRS(position, rotation, Vector3.one)))
            {
                Handles.DrawWireCube(Vector3.zero, size);
                var half = size * 0.5f;
                DrawFace(-half.y, half, startColor);
                DrawFace(half.y, half, endColor);
                // 「箱の中だけ」は上下方向だけで切る（横は切らない）ので、側面は塗らない。上下の面は色 1／色 2 で常に塗られている
                // 面の中央付近に「色1」「色2」の札（どちらの面がどちらの色か分かるように。ユーザー要望 2026-10-03）
                DrawFaceLabel(new Vector3(0f, -half.y, 0f), startColor, Locales.Tr("Scene:Color:EditTarget:Start"));
                DrawFaceLabel(new Vector3(0f, half.y, 0f), endColor, Locales.Tr("Scene:Color:EditTarget:End"));
            }
        }

        /// <summary>
        /// 現在の編集が「画像を入れる」ON なら、編集の置き方（RecolorEdit.decalSticker）に応じて Scene に画像の箱（対象ルートのローカル座標）の操作部品を出す:
        /// シールは面の上の枠と四隅・輪のつまみ（DrawStickerFrame / DrawStickerHandles）、箱は移動・回転・拡縮のハンドルと各面のつまみ、
        /// ワイヤーの箱、+Z 面（投影する側）に画像を半透明で描く（DrawGradientBox と同じ流れ）。
        /// ハンドルの操作は Undo 1 回にまとめ（s_boxDrag はグラデーションと共用。同時に 2 つの箱を掴むことはない）、連結なら連結の全編集に同じ箱を入れる
        /// </summary>
        private static void DrawDecalBox()
        {
            if (s_boxDrag.IsActive && GUIUtility.hotControl == 0) s_boxDrag.End();
            // 離した: 層をフル解像度で作り直させる（プレビューのハッシュにドラッグ状態が入っている）。
            // 編集の切り替え・画像 OFF の後でも低解像度のまま残らないよう、編集を引く前に下ろす
            if (BoxDragState.Decal.IsActive && GUIUtility.hotControl == 0)
            {
                BoxDragState.EndDecal();
                // 離したことをプレビュー（NDMF）に確実に知らせる: ドラッグ状態はコンポーネントの外にあり、ハッシュを見直させるには
                // 変更通知が要る（最後のドラッグの変更通知の順序に頼らない。レビュー指摘 2026-10-04）
                if (ToolSession.TryGetActiveRoot(out var releasedRoot))
                {
                    var releasedComponent = releasedRoot.GetComponent<ClickRecolor>();
                    if (releasedComponent != null) nadena.dev.ndmf.preview.ChangeNotifier.NotifyObjectUpdate(releasedComponent);
                }
                SceneView.RepaintAll();
            }

            if (!ToolSession.TryGetActiveRoot(out var root)) return;
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            if (edit == null || !edit.decalEnabled) return;
            if (s_decalDragging && GUIUtility.hotControl == 0) s_decalDragging = false;
            // 小窓「箱を表示」で選んでいなければ箱・つまみ・ギズモ・画像を出さない（貼った結果を確認しやすくする）
            if (!ToolSession.ShowsDecalBox(edit)) return;

            var e = Event.current;
            using (new Handles.DrawingScope(component.transform.localToWorldMatrix))
            {
                Vector3 position = edit.decalBoxPosition;
                Quaternion rotation = Quaternion.Normalize(edit.decalBoxRotation);
                Vector3 size = edit.decalBoxSize;

                // 置き方がシールなら面の上の枠と四隅・輪のつまみ、箱なら箱とギズモ・面のつまみ（同時には出さない）
                bool sticker = DecalBox.PlacementOf(edit) == DecalPlacement.Sticker;
                // シールの枠・つまみは箱の中心ではなく、中心を通る奥行きの線上の選択範囲の一番手前の面に出す（中心は面の上にあるとは限らない）
                // ドラッグ中は動かしている側が中心を面の上に保つので、描くたびに面を探し直さない
                var stickerCenter = sticker && !BoxDragState.Decal.IsFor(edit) && StickerSurface.TryFindUnderCenter(component, edit, out var onSurface)
                    ? onSurface : position;
                // 枠はつまみより先に描く（つまみが枠の線に隠れないように）
                if (sticker && e.type == EventType.Repaint) DrawStickerFrame(stickerCenter, rotation, size, edit.decalTexture, edit.decalKeepAspect);
                EditorGUI.BeginChangeCheck();
                if (sticker) DrawStickerHandles(stickerCenter, ref rotation, ref size, edit.decalTexture, edit.decalKeepAspect, ref s_decalDragging);
                else DrawBoxWithFaceHandles(ref position, ref rotation, ref size, s_decalBoxHandle, ref s_decalDragging, ref s_decalDragOrigin, DecalBoxColor);
                if (EditorGUI.EndChangeCheck())
                {
                    // シールのつまみを触ったら箱の中心を面の上の点へ移す（同じ Undo。以後は大きさを変えても面が奥行きの真ん中に残る）。
                    // 中心は奥行きの線に沿って動くだけなので、画像の写る位置は変わらない
                    if (sticker) position = stickerCenter;
                    if (GUIUtility.hotControl != 0)
                    {
                        if (!s_boxDrag.IsActive) s_boxDrag.Begin("Tocolo: 画像の箱を変更");
                        // ドラッグ中は層を低解像度で作る（離したら上でフル解像度に戻す）
                        if (s_decalDragging && !BoxDragState.Decal.IsFor(edit))
                        {
                            // 重ね貼りかはドラッグの始めに 1 回だけ判定する（アバター全体を走査するので毎イベントはしない）
                            BoxDragState.BeginDecal(edit, Decal.DecalOverlayMaterial.UseOverlay(component, edit));
                        }
                    }
                    Undo.RecordObject(component, "Tocolo: 画像の箱を変更");
                    EditGroups.ForEachInGroup(component, edit, m =>
                    {
                        m.decalBoxPosition = position;
                        m.decalBoxRotation = rotation;
                        m.decalBoxSize = size;
                    });
                    EditorUtility.SetDirty(component);
                }

                if (!sticker && e.type == EventType.Repaint) DrawDecalBoxShape(position, rotation, size, edit.decalTexture, edit.decalKeepAspect);
            }
        }

        /// <summary>シールの四隅のつまみの大きさ（GetHandleSize に対する割合）</summary>
        private const float StickerKnobScale = 0.15f;
        /// <summary>シールの回転の輪の半径（画像の対角線の半分に対する倍率。四隅のつまみの外を通す）</summary>
        private const float StickerRingScale = 1.15f;

        /// <summary>
        /// 画像の縦横の半分（箱の縦横から比率を保つ縮みを除いた大きさ）
        /// </summary>
        private static Vector2 StickerHalfExtent(Vector3 size, Texture2D image, bool keepAspect)
        {
            var fit = DecalLayerBuilder.FitScale(image, size, keepAspect);
            return new Vector2(Mathf.Abs(size.x) / fit.x, Mathf.Abs(size.y) / fit.y) * 0.5f;
        }

        /// <summary>
        /// シールの枠（画像の外形）を面の上の点 position（StickerSurface.TryFindUnderCenter）を中心に描く。Handles.matrix（ルートのローカル）の中で、Repaint のときだけ呼ぶ。
        /// 手前の物（髪など）に隠れても位置が分かるよう、深度を見ずに描く
        /// </summary>
        private static void DrawStickerFrame(Vector3 position, Quaternion rotation, Vector3 size, Texture2D image, bool keepAspect)
        {
            var half = StickerHalfExtent(size, image, keepAspect);
            var previousZTest = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            try
            {
                using (new Handles.DrawingScope(DecalBoxColor, Handles.matrix * Matrix4x4.TRS(position, rotation, Vector3.one)))
                {
                    var corners = new[]
                    {
                        new Vector3(-half.x, -half.y, 0f), new Vector3(half.x, -half.y, 0f),
                        new Vector3(half.x, half.y, 0f), new Vector3(-half.x, half.y, 0f), new Vector3(-half.x, -half.y, 0f),
                    };
                    Handles.DrawAAPolyLine(3f, corners);
                }
            }
            finally
            {
                Handles.zTest = previousZTest;
            }
        }

        /// <summary>
        /// シールの四隅のつまみ（大きさ。中心から対角線の向きにだけ動かし、箱を縦横・奥行きとも同じ割合で拡縮する＝比率と反転を保つ）と、
        /// まわりの輪（面内の回転。箱の +Z のまわり）を、面の上の点 position を中心に描く。Handles.matrix（ルートのローカル）の中で、EditorGUI.BeginChangeCheck の内側で呼ぶ。
        /// 四隅は画像の面の上だけで動く 2 軸のスライダーにする（3D の矢印では面に沿った方向に動かしにくいため）。
        /// 輪は画面でのマウスの角度で回す StickerRing（標準の輪は回りすぎ・半周で逆回転する）
        /// </summary>
        private static void DrawStickerHandles(Vector3 position, ref Quaternion rotation, ref Vector3 size, Texture2D image, bool keepAspect, ref bool dragging)
        {
            var half = StickerHalfExtent(size, image, keepAspect);
            var previousZTest = Handles.zTest;
            Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
            try
            {
                using (new Handles.DrawingScope(DecalBoxColor, Handles.matrix * Matrix4x4.TRS(position, rotation, Vector3.one)))
                {
                    for (int i = 0; i < 4; i++)
                    {
                        var corner = new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, 0f);
                        if (corner.sqrMagnitude < 1e-12f) continue;
                        float knob = HandleUtility.GetHandleSize(corner) * StickerKnobScale;
                        var moved = Handles.Slider2D(corner, Vector3.forward, Vector3.right, Vector3.up, knob, DrawSelectionBoxKnob, Vector2.zero);
                        if (moved == corner) continue;
                        // 掴んだ点を対角線に写した位置まで隅が来る割合（スライダーは掴んだ時点からの位置を返すので、毎フレーム今の隅と比べてよい）
                        size = DecalBox.ScaleSticker(size, Vector3.Dot(moved, corner) / corner.sqrMagnitude);
                    }
                }

                float radius = half.magnitude * StickerRingScale;
                if (radius > 1e-6f) StickerRing.Do(position, rotation * Vector3.forward, radius, ref rotation, DecalBoxColor);
            }
            finally
            {
                Handles.zTest = previousZTest;
            }
            if (GUI.changed && GUIUtility.hotControl != 0) dragging = true;
        }

        /// <summary>画像の箱の +Z 面に描く画像の不透明度（下のモデルが透けて見え、貼られる位置が分かる程度。0.6 から半分に: ユーザー要望 2026-10-04）</summary>
        private const float DecalGizmoAlpha = 0.3f;

        /// <summary>画像の箱のワイヤーとつまみの色。グラデーション・選択の箱（橙のつまみ）と区別するため、パネルの「画像を入れる」の枠と同じ青緑系</summary>
        internal static readonly Color DecalBoxColor = new Color(0.35f, 0.75f, 0.85f, 1f);

        /// <summary>画像の箱のワイヤーと、+Z 面の画像（DecalGizmo）。Handles.matrix（ルートのローカル）の中で、Repaint のときだけ呼ぶ</summary>
        private static void DrawDecalBoxShape(Vector3 position, Quaternion rotation, Vector3 size, Texture2D image, bool keepAspect)
        {
            using (new Handles.DrawingScope(DecalBoxColor, Handles.matrix * Matrix4x4.TRS(position, rotation, Vector3.one)))
            {
                Handles.DrawWireCube(Vector3.zero, size);
                DecalGizmo.DrawImage(size, image, keepAspect, DecalGizmoAlpha);
            }
        }

        private static GUIStyle s_faceLabelStyle;

        /// <summary>札を面の中央（つまみの位置）から画面上でずらす量（px）。つまみと重ならないよう右上へ</summary>
        private static readonly Vector2 FaceLabelOffset = new Vector2(16f, -22f);

        /// <summary>
        /// 箱のローカル位置 local の近くに、その面の名前の札を出す（Handles.matrix の中で呼ぶ）。枠は付けない。
        /// 文字の色は面の色の明るさ（HSV の V）を反転したもの（白い面なら黒、黒い面なら白。ユーザー要望 2026-10-03）。
        /// モデルの上でも読めるよう、面の色で 1px の影を付ける
        /// </summary>
        private static void DrawFaceLabel(Vector3 local, Color color, string text)
        {
            var world = Handles.matrix.MultiplyPoint3x4(local);
            var camera = SceneView.currentDrawingSceneView != null ? SceneView.currentDrawingSceneView.camera : null;
            if (camera != null && camera.WorldToViewportPoint(world).z <= 0f) return; // カメラの後ろ
            s_faceLabelStyle ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 12, alignment = TextAnchor.MiddleLeft };

            Color.RGBToHSV(color, out float h, out float sat, out float v);
            var textColor = Color.HSVToRGB(h, sat, 1f - v);
            var shadowColor = new Color(color.r, color.g, color.b, 1f);

            var content = new GUIContent(text);
            var size = s_faceLabelStyle.CalcSize(content);
            // WorldToGUIPoint は Handles.matrix を自分で掛けるので、変換前の local を渡す（world を渡すと 2 回掛かって画面外へ出ていた）
            var point = HandleUtility.WorldToGUIPoint(local) + FaceLabelOffset;
            var rect = new Rect(point.x, point.y - size.y * 0.5f, size.x, size.y);
            Handles.BeginGUI();
            try
            {
                var previous = s_faceLabelStyle.normal.textColor;
                s_faceLabelStyle.normal.textColor = shadowColor;
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), content, s_faceLabelStyle);
                s_faceLabelStyle.normal.textColor = textColor;
                GUI.Label(rect, content, s_faceLabelStyle);
                s_faceLabelStyle.normal.textColor = previous;
            }
            finally
            {
                Handles.EndGUI();
            }
        }

        /// <summary>
        /// 選択の箱を離したら、箱に触れるテクスチャのメンバーを増減してから Undo のまとめを締める（同じ 1 回の Undo に入れる）。
        /// 離しの検知は s_boxDrag ではなくドラッグ中の印で行う（s_boxDrag はグラデーション・画像の箱の関数も締める）。
        /// 現在の編集を引く前に見る: 掴んでいる間に編集の切り替え・モードの変更・箱の非表示があっても印を残さない
        /// </summary>
        private static void FinishSelectionBoxDragIfReleased()
        {
            if (!BoxDragState.Selection.IsActive || GUIUtility.hotControl != 0) return;
            string editId = BoxDragState.Selection.EditId;
            BoxDragState.Selection.End(); // 離した: パーツごとの統計をここで掛け直す
            var component = ToolSession.TryGetActiveRoot(out var root) ? root.GetComponent<ClickRecolor>() : null;
            var edit = component != null ? component.FindEdit(editId) : null;
            if (edit != null)
            {
                // ドラッグ中の最後の RecordObject より後のフレームの変更は Undo に入らない（Redo で箱だけ戻りメンバーがずれる）ので記録し直す
                Undo.RecordObject(component, "Tocolo: 選択の箱を変更");
                SelectionBox.SyncMembers(component, edit);
                // RecordObject はマウスを離した直後などの区切りでまとめて登録される。ここは離した後のイベントなので、確定させないと
                // 次の区切りで別の操作として積まれ、Ctrl+Z 1 回で箱とメンバーが一緒に戻らない（下の End でドラッグの記録とまとめる）
                Undo.FlushUndoRecordObjects();
            }
            if (s_boxDrag.IsActive) s_boxDrag.End();
            // ドラッグ状態はコンポーネントの外にあるので、プレビュー（NDMF）にハッシュを見直させるには変更通知が要る
            if (component != null) nadena.dev.ndmf.preview.ChangeNotifier.NotifyObjectUpdate(component);
            SceneView.RepaintAll();
        }

        /// <summary>
        /// 選択の箱・画像の箱のドラッグ中の印を下ろし、プレビューに作り直させる（ツールの終了時）。
        /// 選択の箱は離したときと同じく、箱に触れるテクスチャのメンバーも増減する（離しが来ないまま抜けても箱とメンバーがずれない）
        /// </summary>
        private static void EndBoxDrags()
        {
            if (!BoxDragState.Selection.IsActive && !BoxDragState.Decal.IsActive) return;
            string selectionEditId = BoxDragState.Selection.EditId;
            BoxDragState.Selection.End();
            BoxDragState.EndDecal();
            if (!ToolSession.TryGetActiveRoot(out var root)) return;
            var component = root.GetComponent<ClickRecolor>();
            if (component == null) return;
            var edit = selectionEditId != null ? component.FindEdit(selectionEditId) : null;
            if (edit != null)
            {
                Undo.RecordObject(component, "Tocolo: 選択の箱を変更");
                SelectionBox.SyncMembers(component, edit);
                // 次の区切りまで持ち越さず、ここで確定させる（FinishSelectionBoxDragIfReleased と同じ）
                Undo.FlushUndoRecordObjects();
            }
            nadena.dev.ndmf.preview.ChangeNotifier.NotifyObjectUpdate(component);
        }

        /// <summary>
        /// 影響範囲「箱の中」の箱（RecolorEdit.box*）。グラデーションの箱と同じハンドルで動かせる（色は区別のため水色）。
        /// ハンドルの操作は Undo 1 回にまとめ、連結なら連結の全編集に同じ箱を入れる
        /// </summary>
        private static void DrawSelectionBox()
        {
            if (!ToolSession.TryGetActiveRoot(out var root)) return;
            var component = root.GetComponent<ClickRecolor>();
            var edit = component != null ? component.FindEdit(ToolSession.CurrentEditId) : null;
            if (edit == null || edit.mode != SelectionMode.Box) return;

            // 小窓「箱を表示」で選んでいなければ出さない。既定はグラデーション・画像も ON なら出さない（箱が重なって見づらい。ユーザー要望 2026-09-29）。
            // テクスチャ全体に広げている間は箱が範囲に関係しないので出さない（ToolSession.HasSelectionBox）
            if (!ToolSession.ShowsSelectionBox(edit)) return;

            var e = Event.current;
            using (new Handles.DrawingScope(component.transform.localToWorldMatrix))
            {
                Vector3 position = edit.boxPosition;
                Quaternion rotation = Quaternion.Normalize(edit.boxRotation);
                Vector3 size = edit.boxSize;

                EditorGUI.BeginChangeCheck();
                // 箱全体の移動・回転・拡大縮小はグラデーションの箱と同じハンドル。加えて各面のつまみで、その面だけを軸ごとに引ける
                // （比率は保たない。両方残す: ユーザー要望 2026-09-29）
                bool dragging = BoxDragState.Selection.IsFor(edit);
                DrawBoxWithFaceHandles(ref position, ref rotation, ref size, s_selectionBoxHandle, ref dragging, ref s_boxDragOrigin, SelectionBoxHandleColor);
                if (EditorGUI.EndChangeCheck())
                {
                    if (GUIUtility.hotControl != 0)
                    {
                        if (!s_boxDrag.IsActive) s_boxDrag.Begin("Tocolo: 選択の箱を変更");
                        if (dragging) BoxDragState.Selection.Begin(edit);
                    }
                    Undo.RecordObject(component, "Tocolo: 選択の箱を変更");
                    EditGroups.ForEachInGroup(component, edit, m =>
                    {
                        m.boxPosition = position;
                        m.boxRotation = rotation;
                        m.boxSize = size;
                    });
                    EditorUtility.SetDirty(component);
                }

                if (e.type == EventType.Repaint) DrawSelectionBoxShape(position, rotation, size);
            }
        }

        internal static readonly Color SelectionBoxColor = new Color(0.25f, 0.75f, 1f, 1f);
        /// <summary>選択の箱のドラッグ開始時の中心（対象ルートのローカル）。ドラッグ中の Handles.matrix の原点に使う</summary>
        private static Vector3 s_boxDragOrigin;
        /// <summary>グラデーションの箱の面のつまみと、そのドラッグ状態・開始時の中心</summary>
        private static readonly UnityEditor.IMGUI.Controls.BoxBoundsHandle s_gradientBoxHandle =
            new UnityEditor.IMGUI.Controls.BoxBoundsHandle
            {
                wireframeColor = Color.clear,
                midpointHandleSizeFunction = p => HandleUtility.GetHandleSize(p) * 0.18f,
                midpointHandleDrawFunction = DrawSelectionBoxKnob,
            };
        private static bool s_gradientDragging;
        private static Vector3 s_gradientDragOrigin;
        /// <summary>画像の箱の面のつまみと、そのドラッグ状態・開始時の中心（グラデーションの箱と同じ作り）</summary>
        private static readonly UnityEditor.IMGUI.Controls.BoxBoundsHandle s_decalBoxHandle =
            new UnityEditor.IMGUI.Controls.BoxBoundsHandle
            {
                wireframeColor = Color.clear,
                midpointHandleSizeFunction = p => HandleUtility.GetHandleSize(p) * 0.18f,
                midpointHandleDrawFunction = DrawSelectionBoxKnob,
            };
        private static bool s_decalDragging;
        private static Vector3 s_decalDragOrigin;

        /// <summary>
        /// 箱のギズモ（移動・回転・全体の拡大縮小。DrawFixedAxisTransformHandle）と各面のつまみ（その面だけ伸縮）を描き、position / rotation / size を更新する。
        /// Handles.matrix（ルートのローカル）の中で呼ぶ。EditorGUI.BeginChangeCheck の内側で呼ぶこと。
        /// 面のつまみ（1 軸スライダー）はドラッグ開始位置を Handles.matrix のローカルで覚えるので、ドラッグ中に matrix の原点（箱の中心）を
        /// 動かすと基準がずれて約 2 倍動く（実機 2026-09-29）。dragging の間は原点を dragOrigin（掴んだときの中心）に固定し、
        /// ずれはつまみ側の center に持たせる。大きさの符号（反転）は保つ
        /// </summary>
        private static void DrawBoxWithFaceHandles(
            ref Vector3 position, ref Quaternion rotation, ref Vector3 size,
            UnityEditor.IMGUI.Controls.BoxBoundsHandle handle, ref bool dragging, ref Vector3 dragOrigin, Color handleColor)
        {
            Vector3 positionBefore = position;
            DrawFixedAxisTransformHandle(ref position, ref rotation, ref size);
            Vector3 origin = dragging ? dragOrigin : position;
            using (new Handles.DrawingScope(Handles.matrix * Matrix4x4.TRS(origin, rotation, Vector3.one)))
            {
                var fed = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
                var fedCenter = Quaternion.Inverse(rotation) * (position - origin);
                handle.center = fedCenter;
                handle.size = fed;
                handle.SetColor(handleColor);
                handle.DrawHandle();
                // 面を引いたときだけ反映する（全体のギズモの結果を上書きしない）。中心は箱のローカルなのでルートのローカルへ戻す
                if (handle.center != fedCenter || handle.size != fed)
                {
                    position = origin + rotation * handle.center;
                    size = new Vector3(
                        Mathf.Sign(size.x == 0f ? 1f : size.x) * handle.size.x,
                        Mathf.Sign(size.y == 0f ? 1f : size.y) * handle.size.y,
                        Mathf.Sign(size.z == 0f ? 1f : size.z) * handle.size.z);
                }
            }
            if (GUI.changed && GUIUtility.hotControl != 0 && !dragging)
            {
                dragOrigin = positionBefore; // このフレームの matrix はこの位置で描いた
                dragging = true;
            }
        }
        /// <summary>回転の輪の今の向き（ドラッグしていない間は単位。ドラッグ中は輪が返した向きを持ち、前フレームとの差を箱に足す）</summary>
        private static Quaternion s_rotationHandleRotation = Quaternion.identity;

        /// <summary>
        /// 箱全体の移動・回転・拡大縮小のギズモ。標準の TransformHandle は矢印と輪を箱の向きに合わせて描くので、箱を回すと矢印も傾いて動かしにくい
        /// （ユーザー要望 2026-10-04）。矢印と輪は常に Handles.matrix（対象ルート）の向きで描き、回転は輪を回した差を箱の向きに足す。
        /// 真ん中のつまみは標準と同じく全体の拡大縮小（比率と符号＝反転は保つ）
        /// </summary>
        private static void DrawFixedAxisTransformHandle(ref Vector3 position, ref Quaternion rotation, ref Vector3 size)
        {
            position = Handles.PositionHandle(position, Quaternion.identity);

            // 輪は掴んだ時点からの回転を返すもの（円）と、前フレームからの回転を返すもの（自由回転）があるので、
            // 前フレームに渡した向きとの差を毎フレーム足す（どちらでも合計が掴んでからの回転になる）
            if (GUIUtility.hotControl == 0) s_rotationHandleRotation = Quaternion.identity;
            var handleRotation = Handles.RotationHandle(s_rotationHandleRotation, position);
            if (handleRotation != s_rotationHandleRotation)
            {
                rotation = Quaternion.Normalize(handleRotation * Quaternion.Inverse(s_rotationHandleRotation) * rotation);
                s_rotationHandleRotation = handleRotation;
            }

            float magnitude = size.magnitude;
            if (magnitude > 1e-6f)
            {
                // ScaleValueHandle は渡した大きさに 0.15 を掛けて四角を描くので、ギズモの大きさをそのまま渡す（標準の拡大縮小ツールと同じ。
                // こちらでも縮めると矢印の根元に埋もれて見えなくなった: 実機 2026-10-04）
                float scaled;
                using (new Handles.DrawingScope(Handles.centerColor))
                {
                    scaled = Handles.ScaleValueHandle(magnitude, position, Quaternion.identity,
                        HandleUtility.GetHandleSize(position), Handles.CubeHandleCap, 0f);
                }
                if (scaled != magnitude && scaled > 1e-6f) size *= scaled / magnitude;
            }
        }

        /// <summary>面のつまみの色。箱の水色の上でも見えるよう補色寄りの橙（ユーザー要望 2026-09-29）</summary>
        internal static readonly Color SelectionBoxHandleColor = new Color(1f, 0.6f, 0.1f, 1f);
        /// <summary>面のつまみにマウスが乗った・掴んだときの色（明るい黄）</summary>
        private static readonly Color SelectionBoxHandleHoverColor = new Color(1f, 1f, 0.2f, 1f);
        /// <summary>選択の箱の各面のつまみ（軸ごとに伸縮）。ワイヤーは DrawSelectionBoxShape が描くので消す</summary>
        private static readonly UnityEditor.IMGUI.Controls.BoxBoundsHandle s_selectionBoxHandle =
            new UnityEditor.IMGUI.Controls.BoxBoundsHandle
            {
                wireframeColor = Color.clear,
                // つまみは既定（0.03）の 6 倍の丸（ユーザー要望 2026-09-29）
                midpointHandleSizeFunction = p => HandleUtility.GetHandleSize(p) * 0.18f,
                midpointHandleDrawFunction = DrawSelectionBoxKnob,
            };

        /// <summary>
        /// 面のつまみの描画。マウスが乗っている（nearestControl）か掴んでいる（hotControl）つまみは明るい色にして、
        /// どれが反応するか分かるようにする（ユーザー要望 2026-09-29: 細かいところが掴みにくい）
        /// </summary>
        private static void DrawSelectionBoxKnob(int controlID, Vector3 position, Quaternion rotation, float size, EventType eventType)
        {
            if (eventType == EventType.Repaint)
            {
                bool active = GUIUtility.hotControl == controlID
                    || (GUIUtility.hotControl == 0 && HandleUtility.nearestControl == controlID);
                using (new Handles.DrawingScope(active ? SelectionBoxHandleHoverColor : Handles.color))
                {
                    Handles.SphereHandleCap(controlID, position, rotation, active ? size * 1.15f : size, eventType);
                }
                return;
            }
            Handles.SphereHandleCap(controlID, position, rotation, size, eventType);
        }

        /// <summary>選択の箱のワイヤーと、6 面の薄い塗り（水色）</summary>
        private static void DrawSelectionBoxShape(Vector3 position, Quaternion rotation, Vector3 size)
        {
            using (new Handles.DrawingScope(SelectionBoxColor, Handles.matrix * Matrix4x4.TRS(position, rotation, Vector3.one)))
            {
                Handles.DrawWireCube(Vector3.zero, size);
                var half = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)) * 0.5f;
                // 面の塗りはグラデーションの箱の半分から、25% 薄く、さらに 40% 薄く（ユーザー要望 2026-09-29）
                var fill = new Color(SelectionBoxColor.r, SelectionBoxColor.g, SelectionBoxColor.b, BoxFaceAlpha * 0.5f * 0.75f * 0.6f);
                var outline = new Color(SelectionBoxColor.r, SelectionBoxColor.g, SelectionBoxColor.b, 1f);
                // 6 面: 各軸の ± の面
                for (int axis = 0; axis < 3; axis++)
                {
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        var verts = new Vector3[4];
                        int a = (axis + 1) % 3, b = (axis + 2) % 3;
                        for (int k = 0; k < 4; k++)
                        {
                            var v = Vector3.zero;
                            v[axis] = sign * half[axis];
                            v[a] = (k == 0 || k == 3 ? -1 : 1) * half[a];
                            v[b] = (k < 2 ? -1 : 1) * half[b];
                            verts[k] = v;
                        }
                        Handles.DrawSolidRectangleWithOutline(verts, fill, outline);
                    }
                }
            }
        }

        /// <summary>高さ y の水平な面（箱のローカル）を color で薄く塗る</summary>
        private static void DrawFace(float y, Vector3 half, Color color)
        {
            var verts = new[]
            {
                new Vector3(-half.x, y, -half.z),
                new Vector3(half.x, y, -half.z),
                new Vector3(half.x, y, half.z),
                new Vector3(-half.x, y, half.z),
            };
            Handles.DrawSolidRectangleWithOutline(verts,
                new Color(color.r, color.g, color.b, BoxFaceAlpha),
                new Color(color.r, color.g, color.b, 1f));
        }
    }
}
