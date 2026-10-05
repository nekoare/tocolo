using System.Collections.Generic;
using Nekoare.ClickRecolor.Editor.Colors;
using Nekoare.ClickRecolor.Editor.Masks;
using Nekoare.ClickRecolor.Editor.NDMF;
using Nekoare.ClickRecolor.Editor.Picking;
using Nekoare.ClickRecolor.Editor.Pipeline;
using Nekoare.ClickRecolor.Editor.SceneTool;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// クリックからの編集作成（RecolorSceneTool.CreateEditFromHit）と、
    /// パネルの色決定（ToolPanelOverlay.ApplyTargetColor）、再クリックでの既存編集の検索（RecolorSceneTool.FindEditContaining）、
    /// 既存を選ぶか新規かの判定（RecolorSceneTool.ResolveEdit）、Ctrl＋クリックの種の追加／外し（RecolorSceneTool.ResolveSeedToggle）のテスト
    /// </summary>
    public class EditCreationTests
    {
        // 編集の対象は「テクスチャ資産」に限るので、テスト中だけ一時的な資産を作る
        private const string TextureAssetPath = "Assets/__ClickRecolorEditCreationTests.asset";
        // 再クリックのテスト用（島マスクを画素単位で作るので 64×64）
        private const string ChartTextureAssetPath = "Assets/__ClickRecolorEditCreationTests_Chart.asset";
        private const int ChartSize = 64;

        private readonly List<Object> _cleanup = new List<Object>();
        private Texture2D _texture;

        [SetUp]
        public void SetUp()
        {
            ToolSession.Clear();
            // 既存のテストは島モードで作られる前提（プリセット別のテストは各テストで上書きする）
            ToolSession.Preset = RangePreset.Island;
            // 別テクスチャの島を足すテストのため（既定は OFF）
            ToolSession.CrossTextureEnabled = true;
            AssetDatabase.CreateAsset(new Texture2D(4, 4), TextureAssetPath);
            _texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TextureAssetPath);
            // 前のテストや利用者の操作と Undo グループを分ける
            Undo.IncrementCurrentGroup();
        }

        [TearDown]
        public void TearDown()
        {
            ToolSession.Clear();
            // Undo.RecordObject / AddComponent の記録を消してから破棄する（残すと Ctrl+Z で空のオブジェクトが復活する）
            foreach (var o in _cleanup)
            {
                if (o is GameObject go)
                {
                    foreach (var c in go.GetComponentsInChildren<Component>(true)) if (c != null) Undo.ClearUndo(c);
                    foreach (var t in go.GetComponentsInChildren<Transform>(true)) Undo.ClearUndo(t.gameObject);
                }
                else if (o != null) Undo.ClearUndo(o);
            }
            foreach (var o in _cleanup) if (o != null) Object.DestroyImmediate(o);
            _cleanup.Clear();
            AssetDatabase.DeleteAsset(TextureAssetPath);
            AssetDatabase.DeleteAsset(ChartTextureAssetPath);
            // 再クリックのテストで作ったマスク・被覆・チャート表を残さない
            MaskCache.ClearCache();
            CoverageMask.ClearCache();
            UvChartDetector.ClearCache();
        }

        private T Track<T>(T obj) where T : Object
        {
            _cleanup.Add(obj);
            return obj;
        }

        private ClickRecolor MakeComponent()
        {
            var go = Track(new GameObject("Avatar"));
            return go.AddComponent<ClickRecolor>();
        }

        private PickHit MakeHit(Texture2D texture, int submesh = 1, int triangle = 5, Vector2? uv = null)
        {
            var go = Track(new GameObject("Body"));
            var renderer = go.AddComponent<MeshRenderer>();
            return new PickHit
            {
                renderer = renderer,
                subMeshIndex = submesh,
                materialSlot = submesh,
                triangleIndex = triangle,
                uv0 = new Vector2(0.1f, 0.2f),
                uv = uv ?? new Vector2(0.3f, 0.4f),
                texel = new Vector2Int(1, 1),
                hasMainTexture = texture != null,
                mainTexture = new MainTextureInfo { propertyName = "_MainTex", texture = texture, scale = Vector2.one, offset = Vector2.zero },
            };
        }

        [Test]
        public void クリックで編集が追加されヒットの内容が入る()
        {
            var component = MakeComponent();
            var hit = MakeHit(_texture, submesh: 1, triangle: 5, uv: new Vector2(0.3f, 0.4f));

            var edit = RecolorSceneTool.CreateEditFromHit(component, hit, Color.red);

            Assert.That(edit, Is.Not.Null);
            Assert.That(component.edits.Count, Is.EqualTo(1));
            Assert.That(component.edits[0], Is.SameAs(edit));
            Assert.That(edit.sourceTexture, Is.SameAs(_texture));
            Assert.That(edit.seedRenderer, Is.SameAs(hit.renderer));
            Assert.That(edit.seedSubmesh, Is.EqualTo(1));
            Assert.That(edit.seedTriangle, Is.EqualTo(5));
            // Tiling/Offset 適用後の UV（uv0 ではない）
            Assert.That(edit.seedUv, Is.EqualTo(new Vector2(0.3f, 0.4f)));
            Assert.That(edit.seedColor, Is.EqualTo(Color.red));
            Assert.That(edit.mode, Is.EqualTo(SelectionMode.Island));
            Assert.That(edit.padding, Is.EqualTo(8));
            Assert.That(edit.hasTarget, Is.False);
            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(edit.id));
        }

        [Test]
        public void 同じ色のプリセットでは色モードのつながった範囲で作られる()
        {
            ToolSession.Preset = RangePreset.SameColor;
            var component = MakeComponent();

            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), Color.red);

            Assert.That(edit.mode, Is.EqualTo(SelectionMode.Color));
            Assert.That(edit.threshold, Is.EqualTo(0.08f));
            Assert.That(edit.feather, Is.EqualTo(0.05f));
            Assert.That(edit.scope, Is.EqualTo(ColorScope.Contiguous));
        }

        [Test]
        public void 似た色のプリセットでは広めのしきい値で作られる()
        {
            ToolSession.Preset = RangePreset.SimilarColor;
            var component = MakeComponent();

            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), Color.red);

            Assert.That(edit.mode, Is.EqualTo(SelectionMode.Color));
            Assert.That(edit.threshold, Is.EqualTo(0.30f));
            Assert.That(edit.feather, Is.EqualTo(0.15f));
            Assert.That(edit.scope, Is.EqualTo(ColorScope.Contiguous));
        }

        [Test]
        public void 見本色が無ければ種色は灰色()
        {
            var component = MakeComponent();

            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), null);

            Assert.That(edit.seedColor, Is.EqualTo(Color.gray));
        }

        [Test]
        public void 直前の色未設定の編集は次のクリックで消える()
        {
            var component = MakeComponent();
            var first = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), null);

            var second = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), null);

            Assert.That(component.edits.Count, Is.EqualTo(1));
            Assert.That(component.FindEdit(first.id), Is.Null);
            Assert.That(component.edits[0], Is.SameAs(second));
            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(second.id));
        }

        [Test]
        public void 色を決めた直前の編集は次のクリックでも残る()
        {
            var component = MakeComponent();
            var first = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), null);
            ToolPanelOverlay.ApplyTargetColor(component, first, Color.blue);

            var second = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), null);

            Assert.That(component.edits.Count, Is.EqualTo(2));
            Assert.That(component.FindEdit(first.id), Is.Not.Null);
            Assert.That(component.FindEdit(second.id), Is.Not.Null);
        }

        [Test]
        public void Undo_で編集の追加と直前の編集の削除が戻る()
        {
            var component = MakeComponent();
            var first = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), null);
            string firstId = first.id;
            // RecordObject の記録は確定させてからグループを分ける（未確定のままだと次の操作と同じ記録にまとまる）
            Undo.FlushUndoRecordObjects();
            Undo.IncrementCurrentGroup();

            var second = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), null);
            string secondId = second.id;
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();

            // Undo でリストは入れ替わるので、参照ではなく id で引き直す
            Assert.That(component.edits.Count, Is.EqualTo(1));
            Assert.That(component.FindEdit(firstId), Is.Not.Null);
            Assert.That(component.FindEdit(secondId), Is.Null);
        }

        [Test]
        public void テクスチャが無い_または資産でなければ編集を作らない()
        {
            var component = MakeComponent();
            var runtimeTexture = Track(new Texture2D(4, 4));

            var withoutTexture = RecolorSceneTool.CreateEditFromHit(component, MakeHit(null), null);
            var withRuntimeTexture = RecolorSceneTool.CreateEditFromHit(component, MakeHit(runtimeTexture), null);

            Assert.That(withoutTexture, Is.Null);
            Assert.That(withRuntimeTexture, Is.Null);
            Assert.That(component.edits, Is.Empty);
            Assert.That(ToolSession.CurrentEditId, Is.Null);
        }

        [Test]
        public void 色の初回決定でだけ暗部の明るさを自動調整し_手動値は上書きしない()
        {
            var component = MakeComponent();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), null);

            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.white);

            Assert.That(edit.hasTarget, Is.True);
            Assert.That(edit.targetColor, Is.EqualTo(Color.white));
            Assert.That(edit.darkEndRatio, Is.EqualTo(DarkEndAutoAdjust.Compute(Color.white)));

            // 手動で動かした値は、以後の色変更で上書きしない
            edit.darkEndRatio = 0.2f;
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.red);

            Assert.That(edit.targetColor, Is.EqualTo(Color.red));
            Assert.That(edit.darkEndRatio, Is.EqualTo(0.2f));
        }

        [Test]
        public void 元の色に戻すと色未設定になり_暗部の明るさは保たれる()
        {
            var component = MakeComponent();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), Color.green);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.red);
            edit.darkEndRatio = 0.2f;

            ToolPanelOverlay.ResetToOriginal(component, edit);

            Assert.That(edit.hasTarget, Is.False);
            Assert.That(edit.targetColor, Is.EqualTo(Color.green));
            Assert.That(edit.darkEndRatio, Is.EqualTo(0.2f));
            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(edit.id), "現在の編集（ハイライト）はそのまま");

            // 次に色を選んだときは、初回と同じく暗部の明るさを自動調整する
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.white);

            Assert.That(edit.hasTarget, Is.True);
            Assert.That(edit.darkEndRatio, Is.EqualTo(DarkEndAutoAdjust.Compute(Color.white)));
        }

        // ── 再クリック（FindEditContaining）──

        private static void RequireMaskGpu()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.R8))
            {
                Assert.Ignore("この環境はcompute shader（R8への書き込み）に対応していません");
            }
            Assert.That(UvRasterizer.IsAvailable, Is.True, "Rasterize.computeが読み込めません（.metaのGUIDを確認）");
            Assert.That(Morphology.IsAvailable, Is.True, "Morphology.computeが読み込めません（.metaのGUIDを確認）");
        }

        private static Vector2 ChartPx(float x, float y) => new Vector2(x / ChartSize, y / ChartSize);

        /// <summary>
        /// UV に 2 つの長方形チャートを置いたメッシュ:
        ///   チャート A: x = 4..28 px, y = 4..30 px（三角形 0 = 右下, 1 = 左上。対角線は (4,4)-(28,30)）
        ///   チャート B: x = 32..60 px, y = 4..60 px（三角形 2, 3）
        /// A は上下非対称（下半分だけ）にして、マスクの y が反転していれば A の点が選択外・A の上の空きが選択内になるようにする
        /// </summary>
        private Mesh MakeTwoChartMesh()
        {
            var mesh = Track(new Mesh());
            var uv = new[]
            {
                ChartPx(4, 4), ChartPx(28, 4), ChartPx(28, 30), ChartPx(4, 30),
                ChartPx(32, 4), ChartPx(60, 4), ChartPx(60, 60), ChartPx(32, 60),
            };
            var vertices = new Vector3[uv.Length];
            for (int i = 0; i < uv.Length; i++) vertices[i] = uv[i];
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 };
            return mesh;
        }

        /// <summary>
        /// ルート（ClickRecolor 付き）の下に、2 チャートのメッシュ＋そのテクスチャをメインに持つマテリアルの Renderer を置く。
        /// pixels（sRGB、ChartSize×ChartSize）を渡せばテクスチャの中身にする
        /// </summary>
        private (ClickRecolor component, MeshRenderer renderer, Texture2D texture) MakeChartAvatar(Color[] pixels = null)
        {
            var source = pixels != null
                ? new Texture2D(ChartSize, ChartSize, TextureFormat.RGBA32, false)
                : new Texture2D(ChartSize, ChartSize);
            if (pixels != null)
            {
                source.SetPixels(pixels);
                source.Apply();
            }
            AssetDatabase.CreateAsset(source, ChartTextureAssetPath);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(ChartTextureAssetPath);

            var root = Track(new GameObject("Avatar"));
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform);
            body.AddComponent<MeshFilter>().sharedMesh = MakeTwoChartMesh();
            var renderer = body.AddComponent<MeshRenderer>();
            var material = Track(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            renderer.sharedMaterials = new[] { material };

            var component = root.AddComponent<ClickRecolor>();
            return (component, renderer, texture);
        }

        private static PickHit MakeChartHit(MeshRenderer renderer, Texture2D texture, int triangle, Vector2 uv)
        {
            return new PickHit
            {
                renderer = renderer,
                subMeshIndex = 0,
                materialSlot = 0,
                triangleIndex = triangle,
                uv0 = uv,
                uv = uv,
                texel = new Vector2Int(Mathf.FloorToInt(uv.x * texture.width), Mathf.FloorToInt(uv.y * texture.height)),
                hasMainTexture = true,
                mainTexture = new MainTextureInfo { propertyName = "_MainTex", texture = texture, scale = Vector2.one, offset = Vector2.zero },
                material = renderer.sharedMaterial,
            };
        }

        [Test]
        public void 色を決めた島の別の点をクリックすると既存の編集が返る()
        {
            RequireMaskGpu();
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.blue);

            // 同じチャート A の、別の三角形の点（y が反転していれば A は y = 34..60 になり、パディング 8 を足しても届かない）
            var found = RecolorSceneTool.FindEditContaining(component, MakeChartHit(renderer, texture, 1, ChartPx(8, 20)));

            Assert.That(found, Is.SameAs(edit));
        }

        [Test]
        public void 別の島をクリックすると既存の編集は返らない()
        {
            RequireMaskGpu();
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.blue);

            // チャート B（パディングは被覆の外にしか広がらないので、B の内側は選択外）
            var found = RecolorSceneTool.FindEditContaining(component, MakeChartHit(renderer, texture, 2, ChartPx(46, 32)));
            // A の上の空き（A の縁 y = 30 からパディング 8 より離れている。y が反転していれば A の中になる）
            var aboveA = RecolorSceneTool.FindEditContaining(component, MakeChartHit(renderer, texture, 0, ChartPx(16, 50)));

            Assert.That(found, Is.Null);
            Assert.That(aboveA, Is.Null);
        }

        [Test]
        public void テクスチャの残りを選択すると別の島も範囲に入り_モードを選び直すと戻る()
        {
            RequireMaskGpu();
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.blue);
            Assert.That(RecolorSceneTool.FindEditContaining(component, MakeChartHit(renderer, texture, 2, ChartPx(46, 32))), Is.Null,
                "前提: チャート B は範囲の外");

            edit.wholeTexture = true;
            Assert.That(RecolorSceneTool.FindEditContaining(component, MakeChartHit(renderer, texture, 2, ChartPx(46, 32))), Is.SameAs(edit),
                "テクスチャ全体なら別のチャートも範囲");
            // チャートの無い所（A の上の空き。はみ出し幅より離れている）は入らない
            Assert.That(RecolorSceneTool.FindEditContaining(component, MakeChartHit(renderer, texture, 0, ChartPx(16, 50))), Is.Null);

            RecolorSceneTool.ApplyPreset(edit, RangePreset.Island);
            Assert.That(edit.wholeTexture, Is.False, "モードを選び直すと戻る");
        }

        [Test]
        public void 色未設定の編集は再クリックで返らない()
        {
            // マスクを作る前に弾くので GPU は要らない
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            Assert.That(edit.hasTarget, Is.False, "前提: 色未設定");

            var found = RecolorSceneTool.FindEditContaining(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)));

            Assert.That(found, Is.Null);
        }

        [Test]
        public void 重なる編集は後ろのものが返る()
        {
            RequireMaskGpu();
            var (component, renderer, texture) = MakeChartAvatar();
            var first = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            ToolPanelOverlay.ApplyTargetColor(component, first, Color.blue);
            var second = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 1, ChartPx(8, 20)), null);
            ToolPanelOverlay.ApplyTargetColor(component, second, Color.red);

            var found = RecolorSceneTool.FindEditContaining(component, MakeChartHit(renderer, texture, 0, ChartPx(16, 12)));

            Assert.That(found, Is.SameAs(second));
        }

        /// <summary>
        /// チャート A（x = 4..28 px, y = 4..30 px）を囲む画像入りの編集にする。画像は u &lt; 0.5 が不透明、u ≥ 0.5 が透明。
        /// 投影の u は x と逆向き（u = 0.5 − x / size.x）なので、チャート A の右側（x &gt; 16 px）が不透明、左側が透明の所に写る
        /// </summary>
        private RecolorEdit MakeDecalEdit(ClickRecolor component, MeshRenderer renderer, Texture2D texture)
        {
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            var image = Track(new Texture2D(8, 8, TextureFormat.RGBA32, false));
            var pixels = new Color[64];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++) pixels[y * 8 + x] = x < 4 ? Color.red : Color.clear;
            }
            image.SetPixels(pixels);
            image.Apply();
            edit.decalEnabled = true;
            edit.decalTexture = image;
            edit.decalKeepAspect = false;
            edit.decalBoxPosition = new Vector3(16f / ChartSize, 17f / ChartSize, 0f);
            edit.decalBoxRotation = Quaternion.identity;
            edit.decalBoxSize = new Vector3(24f / ChartSize, 26f / ChartSize, 0.2f);
            edit.confirmed = true;
            return edit;
        }

        /// <summary>MakeChartHit に、ルート原点のメッシュ上の位置（= UV）と箱の正面（+Z）を向いた法線を足す</summary>
        private static PickHit MakeChartHitAt(MeshRenderer renderer, Texture2D texture, int triangle, Vector2 uv)
        {
            var hit = MakeChartHit(renderer, texture, triangle, uv);
            hit.worldPosition = new Vector3(uv.x, uv.y, 0f);
            hit.worldNormal = Vector3.forward;
            return hit;
        }

        [Test]
        public void 画像の上をクリックすると後ろのふつうの編集より画像の編集が返る()
        {
            RequireMaskGpu();
            var (component, renderer, texture) = MakeChartAvatar();
            var decal = MakeDecalEdit(component, renderer, texture);
            var plain = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 1, ChartPx(8, 20)), null);
            ToolPanelOverlay.ApplyTargetColor(component, plain, Color.blue);
            Assert.That(component.edits.IndexOf(plain), Is.GreaterThan(component.edits.IndexOf(decal)), "前提: ふつうの編集が後ろ");

            var found = RecolorSceneTool.FindEditContaining(component, MakeChartHitAt(renderer, texture, 0, ChartPx(24, 17)));

            Assert.That(found, Is.SameAs(decal));
        }

        [Test]
        public void 画像の透明な所をクリックするとふつうの編集が返り_無ければ新しく作れる()
        {
            RequireMaskGpu();
            var (component, renderer, texture) = MakeChartAvatar();
            MakeDecalEdit(component, renderer, texture);

            // 範囲（チャート A）の中だが画像の透明な所: 画像の編集は選ばない（呼び出し側が新しい編集を作る）
            Assert.That(RecolorSceneTool.FindEditContaining(component, MakeChartHitAt(renderer, texture, 1, ChartPx(8, 17))), Is.Null);

            var plain = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 1, ChartPx(8, 20)), null);
            ToolPanelOverlay.ApplyTargetColor(component, plain, Color.blue);
            Assert.That(RecolorSceneTool.FindEditContaining(component, MakeChartHitAt(renderer, texture, 1, ChartPx(8, 17))), Is.SameAs(plain));
        }

        [Test]
        public void 箱の裏を向いた面は画像の上とみなさない()
        {
            RequireMaskGpu();
            var (component, renderer, texture) = MakeChartAvatar();
            var decal = MakeDecalEdit(component, renderer, texture);
            var hit = MakeChartHitAt(renderer, texture, 0, ChartPx(24, 17));
            Assert.That(RecolorSceneTool.IsOnDecalImage(component.transform, decal, hit), Is.True, "前提: 正面なら画像の上");
            hit.worldNormal = Vector3.back;

            Assert.That(RecolorSceneTool.IsOnDecalImage(component.transform, decal, hit), Is.False);
        }

        // ── 既存を選ぶか新規か（ResolveEdit）──

        [Test]
        public void forceNew_なら既存の編集の範囲をクリックしても新しい編集を作る()
        {
            RequireMaskGpu();
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.blue);
            var hit = MakeChartHit(renderer, texture, 1, ChartPx(8, 20));

            // 前提: forceNew = false なら同じ点で既存の編集が選ばれる（新しい編集は作らない）
            var resolved = RecolorSceneTool.ResolveEdit(component, hit, null, forceNew: false);
            Assert.That(resolved, Is.SameAs(edit), "前提: 既存の編集の範囲");
            Assert.That(component.edits.Count, Is.EqualTo(1));

            var created = RecolorSceneTool.ResolveEdit(component, hit, null, forceNew: true);

            Assert.That(created, Is.Not.Null);
            Assert.That(created, Is.Not.SameAs(edit));
            Assert.That(component.edits.Count, Is.EqualTo(2));
            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(created.id));
            // 色を決めた既存の編集は残る
            Assert.That(component.FindEdit(edit.id), Is.Not.Null);
        }

        // ── Ctrl＋クリックの種の追加／外し（ResolveSeedToggle）──
        // 島の判定は UV チャート表（CPU）だけで行うので、マスクを作らないテストは GPU が要らない

        [Test]
        public void Ctrlクリックで別の島が追加の種として足される()
        {
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);

            var result = RecolorSceneTool.ResolveSeedToggle(component, edit, MakeChartHit(renderer, texture, 2, ChartPx(46, 32)));

            Assert.That(result, Is.EqualTo(SeedToggleResult.Added));
            Assert.That(edit.extraSeeds.Count, Is.EqualTo(1));
            Assert.That(edit.extraSeeds[0].renderer, Is.SameAs(renderer));
            Assert.That(edit.extraSeeds[0].submesh, Is.EqualTo(0));
            Assert.That(edit.extraSeeds[0].triangle, Is.EqualTo(2));
            Assert.That(edit.extraSeeds[0].uv, Is.EqualTo(ChartPx(46, 32)));
            // 主種はそのまま
            Assert.That(edit.seedTriangle, Is.EqualTo(0));
            Assert.That(component.edits.Count, Is.EqualTo(1));
        }

        [Test]
        public void 追加した島を再度Ctrlクリックすると外れる()
        {
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            RecolorSceneTool.ResolveSeedToggle(component, edit, MakeChartHit(renderer, texture, 2, ChartPx(46, 32)));

            // 同じチャート B の別の三角形
            var result = RecolorSceneTool.ResolveSeedToggle(component, edit, MakeChartHit(renderer, texture, 3, ChartPx(36, 56)));

            Assert.That(result, Is.EqualTo(SeedToggleResult.Removed));
            Assert.That(edit.extraSeeds, Is.Empty);
            Assert.That(edit.seedTriangle, Is.EqualTo(0));
            Assert.That(component.edits.Count, Is.EqualTo(1));
        }

        [Test]
        public void 主種の島を外すと追加の種の先頭が主種になる()
        {
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            RecolorSceneTool.ResolveSeedToggle(component, edit, MakeChartHit(renderer, texture, 2, ChartPx(46, 32)));

            // 主種のチャート A の別の三角形
            var result = RecolorSceneTool.ResolveSeedToggle(component, edit, MakeChartHit(renderer, texture, 1, ChartPx(8, 20)));

            Assert.That(result, Is.EqualTo(SeedToggleResult.Removed));
            Assert.That(component.FindEdit(edit.id), Is.SameAs(edit));
            Assert.That(edit.seedRenderer, Is.SameAs(renderer));
            Assert.That(edit.seedSubmesh, Is.EqualTo(0));
            Assert.That(edit.seedTriangle, Is.EqualTo(2));
            Assert.That(edit.seedUv, Is.EqualTo(ChartPx(46, 32)));
            Assert.That(edit.extraSeeds, Is.Empty);
        }

        [Test]
        public void 最後の種を外すと色を決めた編集でも削除される()
        {
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.blue);

            var result = RecolorSceneTool.ResolveSeedToggle(component, edit, MakeChartHit(renderer, texture, 1, ChartPx(8, 20)));

            Assert.That(result, Is.EqualTo(SeedToggleResult.EditRemoved));
            Assert.That(component.edits, Is.Empty);
            Assert.That(ToolSession.CurrentEditId, Is.Null);
        }

        [Test]
        public void アバター全体の連結で別のテクスチャをCtrlクリックしても変化せず一時メッセージが出る()
        {
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            // 「アバター全体」の連結（共有の種色あり）。島の連結なら別のテクスチャの島はメンバーとして足される（GroupEditTests）
            edit.groupId = "group";
            edit.hasSeedOklab = true;

            var result = RecolorSceneTool.ResolveSeedToggle(component, edit, MakeHit(_texture));

            Assert.That(result, Is.EqualTo(SeedToggleResult.OtherTexture));
            Assert.That(edit.extraSeeds, Is.Empty);
            Assert.That(edit.seedTriangle, Is.EqualTo(0));
            Assert.That(component.edits.Count, Is.EqualTo(1));
            Assert.That(ToolSession.TransientNotice, Is.EqualTo("Scene:Panel:CtrlOtherTexture"));
        }

        [Test]
        public void アバター全体の連結の編集にはCtrlクリックで種を足さない()
        {
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            edit.groupId = "group";
            edit.hasSeedOklab = true;

            var result = RecolorSceneTool.ResolveSeedToggle(component, edit, MakeChartHit(renderer, texture, 2, ChartPx(46, 32)));

            Assert.That(result, Is.EqualTo(SeedToggleResult.Grouped));
            Assert.That(edit.extraSeeds, Is.Empty);
            Assert.That(ToolSession.TransientNotice, Is.EqualTo("Scene:Panel:CtrlGroupNotice"));
        }

        // ── Ctrl＋ドラッグの矩形で島をまとめて追加（ApplyRectSeeds）──

        private static RectIsland MakeRectIsland(Renderer renderer, Texture2D texture, int triangle, Vector2 uv)
        {
            return new RectIsland { renderer = renderer, submesh = 0, triangle = triangle, chartId = -1, uv0 = uv, uv = uv, texture = texture };
        }

        [Test]
        public void 矩形の島は新しい島だけ追加され_別テクスチャは連結のメンバーになる()
        {
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            // 同じテクスチャを使う 2 つ目の Renderer（別メッシュなので、チャート A も別の島）
            var second = new GameObject("Body2");
            second.transform.SetParent(component.transform);
            second.AddComponent<MeshFilter>().sharedMesh = MakeTwoChartMesh();
            var secondRenderer = second.AddComponent<MeshRenderer>();
            secondRenderer.sharedMaterials = renderer.sharedMaterials;
            var otherHit = MakeHit(_texture);

            var islands = new List<RectIsland>
            {
                MakeRectIsland(renderer, texture, 1, ChartPx(8, 20)),        // 主種と同じチャート A（既に含まれる）
                MakeRectIsland(renderer, texture, 2, ChartPx(46, 32)),       // チャート B（追加）
                MakeRectIsland(secondRenderer, texture, 0, ChartPx(20, 10)), // 2 つ目のメッシュのチャート A（追加）
                MakeRectIsland(renderer, texture, 3, ChartPx(36, 56)),       // チャート B の別の三角形（直前に足したので増えない）
                MakeRectIsland(otherHit.renderer, _texture, 5, ChartPx(8, 8)), // 別のテクスチャ（連結のメンバーを作って主種にする）
            };

            var result = RecolorSceneTool.ApplyRectSeeds(component, edit, islands);

            Assert.That(result.rejected, Is.False);
            Assert.That(result.added, Is.EqualTo(3));
            Assert.That(result.membersCreated, Is.EqualTo(1));
            Assert.That(edit.extraSeeds.Count, Is.EqualTo(2));
            Assert.That(edit.extraSeeds[0].renderer, Is.SameAs(renderer));
            Assert.That(edit.extraSeeds[0].triangle, Is.EqualTo(2));
            Assert.That(edit.extraSeeds[0].uv, Is.EqualTo(ChartPx(46, 32)));
            Assert.That(edit.extraSeeds[1].renderer, Is.SameAs(secondRenderer));
            Assert.That(edit.extraSeeds[1].triangle, Is.EqualTo(0));
            // 主種はそのまま（外さない）
            Assert.That(edit.seedTriangle, Is.EqualTo(0));
            // 別のテクスチャの島は、同じ連結のメンバー（主種＝その島）になる
            Assert.That(component.edits.Count, Is.EqualTo(2));
            var member = EditGroups.FindMemberForTexture(component, edit, _texture);
            Assert.That(member, Is.Not.Null);
            Assert.That(member, Is.Not.SameAs(edit));
            Assert.That(edit.groupId, Is.Not.Null.And.Not.Empty);
            Assert.That(member.groupId, Is.EqualTo(edit.groupId));
            Assert.That(member.seedRenderer, Is.SameAs(otherHit.renderer));
            Assert.That(member.seedTriangle, Is.EqualTo(5));
            Assert.That(member.extraSeeds, Is.Empty);
            Assert.That(ToolSession.TransientNotice, Is.Null);
        }

        [Test]
        public void 色モードの編集には矩形で島を追加しない()
        {
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            edit.mode = SelectionMode.Color;

            var result = RecolorSceneTool.ApplyRectSeeds(component, edit,
                new List<RectIsland> { MakeRectIsland(renderer, texture, 2, ChartPx(46, 32)) });

            Assert.That(result.rejected, Is.True);
            Assert.That(result.added, Is.EqualTo(0));
            Assert.That(edit.extraSeeds, Is.Empty);
            Assert.That(ToolSession.TransientNotice, Is.EqualTo("Scene:Panel:RectColorModeNotice"));
        }

        [Test]
        public void 種の情報から直接編集を作れる()
        {
            var (component, renderer, texture) = MakeChartAvatar();

            var edit = RecolorSceneTool.CreateEditFromSeed(component, renderer, 0, 2, ChartPx(46, 32), texture, Color.red);

            Assert.That(edit, Is.Not.Null);
            Assert.That(component.edits.Count, Is.EqualTo(1));
            Assert.That(edit.sourceTexture, Is.SameAs(texture));
            Assert.That(edit.seedRenderer, Is.SameAs(renderer));
            Assert.That(edit.seedSubmesh, Is.EqualTo(0));
            Assert.That(edit.seedTriangle, Is.EqualTo(2));
            Assert.That(edit.seedUv, Is.EqualTo(ChartPx(46, 32)));
            Assert.That(edit.seedColor, Is.EqualTo(Color.red));
            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(edit.id));
        }

        [Test]
        public void 二つの島を種にした編集のマスクは両方の島が選ばれる()
        {
            RequireMaskGpu();
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            RecolorSceneTool.ResolveSeedToggle(component, edit, MakeChartHit(renderer, texture, 2, ChartPx(46, 32)));

            var users = RecolorPreview.CollectUsers(RecolorSceneTool.CollectPreviewRenderers(component.gameObject), texture);
            var job = RecolorPipeline.PrepareJob(edit, texture, ChartSize, users);
            Assert.That(job?.mask, Is.Not.Null);
            var pixels = FloodFill.ReadR8(job.mask);
            int width = job.mask.width;
            MaskCache.Trim();

            Assert.That(width, Is.EqualTo(ChartSize), "前提: マスクはテクスチャと同じ大きさ");
            Assert.That(pixels[12 * width + 16], Is.EqualTo(255), "チャートAの内側");
            Assert.That(pixels[32 * width + 46], Is.EqualTo(255), "チャートBの内側");
            // A の上の空き（A からも B からもパディング 8 より離れている）
            Assert.That(pixels[50 * width + 16], Is.EqualTo(0), "どちらの島でもない場所");
        }

        // ── 種ごとに色を揃える（perSeedStats）──

        /// <summary>チャート A 側（x &lt; 30 px）を暗い灰色、B 側を明るい灰色にしたテクスチャの画素（sRGB）</summary>
        private static Color[] DarkALightBPixels()
        {
            var pixels = new Color[ChartSize * ChartSize];
            for (int y = 0; y < ChartSize; y++)
            {
                for (int x = 0; x < ChartSize; x++)
                {
                    float v = x < 30 ? 0.25f : 0.75f;
                    pixels[y * ChartSize + x] = new Color(v, v, v, 1f);
                }
            }
            return pixels;
        }

        /// <summary>
        /// 暗いチャート A を主種・明るいチャート B を追加の種にした編集を、同じ目標色で変換し、
        /// 両チャートの内側の結果の平均 Oklab L を返す
        /// </summary>
        private (float lA, float lB, int partCount) RunTwoIslandEdit(bool perSeedStats)
        {
            var (component, renderer, texture) = MakeChartAvatar(DarkALightBPixels());
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            RecolorSceneTool.ResolveSeedToggle(component, edit, MakeChartHit(renderer, texture, 2, ChartPx(46, 32)));
            Assert.That(edit.extraSeeds.Count, Is.EqualTo(1), "前提: 追加の種が1つ");
            // パディングで隣の色を拾わないようにする
            edit.padding = 0;
            edit.perSeedStats = perSeedStats;
            edit.targetColor = new Color(0.2f, 0.5f, 0.8f);
            edit.hasTarget = true;
            edit.darkEndRatio = 0.5f;

            var users = RecolorPreview.CollectUsers(RecolorSceneTool.CollectPreviewRenderers(component.gameObject), texture);
            var job = RecolorPipeline.PrepareJob(edit, texture, ChartSize, users);
            Assert.That(job?.mask, Is.Not.Null);
            int partCount = job.ShiftParts.Count;

            var result = RecolorPipeline.Run(new PipelineInput { sourceAsset = texture, workingSize = ChartSize, jobs = new[] { job } });
            Assert.That(result, Is.Not.Null);
            Color[] pixels;
            try
            {
                pixels = ReadLinear(result);
            }
            finally
            {
                RecolorPipeline.DestroyWorkTexture(result);
                MaskCache.Trim();
            }

            return (MeanL(pixels, 6, 26, 6, 28), MeanL(pixels, 34, 58, 6, 58), partCount);
        }

        private static float MeanL(Color[] linearPixels, int x0, int x1, int y0, int y1)
        {
            double sum = 0;
            int count = 0;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var c = linearPixels[y * ChartSize + x];
                    sum += OklabConverter.LinearRGBToOklab(new Vector3(c.r, c.g, c.b)).x;
                    count++;
                }
            }
            return (float)(sum / count);
        }

        private static Color[] ReadLinear(RenderTexture rt)
        {
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAHalf, false, true);
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
                tex.Apply(false);
                return tex.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(tex);
            }
        }

        private static void RequireShiftGpu()
        {
            RequireMaskGpu();
            if (!SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.ARGBHalf))
            {
                Assert.Ignore("この環境はcompute shader（ARGBHalfへの書き込み）に対応していません");
            }
            Assert.That(RecolorPipeline.IsShiftAvailable, Is.True, "ColorShift.computeが読み込めません（.metaのGUIDを確認）");
        }

        [Test]
        public void 種ごとに色を揃えると明るさの違う島が同じ明るさになる()
        {
            RequireShiftGpu();

            var (lA, lB, partCount) = RunTwoIslandEdit(perSeedStats: true);

            Assert.That(partCount, Is.EqualTo(2), "種ごとに部分が分かれる");
            Assert.That(lA, Is.EqualTo(lB).Within(0.05f), $"暗い島L={lA},明るい島L={lB}");
        }

        [Test]
        public void 種ごとに色を揃えないと暗い島は暗いまま()
        {
            RequireShiftGpu();

            var (lA, lB, partCount) = RunTwoIslandEdit(perSeedStats: false);

            Assert.That(partCount, Is.EqualTo(1), "全体で1つの部分");
            Assert.That(lA, Is.LessThan(lB - 0.05f), $"暗い島L={lA},明るい島L={lB}");
        }

        // ── グラデーション ──

        /// <summary>対象ルートの子に、原点中心・1 辺 1 の立方体の頂点を持つ MeshRenderer を置く（bounds を決めるため）</summary>
        private MeshRenderer MakeBoxRenderer(Transform parent, Vector3 localPosition, Vector3 localScale)
        {
            var mesh = Track(new Mesh());
            var vertices = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                vertices[i] = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
            }
            mesh.vertices = vertices;
            mesh.triangles = new[] { 0, 1, 2, 1, 3, 2, 4, 6, 5, 5, 6, 7 };
            mesh.RecalculateBounds();
            var go = new GameObject("Body");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            return go.AddComponent<MeshRenderer>();
        }

        [Test]
        public void グラデーションを_ON_にすると既定の箱が選択した島の位置の範囲になる()
        {
            RequireMaskGpu();
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            {
                Assert.Ignore("この環境はARGBFloatのRenderTextureに対応していません");
            }
            Assert.That(PositionMap.IsAvailable, Is.True, "PositionMap.shaderが読み込めません（.metaのGUIDを確認）");

            // メッシュは UV = 位置（z = 0）なので、チャート A（x 4..28 px, y 4..30 px）の UV 範囲がそのままルートのローカルの範囲
            var (component, renderer, texture) = MakeChartAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeChartHit(renderer, texture, 0, ChartPx(20, 10)), null);
            try
            {
                var box = GradientBox.DefaultFor(component, edit);

                // 画素の中心で位置を取るので、端は最大 1 px ぶん内側に寄る
                const float tolerance = 1.5f / ChartSize;
                Assert.That(box.position.x, Is.EqualTo(16f / ChartSize).Within(tolerance));
                Assert.That(box.position.y, Is.EqualTo(17f / ChartSize).Within(tolerance));
                Assert.That(box.position.z, Is.EqualTo(0f).Within(1e-4f));
                Assert.That(box.rotation, Is.EqualTo(Quaternion.identity));
                // 各軸 10% 広げる
                Assert.That(box.size.x, Is.EqualTo(24f / ChartSize * 1.1f).Within(tolerance * 1.1f));
                Assert.That(box.size.y, Is.EqualTo(26f / ChartSize * 1.1f).Within(tolerance * 1.1f));
                Assert.That(box.size.z, Is.EqualTo(GradientBox.SelectionMinSize).Within(1e-6f), "薄い軸は最小値で止める");
                // チャート B（x 32..60 px）の位置を含まない
                Assert.That(box.position.x + box.size.x * 0.5f, Is.LessThan(32f / ChartSize), "箱がチャートBまで広がっている");
                // 重心は選択範囲の中（A は長方形なので中心付近）
                Assert.That(box.centroid.x, Is.EqualTo(16f / ChartSize).Within(tolerance));
                Assert.That(box.centroid.y, Is.EqualTo(17f / ChartSize).Within(tolerance));

                // ON にすると箱が既定に置き直される（位置は重心ではなく範囲の中心）
                edit.gradientBoxPosition = new Vector3(5f, 5f, 5f);
                GradientBox.SetEnabled(component, edit, true);

                Assert.That(edit.gradientEnabled, Is.True);
                Assert.That(Vector3.Distance(edit.gradientBoxPosition, box.position), Is.LessThan(1e-4f));
                Assert.That(Vector3.Distance(edit.gradientBoxSize, box.size), Is.LessThan(1e-4f));
                Assert.That(edit.gradientBoxRotation, Is.EqualTo(Quaternion.identity));
            }
            finally
            {
                PositionMap.ClearCache();
            }
        }

        [Test]
        public void 選択範囲が取れないときの既定の箱は主種_Renderer_の_bounds_になる()
        {
            // 主種の Renderer のマテリアルが編集のテクスチャを持たない（位置マップを描けない）ので、従来の bounds にする
            var component = MakeComponent();
            // ルートを動かして拡大しても、箱はルートのローカルで bounds を囲む
            component.transform.position = new Vector3(10f, 0f, 0f);
            component.transform.localScale = Vector3.one * 2f;
            var renderer = MakeBoxRenderer(component.transform, new Vector3(0f, 1f, 0f), new Vector3(0.5f, 2f, 0.01f));
            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = _texture;
            edit.seedRenderer = renderer;
            component.AddEdit(edit);

            var box = GradientBox.DefaultFor(component, edit);

            Assert.That(box.position.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(box.position.y, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(box.position.z, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(box.rotation, Is.EqualTo(Quaternion.identity));
            Assert.That(box.size.x, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(box.size.y, Is.EqualTo(2f).Within(1e-4f));
            Assert.That(box.size.z, Is.EqualTo(GradientBox.MinSize).Within(1e-6f), "薄い軸は最小値で止める");

            // ON にすると箱が既定に置き直される
            edit.gradientBoxPosition = new Vector3(5f, 5f, 5f);
            GradientBox.SetEnabled(component, edit, true);

            Assert.That(edit.gradientEnabled, Is.True);
            Assert.That(Vector3.Distance(edit.gradientBoxPosition, box.position), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(edit.gradientBoxSize, box.size), Is.LessThan(1e-4f));
            Assert.That(edit.gradientBoxRotation, Is.EqualTo(Quaternion.identity));
        }

        private RecolorEdit MakeGradientEdit(out ClickRecolor component)
        {
            component = MakeComponent();
            var renderer = MakeBoxRenderer(component.transform, new Vector3(0f, 1f, 0f), new Vector3(0.5f, 2f, 0.01f));
            var edit = RecolorEdit.CreateNew();
            edit.sourceTexture = _texture;
            edit.seedRenderer = renderer;
            edit.seedColor = Color.green;
            component.AddEdit(edit);
            return edit;
        }

        [Test]
        public void グラデーションをOFFからONに戻しても色2と箱は前のまま()
        {
            var edit = MakeGradientEdit(out var component);
            GradientBox.SetEnabled(component, edit, true);
            edit.gradientColor = Color.blue;
            edit.gradientStrength = 0.3f;
            edit.gradientBoxPosition = new Vector3(5f, 5f, 5f);

            GradientBox.SetEnabled(component, edit, false);
            GradientBox.SetEnabled(component, edit, true);

            Assert.That(edit.gradientEnabled, Is.True);
            Assert.That(edit.gradientColor, Is.EqualTo(Color.blue));
            Assert.That(edit.gradientStrength, Is.EqualTo(0.3f));
            Assert.That(edit.gradientBoxPosition, Is.EqualTo(new Vector3(5f, 5f, 5f)));
        }

        [Test]
        public void 印の無い版でONにしたグラデーションもOFFからONで初期化しない()
        {
            var edit = MakeGradientEdit(out var component);
            edit.gradientEnabled = true; // 印（gradientInitialized）が無い版で ON にした編集
            edit.gradientColor = Color.blue;

            GradientBox.SetEnabled(component, edit, false);
            GradientBox.SetEnabled(component, edit, true);

            Assert.That(edit.gradientColor, Is.EqualTo(Color.blue));
        }

        [Test]
        public void 元の色に戻して切ったグラデーションも次のONで色2が残る()
        {
            var edit = MakeGradientEdit(out var component);
            edit.gradientEnabled = true;
            edit.gradientColor = Color.blue;

            ToolPanelOverlay.ResetToOriginal(component, edit);
            GradientBox.SetEnabled(component, edit, true);

            Assert.That(edit.gradientColor, Is.EqualTo(Color.blue));
        }

        [Test]
        public void グラデーションのリセットで色2と箱が初期値に戻りONのまま()
        {
            var edit = MakeGradientEdit(out var component);
            GradientBox.SetEnabled(component, edit, true);
            var box = GradientBox.DefaultFor(component, edit);
            edit.gradientColor = Color.blue;
            edit.gradientStrength = 0.3f;
            edit.gradientBoxPosition = new Vector3(5f, 5f, 5f);

            GradientBox.ResetSettings(component, edit);

            Assert.That(edit.gradientEnabled, Is.True);
            Assert.That(edit.gradientColor, Is.EqualTo(edit.seedColor));
            Assert.That(edit.gradientStrength, Is.EqualTo(edit.strength));
            Assert.That(Vector3.Distance(edit.gradientBoxPosition, box.position), Is.LessThan(1e-4f));
        }

        [Test]
        public void 元の色に戻すとグラデーションも切れる()
        {
            var component = MakeComponent();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), Color.green);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.red);
            edit.gradientEnabled = true;

            ToolPanelOverlay.ResetToOriginal(component, edit);

            Assert.That(edit.gradientEnabled, Is.False);
        }

        [Test]
        public void グラデーション中に色1をリセットすると_色1だけ元の色になり_グラデーションと編集は残る()
        {
            var component = MakeComponent();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), Color.green);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.red);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.blue, gradientEnd: true);
            edit.gradientEnabled = true;

            ToolPanelOverlay.ResetEditingColor(component, edit, end: false);

            Assert.That(edit.targetColor, Is.EqualTo(Color.green), "色1は元の色");
            Assert.That(edit.hasTarget, Is.True, "色の指定は残る（色未設定の編集として捨てられない）");
            Assert.That(edit.gradientEnabled, Is.True, "グラデーションは残る");
            Assert.That(edit.gradientColor, Is.EqualTo(Color.blue), "色2は変えない");

            // 別の場所を触ったときに捨てられない
            RecolorSceneTool.DiscardPendingEdit();
            Assert.That(component.FindEdit(edit.id), Is.Not.Null);
        }

        [Test]
        public void 色の指定を取り消した編集は自動で捨てられず_色を決めていない編集は従来どおり捨てられる()
        {
            var component = MakeComponent();
            var reset = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), Color.green);
            ToolPanelOverlay.ApplyTargetColor(component, reset, Color.red);
            ToolPanelOverlay.ResetEditingColor(component, reset, end: false);
            Assert.That(reset.hasTarget, Is.False, "見た目は元どおり（色未設定）");
            Assert.That(reset.confirmed, Is.True, "一度色を決めた印は残る");

            RecolorSceneTool.DiscardPendingEdit();
            Assert.That(component.FindEdit(reset.id), Is.Not.Null, "選択範囲（編集）は残る");

            // クリック直後で色を決めていない仮の編集は、従来どおり捨てられる
            var pending = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), Color.green);
            Assert.That(pending.confirmed, Is.False);
            RecolorSceneTool.DiscardPendingEdit();
            Assert.That(component.FindEdit(pending.id), Is.Null);
            Assert.That(component.FindEdit(reset.id), Is.Not.Null);
        }

        [Test]
        public void グラデーションOFFで色1をリセットすると従来どおり色の指定を取り消す()
        {
            var component = MakeComponent();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), Color.green);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.red);

            ToolPanelOverlay.ResetEditingColor(component, edit, end: false);

            Assert.That(edit.hasTarget, Is.False);
            Assert.That(edit.targetColor, Is.EqualTo(Color.green));
        }

        [Test]
        public void 色のリセットでその色のガンマも1に戻る()
        {
            var component = MakeComponent();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), Color.green);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.red);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.blue, gradientEnd: true);
            edit.gradientEnabled = true;
            edit.gamma = 0.5f;
            edit.gradientGamma = 1.5f;

            ToolPanelOverlay.ResetEditingColor(component, edit, end: true);
            Assert.That(edit.gradientGamma, Is.EqualTo(1f), "色 2 のリセットで色 2 のガンマが戻る");
            Assert.That(edit.gamma, Is.EqualTo(0.5f), "色 1 のガンマは変えない");

            ToolPanelOverlay.ResetEditingColor(component, edit, end: false);
            Assert.That(edit.gamma, Is.EqualTo(1f), "グラデーション中の色 1 のリセットで色 1 のガンマが戻る");

            edit.gradientEnabled = false;
            edit.gamma = 0.7f;
            ToolPanelOverlay.ResetToOriginal(component, edit);
            Assert.That(edit.gamma, Is.EqualTo(1f), "色の指定の取り消しでもガンマが戻る");
        }

        [Test]
        public void 陰影のリセットで陰影の暗さは自動の値に_陰影の強調は0に戻り_不透明度は変えない()
        {
            var component = MakeComponent();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), Color.green);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.red);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.blue, gradientEnd: true);
            edit.darkEndRatio = 0.1f;
            edit.shadingStretch = 0.8f;
            edit.strength = 0.3f;
            edit.gradientDarkEndRatio = 0.2f;
            edit.gradientStrength = 0.4f;

            ToolPanelOverlay.ResetShading(component, edit, end: false);
            Assert.That(edit.darkEndRatio, Is.EqualTo(DarkEndAutoAdjust.Compute(Color.red)).Within(1e-6f));
            Assert.That(edit.shadingStretch, Is.EqualTo(0f));
            Assert.That(edit.strength, Is.EqualTo(0.3f), "不透明度は戻さない");
            Assert.That(edit.gradientDarkEndRatio, Is.EqualTo(0.2f), "色 1 のリセットでは色 2 の値は変えない");

            ToolPanelOverlay.ResetShading(component, edit, end: true);
            Assert.That(edit.gradientDarkEndRatio, Is.EqualTo(DarkEndAutoAdjust.Compute(Color.blue)).Within(1e-6f));
            Assert.That(edit.gradientStrength, Is.EqualTo(0.4f), "不透明度は戻さない");
        }

        [Test]
        public void 色2への書き込みは色1を変えず_色1が未設定なら編集を確定する()
        {
            var component = MakeComponent();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(_texture), Color.green);

            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.blue, gradientEnd: true);

            Assert.That(edit.gradientColor, Is.EqualTo(Color.blue));
            Assert.That(edit.targetColor, Is.EqualTo(Color.green), "色1は元の色のまま");
            // 色 1 を決める前に色 2 を決めても編集が確定する（仮のままだとグラデーションが掛からない）
            Assert.That(edit.hasTarget, Is.True);
            Assert.That(edit.darkEndRatio, Is.EqualTo(DarkEndAutoAdjust.Compute(Color.green)).Within(1e-5f));
            Assert.That(edit.gradientDarkEndRatio, Is.EqualTo(DarkEndAutoAdjust.Compute(Color.blue)).Within(1e-5f));
        }
    }
}
