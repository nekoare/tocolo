using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor
{
    /// <summary>
    /// アバタールートに 1 つ付ける。編集リストを持ち、実際の加工は NDMF ビルド時に行う。
    /// シーン上のマテリアル・テクスチャ資産は一切変更しない。
    /// </summary>
    [AddComponentMenu("Tocolo/Tocolo")]
    [DisallowMultipleComponent]
    public sealed class ClickRecolor : MonoBehaviour
#if CLICKRECOLOR_VRCSDK3_AVATARS
        , VRC.SDKBase.IEditorOnly
#endif
    {
        public List<RecolorEdit> edits = new List<RecolorEdit>();
        public WorkingResolution previewResolution = WorkingResolution.R2048;
        public bool previewEnabled = true;
        public bool applyOnBuild = true;
        [HideInInspector] public int dataVersion = 1;

        /// <summary>
        /// 末尾に追加し、名前が空なら「編集 N」を付ける。
        /// Undo.RecordObject と EditorUtility.SetDirty は呼び出し側（Editor）の責任
        /// </summary>
        public RecolorEdit AddEdit(RecolorEdit edit)
        {
            if (edit == null) return null;
            if (string.IsNullOrEmpty(edit.id)) edit.id = RecolorEdit.NewId();
            if (string.IsNullOrEmpty(edit.name)) edit.name = $"編集 {edits.Count + 1}";
            edits.Add(edit);
            return edit;
        }

        public RecolorEdit FindEdit(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < edits.Count; i++)
            {
                if (edits[i] != null && edits[i].id == id) return edits[i];
            }
            return null;
        }

        /// <summary>
        /// id が一致する最初の編集を削除する。無ければ false。
        /// Undo.RecordObject と EditorUtility.SetDirty は呼び出し側（Editor）の責任
        /// </summary>
        public bool RemoveEdit(string id)
        {
            for (int i = 0; i < edits.Count; i++)
            {
                if (edits[i] != null && edits[i].id == id)
                {
                    edits.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Inspector の配列複製（「+」／Duplicate Array Element）で id が重複するのを防ぐ。
        /// 空または既出の id を後勝ちで再採番し、前の要素の id は保つ
        /// </summary>
        internal void OnValidate()
        {
            var seen = new HashSet<string>();
            foreach (var e in edits)
            {
                if (e == null) continue;
                if (string.IsNullOrEmpty(e.id) || !seen.Add(e.id)) { e.id = RecolorEdit.NewId(); seen.Add(e.id); }
            }
        }

        /// <summary>
        /// Project の Prefab アセットに直接付けられたら警告して外す。
        /// Reset 内では直接 Destroy できないので 1 フレーム遅延させる。
        /// シーンのインスタンスから Apply で Prefab に載る経路は Reset を通らないので対象外
        /// （Inspector 側で開始ボタンを無効化して対応）
        /// </summary>
        private void Reset()
        {
            if (!UnityEditor.EditorUtility.IsPersistent(this)) return;
            Debug.LogWarning("[Tocolo] Project の Prefab には付けられません。シーンに置いたアバターに付けてください", this);
            UnityEditor.EditorApplication.delayCall += () => { if (this != null) DestroyImmediate(this, true); };
        }
#endif
    }
}
