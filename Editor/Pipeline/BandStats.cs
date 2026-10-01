using System;
using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nekoare.ClickRecolor.Editor.Pipeline
{
    /// <summary>
    /// 「元のグラデーションを打ち消す」（実験的）の帯ごとの明るさの統計。選択範囲の画素を、3D の位置を axis に射影した値で
    /// Count 本の帯に分け、帯ごとに L の 5／95 パーセンタイルを取る。ColorShift.compute が画素の位置で帯の間を補間して使う
    /// </summary>
    internal sealed class BandStats
    {
        /// <summary>帯の数</summary>
        internal const int Count = 12;
        /// <summary>統計を取るときに読み戻す大きさ（長辺）の上限</summary>
        internal const int ReadbackSize = 512;
        /// <summary>帯の画素がこれ未満なら、隣の帯の値で埋める</summary>
        internal const int MinBandCount = 32;

        /// <summary>対象ルートのローカルでの帯の向き（正規化済み）</summary>
        public Vector3 axis;
        /// <summary>帯の範囲（axis への射影の最小・最大）</summary>
        public float min;
        public float max;
        /// <summary>帯ごとの (p05, p95, 0, 0)。長さ Count</summary>
        public Vector4[] values;

        /// <summary>
        /// 線形 RGB の作業 RT source・選択マスク mask・位置マップ positionMap（どれも同じ UV 空間。大きさは違ってよい）から帯の統計を取る。
        /// 有効な帯が 2 本未満（選択が小さい・位置が描けない）なら null
        /// </summary>
        internal static BandStats Compute(RenderTexture source, RenderTexture mask, RenderTexture positionMap, Vector3 axis)
        {
            if (source == null || mask == null || positionMap == null) return null;
            SelectionStats.ReadbackDownscaled(source, mask, ReadbackSize, out var pixels, out var maskValues);
            float scale = Mathf.Min(1f, ReadbackSize / (float)Mathf.Max(source.width, source.height));
            int w = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            int h = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            var positions = ReadPositions(positionMap, w, h);
            return Compute(pixels, maskValues, positions, axis);
        }

        /// <summary>CPU の配列版（テスト用にも使う）。3 つの配列は同じ並び・同じ長さ</summary>
        internal static BandStats Compute(Color[] linearPixels, float[] mask, Color[] positions, Vector3 axis)
        {
            if (linearPixels == null || mask == null || positions == null) return null;
            if (axis.sqrMagnitude < 1e-8f) return null;
            axis = axis.normalized;
            int n = Mathf.Min(linearPixels.Length, Mathf.Min(mask.Length, positions.Length));

            var ls = new List<float>(n);
            var ws = new List<float>(n);
            var ss = new List<float>(n);
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                float m = mask[i];
                var c = linearPixels[i];
                var p = positions[i];
                if (!(m > 0.5f) || c.a < 0.01f || p.a <= 0f) continue;
                float s = p.r * axis.x + p.g * axis.y + p.b * axis.z;
                ls.Add(OklabConverter.LinearRGBToOklab(new Vector3(c.r, c.g, c.b)).x);
                ws.Add(m);
                ss.Add(s);
                if (s < min) min = s;
                if (s > max) max = s;
            }
            if (ls.Count < MinBandCount * 2 || max - min < 1e-5f) return null;

            // 帯ごとに集める
            var bandL = new List<float>[Count];
            var bandW = new List<float>[Count];
            for (int b = 0; b < Count; b++) { bandL[b] = new List<float>(); bandW[b] = new List<float>(); }
            float span = max - min;
            for (int i = 0; i < ls.Count; i++)
            {
                int b = Mathf.Clamp((int)((ss[i] - min) / span * Count), 0, Count - 1);
                bandL[b].Add(ls[i]);
                bandW[b].Add(ws[i]);
            }

            var values = new Vector4[Count];
            var valid = new bool[Count];
            int validCount = 0;
            for (int b = 0; b < Count; b++)
            {
                if (bandL[b].Count < MinBandCount) continue;
                var l = bandL[b].ToArray();
                var wt = bandW[b].ToArray();
                Array.Sort(l, wt);
                double total = 0;
                foreach (var x in wt) total += x;
                values[b] = new Vector4(Percentile(l, wt, total, 0.05), Percentile(l, wt, total, 0.95), 0f, 0f);
                valid[b] = true;
                validCount++;
            }
            if (validCount < 2) return null;

            // 画素の少ない帯は、近い有効な帯の値で埋める（毛先など）
            for (int b = 0; b < Count; b++)
            {
                if (valid[b]) continue;
                for (int d = 1; d < Count; d++)
                {
                    if (b - d >= 0 && valid[b - d]) { values[b] = values[b - d]; break; }
                    if (b + d < Count && valid[b + d]) { values[b] = values[b + d]; break; }
                }
            }
            return new BandStats { axis = axis, min = min, max = max, values = values };
        }

        private static float Percentile(float[] sorted, float[] weights, double total, double q)
        {
            double threshold = q * total, cumulative = 0;
            for (int i = 0; i < sorted.Length; i++)
            {
                cumulative += weights[i];
                if (cumulative >= threshold) return sorted[i];
            }
            return sorted[sorted.Length - 1];
        }

        /// <summary>位置マップを w×h に縮めて読み戻す（位置マップは Point なので隣の島と混ざらない）。並びは行優先・y = 0 が下</summary>
        private static Color[] ReadPositions(RenderTexture positionMap, int w, int h)
        {
            var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGBFloat, 0) { sRGB = false, useMipMap = false, msaaSamples = 1 };
            var rt = RenderTexture.GetTemporary(desc);
            var tex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(positionMap, rt);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                tex.Apply(false);
                return tex.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(tex);
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>編集の帯の向き（対象ルートのローカル）: グラデーション ON なら箱の Y 軸、OFF ならアバターの上下</summary>
        internal static Vector3 AxisOf(RecolorEdit edit) =>
            edit != null && edit.gradientEnabled
                ? Quaternion.Normalize(edit.gradientBoxRotation) * Vector3.up
                : Vector3.up;
    }
}
