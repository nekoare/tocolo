namespace Nekoare.ClickRecolor
{
    /// <summary>色モードのとき、近い色をどこまで探すか</summary>
    public enum ColorScope
    {
        /// <summary>クリック位置からテクセルがつながっている範囲</summary>
        Contiguous = 0,
        /// <summary>クリックした UV アイランドの中</summary>
        Island = 1,
        /// <summary>テクスチャ全体</summary>
        WholeTexture = 2,
        /// <summary>
        /// アバター全体: 対象ルート配下の全メインテクスチャに同じ種色の編集を作って連結する（RecolorEdit.groupId）。
        /// マスクは各テクスチャで WholeTexture と同じく色だけで選ぶ
        /// </summary>
        WholeAvatar = 3,
    }
}
