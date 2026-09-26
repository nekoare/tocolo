// 流用元: com.nekoare.chimera-hair-master/Editor/Shader/OklabConverter.hlsl
// Oklab/OKLCh / Linear RGB / sRGB 変換ユーティリティ (HLSL)
// Reference: Björn Ottosson "A perceptual color space for image processing"
// https://bottosson.github.io/posts/oklab/
//
// === 色空間 ===
// ここの関数は入出力の色空間を決めない。どの色空間の値を渡すかは呼び出し側の compute の責務。
// sRGB 値を扱う場合は次の順で明示的に変換すること:
//   sRGB → SRGBToLinear → LinearRGBToOklab → ... → OklabToLinearRGB → LinearToSRGB → sRGB
// CPU 側 (Editor/Colors/OklabConverter.cs) と入出力の色空間を合わせること。

#ifndef CLICKRECOLOR_OKLAB_INCLUDED
#define CLICKRECOLOR_OKLAB_INCLUDED

// sRGB <-> Linear
float SRGBToLinearChannel(float c)
{
    // HLSL の三項演算子は両辺を評価するため、c < 0 でも pow が呼ばれる。
    // pow に負値を渡すと警告 + 未定義動作になるので max で 0 にクランプ
    return (c <= 0.04045) ? (c / 12.92) : pow(max((c + 0.055) / 1.055, 0.0), 2.4);
}

float LinearToSRGBChannel(float c)
{
    return (c <= 0.0031308) ? (c * 12.92) : (1.055 * pow(max(c, 0.0), 1.0 / 2.4) - 0.055);
}

float3 SRGBToLinear(float3 srgb)
{
    return float3(SRGBToLinearChannel(srgb.r), SRGBToLinearChannel(srgb.g), SRGBToLinearChannel(srgb.b));
}

float3 LinearToSRGB(float3 lin)
{
    return float3(LinearToSRGBChannel(lin.r), LinearToSRGBChannel(lin.g), LinearToSRGBChannel(lin.b));
}

// 立方根（負値対応）
float SignedCbrt(float x)
{
    return sign(x) * pow(abs(x), 1.0 / 3.0);
}

// Linear RGB -> Oklab
float3 LinearRGBToOklab(float3 lrgb)
{
    float l = 0.4122214708 * lrgb.r + 0.5363325363 * lrgb.g + 0.0514459929 * lrgb.b;
    float m = 0.2119034982 * lrgb.r + 0.6806995451 * lrgb.g + 0.1073969566 * lrgb.b;
    float s = 0.0883024619 * lrgb.r + 0.2817188376 * lrgb.g + 0.6299787005 * lrgb.b;

    float l_ = SignedCbrt(l);
    float m_ = SignedCbrt(m);
    float s_ = SignedCbrt(s);

    return float3(
        0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
        1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
        0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_
    );
}

// Oklab -> Linear RGB
float3 OklabToLinearRGB(float3 lab)
{
    float l_ = lab.x + 0.3963377774 * lab.y + 0.2158037573 * lab.z;
    float m_ = lab.x - 0.1055613458 * lab.y - 0.0638541728 * lab.z;
    float s_ = lab.x - 0.0894841775 * lab.y - 1.2914855480 * lab.z;

    float l = l_ * l_ * l_;
    float m = m_ * m_ * m_;
    float s = s_ * s_ * s_;

    return float3(
        4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
        -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
        -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s
    );
}

// Oklab <-> OKLCh （h はラジアン）
float3 OklabToOklch(float3 lab)
{
    return float3(lab.x, sqrt(lab.y * lab.y + lab.z * lab.z), atan2(lab.z, lab.y));
}

float3 OklchToOklab(float3 lch)
{
    return float3(lch.x, lch.y * cos(lch.z), lch.y * sin(lch.z));
}

