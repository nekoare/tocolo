using Nekoare.ClickRecolor.Editor.Copy;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>コピーで、箱・点・長さを「対象ルート基準」と「髪基準」のあいだで直す（素体が違っても髪に付いてくる）</summary>
    public class HairSpaceTests
    {
        private CopyTestKit _kit;

        [SetUp] public void SetUp() => _kit = new CopyTestKit();
        [TearDown] public void TearDown() => _kit.Dispose();

        private (Transform root, Transform hair) MakeAvatar(Vector3 hairPosition, Quaternion hairRotation, float hairScale)
        {
            var root = _kit.MakeRoot("Avatar").transform;
            var hair = new GameObject("Hair").transform;
            hair.SetParent(root, false);
            hair.localPosition = hairPosition;
            hair.localRotation = hairRotation;
            hair.localScale = Vector3.one * hairScale;
            return (root, hair);
        }

        private static void Move(Transform fromRoot, Transform fromHair, Transform toRoot, Transform toHair,
            ref Vector3 position, ref Quaternion rotation, ref Vector3 size)
        {
            HairSpace.ToBasis(HairSpace.RootToBasis(fromRoot, fromHair), ref position, ref rotation, ref size);
            HairSpace.FromBasis(HairSpace.RootToBasis(toRoot, toHair), ref position, ref rotation, ref size);
        }

        [Test]
        public void 髪の位置が同じなら箱は往復で変わらない()
        {
            var (rootA, hairA) = MakeAvatar(new Vector3(0, 1.5f, 0), Quaternion.Euler(0, 30, 0), 1f);
            var (rootB, hairB) = MakeAvatar(new Vector3(0, 1.5f, 0), Quaternion.Euler(0, 30, 0), 1f);
            var p = new Vector3(0.1f, 1.6f, 0.2f);
            var r = Quaternion.Euler(10, 20, 0);
            var s = new Vector3(0.3f, 0.4f, 0.5f);

            Move(rootA, hairA, rootB, hairB, ref p, ref r, ref s);

            Assert.That(Vector3.Distance(p, new Vector3(0.1f, 1.6f, 0.2f)), Is.LessThan(1e-4f));
            Assert.That(Quaternion.Angle(r, Quaternion.Euler(10, 20, 0)), Is.LessThan(0.01f));
            Assert.That(Vector3.Distance(s, new Vector3(0.3f, 0.4f, 0.5f)), Is.LessThan(1e-4f));
        }

        [Test]
        public void 移す先で髪が上に10cmずれていれば箱も10cm上に付く()
        {
            var (rootA, hairA) = MakeAvatar(new Vector3(0, 1.5f, 0), Quaternion.identity, 1f);
            var (rootB, hairB) = MakeAvatar(new Vector3(0, 1.6f, 0), Quaternion.identity, 1f);
            var p = new Vector3(0, 1.4f, 0);
            var r = Quaternion.identity;
            var s = Vector3.one * 0.2f;

            Move(rootA, hairA, rootB, hairB, ref p, ref r, ref s);

            Assert.That(Vector3.Distance(p, new Vector3(0, 1.5f, 0)), Is.LessThan(1e-4f));
        }

        [Test]
        public void 移す先で髪が1点2倍なら箱の大きさと毛先の長さも1点2倍になる()
        {
            var (rootA, hairA) = MakeAvatar(new Vector3(0, 1f, 0), Quaternion.identity, 1f);
            var (rootB, hairB) = MakeAvatar(new Vector3(0, 1f, 0), Quaternion.identity, 1.2f);
            var p = new Vector3(0, 1.5f, 0);
            var r = Quaternion.identity;
            var s = new Vector3(0.2f, 0.4f, 0.2f);

            Move(rootA, hairA, rootB, hairB, ref p, ref r, ref s);

            Assert.That(Vector3.Distance(p, new Vector3(0, 1.6f, 0)), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(s, new Vector3(0.24f, 0.48f, 0.24f)), Is.LessThan(1e-4f));

            float length = 0.05f;
            length *= HairSpace.ScaleOf(HairSpace.RootToBasis(rootA, hairA));
            length /= HairSpace.ScaleOf(HairSpace.RootToBasis(rootB, hairB));
            Assert.That(length, Is.EqualTo(0.06f).Within(1e-4f));
        }

        [Test]
        public void 回転した髪では箱の向きも一緒に回る()
        {
            var (rootA, hairA) = MakeAvatar(Vector3.zero, Quaternion.identity, 1f);
            var (rootB, hairB) = MakeAvatar(Vector3.zero, Quaternion.Euler(0, 90, 0), 1f);
            var p = new Vector3(1, 0, 0);
            var r = Quaternion.identity;
            var s = Vector3.one;

            Move(rootA, hairA, rootB, hairB, ref p, ref r, ref s);

            Assert.That(Vector3.Distance(p, new Vector3(0, 0, -1)), Is.LessThan(1e-4f));
            Assert.That(Quaternion.Angle(r, Quaternion.Euler(0, 90, 0)), Is.LessThan(0.01f));
        }

        [Test]
        public void スキンメッシュはルートボーンを基準にする()
        {
            var root = _kit.MakeRoot("Avatar");
            var bone = new GameObject("Head").transform;
            bone.SetParent(root.transform, false);
            var smr = new GameObject("Hair").AddComponent<SkinnedMeshRenderer>();
            smr.transform.SetParent(root.transform, false);
            smr.rootBone = bone;

            Assert.AreSame(bone, HairSpace.BasisOf(smr));
        }
    }
}
