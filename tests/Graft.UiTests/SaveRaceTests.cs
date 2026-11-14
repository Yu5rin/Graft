using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.UiTests.TestSupport;

namespace Graft.UiTests;

/// <summary>
/// C5: 自分で保存した直後に「外部で変更されました」と出る不具合、および
/// 保存の書き込み中に打った文字まで保存済み扱いになる不具合の回帰テスト。
/// </summary>
public class SaveRaceTests
{
    [AvaloniaFact(DisplayName = "保存の書き込み中に打った文字は、保存済み扱いにならず未保存のまま残る")]
    public async Task 書き込み中の打鍵は未保存のまま残る()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var path = h.ProjectPath("a.txt");
        await File.WriteAllTextAsync(path, "abc\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var tab = (await h.Shell.Editor.OpenFileAsync(path).ConfigureAwait(true)).Value;
        tab.Session.Document.Insert(0, "X");
        tab.Session.IsModified.Should().BeTrue("前提: 保存前は未保存");

        // SaveAsyncは最初のawaitまでを同期的に実行し、保存する文字列をそこで確定する。
        // 書き込みの待ち中（awaitの間）にもう1文字打つ。
        var saving = tab.Session.SaveAsync();
        tab.Session.Document.Insert(0, "Y");
        var result = await saving.ConfigureAwait(true);

        result.IsSuccess.Should().BeTrue();
        (await File.ReadAllTextAsync(path).ConfigureAwait(true)).Should().Be("Xabc\n", "ディスクには保存を始めた時点の内容が入る");
        tab.Session.IsModified.Should().BeTrue("書き込み中に打った「Y」はまだ保存されていないので、未保存のままでなければならない");
    }

    [AvaloniaFact(DisplayName = "書き込み中に何も打たなければ、従来どおり保存済みになる")]
    public async Task 打鍵が無ければ保存済みになる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var path = h.ProjectPath("a.txt");
        await File.WriteAllTextAsync(path, "abc\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var tab = (await h.Shell.Editor.OpenFileAsync(path).ConfigureAwait(true)).Value;
        tab.Session.Document.Insert(0, "X");

        await tab.Session.SaveAsync().ConfigureAwait(true);

        tab.Session.IsModified.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "保存の直後に打鍵してから届いたファイル監視の通知は、外部の変更として扱わない")]
    public async Task 自分の保存は衝突にしない()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var path = h.ProjectPath("a.txt");
        await File.WriteAllTextAsync(path, "abc\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var tab = (await h.Shell.Editor.OpenFileAsync(path).ConfigureAwait(true)).Value;
        tab.Session.Document.Insert(0, "X");
        await tab.Session.SaveAsync().ConfigureAwait(true);

        // 保存の後、監視の待ち時間（250ms）の間に打鍵した。
        tab.Session.Document.Insert(0, "Y");
        tab.Session.IsModified.Should().BeTrue();
        await h.Shell.Editor.NotifyExternalChangeAsync(path).ConfigureAwait(true);

        tab.HasExternalConflict.Should().BeFalse("ディスクの内容は自分が保存したものと同じ。外部の変更ではない");
        tab.Session.Document.Text.Should().Be("YXabc\n", "打った文字は消えない");
    }

    [AvaloniaFact(DisplayName = "本当に外部で書き換えられたときは、これまでどおり衝突として扱う")]
    public async Task 本物の外部変更は衝突になる()
    {
        using var h = await ShellHarness.CreateAsync().ConfigureAwait(true);
        var path = h.ProjectPath("a.txt");
        await File.WriteAllTextAsync(path, "abc\n").ConfigureAwait(true);
        await h.RegisterProjectAsync().ConfigureAwait(true);
        var tab = (await h.Shell.Editor.OpenFileAsync(path).ConfigureAwait(true)).Value;
        tab.Session.Document.Insert(0, "X");
        await tab.Session.SaveAsync().ConfigureAwait(true);
        tab.Session.Document.Insert(0, "Y");

        await File.WriteAllTextAsync(path, "外部のエディタで書き換えた\n").ConfigureAwait(true);
        await h.Shell.Editor.NotifyExternalChangeAsync(path).ConfigureAwait(true);

        tab.HasExternalConflict.Should().BeTrue();
    }
}
