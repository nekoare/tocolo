using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Colors
{
    /// <summary>
    /// 髪 1 枚ぶんの色の統計（「お手本に合わせる」の④明るさの曲線合わせ）。
    /// 明るさの分位点と、明るさの順に等人数で分けた帯ごとの平均色（Oklab）、帯の平均色からのずれ（色ムラ）の大きさ
    /// </summary>
    internal sealed class MatchProfile
    {
        /// <summary>明るさの分位点を取る位置</summary>
        internal static readonly float[] QuantilePoints = { 0.02f, 0.10f, 0.25f, 0.50f, 0.75f, 0.90f, 0.98f };
        internal const int QuantileCount = 7;
        /// <summary>中央（50%）の分位点の番号</summary>
        internal const int MedianIndex = 3;
        internal const int BandCount = 16;
        /// <summary>これ未満の画素数では統計を作らない（帯 1 つに 1 画素も入らない）</summary>
        internal const int MinCount = BandCount;

        private const string FormatVersion = "1";

        /// <summary>L の分位点（QuantilePoints の順・昇順）</summary>
        public float[] quantiles = new float[QuantileCount];
        /// <summary>帯ごとの平均 (L, a, b)。L は狭義の昇順</summary>
        public Vector3[] bands = new Vector3[BandCount];
        /// <summary>帯の平均色（明るさで補間）からの (a, b) のずれの二乗平均の平方根</summary>
        public float residualRms;
        public int count;

        public bool IsValid => count >= MinCount && quantiles != null && quantiles.Length == QuantileCount
            && bands != null && bands.Length == BandCount;

        /// <summary>
        /// Oklab の画素の並びから統計を作る。画素が MinCount 未満なら無効（IsValid が false）の統計。
        /// 帯は L 昇順に並べて等人数で分ける: L の値で区切ると、同じ明るさが多いテクスチャで空の帯ができて平均が取れない
        /// </summary>
        internal static MatchProfile FromOklab(IReadOnlyList<Vector3> labs)
        {
            var profile = new MatchProfile();
            int n = labs?.Count ?? 0;
            profile.count = n;
            if (n < MinCount) return profile;

            var keys = new float[n];
            var order = new int[n];
            for (int i = 0; i < n; i++)
            {
                keys[i] = labs[i].x;
                order[i] = i;
            }
            Array.Sort(keys, order);

            for (int q = 0; q < QuantileCount; q++)
            {
                double pos = QuantilePoints[q] * (n - 1);
                int lo = (int)Math.Floor(pos);
                int hi = Math.Min(lo + 1, n - 1);
                profile.quantiles[q] = (float)(keys[lo] + (keys[hi] - keys[lo]) * (pos - lo));
            }

            var raw = new Vector3[BandCount];
            for (int b = 0; b < BandCount; b++)
            {
                int start = (int)((long)b * n / BandCount);
                int end = (int)((long)(b + 1) * n / BandCount);
                double sl = 0, sa = 0, sb = 0;
                for (int i = start; i < end; i++)
                {
                    var lab = labs[order[i]];
                    sl += lab.x;
                    sa += lab.y;
                    sb += lab.z;
                }
                int c = Math.Max(1, end - start);
                raw[b] = new Vector3((float)(sl / c), (float)(sa / c), (float)(sb / c));
            }
            // 色だけ [1,2,1]/4 で 1 回なめらかにする（端は端の値を繰り返す）。L は補間の目盛りなのでそのまま
            for (int b = 0; b < BandCount; b++)
            {
                var prev = raw[Math.Max(b - 1, 0)];
                var next = raw[Math.Min(b + 1, BandCount - 1)];
                var cur = raw[b];
                // 同じ明るさの帯が並んでも補間の目盛りが狭義の昇順になるよう、ごくわずかにずらす
                profile.bands[b] = new Vector3(
                    cur.x + b * 1e-6f,
                    (prev.y + 2f * cur.y + next.y) * 0.25f,
                    (prev.z + 2f * cur.z + next.z) * 0.25f);
            }

            var bandVectors = new Vector4[BandCount];
            for (int b = 0; b < BandCount; b++) bandVectors[b] = profile.bands[b];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                var lab = labs[i];
                var curve = ColorMatchCpu.EvalBands(bandVectors, lab.x);
                float da = lab.y - curve.x, db = lab.z - curve.y;
                sum += da * da + db * db;
            }
            profile.residualRms = (float)Math.Sqrt(sum / n);
            return profile;
        }

        /// <summary>控え（RecolorEdit.matchProfileData）の文字列にする。数値は文化に依らない書式</summary>
        internal string Serialize()
        {
            var sb = new StringBuilder(FormatVersion);
            sb.Append(';').Append(count.ToString(CultureInfo.InvariantCulture));
            sb.Append(';').Append(residualRms.ToString("R", CultureInfo.InvariantCulture));
            foreach (var q in quantiles) sb.Append(';').Append(q.ToString("R", CultureInfo.InvariantCulture));
            foreach (var band in bands)
            {
                sb.Append(';').Append(band.x.ToString("R", CultureInfo.InvariantCulture));
                sb.Append(';').Append(band.y.ToString("R", CultureInfo.InvariantCulture));
                sb.Append(';').Append(band.z.ToString("R", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Serialize の文字列から戻す。形が合わなければ false</summary>
        internal static bool TryParse(string data, out MatchProfile profile)
        {
            profile = null;
            if (string.IsNullOrEmpty(data)) return false;
            var parts = data.Split(';');
            if (parts.Length != 3 + QuantileCount + BandCount * 3 || parts[0] != FormatVersion) return false;
            var result = new MatchProfile();
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out result.count)) return false;
            if (!TryFloat(parts[2], out result.residualRms)) return false;
            int k = 3;
            for (int q = 0; q < QuantileCount; q++)
            {
                if (!TryFloat(parts[k++], out result.quantiles[q])) return false;
            }
            for (int b = 0; b < BandCount; b++)
            {
                if (!TryFloat(parts[k++], out float l) || !TryFloat(parts[k++], out float a) || !TryFloat(parts[k++], out float bb)) return false;
                result.bands[b] = new Vector3(l, a, bb);
            }
            if (!result.IsValid) return false;
            profile = result;
            return true;
        }

        private static bool TryFloat(string s, out float value) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>ColorShift.compute の CSMatch と 1:1 のパラメータ。値の意味は compute の同名 uniform を参照</summary>
    internal struct MatchParams
    {
        /// <summary>傾きの上限（狭い明るさの幅を引き伸ばすと 8bit の段差が縞になる）</summary>
        internal const float MaxSlope = 4f;
        internal const float MinResidualScale = 0.25f;
        internal const float MaxResidualScale = 2f;

        /// <summary>明るさの曲線の点 (対象の L, お手本の L, 0, 0)。7 点・x は昇順</summary>
        public Vector4[] lCurve;
        /// <summary>対象の帯 (L, a, b, 0)。16 個</summary>
        public Vector4[] targetBands;
        /// <summary>お手本の帯 (L, a, b, 0)。16 個</summary>
        public Vector4[] referenceBands;
        /// <summary>色ムラの倍率</summary>
        public float k;
        public float lightnessShift;
        /// <summary>色相のずらし（ラジアン）。代表色まわりの色の散らばり（影とハイライトの色の差）をこの角度だけ回す</summary>
        public float hueShift;
        /// <summary>お手本の代表色の (a, b)（移す元）</summary>
        public Vector2 from;
        /// <summary>合わせた結果の代表色の (a, b)（移す先。色相・彩度のずらしを掛けたもの）</summary>
        public Vector2 to;
        public float shadow;
        /// <summary>ハイライトの伸び（支点より明るい側を (1 + 値) 倍）</summary>
        public float highlight;
        /// <summary>陰影の暗さ・ハイライトの伸びの支点（お手本の中央の明るさ）</summary>
        public float pivot;
        public float gamma;
        public float strength;

        /// <summary>
        /// 対象とお手本の統計と、編集の微調整から作る。どちらかの統計が無効なら false。
        /// 曲線は中央の点から外へ向かって、区間ごとの傾きを [0, MaxSlope] に切り詰めて作る
        /// （端から作ると、対象の明るさの幅が 0 のとき全体がお手本の最も暗い所へ寄る）
        /// </summary>
        internal static bool TryBuild(MatchProfile target, MatchProfile reference, RecolorEdit edit, out MatchParams p)
        {
            p = default;
            if (target == null || reference == null || !target.IsValid || !reference.IsValid || edit == null) return false;

            const int n = MatchProfile.QuantileCount;
            const int m = MatchProfile.MedianIndex;
            var xs = target.quantiles;
            var rq = reference.quantiles;
            var ys = new float[n];
            ys[m] = rq[m];
            for (int i = m + 1; i < n; i++)
            {
                float dx = Mathf.Max(xs[i] - xs[i - 1], 0f);
                ys[i] = ys[i - 1] + Mathf.Clamp(rq[i] - rq[i - 1], 0f, dx * MaxSlope);
            }
            for (int i = m - 1; i >= 0; i--)
            {
                float dx = Mathf.Max(xs[i + 1] - xs[i], 0f);
                ys[i] = ys[i + 1] - Mathf.Clamp(rq[i + 1] - rq[i], 0f, dx * MaxSlope);
            }

            p.lCurve = new Vector4[n];
            for (int i = 0; i < n; i++) p.lCurve[i] = new Vector4(xs[i], ys[i], 0f, 0f);
            p.targetBands = ToVectors(target.bands);
            p.referenceBands = ToVectors(reference.bands);
            p.k = target.residualRms > 1e-6f
                ? Mathf.Clamp(reference.residualRms / target.residualRms, MinResidualScale, MaxResidualScale)
                : 1f;
            p.pivot = ys[m];
            p.lightnessShift = edit.matchLightness;
            p.hueShift = edit.matchHue * Mathf.Deg2Rad;
            var representative = MatchRepresentative.Reference(reference);
            p.from = new Vector2(representative.y, representative.z);
            var result = MatchRepresentative.Result(reference, edit);
            p.to = new Vector2(result.y, result.z);
            p.shadow = Mathf.Clamp(edit.matchShadow, -1f, 1f);
            p.highlight = Mathf.Clamp(edit.matchHighlight, -1f, 1f);
            p.gamma = edit.gamma;
            p.strength = edit.strength;
            return true;
        }

        private static Vector4[] ToVectors(Vector3[] bands)
        {
            var result = new Vector4[bands.Length];
            for (int i = 0; i < bands.Length; i++) result[i] = new Vector4(bands[i].x, bands[i].y, bands[i].z, 0f);
            return result;
        }
    }

    /// <summary>
    /// ColorShift.compute（CSMatch）と同じ 1 画素の式の CPU 版。GPU/CPU 一致テストの基準に使う。
    /// ※ Editor/Shader/ColorShift.compute と式・定数・手順を必ず一致させること
    /// </summary>
    internal static class ColorMatchCpu
    {
        /// <summary>区間の幅がこれ未満なら傾き 0 とみなす（0 で割らない）。compute の MatchMinSpan と同じ</summary>
        internal const float MinSpan = 1e-5f;

        /// <summary>線形 RGB の画素を、マスク値 mask（0..1）× 不透明度の割合でお手本の色味へ寄せた線形 RGB。α は変えない</summary>
        internal static Color MatchLinear(Color srcLinear, float mask, in MatchParams p)
        {
            float w = Mathf.Clamp01(mask * p.strength);
            if (w <= 0f) return srcLinear;
            Vector3 shifted = ColorShiftCpu.ApplyGamma(MatchToReferenceLinear(new Vector3(srcLinear.r, srcLinear.g, srcLinear.b), p), p.gamma);
            return new Color(
                srcLinear.r + (shifted.x - srcLinear.r) * w,
                srcLinear.g + (shifted.y - srcLinear.g) * w,
                srcLinear.b + (shifted.z - srcLinear.b) * w,
                srcLinear.a);
        }

        /// <summary>マスクを掛ける前の値（線形 RGB）</summary>
        internal static Vector3 MatchToReferenceLinear(Vector3 lin, in MatchParams p)
        {
            Vector3 lab = OklabConverter.LinearRGBToOklab(lin);
            float l1 = EvalCurve(p.lCurve, lab.x);
            // 陰影の暗さ・ハイライトの伸び: 支点より暗い側・明るい側をそれぞれ広げる・縮める
            float l2 = p.pivot + (l1 - p.pivot) * (1f + (l1 < p.pivot ? p.shadow : p.highlight));
            float l3 = OklabConverter.SoftClip01(l2 + p.lightnessShift, 0.05f);

            // 色: お手本の曲線のその明るさの色 ＋ 対象の色ムラ × k。曲線は写した後の明るさ（ずらす前）で引く（明るさをずらしても色味は変えない）
            Vector2 own = EvalBands(p.targetBands, lab.x);
            Vector2 refColor = EvalBands(p.referenceBands, l1);
            float a = refColor.x + (lab.y - own.x) * p.k;
            float b = refColor.y + (lab.z - own.y) * p.k;
            // 色の集まりを、代表色が移す先に来るよう移し、代表色からの散らばりは色相のずらしの分だけ回す
            // （倍率で彩度を変えると、白・灰色のお手本に色を足せない）
            float da = a - p.from.x, db = b - p.from.y;
            float cos = Mathf.Cos(p.hueShift), sin = Mathf.Sin(p.hueShift);
            float ra = p.to.x + da * cos - db * sin;
            float rb = p.to.y + da * sin + db * cos;

            Vector3 lch = OklabConverter.OklabToOklch(new Vector3(l3, ra, rb));
            Color srgb = OklabConverter.OklchToSRGBGamutMapped(lch);
            return OklabConverter.SRGBToLinear(srgb);
        }

        /// <summary>明るさの曲線（7 点の折れ線。両端の外は端の区間の傾きで伸ばす）</summary>
        internal static float EvalCurve(Vector4[] curve, float l)
        {
            int last = curve.Length - 1;
            int i = 0;
            // l を含む区間 [i, i+1]（外なら端の区間）
            while (i < last - 1 && l > curve[i + 1].x) i++;
            float x0 = curve[i].x, y0 = curve[i].y, x1 = curve[i + 1].x, y1 = curve[i + 1].y;
            float span = x1 - x0;
            float slope = span > MinSpan ? (y1 - y0) / span : 0f;
            return y0 + (l - x0) * slope;
        }

        /// <summary>帯の平均色 (a, b) を明るさ l で線形補間する（両端の外は端の値）</summary>
        internal static Vector2 EvalBands(Vector4[] bands, float l)
        {
            int last = bands.Length - 1;
            if (l <= bands[0].x) return new Vector2(bands[0].y, bands[0].z);
            if (l >= bands[last].x) return new Vector2(bands[last].y, bands[last].z);
            int i = 0;
            while (i < last - 1 && l > bands[i + 1].x) i++;
            float span = bands[i + 1].x - bands[i].x;
            float t = span > MinSpan ? (l - bands[i].x) / span : 0f;
            return new Vector2(
                bands[i].y + (bands[i + 1].y - bands[i].y) * t,
                bands[i].z + (bands[i + 1].z - bands[i].z) * t);
        }
    }

    /// <summary>
    /// 「お手本に合わせる」の代表色（Oklab）。お手本の中央の明るさと、その明るさの帯の色。
    /// 見本・一覧の色はこの色で見せ、色相・彩度のずらしはこの色からの差で持つ
    /// </summary>
    internal static class MatchRepresentative
    {
        /// <summary>この彩度未満の代表色は色相を持たないとみなす（灰色の色相は丸め誤差で決まる向き）</summary>
        internal const float MinChroma = 1e-4f;

        /// <summary>お手本の代表色 (L, a, b)</summary>
        internal static Vector3 Reference(MatchProfile profile)
        {
            float l = profile.quantiles[MatchProfile.MedianIndex];
            var bands = new Vector4[profile.bands.Length];
            for (int i = 0; i < bands.Length; i++) bands[i] = profile.bands[i];
            Vector2 ab = ColorMatchCpu.EvalBands(bands, l);
            return new Vector3(l, ab.x, ab.y);
        }

        /// <summary>合わせた結果の代表色 (L, a, b)（お手本の代表色に edit のずらしを掛けたもの）</summary>
        internal static Vector3 Result(MatchProfile profile, RecolorEdit edit)
        {
            var reference = Reference(profile);
            float chroma = Mathf.Sqrt(reference.y * reference.y + reference.z * reference.z);
            float hue = chroma >= MinChroma ? Mathf.Atan2(reference.z, reference.y) : 0f;
            float newChroma = Mathf.Max(0f, chroma + edit.matchChromaShift);
            float newHue = hue + edit.matchHue * Mathf.Deg2Rad;
            return new Vector3(
                Mathf.Clamp01(reference.x + edit.matchLightness),
                newChroma * Mathf.Cos(newHue),
                newChroma * Mathf.Sin(newHue));
        }
    }

    /// <summary>
    /// 「お手本に合わせる」の明るさの山（合わせた結果の明るさの分布）の形と色、つまんだ位置からのずらしの逆算。
    /// 山はお手本の控え（分位点）から描くので、テクスチャを読み直さない（ドラッグ中も軽い）
    /// </summary>
    internal static class MatchMountain
    {
        /// <summary>左右の裾（2% と 98% の分位点）の外に描く裾野の幅（明るさ）</summary>
        internal const float TailWidth = 0.06f;
        private const float MinSpan = 1e-3f;

        /// <summary>左の裾（2%）・右の裾（98%）の分位点の番号</summary>
        internal const int LeftIndex = 0;
        internal const int RightIndex = MatchProfile.QuantileCount - 1;

        /// <summary>お手本の明るさ l を、合わせた結果の明るさへ写す（1 画素の式の明るさと同じ）</summary>
        internal static float Transform(MatchProfile profile, RecolorEdit edit, float l)
        {
            float pivot = profile.quantiles[MatchProfile.MedianIndex];
            float scale = 1f + Mathf.Clamp(l < pivot ? edit.matchShadow : edit.matchHighlight, -1f, 1f);
            return OklabConverter.SoftClip01(pivot + (l - pivot) * scale + edit.matchLightness, 0.05f);
        }

        /// <summary>結果の明るさ result を出すお手本の明るさ（Transform の逆。端の丸めは無視する）</summary>
        internal static float Inverse(MatchProfile profile, RecolorEdit edit, float result)
        {
            float pivot = profile.quantiles[MatchProfile.MedianIndex];
            float l2 = result - edit.matchLightness;
            float scale = 1f + Mathf.Clamp(l2 < pivot ? edit.matchShadow : edit.matchHighlight, -1f, 1f);
            return scale > MinSpan ? pivot + (l2 - pivot) / scale : pivot;
        }

        /// <summary>左の裾を結果の明るさ left に置く陰影の暗さ（−1〜1）。裾が支点と重なっていれば current のまま</summary>
        internal static float ShadowFor(MatchProfile profile, float lightness, float left, float current)
        {
            float pivot = profile.quantiles[MatchProfile.MedianIndex];
            float span = profile.quantiles[LeftIndex] - pivot;
            if (Mathf.Abs(span) < MinSpan) return current;
            return Mathf.Clamp((Mathf.Min(left - lightness, pivot) - pivot) / span - 1f, -1f, 1f);
        }

        /// <summary>右の裾を結果の明るさ right に置くハイライトの伸び（−1〜1）。裾が支点と重なっていれば current のまま</summary>
        internal static float HighlightFor(MatchProfile profile, float lightness, float right, float current)
        {
            float pivot = profile.quantiles[MatchProfile.MedianIndex];
            float span = profile.quantiles[RightIndex] - pivot;
            if (Mathf.Abs(span) < MinSpan) return current;
            return Mathf.Clamp((Mathf.Max(right - lightness, pivot) - pivot) / span - 1f, -1f, 1f);
        }

        /// <summary>
        /// 結果の明るさを 0〜1 で count 本に分けた柱の高さ（0〜1。いちばん高い柱が 1）と色（sRGB）。
        /// 高さは分位点をなめらかに結んだ累積の傾き（HeightAt）、色はその明るさのお手本の帯の色に、色相・彩度のずらしを掛けたもの
        /// </summary>
        internal static void Columns(MatchProfile profile, RecolorEdit edit, float[] heights, Color[] colors)
        {
            int count = heights.Length;
            var r = ResultQuantiles(profile, edit);
            var m = Tangents(r);

            var bands = new Vector4[profile.bands.Length];
            for (int i = 0; i < bands.Length; i++) bands[i] = profile.bands[i];
            var from = MatchRepresentative.Reference(profile);
            var to = MatchRepresentative.Result(profile, edit);
            float cos = Mathf.Cos(edit.matchHue * Mathf.Deg2Rad), sin = Mathf.Sin(edit.matchHue * Mathf.Deg2Rad);

            float max = 0f;
            for (int i = 0; i < count; i++)
            {
                float x = (i + 0.5f) / count;
                float h = HeightAt(r, m, x);
                heights[i] = h;
                max = Mathf.Max(max, h);

                Vector2 ab = ColorMatchCpu.EvalBands(bands, Inverse(profile, edit, x));
                float da = ab.x - from.y, db = ab.y - from.z;
                var lab = new Vector3(x, to.y + da * cos - db * sin, to.z + da * sin + db * cos);
                colors[i] = OklabConverter.OklchToSRGBGamutMapped(OklabConverter.OklabToOklch(lab));
            }
            if (max > 0f) for (int i = 0; i < count; i++) heights[i] /= max;
        }

        /// <summary>お手本の分位点を合わせた結果の明るさへ写したもの（昇順）</summary>
        internal static float[] ResultQuantiles(MatchProfile profile, RecolorEdit edit)
        {
            var r = new float[MatchProfile.QuantileCount];
            for (int j = 0; j < r.Length; j++) r[j] = Transform(profile, edit, profile.quantiles[j]);
            return r;
        }

        /// <summary>
        /// 分位点 (r[j], QuantilePoints[j]) を通る累積曲線の各点での傾き。
        /// 隣の区間の密度の重み付き調和平均にしているのは、単純な平均だと山の急な所で累積が逆戻りし、高さが負になるため
        /// </summary>
        internal static float[] Tangents(float[] r)
        {
            int n = r.Length;
            var m = new float[n];
            m[0] = EndTangent(Span(r, 0), Span(r, 1), Density(r, 0), Density(r, 1));
            m[n - 1] = EndTangent(Span(r, n - 2), Span(r, n - 3), Density(r, n - 2), Density(r, n - 3));
            for (int j = 1; j < n - 1; j++)
            {
                float h0 = Span(r, j - 1), h1 = Span(r, j);
                float d0 = Density(r, j - 1), d1 = Density(r, j);
                float w0 = 2f * h1 + h0, w1 = h1 + 2f * h0;
                m[j] = (w0 + w1) / (w0 / d0 + w1 / d1);
            }
            return m;
        }

        /// <summary>
        /// 結果の明るさ x での山の高さ（密度）。分位点のあいだは累積を 3 次で結んだ傾き、
        /// 裾の外は端の高さから TailWidth でなめらかに 0 へ下ろす
        /// </summary>
        internal static float HeightAt(float[] r, float[] m, float x)
        {
            int n = r.Length;
            if (x <= r[0]) return m[0] * Falloff((r[0] - x) / TailWidth);
            if (x >= r[n - 1]) return m[n - 1] * Falloff((x - r[n - 1]) / TailWidth);
            int j = 0;
            while (j < n - 2 && x > r[j + 1]) j++;
            float t = Mathf.Clamp01((x - r[j]) / Span(r, j));
            float height = 6f * t * (1f - t) * Density(r, j) + (3f * t * t - 4f * t + 1f) * m[j] + (3f * t * t - 2f * t) * m[j + 1];
            return Mathf.Max(0f, height);
        }

        /// <summary>
        /// 端の点の傾き。端の区間の密度そのままにすると、裾へ下りきる前に端の高さへ引き戻されて裾の内側にこぶが出るので、
        /// 隣の区間の傾きから外へ延ばし、逆向きなら 0・端の区間の 3 倍で止める（累積が逆戻りしない範囲）
        /// </summary>
        private static float EndTangent(float hEnd, float hNext, float dEnd, float dNext)
        {
            float m = ((2f * hEnd + hNext) * dEnd - hEnd * dNext) / (hEnd + hNext);
            if (m <= 0f) return 0f;
            return Mathf.Min(m, 3f * dEnd);
        }

        private static float Falloff(float t)
        {
            if (t >= 1f) return 0f;
            return 1f - t * t * (3f - 2f * t);
        }

        private static float Span(float[] r, int j) => Mathf.Max(r[j + 1] - r[j], MinSpan);

        /// <summary>分位点 j と j+1 のあいだの密度（割合 ÷ 結果の明るさの幅）</summary>
        private static float Density(float[] r, int j)
        {
            float p = MatchProfile.QuantilePoints[j + 1] - MatchProfile.QuantilePoints[j];
            return p / Span(r, j);
        }
    }
}
