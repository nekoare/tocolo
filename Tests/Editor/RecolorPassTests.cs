using nadena.dev.ndmf;
using Nekoare.ClickRecolor.Editor.NDMF;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 本体（無料版）のビルドパス。焼き込みのフック（RecolorBuildHook.Bake）が無いときは報告だけして何も変えないこと、
    /// あるときはフックに任せることを RecolorPass.ExecuteCore（BuildContext 無しの seam）で確かめる
    /// </summary>
    public class RecolorPassTests
    {
        private GameObject _root;
        private Texture2D _texture;
        private Material _material;

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_texture != null) Object.DestroyImmediate(_texture);
            if (_material != null) Object.DestroyImmediate(_material);
        }

        /// <summary>_root の下に、編集のテクスチャをメインに持つ髪と、それを対象にする髪ツール（代役）を置く</summary>
        private void AddHairUsingEditTexture(bool merge = false)
        {
            var hair = new GameObject("hair");
            hair.transform.SetParent(_root.transform);
            var renderer = hair.AddComponent<SkinnedMeshRenderer>();
            _material = new Material(Shader.Find("Standard"));
            _material.SetTexture("_MainTex", _texture);
            renderer.sharedMaterial = _material;
            var chm = _root.AddComponent<Fakes.ChimeraHairMaster>();
            chm.enableMeshMerge = merge;
            chm.targetRenderers.Add(renderer);
        }

        /// <summary>ビルドに効く編集（目標色あり・対象テクスチャあり）を 1 つ持つアバター</summary>
        private ClickRecolor MakeAvatarWithEdit()
        {
            _root = new GameObject("avatar");
            var component = _root.AddComponent<ClickRecolor>();
            _texture = new Texture2D(1, 1);
            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = _texture;
            edit.targetColor = Color.red;
            edit.hasTarget = true;
            component.AddEdit(edit);
            return component;
        }

        [Test]
        public void フックが無ければ_Error_FreeEdition_を_NonFatal_で_1_件報告し_コンポーネントも編集も変えない()
        {
            var component = MakeAvatarWithEdit();
            // 同じアバターに効く編集を持つコンポーネントが複数あっても報告は 1 回
            var child = new GameObject("child");
            child.transform.SetParent(_root.transform);
            var childComponent = child.AddComponent<ClickRecolor>();
            var childEdit = RecolorEdit.CreateNew();
            childEdit.sourceTexture = _texture;
            childEdit.hasTarget = true;
            childComponent.AddEdit(childEdit);
            var edit = component.edits[0];

            int count = -1;
            var errors = ErrorReport.CaptureErrors(() => count = RecolorPass.ExecuteCore(_root, null));

            Assert.That(count, Is.EqualTo(0));
            Assert.That(errors.Count, Is.EqualTo(1));
            Assert.That(errors[0].TheError.Severity, Is.EqualTo(ErrorSeverity.NonFatal));
            Assert.That((errors[0].TheError as SimpleError)?.TitleKey, Is.EqualTo("Error:FreeEdition"));
            Assert.That(_root.GetComponentsInChildren<ClickRecolor>(true), Has.Length.EqualTo(2));
            Assert.That(component.edits, Has.Count.EqualTo(1));
            Assert.That(component.edits[0], Is.SameAs(edit));
            Assert.That(edit.enabled, Is.True);
            Assert.That(edit.hasTarget, Is.True);
            Assert.That(edit.targetColor, Is.EqualTo(Color.red));
        }

        [Test]
        public void フックが無くても効く編集が無ければ報告しない()
        {
            var component = MakeAvatarWithEdit();
            component.edits[0].hasTarget = false;

            var errors = ErrorReport.CaptureErrors(() => RecolorPass.ExecuteCore(_root, null));

            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void フックが無くても_applyOnBuild_が_false_なら報告しない()
        {
            var component = MakeAvatarWithEdit();
            component.applyOnBuild = false;

            var errors = ErrorReport.CaptureErrors(() => RecolorPass.ExecuteCore(_root, null));

            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void フックがあればそれを呼び_戻り値を返して報告しない()
        {
            MakeAvatarWithEdit();
            int calls = 0;

            int count = -1;
            var errors = ErrorReport.CaptureErrors(() => count = RecolorPass.ExecuteCore(_root, () => { calls++; return 3; }));

            Assert.That(calls, Is.EqualTo(1));
            Assert.That(count, Is.EqualTo(3));
            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void 有料版がなめらかに貼るに未対応でも_重ね貼りの編集が無ければ警告しない()
        {
            // 無料版だけ更新されて有料版が古いときの警告（WarnUnsupportedFeatures）は、重ね貼りの編集があるときだけ出す
            MakeAvatarWithEdit();
            bool warned = true;
            var errors = ErrorReport.CaptureErrors(() => warned = RecolorPass.WarnUnsupportedFeatures(_root, 0));
            Assert.That(warned, Is.False);
            Assert.That(errors, Is.Empty);

            warned = true;
            errors = ErrorReport.CaptureErrors(() => warned = RecolorPass.WarnUnsupportedFeatures(_root, RecolorBuildHook.FeatureDecalOverlay));
            Assert.That(warned, Is.False, "対応していれば見ない");
            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void 有料版が髪用に未対応なら_髪ツールの髪の編集で古いと警告する()
        {
            MakeAvatarWithEdit();
            AddHairUsingEditTexture();
            bool warned = false;
            var errors = ErrorReport.CaptureErrors(() =>
                warned = RecolorPass.WarnUnsupportedFeatures(_root, RecolorBuildHook.FeatureDecalOverlay, orderSupported: true));
            Assert.That(warned, Is.True);
            Assert.That(errors.Count, Is.EqualTo(1));
        }

        [Test]
        public void 髪ツールの宣言が無いか_有料版が髪用に対応していれば_髪の編集でも警告しない()
        {
            MakeAvatarWithEdit();
            AddHairUsingEditTexture();
            bool warned = true;
            var errors = ErrorReport.CaptureErrors(() =>
                warned = RecolorPass.WarnUnsupportedFeatures(_root, RecolorBuildHook.FeatureDecalOverlay, orderSupported: false));
            Assert.That(warned, Is.False, "宣言の無い髪ツールでは本体が髪も担当するので、古い有料版でもプレビューと一致する");
            Assert.That(errors, Is.Empty);

            warned = true;
            errors = ErrorReport.CaptureErrors(() => warned = RecolorPass.WarnUnsupportedFeatures(
                _root, RecolorBuildHook.FeatureDecalOverlay | RecolorBuildHook.FeatureChmHair, orderSupported: true));
            Assert.That(warned, Is.False);
            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void 統合ONの髪の編集では_髪用の非対応を警告しない()
        {
            MakeAvatarWithEdit();
            AddHairUsingEditTexture(merge: true);
            bool warned = true;
            var errors = ErrorReport.CaptureErrors(() =>
                warned = RecolorPass.WarnUnsupportedFeatures(_root, RecolorBuildHook.FeatureDecalOverlay, orderSupported: true));
            Assert.That(warned, Is.False, "統合ONの髪はどちらの入口も掛けないので、有料版の新旧で結果が変わらない");
            Assert.That(errors, Is.Empty);
        }
    }
}
