using Nekoare.ClickRecolor.Editor.Colors;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// お手本に合わせるの明るさの山。横が明るさ（左が暗い）、高さがその明るさの画素の多さ、柱の色がその明るさの合わせた結果の色。
    /// 山の本体をつかんで左右に動かすと明るさのずらし、左の裾は陰影の暗さ、右の裾はハイライトの伸びが変わる。
    /// 山の下の▲（左＝影・中＝明るさ・右＝ハイライト）でも同じ操作ができる。薄い灰色の山はずらす前（お手本どおり）
    /// </summary>
    internal static class MatchMountainGUI
    {
        internal enum Part { None, Body, Left, Right }

        /// <summary>山の下の▲を並べる帯の高さ（px）</summary>
        internal const float MarkerStrip = 14f;

        /// <summary>裾・▲をつかめる幅（px）</summary>
        private const float EdgeGrab = 7f;
        /// <summary>中の▲を端より優先してつかむ幅（px）。▲の半分の幅より狭くして、重なった端の▲の外側を残す</summary>
        private const float MiddleCore = 3f;
        private const float ColumnWidth = 2f;
        private const float MarkerHalfWidth = 5f;
        private const float MarkerHeight = 9f;
        /// <summary>太い枠（選んでいる▲）まで含めた▲の半分の幅（px）。帯の端の▲をはみ出させないための余白に使う</summary>
        internal const float ThickMarkerHalfWidth = MarkerHalfWidth + 3.5f;

        private static readonly int s_hint = "ClickRecolor.MatchMountain".GetHashCode();
        private static Part s_part;
        private static float s_startMouseX;
        private static float s_startLightness;

        private static readonly Color Background = new Color(0f, 0f, 0f, 0.35f);
        private static readonly Color Neutral = new Color(1f, 1f, 1f, 0.12f);
        private static readonly Color EdgeLine = new Color(1f, 1f, 1f, 0.45f);
        // ▲の色は画像ソフトのレベル補正にならう（影＝黒・明るさ＝灰・ハイライト＝白）
        private static readonly Color ShadowMarker = new Color(0.08f, 0.08f, 0.08f, 1f);
        private static readonly Color MiddleMarker = new Color(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color HighlightMarker = new Color(0.95f, 0.95f, 0.95f, 1f);
        private static readonly Color MarkerOutlineDark = new Color(0.1f, 0.1f, 0.1f, 1f);
        private static readonly Color MarkerOutlineLight = new Color(0.85f, 0.85f, 0.85f, 1f);
        internal static readonly Color MarkerOutlineActive = new Color(0.35f, 0.65f, 1f, 1f);

        /// <summary>お手本どおり（ずらし無し）の山を描くための編集</summary>
        private static readonly RecolorEdit s_neutral = RecolorEdit.CreateNew();

        private static float[] s_heights = new float[0];
        private static Color[] s_colors = new Color[0];
        private static float[] s_neutralHeights = new float[0];
        private static Color[] s_neutralColors = new Color[0];

        /// <summary>
        /// 描いて、つかんで動かしたら true と新しいずらし（明るさ・陰影の暗さ・ハイライトの伸び）。profile が null なら枠だけ描く。
        /// rect は下の MarkerStrip を▲の帯に使う
        /// </summary>
        internal static bool Draw(Rect rect, MatchProfile profile, RecolorEdit edit, out float lightness, out float shadow, out float highlight)
        {
            lightness = edit.matchLightness;
            shadow = edit.matchShadow;
            highlight = edit.matchHighlight;
            int id = GUIUtility.GetControlID(s_hint, FocusType.Passive, rect);
            var e = Event.current;

            var hill = new Rect(rect.x, rect.y, rect.width, rect.height - MarkerStrip);
            var strip = new Rect(rect.x, hill.yMax, rect.width, MarkerStrip);

            float left = 0f, middle = 0f, right = 0f;
            if (profile != null)
            {
                left = ToX(rect, MatchMountain.Transform(profile, edit, profile.quantiles[MatchMountain.LeftIndex]));
                middle = ToX(rect, MatchMountain.Transform(profile, edit, profile.quantiles[MatchProfile.MedianIndex]));
                right = ToX(rect, MatchMountain.Transform(profile, edit, profile.quantiles[MatchMountain.RightIndex]));
            }

            if (e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(hill, Background);
                if (profile != null)
                {
                    DrawMountains(hill, profile, edit, left, right);
                    Part active = GUIUtility.hotControl == id ? s_part : Part.None;
                    DrawMarker(strip, left, ShadowMarker, active == Part.Left ? MarkerOutlineActive : MarkerOutlineLight);
                    DrawMarker(strip, middle, MiddleMarker, active == Part.Body ? MarkerOutlineActive : MarkerOutlineDark);
                    DrawMarker(strip, right, HighlightMarker, active == Part.Right ? MarkerOutlineActive : MarkerOutlineDark);
                }
            }
            if (profile == null || !GUI.enabled) return false;

            // つかめる所でカーソルを変える（裾と▲は左右の矢印、本体は手）
            EditorGUIUtility.AddCursorRect(new Rect(left - EdgeGrab, hill.y, EdgeGrab * 2f, hill.height), MouseCursor.ResizeHorizontal);
            EditorGUIUtility.AddCursorRect(new Rect(right - EdgeGrab, hill.y, EdgeGrab * 2f, hill.height), MouseCursor.ResizeHorizontal);
            EditorGUIUtility.AddCursorRect(new Rect(left + EdgeGrab, hill.y, Mathf.Max(0f, right - left - EdgeGrab * 2f), hill.height), MouseCursor.Pan);
            foreach (float x in new[] { left, middle, right })
            {
                EditorGUIUtility.AddCursorRect(new Rect(x - EdgeGrab, strip.y, EdgeGrab * 2f, strip.height), MouseCursor.ResizeHorizontal);
            }

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button != 0 || !rect.Contains(e.mousePosition)) break;
                    float mx = e.mousePosition.x;
                    var part = strip.Contains(e.mousePosition) ? PickMarker(mx, left, middle, right) : PickHill(mx, left, right);
                    if (part == Part.None) break;
                    s_part = part;
                    s_startMouseX = mx;
                    s_startLightness = edit.matchLightness;
                    GUIUtility.hotControl = id;
                    e.Use();
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id) break;
                    float target = Mathf.Clamp01((e.mousePosition.x - rect.x) / rect.width);
                    switch (s_part)
                    {
                        case Part.Body:
                            lightness = Mathf.Clamp(s_startLightness + (e.mousePosition.x - s_startMouseX) / rect.width, -0.5f, 0.5f);
                            break;
                        case Part.Left:
                            shadow = MatchMountain.ShadowFor(profile, edit.matchLightness, target, edit.matchShadow);
                            break;
                        case Part.Right:
                            highlight = MatchMountain.HighlightFor(profile, edit.matchLightness, target, edit.matchHighlight);
                            break;
                    }
                    GUI.changed = true;
                    e.Use();
                    return true;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id) break;
                    GUIUtility.hotControl = 0;
                    s_part = Part.None;
                    e.Use();
                    break;
            }
            return false;
        }

        /// <summary>山の上: 裾の近くなら裾（重なるほど狭い山では近い方）、それ以外は本体</summary>
        private static Part PickHill(float mx, float left, float right)
        {
            if (Mathf.Abs(mx - left) <= EdgeGrab && Mathf.Abs(mx - left) <= Mathf.Abs(mx - right)) return Part.Left;
            if (Mathf.Abs(mx - right) <= EdgeGrab) return Part.Right;
            return Part.Body;
        }

        /// <summary>
        /// ▲の帯: 中の▲のすぐ近く（MiddleCore）なら明るさ。それ以外は、押したのが中より左なら影・右ならハイライトの▲を
        /// （つかめる幅の中にあれば）先につかみ、無ければ中の▲、それも遠ければ何もしない。
        /// 単純にいちばん近い▲にしないのは、端の▲が中と重なると端を二度とつかめなくなるため。
        /// 中は山の本体でも同じ操作ができるので、帯では端に広く譲る
        /// </summary>
        internal static Part PickMarker(float mx, float left, float middle, float right)
        {
            float toMiddle = Mathf.Abs(mx - middle);
            if (toMiddle <= MiddleCore) return Part.Body;
            if (mx < middle ? Mathf.Abs(mx - left) <= EdgeGrab : Mathf.Abs(mx - right) <= EdgeGrab)
            {
                return mx < middle ? Part.Left : Part.Right;
            }
            return toMiddle <= EdgeGrab ? Part.Body : Part.None;
        }

        private static float ToX(Rect rect, float l) => rect.x + Mathf.Clamp01(l) * rect.width;

        /// <summary>
        /// 上向きの▲を 1px の横線の積み重ねで描く（Handles の多角形は Scene の描画と行列・色の状態を共有するので、
        /// パネルの中では状態に左右されない GUI の矩形だけで描く）。先に一回り大きい枠の色で描いてから中を塗る
        /// </summary>
        internal static void DrawMarker(Rect strip, float x, Color fill, Color outline, bool thickOutline = false)
        {
            float top = strip.y + 2f;
            // 選んでいる▲は枠をもう一回り太く（小さい▲は枠の色だけでは選んでいるか見分けにくい）
            if (thickOutline) FillTriangle(x, top - 2.5f, MarkerHeight + 4f, ThickMarkerHalfWidth, outline);
            FillTriangle(x, top - 1f, MarkerHeight + 2f, MarkerHalfWidth + 1.5f, outline);
            FillTriangle(x, top + 0.5f, MarkerHeight - 0.5f, MarkerHalfWidth - 0.5f, fill);
        }

        private static void FillTriangle(float x, float top, float height, float halfWidth, Color color)
        {
            int rows = Mathf.CeilToInt(height);
            for (int row = 0; row < rows; row++)
            {
                float half = halfWidth * (row + 1) / rows;
                EditorGUI.DrawRect(new Rect(x - half, top + row, half * 2f, 1f), color);
            }
        }

        private static void DrawMountains(Rect rect, MatchProfile profile, RecolorEdit edit, float left, float right)
        {
            int count = Mathf.Max(8, Mathf.FloorToInt(rect.width / ColumnWidth));
            Ensure(ref s_heights, ref s_colors, count);
            Ensure(ref s_neutralHeights, ref s_neutralColors, count);
            MatchMountain.Columns(profile, s_neutral, s_neutralHeights, s_neutralColors);
            MatchMountain.Columns(profile, edit, s_heights, s_colors);

            float width = rect.width / count;
            float bottom = rect.yMax;
            float height = rect.height - 4f;
            for (int i = 0; i < count; i++)
            {
                float h = s_neutralHeights[i] * height;
                if (h > 0.5f) EditorGUI.DrawRect(new Rect(rect.x + i * width, bottom - h, width + 0.5f, h), Neutral);
            }
            for (int i = 0; i < count; i++)
            {
                float h = s_heights[i] * height;
                if (h <= 0.5f) continue;
                var c = s_colors[i];
                EditorGUI.DrawRect(new Rect(rect.x + i * width, bottom - h, width + 0.5f, h), new Color(c.r, c.g, c.b, 1f));
            }
            // 裾の線（下の▲とつながって見えるよう、山の欄の下端まで引く）
            EditorGUI.DrawRect(new Rect(left - 0.5f, rect.y, 1f, rect.height), EdgeLine);
            EditorGUI.DrawRect(new Rect(right - 0.5f, rect.y, 1f, rect.height), EdgeLine);
        }

        private static void Ensure(ref float[] heights, ref Color[] colors, int count)
        {
            if (heights.Length == count) return;
            heights = new float[count];
            colors = new Color[count];
        }
    }
}
