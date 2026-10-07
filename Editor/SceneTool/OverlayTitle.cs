using nadena.dev.ndmf.localization;
using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor.Overlays;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// 小窓の見出しを今の言語にする。Overlay 属性の表示名は固定の文字列なので、そのままではどの言語でも日本語で出る。
    /// 属性の表示名は .po を引けなかったときの見出しとして残す
    /// </summary>
    internal static class OverlayTitle
    {
        /// <summary>
        /// 見出しを key の訳にし、言語を切り替えたら付け直す。OnCreated の EditorApplication.delayCall から呼ぶこと:
        /// Locales は最初に触れた時点で .po を 1 回だけ読むので、起動直後（アセットをまだ引けない時）に呼ぶとそのセッション中の全文言が key のままになる
        /// </summary>
        internal static void Bind(Overlay overlay, string key)
        {
            Apply(overlay, key);
            // 小窓が捨てられたら通知も外れる（NDMF は弱参照で持つ）
            LanguagePrefs.RegisterLanguageChangeCallback(overlay, o => Apply(o, key));
        }

        private static void Apply(Overlay overlay, string key)
        {
            // Tr は見つからないと "<key>" を返すので、見出しを壊さないよう訳があるときだけ替える
            if (Locales.L.TryGetLocalizedString(key, out string title)) overlay.displayName = title;
        }
    }
}
