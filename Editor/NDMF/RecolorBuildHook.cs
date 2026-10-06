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
        /// <summary>アバターの色変えを焼き込み、焼いたテクスチャの枚数を返す（担当範囲を知らない古い有料版が登録する）</summary>
        public static Func<BuildContext, int> Bake;

        /// <summary>
        /// 担当範囲つきの焼き込み（新しい有料版が登録する）。本体の RecolorPass は Main、髪用の ChmHairPass は ChmHair で呼ぶ。
        /// null なら本体は Bake を呼び、髪用は何もしない
        /// </summary>
        public static Func<BuildContext, RecolorScope, int> BakeScoped;

        /// <summary>
        /// 有料版が対応している機能（Feature* のビット和）。有料版が Bake と一緒に登録する。
        /// 無料版だけ更新されて有料版が古いと、新しい機能の編集がアップロードで黙って消えるので、RecolorPass がこれを見て警告する
        /// （古い有料版はこの欄を知らないので 0 のまま）
        /// </summary>
        public static int SupportedFeatures;

        /// <summary>「なめらかに貼る」（重ね貼り）の編集をビルドに反映できる</summary>
        public const int FeatureDecalOverlay = 1;

        /// <summary>髪ツール（キメラヘアマスター）の髪の上に焼き込める（BakeScoped の ChmHair）</summary>
        public const int FeatureChmHair = 2;
    }
}
