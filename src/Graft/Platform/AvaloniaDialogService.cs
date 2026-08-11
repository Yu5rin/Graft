using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Graft.Core;

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
///
/// 【不具合修正（実機報告）: ボタンの並び順】以前はボタンを「キャンセル→否定→肯定」の順に
/// 並べていたが、実機（Windows）のスクリーンショットで「保存されていない変更があります」の
/// 確認ダイアログが「キャンセル」「破棄」「保存」の順に表示され、Windowsの作法
/// （メモ帳・Office・VS Codeはいずれも肯定的な選択肢が左）と逆になっていることが判明した。
/// <see cref="ConfirmAsync"/>・<see cref="ConfirmThreeWayAsync"/>・<see cref="PromptAsync"/>の
/// 3つ（ボタンを複数持つ既定実装）すべてで「肯定→否定→キャンセル」の順（左から）に統一する
/// （<see cref="ShowMessageAsync"/>・<see cref="ShowActionMessageAsync"/>はボタン1つのみのため
/// 対象外）。既定ボタン（IsDefault、Enterで実行される）は引き続き最も左の肯定的なボタンに
/// 付けるが、それが破壊的・不可逆な操作を意味する場合は、呼び出し側で
/// どちらのラベルを<c>yesLabel</c>に渡すか（＝どちらを既定にするか）を選ぶ必要がある
/// （<see cref="ConfirmThreeWayAsync"/>のコメント参照。呼び出し元の例:
/// <see cref="Graft.ViewModels.ProjectPaneViewModel"/>のプロジェクト削除確認）。
/// </summary>
public sealed class AvaloniaDialogService : IDialogService
{
    /// <summary>
    /// テスト専用の差し替え口。ボタン行を組み立て終え、実際に表示する（<see cref="ShowModal"/>を
    /// 呼ぶ）直前に、その時点のウィンドウと左から並んだボタン一覧を通知する。
    ///
    /// 【なぜ要るか】<c>DialogKeyboardCoverageTests</c>のコメントにあるとおり、
    /// <see cref="ConfirmAsync"/>等が動的に組み立てる<see cref="Window"/>は<see cref="Task{TResult}"/>
    /// しか外部へ返さず、実際に表示されたダイアログへ外部（テスト）からアクセスする手段が無い
    /// （<see cref="ShowModal"/>がオーナーを見つけられないheadlessテスト環境では非モーダルの
    /// <see cref="Window.Show()"/>へ縮退するため、<see cref="Window.OwnedWindows"/>経由でも
    /// 辿れない）。不具合2（ボタンの並び順が実機で逆だった）の回帰を自動テストで押さえるため、
    /// 他のテスト用差し替え口（<see cref="Views.EditorPane.MarkdownLinkDialogs"/>等）と同じ考え方で
    /// この最小限のフックを追加した。本番では常にnull（呼び出しコストは無視できるnullチェックのみ）。
    /// </summary>
    internal static Action<Window, IReadOnlyList<Button>>? OnButtonRowBuiltForTests;

    /// <summary>
    /// <see cref="OnButtonRowBuiltForTests"/>を、<paramref name="buttons"/>（<c>AddButtonRow</c>が
    /// 返した右詰めのボタン行そのもの）の<c>Children</c>から実際の並び順を読み取って呼び出す。
    /// 呼び出し元が別途組み立てた配列を渡すのではなく、必ずこの<c>Children</c>から読み取ることで、
    /// テストが検証する順序が実際に表示される順序と一致することを保証する（ボタンを追加する
    /// 順序を直す修正を将来誰かがうっかり戻しても、テストの側は自動的にそれを検知できる）。
    /// </summary>
    private static void NotifyButtonRowBuiltForTests(Window window, StackPanel buttons)
    {
        if (OnButtonRowBuiltForTests is null) return;
        OnButtonRowBuiltForTests(window, buttons.Children.OfType<Button>().ToList());
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        var window = BuildShell(title, out var body);
        AddMessage(body, message);
        var tcs = new TaskCompletionSource<bool>();

        var buttons = AddButtonRow(body, title, message);
        var ok = CreateButton("OK", isDefault: true, isCancel: false);
        var cancel = CreateButton("キャンセル", isDefault: false, isCancel: true);
        ok.Click += (_, _) => Complete(window, tcs, true);
        cancel.Click += (_, _) => Complete(window, tcs, false);
        // 不具合修正（実機報告）: Windowsの作法（メモ帳・Office等）は肯定的な選択肢が左。
        // 「肯定→否定→キャンセル」の順に揃える（クラスコメント・AddButtonRow参照）。
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        window.Loaded += (_, _) => ok.Focus();
        window.Closed += (_, _) => tcs.TrySetResult(false);
        NotifyButtonRowBuiltForTests(window, buttons);
        ShowModal(window);
        return tcs.Task;
    }

