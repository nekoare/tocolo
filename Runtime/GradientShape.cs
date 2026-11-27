namespace Nekoare.ClickRecolor
{
    /// <summary>グラデーションの混ぜ具合の決め方</summary>
    public enum GradientShape
    {
        /// <summary>Scene の箱の Y 軸に沿って混ぜる</summary>
        Box = 0,
        /// <summary>毛先（房の中でてっぺんから一番遠い所）からの面に沿った距離で混ぜる。毛先が色 2</summary>
        Tips = 1,
    }
}
