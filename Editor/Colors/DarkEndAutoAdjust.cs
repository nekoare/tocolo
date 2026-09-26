// 流用元: com.nekoare.chimera-hair-master/Runtime/ChimeraHairMaster.cs（UpdateOklabLDarkEndRatioFromTargetColor と定数）
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Colors
{
    /// <summary>
    /// 変更後の色（sRGB）から「暗部の明るさ」の自動値を求める純関数。
    ///
    /// 暗い側（V≤0.5）は V 三角波（0.3 + 1.2V と同値）、中間（V=0.5）は 0.9 のピーク
    /// （複数テクスチャの明度感を揃えるフラット化）。明るい側（V>0.5）の端点だけを
    /// 彩度（Oklab C）に応じて振り分ける:
    ///   - 無彩色（白・明グレー）: 0.7 → 白を指定しても暗部が中間グレーまで落ちない
    ///   - ビビッド（純赤・純青）: 0.4 → 深い影のルックをほぼ維持
    ///   - 中間彩度（パステル・金髪）: smoothstep でブレンド
    /// 端点の値は流用元での目視チューニング値をそのまま使う。
    ///
    /// 手動値を上書きしないかどうかは呼び出し側（コンポーネント）の責務で、ここは値を返すだけ。
    /// </summary>
    internal static class DarkEndAutoAdjust
    {
        /// <summary>V=0（暗い色）での暗部の明るさ</summary>
        private const float AtDark = 0.3f;

        /// <summary>V=0.5（中間の色）での暗部の明るさ（フラット化で明度感を揃える）</summary>
        private const float AtMid = 0.9f;

        /// <summary>V=1 かつ無彩色（白など）での暗部の明るさ</summary>
        private const float AtBrightAchromatic = 0.7f;

        /// <summary>V=1 かつビビッド（純赤など）での暗部の明るさ</summary>
        private const float AtBrightVivid = 0.4f;

        /// <summary>この彩度（Oklab C）以上でビビッド側の端点に完全に切り替わる</summary>
        private const float ChromaBlendRef = 0.2f;

        /// <summary>
        /// 変更後の色（sRGB）から暗部の明るさ（0.3〜0.9 程度）を返す。
        /// </summary>
        internal static float Compute(Color targetSrgb)
        {
            Color.RGBToHSV(targetSrgb, out _, out _, out float v);

            if (v <= 0.5f)
            {
                // 暗い側（0.3 + 1.2V と同値）
                return Mathf.Lerp(AtDark, AtMid, v * 2f);
            }

            float targetC = OklabConverter.SRGBToOklch(targetSrgb).y;
            float vividness = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(targetC / ChromaBlendRef));
            float brightEnd = Mathf.Lerp(AtBrightAchromatic, AtBrightVivid, vividness);
            return Mathf.Lerp(AtMid, brightEnd, v * 2f - 1f);
        }
    }
}
