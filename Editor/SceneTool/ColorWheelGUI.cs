using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// IMGUI 手描きのカラーホイール（HSV）。外周のリング = 色相（右が 0、反時計回り）、
    /// 内側の正方形 = 現在の色相での 彩度（横、右が 1）× 明るさ（縦、上が 1）。
    /// ドラッグ中は毎イベント GUI.changed を立てるので EditorGUI.BeginChangeCheck の中で使える。
    /// 掴んだ場所（リング／正方形）はドラッグが終わるまで変えない（リングを掴んだまま外へ出ても色相だけ動く）
    /// </summary>
    internal static class ColorWheelGUI
    {
        /// <summary>リングの太さ（外径に対する割合）</summary>
        private const float RingThickness = 0.16f;

        /// <summary>正方形の半辺（リング内径に対する割合。√2/2 未満でリングに重ならない）</summary>
        private const float SquareHalfRatio = 0.66f;

        private const int RingTextureSize = 128;
        private const int SquareTextureSize = 64;
        private const int MarkerTextureSize = 16;
        private const float MarkerSize = 12f;

        internal enum Part { None, Ring, Square }

        private static Texture2D s_ring;
        private static Texture2D s_square;
        private static Texture2D s_marker;
        private static float s_squareHue = -1f;

        /// <summary>ドラッグ中に掴んでいる場所</summary>
        private static Part s_dragPart;

        // 明るさ 0 や彩度 0 の色は RGB から色相が決まらないので、直前に作った色の HSV を覚えておく
        private static bool s_hasCache;
        private static Color s_cachedColor;
        private static Vector3 s_cachedHsv;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DestroyTextures;
        }

        private static void DestroyTextures()
        {
            if (s_ring != null) Object.DestroyImmediate(s_ring);
            if (s_square != null) Object.DestroyImmediate(s_square);
            if (s_marker != null) Object.DestroyImmediate(s_marker);
            s_ring = s_square = s_marker = null;
            s_squareHue = -1f;
        }

        /// <summary>
        /// ホイールを描いてマウス操作を受ける。戻り値は新しい色（操作が無ければ current）
        /// </summary>
        internal static Color Draw(Rect rect, Color current, int controlIdHint)
        {
            int id = GUIUtility.GetControlID(controlIdHint, FocusType.Passive, rect);
            var e = Event.current;
            Vector3 hsv = GetHsv(current);
            Color result = current;

            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && rect.Contains(e.mousePosition))
                    {
                        var part = HitTest(rect, e.mousePosition);
                        if (part != Part.None)
                        {
                            GUIUtility.hotControl = id;
                            s_dragPart = part;
                            result = FromHsv(DragToHsv(rect, e.mousePosition, hsv, part));
                            GUI.changed = true;
                            e.Use();
                        }
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        result = FromHsv(DragToHsv(rect, e.mousePosition, hsv, s_dragPart));
                        GUI.changed = true;
                        e.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id)
                    {
                        GUIUtility.hotControl = 0;
                        s_dragPart = Part.None;
                        e.Use();
                    }
                    break;
                case EventType.Repaint:
                    DrawWheel(rect, hsv);
                    break;
            }
            return result;
        }

        /// <summary>座標がリング・正方形のどちらにあるか（どちらでもなければ None）</summary>
        internal static Part HitTest(Rect rect, Vector2 point)
        {
            GetRingRadii(rect, out float outer, out float inner);
            float dist = Vector2.Distance(point, rect.center);
            if (dist <= outer && dist >= inner) return Part.Ring;
            if (GetSquareRect(rect).Contains(point)) return Part.Square;
            return Part.None;
        }

        /// <summary>
        /// 座標 → HSV（x = 色相, y = 彩度, z = 明るさ、各 0〜1）。
        /// リング上なら色相だけ、正方形内なら彩度と明るさだけ変える。どちらでもなければ current のまま
        /// </summary>
        internal static Vector3 PointToHsv(Rect rect, Vector2 point, Color current)
        {
            Color.RGBToHSV(current, out float h, out float s, out float v);
            return PointToHsv(rect, point, new Vector3(h, s, v));
        }

        internal static Vector3 PointToHsv(Rect rect, Vector2 point, Vector3 currentHsv)
        {
            return DragToHsv(rect, point, currentHsv, HitTest(rect, point));
        }

        /// <summary>掴んだ場所 part に応じて座標を HSV にする（範囲外に出ても、リングなら角度、正方形なら端に寄せて使う）</summary>
        private static Vector3 DragToHsv(Rect rect, Vector2 point, Vector3 hsv, Part part)
        {
            switch (part)
            {
                case Part.Ring:
                {
                    // GUI 座標は y が下向きなので、画面上の反時計回りにするため y を反転する
                    Vector2 d = point - rect.center;
                    float angle = Mathf.Atan2(-d.y, d.x);
                    float hue = Mathf.Repeat(angle / (2f * Mathf.PI), 1f);
                    return new Vector3(hue, hsv.y, hsv.z);
                }
                case Part.Square:
                {
                    var sq = GetSquareRect(rect);
                    float s = Mathf.Clamp01((point.x - sq.xMin) / sq.width);
                    float v = Mathf.Clamp01(1f - (point.y - sq.yMin) / sq.height);
                    return new Vector3(hsv.x, s, v);
                }
                default:
                    return hsv;
            }
        }

        /// <summary>リングの外径・内径（rect の短辺に内接する円が外径）</summary>
        internal static void GetRingRadii(Rect rect, out float outer, out float inner)
        {
            outer = Mathf.Min(rect.width, rect.height) * 0.5f;
            inner = outer * (1f - RingThickness);
        }

        /// <summary>内側の正方形（彩度×明るさ）の矩形</summary>
        internal static Rect GetSquareRect(Rect rect)
        {
            GetRingRadii(rect, out _, out float inner);
            float half = inner * SquareHalfRatio;
            return new Rect(rect.center.x - half, rect.center.y - half, half * 2f, half * 2f);
        }

        /// <summary>
        /// 色 → HSV。直前に FromHsv で作った色と同じなら、そのときの HSV を返す（黒や灰色でも色相・彩度を失わない）
        /// </summary>
        internal static Vector3 GetHsv(Color color)
        {
            if (s_hasCache && color.r == s_cachedColor.r && color.g == s_cachedColor.g && color.b == s_cachedColor.b)
            {
                return s_cachedHsv;
            }
            Color.RGBToHSV(color, out float h, out float s, out float v);
            return new Vector3(h, s, v);
        }

        /// <summary>HSV → 色（不透明）。GetHsv が色相を引き継げるよう、作った色と HSV を覚える</summary>
        internal static Color FromHsv(Vector3 hsv)
        {
            // 色相は折り返さず 0〜1 に収める（1 は 0 と同じ色。スライダーの 360 が 0 に跳ねないように）
            var clamped = new Vector3(Mathf.Clamp01(hsv.x), Mathf.Clamp01(hsv.y), Mathf.Clamp01(hsv.z));
            var color = Color.HSVToRGB(clamped.x, clamped.y, clamped.z);
            color.a = 1f;
            s_cachedColor = color;
            s_cachedHsv = clamped;
            s_hasCache = true;
            return color;
        }

        private static void DrawWheel(Rect rect, Vector3 hsv)
        {
            EnsureTextures(hsv.x);
            GetRingRadii(rect, out float outer, out float inner);
            var ringRect = new Rect(rect.center.x - outer, rect.center.y - outer, outer * 2f, outer * 2f);
            GUI.DrawTexture(ringRect, s_ring, ScaleMode.StretchToFill, true);
            var sq = GetSquareRect(rect);
            GUI.DrawTexture(sq, s_square, ScaleMode.StretchToFill, false);

            // 現在の色の位置に丸印（リング上の色相と、正方形の中の彩度×明るさ）
            float angle = hsv.x * 2f * Mathf.PI;
            float mid = (outer + inner) * 0.5f;
            DrawMarker(rect.center + new Vector2(Mathf.Cos(angle) * mid, -Mathf.Sin(angle) * mid));
            DrawMarker(new Vector2(sq.xMin + hsv.y * sq.width, sq.yMin + (1f - hsv.z) * sq.height));
        }

        private static void DrawMarker(Vector2 center)
        {
            var r = new Rect(center.x - MarkerSize * 0.5f, center.y - MarkerSize * 0.5f, MarkerSize, MarkerSize);
            GUI.DrawTexture(r, s_marker, ScaleMode.StretchToFill, true);
        }

        private static void EnsureTextures(float hue)
        {
            if (s_ring == null) s_ring = CreateRingTexture();
            if (s_marker == null) s_marker = CreateMarkerTexture();
            if (s_square == null)
            {
                s_square = NewTexture(SquareTextureSize, "ClickRecolor.WheelSquare");
                s_squareHue = -1f;
            }
            if (Mathf.Abs(hue - s_squareHue) > 1e-5f)
            {
                FillSquare(s_square, hue);
                s_squareHue = hue;
            }
        }

        private static Texture2D NewTexture(int size, string name)
        {
            return new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
        }

        /// <summary>色相のリング。外周・内周は 1 テクセル分ぼかして縁のギザギザを抑える</summary>
        private static Texture2D CreateRingTexture()
        {
            int n = RingTextureSize;
            var tex = NewTexture(n, "ClickRecolor.WheelRing");
            var pixels = new Color32[n * n];
            float c = n * 0.5f;
            float outer = n * 0.5f;
            float inner = outer * (1f - RingThickness);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    // テクスチャは下が y=0（画面の上下と同じ向きで描かれる）
                    float dx = x + 0.5f - c, dy = y + 0.5f - c;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Clamp01(outer - dist + 0.5f) * Mathf.Clamp01(dist - inner + 0.5f);
                    float hue = Mathf.Repeat(Mathf.Atan2(dy, dx) / (2f * Mathf.PI), 1f);
                    var col = Color.HSVToRGB(hue, 1f, 1f);
                    col.a = alpha;
                    pixels[y * n + x] = col;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>彩度（横）×明るさ（縦、上が 1）の正方形を色相 hue で塗る</summary>
        private static void FillSquare(Texture2D tex, float hue)
        {
            int n = tex.width;
            var pixels = new Color32[n * n];
            Color pure = Color.HSVToRGB(hue, 1f, 1f);
            for (int y = 0; y < n; y++)
            {
                float v = y / (float)(n - 1);
                for (int x = 0; x < n; x++)
                {
                    float s = x / (float)(n - 1);
                    // HSV → RGB: v × ((1 − s) × 白 + s × 純色)
                    var col = new Color(
                        v * (1f - s + s * pure.r),
                        v * (1f - s + s * pure.g),
                        v * (1f - s + s * pure.b),
                        1f);
                    pixels[y * n + x] = col;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
        }

        /// <summary>丸印（白い輪を黒で縁取り。明るい色・暗い色のどちらの上でも見える）</summary>
        private static Texture2D CreateMarkerTexture()
        {
            int n = MarkerTextureSize;
            var tex = NewTexture(n, "ClickRecolor.WheelMarker");
            var pixels = new Color32[n * n];
            float c = n * 0.5f;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - c, dy = y + 0.5f - c;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    // 半径 4.5〜6.5 が白い輪、その内外 1 テクセルが黒い縁
                    float white = Mathf.Clamp01(6.5f - dist + 0.5f) * Mathf.Clamp01(dist - 4.5f + 0.5f);
                    float border = Mathf.Clamp01(7.5f - dist + 0.5f) * Mathf.Clamp01(dist - 3.5f + 0.5f);
                    var col = Color.Lerp(Color.black, Color.white, white);
                    col.a = Mathf.Max(white, border);
                    pixels[y * n + x] = col;
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }
    }
}
