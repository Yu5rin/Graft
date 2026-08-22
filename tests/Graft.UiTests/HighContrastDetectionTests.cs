using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Platform.Linux;
using Graft.Themes;
using Xunit;

namespace Graft.UiTests;

/// <summary>
/// 依頼3（v2.1 仕様書9.3・17章E707 ハイコントラスト検出）のテスト。
/// <see cref="Graft.Platform.Windows.WindowsSystemThemeWatcher"/>は実機（Windows）でしか
/// SPI_GETHIGHCONTRASTの本体経路を検証できないため対象外とする
/// （tests/Graft.Tests/Graft.Tests.csprojのコメントのとおり、本体はWindows以外での
/// テスト実行を妨げないことを優先し、Platform/Windows配下はUI非依存テストに取り込まない方針）。
/// 代わりにこのファイルでは (a) 実行中のLinux上で<see cref="LinuxSystemThemeWatcher"/>を
/// 直接動かし例外にならないことと、(b) <see cref="ThemeManager"/>側の反映（
/// <see cref="ThemeManager.IsHighContrastActive"/>）がGraft自身のテーマ選択から独立している
/// ことを検証する。
/// </summary>
public class HighContrastDetectionTests
{
    [AvaloniaFact(DisplayName = "LinuxSystemThemeWatcher.TryReadIsHighContrastは例外にならず、bool?として返る")]
    public void Linux実装は例外にならずbool_を返す()
    {
        using var watcher = new LinuxSystemThemeWatcher();

        // このテストコンテナにはGNOMEのa11yスキーマが無いため（実機検証コマンドで
        // `No such schema 'org.gnome.desktop.a11y.interface'`を確認済み）、実際にはnullが
        // 返る想定だが、対応するデスクトップ設定を持つ環境ではtrue/falseも取りうるため
        // 型の契約（例外にならず読み取れること）のみを固定する。
        var act = () => watcher.TryReadIsHighContrast();
        act.Should().NotThrow();
    }

    [AvaloniaFact(DisplayName = "対応するデスクトップ設定が無い環境ではnull（判定不能）を返す（このテストコンテナの実測）")]
    public void 対応する設定が無ければnullを返す()
    {
        // 依頼3の指示「Linuxでは対応するデスクトップ設定があれば追従し、なければ何もしない」を
        // 満たしていることの実測による固定。gsettingsのスキーマ自体が存在しないコンテナ環境で
        // 実行される前提（このリポジトリのCI環境はUbuntu、GNOMEシェルは入っていない）。
        using var watcher = new LinuxSystemThemeWatcher();
        watcher.TryReadIsHighContrast().Should().BeNull();
    }

    [AvaloniaFact(DisplayName = "Graftのテーマ切替(SetTheme)はハイコントラストの検出結果を変化させない")]
    public void テーマ切替はハイコントラストの検出結果を変えない()
    {
        // ThemeManagerは静的なため、テストプロセス内で最初にInitializeされた時点の
        // ISystemThemeWatcher（実行OSに応じた実装）を使い続ける。ここでは「その実装が
        // 何を返すか」ではなく、「Graft側のテーマ選択（SetTheme）を何度切り替えても
        // IsHighContrastActiveの値が変わらないこと」（ThemeManager.IsHighContrastActiveの
        // XMLコメントに書いた設計意図そのもの）を固定する。
        ThemeManager.SetTheme(AppTheme.Dark);
        var before = ThemeManager.IsHighContrastActive;

        ThemeManager.SetTheme(AppTheme.Light);
        ThemeManager.SetTheme(AppTheme.Nord);
        ThemeManager.SetTheme(AppTheme.System);
        ThemeManager.SetTheme(AppTheme.Dark);

        ThemeManager.IsHighContrastActive.Should().Be(before,
            "ハイコントラストの検出はOS側の状態であり、Graft自身のテーマ選択とは独立している必要がある");
    }
}
