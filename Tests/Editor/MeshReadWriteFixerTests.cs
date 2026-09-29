using Nekoare.ClickRecolor.Editor.Picking;
using NUnit.Framework;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>Read/Write 無効のメッシュの判定（MeshReadWriteFixer）</summary>
    public class MeshReadWriteFixerTests
    {
        private Mesh _mesh;
        private GameObject _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            if (_mesh != null) Object.DestroyImmediate(_mesh);
        }

        private MeshRenderer MakeRenderer(bool readable)
        {
            _mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 },
            };
            // 読み取り不可にする（CPU 側のデータを捨てる）
            if (!readable) _mesh.UploadMeshData(true);
            _go = new GameObject("Item");
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            return _go.AddComponent<MeshRenderer>();
        }

        [Test]
        public void 読み取り可能なメッシュは対象外()
        {
            var renderer = MakeRenderer(readable: true);
            Assert.That(MeshReadWriteFixer.IsUnreadable(renderer), Is.False);
            Assert.That(MeshReadWriteFixer.GetFixablePath(_mesh), Is.Null);
        }

        [Test]
        public void 読み取り不可のメッシュは検出され_アセットでなければ直せない()
        {
            var renderer = MakeRenderer(readable: false);
            Assert.That(MeshReadWriteFixer.IsUnreadable(renderer), Is.True);
            Assert.That(MeshReadWriteFixer.GetFixablePath(_mesh), Is.Null, "プロジェクトのアセットでないメッシュは直せない");
            Assert.That(MeshReadWriteFixer.Fix(new[] { _mesh }, confirm: false), Is.False);
        }

        [Test]
        public void メッシュの無い_Renderer_は対象外()
        {
            _go = new GameObject("Empty");
            _go.AddComponent<MeshFilter>();
            var renderer = _go.AddComponent<MeshRenderer>();
            Assert.That(MeshReadWriteFixer.IsUnreadable(renderer), Is.False);
        }
    }
}
