using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="ShellWindow"/> の分割ファイル（1ファイル400行上限のため）。コマンドバー左端の
/// プロジェクト選択<see cref="ShellWindow.ProjectComboBox"/>を、クリックしてフォーカスを
/// 当てなくても、マウスカーソルを乗せてホイールを回すだけで切り替えられるようにする
/// （利用者からの要望）。AvaloniaのComboBoxは既定でホイールによる選択変更を行わないため、
/// EditorPane.TabStrip.cs（タブ列の横スクロール）・DiffView.axaml.cs（Ctrl+ホイールの
/// フォントサイズ変更）と同じ作法（AvaloniaにPreviewMouseWheelは無いため、トンネリング段階で
/// PointerWheelChangedを拾う）で実装する。
/// </summary>
public partial class ShellWindow
{
    /// <summary>コンストラクタから1回だけ呼ぶ（ShellWindow()参照）。</summary>
    private void InitializeProjectComboBoxWheel()
    {
        ProjectComboBox.AddHandler(PointerWheelChangedEvent, OnProjectComboBoxPointerWheelChanged, RoutingStrategies.Tunnel);
    }

    private void OnProjectComboBoxPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // ドロップダウンが開いている間は、ホイールは一覧のスクロールという既定の（利用者が
        // 見慣れた）挙動に譲る。ここで選択切り替えとして横取りすると、開いた一覧を見ながら
        // スクロールしているつもりが選んでいる項目そのものが変わってしまい、「一覧を見て
        // 選ぶ」操作と「閉じたまま素早く切り替える」操作が衝突する。
        if (ProjectComboBox.IsDropDownOpen) return;

        var count = ProjectComboBox.ItemCount;
        if (count == 0) return; // 未登録時に空振りしても例外にならないようにする。

        // 連続切り替え対策（案B・重要）: プロジェクトの切り替え1件はEditor.CloseAllAsync
        // （未保存の変更があれば保存確認ダイアログを出す）・Explorer/Search/QuickOpenの
        // 再構築等、重い処理を伴う（ShellViewModel.OnProjectSelected参照）。ホイールは
        // 1回転で何ノッチも進むため、対策無しで素直にSelectedIndexを進めると、回した分だけ
        // 切り替えがキューされ、未保存確認ダイアログが積み重なって出る事故になる
        // （実際にEditor.CloseAllAsync→EditorTabManager.CloseAsyncの経路でダイアログが
        // 出ることをコードで確認済み）。デバウンス（回し終えてから1回だけ確定する案）も
        // 検討したが、ComboBoxのSelectedItemはViewModelとTwoWayバインディングで直結して
        // おり、確定を遅らせながら見た目（候補のハイライト）だけを動かすには双方向
        // バインディングを一時的に迂回するしくみが要る。無理に迂回すると「回している間、
        // 表示中の候補と実際の選択がずれる」状態が生まれてしまうため、実装が単純でずれの
        // 起きない「前の切り替えが終わるまで後続のホイール入力を無視する」方式を採る。
        if (DataContext is ShellViewModel { IsProjectSwitchBusy: true })
        {
            e.Handled = true;
            return;
        }

        var delta = e.Delta.Y;
        if (delta == 0) return;

        e.Handled = true;

        var currentIndex = ProjectComboBox.SelectedIndex;
        if (currentIndex < 0)
        {
            // 未選択（クリックして選んだことが一度も無い）状態でホイールを回したときは、
            // 上下どちらの向きでも先頭を選ぶ。「未選択でもホイールで切り替えられるように」
            // という要望を、クリック不要でそのまま操作を始められる形として満たす。
            ProjectComboBox.SelectedIndex = 0;
            return;
        }

        // 端で止める（ラップしない）。先頭から末尾、末尾から先頭へ回り込むと、ホイールを
        // 数回多く回しただけで意図せず遠く離れた別のプロジェクトへ飛んでしまう
        // （隣へ1件ずつ進む操作という前提を崩さないようにする）。
        if (delta > 0)
        {
            if (currentIndex > 0) ProjectComboBox.SelectedIndex = currentIndex - 1;
        }
        else
        {
            if (currentIndex < count - 1) ProjectComboBox.SelectedIndex = currentIndex + 1;
        }
    }
}
