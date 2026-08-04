using System.Collections.ObjectModel;
using Graft.Core;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.Editor;

/// <summary>
/// エディタタブの生成・復元・保存確認を担当する（19章）。<see cref="Graft.ViewModels.EditorPaneViewModel"/>
/// から呼び出される、ObservableObjectではない純粋なロジック層。プレビュータブの置換規則
/// （4.3節）とCtrl+Tab用の直近使用順（MRU）もここで管理する。
/// 保存/破棄/キャンセルの3択確認は<see cref="DialogService.ConfirmThreeWayAsync"/>を使う。
/// </summary>
public sealed class EditorTabManager
{
    private readonly DialogService _dialogs;
    private readonly List<EditorTabViewModel> _mru = new();
    private string? _projectRoot;

    public EditorTabManager(DialogService dialogs)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    public ObservableCollection<EditorTabViewModel> Tabs { get; } = new();

    /// <summary>切替先のプロジェクトルートを設定する。タブは呼び出し側が事前に閉じておくこと。</summary>
    public void SetProjectRoot(string? projectRoot)
    {
        _projectRoot = projectRoot;
        _mru.Clear();
    }

    /// <summary>ファイルを開く。既に開いている場合はそのタブを返す（プレビュー→固定への昇格のみ行う）。</summary>
    public async Task<GraftResult<EditorTabViewModel>> OpenAsync(string fullPath, bool preview, CancellationToken ct)
    {
        var existing = Tabs.FirstOrDefault(t => PathsEqual(t.Session.FullPath, fullPath));
        if (existing is not null)
        {
            if (!preview) existing.IsPreview = false;
            return GraftResult<EditorTabViewModel>.Ok(existing);
        }

        var opened = await DocumentSession.OpenAsync(fullPath, _projectRoot ?? string.Empty, ct).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return GraftResult<EditorTabViewModel>.Fail(opened.Issues);
        }

        if (preview)
        {
            // 4.3: 未編集のプレビュータブは確認なしで次のプレビューに置換される。
            var oldPreview = Tabs.FirstOrDefault(t => t.IsPreview);
            if (oldPreview is not null) RemoveTab(oldPreview);
        }

        var tab = new EditorTabViewModel(opened.Value, CloseAsync) { IsPreview = preview };
        Tabs.Add(tab);
        return GraftResult<EditorTabViewModel>.Ok(tab, opened.Issues);
    }

    /// <summary>未保存なら保存確認を挟んでタブを閉じる。falseはユーザーがキャンセルしたことを表す。</summary>
    public async Task<bool> CloseAsync(EditorTabViewModel tab)
    {
        if (tab.Session.IsModified)
        {
            var message = $"「{tab.Title}」には保存されていない変更があります。保存しますか?";
            var choice = await _dialogs.ConfirmThreeWayAsync("変更の保存", message, "保存", "破棄").ConfigureAwait(true);
            if (choice is null) return false; // キャンセル
            if (choice == true)
            {
                var saved = await tab.Session.SaveAsync().ConfigureAwait(true);
                if (!saved.IsSuccess) return false; // 保存に失敗した場合は閉じず編集を残す
            }
        }

        RemoveTab(tab);
        return true;
    }

    /// <summary>全タブを閉じる。途中でキャンセルされた場合はそこで打ち切りfalseを返す。</summary>
    public async Task<bool> CloseAllAsync()
    {
        foreach (var tab in Tabs.ToList())
        {
            if (!await CloseAsync(tab).ConfigureAwait(true)) return false;
        }

        return true;
    }

    /// <summary>指定パスのうち未保存の変更があるタブの絶対パス一覧を返す（4.8）。</summary>
    public IReadOnlyList<string> FindUnsaved(IEnumerable<string> fullPaths)
    {
        var set = new HashSet<string>(fullPaths, PathComparer);
        return Tabs.Where(t => t.Session.IsModified && set.Contains(t.Session.FullPath))
            .Select(t => t.Session.FullPath).ToList();
    }

    /// <summary>指定パスに該当する開いているタブを、確認なしで再読込する（4.8: 適用後の再読込）。</summary>
    public async Task ReloadIfOpenAsync(IEnumerable<string> fullPaths)
    {
        var set = new HashSet<string>(fullPaths, PathComparer);
        foreach (var tab in Tabs.Where(t => set.Contains(t.Session.FullPath)).ToList())
        {
            await tab.Session.ReloadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>エクスプローラでのリネーム・移動に追従し、該当タブのパス表示を更新する（4.2/4.8）。</summary>
    public void NotifyRenamed(string oldFullPath, string newFullPath)
    {
        var tab = Tabs.FirstOrDefault(t => PathsEqual(t.Session.FullPath, oldFullPath));
        if (tab is null) return;

        tab.Session.UpdatePath(newFullPath, _projectRoot ?? string.Empty);
        tab.NotifyPathChanged();
    }

    /// <summary>
    /// エクスプローラでの削除に追従し、該当タブを保存確認なしで閉じる（4.2/4.8）。
    /// 実体が既に無いファイルへの保存確認は意味を持たないため、<see cref="CloseAsync"/>とは
    /// 別に確認なしのタブ除去を行う。
    /// </summary>
    public Task NotifyDeletedAsync(string fullPath)
    {
        var tab = Tabs.FirstOrDefault(t => PathsEqual(t.Session.FullPath, fullPath));
        if (tab is not null) RemoveTab(tab);
        return Task.CompletedTask;
    }

    /// <summary>タブがアクティブ化されたことを記録し、MRU順の先頭へ移動する（Ctrl+Tab用）。</summary>
    public void Touch(EditorTabViewModel tab)
    {
        _mru.Remove(tab);
        _mru.Insert(0, tab);
    }

    /// <summary>
    /// Ctrl+Tabで切り替える先のタブ（直近2番目に使用したタブ）を返す。押すたびに直近2件を
    /// 往復する簡易実装。Ctrl長押しによる連続循環（モーダルなオーバーレイ表示）はE1の対象外。
    /// </summary>
    public EditorTabViewModel? NextByMru() => _mru.Count >= 2 ? _mru[1] : null;

    private void RemoveTab(EditorTabViewModel tab)
    {
        Tabs.Remove(tab);
        _mru.Remove(tab);
        tab.DetachEvents();
        tab.Session.Dispose();
    }

    // Windowsではファイルパスの大文字小文字を区別しない。テスト実行環境（Linux）では
    // Editor/ がテストプロジェクトに取り込まれないため、実行時のOSに合わせるだけでよい。
    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool PathsEqual(string a, string b) => PathComparer.Equals(a, b);
}
