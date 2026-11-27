using Nekoare.ClickRecolor.Editor.UpdateCheck;
using NUnit.Framework;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 更新のお知らせ: 版の読み取り・listing から最新版を選ぶ・知らせるかの判定
    /// </summary>
    public class UpdateCheckLogicTests
    {
        private const string Package = "dev.nekoare.click-recolor";
        private const string Unity = "2022.3.22f1";

        private static PackageVersion V(string s)
        {
            Assert.That(PackageVersion.TryParse(s, out var v), Is.True, s);
            return v;
        }

        private static string Listing(params (string version, string unity)[] versions)
        {
            var entries = new System.Collections.Generic.List<string>();
            foreach (var (version, unity) in versions)
            {
                var unityField = unity == null ? "" : $"\"unity\": \"{unity}\", ";
                entries.Add($"\"{version}\": {{ \"name\": \"{Package}\", \"version\": \"{version}\", {unityField}" +
                            "\"author\": { \"name\": \"nekoare\" }, \"vpmDependencies\": { \"nadena.dev.ndmf\": \">=1.14.0\" } }");
            }
            return "{ \"name\": \"listing\", \"packages\": { \"" + Package + "\": { \"versions\": { " +
                   string.Join(", ", entries) + " } } } }";
        }

        // ----- 版の読み取り -----

        [Test]
        public void 数字三つの版だけを読める()
        {
            Assert.That(PackageVersion.TryParse("2.1.0", out var v), Is.True);
            Assert.That(v.ToString(), Is.EqualTo("2.1.0"));
        }

        [TestCase("2.1")]
        [TestCase("2.1.0-beta.1")]
        [TestCase("v2.1.0")]
        [TestCase("2.1.0 ")]
        [TestCase("2.1.0\n<b>x</b>")]
        [TestCase("")]
        [TestCase(null)]
        [TestCase("99999999999.0.0")]
        public void 数字三つ以外の版は読まない(string s)
        {
            Assert.That(PackageVersion.TryParse(s, out _), Is.False);
        }

        [Test]
        public void 版は文字ではなく数で比べる()
        {
            Assert.That(V("2.10.0").CompareTo(V("2.9.0")), Is.GreaterThan(0));
            Assert.That(V("1.6.10").CompareTo(V("1.6.9")), Is.GreaterThan(0));
            Assert.That(V("2.1.0").CompareTo(V("2.1.0")), Is.EqualTo(0));
        }

        // ----- listing から最新版を選ぶ -----

        [Test]
        public void 並び順に関係なく一番新しい版を選ぶ()
        {
            var json = Listing(("2.0.2", "2022.3"), ("2.1.0", "2022.3"), ("1.6.10", "2022.3"));
            Assert.That(UpdateCheckLogic.PickLatest(json, Package, Unity)?.ToString(), Is.EqualTo("2.1.0"));
        }

        [Test]
        public void プレリリース版は選ばない()
        {
            var json = Listing(("2.1.0", "2022.3"), ("2.2.0-beta.1", "2022.3"));
            Assert.That(UpdateCheckLogic.PickLatest(json, Package, Unity)?.ToString(), Is.EqualTo("2.1.0"));
        }

        [Test]
        public void 今のUnityで入れられない版は選ばない()
        {
            var json = Listing(("2.1.0", "2022.3"), ("3.0.0", "2023.1"));
            Assert.That(UpdateCheckLogic.PickLatest(json, Package, Unity)?.ToString(), Is.EqualTo("2.1.0"));
        }

        [Test]
        public void Unityの指定が無い版も選ぶ()
        {
            var json = Listing(("2.1.0", "2022.3"), ("2.2.0", null));
            Assert.That(UpdateCheckLogic.PickLatest(json, Package, Unity)?.ToString(), Is.EqualTo("2.2.0"));
        }

        [Test]
        public void 同じlistingにある他のパッケージの版は拾わない()
        {
            var json = "{ \"packages\": { " +
                       "\"com.nekoare.mask-creation-tool\": { \"versions\": { \"9.0.0\": { \"version\": \"9.0.0\" } } }, " +
                       "\"" + Package + "\": { \"versions\": { \"2.1.0\": { \"version\": \"2.1.0\", \"unity\": \"2022.3\" } } } } }";
            Assert.That(UpdateCheckLogic.PickLatest(json, Package, Unity)?.ToString(), Is.EqualTo("2.1.0"));
        }

        [TestCase("<html><body>404</body></html>")]
        [TestCase("")]
        [TestCase(null)]
        [TestCase("{ \"packages\": { \"dev.nekoare.click-recolor\": { \"versions\": { \"2.1.0\": { \"version\": \"2.1")]
        [TestCase("{ \"packages\": [] }")]
        [TestCase("{ \"packages\": { \"dev.nekoare.click-recolor\": { \"versions\": { \"2.1.0\": { \"version\": 2 } } } } }")]
        public void 読めない応答なら最新版なし(string json)
        {
            Assert.That(UpdateCheckLogic.PickLatest(json, Package, Unity), Is.Null);
        }

        [Test]
        public void 入れ子が深すぎる応答は途中でやめて最新版なし()
        {
            var json = new string('[', 100000) + new string(']', 100000);
            Assert.That(UpdateCheckLogic.PickLatest(json, Package, Unity), Is.Null);
        }

        [Test]
        public void JSONの文字列の中の記号や逃がし文字に惑わされない()
        {
            var json = "{ \"note\": \"}{ \\\"packages\\\" \\u0041\", \"packages\": { \"" + Package +
                       "\": { \"versions\": { \"2.1.0\": { \"version\": \"2.1.0\", \"description\": \"a\\\\b\\n{}\" } } } } }";
            Assert.That(UpdateCheckLogic.PickLatest(json, Package, Unity)?.ToString(), Is.EqualTo("2.1.0"));
        }

        // ----- 知らせるかの判定 -----

        [Test]
        public void 最新版が今の版より新しいときだけ知らせる()
        {
            Assert.That(UpdateCheckLogic.ShouldNotify(V("2.1.0"), V("2.2.0"), null), Is.True);
            Assert.That(UpdateCheckLogic.ShouldNotify(V("2.1.0"), V("2.1.0"), null), Is.False);
            Assert.That(UpdateCheckLogic.ShouldNotify(V("2.2.0"), V("2.1.0"), null), Is.False);
            Assert.That(UpdateCheckLogic.ShouldNotify(V("2.1.0"), null, null), Is.False);
        }

        [Test]
        public void スキップした版は知らせずそれより新しい版が出たら知らせる()
        {
            Assert.That(UpdateCheckLogic.ShouldNotify(V("2.1.0"), V("2.2.0"), V("2.2.0")), Is.False);
            Assert.That(UpdateCheckLogic.ShouldNotify(V("2.1.0"), V("2.3.0"), V("2.2.0")), Is.True);
        }

        [Test]
        public void 一番上の数字が変わる更新だけを大きな更新とみなす()
        {
            Assert.That(UpdateCheckLogic.IsMajorUpdate(V("2.1.0"), V("3.0.0")), Is.True);
            Assert.That(UpdateCheckLogic.IsMajorUpdate(V("2.1.0"), V("2.2.0")), Is.False);
        }

        // ----- 何を出すか（Scene のお知らせ） -----

        [Test]
        public void 版が取れなければ同意も含めて何も出さない()
        {
            Assert.That(UpdateCheckLogic.Decide(null, false, false, false, null, null, false), Is.EqualTo(UpdateNoticeKind.None));
        }

        [Test]
        public void まだ選んでいなければ同意を聞く()
        {
            Assert.That(UpdateCheckLogic.Decide(V("0.8.0"), false, false, false, null, null, false), Is.EqualTo(UpdateNoticeKind.Consent));
        }

        [Test]
        public void 使わないを選んだら新しい版があっても出さない()
        {
            Assert.That(UpdateCheckLogic.Decide(V("0.8.0"), true, false, false, V("0.9.0"), null, false), Is.EqualTo(UpdateNoticeKind.None));
        }

        [Test]
        public void 使うを選んで新しい版があれば知らせる()
        {
            Assert.That(UpdateCheckLogic.Decide(V("0.8.0"), true, true, false, V("0.9.0"), null, false), Is.EqualTo(UpdateNoticeKind.Update));
        }

        [Test]
        public void 確認中は出さず_最新版を使っていれば出さない()
        {
            Assert.That(UpdateCheckLogic.Decide(V("0.8.0"), true, true, true, V("0.9.0"), null, false), Is.EqualTo(UpdateNoticeKind.None));
            Assert.That(UpdateCheckLogic.Decide(V("0.8.0"), true, true, false, V("0.8.0"), null, false), Is.EqualTo(UpdateNoticeKind.None));
        }

        [Test]
        public void 閉じたあとやスキップした版は出さない()
        {
            Assert.That(UpdateCheckLogic.Decide(V("0.8.0"), true, true, false, V("0.9.0"), null, true), Is.EqualTo(UpdateNoticeKind.None));
            Assert.That(UpdateCheckLogic.Decide(V("0.8.0"), true, true, false, V("0.9.0"), V("0.9.0"), false), Is.EqualTo(UpdateNoticeKind.None));
        }
    }
}
