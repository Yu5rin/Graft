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

        var buildDate = TryGetBuildDate(assembly);
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
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "DiffPlex-LICENSE.txt");
        try
        {
            LicenseText.Text = await File.ReadAllTextAsync(path).ConfigureAwait(true);
        }
        catch (IOException)
        {
            LicenseText.Text = "ライセンスファイルを読み込めませんでした。";
        }
    }

    /// <summary>実行アセンブリの最終更新日時をビルド日時の近似として返す。取得できない場合はnull。</summary>
    private static DateTime? TryGetBuildDate(Assembly assembly)
    {
        var location = assembly.Location;
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
