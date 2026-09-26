using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using nadena.dev.ndmf.localization;
using NUnit.Framework;
using UnityEditor;

namespace Nekoare.ClickRecolor.Tests
{
    public class PoFilesTests
    {
        private const string Dir = "Packages/dev.nekoare.click-recolor/Editor/Localization";

        private static readonly Regex MsgIdRegex = new Regex("^msgid \"(.+)\"$");
        private static readonly Regex MsgStrRegex = new Regex("^msgstr \"(.*)\"$");
        private static readonly Regex PlaceholderRegex = new Regex(@"\{(\d+)");

        private static HashSet<string> ReadMsgIds(string path)
        {
            var ids = new HashSet<string>();
            foreach (var line in File.ReadAllLines(path))
            {
                var m = MsgIdRegex.Match(line);
                if (m.Success) ids.Add(m.Groups[1].Value);
            }
            return ids;
        }

        // msgid 行の直後の msgstr 行をペアにする。ヘッダの空 msgid は MsgIdRegex に一致しないので除外される。
        // msgstr は 1 行で書く前提（継続行形式は非対応）
        private static Dictionary<string, string> ReadEntries(string path)
        {
            var entries = new Dictionary<string, string>();
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                var id = MsgIdRegex.Match(lines[i]);
                if (!id.Success) continue;
                var str = i + 1 < lines.Length ? MsgStrRegex.Match(lines[i + 1]) : Match.Empty;
                if (!str.Success)
                {
                    Assert.Fail($"msgid \"{id.Groups[1].Value}\" の直後に msgstr がありません（msgstr は 1 行で書く前提）");
                }
                entries[id.Groups[1].Value] = str.Groups[1].Value;
            }
            return entries;
        }

        private static HashSet<string> PlaceholderNumbers(string msgstr)
        {
            var numbers = new HashSet<string>();
            foreach (Match m in PlaceholderRegex.Matches(msgstr))
            {
                numbers.Add(m.Groups[1].Value);
            }
            return numbers;
        }

        [Test]
        public void msgstr_の中にエスケープされていない二重引用符が無い()
        {
            // PO では文中の " は \" にする必要がある。MsgStrRegex は貪欲一致なので、ここで別に検査する
            foreach (var file in new[] { "ja-JP.po", "en-US.po" })
            {
                foreach (var (id, text) in ReadEntries(Path.Combine(Dir, file)))
                {
                    Assert.That(Regex.IsMatch(text, "(^|[^\\\\])\""), Is.False, $"{file}: {id} の msgstr に未エスケープの \" があります");
                }
            }
        }

        [Test]
        public void ja_と_en_の_msgid_集合が一致する()
        {
            var ja = ReadMsgIds(Path.Combine(Dir, "ja-JP.po"));
            var en = ReadMsgIds(Path.Combine(Dir, "en-US.po"));

            Assert.That(ja, Is.Not.Empty);
            Assert.That(en, Is.EquivalentTo(ja));
        }

        [Test]
        public void msgid_ごとのプレースホルダ番号が_ja_と_en_で一致する()
        {
            var ja = ReadEntries(Path.Combine(Dir, "ja-JP.po"));
            var en = ReadEntries(Path.Combine(Dir, "en-US.po"));

            Assert.That(ja, Is.Not.Empty);
            foreach (var pair in ja)
            {
                Assert.That(en.ContainsKey(pair.Key), Is.True, $"en-US.po に msgid \"{pair.Key}\" がありません");
                Assert.That(
                    PlaceholderNumbers(en[pair.Key]),
                    Is.EquivalentTo(PlaceholderNumbers(pair.Value)),
                    $"msgid \"{pair.Key}\" のプレースホルダ番号が ja と en で一致しません");
            }
        }

        [Test]
        public void meta_の_GUID_が_Locales_の定数と一致する()
        {
            var jaMeta = File.ReadAllText(Path.Combine(Dir, "ja-JP.po.meta"));
            var enMeta = File.ReadAllText(Path.Combine(Dir, "en-US.po.meta"));

            Assert.That(jaMeta, Does.Contain("guid: " + Nekoare.ClickRecolor.Editor.Localization.Locales.JaGuid));
            Assert.That(enMeta, Does.Contain("guid: " + Nekoare.ClickRecolor.Editor.Localization.Locales.EnGuid));
        }

        [Test]
        public void GUID_から_po_のパスが引ける()
        {
            Assert.That(
                AssetDatabase.GUIDToAssetPath(Nekoare.ClickRecolor.Editor.Localization.Locales.JaGuid),
                Does.EndWith("Editor/Localization/ja-JP.po"));
            Assert.That(
                AssetDatabase.GUIDToAssetPath(Nekoare.ClickRecolor.Editor.Localization.Locales.EnGuid),
                Does.EndWith("Editor/Localization/en-US.po"));
        }

        [Test]
        public void Tr_は現在の言語の訳文を返す()
        {
            // LanguagePrefs の setter は EditorPrefs に書くため、必ず元に戻す
            var original = LanguagePrefs.Language;
            try
            {
                // 見出し（Inspector:Title）は ja/en とも商品名「Tocolo」で同じなので、言語で違う説明文で確かめる
                LanguagePrefs.Language = "ja-jp";
                Assert.That(Nekoare.ClickRecolor.Editor.Localization.Locales.Tr("Inspector:Description"), Does.StartWith("Scene でクリック"));

                LanguagePrefs.Language = "en-us";
                Assert.That(Nekoare.ClickRecolor.Editor.Localization.Locales.Tr("Inspector:Description"), Does.StartWith("Click a spot"));
            }
            finally
            {
                LanguagePrefs.Language = original;
            }
        }
    }
}
