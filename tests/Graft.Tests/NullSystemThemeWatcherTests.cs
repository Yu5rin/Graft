using FluentAssertions;
using Graft.Platform;
using Graft.Platform.Null;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// <see cref="NullSystemThemeWatcher"/>の契約を検証する。テーマ・ハイコントラストいずれも
/// 「判定できない」を意味するnullを返し、呼び出し側（Themes.ThemeManager）を安全な既定
/// （ダーク・ハイコントラストなし扱い）へフォールバックさせる（依頼3の追加分。仕様書2.3・9.3）。
/// </summary>
public class NullSystemThemeWatcherTests
{
    [Fact(DisplayName = "IsSupportedはfalseで、理由付きの文言を持つ")]
    public void 利用不可を表明する()
    {
        var watcher = new NullSystemThemeWatcher();
        watcher.IsSupported.Should().BeFalse();
        watcher.UnsupportedReason.Should().NotBeNullOrEmpty();
    }

    [Fact(DisplayName = "TryReadIsLightThemeは常にnull（判定不能）を返す")]
    public void ライトテーマ判定は常にnull()
    {
        new NullSystemThemeWatcher().TryReadIsLightTheme().Should().BeNull();
    }

    [Fact(DisplayName = "依頼3: TryReadIsHighContrastは常にnull（判定不能）を返す")]
    public void ハイコントラスト判定は常にnull()
    {
        new NullSystemThemeWatcher().TryReadIsHighContrast().Should().BeNull();
    }

    [Fact(DisplayName = "StartWatching/StopWatching/Disposeを呼んでも例外にならない（何もしない実装）")]
    public void 監視の開始停止破棄は何もしない()
    {
        var watcher = new NullSystemThemeWatcher();
        var act = () =>
        {
            watcher.StartWatching();
            watcher.StopWatching();
            watcher.Dispose();
        };
        act.Should().NotThrow();
    }

    [Fact(DisplayName = "ISystemThemeWatcherとして扱っても同じ結果になる（インターフェース経由の呼び出し確認）")]
    public void インターフェース経由でも同じ結果になる()
    {
        ISystemThemeWatcher watcher = new NullSystemThemeWatcher();
        watcher.TryReadIsLightTheme().Should().BeNull();
        watcher.TryReadIsHighContrast().Should().BeNull();
    }
}
