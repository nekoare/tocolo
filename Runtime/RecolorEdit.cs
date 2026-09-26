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
        [Range(0, 32)] public int padding = 8;
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

        public static string NewId() => Guid.NewGuid().ToString("N");

        public static RecolorEdit CreateNew()
        {
            return new RecolorEdit { id = NewId() };
        }
    }
}