// 色相環ラップ (-π, π]
float WrapHueRadians(float h)
{
    static const float TwoPi = 6.28318530717958647692;
    h = fmod(h + 3.14159265358979323846, TwoPi);
    if (h < 0.0) h += TwoPi;
    return h - 3.14159265358979323846;
}

// L 値の上下端 [0,1] へのソフトクリップ
float SoftClip01(float x, float softZone)
{
    if (x > 1.0 - softZone)
    {
        float shifted = x - (1.0 - softZone);
        float compressed = softZone * shifted / (shifted + softZone);
        return (1.0 - softZone) + compressed;
    }
    if (x < softZone)
    {
        float shifted = softZone - x;
        float compressed = softZone * shifted / (shifted + softZone);
        return softZone - compressed;
    }
    return x;
}

// float 誤差レベルのはみ出しを許容する量
// 1e-3 だと暗部（L≈0.05〜0.12）でガマット内の領域が飛び飛びになり、
// 二分探索が前提とする単調性が崩れて帯が残る。L=0.08 のグレーは
// 線形値が 5e-4 しかないので、1e-3 は色そのものより許容量が大きい状態だった。
// ※ ガマット内判定と二分探索の述語で共用する。別々の値にすると
//    探索の目標と判定がずれ、新しい段差が出る
static const float CR_GAMUT_EPS = 1e-6;

// 彩度の二分探索の反復回数。厳密解との 8bit 出力誤差は
// 8 回で 16 階調、10 回で 3 階調、12 回で 1 階調（量子化下限）
#define CR_GAMUT_ITERATIONS 12

// 線形 RGB が sRGB のガマット内か。eps は float 誤差の遊び
bool IsInSRGBGamut(float3 lin, float eps)
{
    return all(lin >= -eps) && all(lin <= 1.0 + eps);
}

// OKLCh -> sRGB。L と h を保ったまま C だけを縮めてガマット内に収める
//
// C を二分探索で連続的に求める。以前は 0.9 倍を最大 8 回繰り返していたが、
// 反復回数が整数なので出力彩度が 9 段階の飛び飛びの値しか取れず、
// 陰影のグラデーション上に帯状の段差が出ていた。
//
// ※ Editor/Colors/OklabConverter.cs の同名関数と定数・手順を必ず一致させること。
//    片方だけ変更すると GPU 利用時と CPU フォールバック時で出力が食い違う。
float3 OklchToSRGBGamutMapped(float3 lch)
{
    // h は探索中ずっと固定なので、cos/sin はループの外で 1 回だけ求める
    float cosH, sinH;
    sincos(lch.z, sinH, cosH);

    float3 lin = OklabToLinearRGB(float3(lch.x, lch.y * cosH, lch.y * sinH));
    if (!IsInSRGBGamut(lin, CR_GAMUT_EPS))
    {
        // ガマット内に収まる最大の C を二分探索する。
        // C = 0 のとき線形 RGB は (L^3, L^3, L^3) になる（Oklab->線形 RGB 行列の
        // 各行の和が 1 のため）。上流の SoftClip01 が L を (0,1) に収めるので、
        // 下端は必ずガマット内。これが探索が成立する前提。
        float lo = 0.0;
        // C が負だと探索が色相の反対側に収束するので、上端は 0 以上に丸める
        float hi = max(0.0, lch.y);

        [unroll]
        for (int i = 0; i < CR_GAMUT_ITERATIONS; i++)
        {
            float mid = (lo + hi) * 0.5;
            float3 probe = OklabToLinearRGB(float3(lch.x, mid * cosH, mid * sinH));
            if (IsInSRGBGamut(probe, CR_GAMUT_EPS)) lo = mid;
            else hi = mid;
        }
        // 内側の端を採用する（彩度が上がる方向には絶対に振らない）
        lin = OklabToLinearRGB(float3(lch.x, lo * cosH, lo * sinH));
    }

    lin = saturate(lin);
    return LinearToSRGB(lin);
}

#endif // CLICKRECOLOR_OKLAB_INCLUDED
