using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor
{
    /// <summary>
    /// 1 回のクリック＝1 編集。選択の「仕様」と目標色を持ち、マスクは保存しない
    /// （プレビュー・ビルドのたびに元テクスチャから再計算する）。
    /// </summary>
    [Serializable]
    public sealed class RecolorEdit
    {
        public string id;
        public string name;
        public bool enabled = true;

        // ── 対象 ──
        /// <summary>編集対象＝元テクスチャ資産。同じテクスチャを使う Renderer 全部に効く</summary>
        public Texture2D sourceTexture;
        /// <summary>島の再計算用。色モードのスコープ「テクスチャ全体」では未使用</summary>
        public Renderer seedRenderer;
        public int seedSubmesh;
        /// <summary>クリックした三角形（サブメッシュ内ローカル番号）。島の再計算で seedUv から三角形を探し直さないために持つ</summary>
        public int seedTriangle;
        /// <summary>クリック位置（マテリアルの Tiling/Offset 適用後、0..1）</summary>
        public Vector2 seedUv;
        /// <summary>クリック時の種色（近傍平均・sRGB）。表示と再現性確認用</summary>
        public Color seedColor = Color.white;
        /// <summary>
        /// 色マスクの種色（Oklab）。hasSeedOklab のときだけ使う。「アバター全体」の連結では、クリックしたテクスチャの
        /// 作業 RT から取った 5×5 近傍の平均を全員で共有する（他のテクスチャには種の画素が無いため）
        /// </summary>
        public Vector3 seedOklab;
        public bool hasSeedOklab;
        /// <summary>
        /// Ctrl＋クリックで足した種（主種 seedRenderer 等に加えて選ぶ島・色）。空なら主種だけ。
        /// 種ごとのマスクを合成して 1 つの範囲にする。共有の種色（hasSeedOklab）の編集では使わない
        /// </summary>
        public List<RecolorSeed> extraSeeds = new List<RecolorSeed>();
        /// <summary>
        /// 「アバター全体」で同時に作った編集の連結 id。空なら単独の編集。
        /// 連結の編集は各テクスチャに 1 つずつあり、色・選択の設定をパネルからまとめて変える
        /// </summary>
        public string groupId;

        // ── 選択 ──
        public SelectionMode mode = SelectionMode.Island;
        public ColorScope scope = ColorScope.Contiguous;
        [Range(0f, 1f)] public float threshold = 0.15f;
        [Range(0f, 1f)] public float feather = 0.10f;
        [Range(0, 2)] public int cleanupRadius = 0;
        /// <summary>はみ出し幅（px）。正なら UV の空きへ広げ、負なら選択範囲を縮める（縁を |padding| px 削る。ユーザー要望 2026-09-29）</summary>
        [Range(-5, 32)] public int padding = 8;
        /// <summary>
        /// 種ごとに色を揃えるか。true なら Ctrl＋クリックで足した種ごとに明るさの分布（統計）を取り、種ごとに変換する
        /// （各島がそれぞれ目標の明るさ範囲いっぱいに写る）。false なら選択全体で 1 つの分布を取る（島どうしの明るさの差が保たれる）。
        /// 種が 1 つなら効かない。連結（groupId）の編集では、false なら全メンバーで 1 つの分布を共有し、true ならメンバーごとに取る
        /// </summary>
        public bool perSeedStats = true;

        // ── 色（CHM の Oklab 変換と同じパラメータ）──
        public Color targetColor = Color.white;
        /// <summary>false の間は見た目を変えない（クリック直後）</summary>
        public bool hasTarget;
        /// <summary>
        /// 一度でも色を決めた編集か。色の指定を取り消して hasTarget が false に戻っても、この編集は自動で捨てない
        /// （選択範囲を残し、再クリックで選び直せる。ユーザー判断 2026-10-03: 案 A）。クリック直後の仮の編集は false
        /// </summary>
        public bool confirmed;

        /// <summary>
        /// 選択範囲を残す編集か（色が決まっている、または一度でも色を決めた、または画像入り）。false なら自動で捨ててよい仮の編集。
        /// 画像入りも残す: 画像を入れた時点で見た目が変わるので、新しい色が未決定でも捨てない
        /// </summary>
        public bool IsKept => hasTarget || confirmed || HasDecal;
        [Range(0f, 1f)] public float darkEndRatio = 0.7f;
        [Range(0f, 1f)] public float lightnessToTarget = 1f;
        [Range(0f, 1f)] public float chromaToTarget = 1f;
        [Range(0f, 1f)] public float hueRetain = 0f;
        [Range(0f, 1f)] public float strength = 1f;
        /// <summary>
        /// 陰影の強調。0 なら従来どおり（元の明るさの幅が狭いほど傾き 1 の加算オフセット寄り）、1 なら幅に関係なく
        /// 選択内の [p05, p95] を目標の明るさ範囲いっぱいへリマップする。色 1・色 2 で共通
        /// </summary>
        [Range(0f, 1f)] public float shadingStretch;
        /// <summary>
        /// 元のグラデーションを打ち消す（実験的。ユーザー要望 2026-09-29）: 選択範囲を上下（グラデーション ON なら箱の向き）の帯に分け、
        /// 帯ごとの明るさの幅で目標へ写す。根元〜毛先の明暗差が消え、帯の中の陰影は残る
        /// </summary>
        public bool flattenBase;
        /// <summary>打ち消す強さ（0 = 従来の全体 1 つの分布、1 = 帯ごとに完全に揃える）</summary>
        [Range(0f, 1f)] public float flattenStrength = 1f;

        // ── グラデーション（Scene の箱の Y 軸に沿って「新しい色」→「終了色」）──
        /// <summary>true なら箱の下端（t=0）で targetColor、上端（t=1）で gradientColor になるよう目標色を混ぜる（両端の外は端の色）</summary>
        public bool gradientEnabled;
        /// <summary>終了色（箱の上端の目標色・sRGB）</summary>
        public Color gradientColor = Color.white;
        /// <summary>色 2（箱の上端）の暗部の明るさ。グラデーション ON のとき、テクセルの t で darkEndRatio と混ぜる</summary>
        [Range(0f, 1f)] public float gradientDarkEndRatio = 0.7f;
        /// <summary>色 2（箱の上端）の強さ。グラデーション ON のとき、テクセルの t で strength と混ぜる</summary>
        [Range(0f, 1f)] public float gradientStrength = 1f;
        /// <summary>箱の中心（対象ルート＝ClickRecolor の GameObject のローカル座標）</summary>
        public Vector3 gradientBoxPosition;
        /// <summary>箱の回転（対象ルートのローカル）</summary>
        public Quaternion gradientBoxRotation = Quaternion.identity;
        /// <summary>箱の大きさ（対象ルートのローカル。Y が混ぜる向きの長さ）</summary>
        public Vector3 gradientBoxSize = Vector3.one;
        /// <summary>true なら箱の外側（上下・横のどこでも）は t=0（新しい色のまま）。false なら箱の Y 方向だけで混ぜ、横の外側にも続く</summary>
        public bool gradientInsideOnly;

        // ── 選択の箱（影響範囲「箱の中」。グラデーションの箱とは別）──
        /// <summary>選択の箱の中心（対象ルートのローカル座標）</summary>
        public Vector3 boxPosition;
        /// <summary>選択の箱の回転（対象ルートのローカル）</summary>
        public Quaternion boxRotation = Quaternion.identity;
        /// <summary>選択の箱の大きさ（対象ルートのローカル）</summary>
        public Vector3 boxSize = Vector3.one;
        /// <summary>
        /// 箱の中: true なら箱に入るパーツ（UV のまとまり）ごとに明るさの分布を取って、それぞれを目標の色に揃える（パーツ複数選択の「各点の色を揃える」と同じ）。
        /// false（既定）なら箱の中全体で 1 つの分布（パーツどうしの明るさの差が残る）。true は箱のドラッグ中は掛けず、離したときに掛ける（重いため）
        /// </summary>
        public bool boxPerPartStats;

        // ── 画像を入れる（デカール。設計 docs/plans/2026-10-04-tocolo-decal-design.md）──
        /// <summary>true なら decalTexture を専用の箱で表面に投影して貼る。ON の間、色の設定（新しい色・暗部・不透明度・グラデーション）は範囲でなく画像に掛かる</summary>
        public bool decalEnabled;
        /// <summary>貼る画像（Project の Texture2D）。null なら何も貼らない</summary>
        public Texture2D decalTexture;
        /// <summary>true なら画像の縦横比を保って箱の XY に内接させる。false なら箱の XY いっぱいに引き伸ばす</summary>
        public bool decalKeepAspect = true;
        /// <summary>画像の箱の中心（対象ルートのローカル）。+Z 面から −Z 方向へ投影する</summary>
        public Vector3 decalBoxPosition;
        /// <summary>画像の箱の回転（対象ルートのローカル）</summary>
        public Quaternion decalBoxRotation = Quaternion.identity;
        /// <summary>画像の箱の大きさ（対象ルートのローカル。負なら反転）</summary>
        public Vector3 decalBoxSize = Vector3.one;
        /// <summary>
        /// なめらかに貼る。lilToon・Poiyomi Toon（ロックなし）のマテリアルでは焼き込まずに、箱に入る三角形を複製したサブメッシュとして重ね、
        /// 元マテリアルの複製（透過）で画像を直接サンプリングする（焼き込みは貼る場所のテクスチャ密度より細かくなれないため）。
        /// それ以外のマテリアルが混ざると効かず焼き込みになる（DecalOverlayMaterial.CanOverlay）
        /// </summary>
        public bool decalSmooth = true;
        /// <summary>
        /// 「ノーマルも反映」: なめらかに貼るとき、元の法線マップを画像の座標へ写し直して重ね貼りの面にも使う（服のしわ・陰影が画像に乗る）。
        /// 法線マップ 1 枚ぶんテクスチャが増える（大きさは元の法線マップの密度に合わせる）。焼き込みでは効かない
        /// </summary>
        public bool decalNormal = true;

        /// <summary>画像を貼る編集か（ON かつ画像あり）</summary>
        public bool HasDecal => decalEnabled && decalTexture != null;

        /// <summary>
        /// 画像を入れる ON で、入れていた画像の資産が消えた（参照切れ）か。この編集は何もしない（範囲の色変えにも化けさせない。
        /// 色の設定は画像に掛けるつもりで決めているため）。一覧に警告アイコンを出す。
        /// 未設定（None）とは区別する: Unity は未設定の欄も偽の null で埋めることがあるが、その InstanceID は 0
        /// </summary>
        public bool HasMissingDecal =>
            decalEnabled && !ReferenceEquals(decalTexture, null) && decalTexture.GetInstanceID() != 0 && decalTexture == null;

        public static string NewId() => Guid.NewGuid().ToString("N");

        public static RecolorEdit CreateNew()
        {
            return new RecolorEdit { id = NewId() };
        }
    }
}
