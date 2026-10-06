using Nekoare.ClickRecolor.Editor.NDMF;
using Nekoare.ClickRecolor.Tests.Fakes;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    public class HairToolTargetsTests
    {
        private GameObject _avatar;
        private SkinnedMeshRenderer _hair;
        private ChimeraHairMaster _chm;

        [SetUp]
        public void SetUp()
        {
            _avatar = new GameObject("avatar");
            var hair = new GameObject("hair");
            hair.transform.SetParent(_avatar.transform);
            _hair = hair.AddComponent<SkinnedMeshRenderer>();
            _chm = _avatar.AddComponent<ChimeraHairMaster>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_avatar != null) Object.DestroyImmediate(_avatar);
        }

        private HairToolRole RoleOfHair(bool orderSupported)
        {
            var roles = HairToolTargets.RolesIn(_avatar, orderSupported);
            return roles.TryGetValue(_hair, out var role) ? role : HairToolRole.None;
        }

        [Test]
        public void CHMの対象でなければ_役割はNone()
        {
            Assert.That(RoleOfHair(orderSupported: true), Is.EqualTo(HairToolRole.None));
        }

        [Test]
        public void CHMが無効なら_対象でも役割はNone()
        {
            _chm.targetRenderers.Add(_hair);
            _chm.isEnabled = false;

            Assert.That(RoleOfHair(orderSupported: true), Is.EqualTo(HairToolRole.None));
        }

        [Test]
        public void 統合OFFの対象は_順序の宣言があればOver()
        {
            _chm.targetRenderers.Add(_hair);

            Assert.That(RoleOfHair(orderSupported: true), Is.EqualTo(HairToolRole.Over));
        }

        [Test]
        public void 統合ONの対象は_順序の宣言があればMerged()
        {
            _chm.targetRenderers.Add(_hair);
            _chm.enableMeshMerge = true;

            Assert.That(RoleOfHair(orderSupported: true), Is.EqualTo(HairToolRole.Merged));
        }

        [Test]
        public void 順序の宣言が無ければ_統合に関係なくLegacy()
        {
            _chm.targetRenderers.Add(_hair);

            Assert.That(RoleOfHair(orderSupported: false), Is.EqualTo(HairToolRole.Legacy));
            _chm.enableMeshMerge = true;
            Assert.That(RoleOfHair(orderSupported: false), Is.EqualTo(HairToolRole.Legacy));
        }

        [Test]
        public void 統合ONと統合OFFの両方のCHMの対象なら_Merged()
        {
            _chm.targetRenderers.Add(_hair);
            var merged = _avatar.AddComponent<ChimeraHairMaster>();
            merged.enableMeshMerge = true;
            merged.targetRenderers.Add(_hair);

            Assert.That(RoleOfHair(orderSupported: true), Is.EqualTo(HairToolRole.Merged));
        }

        [Test]
        public void 本体はNoneとLegacyを担当し_髪用はOverだけを担当する()
        {
            Assert.That(HairToolTargets.InScope(RecolorScope.Main, HairToolRole.None), Is.True);
            Assert.That(HairToolTargets.InScope(RecolorScope.Main, HairToolRole.Legacy), Is.True);
            Assert.That(HairToolTargets.InScope(RecolorScope.Main, HairToolRole.Over), Is.False);
            Assert.That(HairToolTargets.InScope(RecolorScope.Main, HairToolRole.Merged), Is.False);

            Assert.That(HairToolTargets.InScope(RecolorScope.ChmHair, HairToolRole.Over), Is.True);
            Assert.That(HairToolTargets.InScope(RecolorScope.ChmHair, HairToolRole.None), Is.False);
            Assert.That(HairToolTargets.InScope(RecolorScope.ChmHair, HairToolRole.Legacy), Is.False);
            Assert.That(HairToolTargets.InScope(RecolorScope.ChmHair, HairToolRole.Merged), Is.False);
        }

        [Test]
        public void 髪ツールを消すと_同じRendererの役割はその場でNoneに戻る()
        {
            // アバターの外（アバターのルートが見つからない）では Renderer 自身の下を探すので、髪ツールを髪に付ける
            var chm = _hair.gameObject.AddComponent<ChimeraHairMaster>();
            chm.targetRenderers.Add(_hair);
            Assert.That(HairToolTargets.RoleOf(_hair), Is.Not.EqualTo(HairToolRole.None), "前提: 対象なら None 以外（宣言の有無で Over か Legacy）");

            Object.DestroyImmediate(chm);

            Assert.That(HairToolTargets.RoleOf(_hair), Is.EqualTo(HairToolRole.None));
        }

        [Test]
        public void 同じ設定のスナップショットは等しく_対象が変わると等しくない()
        {
            _chm.targetRenderers.Add(_hair);
            var a = HairToolTargets.SnapshotOf(_chm);
            var b = HairToolTargets.SnapshotOf(_chm);
            Assert.That(a.Equals(b), Is.True);

            _chm.targetRenderers.Clear();
            Assert.That(a.Equals(HairToolTargets.SnapshotOf(_chm)), Is.False);
        }
    }
}
