namespace Nekoare.ClickRecolor
{
    /// <summary>
    /// アップロード時・書き出し時に生成するテクスチャの圧縮形式。
    /// 元テクスチャが BC1（DXT1）だとグラデーションがブロック状に潰れるため、既定は高画質（BC7。Android は ASTC 4x4）。
    /// 容量は BC1 の約 2 倍になるが、最適化ツールで再圧縮される環境ではその方が良い元データになる（ユーザー判断 2026-09-28）
    /// </summary>
    public enum BakeCompression
    {
        /// <summary>高画質: BC7（Android は ASTC 4x4）。元が非圧縮なら非圧縮のまま</summary>
        HighQuality = 0,
        /// <summary>元テクスチャと同じ形式（Crunch も維持）</summary>
        SameAsOriginal = 1,
        /// <summary>圧縮しない（RGBA32）</summary>
        Uncompressed = 2,
    }
}
