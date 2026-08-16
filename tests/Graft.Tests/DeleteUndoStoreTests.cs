using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 課題2: ごみ箱削除のアプリ内復元（Ctrl+Z）。<see cref="DeleteUndoStore"/>単体で、
/// 退避→復元の往復（ファイル・フォルダ・複数件スタック）・同名衝突時の失敗・
/// 終了時の掃除（<see cref="DeleteUndoStore.Cleanup"/>）を検証する。
/// エクスプローラからの実際の呼び出し経路（ExplorerViewModel）はUIテスト側
/// （Graft.UiTests/DeleteUndoTests.cs）でキー操作込みに検証する。
/// </summary>
public class DeleteUndoStoreTests
{
    [Fact(DisplayName = "ファイル1件の退避→（削除を模した手動削除）→復元で内容が完全に一致する")]
    public async Task ファイルの退避と復元で内容が一致する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var store = new DeleteUndoStore(appPaths);
        var filePath = ws.WriteText("project/sample.txt", "退避対象の内容\n2行目");

        var staged = await store.StageAsync(filePath, isDirectory: false);
        staged.IsSuccess.Should().BeTrue();
        store.CanUndo.Should().BeTrue();

        // FileTreeService.DeleteAsync が実際に行う「元のファイルを消す」部分を模す。
        File.Delete(filePath);
        File.Exists(filePath).Should().BeFalse("退避後は削除される想定のため前提を確認");

        var undone = await store.UndoAsync();
        undone.IsSuccess.Should().BeTrue(string.Join(",", undone.Issues.Select(i => i.ToDisplayText())));
        undone.Value.Should().NotBeNull();
        undone.Value!.OriginalFullPath.Should().Be(filePath);
        undone.Value!.IsDirectory.Should().BeFalse();

