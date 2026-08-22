using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Graft.Core;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書4.7・13章（PathGuard）の単体テスト。ルート外パスの拒否、拡張子・サイズ・
/// 読み取り専用の判定、大文字小文字を無視した比較、"\" 区切りの受理、
/// シンボリックリンク経由でのルート外脱出の防止を検証する。
/// </summary>
public class PathGuardTests
{
    [Theory(DisplayName = "絶対パス・上位ディレクトリ参照はE201になる")]
    [InlineData("/etc/passwd")]
    [InlineData("../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("sub/../../../outside.txt")]
    public void ルート外パスはE201になる(string relativePath)
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(relativePath);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E201);
    }

    [Fact(DisplayName = "空のパスはE201になる")]
    public void 空のパスはE201になる()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve("   ");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E201);
    }

    [Fact(DisplayName = "未許可拡張子はE202になる")]
    public void 未許可拡張子はE202になる()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve("scripts/malicious.exe");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E202);
    }

    [Fact(DisplayName = "不具合2: 拡張子の無いファイル名（Dockerfile等）はホワイトリストの対象外として許可される")]
    public void 拡張子の無いファイル名は許可される()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve("Dockerfile");

        result.IsSuccess.Should().BeTrue(
            "拡張子ホワイトリストは危険な拡張子（.exe等）の遮断が目的であり、拡張子そのものが無い名前は対象外のはず");
    }

    [Fact(DisplayName = "拡張子の比較は大文字小文字を無視する")]
    public void 拡張子の比較は大文字小文字を無視する()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve("README.TXT");

        result.IsSuccess.Should().BeTrue(".txt は許可拡張子であり大文字小文字を無視して比較されるべき");
    }

    [Fact(DisplayName = "\\区切りのパスも受理される")]
    public void バックスラッシュ区切りのパスも受理される()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve(@"src\features\module.py");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(Path.Combine(ws.RootPath, "src", "features", "module.py"));
    }

    [Fact(DisplayName = "サイズ上限を超えるファイルはE203になる")]
    public void サイズ上限超過はE203になる()
    {
        using var ws = new TempWorkspace();
        var options = PathGuardOptions.Default with { MaxFileSizeMB = 1 };
        var guard = new PathGuard(ws.RootPath, options);

        var bigContent = new byte[2 * 1024 * 1024];
        ws.WriteBytes("data/large.txt", bigContent);

        var result = guard.Inspect("data/large.txt");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E203);
    }

    [Fact(DisplayName = "サイズ上限以内のファイルは成功する")]
    public void サイズ上限以内のファイルは成功する()
    {
        using var ws = new TempWorkspace();
        var options = PathGuardOptions.Default with { MaxFileSizeMB = 1 };
        var guard = new PathGuard(ws.RootPath, options);
        ws.WriteText("data/small.txt", "こんにちは");

        var result = guard.Inspect("data/small.txt");

        result.IsSuccess.Should().BeTrue();
        result.Value.Exists.Should().BeTrue();
    }

    [Fact(DisplayName = "読み取り専用属性のファイルは警告付きで検出される")]
    public void 読み取り専用属性は警告付きで検出される()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);
        ws.WriteText("data/locked.txt", "内容");
        ws.SetReadOnly("data/locked.txt", true);

        try
        {
            var result = guard.Inspect("data/locked.txt");

            result.IsSuccess.Should().BeTrue("読み取り専用は警告であり致命的失敗ではないため");
            result.Value.IsReadOnly.Should().BeTrue();
            var warning = result.Issues.Single(i => i.Code == ErrorCode.E205);
            warning.Severity.Should().Be(Severity.Warning);

            // 課題2-1回帰テスト: 「確認のうえ属性を解除できます」は誰が何をするか曖昧だった。
            // ApplyContext.AllowReadOnlyOverrideは常にfalse（Graftが自動で解除することはない）
            // ため、利用者自身が解除する必要があることを明示した文言になっているか確認する。
            warning.Remedy.Should().Contain("解除してから", "Graftが自動で解除するわけではなく、利用者が解除する必要があることを明示するため");
            warning.Remedy.Should().NotBe("確認のうえ属性を解除できます", "誰が何をするか分からない旧文言に戻っていないこと");
        }
        finally
        {
            ws.SetReadOnly("data/locked.txt", false);
        }
    }

    [Fact(DisplayName = "存在しないファイルはロックも読み取り専用も無しとして扱われる")]
    public void 存在しないファイルは追加検証で問題なしとなる()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Inspect("data/not-created-yet.txt");

        result.IsSuccess.Should().BeTrue();
        result.Value.Exists.Should().BeFalse();
        result.Value.IsReadOnly.Should().BeFalse();
        result.Value.IsLocked.Should().BeFalse();
    }

    [Fact(DisplayName = "シンボリックリンク経由でルート外へ出るパスはE201になる")]
    public void シンボリックリンク経由でルート外はE201になる()
    {
        using var ws = new TempWorkspace();
        using var outside = new TempWorkspace();
        outside.WriteText("secret.txt", "ルート外の内容");

        if (TryCreateSymlinkOrSkip(ws, "linked", outside.RootPath) is null) return;
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve("linked/secret.txt");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E201);
    }

    [Fact(DisplayName = "シンボリックリンクがルート内を指す場合は許可される")]
    public void シンボリックリンクがルート内を指す場合は許可される()
    {
        using var ws = new TempWorkspace();
        var realDir = ws.CreateDirectory("real");
        if (TryCreateSymlinkOrSkip(ws, "linked", realDir) is null) return;
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.Resolve("linked/inside.txt");

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// シンボリックリンクを実際に作成してみて、権限不足で作成できない環境ではテストをスキップする
    /// （不具合3）。Windowsでのシンボリックリンク作成には管理者権限または開発者モードが必要で、
    /// 一般ユーザーの通常環境では<see cref="IOException"/>（ERROR_PRIVILEGE_NOT_HELD）になる。
    /// これはこの環境固有の制約であり、テスト対象（PathGuard）の不具合ではないため、
    /// 「常にWindowsならスキップ」ではなく実際に権限エラーになった場合のみスキップする
    /// （開発者モードが有効なWindows環境では通常どおり実行される）。Linux上では通常権限で
    /// 常に成功するため、このテストはLinux上では必ず実行される。
    /// </summary>
    private static string? TryCreateSymlinkOrSkip(TempWorkspace ws, string linkRelativePath, string targetAbsolutePath)
    {
        try
        {
            return ws.CreateDirectorySymlink(linkRelativePath, targetAbsolutePath);
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                "シンボリックリンクを作成する権限が無いためこのテストをスキップします"
                + $"（{ex.Message}）。実行するには、Windowsで「開発者モード」を有効にするか、"
                + "テストを管理者として実行してください。");
            return null;
        }
    }

    [Fact(DisplayName = "1リビジョンあたりのファイル数上限を超えるとE203になる")]
    public void ファイル数上限超過はE203になる()
    {
        using var ws = new TempWorkspace();
        var options = PathGuardOptions.Default with { MaxFilesPerRevision = 2 };
        var guard = new PathGuard(ws.RootPath, options);

        var result = guard.CheckFileCount(3);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E203);
    }

    [Fact(DisplayName = "1リビジョンあたりのファイル数が上限以内なら成功する")]
    public void ファイル数上限以内は成功する()
    {
        using var ws = new TempWorkspace();
        var options = PathGuardOptions.Default with { MaxFilesPerRevision = 2 };
        var guard = new PathGuard(ws.RootPath, options);

        var result = guard.CheckFileCount(2);

        result.IsSuccess.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // エクスプローラへの既存ファイル取り込み（依頼）用のResolveImportTarget。
    // 拡張子ホワイトリストは適用しない（画像等の非テキスト資産の取り込みが主な動機のため）が、
    // ルート外脱出・シンボリックリンク経由の脱出防止は他のResolve系メソッドと同様に必須。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ResolveImportTargetは拡張子ホワイトリストを適用しない（画像等の取り込みが動機のため）")]
    public void ResolveImportTargetは拡張子ホワイトリストを適用しない()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        // .png はPathGuardOptions.Defaultの許可拡張子に含まれず、通常のResolveならE202になる。
        var normal = guard.Resolve("assets/photo.png");
        normal.IsSuccess.Should().BeFalse("前提: 既定の許可拡張子には.pngが含まれない");

        var result = guard.ResolveImportTarget("assets/photo.png");

        result.IsSuccess.Should().BeTrue("取り込みは拡張子ホワイトリストの対象外であるべき");
        result.Value.Should().Be(Path.Combine(ws.RootPath, "assets", "photo.png"));
    }

    [Fact(DisplayName = "ResolveImportTargetでも上位ディレクトリ参照(..)はE201になる")]
    public void ResolveImportTargetも上位ディレクトリ参照はE201になる()
    {
        using var ws = new TempWorkspace();
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.ResolveImportTarget("../outside.png");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E201);
    }

    [Fact(DisplayName = "ResolveImportTargetでもシンボリックリンク経由でルート外へ出るパスはE201になる")]
    public void ResolveImportTargetもシンボリックリンク経由でルート外はE201になる()
    {
        using var ws = new TempWorkspace();
        using var outside = new TempWorkspace();
        outside.WriteText("secret.png", "ルート外の内容");

        if (TryCreateSymlinkOrSkip(ws, "linked", outside.RootPath) is null) return;
        var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

        var result = guard.ResolveImportTarget("linked/secret.png");

        result.IsSuccess.Should().BeFalse();
        result.Errors.Single().Code.Should().Be(ErrorCode.E201);
    }

    // ------------------------------------------------------------------
    // v1.0.7実機不具合対応: ネットワーク上（UNC）のプロジェクトで取り込みが必ず失敗する不具合。
    // 詳しい経緯はLongPath.cs・ProjectStore.csのコメント、変更履歴1.0.7を参照。
    // ------------------------------------------------------------------

    /// <summary>
    /// 実機不具合の再現＋修正確認。修正前は、拡張UNC表記(<c>\\?\UNC\server\share\...</c>)の
    /// 先頭4文字(<c>\\?\</c>)だけが失われた<c>UNC\server\share\...</c>という文字列がRootとして
    /// 渡ると、<see cref="PathGuard"/>のコンストラクタが<c>Path.GetFullPath</c>で
    /// カレントディレクトリ（実機ではexeフォルダ）基準の絶対パスへ誤って解決してしまい、
    /// 実在するはずのファイルがすべて「見つからない」（E210）扱いになっていた。
    /// 本テストは、化けたRootを渡した場合と、あらかじめ正しく復元したRootを渡した場合とで、
    /// <see cref="PathGuard.Resolve"/>の結果が完全に一致することを確認する（＝PathGuardが
    /// 内部でコンストラクタ時に自動復元していることの証明）。この比較はOSのパス解決の
    /// 実装差（Windows/Linuxで絶対パスの判定基準が異なる）に依存しないため、Linux上の
    /// テスト実行環境でも確実に検証できる。
    /// </summary>
    [Fact(DisplayName = "実機不具合の再現と修正確認: 化けたUNCルート(\"UNC\\\\server\\\\share\\\\proj\")は復元後のルートと同じ結果に解決される")]
    public void 化けたUNCルートは復元後のルートと同じ結果に解決される()
    {
        var corruptedRoot = @"UNC\server\share\project"; // \\?\UNC\server\share\project から \\?\ が失われた形
        var recoveredRoot = LongPath.RecoverProjectRoot(corruptedRoot);
        recoveredRoot.Should().Be(@"\\server\share\project", "先頭に\\\\を補って通常のUNC表記へ戻るべき");

        var guardFromCorrupted = new PathGuard(corruptedRoot, PathGuardOptions.Default);
        var guardFromRecovered = new PathGuard(recoveredRoot, PathGuardOptions.Default);

        var resolvedFromCorrupted = guardFromCorrupted.Resolve("src/app.py");
        var resolvedFromRecovered = guardFromRecovered.Resolve("src/app.py");

        resolvedFromCorrupted.IsSuccess.Should().BeTrue();
        resolvedFromCorrupted.Value.Should().Be(
            resolvedFromRecovered.Value,
            "PathGuardは化けたUNC表記のRootを渡されても、あらかじめ正しく復元した場合とまったく同じ絶対パスへ解決するべき" +
            "（修正前はここが一致せず、化けたRoot側だけがカレントディレクトリ基準の誤ったパスになっていた）");
    }

    /// <summary>
    /// 上記テストのWindows実機向け版。Windows上では<c>\\server\share\...</c>が正しく絶対パスと
    /// 認識される（Linux上のテスト実行環境ではバックスラッシュ区切りのUNC表記はOSの
    /// パス解決仕様上そもそも絶対パスと認識されないため、この検証はWindows上でのみ意味を持つ）。
    /// タスク要件「PathGuardにUNCのプロジェクトルートを渡したときにResolveが正しい絶対パスを
    /// 返すこと」を、実際にWindows実機で実行した場合に検証する。
    /// </summary>
    [Fact(DisplayName = "(Windows専用) UNCのプロジェクトルートを渡すとResolveは正しいUNC絶対パスを返す")]
    public void UNCルートのResolveは正しい絶対パスを返す_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var guard = new PathGuard(@"\\server\share\project", PathGuardOptions.Default);

        var result = guard.Resolve("src/app.py");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(@"\\server\share\project\src\app.py");
    }

    /// <summary>同上のWindows専用版: 化けたUNCルートも、Windows実機では正しいUNC絶対パスへ解決される。</summary>
    [Fact(DisplayName = "(Windows専用) 化けたUNCルートはカレントディレクトリ基準ではなく正しいUNC絶対パスへ解決される")]
    public void 化けたUNCルートは正しい絶対パスへ解決される_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var guard = new PathGuard(@"UNC\gfs\inaden\project", PathGuardOptions.Default);

        var result = guard.Resolve("extension/calendar.html");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(@"\\gfs\inaden\project\extension\calendar.html");
        result.Value.Should().NotContain(
            Directory.GetCurrentDirectory(),
            "修正前はカレントディレクトリ（実機ではexeフォルダ）が誤って混入していた（実機不具合の症状そのもの）");
    }

    // ------------------------------------------------------------------
    // v1.0.7実機不具合対応（環境要約ログ）: NormalizeRoot。
    // コンストラクタが実際に使う正規化と同じ結果を、インスタンスを作らずに得られること。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "NormalizeRoot: コンストラクタが実際に使う正規化後の値（_root）と一致する")]
    public void NormalizeRootはコンストラクタの正規化結果と一致する()
    {
        using var ws = new TempWorkspace();
        // 末尾に区切り文字を付けたり相対化要素を混ぜたりして、正規化が実際に効くことも確認する。
        var messyRoot = ws.RootPath + Path.DirectorySeparatorChar;

        var normalized = PathGuard.NormalizeRoot(messyRoot);

        var guard = new PathGuard(messyRoot, PathGuardOptions.Default);
        // PathGuardは正規化後のrootを直接は公開していないため、Resolve("")相当ではなく
        // 「正規化後のrootの直下」を指す相対パスを解決させ、間接的に一致を確認する。
        var resolved = guard.Resolve("file.txt");
        resolved.IsSuccess.Should().BeTrue();
        Path.GetDirectoryName(resolved.Value).Should().Be(normalized);
    }

    [Fact(DisplayName = "NormalizeRoot: 化けたUNC表記（先頭\\\\?\\が失われた形）も復元してから正規化する")]
    public void NormalizeRootは化けたUNC表記も復元する()
    {
        if (!OperatingSystem.IsWindows()) return;

        PathGuard.NormalizeRoot(@"UNC\gfs\inaden\project").Should().Be(@"\\gfs\inaden\project");
    }

    [Fact(DisplayName = "NormalizeRoot: nullはArgumentNullException")]
    public void NormalizeRootはnullで例外()
    {
        Action act = () => PathGuard.NormalizeRoot(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
