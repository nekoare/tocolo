using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Decal;
using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>パネルの対象（アバター）の選択・切り替えと、対象に関する案内</summary>
    internal sealed partial class ToolPanelOverlay
    {
        /// <summary>「対象を変える」。対象を外して選び直す（自動選択は走らないので、アバター 1 体でも小物を選べる）。HintOverlay から呼ぶ</summary>
        internal static void ChangeTarget()
        {
            s_autoSelectQueued = true; // 選び直し中はアバター 1 体でも自動選択しない
            RecolorSceneTool.SetTarget(null);
            SceneView.RepaintAll();
        }

        /// <summary>
        /// 最後のクリックが Read/Write 無効のメッシュに当たったときの案内と［Read/Write を有効にする］（ユーザー判断 2026-09-29: 案 B）。
        /// 書き換えられない場所のメッシュならボタンは出さず、元の FBX で有効にするよう案内する
        /// </summary>
        private static void DrawUnreadableNotice(GameObject root)
        {
            var renderer = ToolSession.UnreadableRenderer;
            if (renderer == null || !renderer.transform.IsChildOf(root.transform)) return;
            var mesh = RendererMeshAccess.GetSharedMesh(renderer);
            // 直った（他の方法で有効にされた）なら消す。部品の数を変えないよう描画はこのイベントでは続ける
            bool fixable = mesh != null && Picking.MeshReadWriteFixer.GetFixablePath(mesh) != null;
            EditorGUILayout.HelpBox(
                Locales.Tr(fixable ? "Scene:Panel:Unreadable" : "Scene:Panel:UnreadableLocked", renderer.name),
                MessageType.Warning);
            if (!fixable) return;
            if (GUILayout.Button(Locales.Tr("Scene:Panel:UnreadableFix")))
            {
                // ダイアログと再インポートは OnGUI の外で行う
                EditorApplication.delayCall += () =>
                {
                    if (Picking.MeshReadWriteFixer.Fix(new[] { RendererMeshAccess.GetSharedMesh(renderer) }))
                    {
                        ToolSession.UnreadableRenderer = null;
                        SceneView.RepaintAll();
                    }
                };
                GUIUtility.ExitGUI();
            }
        }

        private static bool s_dropQueued;
        private static int s_lastExcludedHash;
        private static bool s_excludedSyncQueued;

        /// <summary>
        /// 除外リスト（オーバーレイ・Inspector どちらで変えても）が変わったら、「箱の中」の現在の編集の連結メンバーを次の更新で揃え直す
        /// （除外した Renderer のテクスチャのメンバーが外れる／戻すと戻る。マスク自体は鍵に除外が入っているので自動で作り直る。ユーザー要望 2026-09-29）
        /// </summary>
        private static void SyncBoxMembersIfExcludedChanged()
        {
            if (!ToolSession.TryGetActiveRoot(out var root)) return;
            var component = root.GetComponent<ClickRecolor>();
            if (component == null) return;
            int hash = 17;
            if (component.excludedRenderers != null)
            {
                unchecked
                {
                    foreach (var renderer in component.excludedRenderers) hash = hash * 31 + (renderer != null ? renderer.GetInstanceID() : 0);
                    hash = hash * 31 + component.excludedRenderers.Count;
                }
            }
            if (hash == s_lastExcludedHash) return;
            s_lastExcludedHash = hash;
            if (s_excludedSyncQueued) return;
            var edit = component.FindEdit(ToolSession.CurrentEditId);
            if (edit == null || edit.mode != SelectionMode.Box) return;
            s_excludedSyncQueued = true;
            // OnGUI の途中でメンバー数（部品の数）を変えないよう次の更新で
            EditorApplication.delayCall += () =>
            {
                s_excludedSyncQueued = false;
                if (!RecolorSceneTool.IsActive || !ToolSession.TryGetActiveRoot(out var currentRoot)) return;
                var currentComponent = currentRoot.GetComponent<ClickRecolor>();
                var current = currentComponent != null ? currentComponent.FindEdit(ToolSession.CurrentEditId) : null;
                if (current == null || current.mode != SelectionMode.Box) return;
                Undo.RecordObject(currentComponent, "Tocolo: 除外リストの変更を反映");
                SelectionBox.SyncMembers(currentComponent, current);
                SceneView.RepaintAll();
            };
        }

        /// <summary>
        /// 対象が無効化・非表示（目のマーク）になっていたら次の更新で外す。外れた後は対象未設定と同じ流れになり、
        /// 候補が 1 体なら自動で選び、2 体以上なら候補のボタンを出す（ユーザー要望 2026-09-29: A を非表示にして開いたら B を選んでほしい）。
        /// OnGUI の途中で外すと Layout と Repaint で部品の数が変わるので delayCall にする
        /// </summary>
        private static void DropTargetIfUnusable()
        {
            if (s_dropQueued || !ToolSession.TryGetActiveRoot(out var root) || TargetResolver.IsUsableTarget(root)) return;
            s_dropQueued = true;
            EditorApplication.delayCall += () =>
            {
                s_dropQueued = false;
                if (!RecolorSceneTool.IsActive || !ToolSession.TryGetActiveRoot(out var current) || TargetResolver.IsUsableTarget(current)) return;
                RecolorSceneTool.SetTarget(null);
                ResetAutoSelect(); // 外した後の自動選択を許す
                SceneView.RepaintAll();
            };
        }

        /// <summary>自動選択を積んだか。ツールの有効化ごとに 1 回だけ走らせる（RecolorSceneTool.OnActivated がリセット）</summary>
        private static bool s_autoSelectQueued;

        internal static void ResetAutoSelect() { s_autoSelectQueued = false; }

        /// <summary>対象の候補: シーンのアバターと、アバター一覧に無い選択中オブジェクトのルート（小物単体用）</summary>
        private static List<GameObject> CollectTargetCandidates(out GameObject selectionRoot)
        {
            var avatars = TargetResolver.FindSceneAvatarRoots();
            selectionRoot = null;
            var selected = Selection.activeGameObject;
            if (selected != null && !EditorUtility.IsPersistent(selected))
            {
                var root = TargetResolver.ResolveRoot(selected);
                if (!avatars.Contains(root)) selectionRoot = root;
            }
            return avatars;
        }

        /// <summary>対象になりうるものの数（シーンのアバター＋アバターでない選択中のルート）。HintOverlay が切り替えボタンを出すかの判定に使う</summary>
        internal static int CountTargetCandidates()
        {
            var avatars = CollectTargetCandidates(out var selectionRoot);
            return avatars.Count + (selectionRoot != null ? 1 : 0);
        }

        /// <summary>
        /// アバターが 1 体だけで選択中の候補も無いときは、次の更新で自動で対象にする（ツールの有効化ごとに 1 回）。
        /// 早期 return はしない（Layout でフラグが立ち、同じ描画パスの Repaint で部品の数が変わると IMGUI のレイアウト例外になる）
        /// </summary>
        private static void QueueAutoSelectIfSingleAvatar()
        {
            var avatars = CollectTargetCandidates(out var selectionRoot);
            if (avatars.Count == 1 && selectionRoot == null && !s_autoSelectQueued) QueueAutoSelect(avatars[0]);
        }

        /// <summary>
        /// 対象未設定時の候補ボタン（「操作」オーバーレイ＝HintOverlay に出す）。シーンのアバターと選択中のオブジェクトのルートを並べる
        /// </summary>
        internal static void DrawTargetButtons()
        {
            var avatars = CollectTargetCandidates(out var selectionRoot);
            foreach (var avatar in avatars)
            {
                if (GUILayout.Button(avatar.name)) SelectTarget(avatar);
            }
            if (selectionRoot != null && GUILayout.Button(Locales.Tr("Scene:Panel:UseSelection", selectionRoot.name)))
            {
                SelectTarget(selectionRoot);
            }
        }

        /// <summary>対象（ルート）を覚えるだけ。コンポーネントは付けない（付けるのは最初の編集を作るとき）</summary>
        private static void SelectTarget(GameObject root)
        {
            RecolorSceneTool.SetTarget(root);
        }

        /// <summary>OnGUI の途中（Layout と Repaint の間）でパネルの中身が切り替わらないよう、次のエディタ更新で 1 回だけ対象にする</summary>
        private static void QueueAutoSelect(GameObject avatar)
        {
            if (s_autoSelectQueued) return;
            s_autoSelectQueued = true;
            EditorApplication.delayCall += () =>
            {
                // 積んでから実行までの間に、アバターが消えた・ツールが終了した・対象が決まった場合は何もしない
                if (avatar == null || !RecolorSceneTool.IsActive || ToolSession.TryGetActiveRoot(out _)) return;
                SelectTarget(avatar);
                // パネルの表示を対象ありに切り替える
                SceneView.RepaintAll();
            };
        }
    }
}
