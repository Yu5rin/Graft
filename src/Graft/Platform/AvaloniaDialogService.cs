using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Graft.Platform;

/// <summary>
/// <see cref="IDialogService"/> のAvalonia実装。v2.0のWPF版（<c>Graft.Views.DialogService</c>）と
/// 同じ見た目・同じ日本語文言になるよう、XAMLを持たずテーマトークンを<c>DynamicResource</c>で
/// 参照するWindowをコードから組み立てる（仕様書19章・20章 L3）。結果は自前の
/// <see cref="TaskCompletionSource{TResult}"/>で受け渡す。タイトルバーの閉じるボタン等、
/// ボタンのクリック以外の経路で閉じられた場合は<see cref="Window.Closed"/>で拾い、
/// 既定値（安全側＝キャンセル扱い）で完了させる。
///
/// フォルダ選択だけは設計が異なる。WPF標準の<c>OpenFolderDialog</c>は同期APIだが、Avalonia標準の
/// <see cref="IStorageProvider.OpenFolderPickerAsync"/>は非同期APIしか持たない。これを
/// <c>GetAwaiter().GetResult()</c>等で同期的に待つとUIスレッドを塞いでピッカー自体が表示・完了
/// できず実質デッドロックするため、<see cref="IDialogService.PickFolderAsync"/>自体を非同期
/// シグネチャへ変更した（v2.0のWPF版は<see cref="Task.FromResult{TResult}(TResult)"/>で包むだけで
/// 追随できる）。
///
/// もう一つの差異は<see cref="Window.ShowDialog(Window)"/>がオーナーウィンドウを必須とする点
/// （v2.0のWPF版はオーナー無しの<c>ShowDialog()</c>を持つ）。<see cref="FindOwnerWindow"/>で
/// デスクトップの表示中ウィンドウを探すが、万一見つからない場合はモーダルにできないため、
/// 非モーダルの<see cref="Window.Show()"/>へ縮退する（呼び出し不能で例外を投げるより安全側。
/// headlessテスト環境で<c>ApplicationLifetime</c>が未設定の場合もこの経路を通る）。
/// </summary>
public sealed class AvaloniaDialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message)
    {
        var window = BuildShell(title, out var body);
        AddMessage(body, message);
        var tcs = new TaskCompletionSource<bool>();

        var buttons = AddButtonRow(body);
        var ok = CreateButton("OK", isDefault: true, isCancel: false);
        var cancel = CreateButton("キャンセル", isDefault: false, isCancel: true);
        ok.Click += (_, _) => Complete(window, tcs, true);
        cancel.Click += (_, _) => Complete(window, tcs, false);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        window.Loaded += (_, _) => ok.Focus();
        window.Closed += (_, _) => tcs.TrySetResult(false);
        ShowModal(window);
        return tcs.Task;
    }

    /// <returns>肯定ならtrue、否定ならfalse、キャンセルならnull。</returns>
    public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
    {
        var window = BuildShell(title, out var body);
        AddMessage(body, message);
        var tcs = new TaskCompletionSource<bool?>();

        var buttons = AddButtonRow(body);
        var yes = CreateButton(yesLabel, isDefault: true, isCancel: false);
        var no = CreateButton(noLabel, isDefault: false, isCancel: false);
        var cancel = CreateButton("キャンセル", isDefault: false, isCancel: true);
        yes.Click += (_, _) => Complete(window, tcs, true);
        no.Click += (_, _) => Complete(window, tcs, false);
        cancel.Click += (_, _) => Complete(window, tcs, null);
        buttons.Children.Add(cancel);
        buttons.Children.Add(no);
        buttons.Children.Add(yes);

        window.Loaded += (_, _) => yes.Focus();
        window.Closed += (_, _) => tcs.TrySetResult(null);
        ShowModal(window);
        return tcs.Task;
    }

    public Task<string?> PromptAsync(string title, string message, string? initial = null)
    {
        var window = BuildShell(title, out var body);
        AddMessage(body, message);
        var tcs = new TaskCompletionSource<string?>();

        var input = new TextBox { Text = initial ?? string.Empty, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetName(input, message);
        body.Children.Add(input);

        var buttons = AddButtonRow(body);
        var ok = CreateButton("OK", isDefault: true, isCancel: false);
        var cancel = CreateButton("キャンセル", isDefault: false, isCancel: true);
        ok.Click += (_, _) => Complete(window, tcs, input.Text);
        cancel.Click += (_, _) => Complete(window, tcs, null);
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        window.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        window.Closed += (_, _) => tcs.TrySetResult(null);
        ShowModal(window);
        return tcs.Task;
    }

    /// <summary>
    /// フォルダ選択ダイアログを表示する。Avalonia標準の<see cref="IStorageProvider"/>を使う
    /// （<see cref="AvaloniaDialogService"/>のコメント参照）。
    /// </summary>
    public async Task<string?> PickFolderAsync(string title)
    {
        var owner = FindOwnerWindow();
        var provider = owner is null ? null : TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (provider is null || !provider.CanPickFolder)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        var picked = await provider.OpenFolderPickerAsync(options).ConfigureAwait(true);
        var folder = picked.Count > 0 ? picked[0] : null;
        return folder?.TryGetLocalPath();
    }

    /// <summary>
    /// ファイル選択ダイアログを表示する（<see cref="PickFolderAsync"/>と同じ設計方針）。
    /// パッチファイルの選択（4.1「ファイルからのパッチ解析」）で使う。
    /// </summary>
    public async Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null)
    {
        var owner = FindOwnerWindow();
        var provider = owner is null ? null : TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (provider is null || !provider.CanOpen)
        {
            return null;
        }

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = BuildFileTypeFilter(extensions),
        };
        var picked = await provider.OpenFilePickerAsync(options).ConfigureAwait(true);
        var file = picked.Count > 0 ? picked[0] : null;
        return file?.TryGetLocalPath();
    }

    /// <summary>
    /// 「名前を付けて保存」ダイアログを表示する（<see cref="PickFileAsync"/>と同じ設計方針）。
    /// コンテキスト収集（10章）の「ファイルへ保存」で使う。
    /// </summary>
    public async Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
    {
        var owner = FindOwnerWindow();
        var provider = owner is null ? null : TopLevel.GetTopLevel(owner)?.StorageProvider;
        if (provider is null || !provider.CanSave)
        {
            return null;
        }

        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            FileTypeChoices = BuildFileTypeFilter(extensions),
        };
        var picked = await provider.SaveFilePickerAsync(options).ConfigureAwait(true);
        return picked?.TryGetLocalPath();
    }

    /// <summary>拡張子一覧から「対応するファイル」フィルタを組み立てる。未指定時はnull（フィルタ無し）。</summary>
    private static IReadOnlyList<FilePickerFileType>? BuildFileTypeFilter(IReadOnlyList<string>? extensions)
    {
        if (extensions is null || extensions.Count == 0) return null;

        var patterns = extensions.Select(ext => $"*{ext}").ToArray();
        return new[]
        {
            new FilePickerFileType("対応するファイル") { Patterns = patterns },
            FilePickerFileTypes.All,
        };
    }

    public Task ShowMessageAsync(string title, string message)
    {
        var window = BuildShell(title, out var body);
        AddMessage(body, message);
        var tcs = new TaskCompletionSource();

        var buttons = AddButtonRow(body);
        var ok = CreateButton("OK", isDefault: true, isCancel: true);
        ok.Click += (_, _) => Complete(window, tcs);
        buttons.Children.Add(ok);

        window.Loaded += (_, _) => ok.Focus();
        window.Closed += (_, _) => tcs.TrySetResult();
        ShowModal(window);
        return tcs.Task;
    }

    /// <summary>
    /// ダイアログの外枠を組み立てる。背景・文字色はすべてテーマトークンへ<c>DynamicResource</c>で
    /// バインドし、ハードコードした色は使わない（附録A.5）。
    /// </summary>
    private static Window BuildShell(string title, out StackPanel body)
    {
        body = new StackPanel { Margin = new Thickness(20) };

        // 不具合4対応: 起動時の問題を1枚のダイアログへ集約すると、件数が多い日は本文が
        // 長くなる。SizeToContent.WidthAndHeightのみだと高さの上限が無く、画面からはみ出して
        // ボタンに手が届かなくなるおそれがあるため、bodyごとScrollViewerで包んで高さに上限を
        // 持たせる。ボタン行もbody内（AddButtonRow参照）に含まれスクロール対象になるが、
        // 「上限に達したら丸ごとスクロールできる」だけでも「画面外に出て一切届かない」不具合は
        // 解消できるため、この程度の作りに留めた（ボタン行だけを常時固定表示にするには、
        // Confirm/ConfirmThreeWay/Prompt/ShowMessageの4種すべてでヘッダ・フッタを分離する
        // 作り直しが要り、この修正の範囲を超える）。
        var scrollableBody = new ScrollViewer
        {
            Content = body,
            MaxHeight = 480,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var window = new Window
        {
            Title = title,
            Content = scrollableBody,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 360,
            MaxWidth = 560,
            CanResize = false,
            ShowInTaskbar = false,
        };
        window.Bind(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("BgElevated"));
        window.Bind(TemplatedControl.ForegroundProperty, new DynamicResourceExtension("TextPrimary"));
        window.Bind(TemplatedControl.FontFamilyProperty, new DynamicResourceExtension("UiFontFamily"));
        window.Bind(TemplatedControl.FontSizeProperty, new DynamicResourceExtension("BodyFontSize"));
        AutomationProperties.SetName(window, title);

        window.WindowStartupLocation = FindOwnerWindow() is not null
            ? WindowStartupLocation.CenterOwner
            : WindowStartupLocation.CenterScreen;

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

    /// <summary>ボタン操作による正常終了。結果を確定してから閉じる（Closedの既定値上書きを防ぐ順序）。</summary>
    private static void Complete<T>(Window window, TaskCompletionSource<T> tcs, T value)
    {
        tcs.TrySetResult(value);
        window.Close();
    }

    private static void Complete(Window window, TaskCompletionSource tcs)
    {
        tcs.TrySetResult();
        window.Close();
    }

    /// <summary>
    /// モーダルとして表示する。オーナーが見つかる通常時は<see cref="Window.ShowDialog(Window)"/>を
    /// 使い、オーナー入力を塞ぐ。見つからない場合のみ非モーダル表示へ縮退する
    /// （クラスのコメント参照）。戻り値は使わず、完了判定は呼び出し側が渡す
    /// <see cref="TaskCompletionSource"/>で行う。
    /// </summary>
    private static void ShowModal(Window window)
    {
        var owner = FindOwnerWindow();
        if (owner is not null)
        {
            _ = window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }

    /// <summary>
    /// 表示中で使用可能なウィンドウを親として探す。デスクトップのライフタイムが取得できない、
    /// または表示中のウィンドウが無い場合はnullを返し、呼び出し側は画面中央起点や
    /// 非モーダル表示へフォールバックする。
    /// </summary>
    private static Window? FindOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        foreach (var candidate in desktop.Windows)
        {
            if (candidate.IsActive && candidate.IsVisible)
            {
                return candidate;
            }
        }

        return desktop.MainWindow is { IsVisible: true } ? desktop.MainWindow : null;
    }
}
