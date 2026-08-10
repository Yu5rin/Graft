using System;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;
using FluentAssertions;
using Graft.Views;

namespace Graft.UiTests.TestSupport;

/// <summary>
/// <see cref="ShellWindow"/>を<c>Show()</c>したあと、<c>OnLoaded</c>（保存済みレイアウトの
/// 反映を含む非同期の初期化処理）が完了するまで待ち合わせる共通ヘルパ。
///
/// ShellWindow.OnLoadedはGraft.InitializeAsync()（実ファイルI/Oを含む非同期処理）の完了を
/// 待ってからApplyLayoutToWindow（layout.jsonの内容をウィンドウ・ペインへ反映する処理）を
/// 呼ぶ非同期の経路のため、headlessテストがShow()直後にDispatcher.UIThread.RunJobs()を
/// 1回（あるいは数回）呼ぶだけでは、まだこの反映が終わっていないことがある
/// （実測で確認済み。CIの負荷次第でこの隙間はさらに広がる）。もともとGraftPanelPlacementTests
/// だけがこの待ち合わせをしており、ShellWindowSplitterTestsは待たずにRunJobs()だけで
/// 先へ進んでいたためCIで不定期に既定値を読んでしまっていた。並行実装を増やさないよう、
/// ShellWindow.IsLayoutAppliedが立つまで待つロジックをここへ一本化する。
/// </summary>
public static class ShellWindowLoadWaiter
{
    /// <summary>実時間ベースの上限。CIの負荷下でも十分な余裕を持たせつつ、
    /// 反映が本当に終わらない不具合が再発したときはテストとして確実に失敗させる。</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary><see cref="ShellWindow.IsLayoutApplied"/>が立つまで待つ。反復回数ではなく
    /// 実時間（既定5秒）を上限にするのは、CIの負荷でディスパッチャの1回あたりの処理時間が
    /// 伸びても、上限そのものが実質的に縮んでしまわないようにするため。</summary>
    public static void WaitForLayoutApplied(ShellWindow window, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var stopwatch = Stopwatch.StartNew();
        while (!window.IsLayoutApplied && stopwatch.Elapsed < limit)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        window.IsLayoutApplied.Should().BeTrue(
            $"ShellWindow.OnLoadedの初期化（保存済みレイアウトの反映）が{limit.TotalSeconds:F0}秒以内に完了しなかった");
    }
}
