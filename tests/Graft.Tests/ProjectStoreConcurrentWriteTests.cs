using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 利用者報告「『使い方を学ぶ』終了後にProjectが消える」の調査で判明した、projects.json への
/// 書き込み競合の回帰テスト。
///
/// 【調査結果】原因は<c>StartupCoordinator.RunStartupValidationAsync</c>（起動のたびに必ず
/// バックグラウンドで走る、back/配下の実体走査を伴う起動時検証。数百ms〜数秒かかりうる）の内部、
/// <c>ReconcileRevisionsAsync</c>（nextRevisionのずれをprojects.jsonへ補正する処理）にあった。
/// 以前の実装は、起動直後に読み込んだ「古いプロジェクト一覧のスナップショット」を検証完了まで
/// 保持し続け、1件でも補正が必要なら<see cref="ProjectStore.SaveAsync"/>でそのスナップショット
/// （＝リスト全体）を1回だけ書き戻していた。この起動時検証の完了を待たずに、画面上の
/// チュートリアル（<c>Graft.Views.ShellWindow</c>のShellWindow.Tutorial.cs、サンプル
/// プロジェクトの登録・削除を伴う）や利用者自身の操作（フォルダ登録等）が並行してprojects.json
/// を書き換えると、後から行われる「スナップショット全体の書き戻し」がそれらの変更を古い内容で
/// 上書きしてしまう。
///
/// このテストは<c>StartupCoordinator</c>を経由せず、同じ「読んでから書くまでの間が長い」
/// パターンをProjectStoreの公開APIだけで直接再現する。
/// - <see cref="スナップショットを保持したままの全件保存は途中の並行登録を消してしまう"/>は、
///   修正前の実装が実際に採用していたパターン（LoadAsyncのスナップショットを保持→並行して
///   別のプロジェクトが登録される→スナップショットのままSaveAsyncで全体を上書き）を直接なぞり、
///   並行登録された分がprojects.jsonから消えることを示す（この呼び出しパターン自体は
///   ProjectStoreの一般的な誤用例として今後も回帰しうるため、テストとして残す）。
/// - <see cref="個別更新なら並行登録が保持される"/>は、同じ状況で
///   <see cref="ProjectStore.UpdateAsync"/>（書き込み直前に自前でLoadAsyncし直す）を使えば
///   並行登録が消えないことを示す。これがStartupCoordinator.Validation.csの
///   ReconcileRevisionsAsyncに適用した実際の修正と同じパターンであり、以後この振る舞いが
///   壊れていないことを保証する。
/// </summary>
public class ProjectStoreConcurrentWriteTests
{
    [Fact(DisplayName = "不具合調査: スナップショットを保持したままの全件保存は、その後に並行登録されたプロジェクトを消してしまう（修正前の実装が持っていたパターン）")]
    public async Task スナップショットを保持したままの全件保存は途中の並行登録を消してしまう()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var store = new ProjectStore(appPaths);

        var existingDir = ws.CreateDirectory("existing-project");
        var existingResult = await store.RegisterAsync(existingDir, null);
        existingResult.IsSuccess.Should().BeTrue();

        // StartupCoordinator.RunStartupValidationAsyncの冒頭（projectStore.LoadAsync()）に相当:
        // 起動直後、まだ何も並行操作が起きていない時点でのスナップショットを読み込む。
        var snapshot = await store.LoadAsync();
        snapshot.IsSuccess.Should().BeTrue();
        snapshot.Value.Should().HaveCount(1);

        // この間に、画面上のチュートリアル（サンプルの登録）や利用者自身の操作が並行して走り、
        // 別のプロジェクトが新たに登録される。
        var concurrentDir = ws.CreateDirectory("concurrent-project");
        var concurrentResult = await store.RegisterAsync(concurrentDir, null);
        concurrentResult.IsSuccess.Should().BeTrue();

        // 起動時検証側は、back/配下の走査等で時間がかかった末に「1件補正が必要」と判断し、
        // 保持していた古いスナップショット（並行登録を反映していないリスト）を丸ごと書き戻す
        // （修正前のReconcileRevisionsAsyncが実際に行っていたパターン）。
        var stale = snapshot.Value;
        var mutated = new System.Collections.Generic.List<Project>(stale)
        {
            [0] = stale[0] with { NextRevision = stale[0].NextRevision + 1 },
        };
        await store.SaveAsync(mutated);

        // 並行登録されたプロジェクトが、projects.jsonから消えてしまっている
        // （このテストが可視化したいのは正にこの実害）。
        var final = await store.LoadAsync();
        final.Value.Should().HaveCount(1, "スナップショットが古いままの全件上書きにより並行登録が失われる");
        final.Value.Should().NotContain(p => p.Id == concurrentResult.Value.Id);
    }

    [Fact(DisplayName = "個別更新（ProjectStore.UpdateAsync）なら、同じ状況でも並行登録されたプロジェクトが保持される（StartupCoordinator.Validation.csの修正と同じパターン）")]
    public async Task 個別更新なら並行登録が保持される()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        appPaths.EnsureCoreDirectoriesExist();
        var store = new ProjectStore(appPaths);

        var existingDir = ws.CreateDirectory("existing-project");
        var existingResult = await store.RegisterAsync(existingDir, null);
        existingResult.IsSuccess.Should().BeTrue();

        // 起動時検証の冒頭スナップショット相当（値そのものは使わず、対象IDだけ後で参照する）。
        var snapshot = await store.LoadAsync();
        snapshot.Value.Should().HaveCount(1);
        var targetId = snapshot.Value[0].Id;

        // 並行して別のプロジェクトが登録される（チュートリアルのサンプル登録に相当）。
        var concurrentDir = ws.CreateDirectory("concurrent-project");
        var concurrentResult = await store.RegisterAsync(concurrentDir, null);
        concurrentResult.IsSuccess.Should().BeTrue();

        // 起動時検証側は、古いスナップショット全体を書き戻すのではなく、補正が必要な
        // プロジェクト1件だけをUpdateAsync（書き込み直前に自前でLoadAsyncし直す）で更新する
        // （StartupCoordinator.Validation.csのReconcileRevisionsAsync修正後の実装と同じ）。
        var updateResult = await store.UpdateAsync(targetId, p => p with { NextRevision = p.NextRevision + 1 });
        updateResult.IsSuccess.Should().BeTrue();

        var final = await store.LoadAsync();
        final.Value.Should().HaveCount(2, "個別更新なら並行登録されたプロジェクトも保持される");
        final.Value.Should().Contain(p => p.Id == concurrentResult.Value.Id);
        final.Value.Should().Contain(p => p.Id == targetId && p.NextRevision == updateResult.Value.NextRevision);
    }
}
