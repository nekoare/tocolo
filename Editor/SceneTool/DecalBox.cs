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
        /// ON/OFF を変える（Undo 記録つき）。ON にしたときは confirmed にし、初めて ON にしたときだけ箱を既定（選択範囲の外接）に置く
        /// （OFF→ON では前の箱のまま。ユーザー要望 2026-10-05）。連結なら全メンバーに同じ値を入れる（箱は現在の編集から決める）
        /// </summary>
        internal static void SetEnabled(ClickRecolor component, RecolorEdit edit, bool enabled)
        {
            if (component == null || edit == null || edit.decalEnabled == enabled) return;
            Undo.RecordObject(component, "Tocolo: 画像を入れるを切り替え");
            bool initialize = enabled && !edit.decalInitialized;
            var box = initialize ? GradientBox.DefaultFor(component, edit) : default;
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.decalEnabled = enabled;
                // OFF にした編集も初期化済みにする（この印が無い版で ON にした編集を、次の ON で置き直さない）
                e.decalInitialized = true;
                if (!enabled) return;
                // 箱を置いた後に別の場所をクリックして、編集ごと仮の編集として捨てられないようにする
                // （ON にしただけで画像も色も無い空の編集が残るのは許容）
                e.confirmed = true;
                if (initialize) PlaceBox(e, box.position, box.rotation, box.size);
            });
            EditorUtility.SetDirty(component);
        }

        /// <summary>パネルの［リセット］: 箱を既定（選択範囲の外接）に置き直す。画像と詳細設定は残す（Undo 記録つき。連結なら全メンバー）</summary>
        internal static void ResetSettings(ClickRecolor component, RecolorEdit edit)
        {
            if (component == null || edit == null) return;
            Undo.RecordObject(component, "Tocolo: 画像の箱をリセット");
            var box = GradientBox.DefaultFor(component, edit);
            EditGroups.ForEachInGroup(component, edit, e =>
            {
                e.decalInitialized = true;
                PlaceBox(e, box.position, box.rotation, box.size);
            });
            EditorUtility.SetDirty(component);
        }

        private static void PlaceBox(RecolorEdit e, Vector3 position, Quaternion rotation, Vector3 size)
        {
            e.decalBoxPosition = position;
            e.decalBoxRotation = rotation;
            e.decalBoxSize = size;
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
