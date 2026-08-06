using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using Graft.Platform;
using Microsoft.Win32;

namespace Graft.Views;

/// <summary>
/// 確認・入力・フォルダ選択・通知の共通ダイアログを提供する（附録A.3によりMVVMフレームワークの
/// ダイアログサービス相当を自前実装したもの）。XAMLを持たず、テーマトークンを
/// <c>DynamicResource</c> で参照するWindowをコードから組み立てる。フォルダ選択は
/// WPF標準の <see cref="OpenFolderDialog"/>（.NET 8）を使い、外部パッケージは追加しない。
/// 8.10: いずれのダイアログもボタンに <c>IsCancel</c> を設定しており、Escで閉じられる。
/// <see cref="IDialogService"/> を実装し、ViewModel層からはWPF非依存の抽象越しに使われる
/// （仕様書v2.1 19章・20章 L3）。
/// </summary>
public sealed class DialogService : IDialogService
{
    /// <summary>OK/キャンセルの確認ダイアログを表示する。</summary>
    public Task<bool> ConfirmAsync(string title, string message)
    {
        var result = false;
        var window = BuildShell(title, out var body);
        AddMessage(body, message);

        var buttons = AddButtonRow(body);
        var ok = CreateButton("OK", isDefault: true, isCancel: false);
        var cancel = CreateButton("キャンセル", isDefault: false, isCancel: true);
        ok.Click += (_, _) => { result = true; window.DialogResult = true; };
        cancel.Click += (_, _) => window.DialogResult = false;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        window.Loaded += (_, _) => ok.Focus();
        window.ShowDialog();
        return Task.FromResult(result);
    }

    /// <summary>
    /// 3択の確認ダイアログを表示する。未保存ファイルを閉じるとき（v2.0 仕様書4.3）のように
    /// 「実行する／実行せず続ける／やめる」を選ばせる用途に使う。
    /// </summary>
    /// <returns>肯定ならtrue、否定ならfalse、キャンセルならnull。</returns>
    public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
    {
        bool? result = null;
        var window = BuildShell(title, out var body);
        AddMessage(body, message);

        var buttons = AddButtonRow(body);
        var yes = CreateButton(yesLabel, isDefault: true, isCancel: false);
        var no = CreateButton(noLabel, isDefault: false, isCancel: false);
        var cancel = CreateButton("キャンセル", isDefault: false, isCancel: true);
        yes.Click += (_, _) => { result = true; window.DialogResult = true; };
        no.Click += (_, _) => { result = false; window.DialogResult = true; };
        cancel.Click += (_, _) => window.DialogResult = false;
        buttons.Children.Add(cancel);
        buttons.Children.Add(no);
        buttons.Children.Add(yes);

        window.Loaded += (_, _) => yes.Focus();
        window.ShowDialog();
        return Task.FromResult(result);
    }

    /// <summary>1行のテキスト入力ダイアログを表示する。キャンセル時はnullを返す。</summary>
    public Task<string?> PromptAsync(string title, string message, string? initial = null)
    {
        string? result = null;
        var window = BuildShell(title, out var body);
        AddMessage(body, message);

        var input = new TextBox { Text = initial ?? string.Empty, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(input, message);
        body.Children.Add(input);

        var buttons = AddButtonRow(body);
        var ok = CreateButton("OK", isDefault: true, isCancel: false);
        var cancel = CreateButton("キャンセル", isDefault: false, isCancel: true);
        ok.Click += (_, _) => { result = input.Text; window.DialogResult = true; };
        cancel.Click += (_, _) => window.DialogResult = false;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        window.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        window.ShowDialog();
        return Task.FromResult(result);
    }

    /// <summary>
    /// フォルダ選択ダイアログを表示する。WPF標準の <see cref="OpenFolderDialog"/> を使う。
    /// WPF側は完全同期のAPIのため、<see cref="Task.FromResult{TResult}(TResult)"/> で包むだけでよい
    /// （<see cref="IDialogService.PickFolderAsync"/> のコメント参照）。
    /// </summary>
    public Task<string?> PickFolderAsync(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        var owner = FindOwnerWindow();
        var accepted = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return Task.FromResult(accepted == true ? dialog.FolderName : null);
    }

    /// <summary>OKボタンのみの通知ダイアログを表示する。</summary>
    public Task ShowMessageAsync(string title, string message)
    {
        var window = BuildShell(title, out var body);
        AddMessage(body, message);

        var buttons = AddButtonRow(body);
        var ok = CreateButton("OK", isDefault: true, isCancel: true);
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(ok);

        window.Loaded += (_, _) => ok.Focus();
        window.ShowDialog();
        return Task.CompletedTask;
    }

    /// <summary>
    /// ダイアログの外枠を組み立てる。背景・文字色はすべてテーマトークンへ<c>DynamicResource</c>で
    /// バインドし、ハードコードした色は使わない（附録A.5）。
    /// </summary>
    private static Window BuildShell(string title, out StackPanel body)
    {
        body = new StackPanel { Margin = new Thickness(20) };
        var window = new Window
        {
            Title = title,
            Content = body,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 360,
            MaxWidth = 560,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ShowInTaskbar = false,
        };
        window.SetResourceReference(Control.BackgroundProperty, "BgElevated");
        window.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        window.SetResourceReference(TextElement.FontFamilyProperty, "UiFontFamily");
        window.SetResourceReference(TextElement.FontSizeProperty, "BodyFontSize");
        AutomationProperties.SetName(window, title);

        var owner = FindOwnerWindow();
        if (owner is not null)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return window;
    }

    private static void AddMessage(StackPanel body, string message)
    {
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
        };
        body.Children.Add(text);
    }

    private static StackPanel AddButtonRow(StackPanel body)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        body.Children.Add(row);
        return row;
    }

    private static Button CreateButton(string label, bool isDefault, bool isCancel)
    {
        var button = new Button
        {
            Content = label,
            IsDefault = isDefault,
            IsCancel = isCancel,
            MinWidth = 84,
            Margin = new Thickness(8, 0, 0, 0),
        };
        AutomationProperties.SetName(button, label);
        return button;
    }

    /// <summary>
    /// 表示中で使用可能なウィンドウを親として探す。<see cref="Application.MainWindow"/> が
    /// 未設定・未表示の場合はnullを返し、呼び出し側は画面中央起点にフォールバックする。
    /// </summary>
    private static Window? FindOwnerWindow()
    {
        var app = Application.Current;
        if (app is null)
        {
            return null;
        }

        var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
        if (active is not null)
        {
            return active;
        }

        var main = app.MainWindow;
        return main is { IsVisible: true } ? main : null;
    }
}
