using System.Text;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// プロンプトのコピー（<see cref="PromptCopyViewModel"/>）のテスト。次の2点を固定する。
///
/// 1. 不具合: <c>BuildRequest</c> が <see cref="ContextRequest.SinceRevision"/> を渡していなかったため、
///    コンテキスト収集が「差分のみ」のとき <c>{{files}}</c> が常に空になっていた。
/// 2. 改善: <c>{{files}}</c> を含まないテンプレート（既定の「初回用（完全版）」など）でも、
///    「選んだファイルも付ける」をオンにすれば、1回のコピーで「指示文＋選んだファイル」を渡せる。
///    オフ（既定）なら従来どおり指示文だけ。<c>{{files}}</c> を含むテンプレートでは二重に付かない。
/// </summary>
public class PromptCopyAppendFilesTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-promptcopyappend", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // 不具合: SinceRevision の受け渡し漏れ
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "収集モードが「差分のみ」でも、プロンプトの{{files}}に変更のあったファイルが入る（SinceRevisionの受け渡し漏れの修正）")]
    public async Task 差分のみモードでもfilesが埋まる()
    {
        var h = await BuildAsync("since", ws =>
        {
            ws.WriteText("changed.py", "CHANGED_MARKER = 1\n");
            ws.WriteText("untouched.py", "UNTOUCHED_MARKER = 2\n");
        });
        // r1 の後に r2 で changed.py が変更された、という履歴を用意する。
        WriteRevision(h, 1, "初回", Array.Empty<string>());
        WriteRevision(h, 2, "changed.pyを変更", new[] { "changed.py" });

        h.Context.SelectedMode = ContextMode.ChangedSince;
        await WaitUntilAsync(() => h.Context.Revisions.Count == 2);
        h.Context.SelectedRevision = h.Context.Revisions.Single(r => r.Revision == 1);

        var copied = await CopyAsync(h, "builtin-fix-request"); // 本文に {{files}} を含む組み込みテンプレート

        copied.Should().Contain("CHANGED_MARKER", "r1より後に変更されたファイルが{{files}}に入るはず");
        copied.Should().NotContain("UNTOUCHED_MARKER", "変更の無かったファイルは入らない");
    }

    [AvaloniaFact(DisplayName = "コンテキスト収集の窓のコピーとプロンプトの{{files}}は、差分のみモードで同じファイルを出す")]
    public async Task 差分のみモードで窓のコピーと一致する()
    {
        var h = await BuildAsync("since-same", ws =>
        {
            ws.WriteText("changed.py", "CHANGED_MARKER = 1\n");
            ws.WriteText("untouched.py", "UNTOUCHED_MARKER = 2\n");
        });
        WriteRevision(h, 1, "初回", Array.Empty<string>());
        WriteRevision(h, 2, "changed.pyを変更", new[] { "changed.py" });
        h.Context.SelectedMode = ContextMode.ChangedSince;
        await WaitUntilAsync(() => h.Context.Revisions.Count == 2);
        h.Context.SelectedRevision = h.Context.Revisions.Single(r => r.Revision == 1);

        h.Context.CopyCommand.Execute(null);
        await WaitUntilAsync(() => !h.Context.CopyCommand.IsExecuting && h.Clipboard.Text is not null);
        var fromWindow = h.Clipboard.Text!;
        var fromPrompt = await CopyAsync(h, "builtin-fix-request");

        fromWindow.Should().Contain("CHANGED_MARKER").And.NotContain("UNTOUCHED_MARKER");
        fromPrompt.Should().Contain("CHANGED_MARKER").And.NotContain("UNTOUCHED_MARKER");
    }

    // ------------------------------------------------------------------
    // 改善: 「選んだファイルも付ける」
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "既定ではオフで、{{files}}を含まないテンプレートは従来どおり指示文だけがコピーされる")]
    public async Task 既定ではオフで指示文だけ()
    {
        var h = await BuildAsync("default-off", ws => ws.WriteText("main.py", "MAIN_MARKER = 1\n"));

        h.Prompt.AppendSelectedFiles.Should().BeFalse("利用者の習慣を急に変えないため、既定はオフ");
        var copied = await CopyAsync(h, "builtin-full");

        copied.Should().Contain("SEARCH/REPLACE");
        copied.Should().NotContain("MAIN_MARKER", "オフなら従来どおりファイルは付かない");
        copied.Should().NotContain("# 対象ファイル");
    }

    [AvaloniaFact(DisplayName = "オンにすると、{{files}}を含まないテンプレートでも指示文の後ろに選んだファイルが付く（1回のコピーで渡せる）")]
    public async Task オンで指示文の後ろにファイルが付く()
    {
        var h = await BuildAsync("on", ws =>
        {
            ws.WriteText("main.py", "MAIN_MARKER = 1\n");
            ws.WriteText("other.py", "OTHER_MARKER = 2\n");
        });
        h.Context.CycleStateCommand.Execute(h.Context.Files.Single(f => f.RelativePath == "other.py")); // 構成だけ
        h.Prompt.AppendSelectedFiles = true;

        var copied = await CopyAsync(h, "builtin-full");

        var instructionAt = copied.IndexOf("SEARCH/REPLACE", StringComparison.Ordinal);
        var fileAt = copied.IndexOf("MAIN_MARKER", StringComparison.Ordinal);
        instructionAt.Should().BeGreaterThanOrEqualTo(0);
        fileAt.Should().BeGreaterThan(instructionAt, "ファイルは指示文の後ろに付く");
        copied.Should().NotContain("OTHER_MARKER", "「内容も出す」でないファイルの中身は付かない");
    }

    [AvaloniaFact(DisplayName = "{{files}}を含むテンプレートでは、オンでもファイルが二重に付かない")]
    public async Task files変数を含むテンプレートでは二重に付かない()
    {
        var h = await BuildAsync("no-dup", ws => ws.WriteText("main.py", "MAIN_MARKER = 1\n"));

        var off = await CopyAsync(h, "builtin-fix-request");
        h.Prompt.AppendSelectedFiles = true;
        var on = await CopyAsync(h, "builtin-fix-request");

        CountOccurrences(off, "MAIN_MARKER").Should().Be(1);
        CountOccurrences(on, "MAIN_MARKER").Should().Be(1, "{{files}}の位置にだけ入り、末尾に重ねて付かない");
        CountOccurrences(on, "# 対象ファイル").Should().Be(1);
    }

    [AvaloniaFact(DisplayName = "オンにしても、組み込みテンプレートの本文は書き換わらない")]
    public async Task テンプレートの本文は書き換わらない()
    {
        var h = await BuildAsync("body", ws => ws.WriteText("main.py", "MAIN_MARKER = 1\n"));
        h.Prompt.AppendSelectedFiles = true;

        await CopyAsync(h, "builtin-full");

        foreach (var option in h.Prompt.Templates)
        {
            var original = PromptTemplateStore.BuiltIns.SingleOrDefault(t => t.Id == option.Template.Id);
            if (original is not null) option.Template.Body.Should().Be(original.Body, option.Template.Id);
        }
        // 保存先のテンプレートファイルにも、ファイルの内容が書き込まれていない。
        var loaded = await new PromptTemplateStore(h.AppPaths).LoadAsync();
        loaded.Value.Select(t => t.Body).Should().NotContain(b => b.Contains("MAIN_MARKER", StringComparison.Ordinal));
    }

    [AvaloniaFact(DisplayName = "「差分のみ」モードでオンにすると、指示文の後ろに変更のあったファイルが付く")]
    public async Task 差分のみモードでもオンならファイルが付く()
    {
        var h = await BuildAsync("since-append", ws =>
        {
            ws.WriteText("changed.py", "CHANGED_MARKER = 1\n");
            ws.WriteText("untouched.py", "UNTOUCHED_MARKER = 2\n");
        });
        WriteRevision(h, 1, "初回", Array.Empty<string>());
        WriteRevision(h, 2, "changed.pyを変更", new[] { "changed.py" });
        h.Context.SelectedMode = ContextMode.ChangedSince;
        await WaitUntilAsync(() => h.Context.Revisions.Count == 2);
        h.Context.SelectedRevision = h.Context.Revisions.Single(r => r.Revision == 1);
        h.Prompt.AppendSelectedFiles = true;

        var copied = await CopyAsync(h, "builtin-full");

        copied.Should().Contain("CHANGED_MARKER").And.NotContain("UNTOUCHED_MARKER");
    }

    [AvaloniaFact(DisplayName = "切り替えたときに、一覧の推定トークン数が付くファイルの分だけ増え、選んでいたテンプレートは変わらない")]
    public async Task 切り替えで推定トークン数が変わり選択は維持される()
    {
        var h = await BuildAsync("tokens", ws => ws.WriteText("big.py", new string('x', 4000) + "\n"));
        h.Prompt.IsOpen = true;
        await WaitForTemplatesAsync(h);
        var chosen = h.Prompt.Templates.Single(t => t.Template.Id == "builtin-investigate");
        h.Prompt.SelectedTemplate = chosen;
        var before = h.Prompt.Templates.Single(t => t.Template.Id == "builtin-full").EstimatedTokens;

        h.Prompt.AppendSelectedFiles = true;
        await WaitUntilAsync(() => h.Prompt.Templates.Count == PromptTemplateStore.BuiltIns.Count
                                   && h.Prompt.Templates.Single(t => t.Template.Id == "builtin-full").EstimatedTokens > before);

        h.Prompt.Templates.Single(t => t.Template.Id == "builtin-full").EstimatedTokens
            .Should().BeGreaterThan(before + 1000, "約4000文字のファイルが付く分だけ増える");
        h.Prompt.SelectedTemplate!.Template.Id.Should().Be("builtin-investigate", "切り替えで選択中のテンプレートを変えない");
    }

    // ------------------------------------------------------------------
    // 設定への保存
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "設定の値で初期化され、設定側の変更は保存依頼を出さずに反映される")]
    public async Task 設定の値で初期化され設定側の変更は反映される()
    {
        var h = await BuildAsync("settings-init", ws => ws.WriteText("a.py", "x"),
            new Settings { Context = new ContextSettings { AppendFilesToPrompt = true } });

        h.Prompt.AppendSelectedFiles.Should().BeTrue("設定に保存された選択が次回も効く");

        var requested = new List<bool>();
        h.Prompt.AppendSelectedFilesChangeCommitted += (_, v) => requested.Add(v);

        h.Prompt.ApplySettings(new Settings { Context = new ContextSettings { AppendFilesToPrompt = false } });
        h.Prompt.AppendSelectedFiles.Should().BeFalse("設定画面で切り替えたら反映される");
        requested.Should().BeEmpty("設定側からの反映で保存依頼を返すと保存の往復が止まらなくなる");

        h.Prompt.AppendSelectedFiles = true;
        requested.Should().Equal(true);
    }

    [AvaloniaFact(DisplayName = "ドロップダウンのチェックを切り替えると設定に保存され、次に開いたときも選択が効く")]
    public async Task チェックの切り替えが設定に保存され次回も効く()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "persist", "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, "persist", "project");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "main.py"), "MAIN_MARKER = 1\n");

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths, new Settings(), new SettingsStore(appPaths), new PatchQueue(appPaths), new ProjectStore(appPaths),
            new RevisionStore(appPaths), new RevisionRestorer(appPaths), new NullDialogService(), new AvaloniaUiServices(),
            openSettings: () => { });
        try
        {
            await shell.Graft.InitializeAsync();
            // StartupCoordinator.StartAsyncが行う配線（AppendFilesToPromptChangeRequested→
            // SettingsViewModel.SetAppendFilesToPromptLive→保存→onLiveSettingsChanged→UpdateSettings）を再現する。
            var settingsVm = new SettingsViewModel(
                appPaths, new NullDialogService(), new AvaloniaUiServices(), onLiveSettingsChanged: updated => shell.Graft.UpdateSettings(updated));
            await settingsVm.InitializeAsync();
            shell.Graft.AppendFilesToPromptChangeRequested += (_, v) => settingsVm.SetAppendFilesToPromptLive(v);
            await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir);

            shell.Graft.PromptCopy!.AppendSelectedFiles.Should().BeFalse("既定");
            shell.Graft.PromptCopy.AppendSelectedFiles = true;

            await WaitUntilAsync(async () =>
                (await new SettingsStore(appPaths).LoadAsync()).Value.Context.AppendFilesToPrompt);
            var saved = await new SettingsStore(appPaths).LoadAsync();
            saved.Value.Context.AppendFilesToPrompt.Should().BeTrue("切り替えは settings.json に保存される");
            saved.Value.Context.RespectGitignore.Should().BeTrue("ほかの設定は変わらない");
            shell.Graft.PromptCopy.AppendSelectedFiles.Should().BeTrue("保存後の設定の反映で選択が戻らない");

            // 次回起動を模す: 保存された設定から新しくPromptCopyを作ると、オンのまま始まる。
            var reopened = BuildPromptCopy(appPaths, shell.Graft.PromptCopy.Context.Project, saved.Value, new FakeClipboard());
            reopened.AppendSelectedFiles.Should().BeTrue();
        }
        finally
        {
            shell.Dispose();
        }
    }

    [AvaloniaFact(DisplayName = "設定画面の「プロンプトのコピーに、選んだファイルも付ける」を切り替えると、開いているドロップダウンのチェックにも反映される")]
    public async Task 設定画面の切り替えがドロップダウンに反映される()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "settings-window", "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, "settings-window", "project");
        Directory.CreateDirectory(projectDir);
        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths, new Settings(), new SettingsStore(appPaths), new PatchQueue(appPaths), new ProjectStore(appPaths),
            new RevisionStore(appPaths), new RevisionRestorer(appPaths), new NullDialogService(), new AvaloniaUiServices(),
            openSettings: () => { });
        try
        {
            await shell.Graft.InitializeAsync();
            var settingsVm = new SettingsViewModel(
                appPaths, new NullDialogService(), new AvaloniaUiServices(), onLiveSettingsChanged: updated => shell.Graft.UpdateSettings(updated));
            await settingsVm.InitializeAsync();
            await shell.Graft.ProjectPane.RegisterFolderAsync(projectDir);

            settingsVm.AppendFilesToPrompt = true;

            await WaitUntilAsync(() => shell.Graft.PromptCopy!.AppendSelectedFiles);
            shell.Graft.PromptCopy!.AppendSelectedFiles.Should().BeTrue();
        }
        finally
        {
            shell.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // 補助
    // ------------------------------------------------------------------

    private sealed record Harness(
        AppPaths AppPaths, Project Project, ContextCollectViewModel Context, PromptCopyViewModel Prompt, FakeClipboard Clipboard);

    private Task<Harness> BuildAsync(string caseName, Action<Workspace> setup) => BuildAsync(caseName, setup, new Settings());

    private async Task<Harness> BuildAsync(string caseName, Action<Workspace> setup, Settings settings)
    {
        var appPaths = new AppPaths(Path.Combine(_root, caseName, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        var projectDir = Path.Combine(_root, caseName, "project");
        Directory.CreateDirectory(projectDir);
        setup(new Workspace(projectDir));

        var store = new ProjectStore(appPaths);
        var project = (await store.RegisterAsync(projectDir, caseName)).Value;
        var clipboard = new FakeClipboard();
        var ui = new FakeUi(clipboard);
        var context = new ContextCollectViewModel(appPaths, store, project, settings, ui, new NullDialogService());
        await context.InitializeAsync();
        var prompt = new PromptCopyViewModel(
            new PromptTemplateStore(appPaths), new PromptTemplateRenderer(new ContextCollector(appPaths)),
            new RevisionStore(appPaths), new NullDialogService(), context, project, settings, ui);
        return new Harness(appPaths, project, context, prompt, clipboard);
    }

    private PromptCopyViewModel BuildPromptCopy(AppPaths appPaths, Project project, Settings settings, FakeClipboard clipboard)
    {
        var ui = new FakeUi(clipboard);
        var context = new ContextCollectViewModel(appPaths, new ProjectStore(appPaths), project, settings, ui, new NullDialogService());
        return new PromptCopyViewModel(
            new PromptTemplateStore(appPaths), new PromptTemplateRenderer(new ContextCollector(appPaths)),
            new RevisionStore(appPaths), new NullDialogService(), context, project, settings, ui);
    }

    /// <summary>テンプレートを選んでコピーし、クリップボードに書かれた内容を返す。</summary>
    private static async Task<string> CopyAsync(Harness h, string templateId)
    {
        if (h.Prompt.Templates.Count == 0)
        {
            h.Prompt.IsOpen = true; // 一覧を読み込む（OnIsOpenChanged→RefreshAsync）
            await WaitForTemplatesAsync(h);
        }

        h.Prompt.SelectedTemplate = h.Prompt.Templates.Single(t => t.Template.Id == templateId);
        h.Clipboard.Clear();
        h.Prompt.CopyCommand.Execute(null);
        await WaitUntilAsync(() => h.Clipboard.Text is not null);
        return h.Clipboard.Text!;
    }

    /// <summary>
    /// 一覧の読み込み完了を待つ。RefreshAsyncはテンプレートを1件ずつ展開しては追加するため、
    /// 件数が0を超えただけでは途中の可能性がある。組み込みテンプレートが出そろい、選択も確定するまで待つ。
    /// </summary>
    private static Task WaitForTemplatesAsync(Harness h)
        => WaitUntilAsync(() => h.Prompt.Templates.Count == PromptTemplateStore.BuiltIns.Count
                                && h.Prompt.SelectedTemplate is not null
                                && h.Prompt.Templates.Contains(h.Prompt.SelectedTemplate));

    /// <summary>リビジョンのmanifest.jsonだけを置く（差分のみモードは変更ファイルの一覧しか読まない）。</summary>
    private static void WriteRevision(Harness h, int revision, string summary, IReadOnlyList<string> changedPaths)
    {
        var folder = Path.Combine(
            h.AppPaths.GetProjectBackupDirectory(h.Project.Id), $"r{revision:0000}_2026010{revision}_120000");
        Directory.CreateDirectory(folder);
        var entries = string.Join(",", changedPaths.Select(p => $"{{\"path\":\"{p}\",\"operation\":\"modify\"}}"));
        var json =
            $"{{\"revision\":{revision},\"projectId\":\"{h.Project.Id}\",\"summary\":\"{summary}\",\"status\":\"success\"," +
            $"\"appliedAt\":\"2026-01-0{revision}T12:00:00+00:00\",\"entries\":[{entries}]}}";
        File.WriteAllText(Path.Combine(folder, "manifest.json"), json, new UTF8Encoding(false));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            if (await condition().ConfigureAwait(true)) return;
            await Task.Delay(10);
        }
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private sealed class Workspace
    {
        private readonly string _root;
        public Workspace(string root) => _root = root;

        public void WriteText(string relativePath, string content)
        {
            var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    private sealed class FakeClipboard : IClipboardAccess
    {
        public string? Text { get; private set; }
        public void Clear() => Text = null;
        public void SetText(string text) => Text = text;
        public Task<string?> GetTextAsync() => Task.FromResult(Text);
    }

    /// <summary>クリップボードだけフェイクにしたUI機能一式（画面情報・タイマーは本物）。</summary>
    private sealed class FakeUi : IUiServices
    {
        private readonly AvaloniaUiServices _inner = new();
        public FakeUi(IClipboardAccess clipboard) => Clipboard = clipboard;
        public IClipboardAccess Clipboard { get; }
        public IScreenInfo Screens => _inner.Screens;
        public IUiTimer CreateTimer(TimeSpan interval, Action onTick) => _inner.CreateTimer(interval, onTick);
    }
}
