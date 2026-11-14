using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaEdit;
using FluentAssertions;
using Graft.Core;
using Graft.Editor;
using Graft.Infra;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// B1の回帰テスト（エディタ側）: UTF-8でもShift_JISでもないファイル（EUC-JP・Windows-1252など）を
/// エディタで開いて保存すると、置換つきで読んだ内容が書き戻され、日本語がすべてU+FFFDに壊れた。
/// 開けるが保存できない（読み取り専用にして理由を示す）ようにする。また、Shift_JISのファイルに
/// Shift_JISで表せない文字を保存するときは、黙って「?」にせず確認を出す。
/// </summary>
public class EncodingReadOnlyTests : IDisposable
{
    static EncodingReadOnlyTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly ShownWindowTracker _windows = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-encoding-readonly", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _windows.Dispose();
        TempDirectoryCleanup.TryDeleteRecursive(_root);
        GC.SuppressFinalize(this);
    }

    // EUC-JP: 「// 日本語」＋改行＋「int x = 1;」。FC CB がShift_JISとして不正なので判定から外れる。
    private static readonly byte[] EucJp = new byte[] { 0x2F, 0x2F, 0x20, 0xC6, 0xFC, 0xCB, 0xDC, 0xB8, 0xEC, 0x0A }
        .Concat(Encoding.ASCII.GetBytes("int x = 1;\n")).ToArray();

    private sealed class FixedAnswerDialogService : IDialogService
    {
        private readonly bool _answer;
        public int ConfirmCount { get; private set; }

        public FixedAnswerDialogService(bool answer) => _answer = answer;

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmCount++;
            return Task.FromResult(_answer);
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(null);

        public Task<string?> PromptAsync(string title, string message, string? initial = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    private (EditorPaneViewModel Vm, TextEditor Editor, Border Bar, Window Window) OpenPane(IDialogService dialogs)
    {
        Directory.CreateDirectory(_root);
        var vm = new EditorPaneViewModel(new Settings(), dialogs, new AvaloniaUiServices());
        vm.SetProject(_root);
        var pane = new EditorPane { DataContext = vm };
        var window = _windows.Track(new Window { Width = 800, Height = 400, Content = pane });
        window.Show();
        var editor = pane.GetVisualDescendants().OfType<TextEditor>().Single();
        var bar = pane.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "EncodingReadOnlyBar");
        return (vm, editor, bar, window);
    }

    [AvaloniaFact(DisplayName = "B1: 文字コードを判定できないファイルは、開けるが読み取り専用で、通知バーが出て、保存はE218で止まる")]
    public async Task 判定できないファイルは読み取り専用で保存できない()
    {
        var (vm, editor, bar, window) = OpenPane(new FixedAnswerDialogService(true));
        var path = Path.Combine(_root, "code.txt");
        await File.WriteAllBytesAsync(path, EucJp);

        var opened = await vm.OpenFileAsync(path).ConfigureAwait(true);
        opened.IsSuccess.Should().BeTrue("読むだけなら開ける");
        var tab = opened.Value;
        using (window.CaptureRenderedFrame()) { }

        tab.IsEncodingReadOnly.Should().BeTrue();
        editor.IsReadOnly.Should().BeTrue("編集できないようにする");
        bar.IsVisible.Should().BeTrue("理由を通知バーで示す");
        vm.EncodingText.Should().Contain("読み取り専用");

        var saved = await tab.Session.SaveAsync().ConfigureAwait(true);
        saved.IsSuccess.Should().BeFalse();
        saved.Issues.Should().Contain(i => i.Code == ErrorCode.E218);
        (await File.ReadAllBytesAsync(path)).Should().Equal(EucJp, "保存を止めたのでファイルは1バイトも変わらない");
    }

    [AvaloniaFact(DisplayName = "B1: UTF-8のファイルは従来どおり編集でき、通知バーも出ない")]
    public async Task UTF8は従来どおり編集できる()
    {
        var (vm, editor, bar, window) = OpenPane(new FixedAnswerDialogService(true));
        var path = Path.Combine(_root, "ok.txt");
        await File.WriteAllTextAsync(path, "日本語\n").ConfigureAwait(true);

        var opened = await vm.OpenFileAsync(path).ConfigureAwait(true);
        using (window.CaptureRenderedFrame()) { }

        opened.Value.IsEncodingReadOnly.Should().BeFalse();
        editor.IsReadOnly.Should().BeFalse();
        bar.IsVisible.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "B1: Shift_JISで表せない文字の保存は確認を出し、了承すれば保存し、断れば保存しない")]
    public async Task 表せない文字の保存は確認を出す()
    {
        var sjis = Encoding.GetEncoding(932);
        foreach (var answer in new[] { true, false })
        {
            var dialogs = new FixedAnswerDialogService(answer);
            var (vm, _, _, _) = OpenPane(dialogs);
            var path = Path.Combine(_root, $"sjis-{answer}.txt");
            var original = sjis.GetBytes("日本語\r\n");
            await File.WriteAllBytesAsync(path, original);

            var opened = await vm.OpenFileAsync(path).ConfigureAwait(true);
            var session = opened.Value.Session;
            session.Document.Insert(session.Document.TextLength, "祝🎉");

            var saved = await session.SaveAsync().ConfigureAwait(true);

            dialogs.ConfirmCount.Should().Be(1, "表せない文字があるので確認を出す");
            saved.IsSuccess.Should().Be(answer);
            if (answer)
            {
                sjis.GetString(await File.ReadAllBytesAsync(path)).Should().Contain("祝?", "了承したので、これまでどおり?に置き換わって保存される");
            }
            else
            {
                saved.Issues.Should().Contain(i => i.Code == ErrorCode.E219);
                (await File.ReadAllBytesAsync(path)).Should().Equal(original, "断ったのでファイルは変わらない");
            }
        }
    }

    [AvaloniaFact(DisplayName = "B1: Shift_JISで表せる文字だけなら確認を出さずに保存する")]
    public async Task 表せる文字なら確認なし()
    {
        var dialogs = new FixedAnswerDialogService(false);
        var (vm, _, _, _) = OpenPane(dialogs);
        var path = Path.Combine(_root, "sjis-ok.txt");
        await File.WriteAllBytesAsync(path, Encoding.GetEncoding(932).GetBytes("日本語\r\n"));

        var opened = await vm.OpenFileAsync(path).ConfigureAwait(true);
        var session = opened.Value.Session;
        session.Document.Insert(session.Document.TextLength, "追記①");

        var saved = await session.SaveAsync().ConfigureAwait(true);

        saved.IsSuccess.Should().BeTrue();
        dialogs.ConfirmCount.Should().Be(0);
    }
}
