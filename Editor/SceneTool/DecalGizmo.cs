using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Masks;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Editor.SceneTool
{
    /// <summary>
    /// Scene で画像の箱の +Z 面に画像を半透明で描く（どこに・どの向きで貼られるか分かるように）。裏から見たときは Cull Back で描かれない。
    /// 四角形のメッシュとマテリアルは 1 つずつ作って使い回す（毎フレーム作ると GC とリークの元になる）
    /// </summary>
    internal static class DecalGizmo
    {
        /// <summary>画像が無いときの +Z 面の塗り（どこから投影するかだけ分かるように薄く）</summary>
        private static readonly Color EmptyFill = new Color(1f, 1f, 1f, 0.15f);
        private static readonly Color EmptyOutline = new Color(1f, 1f, 1f, 0.4f);

        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int AlphaId = Shader.PropertyToID("_Alpha");

        private static Mesh s_mesh;
        private static Material s_material;
        private static bool s_hooked;
        private static readonly Vector3[] s_vertices = new Vector3[4];
        private static readonly Vector2[] s_uvs = new Vector2[4];

        /// <summary>
        /// Handles.matrix（箱のローカル → ワールド。呼び出し側の DrawingScope で Handles.matrix × TRS にしておく）の +Z 面に image を描く。
        /// size は箱の大きさ（符号付き）。keepAspect なら DecalLayerBuilder.FitScale と同じ比率で縮める。image が null なら薄い白の面だけ。
        /// Repaint のときだけ呼ぶこと
        /// </summary>
        internal static void DrawImage(Vector3 size, Texture2D image, bool keepAspect, float alpha)
        {
            if (Event.current != null && Event.current.type != EventType.Repaint) return;

            float fx = Mathf.Abs(size.x) * 0.5f;
            float fy = Mathf.Abs(size.y) * 0.5f;
            float fz = Mathf.Abs(size.z) * 0.5f;

            if (image == null)
            {
                // Handles.matrix（箱のローカル）は DrawSolidRectangleWithOutline が掛けるので、局所座標の頂点をそのまま渡す
                var corners = new[]
                {
                    new Vector3(-fx, -fy, fz),
                    new Vector3(fx, -fy, fz),
                    new Vector3(fx, fy, fz),
                    new Vector3(-fx, fy, fz),
                };
                Handles.DrawSolidRectangleWithOutline(corners, EmptyFill, EmptyOutline);
                return;
            }

            var material = GetMaterial();
            if (material == null) return;

            // 比率を保つときは、シェーダーで UV を広げる代わりに四角形そのものを縮める（同じ見た目。余白に何も描かない）
            var fit = DecalLayerBuilder.FitScale(image, size, keepAspect);
            fx /= fit.x;
            fy /= fit.y;

            // 頂点の並びは 0=(−,−) 1=(+,−) 2=(−,+) 3=(+,+)、三角形 0,1,2 / 2,1,3 で +Z 側から見て表
            s_vertices[0] = new Vector3(-fx, -fy, fz);
            s_vertices[1] = new Vector3(fx, -fy, fz);
            s_vertices[2] = new Vector3(-fx, fy, fz);
            s_vertices[3] = new Vector3(fx, fy, fz);

            // DecalLayer.shader の u = 0.5 − x / size.x、v = y / size.y + 0.5 と同じ向き:
            // 正面（+Z 側）から見て正しく見えるよう x = −fx に u = 1。大きさが負の軸は反転する
            float uLeft = size.x < 0f ? 0f : 1f;
            float vBottom = size.y < 0f ? 1f : 0f;
            s_uvs[0] = new Vector2(uLeft, vBottom);
            s_uvs[1] = new Vector2(1f - uLeft, vBottom);
            s_uvs[2] = new Vector2(uLeft, 1f - vBottom);
            s_uvs[3] = new Vector2(1f - uLeft, 1f - vBottom);

            var mesh = GetMesh();
            mesh.vertices = s_vertices;
            mesh.uv = s_uvs;

            material.SetTexture(MainTexId, image);
            material.SetFloat(AlphaId, alpha);
            if (!material.SetPass(0)) return;
            Graphics.DrawMeshNow(mesh, Handles.matrix);
        }

        private static Mesh GetMesh()
        {
            if (s_mesh != null) return s_mesh;
            HookReload();
            s_mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, name = "TocoloDecalGizmo" };
            s_mesh.vertices = s_vertices;
            s_mesh.uv = s_uvs;
            s_mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            s_mesh.MarkDynamic();
            return s_mesh;
        }

        /// <summary>マテリアル。シェーダーが読めなければ null（警告は ShaderAssets が 1 回だけ出す）</summary>
        private static Material GetMaterial()
        {
            if (s_material != null) return s_material;
            var shader = ShaderAssets.LoadShader(ShaderAssets.DecalGizmoGuid);
            if (shader == null) return null;
            HookReload();
            s_material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return s_material;
        }

        /// <summary>ドメインリロードでは HideAndDontSave のメッシュ・マテリアルが捨てられないので、リロード前に片付ける（ScenePicker と同じ流儀）</summary>
        private static void HookReload()
        {
            if (s_hooked) return;
            s_hooked = true;
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
        }

        private static void Dispose()
        {
            if (s_mesh != null) Object.DestroyImmediate(s_mesh);
            if (s_material != null) Object.DestroyImmediate(s_material);
            s_mesh = null;
            s_material = null;
        }
    }
}
