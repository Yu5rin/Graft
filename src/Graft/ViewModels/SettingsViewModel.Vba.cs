using System.Globalization;
using Graft.Core;
using Graft.Infra;
using Graft.Platform;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="SettingsViewModel"/> の分割ファイル。「VBA 連携」タブ（仕様書_VBA連携 8章）の編集用プロパティ。
/// <para>
/// 【P1 では持ち回りだけだった】 P1 の時点では、設定画面のパネルが無いので、読み込んだ <c>Vba</c> を
/// そのまま保持して保存時に引き継ぐだけだった（<c>BuildSettingsFromFields</c> が <c>new Settings</c> で全項目を
/// 組み立て直すため、引き継がないと別の項目の保存のたびに既定値へ戻る）。パネルを作った今は、5項目（Q2 でブックの情報の5項目を追加）を
/// 他のタブと同じ編集用プロパティ（<c>SetEditableProperty</c> で即時反映）にして、読み込みと組み立てを往復させる。
/// </para>
/// </summary>
public sealed partial class SettingsViewModel
{
    private bool _vbaEnabled = true;
    private bool _vbaPullOnContextOpen = true;
    private bool _vbaPullBeforeDryRun = true;
    private string _vbaComTimeoutMsText = "5000";
    private string _vbaCallTimeoutMsText = "30000";
    private bool _vbaCollectWorkbookInfo = true;
    private string _vbaWorkbookInfoSampleRowsText = "5";
    private string _vbaWorkbookInfoMaxColumnsText = "30";
    private string _vbaWorkbookInfoMaxCellCharsText = "60";
    private string _vbaWorkbookInfoMaxCharsText = "20000";
    private string _vbaWorkbookInfoAllMaxRowsText = "500";
    private string _vbaWorkbookInfoAllMaxColumnsText = "50";
    private string _vbaRescueMaxGenerationsText = "20";
    private string _vbaRescueMaxTotalMBText = "50";

    /// <summary>
    /// この環境で VBA 連携が使えるかを調べる窓口。既定は <c>PlatformServices.Current.Vba</c>（Windows 以外では Null 実装で false）。
    /// テストがこの環境の判定を差し替えられるよう、プロパティで持つ。
    /// </summary>
    public Func<bool> VbaSupportProbe { get; set; } = static () => PlatformServices.Current.Vba.IsSupported;

    /// <summary>VBA 連携がこの環境で使えるか。使えないときは、パネルの中身を出さず <see cref="VbaUnsupportedText"/> だけを出す。</summary>
    public bool IsVbaSupported => VbaSupportProbe();

    /// <summary><see cref="IsVbaSupported"/> の反対（パネルの案内文の表示条件に使う）。</summary>
    public bool IsVbaUnsupported => !IsVbaSupported;

    /// <summary>使えない環境でパネル全体に出す文言。E706 の実際の文言（仕様書_VBA連携 8章・9章）。</summary>
    public string VbaUnsupportedText => ErrorCatalog.SummaryOf(ErrorCode.E706);

    /// <summary>機能全体の有効・無効（<c>Vba.Enabled</c>）。オフのときは COM を一切呼ばず、VBA プロジェクトも普通のファイルプロジェクトとして扱う。</summary>
    public bool VbaEnabled { get => _vbaEnabled; set => SetEditableProperty(ref _vbaEnabled, value); }

    /// <summary>コンテキスト収集の前に Excel から自動で取り込む（<c>Vba.PullOnContextOpen</c>）。</summary>
    public bool VbaPullOnContextOpen { get => _vbaPullOnContextOpen; set => SetEditableProperty(ref _vbaPullOnContextOpen, value); }

    /// <summary>解析の直前に Excel から自動で取り込む（<c>Vba.PullBeforeDryRun</c>）。</summary>
    public bool VbaPullBeforeDryRun { get => _vbaPullBeforeDryRun; set => SetEditableProperty(ref _vbaPullBeforeDryRun, value); }

    /// <summary>COM の再試行の上限（ミリ秒。<c>Vba.ComTimeoutMs</c>）。範囲 1000〜30000。</summary>
    public string VbaComTimeoutMsText { get => _vbaComTimeoutMsText; set => SetEditableProperty(ref _vbaComTimeoutMsText, value); }

    /// <summary>COM 呼び出し全体の時間切れ（ミリ秒。<c>Vba.CallTimeoutMs</c>）。範囲 5000〜120000。</summary>
    public string VbaCallTimeoutMsText { get => _vbaCallTimeoutMsText; set => SetEditableProperty(ref _vbaCallTimeoutMsText, value); }

    /// <summary>
    /// コンテキスト収集・依頼文コピーでブックの情報（シート・テーブル・名前・見出し・参照設定）を取る
    /// （<c>Vba.CollectWorkbookInfo</c>。仕様書_ブックの情報と依頼の観点 9章）。オフのときは Excel を呼ばない。
    /// </summary>
    public bool VbaCollectWorkbookInfo { get => _vbaCollectWorkbookInfo; set => SetEditableProperty(ref _vbaCollectWorkbookInfo, value); }

    /// <summary>「見出し＋N 行」の N の既定（<c>Vba.WorkbookInfoSampleRows</c>）。範囲 1〜20。</summary>
    public string VbaWorkbookInfoSampleRowsText { get => _vbaWorkbookInfoSampleRowsText; set => SetEditableProperty(ref _vbaWorkbookInfoSampleRowsText, value); }

    /// <summary>見出し・見本の最大列数（<c>Vba.WorkbookInfoMaxColumns</c>）。範囲 5〜100。</summary>
    public string VbaWorkbookInfoMaxColumnsText { get => _vbaWorkbookInfoMaxColumnsText; set => SetEditableProperty(ref _vbaWorkbookInfoMaxColumnsText, value); }

