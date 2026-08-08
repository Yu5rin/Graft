using System.ComponentModel;
using System.IO;
using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 実機検証で発見した不具合3（エラーメッセージに英語の生例外が出る）に対する
/// <see cref="ExceptionMessages"/> の単体テスト。
/// 再現手順は「存在しないフォルダをrootに持つプロジェクトで起動する」で、そのとき
/// 実際に投げられる例外は<see cref="FileSystemWatcher"/>のコンストラクタが投げる
/// <see cref="ArgumentException"/>（"The directory name '...' does not exist. (Parameter 'path')"）
/// であり、<see cref="DirectoryNotFoundException"/>ではない点に注意（.NETのAPI仕様）。
/// </summary>
public class ExceptionMessagesTests
{
    [Fact(DisplayName = "不具合3の実例: 存在しないフォルダでFileSystemWatcherを開始すると、Describeが日本語の理由へ変換する")]
    public void FileSystemWatcherが存在しないフォルダで投げる例外は日本語化される()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "graft-tests-does-not-exist-" + Guid.NewGuid().ToString("N"));
        Directory.Exists(missingPath).Should().BeFalse("テストの前提として実在してはならない");

        Exception? caught = null;
        try
        {
            _ = new FileSystemWatcher(missingPath);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull("FileSystemWatcherは存在しないディレクトリに対して例外を投げるはず");
        var described = ExceptionMessages.Describe(caught!);

        described.Should().Contain("フォルダが見つかりません", "英語の生例外文（does not exist等）をそのまま出してはならない");
        described.Should().Contain(caught!.Message, "原文はログ・原因調査のため詳細として残す");
    }

    [Fact(DisplayName = "DirectoryNotFoundExceptionは「フォルダが見つかりません」に変換される")]
    public void DirectoryNotFoundExceptionはフォルダが見つからない旨になる()
    {
        var ex = new DirectoryNotFoundException("Could not find a part of the path 'X'.");

        ExceptionMessages.Describe(ex).Should().StartWith("フォルダが見つかりません。");
    }

    [Fact(DisplayName = "FileNotFoundExceptionは「ファイルが見つかりません」に変換される")]
    public void FileNotFoundExceptionはファイルが見つからない旨になる()
    {
        var ex = new FileNotFoundException("Could not find file 'X'.");

        ExceptionMessages.Describe(ex).Should().StartWith("ファイルが見つかりません。");
    }

    [Fact(DisplayName = "UnauthorizedAccessExceptionは「アクセスが拒否されました」に変換される")]
    public void UnauthorizedAccessExceptionはアクセス拒否の旨になる()
    {
        var ex = new UnauthorizedAccessException("Access to the path 'X' is denied.");

        ExceptionMessages.Describe(ex).Should().StartWith("アクセスが拒否されました。");
    }

    [Fact(DisplayName = "Win32Exception（プロセス起動失敗）はコマンド実行失敗の旨になる")]
    public void Win32Exceptionはコマンド実行失敗の旨になる()
    {
        var ex = new Win32Exception(2, "The system cannot find the file specified");

        ExceptionMessages.Describe(ex).Should().StartWith("コマンドを実行できませんでした。");
    }

    [Fact(DisplayName = "共有違反相当のIOExceptionは「他のアプリが使用中」の旨になる")]
    public void 共有違反のIOExceptionは使用中の旨になる()
    {
        var ex = new IOException("The process cannot access the file 'X' because it is being used by another process.");

        ExceptionMessages.Describe(ex).Should().StartWith("他のアプリがファイルを使用中の可能性があります。");
    }

    [Fact(DisplayName = "分類できない例外でも、次にどうすればよいか分かる日本語の説明＋原文を返す")]
    public void 想定外の例外でも日本語の説明と原文を返す()
    {
        var ex = new InvalidOperationException("Some totally unexpected internal error.");

        var described = ExceptionMessages.Describe(ex);

        described.Should().Contain("予期しないエラー");
        described.Should().Contain("Some totally unexpected internal error.", "原文は詳細として残してよい");
    }

    [Fact(DisplayName = "変換後のメッセージは常に原文（詳細）を含む")]
    public void 変換後のメッセージは常に原文を含む()
    {
        var ex = new DirectoryNotFoundException("The directory name 'X' does not exist. (Parameter 'path')");

        ExceptionMessages.Describe(ex).Should().Contain(ex.Message);
    }

    [Fact(DisplayName = "nullを渡すと例外になる")]
    public void nullを渡すと例外になる()
    {
        var act = () => ExceptionMessages.Describe(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
