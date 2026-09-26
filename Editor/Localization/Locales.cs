// 流用元: com.nekoare.chimera-hair-master/Editor/Localization/CHMLocales.cs
using System.Linq;
using nadena.dev.ndmf.localization;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Localization
{
    internal static class Locales
    {
        internal const string JaGuid = "32ffdde2d05f45fc8732da60ebfbcf68"; // ja-JP.po
        internal const string EnGuid = "d8bf42f49e1449eca4d21f146f37d6ce"; // en-US.po

        // 静的初期化時に 1 回ロードし、以後は NDMF の Localizer.ReloadLocalizations() でしか更新されない。
        // InitializeOnLoad や NDMF Plugin の静的初期化から Tr を呼ばないこと。
        // その時点では AssetDatabase が GUID を引けず、全文言が <key> 表示のままになる。
        public static readonly Localizer L = new Localizer(
            "ja-JP",
            () =>
            {
                var assets = new[] { JaGuid, EnGuid }
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .Select(AssetDatabase.LoadAssetAtPath<LocalizationAsset>)
                    .Where(a => a != null)
                    .ToList();
                if (assets.Count == 0)
                {
                    Debug.LogWarning("[Tocolo] ローカライズファイル（.po）が解決できません。.meta の GUID を確認してください");
                }
                return assets;
            }
        );

        public static string Tr(string key) => L.GetLocalizedString(key);

        public static string Tr(string key, params object[] args) => string.Format(Tr(key), args);

        private static readonly (string code, string display)[] Languages =
        {
            ("ja-jp", "日本語"),
            ("en-us", "English"),
        };

        /// <summary>
        /// 右寄せで言語切替ドロップダウンを描画する。
        /// NDMF の LanguagePrefs を直接操作するため、他の NDMF 連携ツールとも同期する。
        /// </summary>
        public static void DrawLanguagePicker()
        {
            var current = LanguagePrefs.Language;
            int selected = 0;
            for (int i = 0; i < Languages.Length; i++)
            {
                if (Languages[i].code == current) { selected = i; break; }
            }

            var displays = new string[Languages.Length];
            for (int i = 0; i < Languages.Length; i++) displays[i] = Languages[i].display;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label("Language:", GUILayout.ExpandWidth(false));
                int newSelected = EditorGUILayout.Popup(selected, displays, GUILayout.Width(100));
                if (newSelected != selected)
                {
                    LanguagePrefs.Language = Languages[newSelected].code;
                }
            }
        }
    }
}
