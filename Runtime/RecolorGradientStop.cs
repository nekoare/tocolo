using System;
using UnityEngine;

namespace Nekoare.ClickRecolor
{
    /// <summary>
    /// グラデーションの途中の色（色 1＝箱の下端と色 2＝箱の上端のあいだ）。色だけを持ち、陰影の暗さ・ガンマ・強さは両端から決まる
    /// </summary>
    [Serializable]
    public struct RecolorGradientStop
    {
        /// <summary>途中の色を置ける数の上限（両端と合わせて 8 色。compute の配列の長さと同じ）</summary>
        public const int MaxCount = 6;

        /// <summary>箱の下端からの位置（0 = 色 1、1 = 色 2）</summary>
        [Range(0f, 1f)] public float position;
        /// <summary>この位置の目標色（sRGB）</summary>
        public Color color;
    }
}
