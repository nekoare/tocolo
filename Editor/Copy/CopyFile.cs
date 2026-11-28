using System;
using System.Collections.Generic;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.Copy
{
    /// <summary>別アバター・別プロジェクトへ編集を運ぶファイルの中身。ファイル 1 つで自己完結する（画像の中身も入れる）</summary>
    [Serializable]
    internal sealed class CopyFile
    {
        internal const string Format = "tocolo-copy";
        internal const int CurrentVersion = 1;

        public string format = Format;
        public int version = CurrentVersion;
        public string toolVersion;
        public List<CopyEntry> edits = new List<CopyEntry>();
        public List<CopyImage> images = new List<CopyImage>();
    }

    /// <summary>
    /// 編集 1 つ。設定は editJson（RecolorEdit の JSON。Unity の参照は読み込み時に捨てる）で、
    /// 場所は seed / extraSeeds で持つ。箱・点・長さは髪（主の種の Renderer）基準に直した値をここに持ち、editJson の同じ値は使わない
    /// </summary>
    [Serializable]
    internal sealed class CopyEntry
    {
        public string editJson;
        public CopyAnchor seed;
        public List<CopyAnchor> extraSeeds = new List<CopyAnchor>();
        public string imageKey;
        /// <summary>「アバター全体」の連結の主（クリックした 1 件）。移す先で連結を作り直す</summary>
        public bool wasGroupHead;
        /// <summary>島の連結のメンバーなら、同じ連結どうしで同じ値（元の groupId）。移す先で、移せたメンバーどうしをつなぎ直す</summary>
        public string islandGroupKey;
        /// <summary>
        /// 箱・点・長さの基準にした髪。島の連結は箱を共有するので、全メンバーを先頭の種の Renderer で直す
        /// （メンバーごとの髪で直すと、素体が違うときにテクスチャの境目で箱がずれる）。単独の編集は seed と同じ
        /// </summary>
        public CopyAnchor basis;

        public Vector3 gradientBoxPosition;
        public Quaternion gradientBoxRotation = Quaternion.identity;
        public Vector3 gradientBoxSize = Vector3.one;
        public Vector3 boxPosition;
        public Quaternion boxRotation = Quaternion.identity;
        public Vector3 boxSize = Vector3.one;
        public Vector3 decalBoxPosition;
        public Quaternion decalBoxRotation = Quaternion.identity;
        public Vector3 decalBoxSize = Vector3.one;
        public Vector3 gradientTipTop;
        public float gradientTipLength;
    }

    /// <summary>［画像を入れる］の画像。元のファイルのバイト列をそのまま入れる（読めない圧縮形式の資産を作り直すと画質が落ちるため）</summary>
    [Serializable]
    internal sealed class CopyImage
    {
        public string key;
        public string name;
        public string extension;
        public string base64;
        public bool sRGB = true;
        public bool alphaIsTransparency;
        public int wrapMode;
        public int filterMode = 1;
        public int maxTextureSize = 2048;
        public bool isReadable;
        public bool mipmapEnabled = true;
        /// <summary>
        /// 元の画像の読み込み設定（TextureImporterSettings の JSON）。種類（Sprite など）・ミップマップ・ラップなどをまとめてそろえる。
        /// EditorJsonUtility.ToJson(importer) を使わないのは、その形を FromJsonOverwrite で importer に重ねても反映されないため。
        /// 上の個別の値は、これを読めない（Unity の版が大きく違う）ときの最低限
        /// </summary>
        public string textureSettingsJson;
        /// <summary>圧縮・最大サイズ（既定と、PC・Android など上書きしているプラットフォームごと。TextureImporterPlatformSettings の JSON）</summary>
        public List<string> platformSettingsJson = new List<string>();
    }

    internal static class CopyFileIO
    {
        /// <summary>RecolorEdit の Unity の参照のフィールド名（読み込み時に必ず null にする）。増えたらテストが気づく</summary>
        internal static readonly string[] EditObjectFieldNames =
        {
            nameof(RecolorEdit.sourceTexture), nameof(RecolorEdit.seedRenderer), nameof(RecolorEdit.decalTexture), nameof(RecolorEdit.matchRenderer),
        };

        internal static readonly string[] SeedObjectFieldNames = { nameof(RecolorSeed.renderer) };

        /// <summary>字下げしない。足した場所の多い編集では字下げの空白だけでファイルが倍近くになるため</summary>
        internal static string Write(CopyFile file) => JsonUtility.ToJson(file, false);

        internal static bool TryRead(string text, out CopyFile file, out string error)
        {
            file = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text) || text.TrimStart()[0] != '{')
            {
                error = "not json";
                return false;
            }
            try
            {
                file = JsonUtility.FromJson<CopyFile>(text);
            }
            catch (ArgumentException e)
            {
                error = e.Message;
                return false;
            }
            if (file == null || file.format != CopyFile.Format)
            {
                error = "format";
                file = null;
                return false;
            }
            if (file.version > CopyFile.CurrentVersion)
            {
                error = $"version {file.version}";
                file = null;
                return false;
            }
            if (file.edits == null) file.edits = new List<CopyEntry>();
            if (file.images == null) file.images = new List<CopyImage>();
            return true;
        }

        /// <summary>ファイルの先頭だけで、コピーのファイルらしいかを見る（JsonUtility は format を最初に書く）</summary>
        internal static bool LooksLikeCopyFile(string head)
        {
            return !string.IsNullOrEmpty(head)
                && System.Text.RegularExpressions.Regex.IsMatch(head, @"""format""\s*:\s*""" + CopyFile.Format + @"""");
        }

        /// <summary>
        /// 足した場所（extraSeeds）は抜いて書く。場所は移す先で探す手がかり付きの CopyEntry.extraSeeds で運び、
        /// こちらは読み込み時に捨てるだけなので、入れるとファイルが重くなるだけ。
        /// JsonUtility は書かないフィールドを選べないため、書く間だけ空のリストに差し替える
        /// </summary>
        internal static string EditToJson(RecolorEdit edit)
        {
            var extraSeeds = edit.extraSeeds;
            edit.extraSeeds = new List<RecolorSeed>();
            try
            {
                return JsonUtility.ToJson(edit);
            }
            finally
            {
                edit.extraSeeds = extraSeeds;
            }
        }

        /// <summary>
        /// JSON から編集を作る。JsonUtility は Unity の参照を instanceID で書くので、同じセッションで読むと
        /// 元のアバターの Renderer やテクスチャを指したままになる → 参照は全部捨て、呼び出し側が移す先で解決し直す
        /// </summary>
        internal static RecolorEdit EditFromJson(string json)
        {
            var edit = JsonUtility.FromJson<RecolorEdit>(json);
            edit.id = RecolorEdit.NewId();
            edit.sourceTexture = null;
            edit.seedRenderer = null;
            edit.decalTexture = null;
            edit.matchRenderer = null;
            // 今の書き出しは extraSeeds を入れないが、手で書き換えたファイルに入っていても使わない（場所は CopyEntry 側だけを信じる）
            edit.extraSeeds = new List<RecolorSeed>();
            if (edit.gradientMidStops == null) edit.gradientMidStops = new List<RecolorGradientStop>();
            return edit;
        }
    }
}