    /// <summary>1 セルの最大文字数（<c>Vba.WorkbookInfoMaxCellChars</c>）。範囲 10〜500。</summary>
    public string VbaWorkbookInfoMaxCellCharsText { get => _vbaWorkbookInfoMaxCellCharsText; set => SetEditableProperty(ref _vbaWorkbookInfoMaxCellCharsText, value); }

    /// <summary>ブックの情報の出力全体の最大文字数（<c>Vba.WorkbookInfoMaxChars</c>）。範囲 2000〜100000。</summary>
    public string VbaWorkbookInfoMaxCharsText { get => _vbaWorkbookInfoMaxCharsText; set => SetEditableProperty(ref _vbaWorkbookInfoMaxCharsText, value); }

    /// <summary>シートの見せ方「全部」「範囲を指定」の最大行数（<c>Vba.WorkbookInfoAllMaxRows</c>）。範囲 10〜5000。</summary>
    public string VbaWorkbookInfoAllMaxRowsText { get => _vbaWorkbookInfoAllMaxRowsText; set => SetEditableProperty(ref _vbaWorkbookInfoAllMaxRowsText, value); }

    /// <summary>シートの見せ方「全部」「範囲を指定」の最大列数（<c>Vba.WorkbookInfoAllMaxColumns</c>）。範囲 5〜200。</summary>
    public string VbaWorkbookInfoAllMaxColumnsText { get => _vbaWorkbookInfoAllMaxColumnsText; set => SetEditableProperty(ref _vbaWorkbookInfoAllMaxColumnsText, value); }

    /// <summary>1 モジュールあたりの自動控えの世代数（<c>Vba.RescueMaxGenerations</c>）。範囲 1〜200。</summary>
    public string VbaRescueMaxGenerationsText { get => _vbaRescueMaxGenerationsText; set => SetEditableProperty(ref _vbaRescueMaxGenerationsText, value); }

    /// <summary>控え全体の容量（MB。<c>Vba.RescueMaxTotalMB</c>）。範囲 5〜1000。</summary>
    public string VbaRescueMaxTotalMBText { get => _vbaRescueMaxTotalMBText; set => SetEditableProperty(ref _vbaRescueMaxTotalMBText, value); }

    /// <summary>読み込んだ設定を編集用プロパティへ映す（<c>PopulateEditableFields</c> から呼ぶ）。</summary>
    private void PopulateVbaFields(Settings s)
    {
        var vba = s.Vba ?? new VbaSettings();
        VbaEnabled = vba.Enabled;
        VbaPullOnContextOpen = vba.PullOnContextOpen;
        VbaPullBeforeDryRun = vba.PullBeforeDryRun;
        VbaComTimeoutMsText = vba.ComTimeoutMs.ToString(CultureInfo.InvariantCulture);
        VbaCallTimeoutMsText = vba.CallTimeoutMs.ToString(CultureInfo.InvariantCulture);
        VbaCollectWorkbookInfo = vba.CollectWorkbookInfo;
        VbaWorkbookInfoSampleRowsText = vba.WorkbookInfoSampleRows.ToString(CultureInfo.InvariantCulture);
        VbaWorkbookInfoMaxColumnsText = vba.WorkbookInfoMaxColumns.ToString(CultureInfo.InvariantCulture);
        VbaWorkbookInfoMaxCellCharsText = vba.WorkbookInfoMaxCellChars.ToString(CultureInfo.InvariantCulture);
        VbaWorkbookInfoMaxCharsText = vba.WorkbookInfoMaxChars.ToString(CultureInfo.InvariantCulture);
        VbaWorkbookInfoAllMaxRowsText = vba.WorkbookInfoAllMaxRows.ToString(CultureInfo.InvariantCulture);
        VbaWorkbookInfoAllMaxColumnsText = vba.WorkbookInfoAllMaxColumns.ToString(CultureInfo.InvariantCulture);
        VbaRescueMaxGenerationsText = vba.RescueMaxGenerations.ToString(CultureInfo.InvariantCulture);
        VbaRescueMaxTotalMBText = vba.RescueMaxTotalMB.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>編集用プロパティから <see cref="VbaSettings"/> を組み立てる（<c>BuildSettingsFromFields</c> から呼ぶ）。</summary>
    private VbaSettings BuildVbaSettings() => new()
    {
        Enabled = _vbaEnabled,
        PullOnContextOpen = _vbaPullOnContextOpen,
        PullBeforeDryRun = _vbaPullBeforeDryRun,
        ComTimeoutMs = ParseInt(_vbaComTimeoutMsText),
        CallTimeoutMs = ParseInt(_vbaCallTimeoutMsText),
        CollectWorkbookInfo = _vbaCollectWorkbookInfo,
        WorkbookInfoSampleRows = ParseInt(_vbaWorkbookInfoSampleRowsText),
        WorkbookInfoMaxColumns = ParseInt(_vbaWorkbookInfoMaxColumnsText),
        WorkbookInfoMaxCellChars = ParseInt(_vbaWorkbookInfoMaxCellCharsText),
        WorkbookInfoMaxChars = ParseInt(_vbaWorkbookInfoMaxCharsText),
        WorkbookInfoAllMaxRows = ParseInt(_vbaWorkbookInfoAllMaxRowsText),
        WorkbookInfoAllMaxColumns = ParseInt(_vbaWorkbookInfoAllMaxColumnsText),
        RescueMaxGenerations = ParseInt(_vbaRescueMaxGenerationsText),
        RescueMaxTotalMB = ParseInt(_vbaRescueMaxTotalMBText),
    };
}
