using FluentAssertions;
using Graft.Core;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// LongPath.Extended の単体テスト。
/// 実機不具合対応: マップ済みネットワークドライブ上で常に \\?\ プレフィックスを付けていたことが
/// File.Exists 等の失敗（ひいてはE101誤表示）の原因だった。本テストはWindows以外
/// （テスト実行環境含む）でも「Windowsだったらどう判定するか」を確認するため、
/// <see cref="LongPath.ExtendedCore"/> へ isWindows / isNetworkDrive を直接渡して検証する。
/// 公開APIである <see cref="LongPath.Extended(string)"/> 自体も、この実行環境
/// （非Windows）での実際の挙動として別途カバーする。
/// </summary>
public class LongPathTests
{
    [Fact(DisplayName = "空文字・nullはそのまま返る")]
    public void 空文字はそのまま()
    {
        LongPath.ExtendedCore("", isWindows: true, isNetworkDrive: _ => false).Should().Be("");
        LongPath.ExtendedCore(null!, isWindows: true, isNetworkDrive: _ => false).Should().BeNull();
    }

    [Fact(DisplayName = "非Windowsでは長さやネットワークドライブに関わらずそのまま返る")]
    public void 非Windowsではそのまま()
    {
        var longPath = @"C:\" + new string('a', 400);
        LongPath.ExtendedCore(longPath, isWindows: false, isNetworkDrive: _ => true).Should().Be(longPath);
    }

    [Fact(DisplayName = "既に\\\\?\\プレフィックス済みならそのまま返る（冪等）")]
    public void プレフィックス済みはそのまま()
    {
        var already = @"\\?\C:\" + new string('a', 400);
        LongPath.ExtendedCore(already, isWindows: true, isNetworkDrive: _ => false).Should().Be(already);
    }

    [Fact(DisplayName = "MAX_PATH未満のローカルパスはWindowsでもプレフィックスを付けない")]
    public void 短いローカルパスは無変換()
    {
        var shortPath = @"C:\Users\name\project\file.txt";
        shortPath.Length.Should().BeLessThan(LongPath.WindowsMaxPathLength);

        LongPath.ExtendedCore(shortPath, isWindows: true, isNetworkDrive: _ => false).Should().Be(shortPath);
    }

