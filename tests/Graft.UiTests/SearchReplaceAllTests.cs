using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 課題1（重要）の回帰テスト。<see cref="CrossFileSearchEngine"/>は暴走防止のため全体の
/// ヒット上限（既定<see cref="CrossFileSearchOptions.MaxTotalHits"/>=5000）に達すると検索結果の
/// 表示を打ち切るが、以前の<see cref="SearchViewModel.ReplaceAllAsync"/>は画面に表示された
/// <see cref="SearchViewModel.Groups"/>だけを置換対象にしていたため、上限より後のファイルが
/// 一切走査されず置換から漏れていた（「すべて置換」の名前に反してコードベースが中途半端な
/// 状態になる）。ここでは実際にファイルの中身を読み、打ち切りが起きた状態で「すべて置換」を
/// 実行しても取りこぼしが無いことを検証する（画面の表示件数だけでは判断しない）。
/// </summary>
public class SearchReplaceAllTests
{
    /// <summary>非同期コマンドを実行し、完了するまで待つ（tests/Graft.UiTests/ScenarioTests.csと同じ手法）。</summary>
    private static async Task ExecuteAsync(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        if (command is AsyncRelayCommand async)
        {
            while (async.IsExecuting)
            {
                await Task.Delay(10).ConfigureAwait(true);
            }
        }
    }

    /// <summary>常に「はい」で応答しつつ、表示された確認メッセージを記録するダイアログ。
    /// 課題1の確認ダイアログ文言（件数・漏れの有無）と課題2（全角？）の検証に使う。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public List<string> ConfirmMessages { get; } = new();

