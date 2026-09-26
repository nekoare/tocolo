using System;
using UnityEngine;

namespace Nekoare.ClickRecolor
{
    /// <summary>
    /// 編集に後から足した種（Ctrl＋クリック）。主種（RecolorEdit.seedRenderer 等）と同じ意味の値を持ち、
    /// 種ごとに作った選択マスクを合成（最大値）して 1 つの編集の範囲にする
    /// </summary>
    [Serializable]
    public sealed class RecolorSeed
    {
        public Renderer renderer;
        public int submesh;
        /// <summary>クリックした三角形（サブメッシュ内ローカル番号）</summary>
        public int triangle;
        /// <summary>クリック位置（マテリアルの Tiling/Offset 適用後、0..1）</summary>
        public Vector2 uv;
    }
}
