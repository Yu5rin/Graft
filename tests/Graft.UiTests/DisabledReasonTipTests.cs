using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests;

/// <summary>
/// C8: 押せないボタンに理由が出ない不具合の回帰テスト。無効でもツールチップが出て、
/// 無効のときだけ理由が先頭の1行に入る。
/// </summary>
public class DisabledReasonTipTests
{
    private static string TipText(Control c)
        => (ToolTip.GetTip(c) as TextBlock)?.Text ?? string.Empty;

    private static List<Button> Buttons(ShellHarness h, string automationName)
        => h.Window!.GetVisualDescendants().OfType<Button>()
            .Where(b => AutomationProperties.GetName(b) == automationName).ToList();

    [AvaloniaFact(DisplayName = "プロジェクト未選択では、適用・ファイル・プロンプトのボタンは「プロジェクトを選ぶと使えます」と理由を出す")]
    public async Task プロジェクト未選択の理由()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        var names = new[] { "適用を実行", "AIに渡すファイルを選ぶ", "既定の依頼文をコピー", "依頼文のテンプレートを選んでコピー" };

        // バインディングの反映は低い優先度で遅れるため、ツールチップが揃うまで待つ。
        // 以前は「適用を実行」だけを待っていたが、検証するのは4つのボタンすべてなので、
        // 待つ対象も4つすべてにする（待った1つが揃った時点で残りが反映途中の窓を作らない）。
        await ShellHarness.WaitUntilAsync(() => names.All(name =>
            Buttons(h, name).Count > 0
            && Buttons(h, name).All(b => TipText(b).StartsWith("プロジェクトを選ぶと使えます")))).ConfigureAwait(true);

        foreach (var name in names)
        {
            foreach (var b in Buttons(h, name))
            {
                b.IsEffectivelyEnabled.Should().BeFalse($"{name}は押せない状態のはず");
                ToolTip.GetShowOnDisabled(b).Should().BeTrue("無効でもツールチップを出す");
                TipText(b).Should().StartWith("プロジェクトを選ぶと使えます", name);
            }
        }
    }

    [AvaloniaFact(DisplayName = "プロジェクトを選んだが未解析のとき、適用・プレビュー・破棄・キューへ追加は「先に解析してください」を先頭に出す")]
    public async Task 未解析の理由()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();

        foreach (var name in new[] { "適用を実行", "適用できるかを再チェック", "解析結果を破棄", "現在の解析結果をパッチキューへ追加" })
        {
            var buttons = Buttons(h, name);
            buttons.Should().NotBeEmpty(name);
            foreach (var b in buttons)
            {
                TipText(b).Should().StartWith("先に解析してください", name);
            }
        }
        foreach (var b in Buttons(h, "失敗ブロックの修正依頼文をコピー"))
        {
            TipText(b).Should().StartWith("適用できなかったブロックがあるとき");
        }
    }

    [AvaloniaFact(DisplayName = "押せるようになったら理由は消え、通常の説明だけになる")]
    public async Task 有効なら理由は出ない()
    {
        using var h = await ShellHarness.CreateAsync(withWindow: true).ConfigureAwait(true);
        await File.WriteAllTextAsync(h.ProjectPath("a.txt"), "1行目\n2行目\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        await h.ParseAsync("a.txt", "2行目", "2行目（変更後）").ConfigureAwait(true);
        Dispatcher.UIThread.RunJobs();

        foreach (var b in Buttons(h, "適用を実行"))
        {
            b.IsEffectivelyEnabled.Should().BeTrue();
            TipText(b).Should().NotContain("先に解析してください").And.NotContain("プロジェクトを選ぶと");
            TipText(b).Should().NotBeEmpty();
        }
    }
}
