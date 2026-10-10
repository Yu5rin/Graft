using System.IO;
using System.Linq;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 新しいリビジョンの番号が、既存のバックアップフォルダの番号と重ならないことのテスト
/// （<see cref="RevisionNumbering"/>・<see cref="ProjectStore.ConsumeNextRevisionAsync(string, int, System.Threading.CancellationToken)"/>）。
///
/// 実機では、projects.jsonのnextRevisionがr42適用後に36へ巻き戻り、次の適用が既存のr36と
/// 同じ番号でバックアップフォルダを作ってしまった。ここでは「nextRevisionが実体より小さい」
/// 状態を直接作り、それでも払い出す番号が既存のどのフォルダとも重ならないことを固定する。
/// </summary>
public class RevisionNumberingTests : IDisposable
{
    private readonly TempWorkspace _ws = new();
    private readonly AppPaths _paths;
    private readonly ProjectStore _projects;
    private readonly RevisionStore _revisions;
    private string _projectId = string.Empty;

    public RevisionNumberingTests()
    {
        _paths = new AppPaths(_ws.CreateDirectory("app"));
        _paths.EnsureCoreDirectoriesExist();
        _projects = new ProjectStore(_paths);
        _revisions = new RevisionStore(_paths);
    }

    public void Dispose() => _ws.Dispose();

    /// <summary>プロジェクトを登録し、nextRevisionを指定値にして、rFrom〜rToのフォルダを作る。</summary>
    private async Task SetUpAsync(int nextRevision, int folderFrom, int folderTo)
    {
        var registered = await _projects.RegisterAsync(_ws.CreateDirectory("project"), null);
        _projectId = registered.Value.Id;
        await _projects.UpdateAsync(_projectId, p => p with { NextRevision = nextRevision });
        for (var rev = folderFrom; rev <= folderTo; rev++)
        {
            var at = new DateTimeOffset(2026, 10, 9, 13, 0, 0, TimeSpan.Zero).AddMinutes(rev);
            Directory.CreateDirectory(_paths.GetRevisionDirectory(_projectId, rev, at));
        }
    }

    private async Task<int> StoredNextRevisionAsync()
        => (await _projects.LoadAsync()).Value.Single(p => p.Id == _projectId).NextRevision;

    [Fact(DisplayName = "nextRevisionが36に戻っていても、r42まである状態では43が払い出され、nextRevisionは44になる")]
    public async Task 戻ったnextRevisionでも実体の最大番号より後の番号になる()
    {
        await SetUpAsync(nextRevision: 36, folderFrom: 35, folderTo: 42);

        var reserved = await RevisionNumbering.ReserveAsync(_projects, _revisions, _projectId);

        reserved.IsSuccess.Should().BeTrue();
        reserved.Value.Should().Be(43, "既存のr36〜r42と重ならない番号");
        (await StoredNextRevisionAsync()).Should().Be(44, "記録されるnextRevisionは使った番号+1。使った番号と食い違わない");
    }

    [Fact(DisplayName = "nextRevisionが実体と整合しているときは、従来どおりnextRevisionがそのまま払い出される")]
    public async Task 整合していれば従来どおり()
    {
        await SetUpAsync(nextRevision: 8, folderFrom: 1, folderTo: 7);

        var reserved = await RevisionNumbering.ReserveAsync(_projects, _revisions, _projectId);

        reserved.Value.Should().Be(8);
        (await StoredNextRevisionAsync()).Should().Be(9);
    }

    [Fact(DisplayName = "欠番がある場合（nextRevisionが実体の最大+1より大きい）は、欠番を許容してnextRevisionを使う")]
    public async Task nextRevisionのほうが大きければそちらを使う()
    {
        await SetUpAsync(nextRevision: 20, folderFrom: 1, folderTo: 7);

        var reserved = await RevisionNumbering.ReserveAsync(_projects, _revisions, _projectId);

        reserved.Value.Should().Be(20, "欠番を許容する既存の方針（13.1）はそのまま");
        (await StoredNextRevisionAsync()).Should().Be(21);
    }

    [Fact(DisplayName = "フォルダが消えてhistory.jsonlにだけ残る番号とも重ならない")]
    public async Task 履歴だけに残る番号とも重ならない()
    {
        await SetUpAsync(nextRevision: 3, folderFrom: 1, folderTo: 2);
        await new RevisionIndex(_paths).AppendAsync(_projectId, new RevisionIndexEntry
        {
            Revision = 9, Status = RevisionStatus.Success, AppliedAt = DateTimeOffset.Now, FolderName = "r9_20261009_130900",
        });

        var reserved = await RevisionNumbering.ReserveAsync(_projects, _revisions, _projectId);

        reserved.Value.Should().Be(10, "フォルダが無くても履歴にあるr9は再利用しない");
    }

    [Fact(DisplayName = "呼び出し側が見込んだ番号（minimumRevision）より小さい番号は払い出さない")]
    public async Task 見込みより小さい番号は払い出さない()
    {
        await SetUpAsync(nextRevision: 5, folderFrom: 1, folderTo: 4);

        var reserved = await RevisionNumbering.ReserveAsync(_projects, _revisions, _projectId, minimumRevision: 12);

        reserved.Value.Should().Be(12);
        (await StoredNextRevisionAsync()).Should().Be(13);
    }

    [Fact(DisplayName = "巻き戻った状態で10並行に払い出しても、すべて既存のフォルダと重ならず、互いにも重ならない")]
    public async Task 並行して払い出しても重ならない()
    {
        await SetUpAsync(nextRevision: 36, folderFrom: 35, folderTo: 42);

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => RevisionNumbering.ReserveAsync(_projects, _revisions, _projectId)));

        var numbers = results.Select(r => r.Value).OrderBy(v => v).ToList();
        numbers.Should().OnlyHaveUniqueItems();
        numbers.Should().OnlyContain(n => n >= 43);
        (await StoredNextRevisionAsync()).Should().Be(numbers.Max() + 1);
    }

    [Fact(DisplayName = "PeekNextAsyncは何も消費せず、古いスナップショットではなく最新のnextRevisionと実体を見た見込みを返す")]
    public async Task Peekは消費せず最新の見込みを返す()
    {
        await SetUpAsync(nextRevision: 36, folderFrom: 35, folderTo: 42);

        var peeked = await RevisionNumbering.PeekNextAsync(_projects, _revisions, _projectId, snapshotNextRevision: 36);

        peeked.Should().Be(43);
        (await StoredNextRevisionAsync()).Should().Be(36, "見込みを求めただけでは何も書き換えない");

        var reserved = await RevisionNumbering.ReserveAsync(_projects, _revisions, _projectId, minimumRevision: peeked);
        reserved.Value.Should().Be(peeked, "ドライランで見せた番号と実際に使う番号は一致する");
    }

    [Fact(DisplayName = "ProjectStore.ConsumeNextRevisionAsyncの下限は、下限がnextRevision以下なら従来の動作と変わらない")]
    public async Task 下限が小さければ従来どおり()
    {
        await SetUpAsync(nextRevision: 5, folderFrom: 1, folderTo: 0);

        (await _projects.ConsumeNextRevisionAsync(_projectId, minimumRevision: 2)).Value.Should().Be(5);
        (await _projects.ConsumeNextRevisionAsync(_projectId, minimumRevision: 0)).Value.Should().Be(6);
        (await _projects.ConsumeNextRevisionAsync(_projectId)).Value.Should().Be(7);
        (await StoredNextRevisionAsync()).Should().Be(8);
    }
}
