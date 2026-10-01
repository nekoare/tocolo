// 流用元: com.nekoare.chimera-hair-master/Editor/Processing/ColorProcessor.cs（TransformHueShiftOklab）
using Nekoare.ClickRecolor.Editor.Pipeline;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Colors
{
    /// <summary>
    /// ColorShift.compute の 1 画素の式と 1:1 のパラメータ。値の意味は compute の同名 uniform を参照
    /// </summary>
    internal struct ShiftParams
    {
        /// <summary>目標色の OKLCh（L, C, h ラジアン, 未使用）</summary>
        public Vector4 targetOklch;
        public float darkEndRatio;
        public float lToTarget;
        public float chromaToTarget;
        public float hueRetain;
        public float strength;
        public float lP05;
        public float lP95;
        public float hDominant;
        /// <summary>陰影の強調（L のリマップ側の重みの下限）。compute の _ShadingStretch。色 1・色 2 で共通</summary>
        public float shadingStretch;
        /// <summary>「元のグラデーションを打ち消す」の帯の統計（null なら使わない）。GPU だけが使う（CPU 版は未対応・実験的）</summary>
        public BandStats bands;
        /// <summary>打ち消す強さ（全体の統計と帯の統計を混ぜる割合）</summary>
        public float flattenStrength;

        // ── グラデーション（compute の UseGradient / WorldToBox / BoxSize / TargetOklab2）──
        /// <summary>true なら画素の位置（対象ルートのローカル）で目標色を混ぜる。位置マップが無ければ呼び出し側で false にする</summary>
        public bool useGradient;
        /// <summary>対象ルートのローカル座標 → 箱のローカル座標（箱の位置と回転の逆。大きさは boxSize で割る）</summary>
        public Matrix4x4 rootToBox;
        /// <summary>箱の大きさ（Y で混ぜる。gradientInsideOnly の内外判定も Y だけ）</summary>
        public Vector3 boxSize;
        /// <summary>true ならグラデーション方向（Y）で箱の外にある画素は色変えしない（元の色のまま）。横方向は切らない。compute の GradientInsideOnly</summary>
        public bool gradientInsideOnly;
        /// <summary>色 2（箱の上端 t=1 の目標色）の Oklab</summary>
        public Vector3 targetOklab2;
        /// <summary>色 2（箱の上端 t=1）の暗部の明るさ。useGradient のとき t で darkEndRatio と混ぜる。compute の _DarkEndRatio2</summary>
        public float darkEndRatio2;
        /// <summary>色 2（箱の上端 t=1）の不透明度。useGradient のとき t で strength と混ぜる。compute の _Strength2</summary>
        public float strength2;

        /// <summary>編集の色設定と、選択内の統計（元テクスチャ基準）から作る</summary>
        internal static ShiftParams FromEdit(RecolorEdit edit, in Stats stats)
        {
            // targetColor は sRGB（ColorField の値）
            Vector3 lch = OklabConverter.SRGBToOklch(edit.targetColor);
            bool gradient = edit.gradientEnabled;
            return new ShiftParams
            {
                useGradient = gradient,
                rootToBox = gradient
                    ? Matrix4x4.TRS(edit.gradientBoxPosition, Quaternion.Normalize(edit.gradientBoxRotation), Vector3.one).inverse
                    : Matrix4x4.identity,
                boxSize = edit.gradientBoxSize,
                gradientInsideOnly = gradient && edit.gradientInsideOnly,
                targetOklab2 = gradient
                    ? OklabConverter.LinearRGBToOklab(OklabConverter.SRGBToLinear(edit.gradientColor))
                    : Vector3.zero,
                targetOklch = new Vector4(lch.x, lch.y, lch.z, 0f),
                darkEndRatio = edit.darkEndRatio,
                darkEndRatio2 = gradient ? edit.gradientDarkEndRatio : edit.darkEndRatio,
                lToTarget = edit.lightnessToTarget,
                chromaToTarget = edit.chromaToTarget,
                hueRetain = edit.hueRetain,
                strength = edit.strength,
                strength2 = gradient ? edit.gradientStrength : edit.strength,
                lP05 = stats.lP05,
                lP95 = stats.lP95,
                hDominant = stats.hDominant,
                shadingStretch = edit.shadingStretch,
            };
        }
    }

    /// <summary>
    /// ColorShift.compute（CSShift）と同じ 1 画素の式の CPU 版。GPU/CPU 一致テストの基準に使う。
    /// ※ Editor/Shader/ColorShift.compute と式・定数・手順を必ず一致させること
    /// </summary>
    internal static class ColorShiftCpu
    {
        /// <summary>L のリマップを加算オフセットへ切り替える分布の幅</summary>
        internal const float RangeThreshold = 0.20f;

        /// <summary>箱の高さの下限（0 で割らない）。compute の MinBoxHeight と同じ</summary>
        private const float MinBoxHeight = 1e-5f;

        /// <summary>混ぜた目標色の彩度がこれ未満なら色相を 0 とみなす。compute の MinGradientChroma と同じ</summary>
        private const float MinGradientChroma = 1e-6f;

        /// <summary>
        /// 線形 RGB の画素 srcLinear を、マスク値 mask（0..1）× strength の割合で目標色へ寄せた線形 RGB を返す。α は変えない。
        /// 位置は渡さない（グラデーション ON なら t = 0 ＝「新しい色」側）
        /// </summary>
        internal static Color ShiftLinear(Color srcLinear, float mask, in ShiftParams p) =>
            ShiftLinear(srcLinear, mask, p, default);

        /// <summary>
        /// ShiftLinear の位置つき版。position は位置マップの画素（xyz = 対象ルートのローカル位置、w &gt; 0 なら描かれた画素）。
        /// グラデーション ON なら位置から t を求め、目標色・暗部の明るさ・強さを「新しい色」→「終了色」で混ぜてから変換する
        /// </summary>
        internal static Color ShiftLinear(Color srcLinear, float mask, in ShiftParams p, Vector4 position)
        {
            // 強さも t で混ぜるので、マスクの重み w より先に t を求める（OFF なら 0 ＝色 1 の値だけ。compute と同じ）
            // 「箱の中だけ」で箱の外（上下方向）なら色変えしない（元の色のまま。compute と同じ）
            if (p.useGradient && GradientOutsideBox(p, position)) return srcLinear;
            float t = p.useGradient ? GradientT(p, position) : 0f;
            float strength = p.useGradient ? LerpUnclamped(p.strength, p.strength2, t) : p.strength;
            float darkEndRatio = p.useGradient ? LerpUnclamped(p.darkEndRatio, p.darkEndRatio2, t) : p.darkEndRatio;

            float w = Mathf.Clamp01(mask * strength);
            // マスク外は元の値をそのまま返す（式の途中の誤差や NaN を持ち込まない。compute と同じ）
            if (w <= 0f) return srcLinear;

            Vector3 targetLch = new Vector3(p.targetOklch.x, p.targetOklch.y, p.targetOklch.z);
            if (p.useGradient)
            {
                // 目標色は Oklab で混ぜる（色相の回り方で遠回りしない）。混ぜた後に Oklch へ戻して従来の式へ
                Vector3 lab1 = OklabConverter.OklchToOklab(targetLch);
                targetLch = OklabConverter.OklabToOklch(lab1 + (p.targetOklab2 - lab1) * t);
                // 彩度 0 の atan2(0, 0) は GPU で NaN になりうるので色相を 0 に決める（compute と同じ）
                if (targetLch.y < MinGradientChroma) targetLch.z = 0f;
            }

            Vector3 shifted = ShiftToTargetLinear(new Vector3(srcLinear.r, srcLinear.g, srcLinear.b), targetLch, darkEndRatio, p);
            return new Color(
                srcLinear.r + (shifted.x - srcLinear.r) * w,
                srcLinear.g + (shifted.y - srcLinear.g) * w,
                srcLinear.b + (shifted.z - srcLinear.b) * w,
                srcLinear.a);
        }

        /// <summary>「箱の中だけ」でグラデーション方向（y）の箱の外なら true（その画素は色変えしない）。横方向は切らない。描かれていない画素は箱の中扱い</summary>
        internal static bool GradientOutsideBox(in ShiftParams p, Vector4 position)
        {
            if (!p.gradientInsideOnly || position.w <= 0f) return false;
            Vector3 local = p.rootToBox.MultiplyPoint3x4(new Vector3(position.x, position.y, position.z));
            return Mathf.Abs(local.y) > Mathf.Abs(p.boxSize.y) * 0.5f;
        }

        internal static float GradientT(in ShiftParams p, Vector4 position)
        {
            if (position.w <= 0f) return 0f;
            Vector3 local = p.rootToBox.MultiplyPoint3x4(new Vector3(position.x, position.y, position.z));
            // 高さは符号付き（箱の上下反転でグラデーションも反転。compute と同じ）
            float height = p.boxSize.y;
            if (Mathf.Abs(height) < MinBoxHeight) height = height < 0f ? -MinBoxHeight : MinBoxHeight;
            return Mathf.Clamp01(local.y / height + 0.5f);
        }

        /// <summary>線形 RGB を目標色 targetLch（OKLCh）へ寄せた線形 RGB（マスクを掛ける前の値）。darkEndRatio は暗部の明るさ</summary>
        private static Vector3 ShiftToTargetLinear(Vector3 lin, Vector3 targetLch, float darkEndRatio, in ShiftParams p)
        {
            Vector3 lch = OklabConverter.OklabToOklch(OklabConverter.LinearRGBToOklab(lin));

            float targetL = targetLch.x;
            float targetC = targetLch.y;
            float targetH = targetLch.z;

            // L: 選択内の [p05, p95] を [L_t × darkEndRatio, L_t] へ線形リマップ。
            //    分布が狭い（range < 0.20）ときは傾き 1 の加算オフセットへ滑らかに切り替える（微小な陰影を増幅しない）。
            //    陰影の強調（shadingStretch）はリマップ側の重みの下限（1 なら幅に関係なく目標範囲いっぱいへ広げる）
            float range = p.lP95 - p.lP05;
            float mid = (p.lP05 + p.lP95) * 0.5f;
            // range ≈ 0 ならリマップ側の重みもほぼ 0 なので、割り算だけ避けて中央に置く
            float n = range > 1e-5f ? (lch.x - p.lP05) / range : 0.5f;
            float remap = LerpUnclamped(targetL * darkEndRatio, targetL, n);
            float offset = lch.x - mid + targetL;
            float mapped = LerpUnclamped(offset, remap, Mathf.Max(Mathf.Clamp01(range / RangeThreshold), p.shadingStretch));
            float newL = OklabConverter.SoftClip01(LerpUnclamped(lch.x, mapped, p.lToTarget), 0.05f);

            // C: 目標の彩度へ寄せる（chromaToTarget = 0 なら元のまま）
            float newC = Mathf.Max(0f, LerpUnclamped(lch.y, targetC, p.chromaToTarget));

            // h: 目標色相に統一。hueRetain > 0 なら代表色相からの差を一部残す
            float newH = OklabConverter.WrapHueRadians(
                targetH + OklabConverter.WrapHueRadians(lch.z - p.hDominant) * p.hueRetain);

            // ガマット外は L と h を保って C だけ縮める。戻り値は sRGB なので線形に戻す
            Color srgb = OklabConverter.OklchToSRGBGamutMapped(new Vector3(newL, newC, newH));
            return OklabConverter.SRGBToLinear(srgb);
        }

        /// <summary>HLSL の lerp と同じく t を切り詰めない（n は選択外の画素で 0..1 をはみ出す）</summary>
        private static float LerpUnclamped(float a, float b, float t) => a + (b - a) * t;
    }
}
