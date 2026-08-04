using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// <see cref="ShellWindow"/> のキーボード操作（仕様書9.5・附録A「キーマップ移行」）を担う分割
/// ファイル（1ファイル400行上限のため）。エディタが処理すべきキー（Ctrl+F/H/G/W/Tab/
/// Ctrl+/・Ctrl+Space等）はここで一切横取りしない。フォーカスがエディタ内か、通常の
/// テキスト入力欄（TextBox等）かは <see cref="IsTextInput"/> で判定する
/// （AvalonEditはTextBoxBaseを継承しないため、エディタ領域はEditorHostへの包含で判定する）。
/// </summary>
public partial class ShellWindow
{
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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
        if (HandleUnconditionalShortcut(e.Key))
        {
            e.Handled = true;
            return;
        }

        var focused = Keyboard.FocusedElement as DependencyObject;
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

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
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
    private bool HandleUnconditionalShortcut(Key key)
    {
        var mods = Keyboard.Modifiers;
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift)) return HandleCtrlShiftShortcut(key);
        if (mods == (ModifierKeys.Control | ModifierKeys.Alt)) return HandleCtrlAltShortcut(key);
        if (mods == ModifierKeys.Control) return HandlePlainCtrlUnconditionalShortcut(key);
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
            default: return false; // F/G/W/Tab/Y/`/`/Space等はここでも何もしない。
        }
    }

    /// <summary>
    /// F6: サイドビュー（表示中のもの） → エディタ領域 → ブロック一覧 → diff の順に
    /// フォーカスを巡回する。
    /// </summary>
    private void CyclePaneFocus()
    {
        FrameworkElement? sideViewTarget = ViewModel.SelectedSideView switch
        {
            SideViewKind.Project => ProjectPaneControl.ListBoxElement,
            SideViewKind.History => HistoryPaneControl.ListBoxElement,
            _ => null,
        };

        var targets = new FrameworkElement?[]
        {
            sideViewTarget,
            EditorHost,
            GraftPanelControl.ListBoxElement,
            GraftPanelControl.DiffHost,
        };

        var current = Keyboard.FocusedElement as DependencyObject;
        var currentIndex = Array.FindIndex(targets, t => t is not null && IsDescendant(current, t));
        for (var offset = 1; offset <= targets.Length; offset++)
        {
            var candidate = targets[(currentIndex + offset + targets.Length) % targets.Length];
            if (candidate is null) continue;
            candidate.Focus();
            break;
        }
    }

    private static bool IsTextInput(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is TextBoxBase)
            {
                return true;
            }
            element = GetParent(element);
        }
        return false;
    }

    private static bool IsDescendant(DependencyObject? element, DependencyObject? ancestor)
    {
        if (ancestor is null)
        {
            return false;
        }
        while (element is not null)
        {
            if (Equals(element, ancestor))
            {
                return true;
            }
            element = GetParent(element);
        }
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
        => element is Visual or Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
}
