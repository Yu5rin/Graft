using System.Diagnostics;
using Graft.Core;
using Graft.Core.Vba;
using Graft.Features;
using Graft.Infra;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;

namespace Graft.UiTests.Vba;

/// <summary>
/// Excel VBA 連携 段階 P3 のシナリオテスト（<c>VbaP3*Tests</c>）が共通で使う下拵え。
/// <see cref="ShellHarness"/>（実際の起動と同じ依存グラフ）に <see cref="FakeVbaBridge"/> を入れ、ブックを 1 つ開いて
/// プロジェクトとして追加したところまでを作る。衝突ダイアログと選択ダイアログは台本（<see cref="ConflictAnswer"/>・
/// <see cref="ChoiceAnswer"/>）で答え、出た要求をすべて記録する（何が出たか・何が選べたかを後から検証するため）。
/// 台本が無いときは、ダイアログが未配線のときと同じ既定値（中止・「反映しない」・既定のボタン）で答える。
/// </summary>
internal sealed class VbaP3Rig : IDisposable
{
    public const string Book = @"C:\Work\計画.xlsm";

    public const string Module1Original = "Sub A()\r\n  MsgBox \"hi\"\r\nEnd Sub";
    public const string Cls1Original = "Option Explicit\r\nPublic Value As Long";

    private VbaP3Rig(ShellHarness harness, FakeVbaBridge bridge)
    {
        H = harness;
        Bridge = bridge;
    }

    public ShellHarness H { get; }

    public FakeVbaBridge Bridge { get; }

    public Project Project { get; private set; } = null!;

    public MainViewModel Graft => H.Shell.Graft;

    /// <summary>出た選択ダイアログ（<see cref="VbaChoiceRequest"/>）の記録。</summary>
    public List<VbaChoiceRequest> Choices { get; } = new();

    /// <summary>出た衝突ダイアログの記録。</summary>
    public List<VbaConflictRequest> Conflicts { get; } = new();

    /// <summary>選択ダイアログへの答え（選ぶボタンの Id）。null を返すと既定のボタン。</summary>
    public Func<VbaChoiceRequest, string?>? ChoiceAnswer { get; set; }

    /// <summary>衝突ダイアログへの答え。null を返すと既定の結果（中止、または「反映しない」）。</summary>
    public Func<VbaConflictRequest, VbaConflictDecision?>? ConflictAnswer { get; set; }

    public static async Task<VbaP3Rig> CreateAsync(
        Settings? settings = null, bool plainProjectFirst = false, Action<FakeVbaBridge>? arrange = null, bool withWindow = false)
    {
        var harness = await ShellHarness.CreateAsync(settings ?? new Settings { ShowPreview = false }, withWindow).ConfigureAwait(true);
        var bridge = new FakeVbaBridge();
        bridge.AddWorkbook(Book,
            FakeVbaBridge.Std("Module1", Module1Original),
            FakeVbaBridge.Cls("Cls1", Cls1Original),
            FakeVbaBridge.Doc("Sheet1", "' sheet", "集計"));
        arrange?.Invoke(bridge);
        var rig = new VbaP3Rig(harness, bridge);
        harness.Shell.Graft.VbaBridge = bridge;

        harness.Shell.Graft.ChooseVbaAsync = request =>
        {
            rig.Choices.Add(request);
            return Task.FromResult(rig.ChoiceAnswer?.Invoke(request));
        };
        harness.Shell.Graft.ShowVbaConflictAsync = request =>
        {
            rig.Conflicts.Add(request);
            return Task.FromResult(rig.ConflictAnswer?.Invoke(request));
        };

        if (plainProjectFirst) await harness.RegisterProjectAsync().ConfigureAwait(true);
        rig.Project = await rig.AddWorkbookAsync().ConfigureAwait(true);
        return rig;
    }

    private async Task<Project> AddWorkbookAsync()
    {
        var pane = Graft.ProjectPane;
        pane.ShowWorkbookPickerAsync = async picker =>
        {
            await picker.LoadAsync().ConfigureAwait(true);
            picker.SelectedWorkbook = picker.Workbooks.First(w => w.FullName == Book);
            return await picker.ConfirmAsync().ConfigureAwait(true);
        };
        await ShellHarness.ExecuteAsync(pane.AddFromWorkbookCommand).ConfigureAwait(true);
        return pane.SelectedItem!.Project;
    }

    // ------------------------------------------------------------------
    // 操作
    // ------------------------------------------------------------------

