namespace Nekoare.ClickRecolor
{
    /// <summary>プレビュー時の作業解像度（長辺の上限）。Full は縮小しない。
    /// 0 = 上限なし。(int) 値を長辺の上限として使う</summary>
    public enum WorkingResolution
    {
        R1024 = 1024,
        R2048 = 2048,
        Full = 0,
    }
}
