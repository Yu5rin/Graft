using FluentAssertions;
using Graft.Platform.Windows;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 自動更新のあと、再起動する前に、ブックの開閉の調べ直しが <c>Microsoft.CSharp</c> を読み込めず失敗した実機の報告への対策
/// （<see cref="DynamicBinderWarmup"/>）。部品が見つからなくなる仕組みは Linux では再現できないので、ここでは
/// 「起動時に動的バインダーを読み込ませる処理が、例外を出さず、必要なアセンブリを読み込み、何度呼んでも安全」なことを確かめる。
/// </summary>
public class DynamicBinderWarmupTests
{
    [Fact(DisplayName = "動的バインダーの先読み: 例外を出さずに終わり、Microsoft.CSharp が読み込まれている")]
    public void 先読みで動的バインダーが読み込まれる()
    {
        var act = () => DynamicBinderWarmup.Run();

        act.Should().NotThrow("先読みの失敗で起動を妨げてはいけない");
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .Should().Contain("Microsoft.CSharp", "COM 呼び出しの dynamic が使う動的バインダーを先に読み込んでおくのが目的");
    }

    [Fact(DisplayName = "動的バインダーの先読み: 背景での開始は何度呼んでも安全で、呼び出し元を待たせない")]
    public void 背景での開始は何度呼んでも安全()
    {
        var act = () =>
        {
            DynamicBinderWarmup.StartInBackground();
            DynamicBinderWarmup.StartInBackground();
            DynamicBinderWarmup.StartInBackground();
        };

        act.Should().NotThrow();
    }
}