        public Task<bool> ConfirmAsync(string title, string message)
        {
            ConfirmMessages.Add(message);
            return Task.FromResult(true);
        }

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(true);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    [AvaloniaFact(DisplayName = "全体のヒット上限で打ち切られていても「すべて置換」で全ファイルが漏れなく置換される")]
    public async Task 全体上限で打ち切られていてもすべて置換で漏れが出ない()
    {
        // MaxTotalHits(既定5000)を超えさせるため、300ファイル×20件=6000件を用意する。
        // 1ファイルあたりは20件でMaxHitsPerFile(既定500)を大きく下回るため、この打ち切りは
        // 「全体上限」だけが原因になる（1ファイル上限の影響を混在させない）。
        const int FileCount = 300;
        const int HitsPerFile = 20;

        var root = Path.Combine(Path.GetTempPath(), "graft-replaceall-total", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            for (var i = 0; i < FileCount; i++)
            {
                var content = string.Concat(Enumerable.Repeat("TargetWord\n", HitsPerFile));
                File.WriteAllText(Path.Combine(root, $"f{i:D4}.txt"), content);
            }

            var dialogs = new RecordingDialogService();
            var vm = new SearchViewModel(new CrossFileSearchEngine(), dialogs)
            {
                Query = "TargetWord",
                ReplaceText = "REPLACED",
            };
            vm.SetContext(new Project { Id = "p", Name = "p", Root = root }, new Settings());

            var view = new SearchView { DataContext = vm };
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();

            await ExecuteAsync(vm.SearchCommand).ConfigureAwait(true);

            // 前提の確認: 実際に全体上限で打ち切られ、画面には全ファイル(300)より少ない件数しか
            // 表示されていないこと。これが成立していないと、この先の検証が「元の不具合」を
            // 再現できていないことになる。
            vm.Groups.Count.Should().BeLessThan(FileCount, "全体のヒット上限に達し、表示は全ファイル数より少なくなっているはず");
            vm.TruncatedMessage.Should().Contain("上限", "打ち切りが起きたことが利用者に伝わる必要がある");

            await ExecuteAsync(vm.ReplaceAllCommand).ConfigureAwait(true);

            // 画面の表示件数だけで判断せず、実際に全ファイルの中身を読んで漏れが無いことを確認する。
            for (var i = 0; i < FileCount; i++)
            {
                var text = File.ReadAllText(Path.Combine(root, $"f{i:D4}.txt"));
                text.Should().NotContain("TargetWord", $"f{i:D4}.txt が置換対象から漏れてはならない");
                text.Should().Contain("REPLACED");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact(DisplayName = "全体上限で打ち切られた場合、確認ダイアログには数え直した正確な件数が表示される")]
    public async Task 全体上限で打ち切られた場合の確認文言が正確()
    {
        const int FileCount = 300;
        const int HitsPerFile = 20;

        var root = Path.Combine(Path.GetTempPath(), "graft-replaceall-message", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            for (var i = 0; i < FileCount; i++)
            {
                var content = string.Concat(Enumerable.Repeat("TargetWord\n", HitsPerFile));
                File.WriteAllText(Path.Combine(root, $"f{i:D4}.txt"), content);
            }

            var dialogs = new RecordingDialogService();
            var vm = new SearchViewModel(new CrossFileSearchEngine(), dialogs)
            {
                Query = "TargetWord",
                ReplaceText = "REPLACED",
            };
            vm.SetContext(new Project { Id = "p", Name = "p", Root = root }, new Settings());

            var view = new SearchView { DataContext = vm };
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();

            await ExecuteAsync(vm.SearchCommand).ConfigureAwait(true);
            var displayedFileCount = vm.Groups.Count;

            await ExecuteAsync(vm.ReplaceAllCommand).ConfigureAwait(true);

            dialogs.ConfirmMessages.Should().ContainSingle();
            var message = dialogs.ConfirmMessages[0];

            // 打ち切りで表示されなかった分も含め、数え直した実際の対象ファイル数(300)が
            // 文言に含まれること。画面上の表示件数(打ち切られた少ない方)をそのまま
            // 使い回していないことを確認する。
            message.Should().Contain($"{FileCount} ファイル", "打ち切り後に数え直した実際のファイル数を伝える必要がある");
            message.Should().NotContain($"{displayedFileCount} ファイルの", "画面の打ち切り後件数をそのまま置換件数として案内してはならない");

            // 課題2: 半角「?」ではなく全角「？」で終わる。
            message.TrimEnd().Should().EndWith("？");
            message.Should().NotContain("?");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact(DisplayName = "1ファイルあたりの上限に達しても、そのファイル内の一致はすべて置換される")]
    public async Task ファイル単位上限に達してもファイル内は全件置換される()
    {
        // MaxHitsPerFile(既定500)を超えるが、MaxTotalHits(既定5000)は超えない1ファイルを用意する。
        // 全体上限には引っかからないため画面には全ファイルが表示されるが、そのファイルの
        // Hits一覧は500件で打ち切られる。ReplaceInFilesAsyncはファイル単位で正規表現の
        // 全置換を行うため、600件すべてが置換されるはず。
        const int HitCount = 600;

        var root = Path.Combine(Path.GetTempPath(), "graft-replaceall-perfile", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var content = string.Concat(Enumerable.Repeat("NeedleX\n", HitCount));
            var targetPath = Path.Combine(root, "many.txt");
            File.WriteAllText(targetPath, content);

            var dialogs = new RecordingDialogService();
            var vm = new SearchViewModel(new CrossFileSearchEngine(), dialogs)
            {
                Query = "NeedleX",
                ReplaceText = "REPLACED",
            };
            vm.SetContext(new Project { Id = "p", Name = "p", Root = root }, new Settings());

            var view = new SearchView { DataContext = vm };
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();

            await ExecuteAsync(vm.SearchCommand).ConfigureAwait(true);

            vm.Groups.Should().ContainSingle();
            vm.Groups[0].Hits.Count.Should().Be(500, "1ファイルあたりの上限で表示は500件に打ち切られているはず");
            vm.TruncatedMessage.Should().Contain("1ファイルあたり");

            await ExecuteAsync(vm.ReplaceAllCommand).ConfigureAwait(true);

            dialogs.ConfirmMessages.Should().ContainSingle();
            dialogs.ConfirmMessages[0].Should().Contain("以上", "表示件数(500件)より実際の置換件数が多くなりうることを伝える必要がある");

            var text = File.ReadAllText(targetPath);
            text.Should().NotContain("NeedleX", "1ファイルあたりの表示上限を超えた分も置換されている必要がある");
            System.Text.RegularExpressions.Regex.Matches(text, "REPLACED").Count.Should().Be(HitCount);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [AvaloniaFact(DisplayName = "打ち切りが無い通常の「すべて置換」では確認ダイアログが全角？で終わる")]
    public async Task 打ち切りが無い場合の確認文言が全角疑問符で終わる()
    {
        var root = Path.Combine(Path.GetTempPath(), "graft-replaceall-normal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "needle needle\n");

            var dialogs = new RecordingDialogService();
            var vm = new SearchViewModel(new CrossFileSearchEngine(), dialogs)
            {
                Query = "needle",
                ReplaceText = "REPLACED",
            };
            vm.SetContext(new Project { Id = "p", Name = "p", Root = root }, new Settings());

            var view = new SearchView { DataContext = vm };
            var window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();

            await ExecuteAsync(vm.SearchCommand).ConfigureAwait(true);
            await ExecuteAsync(vm.ReplaceAllCommand).ConfigureAwait(true);

            dialogs.ConfirmMessages.Should().ContainSingle();
            var message = dialogs.ConfirmMessages[0];
            message.Should().Be("1 ファイルの 2 件を「REPLACED」に置換します。よろしいですか？");

            File.ReadAllText(Path.Combine(root, "a.txt")).Should().Be("REPLACED REPLACED\n");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
