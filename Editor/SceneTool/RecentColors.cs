using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// 最近使った色（最大 5 色、新しい順）。プロジェクト単位で EditorUserSettings に "#RRGGBB,#RRGGBB,..." で保存する。
    /// 色は 16 進（8bit）に丸めて保存するので、同じ 16 進になる色は同じ色として扱う
    /// </summary>
    internal static class RecentColors
    {
        internal const string ConfigKey = "ClickRecolor.RecentColors";
        internal const int Capacity = 5;

        // 毎イベント描画から呼ばれるので、保存文字列が変わったときだけ読み直す
        private static string s_cachedRaw;
        private static List<Color> s_cached = new List<Color>();

        internal static IReadOnlyList<Color> Get()
        {
            string raw = EditorUserSettings.GetConfigValue(ConfigKey) ?? string.Empty;
            if (raw != s_cachedRaw)
            {
                s_cached = Parse(raw);
                s_cachedRaw = raw;
            }
            return s_cached;
        }

        /// <summary>色を先頭に入れる。同じ色が既にあれば先頭へ移し、5 色を超えた分は古い方から捨てる</summary>
        internal static void Push(Color color)
        {
            string hex = ToHex(color);
            var list = new List<Color>(Get());
            list.RemoveAll(c => ToHex(c) == hex);
            list.Insert(0, color);
            if (list.Count > Capacity) list.RemoveRange(Capacity, list.Count - Capacity);
            EditorUserSettings.SetConfigValue(ConfigKey, Serialize(list));
        }

        internal static void Clear()
        {
            // null で消せるかは保証が無いので空文字にする（空文字は「色なし」として読む）
            EditorUserSettings.SetConfigValue(ConfigKey, string.Empty);
        }

        /// <summary>"#RRGGBB,#RRGGBB,..." を色の一覧にする。読めない項目は飛ばし、最大 5 色まで</summary>
        internal static List<Color> Parse(string raw)
        {
            var list = new List<Color>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var item in raw.Split(','))
            {
                string s = item.Trim();
                if (s.Length == 0) continue;
                if (ColorUtility.TryParseHtmlString(s, out var c)) list.Add(c);
                if (list.Count >= Capacity) break;
            }
            return list;
        }

        internal static string Serialize(IReadOnlyList<Color> colors)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < colors.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ToHex(colors[i]));
            }
            return sb.ToString();
        }

        private static string ToHex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);
    }
}
