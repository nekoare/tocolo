// 流用元: dev.nekoare.uv-side-splitter/Editor/NDMF/UvSideSplitPlugin.cs
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(Nekoare.ClickRecolor.Editor.NDMF.ClickRecolorPlugin))]

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    public class ClickRecolorPlugin : Plugin<ClickRecolorPlugin>
    {
        public override string DisplayName => "Tocolo";
        public override string QualifiedName => "dev.nekoare.click-recolor";

        protected override void Configure()
        {
            // 選択は「元テクスチャ基準」で計算するので、テクスチャを加工する後段プラグインより前に走らせる。
            // マテリアルを差し替えるプラグインより前に終えておくのは順序の決定論化のため（生成マテリアルは走査対象外になる。設計書 §14）。
            // 未インストールのプラグイン名はアンカーが作られるだけで無害（NDMF の WeakOrder）。
            InPhase(BuildPhase.Transforming)
                .BeforePlugin("nadena.dev.modular-avatar")
                .BeforePlugin("net.rs64.tex-trans-tool")
                .BeforePlugin("com.nekoare.chimera-hair-master")
                .Run(RecolorPass.Instance)
                // 重ね貼り（画像のなめらかな貼り付け）は色変え後のマテリアルを複製元にするので、RecolorPreview の後に置く。
                // 髪ツールの髪（その上に掛ける分）は髪用の入口 ClickRecolorChmHairPlugin が担当する
                .PreviewingWith(new RecolorPreview(RecolorScope.Main), new DecalOverlayPreview(RecolorScope.Main));

            // コンポーネント削除は最適化系プラグイン（下の BeforePlugin）より前に行い、未知コンポーネント警告を避ける
            InPhase(BuildPhase.Optimizing)
                .BeforePlugin("com.anatawa12.avatar-optimizer")
                .Run(RemoveComponentsPass.Instance);
        }
    }
}
