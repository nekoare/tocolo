using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Localization;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// 編集一覧（Inspector と Scene の小窓）の 1 行に出す種類と警告の印。連結（「アバター全体」・島の連結）は 1 行にまとめるので、
    /// 警告は連結のどれか 1 つでも当てはまれば出す
    /// </summary>
    internal static class EditRowInfo
    {
        /// <summary>範囲の種類（色・箱・パーツ）。「画像を入れる」ON の編集は範囲の色でなく画像を貼る編集なので「画像」</summary>
        internal static string ModeLabel(RecolorEdit edit)
        {
            if (edit == null) return string.Empty;
            if (edit.decalEnabled) return Locales.Tr("Inspector:Edit:Decal");
            if (edit.matchEnabled) return Locales.Tr("Inspector:Edit:Match");
            return edit.mode switch
            {
                SelectionMode.Color => Locales.Tr("Inspector:Edit:Mode:Color"),
                SelectionMode.Box => Locales.Tr("Inspector:Edit:Mode:Box"),
                _ => Locales.Tr("Inspector:Edit:Mode:Island"),
            };
        }

        /// <summary>
        /// 行（members は連結の全編集。単独ならその編集だけ）の警告の説明。無ければ null。
        /// usedTextures はアバター内のマテリアルが使っているメインテクスチャ（null ならテクスチャの使われ方は見ない）
        /// </summary>
        internal static string WarningTooltip(IEnumerable<RecolorEdit> members, HashSet<Texture2D> usedTextures)
        {
            foreach (var edit in members)
            {
                if (Pipeline.RecolorPipeline.IsSeedMissing(edit)) return Locales.Tr("Inspector:Edit:MissingTooltip");
            }
            // テクスチャがアバター内のどのマテリアルにも使われていない編集（マテリアル差し替え・テクスチャ削除）も同じ印で知らせる。
            // こちらは元に戻せば効くので、「見つからない編集を削除」の対象にはしない
            var unused = UnusedTexture(members, usedTextures, out bool textureDeleted);
            if (textureDeleted) return Locales.Tr("Inspector:Edit:TextureMissingTooltip");
            if (unused != null) return Locales.Tr("Inspector:Edit:TextureUnusedTooltip", unused.name);
            // 画像が消えた編集も同じ印で知らせる。画像を入れ直せば効くので「見つからない編集を削除」の対象にはしない
            foreach (var edit in members)
            {
                if (edit != null && edit.HasMissingDecal) return Locales.Tr("Inspector:Edit:DecalMissingTooltip");
            }
            // お手本が消えた編集は控えの色で効き続けるが、お手本の色を変えても追従しないので知らせる。
            // お手本を指定し直せば済むので「見つからない編集を削除」の対象にはしない
            foreach (var edit in members)
            {
                if (HasMissingMatchReference(edit)) return Locales.Tr("Inspector:Edit:MatchMissingTooltip");
                // 髪ツールの髪になって止めた編集（控えを捨てたので見た目を変えない）
                if (edit != null && edit.matchEnabled && Pipeline.MatchReference.LastStatus(edit) == Pipeline.MatchReferenceStatus.ChmHair)
                {
                    return Locales.Tr("Scene:Match:ChmHair");
                }
            }
            return null;
        }

        /// <summary>
        /// 一覧の色見本の色（sRGB）。お手本に合わせる編集は合わせた結果の代表色（目標色は使っていないので出さない）、
        /// それ以外は目標色
        /// </summary>
        internal static Color SwatchColor(RecolorEdit edit)
        {
            if (edit == null) return Color.gray;
            if (!edit.matchEnabled) return edit.targetColor;
            if (!Colors.MatchProfile.TryParse(edit.matchProfileData, out var profile)) return Color.gray;
            var lab = Colors.MatchRepresentative.Result(profile, edit);
            return Colors.OklabConverter.OklchToSRGBGamutMapped(Colors.OklabConverter.OklabToOklch(lab));
        }

        /// <summary>
        /// お手本に合わせる編集で、指定したお手本の Renderer が削除などで無くなったか（未指定とは区別する: Unity は消えた参照を偽の null で残す）
        /// </summary>
        internal static bool HasMissingMatchReference(RecolorEdit edit) =>
            edit != null && edit.matchEnabled && !ReferenceEquals(edit.matchRenderer, null)
            && edit.matchRenderer.GetInstanceID() != 0 && edit.matchRenderer == null;

        /// <summary>
        /// members の中で、アバター内のどのマテリアルも使っていないテクスチャ（最初の 1 つ）。
        /// テクスチャ自体が無い（削除された）編集があれば textureDeleted = true。どちらも無ければ null
        /// </summary>
        private static Texture2D UnusedTexture(IEnumerable<RecolorEdit> members, HashSet<Texture2D> usedTextures, out bool textureDeleted)
        {
            textureDeleted = false;
            if (usedTextures == null) return null;
            foreach (var edit in members)
            {
                if (edit == null || !edit.IsKept) continue;
                if (edit.sourceTexture == null)
                {
                    textureDeleted = true;
                    return null;
                }
                if (!usedTextures.Contains(edit.sourceTexture)) return edit.sourceTexture;
            }
            return null;
        }

        /// <summary>警告の印を iconRect に描き、tooltip を付ける</summary>
        internal static void DrawWarnIcon(Rect iconRect, string tooltip)
        {
            // アイコンを GUI.Label に渡すと描かれないことがあったので、テクスチャを直接描く。取れなければ橙の「⚠」を文字で出す
            var texture = WarnIcon;
            var square = new Rect(iconRect.x + 1f, iconRect.y + (iconRect.height - 16f) * 0.5f, 16f, 16f);
            if (texture != null) GUI.DrawTexture(square, texture, ScaleMode.ScaleToFit);
            else
            {
                var previous = GUI.contentColor;
                GUI.contentColor = new Color(1f, 0.6f, 0.1f);
                GUI.Label(iconRect, "⚠", EditorStyles.boldLabel);
                GUI.contentColor = previous;
            }
            // ツールチップ用（文字は空）
            GUI.Label(iconRect, new GUIContent(string.Empty, tooltip));
        }

        /// <summary>警告アイコン（Unity の組み込みアイコン。ダークスキンは d_ 付き）。見つからなければ null</summary>
        private static Texture WarnIcon
        {
            get
            {
                if (s_warnIcon != null) return s_warnIcon;
                foreach (var name in EditorGUIUtility.isProSkin
                             ? new[] { "d_console.warnicon.sm", "console.warnicon.sm", "d_console.warnicon", "console.warnicon" }
                             : new[] { "console.warnicon.sm", "console.warnicon" })
                {
                    s_warnIcon = EditorGUIUtility.FindTexture(name);
                    if (s_warnIcon != null) break;
                }
                return s_warnIcon;
            }
        }

        private static Texture s_warnIcon;
    }
}
