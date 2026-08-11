using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 依頼「エクスプローラへ既存のファイルを取り込む手段」（利用者の指摘: 「ファイルをボタンや
/// ドラッグ＆ドロップでエクスプローラーに追加できないのですね」）の回帰テスト。
///
/// 実コピーそのもの（常にコピーで移動ではない・フォルダの再帰コピー・進捗・キャンセル）は
/// <c>FileImportServiceTests</c>（Graft.Tests）で単体検証済み。ここでは
/// <see cref="ExplorerViewModel.ImportPathsAsync"/>・<see cref="ExplorerViewModel.AddFileCommand"/>を
/// 中心に、「配置先の解決（フォルダ／ファイル／余白）」「ツリーへの反映と選択」
/// 「同名衝突時のUI確認（上書き／別名／中止）」「部分的な失敗の通知」を検証する。
///
/// 実際のOSドラッグ＆ドロップ（<c>ExplorerView.axaml.cs</c>のDragEventArgsを介した
/// ヒットテスト・ハイライト）は、ProjectPaneDropTargetTestsのコメントにもあるとおり
/// Avalonia.Headlessから安定して合成するのが難しいため、ここでは対象にしない
/// （「落とされた項目をどこへコピーするか」の判定自体はドロップ・ボタン・右クリックの
/// 3経路が最終的にすべてImportPathsAsyncへ収束するため、本ファイルのテストで実質的に
/// 検証できている）。
/// </summary>
public class ExplorerImportTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-explorer-import", Guid.NewGuid().ToString("N"));

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

    // ===================== 配置先の解決（フォルダ／ファイル／余白） =====================

    [AvaloniaFact(DisplayName = "フォルダのノードへ取り込むと、そのフォルダの直下へコピーされる")]
    public async Task フォルダへ取り込むとその直下へコピーされる()
    {
        var subDir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subDir);
        using var external = new ExternalSource();
        var sourceFile = external.WriteFile("photo.png", "画像データ");

        var (explorer, _) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);
        var subNode = explorer.RootNodes.Single(n => n.Name == "sub");

        await explorer.ImportPathsAsync(subNode, new[] { sourceFile }).ConfigureAwait(true);

        File.Exists(Path.Combine(subDir, "photo.png")).Should().BeTrue("フォルダを対象にした場合はその直下へコピーされるべき");
        File.Exists(sourceFile).Should().BeTrue("依頼の必須要件: 元のファイルは消えない");
    }

    [AvaloniaFact(DisplayName = "ファイルのノードへ取り込むと、その親フォルダの直下へコピーされる")]
    public async Task ファイルへ取り込むと親フォルダへコピーされる()
    {
        var subDir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subDir);
        await File.WriteAllTextAsync(Path.Combine(subDir, "existing.txt"), "既存").ConfigureAwait(true);
        using var external = new ExternalSource();
        var sourceFile = external.WriteFile("photo.png", "画像データ");

        var (explorer, _) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);
        var subNode = explorer.RootNodes.Single(n => n.Name == "sub");
        subNode.IsExpanded = true;
        await WaitForAsync(() => subNode.IsLoaded).ConfigureAwait(true);
        var existingFile = subNode.Children.Single(n => n.Name == "existing.txt");

        await explorer.ImportPathsAsync(existingFile, new[] { sourceFile }).ConfigureAwait(true);

        File.Exists(Path.Combine(subDir, "photo.png")).Should().BeTrue("ファイルを対象にした場合はその親フォルダへコピーされるべき（新規ファイル作成と同じ規約）");
    }

    [AvaloniaFact(DisplayName = "余白（対象ノード無し）へ取り込むと、プロジェクトルート直下へコピーされる")]
    public async Task 余白へ取り込むとルート直下へコピーされる()
    {
        using var external = new ExternalSource();
        var sourceFile = external.WriteFile("photo.png", "画像データ");

        var (explorer, _) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);

        await explorer.ImportPathsAsync(null, new[] { sourceFile }).ConfigureAwait(true);

        File.Exists(Path.Combine(_root, "photo.png")).Should().BeTrue("対象ノードが無い（余白へのドロップ）場合はプロジェクトルートへコピーされるべき");
    }

    // ===================== フォルダごとの再帰コピー =====================

    [AvaloniaFact(DisplayName = "フォルダを取り込むと中身が再帰的にコピーされ、元のフォルダは残る")]
    public async Task フォルダを取り込むと再帰的にコピーされる()
    {
        using var external = new ExternalSource();
        var sourceDir = external.WriteDirectory("assets", ("a.txt", "A"), ("nested/b.txt", "B"));

        var (explorer, _) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);

        await explorer.ImportPathsAsync(null, new[] { sourceDir }).ConfigureAwait(true);

        File.Exists(Path.Combine(_root, "assets", "a.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "assets", "nested", "b.txt")).Should().BeTrue();
        Directory.Exists(sourceDir).Should().BeTrue("依頼の必須要件: 元のフォルダは消えない（移動ではない）");
        File.Exists(Path.Combine(sourceDir, "a.txt")).Should().BeTrue();
    }

    // ===================== ツリーへの反映と選択・ツールバー／右クリック経由 =====================

    [AvaloniaFact(DisplayName = "ツールバーの「ファイルを追加」は、選択中のフォルダへ取り込みツリーに反映して選択する")]
    public async Task ツールバーのファイルを追加は選択中フォルダへ取り込む()
    {
        var subDir = Path.Combine(_root, "sub");
        Directory.CreateDirectory(subDir);
        using var external = new ExternalSource();
        var sourceFile = external.WriteFile("photo.png", "画像データ");

        var (explorer, dialogs) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);
        var subNode = explorer.RootNodes.Single(n => n.Name == "sub");
        subNode.IsExpanded = true;
        await WaitForAsync(() => subNode.IsLoaded).ConfigureAwait(true);
        explorer.SelectedNode = subNode;
        dialogs.NextPickedFiles = new[] { sourceFile };

        // ツールバーの「ファイルを追加」ボタンはCommandParameter無しで呼ばれ、SelectedNodeへ
        // フォールバックする（NewFileCommand等と同じ規約）。
        explorer.AddFileCommand.Execute(null);
        await WaitForAsync(() => File.Exists(Path.Combine(subDir, "photo.png"))).ConfigureAwait(true);
        await WaitForAsync(() => subNode.Children.Any(n => n.Name == "photo.png")).ConfigureAwait(true);

        var created = subNode.Children.SingleOrDefault(n => n.Name == "photo.png");
        created.Should().NotBeNull("取り込んだファイルはツリーへ反映されるべき");
        explorer.SelectedNode.Should().BeSameAs(created, "取り込んだファイルは選択状態になるべき");
    }

    [AvaloniaFact(DisplayName = "右クリックメニューの「ここにファイルを追加...」は、右クリックしたフォルダへ取り込む（選択中フォルダとは別でもよい）")]
    public async Task 右クリックのここに追加は指定フォルダへ取り込む()
    {
        var subA = Path.Combine(_root, "a");
        var subB = Path.Combine(_root, "b");
        Directory.CreateDirectory(subA);
        Directory.CreateDirectory(subB);
        using var external = new ExternalSource();
        var sourceFile = external.WriteFile("photo.png", "画像データ");

        var (explorer, dialogs) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);
        var nodeA = explorer.RootNodes.Single(n => n.Name == "a");
        var nodeB = explorer.RootNodes.Single(n => n.Name == "b");
        explorer.SelectedNode = nodeA; // 選択中はaだが、右クリックしてMenuItemのCommandParameterに渡すのはb。
        dialogs.NextPickedFiles = new[] { sourceFile };

        explorer.AddFileCommand.Execute(nodeB); // MenuItem.CommandParameter="{Binding SelectedNode}"相当を明示的に模擬。
        await WaitForAsync(() => File.Exists(Path.Combine(subB, "photo.png"))).ConfigureAwait(true);

        File.Exists(Path.Combine(subA, "photo.png")).Should().BeFalse("選択中のフォルダではなく、右クリック（CommandParameter）で指定したフォルダへ取り込まれるべき");
        File.Exists(Path.Combine(subB, "photo.png")).Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "ファイル選択ダイアログでキャンセルした場合は何も起きない")]
    public async Task ダイアログをキャンセルすると何も起きない()
    {
        var (explorer, dialogs) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);
        dialogs.NextPickedFiles = null; // キャンセル相当。

        explorer.AddFileCommand.Execute(null);
        await Task.Delay(50).ConfigureAwait(true); // 非同期の完了を待つ猶予（発火しないことの確認のため）。

        Directory.EnumerateFileSystemEntries(_root).Should().BeEmpty("ダイアログをキャンセルした場合は何も取り込まれないはず");
        dialogs.ShownMessages.Should().BeEmpty();
    }

    // ===================== 同名ファイルの扱い（上書き・別名で保存・中止） =====================

    [AvaloniaFact(DisplayName = "同名衝突で「上書き」を選ぶと既存の内容が置き換わる")]
    public async Task 同名衝突で上書きを選ぶと置き換わる()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "古い内容").ConfigureAwait(true);
        using var external = new ExternalSource();
        var sourceFile = external.WriteFile("a.txt", "新しい内容");

        var (explorer, dialogs) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);
        // 不具合2点検（実機報告の横断チェック）: 「上書き」は元へ戻せない破壊的な操作のため
        // ConfirmThreeWayAsyncのyesLabel（既定ボタン）からnoLabelへ移した
        // （ExplorerViewModel.BuildImportPlanAsync参照）。falseが「上書き」になる。
        dialogs.NextThreeWayResult = false; // 「上書き」

        await explorer.ImportPathsAsync(null, new[] { sourceFile }).ConfigureAwait(true);

        File.ReadAllText(Path.Combine(_root, "a.txt")).Should().Be("新しい内容");
        // 実機不具合の回帰確認: 既定ボタン（yesLabel）は不可逆な「上書き」ではなく、
        // 非破壊的な「別名で保存」でなければならない。
        dialogs.LastThreeWayLabels.Should().Be(("別名で保存", "上書き"),
            "既定ボタン（Enterで実行される）に破壊的な選択肢を渡してはいけない");
    }

    [AvaloniaFact(DisplayName = "同名衝突で「別名で保存」を選ぶと、入力した新しい名前で保存され元の項目は残る")]
    public async Task 同名衝突で別名を選ぶと新しい名前で保存される()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "既存の内容").ConfigureAwait(true);
        using var external = new ExternalSource();
        var sourceFile = external.WriteFile("a.txt", "新しい内容");

        var (explorer, dialogs) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);
        // 「別名で保存」は非破壊的なのでyesLabel（既定ボタン）のまま。trueが「別名で保存」になる。
        dialogs.NextThreeWayResult = true; // 「別名で保存」
        dialogs.NextPromptResult = "a-renamed.txt";

        await explorer.ImportPathsAsync(null, new[] { sourceFile }).ConfigureAwait(true);

        File.ReadAllText(Path.Combine(_root, "a.txt")).Should().Be("既存の内容", "黙って上書きしてはならない");
        File.Exists(Path.Combine(_root, "a-renamed.txt")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, "a-renamed.txt")).Should().Be("新しい内容");
    }

    [AvaloniaFact(DisplayName = "同名衝突で「中止」を選ぶと、その項目だけ見送られ、元の項目・取り込み元ともに変化しない")]
    public async Task 同名衝突で中止を選ぶと見送られる()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "既存の内容").ConfigureAwait(true);
        using var external = new ExternalSource();
        var sourceFile = external.WriteFile("a.txt", "新しい内容");

        var (explorer, dialogs) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);
        dialogs.NextThreeWayResult = null; // 「中止」（キャンセル）

        await explorer.ImportPathsAsync(null, new[] { sourceFile }).ConfigureAwait(true);

        File.ReadAllText(Path.Combine(_root, "a.txt")).Should().Be("既存の内容", "中止した項目は元の内容のまま残るべき");
        File.Exists(sourceFile).Should().BeTrue("取り込み元も消えない");
        dialogs.ShownMessages.Should().BeEmpty("利用者が能動的にその場で中止を選んだだけの場合は、あらためて通知しない仕様");
    }

    // ===================== 部分的な失敗の通知（依頼の必須要件） =====================

    [AvaloniaFact(DisplayName = "複数件のうち1件が取り込み時に失敗しても、アプリは落ちず、成功件数と失敗内容が通知される")]
    public async Task 一部失敗しても落ちず通知される()
    {
        // 依頼「同名の項目がある場合」の確認ダイアログ（BuildImportPlanAsync）の最中に、
        // まだ処理していない後続の項目（missing.txt）の取り込み元を消してしまうことで、
        // 「計画には載ったが実コピー時には既に無い」という一部失敗を確定的に再現する
        // （File.Exists判定のタイミング競合をテストの都合で意図的に作っている。実運用でも
        // ドラッグ中に取り込み元が動かされた場合等に起こりうる状況で、FileImportService側の
        // 例外処理（E402）が正しく機能することの統合的な確認を兼ねる）。
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "conflict.txt"), "既存").ConfigureAwait(true);
        using var external = new ExternalSource();
        var missingSoonFile = external.WriteFile("missing.txt", "そのうち消える");
        var conflictFile = external.WriteFile("conflict.txt", "新しい内容");

        var (explorer, dialogs) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);
        dialogs.NextThreeWayResult = false; // conflict.txtは上書きする（falseが「上書き」。上のコメント参照）。
        dialogs.OnThreeWayRequested = () => File.Delete(missingSoonFile); // その直前にmissing.txtを消す。

        // missing.txtを先に、conflict.txtを後に渡す（BuildImportPlanAsyncは順番に処理するため、
        // conflict.txtの確認ダイアログが出た時点でmissing.txtは既に計画へ追加済みになる）。
        await explorer.ImportPathsAsync(null, new[] { missingSoonFile, conflictFile }).ConfigureAwait(true);

        File.ReadAllText(Path.Combine(_root, "conflict.txt")).Should().Be("新しい内容", "失敗した項目が他の項目を巻き込んではならない");
        File.Exists(Path.Combine(_root, "missing.txt")).Should().BeFalse("取り込み元が無くなっていた項目はコピーされない");

        dialogs.ShownMessages.Should().ContainSingle();
        var (title, message) = dialogs.ShownMessages.Single();
        title.Should().Contain("成功 1件", "成功件数が分かるように通知されるべき");
        message.Should().Contain("missing.txt", "どの項目が失敗したか分かるように通知されるべき");
    }

    // ===================== 取り込み中フラグ（進捗・中止ボタンの表示可否） =====================

    [AvaloniaFact(DisplayName = "取り込み中はIsImportingがtrueになり、完了すると必ずfalseへ戻る")]
    public async Task 取り込み中はIsImportingがtrueになる()
    {
        using var external = new ExternalSource();
        var sourceFile = external.WriteFile("a.txt", "内容");
        var (explorer, _) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);

        explorer.IsImporting.Should().BeFalse();
        var sawImporting = false;
        explorer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExplorerViewModel.IsImporting) && explorer.IsImporting) sawImporting = true;
        };

        await explorer.ImportPathsAsync(null, new[] { sourceFile }).ConfigureAwait(true);

        sawImporting.Should().BeTrue("取り込み中はIsImportingがtrueになる必要がある（進捗・中止ボタンの表示条件）");
        explorer.IsImporting.Should().BeFalse("完了後は必ずfalseへ戻る必要がある（finally節で保証）");
    }

    [AvaloniaFact(DisplayName = "取り込み中でないときはCancelImportCommandは実行できない")]
    public async Task 取り込み中でなければ中止コマンドは実行できない()
    {
        var (explorer, _) = await BuildExplorerWithProjectAsync().ConfigureAwait(true);

        explorer.CancelImportCommand.CanExecute(null).Should().BeFalse();
    }

    // ===================== ヘルパ =====================

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private async Task<(ExplorerViewModel Explorer, ScriptedDialogService Dialogs)> BuildExplorerWithProjectAsync()
    {
        Directory.CreateDirectory(_root);
        var dialogs = new ScriptedDialogService();
        var ui = new AvaloniaUiServices();
        var editor = new EditorPaneViewModel(new Settings(), dialogs, ui);
        var appPaths = new AppPaths(Path.Combine(_root, "_app"));
        var explorer = new ExplorerViewModel(appPaths, editor, dialogs, new Settings(), ui);

        var project = new Project { Id = "p_test", Name = "テスト用", Root = _root };
        await explorer.SetProjectAsync(project).ConfigureAwait(true);
        return (explorer, dialogs);
    }

    /// <summary>取り込み元ファイルを置くための、プロジェクト外の一時ディレクトリ。</summary>
    private sealed class ExternalSource : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "graft-explorer-import-src", Guid.NewGuid().ToString("N"));

        public string WriteFile(string name, string content)
        {
            Directory.CreateDirectory(_root);
            var full = Path.Combine(_root, name);
            File.WriteAllText(full, content);
            return full;
        }

        public string WriteDirectory(string name, params (string RelativePath, string Content)[] files)
        {
            var dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            foreach (var (relativePath, content) in files)
            {
                var full = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.WriteAllText(full, content);
            }
            return dir;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// 取り込みの各ダイアログ呼び出しに、テストごとに指定した応答を返すテスト用IDialogService。
    /// PickFilesAsyncを明示的に実装する点がIDialogServiceの既定実装（単一選択フォールバック）と異なる
    /// （AvaloniaDialogServiceの複数選択実装を模す）。
    /// </summary>
    private sealed class ScriptedDialogService : IDialogService
    {
        public IReadOnlyList<string>? NextPickedFiles { get; set; }
        public bool? NextThreeWayResult { get; set; } = true;
        public string? NextPromptResult { get; set; }
        public Action? OnThreeWayRequested { get; set; }
        public List<(string Title, string Message)> ShownMessages { get; } = new();

        /// <summary>直近のConfirmThreeWayAsync呼び出しのyesLabel/noLabel（不具合2の回帰確認用）。</summary>
        public (string YesLabel, string NoLabel)? LastThreeWayLabels { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
        {
            LastThreeWayLabels = (yesLabel, noLabel);
            OnThreeWayRequested?.Invoke();
            return Task.FromResult(NextThreeWayResult);
        }

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult(NextPromptResult);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult((string?)null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult(NextPickedFiles?.FirstOrDefault());

        public Task<IReadOnlyList<string>?> PickFilesAsync(string title, IReadOnlyList<string>? extensions = null)
            => Task.FromResult(NextPickedFiles);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult((string?)null);

        public Task ShowMessageAsync(string title, string message)
        {
            ShownMessages.Add((title, message));
            return Task.CompletedTask;
        }
    }
}
