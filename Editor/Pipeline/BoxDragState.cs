namespace Nekoare.ClickRecolor.Editor.Pipeline
{
    /// <summary>
    /// 箱をドラッグ中の編集。ドラッグ中はプレビューを軽く作り（選択の箱: パーツごとの統計・スーパーサンプリングなし、画像の箱: 低解像度の層）、
    /// 離したときに作り直す。書くのは Scene ツール、読むのはパイプラインとプレビューのハッシュ。
    /// 書き出し・ビルドは印を見ない（RecolorPipeline.PrepareJob の fromExport）
    /// </summary>
    internal static class BoxDragState
    {
        /// <summary>1 種類の箱のドラッグ。ドラッグ中の編集の id と連結の id を持つ（連結は同じ箱を共有するので、連結の全メンバーをドラッグ中とみなす）</summary>
        internal sealed class Drag
        {
            private string _editId;
            private string _groupId;

            public bool IsActive => _editId != null;

            /// <summary>ドラッグ中の編集の id（ドラッグ中でなければ null）</summary>
            public string EditId => _editId;

            /// <summary>edit の箱のドラッグを始めた（同じドラッグ中に何度呼んでもよい）。edit が null なら終える</summary>
            public void Begin(RecolorEdit edit)
            {
                _editId = edit?.id;
                _groupId = edit != null && !string.IsNullOrEmpty(edit.groupId) ? edit.groupId : null;
            }

            public void End()
            {
                _editId = null;
                _groupId = null;
            }

            /// <summary>
            /// edit の箱がドラッグ中か（ドラッグ中の編集そのものか、同じ連結のメンバー）。
            /// 印を全編集で 1 つにすると、ある編集の箱を動かしている間、関係ない編集まで軽い作り方になって色が跳ぶ
            /// </summary>
            public bool IsFor(RecolorEdit edit)
            {
                if (_editId == null || edit == null) return false;
                if (edit.id == _editId) return true;
                return _groupId != null && edit.groupId == _groupId;
            }
        }

        /// <summary>影響範囲「箱の中」の箱</summary>
        internal static readonly Drag Selection = new Drag();

        /// <summary>「画像を入れる」の箱（BeginDecal / EndDecal で変える）</summary>
        internal static readonly Drag Decal = new Drag();

        /// <summary>
        /// ドラッグ中の画像の箱の編集が重ね貼りで表示されているか。重ね貼りなら色変えのプレビュー（RecolorPreview）はドラッグ中に作り直さない
        /// （重ね貼りは表示用メッシュの UV の書き換えで追従する。焼き込みは箱の値で層を作り直す必要がある）
        /// </summary>
        public static bool DecalIsOverlay { get; private set; }

        /// <summary>
        /// ドラッグ中の重ね貼りが、作ったときに複製した範囲の外へ出た（DecalOverlayPreview が立てる）。ドラッグ中は箱の値をプレビューのハッシュに入れないので、
        /// 代わりにこれを入れて 1 回だけ作り直させる（ドラッグ中に作り直した段は範囲で絞らないので、その後は立たない）
        /// </summary>
        public static bool DecalNeedsFullCopy { get; private set; }

        /// <summary>
        /// edit の画像の箱のドラッグを始めた（同じドラッグ中に何度呼んでもよい）。overlay はその編集が重ね貼りで表示されているか
        /// （ドラッグ中の作り直しの要否に使う。ドラッグの始めに 1 回だけ判定して覚える）
        /// </summary>
        public static void BeginDecal(RecolorEdit edit, bool overlay = false)
        {
            // 同じドラッグ中の呼び直しでは下ろさない（下ろすと作り直しの合図が毎フレーム消える）
            if (!Decal.IsActive || Decal.EditId != edit?.id) DecalNeedsFullCopy = false;
            Decal.Begin(edit);
            DecalIsOverlay = overlay;
        }

        public static void EndDecal()
        {
            Decal.End();
            DecalIsOverlay = false;
            DecalNeedsFullCopy = false;
        }

        public static void RequestDecalFullCopy()
        {
            if (Decal.IsActive) DecalNeedsFullCopy = true;
        }

        /// <summary>どちらの箱のドラッグも終える（ツールの終了・状態の消去）</summary>
        public static void EndAll()
        {
            Selection.End();
            EndDecal();
        }
    }
}
