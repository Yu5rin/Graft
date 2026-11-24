using System.IO;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Infra;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// Excel ブックをプロジェクト一覧へドラッグ＆ドロップして、VBA のプロジェクトとして追加する流れ（1.3.1。仕様書_VBA連携 7.1b）。
/// 実際のドラッグ操作は合成せず、<see cref="ProjectPaneViewModel.HandleDropAsync"/>（OnDrop が呼ぶ入口）を、
/// <see cref="FakeVbaBridge"/> と台本の選択画面で通す。振り分けの細部は <c>ProjectDropPlanTests</c>。
/// </summary>
public class VbaDropAddTests
{
    private const string Book = @"C:\Work\計画.xlsm";
    private const string OtherBook = @"C:\Work\別.xlsm";

    private static FakeVbaBridge NewBridge()
    {
        var bridge = new FakeVbaBridge();
        bridge.AddWorkbook(Book, FakeVbaBridge.Std("Module1", "Sub A()\r\nEnd Sub"));
        bridge.AddWorkbook(OtherBook, FakeVbaBridge.Std("Module2", "Sub B()\r\nEnd Sub"));
        return bridge;
    }

    /// <summary>選択画面の台本。開いたときの選択を記録し、そのまま確定する。</summary>
    private sealed class PickerScript
    {
        public int Opened { get; private set; }

        public string? SelectedOnOpen { get; private set; }

        public bool SelectedWasSelectable { get; private set; }

        public void Attach(ProjectPaneViewModel pane)
            => pane.ShowWorkbookPickerAsync = async picker =>
            {
                Opened++;
                SelectedOnOpen = picker.SelectedWorkbook?.FullName;
                SelectedWasSelectable = picker.SelectedWorkbook?.IsSelectable ?? false;
                return await picker.ConfirmAsync().ConfigureAwait(true);
            };
    }

    [AvaloniaFact(DisplayName = "ブックを落とすと、選択画面がそのブックを選んだ状態で開き、確定すると VBA のプロジェクトとして登録される（親フォルダは登録しない）")]
    public async Task 開いているブックを落とすと登録される()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        h.Shell.Graft.VbaBridge = NewBridge();
        var pane = h.Shell.Graft.ProjectPane;
        var script = new PickerScript();
        script.Attach(pane);

        // 一覧の先頭は「別.xlsm」（名前順）。既定の選択ではなく、落としたブックの方を選んで開くことを確かめる。
        await pane.HandleDropAsync(new string?[] { @"c:\work\.\計画.XLSM" }).ConfigureAwait(true);

