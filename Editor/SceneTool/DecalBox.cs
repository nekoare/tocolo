using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// 「画像を入れる」の ON/OFF と画像の割り当て（RecolorEdit.decal*）。箱の既定は GradientBox.DefaultFor（選択範囲の外接の箱・回転なし）。
    /// グラデーションと同じ置き方にするのは、どちらも「選択範囲に掛ける付属設定＋専用の箱」で、最初に範囲を囲んでいれば迷わないため
    /// </summary>
    internal static class DecalBox
    {
        /// <summary>
        /// ON/OFF を変える（Undo 記録つき）。ON にしたときは箱を既定（選択範囲の外接）に置き、confirmed にする。
        /// 連結なら全メンバーに同じ値を入れる（箱は現在の編集から決める）
        /// </summary>
        internal static void SetEnabled(ClickRecolor component, RecolorEdit edit, bool enabled)
        {
            if (component == null || edit == null || edit.decalEnabled == enabled) return;
            Undo.RecordObject(component, "Tocolo: 画像を入れるを切り替え");
            var box = enabled ? GradientBox.DefaultFor(component, edit) : default;
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.decalEnabled = enabled;
                if (!enabled) return;
                // 箱を置いた後に別の場所をクリックして、編集ごと仮の編集として捨てられないようにする
                // （ON にしただけで画像も色も無い空の編集が残るのは許容）
                e.confirmed = true;
                e.decalBoxPosition = box.position;
                e.decalBoxRotation = box.rotation;
                e.decalBoxSize = box.size;
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>
        /// 画像を割り当てる（Undo 記録つき）。連結なら全メンバーに同じ画像を入れる。
        /// 一度でも画像を入れた編集は confirmed にして、色の取り消しや画像 OFF で自動で捨てないようにする:
        /// 画像を OFF に戻した後に別の場所をクリックすると IsKept が false になり、仮の編集として捨てられて画像と箱が消えてしまう。
        /// 色を決めたときと同じ「一度決めたら残す」流儀
        /// </summary>
        internal static void SetTexture(ClickRecolor component, RecolorEdit edit, Texture2D texture)
        {
            if (component == null || edit == null) return;
            Undo.RecordObject(component, "Tocolo: 画像を変更");
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.decalTexture = texture;
                if (texture != null) e.confirmed = true;
            });
            EditorUtility.SetDirty(component);
        }
    }
}
