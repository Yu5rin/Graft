using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 点検で実測されたデータ不整合（projects.json の読み書きに排他が無い）の回帰テスト。
///
/// 【修正前に実測した壊れ方】 <see cref="ProjectStore"/> の更新系メソッドはいずれも
/// 「LoadAsync → 加工 → SaveAsync」という読み書き分離の形をしており、その間に排他が
/// 無かった。そのため同一プロセス内で並行して呼ぶと、後から保存した側が「自分が読んだ
/// 時点の一覧」で全体を上書きしてしまう（lost update）。
/// ・<see cref="ProjectStore.ConsumeNextRevisionAsync"/> を20並行 → 20件すべてが同じ 1 を返し、
///   nextRevision は 21 ではなく 2 にしかならなかった。
/// ・<see cref="ProjectStore.RegisterAsync"/> を（別々のフォルダで）10並行 → projects.json に
///   10件中2件しか残らなかった。
///
/// 【実害】 リビジョン番号が重複すると、複数の適用が back/&lt;projectId&gt;/ の同じ番号の
/// バックアップフォルダを同時に読み書きすることになり、履歴と実体の対応が壊れて
/// 「ここまで戻す」で戻せない世代ができる。登録が消えるほうは、利用者から見ると
/// 「登録したはずのフォルダがプロジェクト一覧に無い」という形で現れる。
///
/// 【修正】 projects.json 1ファイルにつき1本の<see cref="System.Threading.SemaphoreSlim"/>で
/// 「読み込み〜保存」を1つの臨界区間にまとめた（ProjectStore.cs の FileGates 参照）。
/// このテストはその不可分性を、並行呼び出しの結果として観測できる形で固定する。
/// </summary>
public class ProjectStoreConcurrencyTests
{
    /// <summary>
    /// 並行数。実測時と同じ20で固定する。数を増やせば壊れやすくなるが、修正前でも
    /// この数で確実に（20件すべてが1を返すという形で）再現できたため、これ以上増やさない。
    /// </summary>
    private const int RevisionConcurrency = 20;

    [Fact(DisplayName = "ConsumeNextRevisionAsyncを20並行で呼んでも、返る番号は1〜20が重複なく払い出される")]
    public async Task 並行してリビジョン番号を消費しても重複しない()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var store = new ProjectStore(appPaths);

        var registered = await store.RegisterAsync(ws.CreateDirectory("project"), null);
        registered.IsSuccess.Should().BeTrue();
        var projectId = registered.Value.Id;

