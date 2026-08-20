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
}
