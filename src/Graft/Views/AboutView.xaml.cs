using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace Graft.Views;

/// <summary>
/// 8.15章のバージョン情報。アプリのバージョン・ビルド日時・使用ライブラリのライセンス表記
/// （DiffPlex＝Apache License 2.0、System.Text.Encoding.CodePages＝MIT）を表示する。
/// ロゴは <c>Themes/Logo.xaml</c> の <c>{DynamicResource LogoImage}</c> を使い、
/// ラスタ画像は使用しない。DiffPlexのライセンス全文は <c>Assets/DiffPlex-LICENSE.txt</c> を
/// 展開時に非同期で読み込んで表示する。
/// </summary>
public partial class AboutView : UserControl
{
    private bool _licenseLoaded;

    public AboutView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "不明";
        VersionText.Text = $"バージョン {version}";

        var buildDate = TryGetBuildDate();
        BuildDateText.Text = buildDate is null
            ? "ビルド日時: 不明"
            : $"ビルド日時: {buildDate.Value:yyyy-MM-dd HH:mm}";
    }

    private async void OnLicenseExpanded(object sender, RoutedEventArgs e)
    {
        if (_licenseLoaded)
        {
            return;
        }

        _licenseLoaded = true;

        // 単一exeで配布するため、ライセンス全文は埋め込みリソースとして持つ。
        // 外部ファイルに置くと発行物がexe1つに収まらない。
        const string resourceName = "Graft.Assets.DiffPlex-LICENSE.txt";
        try
        {
            await using var stream = typeof(AboutView).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                LicenseText.Text = "ライセンスファイルを読み込めませんでした。";
                return;
            }

            using var reader = new StreamReader(stream);
            LicenseText.Text = await reader.ReadToEndAsync().ConfigureAwait(true);
        }
        catch (IOException)
        {
            LicenseText.Text = "ライセンスファイルを読み込めませんでした。";
        }
    }

    /// <summary>
    /// ビルド日時を返す。単一exeでは <c>Assembly.Location</c> が空になるため、
    /// 実行中のプロセスイメージ（exe本体）の最終更新日時を近似として使う。
    /// </summary>
    private static DateTime? TryGetBuildDate()
    {
        var location = Environment.ProcessPath;
        if (string.IsNullOrEmpty(location) || !File.Exists(location))
        {
            return null;
        }

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
