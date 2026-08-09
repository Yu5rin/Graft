using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit.Editing;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="ShellWindow"/> のキーボード操作（仕様書9.5・附録A「キーマップ移行」）を担う分割
/// ファイル（1ファイル400行上限のため）。エディタが処理すべきキー（Ctrl+F/H/G/W/Tab/
/// Ctrl+/・Ctrl+Space等）はここで一切横取りしない。フォーカスがエディタ内か、通常の
/// テキスト入力欄（TextBox等）かは <see cref="IsTextInput"/> で判定する。
///
/// v2.0のWPF版からの移植（19章 L3）。PreviewKeyDown が無いためトンネリング段階の
/// KeyDown を AddHandler で購読し、Keyboard.FocusedElement は
/// <see cref="TopLevel.FocusManager"/> から取得する。AvaloniaEditのTextAreaは
/// TextBoxを継承しないため、v2.0のWPF版と同様にエディタ領域（EditorHost）への包含でも判定する。
/// </summary>
public partial class ShellWindow
{
    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel) return;

        // クイックオープン（Ctrl+P）が開いている間は、上下キー・Enter・Escapeを
        // フォーカス位置に関わらずここで処理する。検索ボックス（TextBox）へフォーカスが
        // あってもトンネリング段階のこのハンドラが先に届くため、他の分岐より前に判定する。
        if (ViewModel.QuickOpen.IsOpen && HandleQuickOpenKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            ViewModel.Graft.DiscardCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F6)
        {
            CyclePaneFocus();
            e.Handled = true;
            return;
        }

        // 9.5: Ctrl+Shift+V/C/H/E/F・Ctrl+Alt+Z/1〜9・Ctrl+J・Ctrl+S はエディタの標準操作と
        // 衝突しない組み合わせのため、フォーカス位置に関わらずシェル側で処理する。
        if (HandleUnconditionalShortcut(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
            return;
        }

        var focused = FocusManager?.GetFocusedElement() as Visual;
        var inTextInput = IsTextInput(focused) || IsDescendant(focused, EditorHost);

        if (e.Key == Key.Space && !inTextInput && IsDescendant(focused, GraftPanelControl.ListBoxElement))
        {
            ViewModel.Graft.SelectedBlock?.Toggle();
            e.Handled = true;
            return;
        }

        if (inTextInput)
        {
            // Ctrl+F/H/G/W/Tab/Ctrl+//Ctrl+Space、Ctrl+Z/Y（アンドゥ/リドゥ）等は
            // エディタ・テキストボックスの既定処理に委ねる（9.5）。
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            // 課題2: エクスプローラにフォーカスがあるときのCtrl+Zは「削除の取り消し」に使うため、
            // ここでは横取りしない（Handledをtrueにしない）。ExplorerView.axamlの
            // UserControl.KeyBindings（バブリング段階）へそのまま届かせ、
            // ExplorerViewModel.UndoDeleteCommandを実行させる。エディタ内のCtrl+Z
            // （テキストの取り消し）はこれより前のinTextInput分岐で既に処理済みのため衝突しない。
            if (e.Key == Key.Z && IsDescendant(focused, ExplorerViewControl)) return;

            e.Handled = HandlePlainCtrlGatedShortcut(e.Key);
            return;
        }

        if (e.Key is >= Key.D1 and <= Key.D9)
        {
            // 附録A キーマップ移行: 素の1〜9は仕様書3.2によりCtrl+Alt+1〜9へ変更された。
            ViewModel.NotifyLegacyKey(LegacyKey.ProjectDigit);
        }
    }

    /// <summary>Ctrl+Shift+*・Ctrl+Alt+*・一部の素のCtrl+*（J/S/Enter）をフォーカス位置に関係なく処理する。</summary>
    private bool HandleUnconditionalShortcut(Key key, KeyModifiers mods)
    {
        if (mods == (KeyModifiers.Control | KeyModifiers.Shift)) return HandleCtrlShiftShortcut(key);
        if (mods == (KeyModifiers.Control | KeyModifiers.Alt)) return HandleCtrlAltShortcut(key);
        if (mods == KeyModifiers.Control) return HandlePlainCtrlUnconditionalShortcut(key);
        return false;
    }

    private bool HandleCtrlShiftShortcut(Key key)
    {
        switch (key)
        {
            case Key.C: ViewModel.Graft.CopyPromptCommand.Execute(null); return true;
            case Key.V: ViewModel.Graft.PasteAndParseCommand.Execute(null); return true; // 素のCtrl+Vから変更（9.5）。
            case Key.H: ViewModel.Graft.ShowHistoryCommand.Execute(null); return true; // Ctrl+Hから変更（9.5）。
            case Key.E: ViewModel.SelectSideView(SideViewKind.Explorer); return true;
            case Key.F: ViewModel.SelectSideView(SideViewKind.Search); return true;
            case Key.S: _ = ViewModel.Editor.SaveAllAsync(); return true;
            default: return false;
        }
    }

    private bool HandleCtrlAltShortcut(Key key)
    {
        if (key == Key.Z) // Ctrl+Zから変更（9.5）。Ctrl+Zはエディタのアンドゥへ。
        {
            ViewModel.Graft.UndoCommand.Execute(null);
            return true;
        }
        if (key is >= Key.D1 and <= Key.D9) // 素の1〜9から変更（3.2・9.5）。
        {
            ViewModel.Graft.ProjectPane.SelectByShortcut(key - Key.D0);
            return true;
        }
        return false;
    }

    private bool HandlePlainCtrlUnconditionalShortcut(Key key)
    {
        switch (key)
        {
            case Key.J: ViewModel.ToggleGraftPanelCommand.Execute(null); return true;
            case Key.S: _ = ViewModel.Editor.SaveActiveAsync(); return true;
            case Key.Enter: ViewModel.Graft.ApplyCommand.Execute(null); return true;
            // クイックオープン: AvaloniaEdit標準のキーバインドと衝突しないため、
            // エディタ内フォーカス時も含め常に反応させる（他の素のCtrl+*と同じ扱い）。
            case Key.P: ViewModel.ToggleQuickOpenCommand.Execute(null); return true;
            default: return false;
        }
    }

    /// <summary>クイックオープンが開いている間の上下キー・Enter・Escape。</summary>
    private bool HandleQuickOpenKey(Key key)
    {
        switch (key)
        {
            case Key.Escape: ViewModel.QuickOpen.Close(); return true;
            case Key.Down: ViewModel.QuickOpen.MoveSelection(1); return true;
            case Key.Up: ViewModel.QuickOpen.MoveSelection(-1); return true;
            case Key.Enter: ViewModel.QuickOpen.ConfirmSelection(); return true;
            default: return false;
        }
    }

    /// <summary>
    /// テキスト入力欄・エディタのいずれにもフォーカスが無いときだけ届く素のCtrl+*。
    /// V/Z/Hは附録Aのキーマップ移行通知（旧キーが押されたことを1回だけ知らせる）。
    /// </summary>
    private bool HandlePlainCtrlGatedShortcut(Key key)
    {
        switch (key)
        {
            case Key.V: ViewModel.NotifyLegacyKey(LegacyKey.PasteCtrlV); return true;
            case Key.Z: ViewModel.NotifyLegacyKey(LegacyKey.UndoCtrlZ); return true;
            case Key.H: ViewModel.NotifyLegacyKey(LegacyKey.HistoryCtrlH); return true;
            case Key.OemComma: ViewModel.Graft.OpenSettingsCommand.Execute(null); return true;
            // Ctrl+/: ショートカット一覧を開く。ドキュメントタブを開いたエディタ内では同じキーが
            // 行コメントの切り替え（EditorPane.axaml.cs）に使われているため、ここは
            // テキスト入力欄・エディタの外（inTextInputがfalse）でのみ届く経路にとどめ、
            // 既存の行コメント操作を横取りしないようにする。
            case Key.OemQuestion or Key.Divide: ViewModel.OpenShortcutsCommand.Execute(null); return true;
            default: return false; // F/G/W/Tab/Y/Space等はここでも何もしない。
        }
    }

    /// <summary>
    /// F6: サイドビュー（表示中のもの） → エディタ領域 → ブロック一覧 → 適用ボタンの順に
    /// フォーカスを巡回する。
    /// </summary>
    private void CyclePaneFocus()
    {
        Control? sideViewTarget = ViewModel.SelectedSideView switch
        {
            SideViewKind.Project => ProjectPaneControl.ListBoxElement,
            SideViewKind.History => HistoryPaneControl.ListBoxElement,
            _ => null,
        };

        var targets = new Control?[]
        {
            sideViewTarget,
            EditorHost,
            GraftPanelControl.ListBoxElement,
            GraftPanelControl.DiffHost,
        };

        var current = FocusManager?.GetFocusedElement() as Visual;
        var currentIndex = Array.FindIndex(targets, t => t is not null && IsDescendant(current, t));
        for (var offset = 1; offset <= targets.Length; offset++)
        {
            var candidate = targets[(currentIndex + offset + targets.Length) % targets.Length];
            if (candidate is null) continue;
            candidate.Focus();
            break;
        }
    }

    private static bool IsTextInput(Visual? element)
    {
        while (element is not null)
        {
            // AvaloniaEditのTextAreaはTextBoxを継承しないため個別に判定する
            // （v2.0のWPF版がTextBoxBaseで拾えていた範囲に相当させるため）。
            if (element is TextBox or TextPresenter or TextArea) return true;
            element = element.GetVisualParent();
        }
        return false;
    }

    private static bool IsDescendant(Visual? element, Visual? ancestor)
    {
        if (ancestor is null) return false;

        while (element is not null)
        {
            if (ReferenceEquals(element, ancestor)) return true;
            element = element.GetVisualParent();
        }
        return false;
    }
}
