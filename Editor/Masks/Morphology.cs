// 流用元: com.nekoare.mask-creation-tool/Editor/Processing/IslandSelector.cs（ApplyMorphologicalOpen / ApplyMorphologicalClose）
using Nekoare.ClickRecolor.Editor.Colors;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Masks
{
    /// <summary>
    /// Morphology.compute の C# ラッパー。どの操作も rt をその場で書き換える（中間は一時 RT）。
    /// rt は MaskTextures で作った R8・enableRandomWrite の RT を渡すこと
    /// </summary>
    internal static class Morphology
    {
        private const int ThreadGroupSize = 16;

        private static ComputeShader s_shader;
        private static int s_kernelDilate;
        private static int s_kernelErode;
        private static int s_kernelDilateMasked;
        private static int s_kernelBlur3;
        private static int s_kernelMul;
        private static int s_kernelMax;

        private static readonly int InId = Shader.PropertyToID("In");
        private static readonly int OtherId = Shader.PropertyToID("Other");
        private static readonly int AllowedId = Shader.PropertyToID("Allowed");
        private static readonly int OutId = Shader.PropertyToID("Out");
        private static readonly int WidthId = Shader.PropertyToID("Width");
        private static readonly int HeightId = Shader.PropertyToID("Height");
        private static readonly int RadiusId = Shader.PropertyToID("Radius");
        private static readonly int AllowedWidthId = Shader.PropertyToID("AllowedWidth");
        private static readonly int AllowedHeightId = Shader.PropertyToID("AllowedHeight");
        private static readonly int AllowedInvertId = Shader.PropertyToID("AllowedInvert");

        private static ComputeShader Compute
        {
            get
            {
                if (s_shader == null)
                {
                    s_shader = ShaderAssets.Load(ShaderAssets.MorphologyGuid);
                    if (s_shader != null)
                    {
                        s_kernelDilate = s_shader.FindKernel("CSDilate");
                        s_kernelErode = s_shader.FindKernel("CSErode");
                        s_kernelDilateMasked = s_shader.FindKernel("CSDilateMasked");
                        s_kernelBlur3 = s_shader.FindKernel("CSBlur3");
                        s_kernelMul = s_shader.FindKernel("CSMul");
                        s_kernelMax = s_shader.FindKernel("CSMax");
                    }
                }
                return s_shader;
            }
        }

        internal static bool IsAvailable =>
            SystemInfo.supportsComputeShaders
            && SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.R8)
            && Compute != null;

        /// <summary>膨張（正方形窓の最大値）。r ≤ 0 なら何もしない</summary>
        internal static void Dilate(RenderTexture rt, int r)
        {
            if (r <= 0 || Compute == null || rt == null) return;
            var tmp = MaskTextures.GetTemporary(rt.width, rt.height);
            try
            {
                Run(s_kernelDilate, rt, tmp, r);
                Graphics.CopyTexture(tmp, rt);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(tmp);
            }
        }

        /// <summary>クローズ（膨張→収縮）: ラスタライズの 1px 穴など内側の小さな穴を埋める。r ≤ 0 なら何もしない</summary>
        internal static void Close(RenderTexture rt, int r)
        {
            if (r <= 0 || Compute == null || rt == null) return;
            var tmp = MaskTextures.GetTemporary(rt.width, rt.height);
            try
            {
                Run(s_kernelDilate, rt, tmp, r);
                Run(s_kernelErode, tmp, rt, r);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(tmp);
            }
        }

        /// <summary>オープン（収縮→膨張）: r px 以下の細い構造やゴマ塩を消し、太い本体は残す。r ≤ 0 なら何もしない</summary>
        internal static void Open(RenderTexture rt, int r)
        {
            if (r <= 0 || Compute == null || rt == null) return;
            var tmp = MaskTextures.GetTemporary(rt.width, rt.height);
            try
            {
                Run(s_kernelErode, rt, tmp, r);
                Run(s_kernelDilate, tmp, rt, r);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(tmp);
            }
        }

        /// <summary>
        /// allowedRt が 0.5 以上の画素へだけ r px 膨張する（チャートを意識したパディング）。
        /// 半径 1 の許可付き膨張を r 回繰り返すので、許可された画素だけをたどって届く所までしか広がらない
        /// （細い隣チャートを飛び越えて向こう側の空白へ滲まない）。
        /// invertAllowed が true なら allowedRt を反転して使う（被覆マスクを渡して「どのチャートにも属さない画素」だけに広げる）。
        /// allowedRt は rt と別サイズでもよい（正規化座標で対応させる）。r ≤ 0 なら何もしない
        /// </summary>
        internal static void DilateInto(RenderTexture rt, RenderTexture allowedRt, int r, bool invertAllowed = false)
        {
            if (r <= 0 || Compute == null || rt == null || allowedRt == null) return;
            var shader = Compute;
            var tmp = MaskTextures.GetTemporary(rt.width, rt.height);
            try
            {
                shader.SetTexture(s_kernelDilateMasked, AllowedId, allowedRt);
                shader.SetInt(AllowedWidthId, allowedRt.width);
                shader.SetInt(AllowedHeightId, allowedRt.height);
                shader.SetInt(AllowedInvertId, invertAllowed ? 1 : 0);
                // rt と tmp を交互に読み書きする（ping-pong）
                RenderTexture src = rt;
                RenderTexture dst = tmp;
                for (int i = 0; i < r; i++)
                {
                    Run(s_kernelDilateMasked, src, dst, 1);
                    (src, dst) = (dst, src);
                }
                if (src != rt) Graphics.CopyTexture(src, rt);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(tmp);
            }
        }

        /// <summary>3×3 の平均で 1px ぼかす（縁をソフトにする）。どの画素へもにじむ</summary>
        internal static void Blur1(RenderTexture rt)
        {
            // 白（全画素許可）を渡して制約なしにする
            RunBlur(rt, Texture2D.whiteTexture, false);
        }

        /// <summary>
        /// 3×3 の平均で 1px ぼかすが、書くのはマスク内（rt > 0）か allowedRt が 0.5 以上の画素だけで、それ以外は 0 にする。
        /// invertAllowed が true なら allowedRt を反転して使う（被覆マスクを渡して「どのチャートにも属さない画素」にだけにじませる）。
        /// allowedRt は rt と別サイズでもよい（正規化座標で対応させる）。null なら制約なしの Blur1 と同じ
        /// </summary>
        internal static void Blur1(RenderTexture rt, RenderTexture allowedRt, bool invertAllowed)
        {
            if (allowedRt == null)
            {
                Blur1(rt);
                return;
            }
            RunBlur(rt, allowedRt, invertAllowed);
        }

        private static void RunBlur(RenderTexture rt, Texture allowed, bool invertAllowed)
        {
            if (Compute == null || rt == null || allowed == null) return;
            var shader = Compute;
            var tmp = MaskTextures.GetTemporary(rt.width, rt.height);
            try
            {
                shader.SetTexture(s_kernelBlur3, AllowedId, allowed);
                shader.SetInt(AllowedWidthId, allowed.width);
                shader.SetInt(AllowedHeightId, allowed.height);
                shader.SetInt(AllowedInvertId, invertAllowed ? 1 : 0);
                Run(s_kernelBlur3, rt, tmp, 0);
                Graphics.CopyTexture(tmp, rt);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(tmp);
            }
        }

        /// <summary>rt に other を掛ける（同じサイズであること）</summary>
        internal static void Multiply(RenderTexture rt, RenderTexture other)
        {
            if (Compute == null || rt == null || other == null) return;
            var shader = Compute;
            var tmp = MaskTextures.GetTemporary(rt.width, rt.height);
            try
            {
                shader.SetTexture(s_kernelMul, OtherId, other);
                Run(s_kernelMul, rt, tmp, 0);
                Graphics.CopyTexture(tmp, rt);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(tmp);
            }
        }

        /// <summary>rt を rt と other の画素ごとの最大値にする（同じサイズであること）</summary>
        internal static void Max(RenderTexture rt, RenderTexture other)
        {
            if (Compute == null || rt == null || other == null) return;
            var shader = Compute;
            var tmp = MaskTextures.GetTemporary(rt.width, rt.height);
            try
            {
                shader.SetTexture(s_kernelMax, OtherId, other);
                Run(s_kernelMax, rt, tmp, 0);
                Graphics.CopyTexture(tmp, rt);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(tmp);
            }
        }

        private static void Run(int kernel, RenderTexture src, RenderTexture dst, int radius)
        {
            var shader = Compute;
            shader.SetInt(WidthId, dst.width);
            shader.SetInt(HeightId, dst.height);
            shader.SetInt(RadiusId, radius);
            shader.SetTexture(kernel, InId, src);
            shader.SetTexture(kernel, OutId, dst);
            shader.Dispatch(kernel, Groups(dst.width), Groups(dst.height), 1);
        }

        private static int Groups(int size) => (size + ThreadGroupSize - 1) / ThreadGroupSize;
    }
}
