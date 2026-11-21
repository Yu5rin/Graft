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
    private string? _chosen;

    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public VbaChoiceWindow() : this(new VbaChoiceRequest("確認", string.Empty, new[] { new VbaChoiceOption("ok", "OK", IsDefault: true) }))
    {
    }

    public VbaChoiceWindow(VbaChoiceRequest request)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        InitializeComponent();
        Title = request.Title;
        AutomationProperties.SetName(this, request.Title);
        MessageText.Text = request.Message;
        AutomationProperties.SetName(MessageText, request.Message);

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