    [Fact(DisplayName = "MAX_PATH以上のローカルパスはWindowsで\\\\?\\プレフィックスが付く")]
    public void 長いローカルパスはプレフィックス付き()
    {
        var longPath = @"C:\" + new string('a', LongPath.WindowsMaxPathLength);
        longPath.Length.Should().BeGreaterThanOrEqualTo(LongPath.WindowsMaxPathLength);

        var result = LongPath.ExtendedCore(longPath, isWindows: true, isNetworkDrive: _ => false);

        result.Should().Be(@"\\?\" + longPath);
    }

    [Fact(DisplayName = "境界値: ちょうど259文字は無変換、ちょうど260文字はプレフィックス付き")]
    public void 境界値の確認()
    {
        var justBelow = @"C:\" + new string('a', LongPath.WindowsMaxPathLength - 1 - 3);
        justBelow.Length.Should().Be(LongPath.WindowsMaxPathLength - 1);
        LongPath.ExtendedCore(justBelow, isWindows: true, isNetworkDrive: _ => false).Should().Be(justBelow);

        var atThreshold = justBelow + "a";
        atThreshold.Length.Should().Be(LongPath.WindowsMaxPathLength);
        LongPath.ExtendedCore(atThreshold, isWindows: true, isNetworkDrive: _ => false)
            .Should().Be(@"\\?\" + atThreshold);
    }

    [Fact(DisplayName = "MAX_PATH以上のUNCパスは\\\\?\\UNC\\プレフィックスが付く")]
    public void 長いUNCパスはUNCプレフィックス付き()
    {
        var longUnc = @"\\server\share\" + new string('a', LongPath.WindowsMaxPathLength);

        var result = LongPath.ExtendedCore(longUnc, isWindows: true, isNetworkDrive: _ => false);

        result.Should().Be(@"\\?\UNC\" + longUnc[2..]);
    }

    [Fact(DisplayName = "回帰テスト: MAX_PATH以上でもマップ済みネットワークドライブなら\\\\?\\を付けずそのまま返す")]
    public void 長いネットワークドライブパスはプレフィックスを付けない()
    {
        // 実機不具合の核心: \\?\Z:\... はマップ済みドライブ文字を解決できず、
        // File.Exists等が静かに失敗する（存在するファイルでもfalseを返す）。
        // 「拡張パスでは失敗するが素のパスなら成功する」状況を、この判定関数のレベルで
        // 再現・固定する（実ファイルシステムでの再現はLinux環境では行えないため、
        // ここでは「Graftが\\?\を付けない」という決定そのものを担保する）。
        var longNetworkPath = @"Z:\" + new string('a', LongPath.WindowsMaxPathLength);

        var result = LongPath.ExtendedCore(longNetworkPath, isWindows: true, isNetworkDrive: p => p == longNetworkPath);

        result.Should().Be(longNetworkPath, "ネットワークドライブには\\\\?\\に対応する表記が無いため、付けても解決できない");
    }

    [Fact(DisplayName = "MAX_PATH未満のネットワークドライブパスはそもそも判定関数を呼ばずに無変換")]
    public void 短いネットワークドライブパスは無変換()
    {
        var shortNetworkPath = @"Z:\project\file.txt";
        var calledIsNetworkDrive = false;

        var result = LongPath.ExtendedCore(shortNetworkPath, isWindows: true, isNetworkDrive: _ =>
        {
            calledIsNetworkDrive = true;
            return true;
        });

        result.Should().Be(shortNetworkPath);
        // 短いパスでは長さ判定だけで確定するため、ネットワークドライブ判定（DriveInfo等の
        // 比較的コストのある呼び出し）自体を行わないことも確認する。
        calledIsNetworkDrive.Should().BeFalse();
    }

    [Fact(DisplayName = "公開API Extended: この実行環境（非Windows）では常にそのまま返る")]
    public void 公開APIは非Windows環境ではそのまま返す()
    {
        var path = @"C:\" + new string('a', 400);
        LongPath.Extended(path).Should().Be(path);
    }

    [Fact(DisplayName = "ExceedsExtendedLimit: プレフィックス込みでも上限を超える極端な長さはtrue")]
    public void 極端に長いパスは上限超過()
    {
        var huge = @"C:\" + new string('a', LongPath.MaxExtendedPathLength + 100);
        // この実行環境は非Windowsのため素のパスのままでも判定できる。
        LongPath.ExceedsExtendedLimit(huge).Should().BeTrue();
    }

    [Fact(DisplayName = "ExceedsExtendedLimit: 通常の長パス程度では上限を超えない")]
    public void 通常の長さでは上限を超えない()
    {
        var moderate = @"C:\" + new string('a', 400);
        LongPath.ExceedsExtendedLimit(moderate).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // v1.0.7実機不具合対応: UNCパスの往復（\\server\share\... ⇔ \\?\UNC\server\share\...）と、
    // プロジェクトルート復元（RecoverProjectRoot）。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "UNCパスの往復: Extendedで拡張表記にしてもStripExtendedPrefixで元に戻る")]
    public void UNCパスは拡張表記との間を往復できる()
    {
        var longUnc = @"\\server\share\" + new string('a', LongPath.WindowsMaxPathLength);

        var extended = LongPath.ExtendedCore(longUnc, isWindows: true, isNetworkDrive: _ => false);
        extended.Should().Be(@"\\?\UNC\" + longUnc[2..], "前提: 拡張変換自体は既存仕様のまま");

        LongPath.StripExtendedPrefix(extended).Should().Be(longUnc, "拡張表記から通常のUNC表記へ戻せること");
    }

    [Fact(DisplayName = "ローカルパスの往復: Extendedで拡張表記にしてもStripExtendedPrefixで元に戻る")]
    public void ローカルパスは拡張表記との間を往復できる()
    {
        var longLocal = @"C:\" + new string('a', LongPath.WindowsMaxPathLength);

        var extended = LongPath.ExtendedCore(longLocal, isWindows: true, isNetworkDrive: _ => false);
        extended.Should().Be(@"\\?\" + longLocal);

        LongPath.StripExtendedPrefix(extended).Should().Be(longLocal);
    }

    [Fact(DisplayName = "StripExtendedPrefix: 拡張表記でない通常のパスはそのまま返る（冪等）")]
    public void 拡張表記でないパスはそのまま()
    {
        LongPath.StripExtendedPrefix(@"\\server\share\project").Should().Be(@"\\server\share\project");
        LongPath.StripExtendedPrefix(@"C:\Users\name\project").Should().Be(@"C:\Users\name\project");
        LongPath.StripExtendedPrefix("").Should().Be("");
    }

    /// <summary>
    /// 実機不具合（v1.0.6）の再現テスト。
    /// <see cref="LongPath.ExtendedCore"/>がUNCパス（<c>\\gfs\inaden\...\inaCalendar</c>）を
    /// <c>\\?\UNC\gfs\inaden\...\inaCalendar</c>へ変換するところまでは正しい（E210メッセージの
    /// 現地調査で確認済み）。しかし報告された絶対パスは、この拡張表記から先頭の<c>\\?\</c>
    /// （4文字）だけが失われ、<c>UNC\gfs\inaden\...\inaCalendar</c>という一見相対パスに見える
    /// 文字列になっていた。<see cref="LongPath.RecoverProjectRoot"/>が導入される前は、この文字列を
    /// そのまま<c>Path.GetFullPath</c>へ渡すとカレントディレクトリ（exeフォルダ）基準の
    /// 絶対パスへ誤って解決されてしまう（このプロジェクト配下の全ブロックが失敗した根本原因）。
    /// 本テストはRecoverProjectRootが、この壊れた形を正しいUNC表記へ復元することを保証する。
    /// </summary>
    [Fact(DisplayName = "実機不具合の再現: \\\\?\\UNC\\の先頭\\\\?\\が失われた文字列をUNC表記へ復元する")]
    public void 化けたUNC表記のRootは元のUNC表記へ復元される()
    {
        // 実機で報告された実際のパスはMAX_PATH未満だが、ExtendedCoreの\\?\UNC\変換自体は
        // 長さに関わらず同じ規則（\\?\UNC\ + absolutePath[2..]）で行われるため、ここでは
        // MAX_PATH以上になるよう末尾を延ばして変換条件（1.0.6で実際に変換された条件と同じ）を
        // 満たしたうえで検証する（変換規則そのものはLongPathTests内の他のテストで別途検証済み）。
        var original = @"\\gfs\inaden\営業部\02-国営課\18_各担当ファイル\佐々木\7.ツール\10. ツール拡張機能\inaCalendar"
            + new string('a', LongPath.WindowsMaxPathLength);
        var extended = LongPath.ExtendedCore(original, isWindows: true, isNetworkDrive: _ => false);
        extended.Should().StartWith(@"\\?\UNC\");

        // 実際に報告された壊れた文字列: 拡張表記の先頭4文字（\\?\）だけが失われた形。
        var corrupted = extended[4..];
        corrupted.Should().Be("UNC\\" + original[2..], "再現条件: 先頭の\\\\?\\が失われるとUNC\\...という相対パスに見える文字列になる");
        corrupted.Should().NotStartWith(@"\", "再現条件: 化けた文字列は絶対パスに見えない（先頭に区切り文字が無い）");

        var recovered = LongPath.RecoverProjectRoot(corrupted);

        recovered.Should().Be(original, "化けたRootは元のUNC表記へ復元されるべき");
    }

    [Fact(DisplayName = "RecoverProjectRoot: 拡張表記のまま渡されても通常のUNC表記へ戻す")]
    public void 拡張表記のRootは通常のUNC表記へ戻る()
    {
        var extended = @"\\?\UNC\gfs\inaden\project";

        LongPath.RecoverProjectRoot(extended).Should().Be(@"\\gfs\inaden\project");
    }

    [Fact(DisplayName = "RecoverProjectRoot: 拡張表記のまま渡されても通常のローカル表記へ戻す")]
    public void 拡張表記のローカルRootは通常表記へ戻る()
    {
        var extended = @"\\?\C:\Users\name\project";

        LongPath.RecoverProjectRoot(extended).Should().Be(@"C:\Users\name\project");
    }

    [Fact(DisplayName = "RecoverProjectRoot: 既に正しい絶対パスは一切変更しない（冪等・誤爆防止）")]
    public void 正常なRootは変更されない()
    {
        LongPath.RecoverProjectRoot(@"\\server\share\project").Should().Be(@"\\server\share\project");
        LongPath.RecoverProjectRoot(@"C:\Users\name\project").Should().Be(@"C:\Users\name\project");
        LongPath.RecoverProjectRoot("/home/user/project").Should().Be("/home/user/project");

        // "UNC"のような名前を含んでいても、絶対パスとして正しい形なら誤って書き換えない。
        LongPath.RecoverProjectRoot(@"C:\Projects\UNC\share").Should().Be(@"C:\Projects\UNC\share");
    }

    [Fact(DisplayName = "RecoverProjectRoot: null・空文字はそのまま返る")]
    public void RecoverProjectRootはnullと空文字を素通しする()
    {
        LongPath.RecoverProjectRoot("").Should().Be("");
        LongPath.RecoverProjectRoot(null!).Should().BeNull();
    }

    // ------------------------------------------------------------------
    // v1.0.7実機不具合対応（環境要約ログ）: ClassifyLocation。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ClassifyLocation: UNC共有はUncShare")]
    public void UNCパスはUncShare()
    {
        LongPath.ClassifyLocation(@"\\server\share\project").Should().Be(LongPath.PathLocationKind.UncShare);
    }

    [Theory(DisplayName = "ClassifyLocation: 主要なクラウド同期フォルダはCloudSyncFolder")]
    [InlineData(@"C:\Users\name\OneDrive\project")]
    [InlineData(@"C:\Users\name\Dropbox\project")]
    [InlineData("/home/name/Google Drive/project")]
    public void クラウド同期フォルダはCloudSyncFolder(string path)
    {
        LongPath.ClassifyLocation(path).Should().Be(LongPath.PathLocationKind.CloudSyncFolder);
    }

    [Fact(DisplayName = "ClassifyLocation: 何にも当てはまらない普通のローカルパスはLocal")]
    public void 普通のローカルパスはLocal()
    {
        LongPath.ClassifyLocation(@"C:\Users\name\project").Should().Be(LongPath.PathLocationKind.Local);
        LongPath.ClassifyLocation("/home/name/project").Should().Be(LongPath.PathLocationKind.Local);
    }

    [Fact(DisplayName = "ClassifyLocation: 空文字・nullはLocal（安全側）")]
    public void 空文字はLocal扱い()
    {
        LongPath.ClassifyLocation("").Should().Be(LongPath.PathLocationKind.Local);
        LongPath.ClassifyLocation(null!).Should().Be(LongPath.PathLocationKind.Local);
    }

    [Fact(DisplayName = "IsNetworkOrCloudSyncFolder: ClassifyLocationがLocal以外ならtrue、Localならfalse（回帰）")]
    public void IsNetworkOrCloudSyncFolderはClassifyLocationと整合する()
    {
        LongPath.IsNetworkOrCloudSyncFolder(@"\\server\share\project").Should().BeTrue();
        LongPath.IsNetworkOrCloudSyncFolder(@"C:\Users\name\OneDrive\project").Should().BeTrue();
        LongPath.IsNetworkOrCloudSyncFolder(@"C:\Users\name\project").Should().BeFalse();
    }
}
