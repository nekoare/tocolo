namespace Nekoare.ClickRecolor
{
    /// <summary>
    /// Android（Quest）向けビルド・書き出しで生成するテクスチャの圧縮形式。PC 向けは BakeCompression。
    /// Quest は容量制限が厳しいので、PC の 3 択に「標準（ASTC 6x6）」を足してある
    /// </summary>
    public enum AndroidBakeCompression
    {
        /// <summary>高画質: ASTC 4x4（8 bpp。BC7 と同容量）。元が非圧縮なら非圧縮のまま</summary>
        HighQuality = 0,
        /// <summary>標準: ASTC 6x6（3.6 bpp。Quest で一般的な形式）。元が非圧縮なら非圧縮のまま</summary>
        Normal = 1,
        /// <summary>元テクスチャと同じ形式（Android で使えない DXT/BC 系なら ASTC 6x6）</summary>
        SameAsOriginal = 2,
        /// <summary>圧縮しない（RGBA32）。容量が大きく非推奨</summary>
        Uncompressed = 3,
    }
}
