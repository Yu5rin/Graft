using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 課題2（Git自動コミット）の土台となる<see cref="GitIntegration"/>単体テスト。
/// CommitAsync/GetStatusAsyncが実際のgitコマンドと正しくやり取りできることと、
/// gitリポジトリでない場合にエラーを投げず<see cref="GraftResult{T}"/>で失敗を表現することを
/// 実際のワーキングツリーで検証する（HookRunnerTests等と同様、テスト実行環境に
/// 対応するコマンド（ここではgit）が存在することを前提とする）。
/// </summary>
[Collection(GitProcessCollection.Name)]
public class GitIntegrationTests
{
    [Fact(DisplayName = "gitリポジトリへコミットすると type: summary 形式のログが残る")]
    public async Task コミットするとtype_summary形式でログに残る()
    {
        using var ws = new TempWorkspace();
        await InitRepoAsync(ws.RootPath);
        ws.WriteText("a.py", "x=1\n");

        var git = new GitIntegration();
        var result = await git.CommitAsync(ws.RootPath, "feat", "テスト用の変更 (r1)", new[] { "a.py" });

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("feat: テスト用の変更 (r1)");

        var log = await RunGitAsync(ws.RootPath, "log", "-1", "--pretty=%s");
        log.Should().Be("feat: テスト用の変更 (r1)");

        var status = await git.GetStatusAsync(ws.RootPath);
        status.Value.IsRepository.Should().BeTrue();
        status.Value.HasUncommittedChanges.Should().BeFalse("コミット直後は未コミットの変更が無いはず");
    }

    [Fact(DisplayName = "typeが無い場合はsummaryのみでコミットする")]
    public async Task typeが無い場合はsummaryのみになる()
    {
        using var ws = new TempWorkspace();
        await InitRepoAsync(ws.RootPath);
        ws.WriteText("a.py", "x=1\n");

        var git = new GitIntegration();
        var result = await git.CommitAsync(ws.RootPath, type: null, "テスト用の変更", new[] { "a.py" });

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("テスト用の変更");
    }

    [Fact(DisplayName = "gitリポジトリでないフォルダではGetStatusAsyncがIsRepository=falseを返す")]
    public async Task gitリポジトリでない場合はIsRepositoryがfalse()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x=1\n"); // git init しない。

        var git = new GitIntegration();
        var status = await git.GetStatusAsync(ws.RootPath);

