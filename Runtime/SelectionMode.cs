namespace Nekoare.ClickRecolor
{
    /// <summary>影響範囲の決め方</summary>
    public enum SelectionMode
    {
        /// <summary>クリックした UV アイランド（UV エッジで連結したチャート）</summary>
        Island = 0,
        /// <summary>クリックした色に近い色（閾値・ぼかし幅・スコープで調整）</summary>
        Color = 1,
    }
}
