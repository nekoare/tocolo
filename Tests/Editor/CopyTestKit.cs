using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.Pipeline;
using Nekoare.ClickRecolor.Editor.SceneTool;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// コピー（書き出し→別アバターへ読み込み）のテストで使うアバター・メッシュ・テクスチャ資産を作り、後始末する。
    /// 移す元と移す先で同じメッシュ資産を共有させるため、メッシュは 1 回作って使い回す
    /// </summary>
    internal sealed class CopyTestKit
    {
        internal const string Folder = "Assets/__TocoloCopyTests";
        private const int Size = 16;

        private readonly List<Object> _cleanup = new List<Object>();
        private int _textureCount;

        internal T Track<T>(T obj) where T : Object
        {
            _cleanup.Add(obj);
            return obj;
        }

        internal void Dispose()
        {
            foreach (var o in _cleanup)
            {
                if (o is GameObject go)
                {
                    foreach (var c in go.GetComponentsInChildren<Component>(true)) if (c != null) Undo.ClearUndo(c);
                    foreach (var t in go.GetComponentsInChildren<Transform>(true)) Undo.ClearUndo(t.gameObject);
                }
                else if (o != null) Undo.ClearUndo(o);
            }
            foreach (var o in _cleanup) if (o != null && !AssetDatabase.Contains(o)) Object.DestroyImmediate(o);
            _cleanup.Clear();
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.DeleteAsset(CopiedImageStoreFolderForTests);
            MaskCache.ClearCache();
            CoverageMask.ClearCache();
            UvChartDetector.ClearCache();
        }

        /// <summary>テストが CopiedImageStore に画像を作るときの置き場所（Dispose で消す）</summary>
        internal static string CopiedImageStoreFolderForTests = "Assets/__TocoloCopyTests_Images";

        internal Texture2D MakeTextureAsset(Color color, string name = null)
        {
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "__TocoloCopyTests");
            var texture = new Texture2D(Size, Size);
            var pixels = new Color[Size * Size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            texture.SetPixels(pixels);
            texture.Apply();
            string path = $"{Folder}/{name ?? "Tex" + (_textureCount++)}.asset";
            AssetDatabase.CreateAsset(texture, path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>UV 全面の四角形 2 枚（三角形 4 つ）。name はメッシュ名</summary>
        internal Mesh MakeMesh(string name, int quads = 2)
        {
            var mesh = Track(new Mesh { name = name });
            var vertices = new List<Vector3>();
            var uv = new List<Vector2>();
            var triangles = new List<int>();
            for (int q = 0; q < quads; q++)
            {
                int b = vertices.Count;
                float x = q * 1.5f;
                vertices.Add(new Vector3(x, 0, 0)); vertices.Add(new Vector3(x + 1, 0, 0));
                vertices.Add(new Vector3(x + 1, 1, 0)); vertices.Add(new Vector3(x, 1, 0));
                float u0 = q / (float)quads, u1 = (q + 0.9f) / quads;
                uv.Add(new Vector2(u0, 0)); uv.Add(new Vector2(u1, 0)); uv.Add(new Vector2(u1, 1)); uv.Add(new Vector2(u0, 1));
                triangles.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        internal GameObject MakeRoot(string name) => Track(new GameObject(name));

        /// <summary>root の下の path（"A/B/Hair" のように / 区切り）に Renderer を置く。途中の GameObject は作る</summary>
        internal MeshRenderer AddRenderer(GameObject root, string path, Mesh mesh, Texture2D texture)
        {
            var parent = root.transform;
            var names = path.Split('/');
            for (int i = 0; i < names.Length - 1; i++)
            {
                var child = parent.Find(names[i]);
                if (child == null)
                {
                    child = new GameObject(names[i]).transform;
                    child.SetParent(parent, false);
                }
                parent = child;
            }
            var go = new GameObject(names[names.Length - 1]);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            var material = Track(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            renderer.sharedMaterials = new[] { material };
            return renderer;
        }

        /// <summary>renderer の三角形 triangle をクリックして作ったのと同じ形の、色を決めた編集</summary>
        internal static RecolorEdit MakeEdit(Renderer renderer, Texture2D texture, int triangle = 0, string name = "edit")
        {
            var edit = RecolorEdit.CreateNew();
            edit.name = name;
            edit.sourceTexture = texture;
            edit.seedRenderer = renderer;
            edit.seedSubmesh = 0;
            edit.seedTriangle = triangle;
            edit.seedUv = new Vector2(0.2f, 0.3f);
            edit.targetColor = new Color(0.2f, 0.6f, 0.9f);
            edit.hasTarget = true;
            edit.confirmed = true;
            edit.threshold = 0.33f;
            return edit;
        }
    }
}
