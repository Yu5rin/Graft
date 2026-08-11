using System.Threading;
using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 実機バグ修正の回帰テスト: 多重起動防止のMutexがLinuxでセッションをまたいで機能しなかった
/// 不具合（実機で同一発行フォルダのGraftを2つ起動すると両方立ち上がってしまっていた）と、
/// 権限問題等で判定不能な場合に「多重起動とみなして起動を止める」へ倒していた安全側の
/// 判断の見直し（詳しい経緯は Core/SingleInstanceGuard.cs のクラスコメントを参照）。
///
/// 名前付きMutexが本当にセッションをまたいで機能するかどうかはプロセス（別セッション）を
/// またがないと検証できないが、以下は単体テストで確認できる:
///   - 実際に "Global\" を付けた名前でMutexが作られていること
///     （<see cref="Mutex.TryOpenExisting(string, out Mutex)"/> で別ハンドルから
///     "Global\"付きの名前を直接開けることで確認する）。
///   - 同一名を同一プロセス内で再取得しようとした場合に失敗すること（名前付きカーネル
///     オブジェクトはプロセス局所ではなくOS共有のため、多重起動検知の基本動作の確認になる）。
///   - Mutex名の作成そのものが例外になる場合に、「判定不能＝多重起動とみなす」ではなく
///     「起動を許可する」側へ縮退していること（Unix版ランタイムは名前に'/'を含む場合に
///     必ず<see cref="IOException"/>を投げる実挙動を利用して再現する。実機で確認済み）。
/// </summary>
public class SingleInstanceGuardTests
{
    [Fact(DisplayName = "TryAcquireはGlobal\\プレフィックス付きの名前でMutexを作成する")]
    public void GlobalプレフィックスでMutexを作成する()
    {
        var name = $"GraftTest.Global.{Guid.NewGuid():N}";
        using var guard = SingleInstanceGuard.TryAcquire(name);

        guard.Should().NotBeNull();

        var opened = Mutex.TryOpenExisting(@"Global\" + name, out var handle);
        opened.Should().BeTrue("Global\\プレフィックス付きの名前で実際にMutexが作られているはず");
        handle?.Dispose();
    }

    [Fact(DisplayName = "既に取得済みの名前を別ハンドルで取得しようとすると失敗する（多重起動の検知そのもの）")]
    public void 取得済みの名前は再取得できない()
    {
        var name = $"GraftTest.Dup.{Guid.NewGuid():N}";
        using var first = SingleInstanceGuard.TryAcquire(name);
        first.Should().NotBeNull();

        using var second = SingleInstanceGuard.TryAcquire(name);
        second.Should().BeNull("既に保持されている名前は多重起動として検知されるはず");
    }

    [Fact(DisplayName = "Disposeで解放した後は同じ名前を再取得できる")]
    public void 解放後は再取得できる()
    {
        var name = $"GraftTest.Reacquire.{Guid.NewGuid():N}";
        var first = SingleInstanceGuard.TryAcquire(name);
        first.Should().NotBeNull();
        first!.Dispose();

        using var second = SingleInstanceGuard.TryAcquire(name);
        second.Should().NotBeNull("解放後は新規プロセスと同様に取得できるはず");
    }

    [Fact(DisplayName = "判定不能（Mutex作成が例外になる）場合は多重起動とみなさず起動を許可する側へ縮退する")]
    public void 判定不能な場合は起動を許可する側へ縮退する()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // 下記の再現方法（名前に'/'を含める）はUnix版ランタイム固有の挙動のためスキップ。
        }

        // Unix版の名前付きMutexは実体をファイルパスとして扱うため、名前に'/'を含めると
        // "Global\"付き・無し双方の作成試行が必ずIOExceptionになる（実機で確認済み。
        // TryCreateMutexが捕捉する例外種別の1つ）。以前の実装ならここでnullを返し
        // 「多重起動とみなして起動を止める」を選んでいたが、権限やOS制約による判定不能を
        // 多重起動と混同すべきではない（本タスクの発端となった実害の裏返し）。
        var name = $"GraftTest/Indeterminate/{Guid.NewGuid():N}";

        using var guard = SingleInstanceGuard.TryAcquire(name);

        guard.Should().NotBeNull("判定不能な場合は起動をブロックしない側に倒すべき");

        // 実体（Mutexハンドル）を持たない縮退のため、Disposeも例外を投げず静かに終わること。
        var act = () => guard!.Dispose();
        act.Should().NotThrow();
    }

    [Fact(DisplayName = "BuildInstanceScopedNameは既知の入力に対して決定的な名前を作る（プロセスごとに変わるGetHashCodeではないことの回帰確認）")]
    public void 既知の入力に対して決定的な名前を作る()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // 大小文字を正規化する分岐があるため、期待値はLinux/macOS想定に限定する。
        }

        var name = SingleInstanceGuard.BuildInstanceScopedName("Graft.SingleInstance.", "/tmp/graft-test-path");

        name.Should().Be("Graft.SingleInstance.DF77D654546C9BF3");
    }

    [Fact(DisplayName = "課題4: 異なる発行フォルダは異なる名前になる")]
    public void 異なる発行フォルダは異なる名前になる()
    {
        var nameA = SingleInstanceGuard.BuildInstanceScopedName("Graft.SingleInstance.", "/tmp/graft-a");
        var nameB = SingleInstanceGuard.BuildInstanceScopedName("Graft.SingleInstance.", "/tmp/graft-b");

        nameA.Should().NotBe(nameB, "別々の発行フォルダに置かれたGraftは互いに独立したインスタンスとして扱うべき");
    }

    [Fact(DisplayName = "課題4: 同じ発行フォルダは同じ名前になる")]
    public void 同じ発行フォルダは同じ名前になる()
    {
        var nameA = SingleInstanceGuard.BuildInstanceScopedName("Graft.SingleInstance.", "/tmp/graft-a");
        var nameB = SingleInstanceGuard.BuildInstanceScopedName("Graft.SingleInstance.", "/tmp/graft-a");

        nameA.Should().Be(nameB, "同じ発行フォルダを指すなら、別プロセスからの呼び出しでも常に同じ名前になる必要がある");
    }
}
