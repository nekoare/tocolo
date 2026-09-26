using System;

namespace Nekoare.ClickRecolor.Editor.Inspector
{
    /// <summary>
    /// Inspector に欄を差し込む口。有料版が InitializeOnLoad で登録する。
    /// null のまま（無料版）なら書き出し欄の代わりに「有料版で使えます」の案内を出す
    /// </summary>
    internal static class InspectorExtensions
    {
        /// <summary>書き出し欄を描く（引数は描画中の Inspector と対象のコンポーネント）</summary>
        public static Action<UnityEditor.Editor, ClickRecolor> DrawExportSection;
    }
}