        // 適用が同時に走った状況をそのまま模す（実機ではキーボードからの多重起動で起きた）。
        var tasks = Enumerable
            .Range(0, RevisionConcurrency)
            .Select(_ => store.ConsumeNextRevisionAsync(projectId))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.IsSuccess);
        var consumed = results.Select(r => r.Value).OrderBy(v => v).ToList();
        consumed.Should().BeEquivalentTo(
            Enumerable.Range(1, RevisionConcurrency),
            "修正前は20件すべてが同じ 1 を返し、同じr1のバックアップフォルダを複数の適用が奪い合っていた");

        var final = await store.LoadAsync();
        final.Value.Single(p => p.Id == projectId).NextRevision.Should().Be(
            RevisionConcurrency + 1,
            "20個消費したのだから次は21。修正前は2にしかならなかった");
    }

    [Fact(DisplayName = "RegisterAsyncを別々のフォルダで10並行に呼んでも、10件すべてがprojects.jsonに残る")]
    public async Task 並行して登録しても登録が消えない()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var store = new ProjectStore(appPaths);

        var directories = Enumerable
            .Range(0, 10)
            .Select(i => ws.CreateDirectory($"project-{i}"))
            .ToList();

        var results = await Task.WhenAll(directories.Select(d => store.RegisterAsync(d, null)));
        results.Should().OnlyContain(r => r.IsSuccess);

        var final = await store.LoadAsync();
        final.Value.Should().HaveCount(10, "修正前は後勝ちの全件上書きにより10件中2件しか残らなかった");
        final.Value.Select(p => p.Id).Should().BeEquivalentTo(
            results.Select(r => r.Value.Id), "並行登録した10件がそのまま残る必要がある");
    }

    [Fact(DisplayName = "デグレ防止: 逐次的な登録（登録→登録→登録）は従来どおり3件とも残る")]
    public async Task 逐次的な登録は従来どおり動く()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var store = new ProjectStore(appPaths);

        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var result = await store.RegisterAsync(ws.CreateDirectory($"project-{i}"), null);
            result.IsSuccess.Should().BeTrue();
            ids.Add(result.Value.Id);
        }

        var final = await store.LoadAsync();
        final.Value.Select(p => p.Id).Should().BeEquivalentTo(ids);
    }

    [Fact(DisplayName = "デグレ防止: 逐次的なリビジョン消費（適用→適用）は1・2と順に払い出される")]
    public async Task 逐次的なリビジョン消費は従来どおり動く()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var store = new ProjectStore(appPaths);

        var registered = await store.RegisterAsync(ws.CreateDirectory("project"), null);
        var projectId = registered.Value.Id;

        (await store.ConsumeNextRevisionAsync(projectId)).Value.Should().Be(1);
        (await store.ConsumeNextRevisionAsync(projectId)).Value.Should().Be(2);
        (await store.LoadAsync()).Value.Single(p => p.Id == projectId).NextRevision.Should().Be(3);
    }

    [Fact(DisplayName = "別インスタンスのProjectStore同士でも排他される（アプリ内でnew ProjectStoreは複数箇所にある）")]
    public async Task 別インスタンス同士でも排他される()
    {
        using var ws = new TempWorkspace();
        var appDirectory = ws.CreateDirectory("app");
        var appPaths = new AppPaths(appDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        // StartupCoordinator・SettingsViewModel・OnboardingWindow がそれぞれ
        // new ProjectStore(appPaths) している実態を模す。排他をインスタンスのフィールドで
        // 持つと、この経路が塞げないままになる。
        var storeA = new ProjectStore(appPaths);
        var storeB = new ProjectStore(new AppPaths(appDirectory));

        var registered = await storeA.RegisterAsync(ws.CreateDirectory("project"), null);
        var projectId = registered.Value.Id;

        var tasks = Enumerable
            .Range(0, RevisionConcurrency)
            .Select(i => (i % 2 == 0 ? storeA : storeB).ConsumeNextRevisionAsync(projectId))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        results.Select(r => r.Value).OrderBy(v => v).Should().BeEquivalentTo(
            Enumerable.Range(1, RevisionConcurrency),
            "同じprojects.jsonを見ているインスタンス同士は、別インスタンスでも番号を共有して払い出す必要がある");
    }

    [Fact(DisplayName = "並行してもデッドロックしない: 登録・更新・削除・番号消費を混ぜて同時に走らせても全て完了する")]
    public async Task 更新系を混ぜて並行実行してもデッドロックしない()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var store = new ProjectStore(appPaths);

        var keep = await store.RegisterAsync(ws.CreateDirectory("keep"), null);
        var removable = await store.RegisterAsync(ws.CreateDirectory("removable"), null);

        // 公開メソッドが内部で別の公開メソッドを呼んでいると、SemaphoreSlimは再入できないため
        // 自分の握ったゲートを自分で待って永久に戻ってこない。LoadAsyncは復旧時に書き戻す
        // （＝内部でもう1回保存する）経路を持つため、特にここが危ない。実際に全経路を
        // 混ぜて走らせ、時間内に全部完了することで「ゲートを取り直していない」ことを担保する。
        var work = new List<Task>
        {
            store.RegisterAsync(ws.CreateDirectory("added-1"), null),
            store.RegisterAsync(ws.CreateDirectory("added-2"), null),
            store.UpdateAsync(keep.Value.Id, p => p with { Pinned = true }),
            store.ConsumeNextRevisionAsync(keep.Value.Id),
            store.MarkAppliedAsync(keep.Value.Id, System.DateTimeOffset.Now),
            store.LoadAsync(),
            store.RemoveAsync(removable.Value.Id, deleteHistory: true),
        };

        var all = Task.WhenAll(work);
        var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)));
        finished.Should().BeSameAs(all, "30秒以内に全ての操作が完了する必要がある（完了しない＝デッドロック）");
        await all; // 例外があればここで表面化させる。

        var final = await store.LoadAsync();
        final.Value.Should().HaveCount(3, "keep + added-1 + added-2（removableは削除済み）");
        final.Value.Single(p => p.Id == keep.Value.Id).Pinned.Should().BeTrue();
    }
}
