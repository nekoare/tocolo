using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>ホイール・スライダー・最近の色の書き込み先: 色 1（箱の下端）・色 2（箱の上端）・途中の色（一覧の番号）</summary>
    internal readonly struct GradientTarget
    {
        // 0 = 色 1、1 = 色 2、2 以上 = 途中の色（2 + 番号）。SessionState にこの値のまま覚える
        internal readonly int value;

        private GradientTarget(int value)
        {
            this.value = value;
        }

        internal static readonly GradientTarget Start = new GradientTarget(0);
        internal static readonly GradientTarget End = new GradientTarget(1);
        internal static GradientTarget Mid(int index) => new GradientTarget(2 + index);
        internal static GradientTarget FromValue(int value) => new GradientTarget(Mathf.Max(0, value));

        internal bool IsStart => value == 0;
        internal bool IsEnd => value == 1;
        /// <summary>途中の色の番号（途中の色でなければ -1）</summary>
        internal int MidIndex => value >= 2 ? value - 2 : -1;

        public override bool Equals(object obj) => obj is GradientTarget other && other.value == value;
        public override int GetHashCode() => value;
    }

    /// <summary>
    /// グラデーションの見本の帯と、その下の▲（左端＝色 1・右端＝色 2・あいだ＝途中の色）。
    /// ▲を押すとその色を選び、途中の▲は左右にドラッグして動かせる。▲の無い所を押すとそこに途中の色を足す。
    /// 両端の▲は箱の端に当たるので動かさない（範囲は Scene の箱で決める）
    /// </summary>
    internal static class GradientStopsGUI
    {
        /// <summary>帯の下の▲を並べる高さ（px）</summary>
        internal const float StripHeight = MatchMountainGUI.MarkerStrip;

        /// <summary>▲をつかめる幅（px）</summary>
        private const float Grab = 7f;
        /// <summary>この差より近い▲は重なっているとみなす（px）</summary>
        private const float Overlap = 0.5f;
        private const int PreviewSteps = 48;

        private static readonly int s_hint = "ClickRecolor.GradientStops".GetHashCode();
        private static GradientTarget s_dragging;

        private static readonly Color EndOutline = new Color(0.85f, 0.85f, 0.85f, 1f);

        internal enum Action { None, Select, Add, Move }

        internal struct Result
        {
            public Action action;
            /// <summary>Select / Move の対象（Add なら足した後の途中の色）</summary>
            public GradientTarget target;
            /// <summary>Add / Move の位置（0..1）</summary>
            public float position;
        }

        /// <summary>帯（rect の上側）と▲（下の StripHeight）を描き、押した・動かした結果を返す。データは書き換えない（呼び出し側が Undo つきで書く）</summary>
        internal static Result Draw(Rect rect, RecolorEdit edit, GradientTarget selected)
        {
            var result = new Result { action = Action.None };
            int id = GUIUtility.GetControlID(s_hint, FocusType.Passive, rect);
            // 両端の▲が rect の外へはみ出さないよう、帯と▲の列を▲の半分の幅だけ内側に寄せる（端の▲の中心が帯の端）
            float inset = Mathf.Ceil(MatchMountainGUI.ThickMarkerHalfWidth);
            var bar = new Rect(rect.x + inset, rect.y, Mathf.Max(0f, rect.width - inset * 2f), rect.height - StripHeight);
            var strip = new Rect(bar.x, bar.yMax, bar.width, StripHeight);
            var stops = edit.gradientMidStops ?? new List<RecolorGradientStop>();
            var e = Event.current;

            if (e.type == EventType.Repaint)
            {
                DrawBar(bar, edit);
                Part(strip, 0f, edit.targetColor, selected.IsStart);
                Part(strip, 1f, edit.gradientColor, selected.IsEnd);
                for (int i = 0; i < stops.Count; i++) Part(strip, stops[i].position, stops[i].color, selected.MidIndex == i);
                // 選んでいる▲は、帯の上にも縦の線を引いて位置を見せる（重なった▲のどれを選んでいるかは枠の太さで分かる）
                float selectedPosition = selected.IsEnd ? 1f
                    : selected.MidIndex >= 0 && selected.MidIndex < stops.Count ? stops[selected.MidIndex].position : 0f;
                DrawSelectedLine(bar, ToX(bar, selectedPosition));
            }
            if (!GUI.enabled) return result;

            for (int i = 0; i < stops.Count; i++)
            {
                float x = ToX(strip, stops[i].position);
                EditorGUIUtility.AddCursorRect(new Rect(x - Grab, strip.y, Grab * 2f, strip.height), MouseCursor.ResizeHorizontal);
            }

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button != 0 || !rect.Contains(e.mousePosition)) break;
                    var picked = Pick(e.mousePosition.x, strip.x, strip.width, Positions(stops), selected);
                    if (picked.HasValue)
                    {
                        result = new Result { action = Action.Select, target = picked.Value };
                        if (picked.Value.MidIndex >= 0) StartDrag(id, picked.Value);
                    }
                    else if (stops.Count < RecolorGradientStop.MaxCount)
                    {
                        var added = GradientTarget.Mid(stops.Count);
                        result = new Result { action = Action.Add, target = added, position = ToPosition(strip, e.mousePosition.x) };
                        StartDrag(id, added);
                    }
                    e.Use();
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl != id) break;
                    result = new Result { action = Action.Move, target = s_dragging, position = ToPosition(strip, e.mousePosition.x) };
                    GUI.changed = true;
                    e.Use();
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl != id) break;
                    GUIUtility.hotControl = 0;
                    e.Use();
                    break;
            }
            return result;
        }

        private static void StartDrag(int id, GradientTarget target)
        {
            s_dragging = target;
            GUIUtility.hotControl = id;
        }

        private static List<float> Positions(List<RecolorGradientStop> stops)
        {
            var positions = new List<float>(stops.Count);
            foreach (var stop in stops) positions.Add(stop.position);
            return positions;
        }

        /// <summary>
        /// 押した x（px）でつかむ▲。いちばん近い▲（つかめる幅の中）を選ぶ。重なった▲（段差にした 2 色・端と同じ位置の色）は、
        /// 選んでいる▲以外を先にする（押すたびに重なった▲を順に選べる。いちばん近い物だけでは下の▲を二度と選べない）。何も無ければ null
        /// </summary>
        internal static GradientTarget? Pick(float mx, float left, float width, IReadOnlyList<float> midPositions, GradientTarget selected)
        {
            var candidates = new List<(GradientTarget target, float distance)>
            {
                (GradientTarget.Start, Mathf.Abs(mx - left)),
                (GradientTarget.End, Mathf.Abs(mx - (left + width))),
            };
            for (int i = 0; i < midPositions.Count; i++)
            {
                candidates.Add((GradientTarget.Mid(i), Mathf.Abs(mx - (left + Mathf.Clamp01(midPositions[i]) * width))));
            }
            float nearest = float.MaxValue;
            foreach (var c in candidates) nearest = Mathf.Min(nearest, c.distance);
            if (nearest > Grab) return null;

            var closest = candidates.FindAll(c => c.distance <= nearest + Overlap);
            int at = closest.FindIndex(c => c.target.Equals(selected));
            return closest[at >= 0 ? (at + 1) % closest.Count : 0].target;
        }

        private static void Part(Rect strip, float position, Color color, bool active)
        {
            var outline = active ? MatchMountainGUI.MarkerOutlineActive : EndOutline;
            MatchMountainGUI.DrawMarker(strip, ToX(strip, position), new Color(color.r, color.g, color.b, 1f), outline, thickOutline: active);
        }

        /// <summary>帯の上の縦線（暗い縁取りの中に選択色。帯がどんな色でも見えるように 2 色で引く）。両端は帯の内側に収める</summary>
        private static void DrawSelectedLine(Rect bar, float x)
        {
            x = Mathf.Clamp(x, bar.x + 1.5f, bar.xMax - 1.5f);
            EditorGUI.DrawRect(new Rect(x - 1.5f, bar.y, 3f, bar.height), SelectedLineShadow);
            EditorGUI.DrawRect(new Rect(x - 0.5f, bar.y, 1f, bar.height), MatchMountainGUI.MarkerOutlineActive);
        }

        private static readonly Color SelectedLineShadow = new Color(0f, 0f, 0f, 0.6f);

        private static float ToX(Rect rect, float position) => rect.x + Mathf.Clamp01(position) * rect.width;

        private static float ToPosition(Rect rect, float x) => rect.width > 0f ? Mathf.Clamp01((x - rect.x) / rect.width) : 0f;

        /// <summary>色 1 → 途中の色 → 色 2 の見本を描く（パイプラインと同じ混ぜ方）</summary>
        private static void DrawBar(Rect rect, RecolorEdit edit)
        {
            var p = Palette(edit, out var lab1);
            float w = rect.width / PreviewSteps;
            for (int i = 0; i < PreviewSteps; i++)
            {
                var c = ToSrgb(ColorShiftCpu.GradientTargetOklab(lab1, p, (i + 0.5f) / PreviewSteps));
                EditorGUI.DrawRect(new Rect(rect.x + i * w, rect.y, w + 0.5f, rect.height), c);
            }
        }

        /// <summary>位置 t の目標色（sRGB）。途中の色を足すときの初期値（足しただけでは見た目が変わらない）と Scene の箱に使う</summary>
        internal static Color ColorAt(RecolorEdit edit, float t)
        {
            var p = Palette(edit, out var lab1);
            return ToSrgb(ColorShiftCpu.GradientTargetOklab(lab1, p, Mathf.Clamp01(t)));
        }

        private static ShiftParams Palette(RecolorEdit edit, out Vector3 lab1)
        {
            lab1 = OklabConverter.LinearRGBToOklab(OklabConverter.SRGBToLinear(edit.targetColor));
            return new ShiftParams
            {
                targetOklab2 = OklabConverter.LinearRGBToOklab(OklabConverter.SRGBToLinear(edit.gradientColor)),
                midStops = ShiftParams.SortedMidStops(edit.gradientMidStops),
            };
        }

        private static Color ToSrgb(Vector3 lab)
        {
            var c = OklabConverter.LinearToSRGB(OklabConverter.OklabToLinearRGB(lab));
            return new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
        }
    }
}
