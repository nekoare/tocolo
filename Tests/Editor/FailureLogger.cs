using NUnit.Framework.Interfaces;
using UnityEngine;
using UnityEngine.TestRunner;

[assembly: TestRunCallback(typeof(Nekoare.ClickRecolor.Tests.FailureLogger))]

namespace Nekoare.ClickRecolor.Tests
{
    /// <summary>
    /// 失敗したテストの名前・メッセージ・スタックを Editor.log に書き出す。
    /// 通常の assert 失敗は Test Runner の画面にしか出ず Editor.log に残らないので、
    /// WSL 側からログだけで原因を追えるようにするための補助（テストの結果には影響しない）
    /// </summary>
    public sealed class FailureLogger : ITestRunCallback
    {
        public void RunStarted(ITest testsToRun) { }

        public void TestStarted(ITest test) { }

        public void TestFinished(ITestResult result)
        {
            if (result.Test.IsSuite) return;
            if (result.ResultState.Status != TestStatus.Failed) return;
            Debug.Log($"[ClickRecolor Tests] FAILED {result.Test.FullName}\n{result.Message}\n{result.StackTrace}");
        }

        public void RunFinished(ITestResult testResults)
        {
            Debug.Log($"[ClickRecolor Tests] finished: passed={testResults.PassCount} failed={testResults.FailCount} skipped={testResults.SkipCount}");
        }
    }
}
