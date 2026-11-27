using System;
using System.Globalization;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Networking;

namespace Nekoare.ClickRecolor.Editor.UpdateCheck
{
    /// <summary>
    /// 1 日 1 回、公開中の VPM listing を読んで新しい版があるか確かめる。送る情報は無く、受け取った内容は版番号としてだけ使う。
    /// 結果は EditorPrefs（PC 全体）に置く。プロジェクトごとに置くと、複数のプロジェクトで同じ通知を何度も出すことになる
    /// </summary>
    [InitializeOnLoad]
    internal static class UpdateChecker
    {
        private const string PackageName = "dev.nekoare.click-recolor";
        private const string ListingUrl = "https://nekoare.github.io/vpm-listing/tocolo-trial/index.json";
        private const string MenuPath = "Tools/Tocolo/更新を確認する";

        private const string EnabledKey = "Tocolo_UpdateCheck_Enabled";
        private const string LatestKey = "Tocolo_UpdateCheck_Latest";
        private const string LastCheckedKey = "Tocolo_UpdateCheck_LastChecked";
        private const string SkippedKey = "Tocolo_UpdateCheck_Skipped";

        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
        private const int TimeoutSeconds = 10;
        private const int MaxResponseBytes = 1024 * 1024;

        private static bool s_requesting;

        /// <summary>確認の通信中か（通信が終わるまでお知らせを出さない）</summary>
        public static bool Requesting => s_requesting;
        private static bool s_currentVersionResolved;
        private static PackageVersion? s_currentVersion;

        static UpdateChecker()
        {
            EditorApplication.delayCall += CheckIfDue;
        }

        // 選ぶまでは通信しない（既定オフ）。利用者が予想しない通信を、同意なしに始めないため。
        // 「選んだか」は別のキーを持たず Enabled のキーがあるかで見分ける（2 つに分けると、片方だけ書かれた状態が生まれる）
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(EnabledKey, false);
            set => EditorPrefs.SetBool(EnabledKey, value);
        }

        public static bool HasChosen => EditorPrefs.HasKey(EnabledKey);

        public static void Choose(bool enable)
        {
            Enabled = enable;
            if (enable)
            {
                EditorPrefs.DeleteKey(LastCheckedKey);
                CheckIfDue();
            }
            InternalEditorUtility.RepaintAllViews();
        }

        /// <summary>入っている Tocolo の版。VPM 以外（Assets に置いた等）で入れていて取れないときは null</summary>
        public static PackageVersion? CurrentVersion
        {
            get
            {
                if (!s_currentVersionResolved)
                {
                    s_currentVersionResolved = true;
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UpdateChecker).Assembly);
                    s_currentVersion = info != null && PackageVersion.TryParse(info.version, out var v) ? v : (PackageVersion?)null;
                }
                return s_currentVersion;
            }
        }

        public static PackageVersion? LatestVersion => ReadVersion(LatestKey);

        public static PackageVersion? SkippedVersion
        {
            get => ReadVersion(SkippedKey);
            set => EditorPrefs.SetString(SkippedKey, value?.ToString() ?? "");
        }

        private static PackageVersion? ReadVersion(string key)
            => PackageVersion.TryParse(EditorPrefs.GetString(key, ""), out var v) ? v : (PackageVersion?)null;

        private static void CheckIfDue()
        {
            if (!Enabled || s_requesting || Application.isBatchMode) return;
            if (CurrentVersion == null) return;
            if (!IsDue()) return;

            s_requesting = true;
            var request = UnityWebRequest.Get(ListingUrl);
            request.timeout = TimeoutSeconds;
            request.SendWebRequest().completed += _ => OnCompleted(request);
        }

        private static bool IsDue()
        {
            if (!long.TryParse(EditorPrefs.GetString(LastCheckedKey, ""), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return true;
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return true;
            var elapsed = DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc);
            // 時計が戻された（前回が未来になっている）ときは待たずに確かめる
            return elapsed < TimeSpan.Zero || elapsed >= CheckInterval;
        }

        // 確認時刻は通信が終わってから記録する。送る前に記録すると、起動直後のドメインリロードで通信が打ち切られたときに 24 時間確認されなくなる。
        // 失敗（オフライン等）でも記録するのは、再コンパイルのたびに通信し直さないため。失敗は Console に出さない（オフラインの人に毎回出てしまう）
        private static void OnCompleted(UnityWebRequest request)
        {
            try
            {
                EditorPrefs.SetString(LastCheckedKey, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                if (request.result != UnityWebRequest.Result.Success) return;
                var data = request.downloadHandler.data;
                if (data == null || data.Length > MaxResponseBytes) return;

                var latest = UpdateCheckLogic.PickLatest(request.downloadHandler.text, PackageName, Application.unityVersion);
                if (latest == null) return;
                EditorPrefs.SetString(LatestKey, latest.Value.ToString());
                InternalEditorUtility.RepaintAllViews();
            }
            catch (Exception)
            {
                // お知らせは補助機能なので、ここで何が起きても Console に出さない
            }
            finally
            {
                s_requesting = false;
                request.Dispose();
            }
        }

        [MenuItem(MenuPath, false, 2000)]
        private static void ToggleEnabled() => Choose(!Enabled);

        [MenuItem(MenuPath, true)]
        private static bool ToggleEnabledValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }
    }
}
