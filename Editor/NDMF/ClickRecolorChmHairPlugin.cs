using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(Nekoare.ClickRecolor.Editor.NDMF.ClickRecolorChmHairPlugin))]

namespace Nekoare.ClickRecolor.Editor.NDMF
{
    /// <summary>
    /// 髪ツール（キメラヘアマスター）の髪の上に色を掛ける入口。処理は本体と共通で、担当範囲（RecolorScope.ChmHair）だけが違う。
    /// 本体（ClickRecolorPlugin）と別のプラグインにする: NDMF の BeforePlugin はプラグインの全シーケンスの前に掛かるので、
    /// 1 つのプラグインに「髪ツールより前」（本体）と「髪ツールより後」（ここ）を同居させると循環する。
    /// 「髪ツールより後」は髪ツール側が BeforePlugin(このプラグイン) で宣言する。こちらから AfterPlugin(髪ツール) と書くと、
    /// 髪ツールの「他のプラグインの後に動くシーケンス」まで含めた「後」になり、下の BeforePlugin と循環してビルドが止まる。
    /// 宣言の無い古い髪ツールでは順序が決まらないので、パスもフィルタも何もしない（本体が髪も今までどおり担当する）
    /// </summary>
    public class ClickRecolorChmHairPlugin : Plugin<ClickRecolorChmHairPlugin>
    {
        public override string DisplayName => "Tocolo (over Chimera Hair Master)";
        public override string QualifiedName => HairToolTargets.HairPluginName;

        protected override void Configure()
        {
            // 髪ツールが材質・メッシュを差し替える前の状態を記録する（範囲はプレビューと同じ元の状態で作る）
            InPhase(BuildPhase.Resolving)
                .Run(ChmHairRecordPass.Instance);

            InPhase(BuildPhase.Transforming)
                .BeforePlugin("nadena.dev.modular-avatar")
                .BeforePlugin("net.rs64.tex-trans-tool")
                .Run(ChmHairPass.Instance)
                .PreviewingWith(new RecolorPreview(RecolorScope.ChmHair), new DecalOverlayPreview(RecolorScope.ChmHair));
        }
    }
}
