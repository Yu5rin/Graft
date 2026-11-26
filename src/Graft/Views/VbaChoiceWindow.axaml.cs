using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.Core.Vba;

namespace Graft.Views;

/// <summary>
/// 選択肢つきの確認ダイアログ（<see cref="VbaChoiceRequest"/>）。ボタンの文言と数を呼び出し側が決められる。
/// Esc・×で閉じたときは、既定のボタン（<see cref="VbaChoiceRequest.Default"/>）を選んだのと同じ結果にする
/// （Push 失敗なら「ファイルの変更は残す」＝何も失わない側）。
/// </summary>
public partial class VbaChoiceWindow : Window
{
    private readonly VbaChoiceRequest _request;
    private readonly Action<string>? _openWorkbook;
    private string? _chosen;

    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public VbaChoiceWindow() : this(new VbaChoiceRequest("確認", string.Empty, new[] { new VbaChoiceOption("ok", "OK", IsDefault: true) }))
    {
    }

    /// <param name="request">ボタンの文言と数。</param>
    /// <param name="openWorkbook">
    /// 「Excel で開く」ボタン（<see cref="VbaChoiceRequest.OpenWorkbookPath"/> があるとき）が呼ぶ処理。ブックのフルパスを渡す。
    /// null ならボタンを出さない（開く手段が無い環境・テスト）。
    /// </param>
    public VbaChoiceWindow(VbaChoiceRequest request, Action<string>? openWorkbook = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _openWorkbook = openWorkbook;
        InitializeComponent();
        Title = request.Title;
        AutomationProperties.SetName(this, request.Title);
        MessageText.Text = request.Message;
        AutomationProperties.SetName(MessageText, request.Message);

        // E804（ブックが開いていない）のときの「Excel で開く」。選択のボタンとは別で、押しても窓は閉じない
        // （開いたあとに、そのまま「ファイルだけ適用する」「中止」などを選べるように）。いちばん左に置き、
        // 右側の選択のボタン（肯定的な選択肢が左）と区別する。
        if (request.OpenWorkbookPath is { Length: > 0 } workbookPath && openWorkbook is not null)
        {
            var openButton = new Button { Content = "Excel で開く", MinWidth = 88, Margin = new Avalonia.Thickness(0, 0, 16, 0) };
            AutomationProperties.SetName(openButton, "Excel でブックを開く");
            openButton.Click += (_, _) => openWorkbook(workbookPath);
            openButton.Name = "OpenWorkbookButton";
            ButtonRow.Children.Add(openButton);
        }

        Button? defaultButton = null;
        foreach (var option in request.Options)
        {
            var button = new Button { Content = option.Label, MinWidth = 88 };
            if (option.IsDefault)
            {
                button.Classes.Add("primary");
                button.IsDefault = true;
                defaultButton = button;
            }

            AutomationProperties.SetName(button, option.Label);
            var id = option.Id;
            if (option.KeepOpenAction is { } keepOpen)
            {
                // 窓を閉じない操作（VBE を開いてロックを解除する）。実行中に重ねて押されないよう、終わるまで無効にする。
                button.Name = "KeepOpen_" + id;
                button.Click += async (_, _) =>
                {
                    button.IsEnabled = false;
                    try
                    {
                        await keepOpen().ConfigureAwait(true);
                    }
                    finally
                    {
                        button.IsEnabled = true;
                    }
                };
                ButtonRow.Children.Add(button);
                continue;
            }

            button.Click += (_, _) =>
            {
                _chosen = id;
                Close();
            };
            ButtonRow.Children.Add(button);
        }

        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        Loaded += (_, _) => defaultButton?.Focus();
    }

    /// <summary>モーダル表示し、閉じるまで待って、選ばれたボタンの識別子を返す。</summary>
    public async Task<string> ShowAndChooseAsync(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await ShowDialog(owner).ConfigureAwait(true);
        return _chosen ?? _request.Default.Id;
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
}