        script.Opened.Should().Be(1);
        script.SelectedOnOpen.Should().Be(Book, "落としたブックを選んだ状態で開く（パスの比較は大文字小文字・区切りを無視）");
        pane.Items.Should().ContainSingle();
        pane.SelectedItem!.Project.Vba!.WorkbookFullName.Should().Be(Book);
        h.Shell.Graft.Notice.Text.Should().Contain("追加しました");
    }

    [AvaloniaFact(DisplayName = "落としたブックが Excel で開かれていないとき: 選択画面は開かず、知らせるだけ。何も登録されない")]
    public async Task 開いていないブックは知らせるだけ()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        h.Shell.Graft.VbaBridge = bridge;
        var pane = h.Shell.Graft.ProjectPane;
        var script = new PickerScript();
        script.Attach(pane);

        await pane.HandleDropAsync(new string?[] { @"C:\Work\未起動.xlsm" }).ConfigureAwait(true);

        script.Opened.Should().Be(0);
        h.Shell.Graft.Notice.Text.Should().Be("未起動.xlsm は Excel で開かれていません。Excel で開いてから、もう一度落としてください。");
        pane.Items.Should().BeEmpty();
        bridge.Calls.Should().OnlyContain(c => c == "List", "起動中のブックの一覧を見るだけ。Graft から Excel でブックを開くことはしない");
    }

    [AvaloniaFact(DisplayName = "一覧にあるが使えないブック（保護ビュー）: 選択画面を開き、そのブックを選んだ状態にして、理由は既存の表示に任せる")]
    public async Task 使えないブックは画面で理由を見せる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var bridge = NewBridge();
        var book = bridge.Get(Book)!;
        book.Availability = VbaWorkbookAvailability.ProtectedView;
        book.UnavailableReason = "保護ビューです。";
        h.Shell.Graft.VbaBridge = bridge;
        var pane = h.Shell.Graft.ProjectPane;
        var script = new PickerScript();
        script.Attach(pane);

        await pane.HandleDropAsync(new string?[] { Book }).ConfigureAwait(true);

        script.Opened.Should().Be(1);
        script.SelectedOnOpen.Should().Be(Book);
        script.SelectedWasSelectable.Should().BeFalse();
        pane.Items.Should().BeEmpty("確定できないので登録されない");
    }

    [AvaloniaFact(DisplayName = ".xlsx を落とすと、マクロを保存できないと知らせる。選択画面は開かず、親フォルダも登録しない")]
    public async Task Xlsxは知らせるだけ()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        h.Shell.Graft.VbaBridge = NewBridge();
        var pane = h.Shell.Graft.ProjectPane;
        var script = new PickerScript();
        script.Attach(pane);
        var xlsx = Path.Combine(h.ProjectDirectory, "表.xlsx");
        File.WriteAllText(xlsx, "x");

        await pane.HandleDropAsync(new string?[] { xlsx }).ConfigureAwait(true);

        script.Opened.Should().Be(0);
        h.Shell.Graft.Notice.Text.Should().Contain("表.xlsx").And.Contain("マクロを保存できない形式（.xlsx）").And.Contain(".xlsm で保存し直してください");
        pane.Items.Should().BeEmpty("親フォルダも登録しない");
    }

    [AvaloniaFact(DisplayName = "VBA 連携が設定でオフ: ブックのファイルも従来どおり親フォルダを登録し、選択画面は開かない")]
    public async Task 連携オフなら従来どおり親フォルダ()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        h.Shell.Graft.VbaBridge = NewBridge();
        h.Shell.Graft.UpdateSettings(h.Shell.Graft.CurrentSettings with { Vba = new VbaSettings { Enabled = false } });
        var pane = h.Shell.Graft.ProjectPane;
        var script = new PickerScript();
        script.Attach(pane);
        var book = Path.Combine(h.ProjectDirectory, "計画.xlsm");
        File.WriteAllText(book, "x");

        await pane.HandleDropAsync(new string?[] { book }).ConfigureAwait(true);

        script.Opened.Should().Be(0);
        pane.Items.Should().ContainSingle().Which.Project.Root.Should().Be(h.ProjectDirectory);
        pane.SelectedItem!.Project.Vba.Should().BeNull();
    }

    [AvaloniaFact(DisplayName = "Null 環境（Linux）: ブックのファイルも従来どおり親フォルダを登録する")]
    public async Task Null環境では従来どおり親フォルダ()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var pane = h.Shell.Graft.ProjectPane;
        var book = Path.Combine(h.ProjectDirectory, "計画.xlsm");
        File.WriteAllText(book, "x");

        await pane.HandleDropAsync(new string?[] { book }).ConfigureAwait(true);

        pane.Items.Should().ContainSingle().Which.Project.Root.Should().Be(h.ProjectDirectory);
    }

    [AvaloniaFact(DisplayName = "フォルダとブックを一緒に落とす: フォルダは登録し、ブックは選択画面で扱う。2 冊以上なら先頭の 1 冊だけで、そのことを知らせる")]
    public async Task 複数を落とす()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        h.Shell.Graft.VbaBridge = NewBridge();
        var pane = h.Shell.Graft.ProjectPane;
        var script = new PickerScript();
        script.Attach(pane);

        await pane.HandleDropAsync(new string?[] { h.ProjectDirectory, OtherBook, Book }).ConfigureAwait(true);

        script.Opened.Should().Be(1, "ブックは先頭の 1 冊だけ");
        script.SelectedOnOpen.Should().Be(OtherBook);
        pane.Items.Should().HaveCount(2, "フォルダと、ブック 1 冊");
        pane.Items.Should().Contain(i => i.Project.Root == h.ProjectDirectory);
        pane.Items.Should().Contain(i => i.Project.Vba != null && i.Project.Vba.WorkbookFullName == OtherBook);
        h.Dialogs.Messages.Should().ContainSingle().Which.Message.Should().Contain("ブックは1冊ずつ追加します");
    }
}