        File.Exists(filePath).Should().BeTrue("元の場所へ書き戻されている必要がある");
        (await File.ReadAllTextAsync(filePath)).Should().Be("退避対象の内容\n2行目");
        store.CanUndo.Should().BeFalse("復元済みの分はスタックから取り除かれる必要がある");
    }

    [Fact(DisplayName = "フォルダ（複数ファイル・空フォルダ入り）の退避→復元で丸ごと元に戻る")]
    public async Task フォルダの退避と復元で丸ごと元に戻る()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var store = new DeleteUndoStore(appPaths);

        var folderPath = ws.CreateDirectory("project/folder");
        ws.WriteText("project/folder/a.txt", "A");
        ws.WriteText("project/folder/nested/b.txt", "B");
        ws.CreateDirectory("project/folder/empty"); // 空フォルダも復元対象に含める

        var staged = await store.StageAsync(folderPath, isDirectory: true);
        staged.IsSuccess.Should().BeTrue();

        Directory.Delete(folderPath, recursive: true);
        Directory.Exists(folderPath).Should().BeFalse();

        var undone = await store.UndoAsync();
        undone.IsSuccess.Should().BeTrue(string.Join(",", undone.Issues.Select(i => i.ToDisplayText())));

        Directory.Exists(folderPath).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(folderPath, "a.txt"))).Should().Be("A");
        (await File.ReadAllTextAsync(Path.Combine(folderPath, "nested", "b.txt"))).Should().Be("B");
        Directory.Exists(Path.Combine(folderPath, "empty")).Should().BeTrue("空フォルダも復元されている必要がある");
    }

    [Fact(DisplayName = "連続で2件削除すると、Ctrl+Z相当のUndoAsyncを2回呼ぶことで新しい順に戻る")]
    public async Task 複数件のスタックは新しい順に戻る()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var store = new DeleteUndoStore(appPaths);

        var first = ws.WriteText("project/first.txt", "1件目");
        var second = ws.WriteText("project/second.txt", "2件目");

        (await store.StageAsync(first, isDirectory: false)).IsSuccess.Should().BeTrue();
        File.Delete(first);
        (await store.StageAsync(second, isDirectory: false)).IsSuccess.Should().BeTrue();
        File.Delete(second);

        var firstUndo = await store.UndoAsync();
        firstUndo.IsSuccess.Should().BeTrue();
        firstUndo.Value!.OriginalFullPath.Should().Be(second, "後から削除した2件目が先に戻るはず（スタック）");
        File.Exists(second).Should().BeTrue();
        File.Exists(first).Should().BeFalse("1件目はまだ戻していない");

        var secondUndo = await store.UndoAsync();
        secondUndo.IsSuccess.Should().BeTrue();
        secondUndo.Value!.OriginalFullPath.Should().Be(first);
        File.Exists(first).Should().BeTrue();

        store.CanUndo.Should().BeFalse();
    }

    [Fact(DisplayName = "復元先に同名ファイルが既にある場合は上書きせず、E201として失敗する")]
    public async Task 復元先に同名ファイルがあると失敗する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var store = new DeleteUndoStore(appPaths);

        var filePath = ws.WriteText("project/sample.txt", "元の内容");
        (await store.StageAsync(filePath, isDirectory: false)).IsSuccess.Should().BeTrue();
        File.Delete(filePath);

        // 削除後、同じ場所へ別の新規ファイルが作られた状況を再現する。
        File.WriteAllText(filePath, "後から作られた別の内容");

        var undone = await store.UndoAsync();
        undone.IsSuccess.Should().BeFalse("同名の項目が既にある場合は上書きしてはならない");
        undone.Issues.Should().Contain(i => i.Code == ErrorCode.E201);

        (await File.ReadAllTextAsync(filePath)).Should().Be("後から作られた別の内容", "失敗時に上書きされていないことを確認");
        store.CanUndo.Should().BeTrue("復元に失敗した項目はスタックに残り、衝突を解消すれば再試行できる必要がある");

        // 衝突を解消すれば、同じUndoAsync呼び出しで改めて復元できることも確認する。
        File.Delete(filePath);
        var retried = await store.UndoAsync();
        retried.IsSuccess.Should().BeTrue();
        (await File.ReadAllTextAsync(filePath)).Should().Be("元の内容");
    }

    [Fact(DisplayName = "何も削除していない状態でUndoAsyncを呼んでも成功のうえ何もしない")]
    public async Task 取り消し対象が無いときは何もしない()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var store = new DeleteUndoStore(appPaths);

        store.CanUndo.Should().BeFalse();
        var undone = await store.UndoAsync();
        undone.IsSuccess.Should().BeTrue();
        undone.Value.Should().BeNull();
    }

    [Fact(DisplayName = "退避に成功した直後に実際の削除が失敗した場合、DiscardLastで退避コピーだけ後始末できる")]
    public async Task 実削除が失敗した場合は退避だけ後始末する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var store = new DeleteUndoStore(appPaths);
        var filePath = ws.WriteText("project/sample.txt", "内容");

        (await store.StageAsync(filePath, isDirectory: false)).IsSuccess.Should().BeTrue();
        store.CanUndo.Should().BeTrue();

        // 実際の削除（ITrashService.Send/Directory.Delete等）は失敗し、元のファイルはまだ残っている想定。
        store.DiscardLast();

        store.CanUndo.Should().BeFalse("実削除しなかった分はスタックに残してはならない");
        File.Exists(filePath).Should().BeTrue("元のファイルには一切手を付けていない");
        Directory.Exists(appPaths.TrashStagingDirectory).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(appPaths.TrashStagingDirectory).Should().BeEmpty("退避コピーの後始末が必要");
    }

    [Fact(DisplayName = "Cleanupで退避ディレクトリ（back/trash/）が丸ごと空になる（アプリ終了時の掃除）")]
    public async Task Cleanupで退避ディレクトリが空になる()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var store = new DeleteUndoStore(appPaths);

        var first = ws.WriteText("project/a.txt", "A");
        var second = ws.WriteText("project/b.txt", "B");
        (await store.StageAsync(first, isDirectory: false)).IsSuccess.Should().BeTrue();
        (await store.StageAsync(second, isDirectory: false)).IsSuccess.Should().BeTrue();

        Directory.Exists(appPaths.TrashStagingDirectory).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(appPaths.TrashStagingDirectory).Should().NotBeEmpty();

        store.Cleanup();

        store.CanUndo.Should().BeFalse("セッション内のみの保持のため、終了時は取り消しも失われる");
        Directory.Exists(appPaths.TrashStagingDirectory).Should().BeFalse("back/trash/ 自体を消す");

        // Cleanup後にもう一度呼んでも例外にならない（多重終了経路への耐性）。
        var act = () => store.Cleanup();
        act.Should().NotThrow();
    }

    // ------------------------------------------------------------------
    // スレッド安全性の回帰（負荷下フレークの真因調査）
    //
    // 【背景】 tests/Graft.UiTests/DeleteUndoTests.cs が負荷実行時にごく低頻度で
    // "Stack empty"（InvalidOperationException）で落ちる報告があった。調査の結果、
    // DeleteUndoStore.StageAsync/UndoAsync 内部のawaitが ConfigureAwait(false) を使っており、
    // 本来UIスレッドだけで完結する前提の _stack（System.Collections.Generic.Stack&lt;T&gt;、
    // それ自体はスレッド非安全）への書き込みがスレッドプールのスレッド上で行われていたこと、
    // かつ呼び出し元の ExplorerViewModel.DeleteCommand には多重起動を防ぐ仕組みが無く
    // （UndoDeleteCommand は AsyncRelayCommand の実行中フラグで防いでいるが、DeleteCommand は
    // 素の RelayCommand&lt;T&gt; で防いでいない）、負荷下で削除操作が本当に複数スレッドから
    // 並行実行されうることを突き止めた。以下は、その2つの条件（並行呼び出し・非UIスレッドでの
    // Stack操作）を人為的に作って"Stack empty"を実際に再現し、修正（DeleteUndoStore内部を
    // SemaphoreSlimで直列化）後は再現しなくなることを確認する回帰テスト。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "StageAsyncを大量に真の並行実行しても、退避した件数ぶん過不足なくスタックに積まれる")]
    public async Task StageAsyncの並行実行で取りこぼしが起きない()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var store = new DeleteUndoStore(appPaths);

        const int n = 300;
        var paths = new string[n];
        for (var i = 0; i < n; i++) paths[i] = ws.WriteText($"project/f{i}.txt", $"content {i}");

        // 実機のDeleteNodeAsyncと同じ順序（Stage→元ファイル削除）を1セットとして、
        // DeleteCommandに多重起動ガードが無い状況を模してN件ぶん完全並行にキックする。
        var tasks = new Task[n];
        for (var i = 0; i < n; i++)
        {
            var p = paths[i];
            tasks[i] = Task.Run(async () =>
            {
                var staged = await store.StageAsync(p, isDirectory: false);
                if (staged.IsSuccess) File.Delete(p);
            });
        }
        await Task.WhenAll(tasks);

        // 積まれた件数は、内部実装に依存しない外部から観測可能な方法（UndoAsyncを積まれた分
        // だけ呼び切れるか）で数える。単なるCanUndoの真偽ではなく、実際に全件を正しく
        // 復元しきれることまで確認する（Stack破損は取りこぼしだけでなく、内部要素の破損
        // ＝復元先パスの不整合としても現れうるため）。
        var restoredCount = 0;
        while (store.CanUndo)
        {
            var undone = await store.UndoAsync();
            if (!undone.IsSuccess || undone.Value is null) break;
            restoredCount++;
        }

        restoredCount.Should().Be(n, "並行にPushしても1件も取りこぼさず、全件を正しく取り消せる必要がある");
        foreach (var p in paths)
        {
            File.Exists(p).Should().BeTrue($"{p} が復元されているはず");
        }
    }

    [Fact(DisplayName = "削除（Push）と取り消し（Pop）を真に並行実行しても\"Stack empty\"等の例外にならない")]
    public async Task 削除と取り消しの並行実行で例外にならない()
    {
        // 1回の実行では発生しないことがあるため、複数回試行して負荷下の低頻度事象を
        // 手元でも安定して捕まえる（タスク側の指示どおり、繰り返し実行による再現を採る）。
        for (var attempt = 0; attempt < 30; attempt++)
        {
            using var ws = new TempWorkspace();
            var appPaths = new AppPaths(ws.CreateDirectory($"app{attempt}"));
            var store = new DeleteUndoStore(appPaths);

            const int n = 60;
            var paths = new string[n];
            for (var i = 0; i < n; i++) paths[i] = ws.WriteText($"project/f{i}.txt", $"c{i}");

            // 半分は削除（Push側）、半分は取り消し（Pop側）を完全並行にキックする。
            // 取り消し対象がまだ無い状態でのUndoAsyncは何もせず成功するだけの安全な操作
            // （「何も取り消せるものが無い場合は成功のうえnullを返す」実装）なので、
            // 純粋にPushとPopが同時多発したときの例外の有無だけを見られる。
            var tasks = new Task[n];
            for (var i = 0; i < n; i++)
            {
                if (i % 2 == 0)
                {
                    var p = paths[i];
                    tasks[i] = Task.Run(async () =>
                    {
                        var staged = await store.StageAsync(p, isDirectory: false);
                        if (staged.IsSuccess) File.Delete(p);
                    });
                }
                else
                {
                    tasks[i] = Task.Run(async () => await store.UndoAsync());
                }
            }

            var act = async () => await Task.WhenAll(tasks);
            await act.Should().NotThrowAsync(
                $"attempt={attempt}: PushとPopの並行実行で例外が発生した（\"Stack empty\"等）。" +
                "DeleteUndoStore内部が真のスレッド安全性を持つ必要がある");
        }
    }

    [Fact(DisplayName = "退避対象が既に存在しない場合はE405として失敗する")]
    public async Task 退避対象が存在しない場合は失敗する()
    {
        using var ws = new TempWorkspace();
        var appPaths = new AppPaths(ws.CreateDirectory("app"));
        var store = new DeleteUndoStore(appPaths);

        var missing = ws.Combine("project/missing.txt");
        var staged = await store.StageAsync(missing, isDirectory: false);

        staged.IsSuccess.Should().BeFalse();
        staged.Issues.Should().Contain(i => i.Code == ErrorCode.E405);
        store.CanUndo.Should().BeFalse();
    }
}
