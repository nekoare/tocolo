// 流用元: com.nekoare.chimera-hair-master/Editor/Inspector/ChimeraHairMasterEditor.cs（DrawHelpMark / DrawHelpBoxIfOpen）
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Localization
{
    /// <summary>
    /// 項目の横の「？」ボタンと、押すと開く説明（HelpBox）。説明の文言は .po の key（"Help:..."）。
    /// 開閉はエディタのセッション中だけ覚え、Inspector と Scene パネルで共有する（複数同時に開ける）
    /// </summary>
    internal static class HelpMark
    {
        private static readonly HashSet<string> s_open = new HashSet<string>();

        /// <summary>閉じている「？」ボタンの不透明度</summary>
        private const float IdleAlpha = 0.45f;

        /// <summary>
        /// 押された key。開閉は次の Layout で切り替える。押したイベント（MouseUp）の途中で HelpBox が増えると、
        /// Layout で数えた部品の数と食い違って IMGUI がエラーを出すため
        /// </summary>
        private static readonly List<string> s_pendingToggles = new List<string>();

        internal static bool IsOpen(string key)
        {
            ApplyPendingOnLayout();
            return s_open.Contains(key);
        }

        /// <summary>
        /// 行内（Horizontal の中）に「？」ボタンを置く。押すと key の説明の開閉を切り替える。
        /// ボタンは GUI.changed を立てるが、周りの ChangeCheck（スライダーの変更検知）に拾わせないよう元に戻す
        /// </summary>
        internal static void Draw(string key)
        {
            bool open = IsOpen(key);
            bool changed = GUI.changed;
            // 閉じている「？」は薄くして、ブロックの背景色を透かす（主張を抑える。ユーザー要望 2026-10-03）。開いている「✕」は元の濃さ
            var previousColor = GUI.color;
            if (!open) GUI.color = new Color(previousColor.r, previousColor.g, previousColor.b, previousColor.a * IdleAlpha);
            bool clicked = GUILayout.Button(open ? "✕" : "?", EditorStyles.miniButton, GUILayout.Width(20f));
            GUI.color = previousColor;
            if (clicked)
            {
                if (!s_pendingToggles.Contains(key)) s_pendingToggles.Add(key);
                GUI.FocusControl(null);
            }
            GUI.changed = changed;
        }

        /// <summary>「？」で開かれていれば key の説明を HelpBox で出す。対象の行（Horizontal の終わり）の直後に呼ぶ</summary>
        internal static void DrawBoxIfOpen(string key)
        {
            if (IsOpen(key)) EditorGUILayout.HelpBox(Locales.Tr(key), MessageType.Info);
        }

        /// <summary>見出し付きで出す（同じ場所に複数の説明が並ぶとき、どれの説明か分かるようにする）。title はそのまま 1 行目に出す</summary>
        internal static void DrawBoxIfOpen(string key, string title)
        {
            if (IsOpen(key)) EditorGUILayout.HelpBox($"【{title}】\n{Locales.Tr(key)}", MessageType.Info);
        }

        /// <summary>Layout イベントのときだけ、押された key の開閉を反映する（同じイベントの Layout と Repaint で部品の数を揃える）</summary>
        private static void ApplyPendingOnLayout()
        {
            if (s_pendingToggles.Count == 0 || Event.current == null || Event.current.type != EventType.Layout) return;
            foreach (var key in s_pendingToggles)
            {
                if (!s_open.Remove(key)) s_open.Add(key);
            }
            s_pendingToggles.Clear();
        }
    }
}
