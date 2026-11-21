using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core.Vba;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// 「Excel ブックから追加」の選択画面の回帰テスト。
/// 不具合 3: ミラーの場所に「ドキュメント」のようなミラーでないフォルダを選んでも、「既存のミラー」とは見なさず、
/// 「作り直す」も選べない（修正前は何か 1 つでもあれば既存のミラー扱いで、作り直しでフォルダごとごみ箱へ送った）。
/// </summary>
public class VbaFixPickerTests
{
    private const string Book = @"C:\Work\計画.xlsm";

    private static async Task<(ShellHarness H, FakeVbaBridge Bridge, VbaWorkbookPickerViewModel Picker)> OpenPickerAsync()
    {
        var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = new FakeVbaBridge();
        bridge.AddWorkbook(Book, FakeVbaBridge.Std("Module1", "x"));
        h.Shell.Graft.VbaBridge = bridge;
        var picker = new VbaWorkbookPickerViewModel(h.Shell.Graft.VbaSync, h.Dialogs);
        await picker.LoadAsync().ConfigureAwait(true);
        return (h, bridge, picker);
    }

    [AvaloniaFact(DisplayName = "不具合3 選択画面: ミラーでないフォルダ（家計簿.xlsx と写真/）を選んでも「既存のミラー」は出ず、作り直しは立てられない。確定すると中に新しく作る")]
    public async Task ミラーでないフォルダは既存のミラーにならない()
    {
        var (h, _, picker) = await OpenPickerAsync().ConfigureAwait(true);
        using var harness = h;
        var folder = Path.Combine(h.Root, "Documents");
        Directory.CreateDirectory(Path.Combine(folder, "写真"));
        File.WriteAllText(Path.Combine(folder, "家計簿.xlsx"), "dummy");

        picker.MirrorFolder = folder;
        picker.RebuildMirror = true;

        picker.ExistingMirrorFound.Should().BeFalse();
        picker.CanRebuildMirror.Should().BeFalse();
        picker.RebuildMirror.Should().BeFalse("作り直しは立てられない");

        (await picker.ConfirmAsync().ConfigureAwait(true)).Should().BeTrue();
        File.Exists(Path.Combine(folder, "家計簿.xlsx")).Should().BeTrue("既存のファイルは消えない");
        File.Exists(Path.Combine(folder, "Modules", "Module1.bas")).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "不具合3 選択画面: ミラーのファイルとミラーでないファイルが混じるフォルダは、既存のミラーとして使えるが、作り直しは選べない")]
    public async Task 混在したフォルダは作り直せない()
    {
        var (h, _, picker) = await OpenPickerAsync().ConfigureAwait(true);
        using var harness = h;
        var folder = Path.Combine(h.Root, "repo");
        Directory.CreateDirectory(Path.Combine(folder, "Modules"));
        File.WriteAllText(Path.Combine(folder, "Modules", "Module1.bas"), "x\r\n");
        File.WriteAllText(Path.Combine(folder, "README.md"), "hello");

        picker.MirrorFolder = folder;
        picker.RebuildMirror = true;

        picker.ExistingMirrorFound.Should().BeTrue();
        picker.CanRebuildMirror.Should().BeFalse();
        picker.RebuildMirror.Should().BeFalse();
        picker.ReuseExistingMirror.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "不具合3 選択画面: ミラーだけのフォルダは従来どおり作り直しを選べる")]
    public async Task ミラーだけのフォルダは作り直せる()
    {
        var (h, _, picker) = await OpenPickerAsync().ConfigureAwait(true);
        using var harness = h;
        var folder = Path.Combine(h.Root, "mirror");
        Directory.CreateDirectory(Path.Combine(folder, "Modules"));
        File.WriteAllText(Path.Combine(folder, "Modules", "Module1.bas"), "x\r\n");

        picker.MirrorFolder = folder;
        picker.RebuildMirror = true;

        picker.ExistingMirrorFound.Should().BeTrue();
        picker.CanRebuildMirror.Should().BeTrue();
        picker.RebuildMirror.Should().BeTrue();
    }
}
