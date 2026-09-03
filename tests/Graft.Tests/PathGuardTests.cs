using System;
using System.Collections.Generic;
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

    // ------------------------------------------------------------------
    // v1.0.14 実機不具合対応: マップ済みネットワークドライブ（Z:\...）上のプロジェクトで
    // 既存ファイルの変更が全件E210になる不具合。
    //
    // 【確定していること（実機ログ）】 projects.jsonの保存値は
    // "Z:\営業部\...\MAI-History" と正常だったのに、PathGuard.NormalizeRootの戻り値
    // （environmentログのtargetPath）が
    // "C:\追加\Graft\UNC\gfs\inaden\営業部\...\MAI-History" になっていた。
    // Path.GetFullPathはResolveRealPathより手前にしか無いため、カレントディレクトリ
    // （C:\追加\Graft＝exeのフォルダ）が混ざったのはResolveRealPathの内部である。
    //
    // 【確定していること（.NETの実装）】 win-x64ランタイム8.0.29の
    // System.Private.CoreLib を逆コンパイルしたところ、
    // System.IO.FileSystem.GetFinalLinkTarget は GetFinalPathNameByHandle の戻り値から
    // 「呼び出し側のパスが拡張表記でなければ先頭4文字を無条件に切り落とす」実装だった。
    // ネットワーク対象では戻り値が \\?\UNC\... のため、切り落とすと
    // "UNC\サーバ名\共有名\..." という相対パスに見える文字列になる。
    // FileSystemInfo.ToString()（OriginalPath）はこの生の値を返し、FullNameは
    // Path.GetFullPath済み＝カレントディレクトリと連結済みである。
    //
    // 【Linuxでは確認できないこと】 実機でこの経路を通ること自体（ネットワーク上の
    // ジャンクション／シンボリックリンクの解決、GetFinalPathNameByHandleの戻り値）は
    // **Windows実機でしか確認できない**。以下のテストは、PathGuard側で文字列処理として
    // 切り出した判断（SanitizeLinkTarget / ResolveRealPathCore）だけを固定する。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "v1.0.14: リンク解決の生の値が\"UNC\\\\server\\\\share\\\\...\"でも、FullNameではなく生の値からUNC表記へ復元する")]
    public void 化けたリンク実体は生の値から復元される()
    {
        // .NETが返す2つの値をそのまま再現する。
        // rawTarget: GetFinalPathNameByHandleの \\?\UNC\gfs\inaden\営業部 から先頭4文字が落ちた形
        // fullName : それをPath.GetFullPathした形（exeフォルダと連結済み＝手遅れの値）
        const string rawTarget = @"UNC\gfs\inaden\営業部";
        const string fullName = @"C:\追加\Graft\UNC\gfs\inaden\営業部";

        var sanitized = PathGuard.SanitizeLinkTarget(rawTarget, fullName);

        sanitized.Should().Be(@"\\gfs\inaden\営業部",
            "カレントディレクトリと連結済みのFullNameではなく、生の値を復元して使うべき");
    }

    [Fact(DisplayName = "v1.0.14: ローカル対象（\\\\?\\C:\\...から4文字落ちた正常な形）はそのまま使う")]
    public void ローカル対象のリンク実体はそのまま使われる()
    {
        // ローカルなら \\?\C:\real\path から4文字落として C:\real\path となり、.NETの処理は正しい。
        // 実機でローカルのプロジェクトだけ正常だったことと整合する。
        PathGuard.SanitizeLinkTarget(@"C:\real\path", @"C:\real\path").Should().Be(@"C:\real\path");
        PathGuard.SanitizeLinkTarget("/var/real/path", "/var/real/path").Should().Be("/var/real/path");
    }

    [Fact(DisplayName = "v1.0.14: 拡張表記（\\\\?\\UNC\\・\\\\?\\）が残っていれば通常表記へ戻す")]
    public void 拡張表記のリンク実体は通常表記へ戻る()
    {
        PathGuard.SanitizeLinkTarget(@"\\?\UNC\gfs\inaden\営業部", @"\\?\UNC\gfs\inaden\営業部")
            .Should().Be(@"\\gfs\inaden\営業部");
        PathGuard.SanitizeLinkTarget(@"\\?\C:\real\path", @"\\?\C:\real\path")
            .Should().Be(@"C:\real\path");
    }

    [Fact(DisplayName = "v1.0.14: 生の値が取れないときはFullNameへ後退し、拡張表記だけは剥がす")]
    public void 生の値が無ければFullNameへ後退する()
    {
        PathGuard.SanitizeLinkTarget(null, @"\\?\UNC\gfs\inaden\営業部").Should().Be(@"\\gfs\inaden\営業部");
        PathGuard.SanitizeLinkTarget("", @"C:\real\path").Should().Be(@"C:\real\path");
    }

    [Fact(DisplayName = "v1.0.14: どちらからも絶対パスを得られなければnull（リンクとして解決しない）")]
    public void 絶対パスを得られなければnull()
    {
        PathGuard.SanitizeLinkTarget(null, null).Should().BeNull();
        PathGuard.SanitizeLinkTarget("", "").Should().BeNull();
        // "UNC\"でも"\\?\"でもない、ただの相対パス。復元しようがないので採用しない。
        PathGuard.SanitizeLinkTarget(@"relative\target", @"relative\target").Should().BeNull();
    }

    [Fact(DisplayName = "v1.0.14: ResolveRealPathCoreはリンク解決が相対パスを返したら元のパスを返す（カレントディレクトリと連結させない）")]
    public void 相対パスへ転落したら元のパスを返す()
    {
        // SanitizeLinkTargetを素通りしてしまった場合の最後の砦。
        // 区切り文字が両OSで有効な「ルートを持つパス」を使い、Linux上でも組み立て経路を
        // 実際に通す（Z:\... はLinuxではPath.GetPathRootが空を返し、早期returnで
        // 素通りしてしまうため、この検証には使えない）。
        const string input = "/net/営業部/02-国営課/MAI-History";
        static bool IsFirstSegment(string path) => path.EndsWith("営業部", StringComparison.Ordinal);

        // 対照: リンクの実体が絶対パスなら、従来どおりそこから組み立てが進む
        // （＝このテストが「早期returnで素通りしただけ」ではないことの担保）。
        PathGuard.ResolveRealPathCore(input, p => IsFirstSegment(p) ? "/real/営業部" : null)
            .Should().NotBe(input, "絶対パスの実体からは組み立てが進むはず");

        // 本題: 同じ位置で相対パス（実機で.NETが返していた形）が返ったら元のパスへ後退する。
        PathGuard.ResolveRealPathCore(input, p => IsFirstSegment(p) ? @"UNC\gfs\inaden\営業部" : null)
            .Should().Be(input,
                "相対パスのまま組み立てを続けると、呼び出し元のPath.GetFullPathでカレントディレクトリと連結されてしまう");
    }

    [Fact(DisplayName = "v1.0.14: ResolveRealPathCoreはルートを持たない入力をそのまま返す")]
    public void ルートを持たない入力はそのまま返る()
    {
        // 従来はcurrentが空文字から始まり、セグメントを連結して相対パスを組み上げていた。
        const string input = @"UNC\gfs\inaden\営業部";

        PathGuard.ResolveRealPathCore(input, _ => null).Should().Be(input);
        PathGuard.ResolveRealPathCore("", _ => null).Should().Be("");
    }

    [Theory(DisplayName = "v1.0.14: リンクが無ければ、マップ済みドライブ・UNC・ローカルのいずれも入力どおりに組み立てる")]
    [InlineData(@"Z:\営業部\02-国営課\MAI-History")]
    [InlineData(@"\\gfs\inaden\営業部\02-国営課\MAI-History")]
    [InlineData(@"C:\Users\name\project")]
    public void リンクが無ければ入力どおりに組み立てる(string input)
    {
        if (!OperatingSystem.IsWindows()) return;   // Path.GetPathRoot / Path.Combine の区切り解釈がOS依存のため

        PathGuard.ResolveRealPathCore(input, _ => null).Should().Be(input);
    }

    [Fact(DisplayName = "v1.0.14: リンク解決が絶対パスを返した場合は、そこから続きを組み立てる（従来どおり）")]
    public void 絶対パスのリンク実体からは続きを組み立てる()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 1段目（Z:\営業部）がネットワーク上のジャンクションで \\gfs\inaden\営業部 が実体、
        // という実機で起きていたはずの形。以降のセグメントはその実体の下へ連結される。
        static string? ResolveFirstSegment(string path)
            => path == @"Z:\営業部" ? @"\\gfs\inaden\営業部" : null;

        PathGuard.ResolveRealPathCore(@"Z:\営業部\02-国営課\MAI-History", ResolveFirstSegment)
            .Should().Be(@"\\gfs\inaden\営業部\02-国営課\MAI-History");
    }

    // ------------------------------------------------------------------
    // v1.0.14 実機不具合対応: マップ済みネットワークドライブ（Z:\...）上のプロジェクトで、
    // プロジェクト直下の普通のファイル（theme.js / settings.js）が
    // 「E201 シンボリックリンク経由でルート外を参照しています」で拒否される不具合。
    //
    // 【実機ログ 20260902 で確定したこと】
    //  (1) 同じ projects.json の値に対して、PathGuard.NormalizeRoot の戻り値
    //      （environmentログ）が時刻によって2通りに揺れていた。
    //        14:03:09 EPSEnhance   → \\gfs\inaden\営業部\...（UNC共有）
    //        14:03:57 MAI-History  → Z:\営業部\...（ネットワークドライブ）
    //        15:37:23 MAI-History  → \\gfs\inaden\営業部\...（UNC共有）
    //      Z: は \\gfs\inaden へのマップなので、両者は同じ場所を指している。
    //  (2) v1.0.14で入れた path-guard イベントは、このログに1件も出ていない。つまり
    //      「実体解決が絶対パスにならず後退した」経路は通っていない。揺れているのは
    //      Directory.ResolveLinkTarget が「そこはリンクだ」と報告するかどうかそのもの。
    //  (3) E201 が出たのはルートが Z: 表記だった時間帯（16:17台）だけで、
    //      両者の表記が揃っていた時間帯（15:39・15:46・15:51）の適用は成功している。
    //
    // 【原因】 _root は NormalizeRoot が「PathGuardを作った時点で1回だけ」実体解決した値。
    // 一方 Resolve は、ファイル1件ごとに結合後のパスを「ドライブルートから丸ごと」
    // 実体解決し直していた。この2回の解決結果が食い違うと、同じ場所を指しているのに
    // 前方一致（IsWithinRoot）が外れてE201になる。
    //
    // 【対処】 実体解決の起点を _root そのものにし、ルートより下の構成要素だけを辿る
    // （ResolveRealPathBelowRootCore）。加えて第二の砦として、ルートの実体とも突き合わせる。
    //
    // 【Linuxでは確認できないこと】 Z: と \\gfs\inaden が同じ場所を指すこと自体、および
    // ResolveLinkTarget の報告が揺れることは Windows 実機でしか再現できない。以下のテストは
    // 「揺れが起きたときに前方一致がどう転ぶか」という判断（純粋な文字列処理・走査ロジック）
    // だけを固定する。なお .github/workflows/ci.yml は runs-on: ubuntu-latest のため、
    // OperatingSystem.IsWindows() で守った検証は CI では中身が実行されない
    // （テスト自体は成功として数えられるが、Windows専用の表明は評価されない）。
    // ------------------------------------------------------------------

    /// <summary>区切り文字をOSのものへ揃える。判定は前方一致なので、両OSで同じ意味になる。</summary>
    private static string P(string slashPath) => slashPath.Replace('/', Path.DirectorySeparatorChar);

    [Fact(DisplayName = "v1.0.14（本命）: ルートより上の構成要素は実体解決し直さないので、表記が揺れても前方一致が外れない")]
    public void ルートより上の構成要素は実体解決しない()
    {
        // 実機で起きていた形を、リンク解決だけ差し替えて再現する。
        // ルートの途中（営業部）が「あるときはリンクとして報告され、あるときは報告されない」。
        var root = P("/net/営業部/02-国営課/MAI-History");
        var combined = P("/net/営業部/02-国営課/MAI-History/theme.js");
        static string? DriftsToOtherNotation(string path)
            => path == P("/net/営業部") ? P("/gfs/inaden/営業部") : null;

        // 修正前の走査（ドライブルートから丸ごと）。ルートの途中で表記が変わってしまい、
        // _root（＝Z:表記のまま）との前方一致が外れる＝実機のE201そのもの。
        var wholeWalk = PathGuard.ResolveRealPathCore(combined, DriftsToOtherNotation);
        PathGuard.IsWithin(wholeWalk, root).Should().BeFalse(
            "前提: 丸ごと辿ると表記が入れ替わり、ルートとの前方一致が外れる（これが実機の症状）");

        // 修正後の走査。起点がルートなので、ルートより上の構成要素には触れない。
        var belowRootWalk = PathGuard.ResolveRealPathBelowRootCore(root, combined, DriftsToOtherNotation);

        belowRootWalk.Should().Be(combined);
        PathGuard.IsWithin(belowRootWalk, root).Should().BeTrue(
            "プロジェクト直下の普通のファイルが、リンク解決の揺れだけでルート外扱いされてはならない");
    }

    [Fact(DisplayName = "v1.0.14（安全機構）: ルートより下のリンクは従来どおり実体解決され、ルート外なら前方一致が外れる")]
    public void ルートより下のリンクは従来どおり解決される()
    {
        var root = P("/proj");
        var combined = P("/proj/linked/secret.txt");
        static string? ResolveLinkedToOutside(string path)
            => path == P("/proj/linked") ? P("/outside/secret-dir") : null;

        var real = PathGuard.ResolveRealPathBelowRootCore(root, combined, ResolveLinkedToOutside);

        real.Should().Be(P("/outside/secret-dir/secret.txt"),
            "ルート配下のリンクは実体まで解決されるべき（脱出検知はこれが前提）");
        PathGuard.IsWithin(real, root).Should().BeFalse("ルート外を指すリンクは従来どおり弾かれるべき");
    }

    [Fact(DisplayName = "v1.0.14: ルート下のリンク解決が相対パスを返したら、組み立てを打ち切って結合後のパスを使う")]
    public void ルート下のリンクが相対パスを返したら打ち切る()
    {
        // v1.0.14と同じ最後の砦。相対パスのまま組み立てると呼び出し元の
        // Path.GetFullPath でカレントディレクトリと連結されてしまう。
        var root = P("/proj");
        var combined = P("/proj/linked/secret.txt");
        static string? ReturnsRelative(string path)
            => path == P("/proj/linked") ? @"UNC\gfs\inaden\営業部" : null;

        PathGuard.ResolveRealPathBelowRootCore(root, combined, ReturnsRelative).Should().Be(combined);
    }

    [Fact(DisplayName = "v1.0.14: 結合後のパスがルートで始まらない想定外の入力は、従来どおり全体を辿る")]
    public void ルートで始まらない入力は全体を辿る()
    {
        var combined = P("/proj/a.txt");

        // 起点がずれた状態で黙って走査を始めると、ルートの下だけを見たつもりが
        // 何も見ていないことになる。安全側に倒して従来の全体走査へ落とす。
        PathGuard.ResolveRealPathBelowRootCore(P("/other"), combined, _ => null)
            .Should().Be(PathGuard.ResolveRealPathCore(combined, _ => null));
        PathGuard.ResolveRealPathBelowRootCore("", combined, _ => null).Should().Be(combined);
    }

    [Fact(DisplayName = "v1.0.14（回帰）: 区切り文字を挟まない同名接頭辞（root=/proj, combined=/proj2/...）でも走査を始めない")]
    public void 同名接頭辞は走査の起点にしない()
    {
        // Copilotのレビュー指摘。フォールバック判定が単なるStartsWith(root)だと、
        // root="/proj" に対して combined="/proj2/a.txt" が素通りしてしまう。
        var root = P("/proj");
        var combined = P("/proj2/a.txt");

        var real = PathGuard.ResolveRealPathBelowRootCore(root, combined, _ => null);

        // 修正前の挙動（対照）: remainderが "2/a.txt" になり、走査が
        // /proj → /proj/2 → /proj/2/a.txt と実在しない別のパスへ組み替わっていた。
        // しかもその値は呼び出し元のルート内判定を通ってしまう＝ルート外がルート内に見える。
        real.Should().NotBe(P("/proj/2/a.txt"), "ルート外のパスがルート配下へ組み替わってはならない");
        real.Should().Be(combined, "ルート配下でない入力は、従来どおり全体を辿った結果になるべき");
        PathGuard.IsWithin(real, root).Should().BeFalse("組み替わりの結果、ルート内と誤判定されてはならない");
    }

    [Fact(DisplayName = "v1.0.14: 解決の内訳（診断ログ用）に、どの構成要素が何へ解決されたかが記録される")]
    public void 解決の内訳が記録される()
    {
        var root = P("/proj");
        var combined = P("/proj/linked/secret.txt");
        static string? ResolveLinkedToOutside(string path)
            => path == P("/proj/linked") ? P("/outside/secret-dir") : null;
        var trace = new List<string>();

        PathGuard.ResolveRealPathBelowRootCore(root, combined, ResolveLinkedToOutside, trace);

        trace.Should().ContainSingle().Which.Should().Be($"{P("/proj/linked")} → {P("/outside/secret-dir")}");
    }

    [Theory(DisplayName = "v1.0.14: 表記が食い違う組み合わせで前方一致がどう転ぶか（実機の症状の表）")]
    // ルートと実体の表記が揃っていれば通る（実機で適用が成功していた時間帯）。
    [InlineData("/gfs/inaden/営業部/MAI-History/theme.js", "/gfs/inaden/営業部/MAI-History", true)]
    [InlineData("/mapped/MAI-History/theme.js", "/mapped/MAI-History", true)]
    // ルートと同じ位置そのもの。
    [InlineData("/mapped/MAI-History", "/mapped/MAI-History", true)]
    // 表記が食い違うと外れる（16:17台のE201）。だからこそ実体ルートとも突き合わせる必要がある。
    [InlineData("/gfs/inaden/営業部/MAI-History/theme.js", "/mapped/MAI-History", false)]
    // 本当にルート外。実体ルート側（UNC表記）から見ても外れる＝従来どおり拒否されるべき。
    [InlineData("/gfs/inaden/営業部/OTHER-PROJECT/theme.js", "/gfs/inaden/営業部/MAI-History", false)]
    // 前方一致の落とし穴（区切りを挟まない同名接頭辞）。
    [InlineData("/mapped/MAI-History2/theme.js", "/mapped/MAI-History", false)]
    public void 表記の食い違いと前方一致の表(string candidate, string root, bool expected)
    {
        PathGuard.IsWithin(P(candidate), P(root)).Should().Be(expected);
    }

    [Fact(DisplayName = "v1.0.14: 実体ルートとも突き合わせても、本当にルート外を指すリンクは通らない")]
    public void 実体ルートとの突き合わせでもルート外は通らない()
    {
        // IsWithinRealRoot が行う判定を、両方のルートを並べた形でそのまま固定する。
        // 「_root（マップ済みドライブ表記）」と「実体ルート（UNC表記）」の2つを許すが、
        // 別プロジェクトを指す実体はそのどちらからも外れる。
        var mappedRoot = P("/mapped/MAI-History");
        var realRoot = P("/gfs/inaden/営業部/MAI-History");

        var insideByRealRoot = P("/gfs/inaden/営業部/MAI-History/theme.js");
        (PathGuard.IsWithin(insideByRealRoot, mappedRoot) || PathGuard.IsWithin(insideByRealRoot, realRoot))
            .Should().BeTrue("同じ場所を指す別表記は、実体ルート側で拾えるべき");

        var outside = P("/gfs/inaden/営業部/OTHER-PROJECT/theme.js");
        (PathGuard.IsWithin(outside, mappedRoot) || PathGuard.IsWithin(outside, realRoot))
            .Should().BeFalse("本当にルート外を指すリンクは、どちらのルートからも外れるべき");

        var farOutside = P("/etc/passwd");
        (PathGuard.IsWithin(farOutside, mappedRoot) || PathGuard.IsWithin(farOutside, realRoot))
            .Should().BeFalse();
    }

    [Fact(DisplayName = "v1.0.14: E201で拒否したとき、ルート・結合後・実体解決後の実際の値がログへ残る")]
    public void E201のときは判断材料がログへ残る()
    {
        using var ws = new TempWorkspace();
        using var outside = new TempWorkspace();
        outside.WriteText("secret.txt", "ルート外の内容");
        if (TryCreateSymlinkOrSkip(ws, "linked", outside.RootPath) is null) return;

        var messages = new System.Collections.Concurrent.ConcurrentBag<string>();
        var previous = PathGuard.AnomalyLogger;
        try
        {
            PathGuard.AnomalyLogger = messages.Add;
            var guard = new PathGuard(ws.RootPath, PathGuardOptions.Default);

            var result = guard.Resolve("linked/secret.txt");

            result.IsSuccess.Should().BeFalse();
            result.Errors.Single().Code.Should().Be(ErrorCode.E201);
        }
        finally
        {
            PathGuard.AnomalyLogger = previous;
        }

        // v1.0.14のpath-guardログは「絶対パスへ解決できなかった」異常時にしか出ないため、
        // 実機ログには1件も残らず、_rootとrealの実際の文字列を推定するしかなかった。
        // 二度と同じ推定をしなくて済むよう、拒否した瞬間の値をすべて残す。
        var escape = messages.Should().ContainSingle(m => m.Contains("E201", StringComparison.Ordinal)).Which;
        escape.Should().Contain("linked/secret.txt");
        escape.Should().Contain("ルート=");
        escape.Should().Contain("結合後=");
        escape.Should().Contain("実体解決後=");
        escape.Should().Contain(outside.RootPath, "どこへ逃げたのか（実体解決の行き先）が分からないと原因を追えない");
    }

}