    /// <returns>肯定ならtrue、否定ならfalse、キャンセルならnull。</returns>
    public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
    {
        var window = BuildShell(title, out var body);
        AddMessage(body, message);
        var tcs = new TaskCompletionSource<bool?>();

        var buttons = AddButtonRow(body, title, message);
        var yes = CreateButton(yesLabel, isDefault: true, isCancel: false);
        var no = CreateButton(noLabel, isDefault: false, isCancel: false);
        var cancel = CreateButton("キャンセル", isDefault: false, isCancel: true);
        yes.Click += (_, _) => Complete(window, tcs, true);
        no.Click += (_, _) => Complete(window, tcs, false);
        cancel.Click += (_, _) => Complete(window, tcs, null);
        // 不具合修正（実機報告）: 「肯定→否定→キャンセル」の順に揃える（ConfirmAsync参照）。
        // 呼び出し側は「肯定（yesLabel）」に破壊的・不可逆な選択肢を渡さないこと
        // （yesLabelは既定ボタン＝Enterで実行されるため。各呼び出し元のコメント参照）。
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        buttons.Children.Add(cancel);

        window.Loaded += (_, _) => yes.Focus();
        window.Closed += (_, _) => tcs.TrySetResult(null);
        NotifyButtonRowBuiltForTests(window, buttons);
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
        // 不具合修正（実機報告）: 「肯定→キャンセル」の順に揃える（ConfirmAsync参照）。
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        window.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        window.Closed += (_, _) => tcs.TrySetResult(null);
        NotifyButtonRowBuiltForTests(window, buttons);
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
    /// エクスプローラへの取り込み（「ファイルを追加」）用の複数選択版。<see cref="PickFileAsync"/>と
    /// 同じ設計方針で、<c>AllowMultiple = true</c>にする点だけが異なる。
    /// </summary>
    public async Task<IReadOnlyList<string>?> PickFilesAsync(string title, IReadOnlyList<string>? extensions = null)
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
            AllowMultiple = true,
            FileTypeFilter = BuildFileTypeFilter(extensions),
        };
        var picked = await provider.OpenFilePickerAsync(options).ConfigureAwait(true);
        var paths = picked.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        return paths.Count == 0 ? null : paths;
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

        var buttons = AddButtonRow(body, title, message);
        var ok = CreateButton("OK", isDefault: true, isCancel: true);
        ok.Click += (_, _) => Complete(window, tcs);
        buttons.Children.Add(ok);

        window.Loaded += (_, _) => ok.Focus();
        window.Closed += (_, _) => tcs.TrySetResult();
        ShowModal(window);
        return tcs.Task;
    }

    /// <summary>
    /// 不具合3: <see cref="ShowMessageAsync"/>と同じ見た目だが、ボタンのラベルを差し替えられる
    /// （例:「再起動」）。<see cref="ShowMessageAsync"/>と異なり、そのボタンが実際に押されたか
    /// （＝アクションを実行してよいか）を呼び出し側が区別できるよう、タイトルバーの×で
    /// 閉じられた場合はfalseを返す（あえて<c>isCancel: false</c>にし、Escapeキーではアクション
    /// ボタンが起動しないようにする。Escapeでの意図しない再起動等を避けるため）。
    /// </summary>
    public Task<bool> ShowActionMessageAsync(string title, string message, string actionLabel)
    {
        var window = BuildShell(title, out var body);
        AddMessage(body, message);
        var tcs = new TaskCompletionSource<bool>();

        var buttons = AddButtonRow(body, title, message);
        var action = CreateButton(actionLabel, isDefault: true, isCancel: false);
        action.Click += (_, _) => Complete(window, tcs, true);
        buttons.Children.Add(action);

        window.Loaded += (_, _) => action.Focus();
        window.Closed += (_, _) => tcs.TrySetResult(false);
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

    /// <summary>
    /// ボタン行を組み立てる。戻り値は右詰めの主要ボタン（OK・キャンセル等）を並べる
    /// <see cref="StackPanel"/>で、既存の呼び出しパターン（<c>buttons.Children.Add(...)</c>）を
    /// 変えずに済むようにしてある。
    ///
    /// 機能1: <paramref name="title"/>・<paramref name="message"/>の両方が渡され、かつ
    /// メッセージにエラーコードのパターンが含まれる場合（<see cref="ErrorDetailFormatter.ContainsErrorCode"/>）
    /// にだけ、左詰めで「詳細をコピー」ボタンを追加する。これにより<see cref="IDialogService"/>の
    /// シグネチャや呼び出し側を一切変えずに、単一箇所（このメソッド）だけで
    /// 「エラー由来のメッセージにはボタンを出す／通常の確認メッセージには出さない」を実現する
    /// （クラスの呼び出し元一覧は<see cref="ErrorDetailFormatter"/>のコメント参照）。
    /// 主要ボタン列とは左右で分けて配置することで、通常操作の妨げにならないようにする
    /// （<see cref="PromptAsync"/>は<paramref name="title"/>・<paramref name="message"/>を
    /// 渡さないため、常にボタンは出ない）。
    /// </summary>
    private static StackPanel AddButtonRow(StackPanel body, string? title = null, string? message = null)
    {
        var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        body.Children.Add(row);

        if (title is not null && message is not null && ErrorDetailFormatter.ContainsErrorCode(message))
        {
            var copyButton = CreateButton("詳細をコピー", isDefault: false, isCancel: false);
            copyButton.HorizontalAlignment = HorizontalAlignment.Left;
            copyButton.Margin = new Thickness(0);
            copyButton.Click += (_, _) => CopyIssueDetails(title, message);
            Grid.SetColumn(copyButton, 0);
            row.Children.Add(copyButton);
        }

        var actionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(actionButtons, 1);
        row.Children.Add(actionButtons);
        return actionButtons;
    }

    /// <summary>
    /// 「詳細をコピー」の実処理。文面組み立ては<see cref="ErrorDetailFormatter"/>（純粋関数・
    /// 単体テスト対象）に委ね、ここでは実行環境固有の値（バージョン・OS）の取得と、
    /// クリップボードへの書き込みのみを担う。書き込みは<see cref="AvaloniaUiServices.SharedClipboard"/>
    /// （Linuxでは自前のX11実装を優先する既存経路。<see cref="AvaloniaUiServices"/>のコメント参照）を
    /// 再利用し、専用の書き込み経路を新設しない。
    /// </summary>
    private static void CopyIssueDetails(string title, string message)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "不明";
        var text = ErrorDetailFormatter.BuildCopyText(title, message, version, RuntimeInformation.OSDescription);
        AvaloniaUiServices.SharedClipboard.SetText(text);
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