    public FakeVbaBridge.Module Excel(string name) => Bridge.Get(Book)!.Find(name)!;

    public string MirrorPath(string relative) => Path.Combine(Project.Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Mirror(string relative) => File.ReadAllText(MirrorPath(relative));

    /// <summary>SEARCH/REPLACE のブロック 1 つ（複数をつなげて 1 つのパッチにする）。</summary>
    public static string Edit(string path, string search, string replace)
        => $"<<<< FILE: {path}\n<<<<<<< SEARCH\n{search}\n=======\n{replace}\n>>>>>>> REPLACE\n>>>> END\n";

    public static string Create(string path, string content)
        => $"<<<< FILE: {path} MODE=FULL\n{content}\n>>>> END\n";

    public static string Delete(string path) => $"<<<< DELETE: {path}\n";

    /// <summary>クリップボードにパッチを置いて解析する（解析前の自動 Pull もここで起きる）。</summary>
    public async Task PasteAsync(params string[] blocks)
    {
        H.Clipboard.Text = string.Concat(blocks);
        await ShellHarness.ExecuteAsync(Graft.PasteAndParseCommand).ConfigureAwait(true);
    }

    public Task ApplyAsync() => ShellHarness.ExecuteAsync(Graft.ApplyCommand);

    public BlockItemViewModel Block(string path) => Graft.Blocks.First(b => b.Plan.Path == path);

    public async Task<IReadOnlyList<RevisionSummary>> RevisionsAsync()
        => (await new RevisionStore(H.AppPaths).ListAsync(Project.Id).ConfigureAwait(true)).Value;

    public Task<VbaLocalStatus> StatusAsync() => Graft.VbaSync.GetLocalStatusAsync(Project);

    /// <summary>VbaLink の設定を書き換えて、一覧を読み直す（右クリックメニューの設定の代わり）。</summary>
    public async Task UpdateLinkAsync(Func<VbaLink, VbaLink> change)
    {
        await new ProjectStore(H.AppPaths).UpdateAsync(Project.Id, p => p with { Vba = change(p.Vba!) }).ConfigureAwait(true);
        await Graft.ProjectPane.LoadAsync().ConfigureAwait(true);
        await Graft.ProjectPane.SelectAsync(Graft.ProjectPane.Items.First(i => i.Project.Id == Project.Id)).ConfigureAwait(true);
        Project = Graft.ProjectPane.SelectedItem!.Project;
    }

    /// <summary>適用後フックを設定して読み直す。</summary>
    public async Task SetHooksAsync(params PostApplyHook[] hooks)
    {
        await new ProjectStore(H.AppPaths).UpdateAsync(Project.Id, p => p with { PostApplyHooks = hooks }).ConfigureAwait(true);
        await Graft.ProjectPane.LoadAsync().ConfigureAwait(true);
        await Graft.ProjectPane.SelectAsync(Graft.ProjectPane.Items.First(i => i.Project.Id == Project.Id)).ConfigureAwait(true);
        Project = Graft.ProjectPane.SelectedItem!.Project;
    }

    /// <summary>Excel 側の控え（<c>.rescue/</c>）に残ったファイルの本文。</summary>
    public IReadOnlyList<string> RescueTexts()
    {
        var directory = H.AppPaths.GetVbaRescueDirectory(Project.Id);
        return Directory.Exists(directory)
            ? Directory.GetFiles(directory).Select(File.ReadAllText).ToList()
            : Array.Empty<string>();
    }

    public int WriteCalls => Bridge.Calls.Count(c => c.StartsWith("Write:", StringComparison.Ordinal));

    /// <summary>ミラーを Git リポジトリにして初期コミットを作る（Git 自動コミットが実行されないことの確認用）。</summary>
    public async Task<string> InitGitAsync()
    {
        await RunGitAsync("init", "-q").ConfigureAwait(true);
        await RunGitAsync("config", "user.name", "test").ConfigureAwait(true);
        await RunGitAsync("config", "user.email", "test@example.com").ConfigureAwait(true);
        await RunGitAsync("add", "-A").ConfigureAwait(true);
        await RunGitAsync("commit", "-q", "-m", "初期コミット").ConfigureAwait(true);
        return await RunGitAsync("log", "-1", "--pretty=%H").ConfigureAwait(true);
    }

    public async Task<string> RunGitAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Project.Root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return output.Trim();
    }

    public void Dispose() => H.Dispose();
}
