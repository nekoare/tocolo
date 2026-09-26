using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 最近使った色（RecentColors）のテスト。EditorUserSettings を直接使うので、元の値を TearDown で戻す
    /// </summary>
    public class RecentColorsTests
    {
        private string _original;

        [SetUp]
        public void SetUp()
        {
            _original = EditorUserSettings.GetConfigValue(RecentColors.ConfigKey);
            RecentColors.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            // 元が未設定なら空文字で戻す（空文字は「色なし」として読まれる）
            EditorUserSettings.SetConfigValue(RecentColors.ConfigKey, _original ?? string.Empty);
        }

        [Test]
        public void 最大5色で_古い色から押し出される()
        {
            var colors = new[] { Color.red, Color.green, Color.blue, Color.yellow, Color.cyan, Color.magenta };
            foreach (var c in colors) RecentColors.Push(c);

            var list = RecentColors.Get();

            Assert.That(list.Count, Is.EqualTo(5));
            Assert.That(list[0], Is.EqualTo(Color.magenta)); // 最後に使った色が先頭
            Assert.That(list[4], Is.EqualTo(Color.green));   // 最初の赤は押し出される
            Assert.That(list, Has.No.Member(Color.red));
        }

        [Test]
        public void 同じ色は増やさず先頭へ移す()
        {
            RecentColors.Push(Color.red);
            RecentColors.Push(Color.green);
            RecentColors.Push(Color.blue);

            RecentColors.Push(Color.red);

            var list = RecentColors.Get();
            Assert.That(list.Count, Is.EqualTo(3));
            Assert.That(list[0], Is.EqualTo(Color.red));
            Assert.That(list[1], Is.EqualTo(Color.blue));
            Assert.That(list[2], Is.EqualTo(Color.green));
        }

        [Test]
        public void 保存文字列は16進のカンマ区切りで_読み戻せる()
        {
            RecentColors.Push(Color.red);
            RecentColors.Push(new Color(0f, 0.5f, 1f));

            string saved = EditorUserSettings.GetConfigValue(RecentColors.ConfigKey);
            Assert.That(saved, Is.EqualTo("#0080FF,#FF0000"));

            var parsed = RecentColors.Parse(saved);
            Assert.That(parsed.Count, Is.EqualTo(2));
            Assert.That(RecentColors.Serialize(parsed), Is.EqualTo(saved));
            Assert.That(parsed[1], Is.EqualTo(Color.red));
        }

        [Test]
        public void 読めない項目は飛ばす()
        {
            var parsed = RecentColors.Parse("#FF0000,xyz,,#00FF00");

            Assert.That(parsed.Count, Is.EqualTo(2));
            Assert.That(parsed[0], Is.EqualTo(Color.red));
            Assert.That(parsed[1], Is.EqualTo(Color.green));
        }

        [Test]
        public void Clear_で空になる()
        {
            RecentColors.Push(Color.red);

            RecentColors.Clear();

            Assert.That(RecentColors.Get(), Is.Empty);
        }
    }
}
