using System;
using nadena.dev.ndmf;

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    /// <summary>
    /// ビルド時の焼き込みを差し込む口。有料版が InitializeOnLoad で登録する。
    /// null のまま（無料版）なら RecolorPass はアップロードに反映しない
    /// </summary>
    internal static class RecolorBuildHook
    {
        /// <summary>アバターの色変えを焼き込み、焼いたテクスチャの枚数を返す</summary>
        public static Func<BuildContext, int> Bake;
    }
}
