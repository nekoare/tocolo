using UnityEditor;

namespace Nekoare.ClickRecolor.Editor
{
    internal static class ClickRecolorMenuItems
    {
        // MenuItem の文字列は定数なのでローカライズできない
        private const string AddToAvatarPath = "Tools/Tocolo/選択中のアバターに追加";

        [MenuItem(AddToAvatarPath, false, 10)]
        private static void AddToAvatar()
        {
            var selected = Selection.activeGameObject;
            if (selected == null) return;

            var target = TargetResolver.ResolveRoot(selected);
            var component = TargetResolver.GetOrAddComponent(target);
            if (component == null) return;

            EditorGUIUtility.PingObject(component);
            Selection.activeGameObject = target;
        }

        [MenuItem(AddToAvatarPath, true)]
        private static bool AddToAvatarValidate() => Selection.activeGameObject != null && !EditorUtility.IsPersistent(Selection.activeGameObject);
    }
}
