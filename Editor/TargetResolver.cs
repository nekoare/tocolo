using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nekoare.ClickRecolor.Editor
{
    /// <summary>
    /// 編集対象（ルート）の決定。ClickRecolor は対象決定時には付けず、編集を作るときにこのルートへ付ける。
    /// VRC SDK が無い環境でもコンパイルできるよう、VRCAvatarDescriptor は型ではなく型名の文字列で判定する
    /// </summary>
    internal static class TargetResolver
    {
        // アバター一覧は IMGUI のイベントごとに呼ばれるので、Hierarchy が変わるまでキャッシュする
        private static List<GameObject> s_avatarRootsCache;

        /// <summary>
        /// アバター一覧のキャッシュを捨てる。Hierarchy 変更イベントは次のエディタ更新まで届かないので、
        /// 同じフレーム内で作ったオブジェクトを見たいとき（テスト等）はこれを呼ぶ
        /// </summary>
        internal static void InvalidateAvatarRoots() { s_avatarRootsCache = null; }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.hierarchyChanged += InvalidateAvatarRoots;
        }

        private const string AvatarDescriptorTypeName = "VRCAvatarDescriptor";

        private static bool IsAvatarDescriptor(Component c) => c != null && c.GetType().Name == AvatarDescriptorTypeName;

        /// <summary>祖先（自身を含む）で VRCAvatarDescriptor を持つ GameObject。無ければ go の Hierarchy 最上位</summary>
        public static GameObject ResolveRoot(GameObject go)
        {
            if (go == null) return null;
            foreach (var c in go.GetComponentsInParent<Component>(true))
            {
                if (IsAvatarDescriptor(c)) return c.gameObject;
            }
            return go.transform.root.gameObject;
        }

        /// <summary>
        /// root の ClickRecolor を返す。無ければ Undo 付きで追加する。
        /// 対象決定時には呼ばない。編集を作るとき（とメニュー「選択中のアバターに追加」）に呼ぶ。
        /// Project の Prefab アセットには付けない（警告して null）
        /// </summary>
        public static ClickRecolor GetOrAddComponent(GameObject root)
        {
            if (root == null) return null;
            if (EditorUtility.IsPersistent(root))
            {
                Debug.LogWarning("[Tocolo] ProjectのPrefabには付けられません。シーンに置いたアバターを選んでください", root);
                return null;
            }
            var component = root.GetComponent<ClickRecolor>();
            if (component == null) component = Undo.AddComponent<ClickRecolor>(root);
            return component;
        }

        /// <summary>開いている全シーン（読み込み済みのみ）から VRCAvatarDescriptor を持つ GameObject を Hierarchy 順に集める</summary>
        public static List<GameObject> FindSceneAvatarRoots()
        {
            // 無効（チェックが外れている）か、Hierarchy の目のマークで隠されているアバターは対象の候補に数えない（ユーザー要望 2026-09-27）。
            // 表示状態はキャッシュに入れず毎回見る（目のマークの切り替えは hierarchyChanged を起こさないため）
            var all = FindAllSceneAvatarRoots();
            var visible = new List<GameObject>(all.Count);
            foreach (var go in all)
            {
                if (go == null || !go.activeInHierarchy) continue;
                if (SceneVisibilityManager.instance.IsHidden(go)) continue;
                visible.Add(go);
            }
            return visible;
        }

        /// <summary>
        /// 対象として使える状態か（有効で、Hierarchy の目のマークで隠されていない）。
        /// 対象にした後で無効化・非表示にされたアバターを外す判定に使う（ユーザー要望 2026-09-29）
        /// </summary>
        public static bool IsUsableTarget(GameObject root) =>
            root != null && root.activeInHierarchy && !SceneVisibilityManager.instance.IsHidden(root);

        /// <summary>シーンの全アバターのルート（有効・表示にかかわらず）。Hierarchy の変更まではキャッシュする</summary>
        private static List<GameObject> FindAllSceneAvatarRoots()
        {
            if (s_avatarRootsCache != null && s_avatarRootsCache.TrueForAll(g => g != null)) return s_avatarRootsCache;
            var result = new List<GameObject>();
            s_avatarRootsCache = result;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var rootObject in scene.GetRootGameObjects())
                {
                    foreach (var c in rootObject.GetComponentsInChildren<Component>(true))
                    {
                        if (IsAvatarDescriptor(c) && !result.Contains(c.gameObject)) result.Add(c.gameObject);
                    }
                }
            }
            return result;
        }
    }
}
