using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.NDMF;
using Nekoare.ClickRecolor.Editor.Picking;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>ツールバーの「影響範囲」3 ボタンに対応するプリセット（島＝島モード、同じ色／似た色＝色モード。値は RecolorSceneTool.ApplyPreset）</summary>
    internal enum RangePreset { Island = 0, SameColor = 1, SimilarColor = 2, Box = 3 }

    /// <summary>ツールのエディタ状態。コンポーネントには保存しない（ビルドに乗せない）</summary>
    internal static class ToolSession
    {
        private const string KeyRoot = "ClickRecolor.ActiveRoot";
        private const string KeyPreset = "ClickRecolor.Preset";
        private const string KeyCurrentEdit = "ClickRecolor.CurrentEditId";
        private const string KeyHighlight = "ClickRecolor.HighlightEnabled";
        private const string KeyColorScope = "ClickRecolor.ColorScopePreference";

        public static RangePreset Preset
        {
            get => (RangePreset)SessionState.GetInt(KeyPreset, (int)RangePreset.Island);
            set => SessionState.SetInt(KeyPreset, (int)value);
        }

        /// <summary>
        /// 色モード（同じ色／似た色）の編集を作るときの範囲。パネルで範囲を変えると覚える（既定はつながった範囲）。
        /// ApplyPreset が使う。「アバター全体」なら作成時に連結される
        /// </summary>
        public static ColorScope ColorScopePreference
        {
            get => (ColorScope)SessionState.GetInt(KeyColorScope, (int)ColorScope.Contiguous);
            set => SessionState.SetInt(KeyColorScope, (int)value);
        }

        /// <summary>プリセットから導かれる選択モード（島＝Island、同じ色／似た色＝Color、箱の中＝Box）</summary>
        public static SelectionMode Mode => Preset switch
        {
            RangePreset.Island => SelectionMode.Island,
            RangePreset.Box => SelectionMode.Box,
            _ => SelectionMode.Color,
        };

        /// <summary>
        /// 選択の箱をドラッグ中か。「箱の中の色を揃える」（パーツごとの統計）は重いので、ドラッグ中は掛けず離したときに掛ける
        /// （RecolorPipeline が見る。プレビューのハッシュにも入れて、離した瞬間に作り直す）
        /// </summary>
        public static bool BoxDragging { get; set; }

        /// <summary>最後にクリックした結果（Repaint で描く）。ドメインリロードで消えてよい</summary>
        public static PickHit? LastPick;

        /// <summary>最後にクリックした位置の見本色（クリック時に 1 回だけ取る）。取れなければ null</summary>
        public static Color? LastPickColor;

        /// <summary>マウスが乗っている位置の判定結果（ホバーの丸を Repaint で描く）。外れていれば null。ドメインリロードで消えてよい</summary>
        public static PickHit? HoverPick;

        /// <summary>
        /// パネルの色欄が操作する編集の id（最後にクリックで作った編集）。未設定は null。
        /// 編集そのものは Undo で List ごと入れ替わるので参照を持たず、使うたびに ClickRecolor.FindEdit で引く
        /// </summary>
        public static string CurrentEditId
        {
            get
            {
                string id = SessionState.GetString(KeyCurrentEdit, "");
                return string.IsNullOrEmpty(id) ? null : id;
            }
            set
            {
                SessionState.SetString(KeyCurrentEdit, value ?? "");
                PublishHighlight();
            }
        }

        /// <summary>一時メッセージ（Ctrl＋クリックで種を足せなかった理由など）を出しておく秒数</summary>
        public const double TransientNoticeSeconds = 3.0;

        private static string s_transientNoticeKey;
        private static object[] s_transientNoticeArgs;
        private static double s_transientNoticeUntil;

        /// <summary>
        /// パネルに一時的に出すメッセージの翻訳キー。出してから TransientNoticeSeconds 秒を過ぎたら null。
        /// エディタ上の案内のためだけなので、ドメインリロードで消えてよい
        /// </summary>
        public static string TransientNotice =>
            s_transientNoticeKey != null && EditorApplication.timeSinceStartup < s_transientNoticeUntil ? s_transientNoticeKey : null;

        /// <summary>一時メッセージ（TransientNotice）の書式の引数（{0} など）。引数が無ければ空の配列</summary>
        public static object[] TransientNoticeArgs => s_transientNoticeArgs ?? System.Array.Empty<object>();

        /// <summary>翻訳キー key の一時メッセージを TransientNoticeSeconds 秒だけ出す（前のメッセージは置き換える）。args は書式の引数</summary>
        public static void ShowTransientNotice(string key, params object[] args)
        {
            s_transientNoticeKey = key;
            s_transientNoticeArgs = args;
            s_transientNoticeUntil = EditorApplication.timeSinceStartup + TransientNoticeSeconds;
        }

        /// <summary>
        /// 編集 id → 種のチャートに同じ UV を使う別のパーツがあるか。編集作成時に 1 回だけ計算して覚える。
        /// エディタ上の注記のためだけなので、ドメインリロードで消えてよい（消えたら注記が出ないだけ）
        /// </summary>
        private static readonly Dictionary<string, bool> s_hasTwin = new Dictionary<string, bool>();

        /// <summary>
        /// 現在の編集（CurrentEditId）の種のチャートに、同じ UV を使う別のパーツがあるか（UvTwinDetector）。
        /// 覚えていない編集は false
        /// </summary>
        public static bool CurrentEditHasTwin
        {
            get
            {
                string id = CurrentEditId;
                return id != null && s_hasTwin.TryGetValue(id, out var hasTwin) && hasTwin;
            }
        }

        /// <summary>編集 editId の双子判定の結果を覚える（CurrentEditHasTwin が読む）</summary>
        public static void SetEditHasTwin(string editId, bool hasTwin)
        {
            if (string.IsNullOrEmpty(editId)) return;
            s_hasTwin[editId] = hasTwin;
        }

        private static readonly Dictionary<string, bool> s_hairToolTarget = new Dictionary<string, bool>();

        /// <summary>現在の編集の種 Renderer が髪ツール（キメラヘアマスター）の対象か（HairToolTargetDetector。覚えていない編集は false）</summary>
        public static bool CurrentEditIsHairToolTarget
        {
            get
            {
                string id = CurrentEditId;
                return id != null && s_hairToolTarget.TryGetValue(id, out var v) && v;
            }
        }

        public static void SetEditIsHairToolTarget(string editId, bool value)
        {
            if (string.IsNullOrEmpty(editId)) return;
            s_hairToolTarget[editId] = value;
        }

        private static bool s_eyedropperActive;

        /// <summary>
        /// スポイト中か（ON の間、Scene の次のクリックでその場所の元の色を編集中の色に入れる。取ったら OFF）。ツール終了で消える。
        /// OFF にしたときは Scene のカーソルを標準に戻す（ON 中は RecolorSceneTool が十字のカーソルを出す）
        /// </summary>
        public static bool EyedropperActive
        {
            get => s_eyedropperActive;
            set
            {
                if (s_eyedropperActive == value) return;
                s_eyedropperActive = value;
                if (!value) Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
            }
        }

        /// <summary>
        /// Prefab の編集モード中か。NDMF のプレビューは Prefab 編集モードでは描画を差し替えないため、Tocolo の結果も Scene に出ない。
        /// その間はクリックを受け付けず、案内だけ出す（ユーザー判断 2026-09-26）
        /// </summary>
        public static bool IsInPrefabMode => UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() != null;

        private const string KeyCrossTexture = "ClickRecolor.CrossTextureEnabled";
        private const string KeySelectHidden = "ClickRecolor.SelectHiddenEnabled";

        /// <summary>［隠れている範囲も選ぶ］: 矩形選択で、他のメッシュの奥に隠れているUVアイランドも拾うか（既定 OFF）</summary>
        public static bool SelectHiddenEnabled
        {
            get => SessionState.GetBool(KeySelectHidden, false);
            set => SessionState.SetBool(KeySelectHidden, value);
        }

        /// <summary>［マテリアルをまたいで選ぶ］: Ctrl＋クリック／矩形で別テクスチャの島を足せるか（既定 OFF）</summary>
        public static bool CrossTextureEnabled
        {
            get => SessionState.GetBool(KeyCrossTexture, false);
            set => SessionState.SetBool(KeyCrossTexture, value);
        }

        /// <summary>［範囲を表示］: 現在の編集の選択範囲をプレビューにハイライトするか（既定 ON）</summary>
        public static bool HighlightEnabled
        {
            // 再起動後も残す（EditorPrefs。ユーザー要望 2026-09-27）。既定 ON
            get => EditorPrefs.GetBool(KeyHighlight, true);
            set
            {
                EditorPrefs.SetBool(KeyHighlight, value);
                PublishHighlight();
            }
        }

        /// <summary>
        /// 現在の編集と［範囲を表示］をプレビューのハイライト状態へ反映する。連結なら全メンバーをハイライトする。
        /// 編集が無ければ出さない。同じ値なら publish しない（プレビューを無駄に作り直さない）
        /// </summary>
        public static void PublishHighlight()
        {
            string id = CurrentEditId;
            SetHighlight(ResolveHighlight(id, HighlightEnabled && id != null));
        }

        /// <summary>ハイライトを消す（ツールの終了時。現在の編集と［範囲を表示］の設定はそのまま）</summary>
        public static void HideHighlight()
        {
            SetHighlight(ResolveHighlight(CurrentEditId, false));
        }

        /// <summary>
        /// 編集 id のハイライト状態。対象ルートの ClickRecolor にその編集があれば、連結の全メンバーの id を入れる（HighlightState.For）。
        /// 見つからなければ（対象未設定・Undo 直後など）id だけ
        /// </summary>
        private static HighlightState ResolveHighlight(string id, bool enabled)
        {
            if (id != null && TryGetActiveRoot(out var root))
            {
                var component = root.GetComponent<ClickRecolor>();
                var edit = component != null ? component.FindEdit(id) : null;
                if (edit != null) return HighlightState.For(component, edit, enabled);
            }
            return new HighlightState(id, enabled);
        }

        private static void SetHighlight(in HighlightState state)
        {
            if (RecolorPreview.Highlight.Value.Equals(state)) return;
            RecolorPreview.Highlight.Value = state;
        }

        /// <summary>
        /// 編集対象（ルート GameObject）を覚える。null で解除。
        /// 対象を決めただけではコンポーネントを付けない（ClickRecolor は最初の編集を作るときに付ける）
        /// </summary>
        public static void SetActiveRoot(GameObject root)
        {
            SessionState.SetInt(KeyRoot, root != null ? root.GetInstanceID() : 0);
        }

        /// <summary>編集対象（ルート GameObject）。未設定・破棄済みなら false</summary>
        public static bool TryGetActiveRoot(out GameObject root)
        {
            root = EditorUtility.InstanceIDToObject(SessionState.GetInt(KeyRoot, 0)) as GameObject;
            return root != null;
        }

        public static void Clear()
        {
            SessionState.EraseBool(KeyCrossTexture);
            SessionState.EraseBool(KeySelectHidden);
            SessionState.EraseInt(KeyRoot);
            SessionState.EraseInt(KeyPreset);
            SessionState.EraseString(KeyCurrentEdit);
            SessionState.EraseInt(KeyColorScope);
            s_hasTwin.Clear();
            s_hairToolTarget.Clear();
            s_transientNoticeKey = null;
            s_transientNoticeArgs = null;
            LastPick = null;
            LastPickColor = null;
            HoverPick = null;
            EyedropperActive = false;
            HideHighlight();
        }
    }
}
