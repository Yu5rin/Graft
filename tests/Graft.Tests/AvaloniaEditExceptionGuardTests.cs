using System;
using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 不具合1（AvaloniaEdit内部の未処理例外でアプリが落ちる不具合）の回帰テスト。
/// <see cref="AvaloniaEditExceptionGuard.ShouldContinue"/>は、UIスレッドの未処理例外を
/// 「AvaloniaEdit由来（このアプリの外のコード）と判定できる場合に限りアプリを継続させる」
/// という純粋な判定ロジックのみを担う（App.axaml.cs側のDispatcher.UIThread.UnhandledException
/// 配線はAvalonia型に依存するためheadless UIテスト側で検証する）。
/// </summary>
public class AvaloniaEditExceptionGuardTests
{
    [Fact(DisplayName = "SourceがAvaloniaEditの例外は継続してよい（true）")]
    public void AvaloniaEdit由来の例外は継続してよい()
    {
        var ex = new ArgumentException("Invalid document");
        ex.Source = "AvaloniaEdit";

        AvaloniaEditExceptionGuard.ShouldContinue(ex).Should().BeTrue();
    }

    [Fact(DisplayName = "Sourceがこのアプリ自身（Graft）の例外は継続させない（false）")]
    public void このアプリ自身の例外は継続させない()
    {
        var ex = new InvalidOperationException("想定外の状態");
        ex.Source = "Graft";

        AvaloniaEditExceptionGuard.ShouldContinue(ex).Should().BeFalse();
    }

    [Fact(DisplayName = "Sourceが未設定（null）の例外は継続させない（false、安全側）")]
    public void Source未設定の例外は継続させない()
    {
        var ex = new InvalidOperationException("Sourceが設定されていないケース");

        AvaloniaEditExceptionGuard.ShouldContinue(ex).Should().BeFalse();
    }

    [Fact(DisplayName = "nullを渡すとArgumentNullExceptionになる")]
    public void nullを渡すと例外になる()
    {
        var act = () => AvaloniaEditExceptionGuard.ShouldContinue(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
