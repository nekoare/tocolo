using UnityEditor;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// スライダーやホイールのドラッグ 1 回分の変更を Undo 1 回にまとめる。
    /// 掴んだ瞬間に Begin、離した瞬間に End を呼ぶ（間の各変更は従来どおり Undo.RecordObject で記録する）
    /// </summary>
    internal sealed class UndoDragScope
    {
        private int _group = -1;

        public bool IsActive => _group >= 0;

        public void Begin(string name)
        {
            if (IsActive) End();
            Undo.IncrementCurrentGroup();
            _group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(name);
        }

        public void End()
        {
            if (!IsActive) return;
            Undo.CollapseUndoOperations(_group);
            _group = -1;
        }
    }
}
