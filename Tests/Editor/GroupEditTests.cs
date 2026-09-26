using System.Collections.Generic;
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
    /// 「アバター全体」の連結（RecolorEdit.groupId）: 範囲の切り替えでの作成・解除、パネル操作の連結全体への反映、
    /// 連結ごとの削除、他テクスチャ分の選択範囲の作成のテスト。
    /// 島の連結（Ctrl＋クリック・矩形で別のテクスチャの島を足す）のメンバーの追加・削除・ハイライトのテスト
    /// </summary>
    public class GroupEditTests
    {
        private const string TextureAPath = "Assets/__ClickRecolorGroupEditTests_A.asset";
        private const string TextureBPath = "Assets/__ClickRecolorGroupEditTests_B.asset";
        private const int Size = 16;

        private readonly List<Object> _cleanup = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            // Clear は ColorScopePreference も消す（既定のつながった範囲に戻る）。TearDown でも同じく戻す
            ToolSession.Clear();
            ToolSession.Preset = RangePreset.SimilarColor;
            // 別テクスチャの島を足すテストのため（既定は OFF）
            ToolSession.CrossTextureEnabled = true;
            Undo.IncrementCurrentGroup();
        }

        [TearDown]
        public void TearDown()
        {
            ToolSession.Clear();
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
            AssetDatabase.DeleteAsset(TextureAPath);
            AssetDatabase.DeleteAsset(TextureBPath);
            MaskCache.ClearCache();
            CoverageMask.ClearCache();
            UvChartDetector.ClearCache();
        }

        private T Track<T>(T obj) where T : Object
        {
            _cleanup.Add(obj);
            return obj;
        }

        private static Texture2D MakeTextureAsset(string path, Color color)
        {
            var texture = new Texture2D(Size, Size);
            var pixels = new Color[Size * Size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            texture.SetPixels(pixels);
            texture.Apply();
            AssetDatabase.CreateAsset(texture, path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>UV 全面の四角形 1 枚</summary>
        private Mesh MakeQuad()
        {
            var mesh = Track(new Mesh());
            var uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            var vertices = new Vector3[uv.Length];
            for (int i = 0; i < uv.Length; i++) vertices[i] = uv[i];
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            return mesh;
        }

        private MeshRenderer AddBody(GameObject root, string name, Texture2D texture)
        {
            var body = new GameObject(name);
            body.transform.SetParent(root.transform);
            body.AddComponent<MeshFilter>().sharedMesh = MakeQuad();
            var renderer = body.AddComponent<MeshRenderer>();
            var material = Track(new Material(Shader.Find("Standard")));
            material.SetTexture("_MainTex", texture);
            renderer.sharedMaterials = new[] { material };
            return renderer;
        }

        /// <summary>ルート（ClickRecolor 付き）の下に、別々のテクスチャを持つ Renderer を 2 つ置く</summary>
        private (ClickRecolor component, MeshRenderer bodyA, MeshRenderer bodyB, Texture2D texA, Texture2D texB) MakeAvatar()
        {
            var texA = MakeTextureAsset(TextureAPath, Color.red);
            var texB = MakeTextureAsset(TextureBPath, Color.blue);
            var root = Track(new GameObject("Avatar"));
            var bodyA = AddBody(root, "BodyA", texA);
            var bodyB = AddBody(root, "BodyB", texB);
            var component = root.AddComponent<ClickRecolor>();
            return (component, bodyA, bodyB, texA, texB);
        }

        private static PickHit MakeHit(MeshRenderer renderer, Texture2D texture)
        {
            var uv = new Vector2(0.25f, 0.25f);
            return new PickHit
            {
                renderer = renderer,
                subMeshIndex = 0,
                materialSlot = 0,
                triangleIndex = 0,
                uv0 = uv,
                uv = uv,
                texel = new Vector2Int(4, 4),
                hasMainTexture = true,
                mainTexture = new MainTextureInfo { propertyName = "_MainTex", texture = texture, scale = Vector2.one, offset = Vector2.zero },
                material = renderer.sharedMaterial,
            };
        }

        /// <summary>クリックで編集を作り（似た色＝つながった範囲）、範囲を「アバター全体」にして連結を作る</summary>
        private (ClickRecolor component, RecolorEdit clicked, MeshRenderer bodyA, Texture2D texA, Texture2D texB) MakeGroup()
        {
            var (component, bodyA, _, texA, texB) = MakeAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(bodyA, texA), Color.red);
            var current = ToolPanelOverlay.ChangeScope(component, edit, ColorScope.WholeAvatar);
            Assert.That(current, Is.SameAs(edit), "前提: 連結を作っても現在の編集はクリックした編集");
            return (component, edit, bodyA, texA, texB);
        }

        [Test]
        public void アバター全体にすると他のテクスチャ分が同じ連結で作られる()
        {
            var (component, clicked, bodyA, texA, texB) = MakeGroup();

            Assert.That(component.edits.Count, Is.EqualTo(2));
            var other = component.edits[0] == clicked ? component.edits[1] : component.edits[0];
            Assert.That(clicked.sourceTexture, Is.SameAs(texA));
            Assert.That(other.sourceTexture, Is.SameAs(texB));

            Assert.That(clicked.groupId, Is.Not.Null.And.Not.Empty);
            Assert.That(other.groupId, Is.EqualTo(clicked.groupId));
            Assert.That(clicked.scope, Is.EqualTo(ColorScope.WholeAvatar));
            Assert.That(other.scope, Is.EqualTo(ColorScope.WholeAvatar));
            Assert.That(other.mode, Is.EqualTo(SelectionMode.Color));

            // 種色（Oklab）は全員で共有する
            Assert.That(clicked.hasSeedOklab, Is.True);
            Assert.That(other.hasSeedOklab, Is.True);
            Assert.That(other.seedOklab, Is.EqualTo(clicked.seedOklab));

            // 同じ設定
            Assert.That(other.threshold, Is.EqualTo(clicked.threshold));
            Assert.That(other.feather, Is.EqualTo(clicked.feather));
            Assert.That(other.seedColor, Is.EqualTo(clicked.seedColor));
            Assert.That(other.targetColor, Is.EqualTo(clicked.seedColor));
            Assert.That(other.hasTarget, Is.False);

            // 種の Renderer はクリックした編集だけが持つ
            Assert.That(clicked.seedRenderer, Is.SameAs(bodyA));
            Assert.That(other.seedRenderer, Is.Null);
            Assert.That(other.seedTriangle, Is.EqualTo(-1));
            Assert.That(other.seedUv, Is.EqualTo(Vector2.zero));

            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(clicked.id));
            Assert.That(RecolorPipeline.NeedsSeedRenderer(other), Is.False);
        }

        [Test]
        public void 色の変更と元の色に戻すは連結の全編集に効く()
        {
            var (component, clicked, _, _, _) = MakeGroup();

            ToolPanelOverlay.ApplyTargetColor(component, clicked, Color.green);

            foreach (var e in component.edits)
            {
                Assert.That(e.targetColor, Is.EqualTo(Color.green));
                Assert.That(e.hasTarget, Is.True);
            }

            ToolPanelOverlay.ResetToOriginal(component, clicked);

            foreach (var e in component.edits)
            {
                Assert.That(e.hasTarget, Is.False);
                Assert.That(e.targetColor, Is.EqualTo(e.seedColor));
            }
        }

        [Test]
        public void 範囲の既定がアバター全体ならクリックだけで連結が作られる()
        {
            var (component, bodyA, _, texA, texB) = MakeAvatar();
            ToolSession.ColorScopePreference = ColorScope.WholeAvatar;

            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(bodyA, texA), Color.red);

            Assert.That(component.edits.Count, Is.EqualTo(2));
            var other = component.edits[0] == edit ? component.edits[1] : component.edits[0];
            Assert.That(edit.scope, Is.EqualTo(ColorScope.WholeAvatar));
            Assert.That(edit.groupId, Is.Not.Null.And.Not.Empty);
            Assert.That(other.groupId, Is.EqualTo(edit.groupId));
            Assert.That(other.sourceTexture, Is.SameAs(texB));
            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(edit.id));
        }

        [Test]
        public void 削除は連結ごと消える()
        {
            var (component, clicked, _, _, _) = MakeGroup();
            ToolPanelOverlay.ApplyTargetColor(component, clicked, Color.green);

            ToolPanelOverlay.DeleteEdit(component, clicked);

            Assert.That(component.edits, Is.Empty);
            Assert.That(ToolSession.CurrentEditId, Is.Null);
        }

        [Test]
        public void 色未設定の連結は次のクリックで連結ごと捨てられる()
        {
            var (component, _, bodyA, texA, _) = MakeGroup();

            var next = RecolorSceneTool.CreateEditFromHit(component, MakeHit(bodyA, texA), Color.red);

            Assert.That(component.edits.Count, Is.EqualTo(1));
            Assert.That(component.edits[0], Is.SameAs(next));
        }

        [Test]
        public void アバター全体からつながった範囲に戻すと1件に戻り連結が外れる()
        {
            var (component, clicked, bodyA, _, _) = MakeGroup();
            var other = component.edits[0] == clicked ? component.edits[1] : component.edits[0];

            // 種の Renderer を持たない側から戻しても、種の Renderer を持つ編集が残る
            var current = ToolPanelOverlay.ChangeScope(component, other, ColorScope.Contiguous);

            Assert.That(component.edits.Count, Is.EqualTo(1));
            Assert.That(current, Is.SameAs(clicked));
            Assert.That(current.seedRenderer, Is.SameAs(bodyA));
            Assert.That(current.groupId, Is.Null.Or.Empty);
            Assert.That(current.hasSeedOklab, Is.False);
            Assert.That(current.scope, Is.EqualTo(ColorScope.Contiguous));
            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(clicked.id));
        }

        [Test]
        public void つながった範囲からアバター全体にすると2件に増える()
        {
            var (component, bodyA, _, texA, _) = MakeAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(bodyA, texA), Color.red);
            ToolPanelOverlay.ApplyTargetColor(component, edit, Color.green);
            Assert.That(component.edits.Count, Is.EqualTo(1), "前提: 単独の編集");

            ToolPanelOverlay.ChangeScope(component, edit, ColorScope.WholeAvatar);

            Assert.That(component.edits.Count, Is.EqualTo(2));
            // 色を決めた後に連結しても、他のテクスチャ分は同じ色になる
            foreach (var e in component.edits)
            {
                Assert.That(e.groupId, Is.EqualTo(edit.groupId));
                Assert.That(e.targetColor, Is.EqualTo(Color.green));
                Assert.That(e.hasTarget, Is.True);
            }
        }

        [Test]
        public void 連結の他のテクスチャ分も種の_Renderer_なしで選択範囲が作れる()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.SupportsRandomWriteOnRenderTextureFormat(RenderTextureFormat.R8))
            {
                Assert.Ignore("この環境は compute shader（R8 への書き込み）に対応していません");
            }
            var (component, clicked, _, _, texB) = MakeGroup();
            var other = component.edits[0] == clicked ? component.edits[1] : component.edits[0];

            var users = RecolorPreview.CollectUsers(RecolorSceneTool.CollectPreviewRenderers(component.gameObject), texB);
            var job = RecolorPipeline.PrepareJob(other, texB, Size, users);
            MaskCache.Trim();

            Assert.That(job, Is.Not.Null);
            Assert.That(job.mask, Is.Not.Null);
        }

        // ── 島の連結（Ctrl＋クリック・矩形で別のテクスチャの島を足す。各メンバーが自分の種を持つ）──

        /// <summary>島モードで A の島から編集を作り、Ctrl＋クリック相当で B の島を足して島の連結にする</summary>
        private (ClickRecolor component, RecolorEdit edit, RecolorEdit member, MeshRenderer bodyA, MeshRenderer bodyB, Texture2D texA, Texture2D texB) MakeSeedGroup()
        {
            ToolSession.Preset = RangePreset.Island;
            var (component, bodyA, bodyB, texA, texB) = MakeAvatar();
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(bodyA, texA), Color.red);
            var result = RecolorSceneTool.ResolveSeedToggle(component, edit, MakeHit(bodyB, texB), Color.blue);
            Assert.That(result, Is.EqualTo(SeedToggleResult.MemberAdded), "前提: 別のテクスチャの島はメンバーとして足される");
            var member = EditGroups.FindMemberForTexture(component, edit, texB);
            Assert.That(member, Is.Not.Null, "前提: B のメンバーがある");
            return (component, edit, member, bodyA, bodyB, texA, texB);
        }

        [Test]
        public void Ctrlクリックで別のテクスチャの島を足すと同じ連結のメンバーが増える()
        {
            var (component, edit, member, _, bodyB, _, texB) = MakeSeedGroup();

            Assert.That(component.edits.Count, Is.EqualTo(2));
            Assert.That(member, Is.Not.SameAs(edit));
            Assert.That(edit.groupId, Is.Not.Null.And.Not.Empty);
            Assert.That(member.groupId, Is.EqualTo(edit.groupId));
            // 主種はクリックした B の島。共有の種色は持たない（「アバター全体」の連結とは別）
            Assert.That(member.sourceTexture, Is.SameAs(texB));
            Assert.That(member.seedRenderer, Is.SameAs(bodyB));
            Assert.That(member.seedTriangle, Is.EqualTo(0));
            Assert.That(member.seedUv, Is.EqualTo(new Vector2(0.25f, 0.25f)));
            Assert.That(member.extraSeeds, Is.Empty);
            Assert.That(member.hasSeedOklab, Is.False);
            Assert.That(edit.hasSeedOklab, Is.False);
            Assert.That(EditGroups.IsWholeAvatarGroup(edit), Is.False);
            // 設定は写す・元の色はクリックした位置の色
            Assert.That(member.mode, Is.EqualTo(SelectionMode.Island));
            Assert.That(member.seedColor, Is.EqualTo(Color.blue));
            // 現在の編集は操作したメンバー
            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(member.id));
        }

        [Test]
        public void 連結に同じテクスチャのメンバーがあれば2回目はそのメンバーに種が増える()
        {
            var (component, edit, member, _, _, _, texB) = MakeSeedGroup();
            // B を使う別メッシュの Renderer（別の島）
            var bodyB2 = AddBody(component.gameObject, "BodyB2", texB);

            var result = RecolorSceneTool.ResolveSeedToggle(component, edit, MakeHit(bodyB2, texB));

            Assert.That(result, Is.EqualTo(SeedToggleResult.Added));
            Assert.That(component.edits.Count, Is.EqualTo(2));
            Assert.That(member.extraSeeds.Count, Is.EqualTo(1));
            Assert.That(member.extraSeeds[0].renderer, Is.SameAs(bodyB2));
            Assert.That(edit.extraSeeds, Is.Empty);
            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(member.id));
        }

        [Test]
        public void メンバーの最後の種を外すとメンバーが消え_残り1件なら連結が外れる()
        {
            var (component, edit, _, _, bodyB, _, texB) = MakeSeedGroup();

            var result = RecolorSceneTool.ResolveSeedToggle(component, edit, MakeHit(bodyB, texB));

            Assert.That(result, Is.EqualTo(SeedToggleResult.MemberRemoved));
            Assert.That(component.edits.Count, Is.EqualTo(1));
            Assert.That(component.edits[0], Is.SameAs(edit));
            Assert.That(edit.groupId, Is.Null.Or.Empty);
            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(edit.id));
        }

        [Test]
        public void 島の連結でも色の変更は全メンバーに効く()
        {
            var (component, edit, member, _, _, _, _) = MakeSeedGroup();

            ToolPanelOverlay.ApplyTargetColor(component, member, Color.green);

            foreach (var e in new[] { edit, member })
            {
                Assert.That(e.targetColor, Is.EqualTo(Color.green));
                Assert.That(e.hasTarget, Is.True);
            }
        }

        [Test]
        public void 矩形で2つのテクスチャの島が一度に入る()
        {
            ToolSession.Preset = RangePreset.Island;
            var (component, bodyA, bodyB, texA, texB) = MakeAvatar();
            var bodyB2 = AddBody(component.gameObject, "BodyB2", texB);
            var edit = RecolorSceneTool.CreateEditFromHit(component, MakeHit(bodyA, texA), Color.red);
            var uv = new Vector2(0.25f, 0.25f);
            var islands = new List<RectIsland>
            {
                new RectIsland { renderer = bodyA, submesh = 0, triangle = 1, chartId = -1, uv0 = uv, uv = uv, texture = texA },  // 主種と同じ島
                new RectIsland { renderer = bodyB, submesh = 0, triangle = 0, chartId = -1, uv0 = uv, uv = uv, texture = texB },  // B のメンバーの主種
                new RectIsland { renderer = bodyB2, submesh = 0, triangle = 0, chartId = -1, uv0 = uv, uv = uv, texture = texB }, // B のメンバーの追加の種
            };

            var result = RecolorSceneTool.ApplyRectSeeds(component, edit, islands);

            Assert.That(result.rejected, Is.False);
            Assert.That(result.added, Is.EqualTo(2));
            Assert.That(result.membersCreated, Is.EqualTo(1));
            Assert.That(component.edits.Count, Is.EqualTo(2));
            Assert.That(edit.extraSeeds, Is.Empty);
            var member = EditGroups.FindMemberForTexture(component, edit, texB);
            Assert.That(member, Is.Not.Null);
            Assert.That(member.groupId, Is.EqualTo(edit.groupId).And.Not.Empty);
            Assert.That(member.seedRenderer, Is.SameAs(bodyB));
            Assert.That(member.extraSeeds.Count, Is.EqualTo(1));
            Assert.That(member.extraSeeds[0].renderer, Is.SameAs(bodyB2));
        }

        [Test]
        public void ハイライトの状態は連結の全メンバーを含む()
        {
            var (component, edit, member, _, _, _, _) = MakeSeedGroup();

            var state = HighlightState.For(component, edit);

            Assert.That(state.enabled, Is.True);
            Assert.That(state.editIds, Is.EqualTo(new[] { edit.id, member.id }));
            // 色未設定でも、ハイライト中なら両方のテクスチャがプレビューの対象
            Assert.That(RecolorPreview.IsPreviewTarget(edit, state), Is.True);
            Assert.That(RecolorPreview.IsPreviewTarget(member, state), Is.True);
            // どのメンバーから作っても同じ値（並びは edits の順）
            Assert.That(HighlightState.For(component, member), Is.EqualTo(state));
            // 比較は順序込み
            Assert.That(new HighlightState(new[] { member.id, edit.id }, true), Is.Not.EqualTo(state));
        }

        [Test]
        public void 島の連結で範囲を変えても連結は保たれる()
        {
            var (component, edit, member, _, _, _, _) = MakeSeedGroup();

            var current = ToolPanelOverlay.ChangeScope(component, edit, ColorScope.WholeTexture);

            Assert.That(current, Is.SameAs(edit));
            Assert.That(component.edits.Count, Is.EqualTo(2));
            Assert.That(member.groupId, Is.EqualTo(edit.groupId));
            Assert.That(edit.scope, Is.EqualTo(ColorScope.WholeTexture));
            Assert.That(member.scope, Is.EqualTo(ColorScope.WholeTexture));
        }

        [Test]
        public void 島の連結からアバター全体にすると現在の編集だけ残して連結し直す()
        {
            var (component, edit, member, _, _, _, texB) = MakeSeedGroup();

            var current = ToolPanelOverlay.ChangeScope(component, edit, ColorScope.WholeAvatar);

            Assert.That(current, Is.SameAs(edit));
            Assert.That(component.edits.Count, Is.EqualTo(2));
            Assert.That(component.edits.Contains(member), Is.False, "島の連結のメンバーは消える");
            Assert.That(EditGroups.IsWholeAvatarGroup(edit), Is.True);
            var other = EditGroups.FindMemberForTexture(component, edit, texB);
            Assert.That(other, Is.Not.Null);
            Assert.That(other.seedRenderer, Is.Null);
            Assert.That(other.hasSeedOklab, Is.True);
            Assert.That(ToolSession.CurrentEditId, Is.EqualTo(edit.id));
        }

        // ── 連結の統計の共有（RecolorPipeline.ShareGroupStats。GPU を使わないようダミーの EditJob で確かめる）──

        private static EditJob DummyJob(string groupId, float lP05, float lP95, bool perSeedStats = false)
        {
            var edit = RecolorEdit.CreateNew();
            edit.groupId = groupId;
            edit.hasTarget = true;
            edit.perSeedStats = perSeedStats;
            var stats = new Stats { lP05 = lP05, lP95 = lP95, rep = Color.gray, count = 100 };
            return new EditJob
            {
                edit = edit,
                stats = stats,
                parts = new List<EditJob.Part> { new EditJob.Part { stats = stats } },
            };
        }

        [Test]
        public void 連結の2メンバーは統計を共有して明るさの範囲が全体の最小と最大になる()
        {
            // 黒い髪（暗い）と白い髪（明るい）を 1 つの分布として扱う
            var dark = DummyJob("g", 0.1f, 0.3f);
            var light = DummyJob("g", 0.7f, 0.9f);
            var originalDarkParts = dark.parts;

            RecolorPipeline.ShareGroupStats(new[] { dark, light });

            foreach (var job in new[] { dark, light })
            {
                Assert.That(job.ShiftParts.Count, Is.EqualTo(1));
                Assert.That(job.ShiftParts[0].stats.lP05, Is.EqualTo(0.1f));
                Assert.That(job.ShiftParts[0].stats.lP95, Is.EqualTo(0.9f));
                Assert.That(job.ShiftParts[0].stats.count, Is.EqualTo(200));
                Assert.That(job.stats.lP05, Is.EqualTo(0.1f));
                Assert.That(job.stats.lP95, Is.EqualTo(0.9f));
            }
            // 部分の一覧は差し替える（MaskCache が持つ元の一覧の統計は変えない）
            Assert.That(originalDarkParts[0].stats.lP95, Is.EqualTo(0.3f));
        }

        [Test]
        public void 各点の色を揃える連結と別の連結と単独の編集は統計を共有しない()
        {
            var perSeedA = DummyJob("p", 0.1f, 0.3f, perSeedStats: true);
            var perSeedB = DummyJob("p", 0.7f, 0.9f, perSeedStats: true);
            var otherGroup = DummyJob("q", 0.5f, 0.6f);
            var single = DummyJob(null, 0.2f, 0.4f);
            var grouped = DummyJob("g", 0.0f, 1.0f);

            RecolorPipeline.ShareGroupStats(new[] { perSeedA, perSeedB, otherGroup, single, grouped });

            Assert.That(perSeedA.ShiftParts[0].stats.lP95, Is.EqualTo(0.3f));
            Assert.That(perSeedB.ShiftParts[0].stats.lP05, Is.EqualTo(0.7f));
            Assert.That(otherGroup.ShiftParts[0].stats.lP05, Is.EqualTo(0.5f));
            Assert.That(single.ShiftParts[0].stats.lP05, Is.EqualTo(0.2f));
            Assert.That(single.ShiftParts[0].stats.lP95, Is.EqualTo(0.4f));
        }
    }
}