        status.IsSuccess.Should().BeTrue("gitリポジトリでないこと自体はエラーとして扱わない");
        status.Value.IsRepository.Should().BeFalse();
    }

    [Fact(DisplayName = "gitリポジトリでないフォルダへのCommitAsyncは例外を投げず失敗を返す")]
    public async Task gitリポジトリでない場合のコミットは失敗を返す()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x=1\n"); // git init しない。

        var git = new GitIntegration();
        var result = await git.CommitAsync(ws.RootPath, "feat", "テスト用の変更", new[] { "a.py" });

        result.IsSuccess.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // 課題3: 自動コミット失敗理由のログ区別に使う前提条件チェック
    // ------------------------------------------------------------------

    [Fact(DisplayName = "gitリポジトリならCheckCommitPreflightAsyncはReadyを返す")]
    public async Task 前提条件チェックはリポジトリならReadyを返す()
    {
        using var ws = new TempWorkspace();
        await InitRepoAsync(ws.RootPath);

        var git = new GitIntegration();
        var preflight = await git.CheckCommitPreflightAsync(ws.RootPath);

        preflight.Should().Be(GitCommitPreflight.Ready);
    }

    [Fact(DisplayName = "gitリポジトリでないフォルダではCheckCommitPreflightAsyncがNotARepositoryを返す")]
    public async Task 前提条件チェックはリポジトリでなければNotARepositoryを返す()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.py", "x=1\n"); // git init しない。

        var git = new GitIntegration();
        var preflight = await git.CheckCommitPreflightAsync(ws.RootPath);

        preflight.Should().Be(GitCommitPreflight.NotARepository);
    }

    // ------------------------------------------------------------------
    // 不具合1: Git出力の文字化け（RunGitAsyncが組み立てるProcessStartInfoの検証）
    // ------------------------------------------------------------------

    /// <summary>
    /// GitIntegration.RunGitAsyncが実際にgitへ渡す<see cref="ProcessStartInfo"/>がBOM無しUTF-8を
    /// 明示していることを、gitを実行せずに検証する。Linux上ではOSの既定エンコーディングが
    /// 元々UTF-8のため、実際にgitを実行して文字化けの有無だけを見るテストでは
    /// 「エンコーディングを明示しないまま偶然通っている」状態を検知できない
    /// （実機で見つかった不具合はこの検証がなかったために埋め込まれた）。
    /// </summary>
    [Fact(DisplayName = "不具合1: RunGitAsyncはStandardOutput/ErrorEncodingにBOM無しUTF-8を明示する")]
    public void 標準出力と標準エラーの文字コードがUTF8で明示される()
    {
        var psi = GitIntegration.BuildProcessStartInfo("/tmp/dummy", new[] { "status" });

        psi.StandardOutputEncoding.Should().Be(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        psi.StandardErrorEncoding.Should().Be(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// 日本語ファイル名の文字化け（quotepath既定動作によるエスケープ表記）と、コミットログの
    /// 再エンコード揺れに対する保険（core.quotepath=false / i18n.logOutputEncoding=UTF-8）が
    /// 実際に引数として渡っていることを検証する。
    /// </summary>
    [Fact(DisplayName = "不具合1: RunGitAsyncはcore.quotepath=falseとi18n.logOutputEncoding=UTF-8を渡す")]
    public void グローバルgit設定オプションが渡される()
    {
        var psi = GitIntegration.BuildProcessStartInfo("/tmp/dummy", new[] { "status", "--porcelain" });

        psi.ArgumentList.Should().ContainInOrder("-c", "core.quotepath=false", "-c", "i18n.logOutputEncoding=UTF-8");
        // -c オプションは常にサブコマンドより前に置かれる必要がある（git自体の制約）。
        psi.ArgumentList.Should().EndWith(new[] { "status", "--porcelain" });
    }

    /// <summary>
    /// コミットメッセージが<see cref="ProcessStartInfo.ArgumentList"/>経由（文字列連結ではなく）で
    /// 渡っていることの確認（不具合1の報告に対する裏付け）。ArgumentList経由であれば、Windowsでは
    /// CreateProcessWがUTF-16文字列としてそのまま子プロセスへ渡すため、コンソールのコードページに
    /// 起因する文字化けは生じない（issue: 引数側は問題なしと確認済み）。
    /// </summary>
    [Fact(DisplayName = "不具合1: CommitAsyncはコミットメッセージをArgumentList経由で渡す（文字列結合はしない）")]
    public void コミットメッセージはArgumentList経由で渡される()
    {
        var psi = GitIntegration.BuildProcessStartInfo("/tmp/dummy", new[] { "commit", "-m", "テスト用の変更 (r1)" });

        psi.ArgumentList.Should().Contain("テスト用の変更 (r1)");
        // 単一の要素としてそのまま入っていること（他の引数と結合されていないこと）を確認する。
        psi.ArgumentList.Count(a => a == "テスト用の変更 (r1)").Should().Be(1);
    }

    // ------------------------------------------------------------------
    // コンテキスト収集の「gitの変更ファイルだけ」: GetChangedFilesAsync（実際のgitで検証する）
    // ------------------------------------------------------------------

    /// <summary>初期コミット済みのリポジトリを作る（a.txt・sub/b.txt・名前 変更.txt・del.txt が追跡済み）。</summary>
    private static async Task<TempWorkspace> CreateCommittedRepoAsync()
    {
        var ws = new TempWorkspace();
        await InitRepoAsync(ws.RootPath);
        ws.WriteText("a.txt", "a\n");
        ws.WriteText("sub/b.txt", "b\n");
        ws.WriteText("del.txt", "d\n");
        ws.WriteText("old name.txt", "this content is long enough for rename detection\nline2\nline3\n");
        await RunGitAsync(ws.RootPath, "add", "-A");
        await RunGitAsync(ws.RootPath, "commit", "-q", "-m", "初期");
        return ws;
    }

    [Fact(DisplayName = "gitの変更ファイル: 変更・追加・未追跡（フォルダの中も1つずつ）が取れ、削除は件数だけ数えて含めない")]
    public async Task 変更ファイルが実際のgitから取れる()
    {
        using var ws = await CreateCommittedRepoAsync();
        ws.WriteText("a.txt", "a changed\n"); // 変更
        ws.WriteText("added.txt", "new\n"); // 追加（ステージ済み）
        await RunGitAsync(ws.RootPath, "add", "added.txt");
        ws.WriteText("newdir/deep/untracked.txt", "u\n"); // 未追跡のフォルダの中
        File.Delete(ws.Combine("del.txt")); // 削除

        var result = await new GitIntegration().GetChangedFilesAsync(ws.RootPath);

        result.State.Should().Be(GitChangedFilesState.Ready);
        result.Paths.Should().BeEquivalentTo(new[] { "a.txt", "added.txt", "newdir/deep/untracked.txt" });
        result.DeletedCount.Should().Be(1);
    }

    [Fact(DisplayName = "gitの変更ファイル: 名前変更は新しい側だけが取れ、空白を含む名前も引用符なしで取れる")]
    public async Task 名前変更と空白を含む名前が取れる()
    {
        using var ws = await CreateCommittedRepoAsync();
        await RunGitAsync(ws.RootPath, "mv", "old name.txt", "new name 日本語.txt");

        var result = await new GitIntegration().GetChangedFilesAsync(ws.RootPath);

        result.State.Should().Be(GitChangedFilesState.Ready);
        result.Paths.Should().Equal("new name 日本語.txt");
        result.DeletedCount.Should().Be(0, "名前変更の古い側は削除として数えない");
    }

    [Fact(DisplayName = "gitの変更ファイル: プロジェクトのルートがリポジトリのサブフォルダなら、サブフォルダ相対のパスで返り、外の変更は含まない")]
    public async Task サブフォルダのルートではサブフォルダ相対で返る()
    {
        using var ws = await CreateCommittedRepoAsync();
        ws.WriteText("sub/b.txt", "b changed\n"); // サブフォルダの中の変更
        ws.WriteText("sub/inner/new.txt", "n\n"); // サブフォルダの中の未追跡
        ws.WriteText("a.txt", "a changed\n"); // サブフォルダの外の変更（含まれてはならない）
        ws.WriteText("subway/other.txt", "o\n"); // 名前が前方一致するだけの別フォルダ（含まれてはならない）

        var result = await new GitIntegration().GetChangedFilesAsync(ws.Combine("sub"));

        result.State.Should().Be(GitChangedFilesState.Ready);
        result.Paths.Should().BeEquivalentTo(new[] { "b.txt", "inner/new.txt" });
    }

    [Fact(DisplayName = "gitの変更ファイル: 変更が無ければReadyで0件")]
    public async Task 変更が無ければ0件()
    {
        using var ws = await CreateCommittedRepoAsync();

        var result = await new GitIntegration().GetChangedFilesAsync(ws.RootPath);

        result.State.Should().Be(GitChangedFilesState.Ready);
        result.Paths.Should().BeEmpty();
        result.DeletedCount.Should().Be(0);
    }

    [Fact(DisplayName = "gitの変更ファイル: .gitignoreで無視されるファイルは変更ファイルに含まれない")]
    public async Task 無視されるファイルは含まれない()
    {
        using var ws = await CreateCommittedRepoAsync();
        ws.WriteText(".gitignore", "*.log\n");
        ws.WriteText("noise.log", "x\n");
        ws.WriteText("a.txt", "a changed\n");

        var result = await new GitIntegration().GetChangedFilesAsync(ws.RootPath);

        result.Paths.Should().BeEquivalentTo(new[] { ".gitignore", "a.txt" });
    }

    [Fact(DisplayName = "gitの変更ファイル: gitリポジトリでないフォルダではNotARepositoryを返す（例外にしない）")]
    public async Task リポジトリでなければNotARepository()
    {
        using var ws = new TempWorkspace();
        ws.WriteText("a.txt", "x\n"); // git init しない。

        var result = await new GitIntegration().GetChangedFilesAsync(ws.RootPath);

        result.State.Should().Be(GitChangedFilesState.NotARepository);
        result.Paths.Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // D3: 自動コミットに、利用者が前からステージしていた無関係の変更が混ざる不具合。
    // 以前は「git add <対象>」のあと「git commit -m」だけだったため、インデックスに
    // 既にあった他のファイルまで同じコミットに入っていた。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "D3: 前からステージ済みの無関係なファイルはコミットに入らず、ステージされたまま残る")]
    public async Task 無関係のステージ済み変更はコミットに混ざらない()
    {
        using var ws = new TempWorkspace();
        await InitRepoAsync(ws.RootPath);
        ws.WriteText("keep.txt", "元\n");
        ws.WriteText("target.py", "x=1\n");
        await RunGitAsync(ws.RootPath, "add", "--", "keep.txt", "target.py");
        await RunGitAsync(ws.RootPath, "commit", "-q", "-m", "初期");

        // 利用者が前からステージしていた無関係の変更（既存ファイルの編集と、新規ファイル）。
        ws.WriteText("keep.txt", "利用者の編集\n");
        ws.WriteText("userNew.txt", "利用者の新規\n");
        await RunGitAsync(ws.RootPath, "add", "--", "keep.txt", "userNew.txt");
        // パッチが変えたファイル（既存の編集と新規作成）。
        ws.WriteText("target.py", "x=2\n");
        ws.WriteText("created.py", "y=1\n");

        var result = await new GitIntegration().CommitAsync(
            ws.RootPath, "feat", "パッチ (r1)", new[] { "target.py", "created.py" });

        result.IsSuccess.Should().BeTrue(result.Issues.FirstOrDefault()?.Detail);
        var committed = await RunGitAsync(ws.RootPath, "show", "--name-only", "--pretty=format:", "HEAD");
        Lines(committed).Should().BeEquivalentTo(new[] { "target.py", "created.py" }, "パッチが変えたパスだけがコミットされる");

        var staged = await RunGitAsync(ws.RootPath, "diff", "--cached", "--name-only");
        Lines(staged).Should().BeEquivalentTo(new[] { "keep.txt", "userNew.txt" }, "利用者のステージ済み変更はステージされたまま残る");
    }

    [Fact(DisplayName = "D3: 削除・改名されたパスも正しくコミットに入る")]
    public async Task 削除と改名もコミットに入る()
    {
        using var ws = new TempWorkspace();
        await InitRepoAsync(ws.RootPath);
        ws.WriteText("gone.txt", "消える\n");
        ws.WriteText("old.txt", "改名前\n");
        ws.WriteText("other.txt", "無関係\n");
        await RunGitAsync(ws.RootPath, "add", "--", ".");
        await RunGitAsync(ws.RootPath, "commit", "-q", "-m", "初期");

        ws.WriteText("other.txt", "無関係の編集\n");
        await RunGitAsync(ws.RootPath, "add", "--", "other.txt");
        System.IO.File.Delete(ws.Combine("gone.txt"));
        System.IO.File.Move(ws.Combine("old.txt"), ws.Combine("new.txt"));

        var result = await new GitIntegration().CommitAsync(
            ws.RootPath, null, "削除と改名", new[] { "gone.txt", "old.txt", "new.txt" });

        result.IsSuccess.Should().BeTrue(result.Issues.FirstOrDefault()?.Detail);
        var committed = await RunGitAsync(ws.RootPath, "show", "--name-status", "--no-renames", "--pretty=format:", "HEAD");
        Lines(committed.Replace('\t', ' ')).Should().BeEquivalentTo(new[] { "D gone.txt", "D old.txt", "A new.txt" });
        Lines(await RunGitAsync(ws.RootPath, "diff", "--cached", "--name-only")).Should().Equal("other.txt");
    }

    [Fact(DisplayName = "D3: 日本語や記号を含むパスと、非常に多くのパスでも1コミットにまとまる")]
    public async Task 特殊なパスと多数のパスでも1コミットにまとまる()
    {
        using var ws = new TempWorkspace();
        await InitRepoAsync(ws.RootPath);
        ws.WriteText("seed.txt", "x\n");
        await RunGitAsync(ws.RootPath, "add", "--", ".");
        await RunGitAsync(ws.RootPath, "commit", "-q", "-m", "初期");

        var paths = new System.Collections.Generic.List<string> { "日本語 フォルダ/ファイル[1].txt", "glob*.txt" };
        // コマンドライン長（Windowsで約3万文字）を確実に超える量。
        for (var i = 0; i < 1500; i++) paths.Add($"many/ディレクトリ名をそれなりに長くしておく/file_with_a_long_name_{i:D5}.txt");
        foreach (var p in paths) ws.WriteText(p, "内容\n");
        ws.WriteText("unrelated.txt", "含めない\n");

        var result = await new GitIntegration().CommitAsync(ws.RootPath, "chore", "多数", paths);

        result.IsSuccess.Should().BeTrue(result.Issues.FirstOrDefault()?.Detail);
        (await RunGitAsync(ws.RootPath, "rev-list", "--count", "HEAD")).Should().Be("2", "1回のコミットにまとまる");
        (await RunGitAsync(ws.RootPath, "status", "--porcelain")).Should().Be("?? unrelated.txt");
    }

    private static string[] Lines(string text)
        => text.Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();

    /// <summary>git init と、テスト実行環境のグローバル設定に依存しないローカルのuser設定を行う。</summary>
    private static async Task InitRepoAsync(string root)
    {
        await RunGitAsync(root, "init", "-q");
        await RunGitAsync(root, "config", "user.email", "test@example.com");
        await RunGitAsync(root, "config", "user.name", "Graft Test");
    }

    /// <summary>
    /// テスト側の検証用git実行ヘルパー。製品コードのRunGitAsyncとは別実装だが、
    /// 「gitのUTF-8出力を正しく読めること」を検証する側でも同じくエンコーディングを
    /// 明示しないとWindows上で文字化けし、検証そのものが誤って失敗する（不具合1）。
    /// </summary>
    private static async Task<string> RunGitAsync(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return stdout.Trim();
    }
}
