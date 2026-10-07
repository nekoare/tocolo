using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// パネルから、マテリアルなどを Inspector に出す。Unity の選択をその物に移す
    /// （別窓の Inspector を開く方法は窓が増えていくので採らない。Tocolo は Inspector を使わない作りなので、選択が外れても困らない）
    /// </summary>
    internal static class InspectorJump
    {
        /// <summary>
        /// target を選んで Inspector に出す。Inspector がすべてロックされているときは何もしない
        /// （選択だけ変わって表示は変わらず、Hierarchy の選択が外れるだけになるため）
        /// </summary>
        internal static void Select(Object target)
        {
            if (target == null || !HasUnlockedInspector()) return;
            Selection.activeObject = target;
        }

        /// <summary>
        /// ロックされていない Inspector があるか。Inspector の窓の型とロックの状態は公開されていないので名前で探し、
        /// 見つからなければ最初の Inspector が使う ActiveEditorTracker.sharedTracker のロックで判断する
        /// </summary>
        private static bool HasUnlockedInspector()
        {
            try
            {
                var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.InspectorWindow");
                var isLocked = type?.GetProperty("isLocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (isLocked != null && isLocked.PropertyType == typeof(bool))
                {
                    var windows = Resources.FindObjectsOfTypeAll(type);
                    if (windows.Length == 0) return false;
                    foreach (var window in windows)
                    {
                        if (!(bool)isLocked.GetValue(window)) return true;
                    }
                    return false;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Tocolo] Inspectorのロックの状態を取得できませんでした: {e.Message}");
            }
            return !ActiveEditorTracker.sharedTracker.isLocked;
        }
    }
}
