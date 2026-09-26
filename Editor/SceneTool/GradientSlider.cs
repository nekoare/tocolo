using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// Unity のカラーピッカー風の 1 行スライダー: ラベル ＋ グラデーションを敷いたトラック ＋ 数値欄。
    /// スライダーは GUI.HorizontalSlider（ドラッグ中は hotControl を持つ）、数値欄は Delayed 系（Enter かフォーカスを外したときに確定）。
    /// 変更は GUI.changed で知らせるので EditorGUI.BeginChangeCheck の中で使える
    /// </summary>
    internal static class GradientSlider
    {
        /// <summary>ラベルの幅（既定）</summary>
        internal const float LabelWidth = 20f;

        /// <summary>数値欄の幅</summary>
        internal const float FieldWidth = 44f;

        /// <summary>トラック（グラデーション）の高さ</summary>
        internal const float TrackHeight = 12f;

        /// <summary>色が変わるたびに作り直すグラデーションの横の画素数</summary>
        internal const int GradientSize = 32;

        private const int HueGradientSize = 64;
        private const float Gap = 4f;

        private static Texture2D s_hue;

        /// <summary>破棄し忘れないよう、作ったキャッシュを覚えておく（アセンブリの再読み込み前にまとめて破棄する）</summary>
        private static readonly List<Cache> s_caches = new List<Cache>();

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            AssemblyReloadEvents.beforeAssemblyReload += DestroyTextures;
        }

        private static void DestroyTextures()
        {
            if (s_hue != null) UnityEngine.Object.DestroyImmediate(s_hue);
            s_hue = null;
            foreach (var cache in s_caches.ToArray()) cache.Dispose();
        }

        /// <summary>色相の虹（左が 0、右が 1。固定なので 1 回だけ作る）</summary>
        internal static Texture2D HueTexture
        {
            get
            {
                if (s_hue == null)
                {
                    s_hue = NewTexture(HueGradientSize, "ClickRecolor.HueGradient");
                    Fill(s_hue, t => Color.HSVToRGB(t, 1f, 1f));
                }
                return s_hue;
            }
        }

        /// <summary>1 行を描く（ラベルの幅は既定の 20）。戻り値は新しい値（操作が無ければ value）</summary>
        internal static float Draw(string label, float value, float min, float max, Texture2D gradient, string format)
        {
            return Draw(label, value, min, max, gradient, format, LabelWidth);
        }

        /// <summary>
        /// 1 行を描く。format は数値欄の表示の桁（"0" なら整数、"0.00" なら小数 2 桁）。
        /// labelWidth は並べる行どうしでトラックの位置を揃えるために、呼び出し側でまとめて決める
        /// </summary>
        internal static float Draw(string label, float value, float min, float max, Texture2D gradient, string format, float labelWidth)
        {
            Rect row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            var labelRect = new Rect(row.x, row.y, labelWidth, row.height);
            var fieldRect = new Rect(row.xMax - FieldWidth, row.y, FieldWidth, row.height);
            float trackX = labelRect.xMax + Gap;
            var trackRect = new Rect(trackX, row.y, Mathf.Max(0f, fieldRect.xMin - Gap - trackX), row.height);

            GUI.Label(labelRect, label, EditorStyles.label);

            GUIStyle thumb = GUI.skin.horizontalSliderThumb;
            float thumbWidth = thumb.fixedWidth > 0f ? thumb.fixedWidth : 10f;
            float thumbHeight = thumb.fixedHeight > 0f ? thumb.fixedHeight : TrackHeight;
            float sliderHeight = Mathf.Min(row.height, Mathf.Max(thumbHeight, TrackHeight));
            var sliderRect = new Rect(trackRect.x, row.center.y - sliderHeight * 0.5f, trackRect.width, sliderHeight);

            // つまみの中心が値の位置なので、グラデーションはつまみの半分ずつ内側に敷く（端の色とつまみの位置を合わせる）
            if (gradient != null && Event.current.type == EventType.Repaint)
            {
                var bar = new Rect(
                    trackRect.x + thumbWidth * 0.5f,
                    row.center.y - TrackHeight * 0.5f,
                    Mathf.Max(0f, trackRect.width - thumbWidth),
                    TrackHeight);
                GUI.DrawTexture(bar, gradient, ScaleMode.StretchToFill, false);
            }

            float result = GUI.HorizontalSlider(sliderRect, value, min, max, GUIStyle.none, thumb);

            // 数値欄は表示の桁に丸めた値を出し、入力で変わったときだけ採る（丸めただけで値が変わらないように）
            int decimals = DecimalsOf(format);
            if (decimals == 0)
            {
                int shown = Mathf.RoundToInt(value);
                int typed = EditorGUI.DelayedIntField(fieldRect, shown);
                if (typed != shown) result = typed;
            }
            else
            {
                float shown = (float)Math.Round(value, decimals);
                float typed = EditorGUI.DelayedFloatField(fieldRect, shown);
                if (typed != shown) result = typed;
            }
            return Mathf.Clamp(result, min, max);
        }

        /// <summary>"0" → 0、"0.00" → 2、"F1" → 1（読めなければ 2）</summary>
        private static int DecimalsOf(string format)
        {
            if (string.IsNullOrEmpty(format)) return 2;
            if (format[0] == 'F' || format[0] == 'f')
            {
                return int.TryParse(format.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 2;
            }
            int dot = format.IndexOf('.');
            return dot < 0 ? 0 : format.Length - dot - 1;
        }

        /// <summary>
        /// グラデーションの画素（左端 t = 0、右端 t = 1 の n 画素、不透明）。n は 2 以上
        /// </summary>
        internal static Color[] BuildGradient(Func<float, Color> colorAt, int n)
        {
            var pixels = new Color[n];
            for (int i = 0; i < n; i++)
            {
                var c = colorAt(n > 1 ? i / (float)(n - 1) : 0f);
                c.a = 1f;
                pixels[i] = c;
            }
            return pixels;
        }

        private static Texture2D NewTexture(int width, string name)
        {
            // ColorWheelGUI と同じく sRGB（linear: false）で持つ
            return new Texture2D(width, 1, TextureFormat.RGBA32, false, false)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
        }

        private static void Fill(Texture2D tex, Func<float, Color> colorAt)
        {
            tex.SetPixels(BuildGradient(colorAt, tex.width));
            tex.Apply(false, false);
        }

        /// <summary>
        /// 現在の色によって変わるグラデーション 1 本。前回の色（key）と違うときだけ作り直す
        /// </summary>
        internal sealed class Cache : IDisposable
        {
            private readonly int _size;
            private readonly string _name;
            private Vector3 _key;
            private bool _hasKey;

            internal Texture2D Texture { get; private set; }

            internal Cache(int size, string name)
            {
                _size = size;
                _name = name;
            }

            /// <summary>key（h/s/v や r/g/b）が前回と違うか、テクスチャが無ければ作り直す。作り直したら true</summary>
            internal bool Update(Vector3 key, Func<float, Color> colorAt)
            {
                if (Texture != null && _hasKey && key.x == _key.x && key.y == _key.y && key.z == _key.z) return false;
                if (Texture == null)
                {
                    Texture = NewTexture(_size, _name);
                    // テクスチャを持っている間だけ、再読み込み前の破棄の対象に入れる
                    if (!s_caches.Contains(this)) s_caches.Add(this);
                }
                Fill(Texture, colorAt);
                _key = key;
                _hasKey = true;
                return true;
            }

            public void Dispose()
            {
                if (Texture != null) UnityEngine.Object.DestroyImmediate(Texture);
                Texture = null;
                _hasKey = false;
                s_caches.Remove(this);
            }
        }
    }
}
