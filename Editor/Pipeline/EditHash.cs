namespace Nekoare.ClickRecolor.Editor.Pipeline
{
    /// <summary>
    /// 編集（RecolorEdit）の中身のハッシュ。プレビューの作り直しの判断に使う 3 区分:
    /// 形（範囲・画像・貼り方）／置き方（画像の箱）／見た目（色・陰影・グラデーション）。
    /// 重ね貼り（DecalOverlayPreview）は区分ごとに作り直す範囲を変える（置き方だけなら置き直し、見た目だけなら画像の色だけやり直す）。
    /// 表示専用のフィールド（name・seedColor）と状態の印（confirmed・*Initialized）は含めない。
    /// 出力に関わるフィールドを足したらどれかの区分に入れる（EditHashTests が全フィールドを見ている）
    /// </summary>
    internal static class EditHash
    {
        /// <summary>編集の出力に関わる全部（形・置き方・見た目）</summary>
        internal static int All(int h, RecolorEdit edit) => Look(Placement(Shape(h, edit), edit), edit);

        /// <summary>
        /// 編集の「形」: 範囲・画像・貼り方など、色の設定と画像の置き方（Placement）以外。
        /// 重ね貼り（DecalOverlayPreview）では、ここが変わると表示用メッシュを作り直す
        /// </summary>
        internal static int Shape(int h, RecolorEdit edit)
        {
            unchecked
            {
                if (edit == null) return h * 31 + 1;
                h = h * 31 + (edit.id != null ? edit.id.GetHashCode() : 0);
                h = h * 31 + (edit.enabled ? 1 : 0);
                h = h * 31 + (edit.sourceTexture != null ? edit.sourceTexture.GetInstanceID() : 0);
                h = h * 31 + (edit.seedRenderer != null ? edit.seedRenderer.GetInstanceID() : 0);
                h = h * 31 + edit.seedSubmesh;
                h = h * 31 + edit.seedTriangle;
                h = h * 31 + edit.seedUv.GetHashCode();
                h = h * 31 + RecolorPipeline.HashExtraSeeds(edit);
                h = h * 31 + (edit.hasSeedOklab ? 1 : 0);
                h = h * 31 + edit.seedOklab.GetHashCode();
                h = h * 31 + (edit.groupId != null ? edit.groupId.GetHashCode() : 0);
                h = h * 31 + (int)edit.mode;
                h = h * 31 + (edit.wholeTexture ? 1 : 0);
                h = h * 31 + (int)edit.scope;
                h = h * 31 + edit.threshold.GetHashCode();
                h = h * 31 + edit.feather.GetHashCode();
                h = h * 31 + edit.cleanupRadius;
                h = h * 31 + edit.padding;
                h = h * 31 + (edit.perSeedStats ? 1 : 0);
                // なぞって調整。なぞっている間は中身（paintAdjust）を書かず番号だけ振り直すので、番号も畳む
                h = h * 31 + edit.paintAdjustRevision;
                h = h * 31 + Masks.PaintAdjustStore.DataHash(edit.paintAdjustData);
                h = h * 31 + edit.paintAdjustWidth;
                h = h * 31 + edit.paintAdjustHeight;
                h = h * 31 + edit.paintEdgeOffset;
                // 打ち消し・グラデーションの ON/OFF は画像の下地に位置マップが要るかを決める（DecalImageBuilder.BuildBase）ので形の側
                h = h * 31 + (edit.flattenBase ? 1 : 0);
                h = h * 31 + (edit.gradientEnabled ? 1 : 0);
                // 「箱の中」は箱で範囲が決まる（他のモードでは箱を変えても結果が変わらないので含めない）
                if (edit.mode == SelectionMode.Box)
                {
                    h = h * 31 + edit.boxPosition.GetHashCode();
                    h = h * 31 + edit.boxRotation.GetHashCode();
                    h = h * 31 + edit.boxSize.GetHashCode();
                    // ドラッグ中はパーツごとの統計とスーパーサンプリングを掛けないので、離した瞬間に作り直せるようドラッグ状態も含める
                    h = h * 31 + (edit.boxPerPartStats ? 1 : 0);
                    h = h * 31 + (BoxDragState.Selection.IsFor(edit) ? 1 : 0);
                }
                // 画像を入れる: 画像の中身・箱・比率・貼り方で結果が変わる（OFF なら含めない）
                h = h * 31 + (edit.decalEnabled ? 1 : 0);
                if (edit.decalEnabled)
                {
                    h = h * 31 + (edit.decalTexture != null ? edit.decalTexture.GetInstanceID() : 0);
                    h = h * 31 + (edit.decalTexture != null ? edit.decalTexture.imageContentsHash.GetHashCode() : 0);
                    h = h * 31 + (edit.decalSmooth ? 1 : 0);
                    h = h * 31 + (edit.decalNormal ? 1 : 0);
                }
                return h;
            }
        }

        /// <summary>
        /// 編集の画像の「置き方」: 画像の箱の位置・回転・大きさ・比率を保つ・ドラッグ中か（画像を入れていなければ何も畳まない）。
        /// 重ね貼り（DecalOverlayPreview）では、ここだけ変わったときは表示用メッシュを作り直さず、描く三角形・投影 UV・画像だけ置き直す（2026-10-06 計測）
        /// </summary>
        internal static int Placement(int h, RecolorEdit edit)
        {
            unchecked
            {
                if (edit == null || !edit.decalEnabled) return h;
                h = h * 31 + (edit.decalKeepAspect ? 1 : 0);
                // 置き方で割り付け方（平行投影／面に沿った展開）が変わる。シールから箱へは箱の値を変えずに切り替わるので、これが無いと作り直されない
                h = h * 31 + (edit.decalSticker ? 1 : 0);
                // 重ね貼りで表示している編集は、ドラッグ中は箱の値を畳まない（DecalOverlayPreview がメッシュを作り直さず投影 UV だけ書き換えて追従させる。
                // 毎フレーム作り直すと追従しない。実機 2026-10-04）。焼き込みの編集はドラッグ中も箱の値で層を作り直す
                if (!(BoxDragState.Decal.IsFor(edit) && BoxDragState.DecalIsOverlay))
                {
                    h = h * 31 + edit.decalBoxPosition.GetHashCode();
                    h = h * 31 + edit.decalBoxRotation.GetHashCode();
                    h = h * 31 + edit.decalBoxSize.GetHashCode();
                }
                // 箱の値の代わりに、複製した範囲の外へ出たかを入れる（出たら 1 回だけ範囲で絞らずに作り直させる）
                else h = h * 31 + (BoxDragState.DecalNeedsFullCopy ? 1 : 0);
                // ドラッグ中は層を低解像度で作るので、離した瞬間にフル解像度で作り直せるようドラッグ状態も含める
                h = h * 31 + (BoxDragState.Decal.IsFor(edit) ? 1 : 0);
                return h;
            }
        }

        /// <summary>
        /// 編集の「見た目」: 色・陰影・ガンマ・不透明度・打ち消す強さ・グラデーションの色と箱。
        /// 重ね貼り（DecalOverlayPreview）では、ここだけ変わったときは画像の色の段（DecalImageBuilder.ApplyLook）だけやり直す（ユーザー要望 2026-10-06）
        /// </summary>
        internal static int Look(int h, RecolorEdit edit)
        {
            unchecked
            {
                if (edit == null) return h;
                h = h * 31 + edit.targetColor.GetHashCode();
                h = h * 31 + (edit.hasTarget ? 1 : 0);
                h = h * 31 + edit.darkEndRatio.GetHashCode();
                h = h * 31 + edit.gamma.GetHashCode();
                h = h * 31 + edit.lightnessToTarget.GetHashCode();
                h = h * 31 + edit.chromaToTarget.GetHashCode();
                h = h * 31 + edit.hueRetain.GetHashCode();
                h = h * 31 + edit.strength.GetHashCode();
                h = h * 31 + edit.shadingStretch.GetHashCode();
                if (edit.flattenBase) h = h * 31 + edit.flattenStrength.GetHashCode();
                // グラデーション OFF のときは終了色・箱を変えても結果が変わらないので含めない（無駄に作り直さない）
                if (edit.gradientEnabled)
                {
                    h = h * 31 + edit.gradientColor.GetHashCode();
                    h = h * 31 + edit.gradientBoxPosition.GetHashCode();
                    h = h * 31 + edit.gradientBoxRotation.GetHashCode();
                    h = h * 31 + edit.gradientBoxSize.GetHashCode();
                    h = h * 31 + (edit.gradientInsideOnly ? 1 : 0);
                    h = h * 31 + edit.gradientDarkEndRatio.GetHashCode();
                    h = h * 31 + edit.gradientGamma.GetHashCode();
                    h = h * 31 + edit.gradientStrength.GetHashCode();
                }
                return h;
            }
        }
    }
}
