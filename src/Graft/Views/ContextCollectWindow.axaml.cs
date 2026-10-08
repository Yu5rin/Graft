using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// コンテキスト収集ウィンドウ（仕様書10章）。DataContextには
/// <see cref="ContextCollectViewModel"/>を受け取る。v2.0のWPF版からの移植（19章 L3）。
/// </summary>
public partial class ContextCollectWindow : Window
{
    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。</summary>
    public ContextCollectWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        // 細かいユーザビリティ改善5: 開いた直後の初期フォーカスを最初の操作対象（収集モード）へ当てる。
        // headlessテスト・デザイナ用のこのコンストラクタでも効くよう、DataContextを必要としない
        // ここに置く（DataContextを持つコンストラクタ側にだけ置くと、テストで既定コンストラクタを
        // 直接使った場合にフォーカスが当たらなくなる）。
        Loaded += (_, _) => ModeComboBox.Focus();
    }

    /// <param name="viewModel">コンテキスト収集のViewModel。</param>
    /// <param name="requestedPaths">
    /// AIが「このファイルも見せて」（E710）と求めたパス。空でなければ、初回の走査が終わったあとに
    /// そのファイルを「内容も出す」に設定し、結果（見つからない・除外されている等）を
    /// ステータス欄に出す。走査の前に設定すると、走査が選択状態を作り直して失われるため、
    /// 必ず<see cref="ContextCollectViewModel.InitializeAsync"/>の後に行う。
    /// </param>
    public ContextCollectWindow(ContextCollectViewModel viewModel, IReadOnlyList<string>? requestedPaths = null) : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            await SafeHandler.RunAsync("コンテキスト収集の初期化", () => viewModel.InitializeAsync())
                .ConfigureAwait(true);
            if (requestedPaths is { Count: > 0 })
            {
                await SafeHandler.RunAsync("要求されたファイルの反映", () => viewModel.ApplyRequestedFilesAsync(requestedPaths))
                    .ConfigureAwait(true);
            }
        };
    }

    /// <summary>
    /// 窓全体のキー操作（トンネル＝子より先に受ける）。
    ///
    /// 【Esc】従来どおり窓を閉じる。ただしファイル名の絞り込み欄にフォーカスがあり、中身が
    /// あるときだけは、窓を閉じずに欄を空にする（絞り込みを解いてツリーへ戻す）。
    /// 入力欄でEscを押す利用者の意図は「入力を取り消す」であり、絞り込み途中のつもりで
    /// 押して窓ごと閉じる（選択途中の状態を失ったように感じる）のを避けるため。
    /// 欄が空のとき、または別の部品にフォーカスがあるときは、これまでと変わらず窓を閉じる
    /// （Escで必ず閉じられる、という他のダイアログとの一貫性を保つ）。
    /// 絞り込み欄の中身の有無は、ViewModelではなく欄自身のTextで見る。DataContextを持たない
    /// 既定コンストラクタ（デザイナ・一部のテスト）でも同じ挙動になるようにするため。
    ///
    /// 【Ctrl+F】絞り込み欄へ移る（この窓には、ほかにCtrl+Fを使う操作が無い。エディタの検索の
    /// Ctrl+Fは別のウィンドウの話で、このモーダルの中では衝突しない）。プレビュータブを
    /// 見ているときも、ファイル選択タブへ切り替えてから移る。すでに欄にいるときは、
    /// 全選択して打ち直しやすくする（多くのアプリのCtrl+Fの挙動に合わせる）。
    /// </summary>
    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (FileFilterBox.IsKeyboardFocusWithin && !string.IsNullOrEmpty(FileFilterBox.Text))
            {
                FileFilterBox.Text = string.Empty;
                e.Handled = true;
                return;
            }

            Close();
            return;
        }

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            FocusFileFilter();
            e.Handled = true;
        }
    }

    /// <summary>ファイル選択タブを表示して、絞り込み欄へフォーカスを移す。</summary>
    private void FocusFileFilter()
    {
        MainTabs.SelectedIndex = 0;
        FileFilterBox.Focus();
        FileFilterBox.SelectAll();
    }

    /// <summary>
    /// 実機で確認された指摘7: 設定・キュー・取扱説明書にはある「閉じる」ボタンがこのウィンドウ
    /// にだけ無く、マウスだけの利用者にはタイトルバーの×しか到達手段が無かった。
    /// </summary>
    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
