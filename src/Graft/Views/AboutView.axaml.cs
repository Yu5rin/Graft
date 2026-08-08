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
/// v2.0のWPF版からの移植（19章 L3）。AvaloniaのExpanderにはExpandedイベントが無いため、
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

        // 製作者・著作権表示はGraft.csprojの<Company>/<Copyright>から生成される
        // アセンブリ属性を読む。画面側に文字列を直書きすると、csprojと二重管理になり
        // どちらか一方だけ更新して食い違う恐れがあるため、単一の情報源(csproj)に揃える。
        var company = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        if (string.IsNullOrEmpty(company))
        {
            // 空文字の行が残って隙間になるのを避けるため、行ごと非表示にする
            AuthorText.IsVisible = false;
        }
        else
        {
            AuthorText.Text = $"製作者: {company}";
        }

        var copyright = assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
        if (string.IsNullOrEmpty(copyright))
        {
            CopyrightText.IsVisible = false;
        }
        else
        {
            CopyrightText.Text = copyright;
        }
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

    // 発行フォルダに含めるファイルを増やさないよう、ライセンス全文は埋め込みリソースとして持つ
    // （外部ファイルにすると同梱漏れや配置ミスで参照できなくなる恐れがある）。
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
    /// ビルド日時を返す。単一ファイル発行では <c>Assembly.Location</c> が空になる問題があり、
    /// フォルダ形式に変わった現在も実行中のプロセスイメージの最終更新日時を
    /// 近似として使い続ける（どちらの発行形式でも安定して取得できるため）。
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
