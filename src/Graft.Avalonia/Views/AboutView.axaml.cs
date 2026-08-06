using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Graft.Views;

/// <summary>
/// 8.15章のバージョン情報。アプリのバージョン・ビルド日時・使用ライブラリのライセンス表記
/// （DiffPlex＝Apache License 2.0、System.Text.Encoding.CodePages＝MIT、AvaloniaEdit＝MIT）を
/// 表示する。ロゴは <c>Themes/Logo.axaml</c> の <c>{DynamicResource LogoImage}</c> を使い、
/// ラスタ画像は使用しない。ライセンス全文は埋め込みリソースから展開時に読み込む。
/// WPF版からの移植（19章 L3）。AvaloniaのExpanderにはExpandedイベントが無いため、
/// IsExpandedプロパティの変化を購読して同じタイミングで読み込む。
/// </summary>
public partial class AboutView : UserControl
{
    private bool _licenseLoaded;
    private bool _avalonEditLicenseLoaded;

    public AboutView()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        DiffPlexLicenseExpander.PropertyChanged += OnDiffPlexExpanderPropertyChanged;
        AvalonEditLicenseExpander.PropertyChanged += OnAvalonEditExpanderPropertyChanged;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "不明";
        VersionText.Text = $"バージョン {version}";

        var buildDate = TryGetBuildDate();
        BuildDateText.Text = buildDate is null
            ? "ビルド日時: 不明"
            : $"ビルド日時: {buildDate.Value:yyyy-MM-dd HH:mm}";
    }

    private async void OnDiffPlexExpanderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Expander.IsExpandedProperty || !e.GetNewValue<bool>() || _licenseLoaded) return;

        _licenseLoaded = true;
        LicenseText.Text = await LoadLicenseTextAsync("Graft.Assets.DiffPlex-LICENSE.txt").ConfigureAwait(true);
    }

    /// <summary>9.6 バージョン情報。AvaloniaEditのライセンス全文（MIT）を展開時に読み込む。</summary>
    private async void OnAvalonEditExpanderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Expander.IsExpandedProperty || !e.GetNewValue<bool>() || _avalonEditLicenseLoaded) return;

        _avalonEditLicenseLoaded = true;
        AvalonEditLicenseText.Text =
            await LoadLicenseTextAsync("Graft.Assets.AvalonEdit-LICENSE.txt").ConfigureAwait(true);
    }

    // 単一実行ファイルで配布するため、ライセンス全文は埋め込みリソースとして持つ
    // （外部ファイルに置くと発行物が1つに収まらない）。
    private static async Task<string> LoadLicenseTextAsync(string resourceName)
    {
        try
        {
            await using var stream = typeof(AboutView).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return "ライセンスファイルを読み込めませんでした。";

            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync().ConfigureAwait(true);
        }
        catch (IOException)
        {
            return "ライセンスファイルを読み込めませんでした。";
        }
    }

    /// <summary>
    /// ビルド日時を返す。単一実行ファイルでは <c>Assembly.Location</c> が空になるため、
    /// 実行中のプロセスイメージの最終更新日時を近似として使う。
    /// </summary>
    private static DateTime? TryGetBuildDate()
    {
        var location = Environment.ProcessPath;
        if (string.IsNullOrEmpty(location) || !File.Exists(location)) return null;

        try
        {
            return File.GetLastWriteTime(location);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
