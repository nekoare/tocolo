// 流用元: com.nekoare.chimera-hair-master/Editor/Processing/OklabConverter.cs
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Colors
{
    /// <summary>
    /// sRGB ↔ Linear RGB ↔ Oklab/OKLCh の変換ユーティリティ
    /// 参考: Björn Ottosson "A perceptual color space for image processing" (https://bottosson.github.io/posts/oklab/)
    /// </summary>
    internal static class OklabConverter
    {
        #region sRGB <-> Linear RGB

        /// <summary>
        /// sRGB (0-1) を Linear RGB (0-1) に変換
        /// </summary>
        public static float SRGBToLinear(float c)
        {
            if (c <= 0.04045f) return c / 12.92f;
            return Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        /// <summary>
        /// Linear RGB (0-1) を sRGB (0-1) に変換
        /// </summary>
        public static float LinearToSRGB(float c)
        {
            if (c <= 0.0031308f) return c * 12.92f;
            return 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
        }

        public static Vector3 SRGBToLinear(Color srgb)
        {
            return new Vector3(SRGBToLinear(srgb.r), SRGBToLinear(srgb.g), SRGBToLinear(srgb.b));
        }

        public static Color LinearToSRGB(Vector3 lin, float alpha = 1f)
        {
            return new Color(LinearToSRGB(lin.x), LinearToSRGB(lin.y), LinearToSRGB(lin.z), alpha);
        }

        #endregion

        #region Linear RGB <-> Oklab

        /// <summary>
        /// Linear RGB を Oklab (L, a, b) に変換
        /// </summary>
        public static Vector3 LinearRGBToOklab(Vector3 lrgb)
        {
            float l = 0.4122214708f * lrgb.x + 0.5363325363f * lrgb.y + 0.0514459929f * lrgb.z;
            float m = 0.2119034982f * lrgb.x + 0.6806995451f * lrgb.y + 0.1073969566f * lrgb.z;
            float s = 0.0883024619f * lrgb.x + 0.2817188376f * lrgb.y + 0.6299787005f * lrgb.z;

            float l_ = Cbrt(l);
            float m_ = Cbrt(m);
            float s_ = Cbrt(s);

            return new Vector3(
                0.2104542553f * l_ + 0.7936177850f * m_ - 0.0040720468f * s_,
                1.9779984951f * l_ - 2.4285922050f * m_ + 0.4505937099f * s_,
                0.0259040371f * l_ + 0.7827717662f * m_ - 0.8086757660f * s_
            );
        }

        /// <summary>
        /// Oklab (L, a, b) を Linear RGB に変換
        /// </summary>
        public static Vector3 OklabToLinearRGB(Vector3 lab)
        {
            float l_ = lab.x + 0.3963377774f * lab.y + 0.2158037573f * lab.z;
            float m_ = lab.x - 0.1055613458f * lab.y - 0.0638541728f * lab.z;
            float s_ = lab.x - 0.0894841775f * lab.y - 1.2914855480f * lab.z;

            float l = l_ * l_ * l_;
            float m = m_ * m_ * m_;
            float s = s_ * s_ * s_;

            return new Vector3(
                4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
                -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
                -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s
            );
        }

        #endregion

        #region Oklab <-> OKLCh

        /// <summary>
        /// Oklab (L, a, b) を OKLCh (L, C, h) に変換。h は ラジアン (-π, π]
        /// </summary>
        public static Vector3 OklabToOklch(Vector3 lab)
        {
            float c = Mathf.Sqrt(lab.y * lab.y + lab.z * lab.z);
            float h = Mathf.Atan2(lab.z, lab.y);
            return new Vector3(lab.x, c, h);
        }

        /// <summary>
        /// OKLCh (L, C, h) を Oklab (L, a, b) に変換。h は ラジアン
        /// </summary>
        public static Vector3 OklchToOklab(Vector3 lch)
        {
            float a = lch.y * Mathf.Cos(lch.z);
            float b = lch.y * Mathf.Sin(lch.z);
            return new Vector3(lch.x, a, b);
        }

        #endregion

        #region 高レベル便利関数

        /// <summary>
        /// sRGB Color → OKLCh (L, C, h_radians)
        /// </summary>
        public static Vector3 SRGBToOklch(Color srgb)
        {
            return OklabToOklch(LinearRGBToOklab(SRGBToLinear(srgb)));
        }

        /// <summary>
        /// OKLCh (L, C, h_radians) → sRGB Color (アルファは別途指定)
        /// 結果が sRGB の [0,1] 範囲外の場合は単純 saturate でクリップ
        ///
        /// ※ 色変換パスでは使わないこと。範囲外の色をチャンネルごとに切るため
        ///    色相までずれる。ガマット外を正しく扱うには OklchToSRGBGamutMapped を使う。
        /// </summary>
        public static Color OklchToSRGB(Vector3 lch, float alpha = 1f)
        {
            Vector3 lin = OklabToLinearRGB(OklchToOklab(lch));
            lin.x = Mathf.Clamp01(lin.x);
            lin.y = Mathf.Clamp01(lin.y);
            lin.z = Mathf.Clamp01(lin.z);
            return LinearToSRGB(lin, alpha);
        }

        /// <summary>
        /// OKLCh (L, C, h_radians) → sRGB Color。L と h を保ったまま C だけを縮めて
        /// sRGB のガマット内に収める。
        ///
        /// C を二分探索で連続的に求める。以前は 0.9 倍を最大 8 回繰り返していたが、
        /// 反復回数が整数なので出力彩度が 9 段階の飛び飛びの値しか取れず、
        /// 陰影のグラデーション上に帯状の段差が出ていた。
        ///
        /// ※ Editor/Shader/OklabConverter.hlsl の同名関数と定数・手順を必ず一致させること。
        ///    片方だけ変更すると GPU 利用時と CPU フォールバック時で出力が食い違う。
        /// ※ GamutEps はガマット内判定と探索の述語で共用する。別々の値にすると
        ///    探索の目標と判定がずれ、新しい段差が出る。
        /// </summary>
        public static Color OklchToSRGBGamutMapped(Vector3 lch, float alpha = 1f)
        {
            // h は探索中ずっと固定なので、cos/sin はループの外で 1 回だけ求める
            float cosH = Mathf.Cos(lch.z);
            float sinH = Mathf.Sin(lch.z);

            Vector3 lin = OklabToLinearRGB(new Vector3(lch.x, lch.y * cosH, lch.y * sinH));
            if (!IsInSRGBGamut(lin, GamutEps))
            {
                // ガマット内に収まる最大の C を二分探索する。
                // C = 0 のとき線形 RGB は (L³, L³, L³) になる（Oklab→線形 RGB 行列の
                // 各行の和が 1 のため）。上流の SoftClip01 が L を (0,1) に収めるので、
                // 下端は必ずガマット内。これが探索が成立する前提。
                float lo = 0f;
                // C が負だと探索が色相の反対側に収束するので、上端は 0 以上に丸める
                float hi = Mathf.Max(0f, lch.y);
                for (int i = 0; i < GamutIterations; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    Vector3 probe = OklabToLinearRGB(new Vector3(lch.x, mid * cosH, mid * sinH));
                    if (IsInSRGBGamut(probe, GamutEps)) lo = mid;
                    else hi = mid;
                }
                // 内側の端を採用する（彩度が上がる方向には絶対に振らない）
                lin = OklabToLinearRGB(new Vector3(lch.x, lo * cosH, lo * sinH));
            }

            lin.x = Mathf.Clamp01(lin.x);
            lin.y = Mathf.Clamp01(lin.y);
            lin.z = Mathf.Clamp01(lin.z);
            return LinearToSRGB(lin, alpha);
        }

        /// <summary>
        /// L 値の上下端 [0, 1] へのソフトクリップ
        /// 範囲 [softZone, 1 - softZone] では線形、外側では端へ滑らかに漸近
        /// </summary>
        public static float SoftClip01(float x, float softZone)
        {
            // 上端
            if (x > 1f - softZone)
            {
                float shifted = x - (1f - softZone);
                float compressed = softZone * shifted / (shifted + softZone);
                return (1f - softZone) + compressed;
            }
            // 下端
            if (x < softZone)
            {
                float shifted = softZone - x;
                float compressed = softZone * shifted / (shifted + softZone);
                return softZone - compressed;
            }
            return x;
        }

        /// <summary>
        /// 色相環での加算（ラジアン単位）。結果は (-π, π] の範囲に正規化
        /// </summary>
        public static float WrapHueRadians(float h)
        {
            const float TwoPi = Mathf.PI * 2f;
            h = (h + Mathf.PI) % TwoPi;
            if (h < 0f) h += TwoPi;
            return h - Mathf.PI;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// float 誤差レベルのはみ出しを許容する量。
        /// 1e-3 だと暗部（L≈0.05〜0.12）でガマット内の領域が飛び飛びになり、
        /// 二分探索が前提とする単調性が崩れて帯が残る。L=0.08 のグレーは
        /// 線形値が 5e-4 しかないので、1e-3 は色そのものより許容量が大きい状態だった。
        /// </summary>
        private const float GamutEps = 1e-6f;

        /// <summary>
        /// 彩度の二分探索の反復回数。厳密解との 8bit 出力誤差は
        /// 8 回で 16 階調、10 回で 3 階調、12 回で 1 階調（量子化下限）
        /// </summary>
        private const int GamutIterations = 12;

        /// <summary>
        /// 線形 RGB が sRGB のガマット内か。eps は float 誤差の遊び
        /// </summary>
        private static bool IsInSRGBGamut(Vector3 lin, float eps)
        {
            return lin.x >= -eps && lin.x <= 1f + eps &&
                   lin.y >= -eps && lin.y <= 1f + eps &&
                   lin.z >= -eps && lin.z <= 1f + eps;
        }

        private static float Cbrt(float x)
        {
            // C# には立方根がないため、符号付きの pow で実装
            if (x >= 0f) return Mathf.Pow(x, 1f / 3f);
            return -Mathf.Pow(-x, 1f / 3f);
        }

        #endregion
    }
}
