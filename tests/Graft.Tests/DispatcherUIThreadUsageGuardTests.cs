using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 再発防止ガード（CI間欠失敗「毎回違うテスト名がPlatformNotSupportedExceptionで落ちる」の
/// 調査対応）。
///
/// <c>Avalonia.Threading.Dispatcher.UIThread</c>は遅延生成・スレッド非安全な静的プロパティで、
/// まだ誰も読んでいない状態で最初に読んだスレッドの<c>IDispatcherImpl</c>をキャッシュする。
/// Avalonia.Headlessのテストはテストごとに1回、セッションのディスパッチャスレッドが
/// <c>Dispatcher.ResetForUnitTests()</c>でこのキャッシュを空にしてからheadless用の実装を
/// 登録し直しており、この一瞬の窓の間にディスパッチャスレッド以外（<c>ConfigureAwait(false)</c>で
/// 移ったスレッドプール上のスレッドや、FileSystemWatcher・X11監視スレッド等）が
/// <c>Dispatcher.UIThread</c>を読むと、壊れたインスタンスがキャッシュされて以降
/// <c>PlatformNotSupportedException</c>が出るようになる（本タスクでの調査で確認した実際の
/// 原因。<see cref="Graft.Editor.DocumentSession"/>クラス冒頭のコメント参照）。
///
/// 正しい作法は、確実にUIスレッドで動くと分かっている場所（メソッド冒頭・コンストラクタ等、
/// まだ<c>ConfigureAwait(false)</c>で離脱する前）で一度だけ<c>Dispatcher.UIThread</c>を
/// ローカル変数／フィールドへ捕捉し、以降はそれ越しに使うこと（<see cref="Graft.Editor.
/// DocumentSession.OpenAsync"/>・<c>SaveAsync</c>・<c>ReloadAsync</c>が実例）。
///
/// このテストは、src/Graft配下で<c>Dispatcher.UIThread.</c>（＝捕捉せずに直接メンバーへ
/// アクセスしている箇所）を全件洗い出し、下の<see cref="AllowedDirectUsages"/>に無い新規の
/// 出現を失敗させる。新しく直接呼び出す箇所を追加する場合は、それが確実にUIスレッドから
/// しか呼ばれないことを確認したうえで、このテストの許可リストへ理由とともに追記すること。
/// バックグラウンドスレッドから呼ばれうる場所は、DocumentSessionと同じ「一度だけ捕捉」の
/// 形へ直すこと（許可リストに追記して回避してはならない）。
/// </summary>
public class DispatcherUIThreadUsageGuardTests
{
    /// <summary>
    /// (src/Graftからの相対パス, トリムした行内容) の組。すべてAvaloniaのUIイベント
    /// （コントロールのイベントハンドラ・アプリ起動直後の購読等）から、確実にUIスレッド上で
    /// しか呼ばれない箇所であることをコードレビューで確認済み。
    /// </summary>
    private static readonly HashSet<(string File, string Line)> AllowedDirectUsages = new()
    {
        // アプリ起動時（App.OnFrameworkInitializationCompleted、UIスレッド）に1度だけ購読する。
        ("App.axaml.cs", "Avalonia.Threading.Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;"),

        // 以下はいずれもAvaloniaのコントロール・イベントハンドラ（PropertyChanged講読やLoaded等）
        // から呼ばれており、Avaloniaのイベントディスパッチ自体がUIスレッド上でしか発生しないため
        // 安全（DocumentSessionのように「ConfigureAwait(false)で離脱した後」の箇所ではない）。
        ("Views/EditorPane.axaml.cs", "Dispatcher.UIThread.Post(() =>"),
        ("Views/TutorialOverlay.axaml.cs", "Dispatcher.UIThread.Post(() => PrimaryButton.Focus(), DispatcherPriority.Background);"),
        ("Views/ShellWindow.axaml.cs", "Dispatcher.UIThread.Post(() => SearchViewControl.QueryBoxElement.Focus(), DispatcherPriority.Background);"),
        ("Views/ShellWindow.axaml.cs", "Dispatcher.UIThread.Post(() => QuickOpenOverlayControl.QueryBoxElement.Focus(), DispatcherPriority.Background);"),
        ("Views/ShellWindow.axaml.cs", "Dispatcher.UIThread.Post(() => CommandPaletteOverlayControl.QueryBoxElement.Focus(), DispatcherPriority.Background);"),
        ("Views/SearchOverlay.axaml.cs", "Dispatcher.UIThread.Post(() =>"),
        // 検索ハイライト機能B（スクロールバー上のヒット位置目印）: SearchOverlay.Attachから、
        // タブ切替のたびにUIスレッド上のイベントハンドラ（EditorPane側のタブ切替処理）経由で
        // しか呼ばれない。ConfigureAwait(false)後の継続等、バックグラウンドスレッドから
        // 呼ばれる経路は無い。
        ("Views/SearchOverlay.axaml.cs", "Dispatcher.UIThread.Post(PushMarkerState);"),
        ("Views/EditorPane.TabStrip.cs", "Dispatcher.UIThread.Post(() =>"),
        ("Views/EditorPane.TabStrip.cs", "Dispatcher.UIThread.Post(() => TabPickerSearchBox.Focus(), DispatcherPriority.Background);"),
        // チュートリアル進行中のみ、UIスレッド上のawaitチェーンから呼ばれる（レイアウト反映待ち）。
        ("Views/ShellWindow.Tutorial.cs", "await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);"),
    };

    // Dispatcher.UIThreadを「捕捉」するだけの行（例: var ui = Dispatcher.UIThread;）は対象外。
    // 直後にメンバーアクセスの'.'が続く（＝キャッシュを毎回引き直して使っている）行だけを拾う。
    private static readonly Regex DirectUsagePattern = new(@"Dispatcher\.UIThread\.", RegexOptions.Compiled);

    [Fact(DisplayName = "src/Graft内でDispatcher.UIThreadを直接（未捕捉のまま）呼び出す箇所は、許可リストの範囲に収まっている")]
    public void Dispatcher_UIThreadの直接呼び出しは許可リストの範囲に収まる()
    {
        var srcGraftDir = FindSrcGraftDirectory();
        var offenders = new List<string>();
        var unusedAllowlistEntries = new HashSet<(string File, string Line)>(AllowedDirectUsages);

        foreach (var file in Directory.EnumerateFiles(srcGraftDir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(srcGraftDir, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (!DirectUsagePattern.IsMatch(trimmed)) continue;
                if (IsCommentLine(trimmed)) continue;

                var key = (relative, trimmed);
                if (AllowedDirectUsages.Contains(key))
                {
                    unusedAllowlistEntries.Remove(key);
                }
                else
                {
                    offenders.Add($"{relative}:{i + 1}: {trimmed}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "Dispatcher.UIThreadを未捕捉のまま直接呼び出す新しい箇所が見つかりました。" +
            "バックグラウンドスレッド（ConfigureAwait(false)後の継続、FileSystemWatcher・" +
            "監視スレッド等）から呼ばれうる場所ではないか確認してください。呼ばれうるなら、" +
            "確実にUIスレッドで動く場所で一度だけローカル変数／フィールドへ捕捉してから使う形へ" +
            "直してください（Graft.Editor.DocumentSession.OpenAsync/SaveAsync/ReloadAsyncが実例）。" +
            "確実にUIスレッドからしか呼ばれないと確認できた場合のみ、このテストの" +
            $"AllowedDirectUsagesへ理由とともに追記してください。\n{string.Join("\n", offenders)}");

        // 許可リスト側にもう存在しない行が残っていたら、リファクタ等で消えた可能性がある。
        // 掃除漏れに気付けるよう、これも失敗として報告する（安全側の検証なので厳しめでよい）。
        unusedAllowlistEntries.Should().BeEmpty(
            "許可リストにあるが実際のソースには見つからなかった行があります。" +
            "リファクタで消えた／文言が変わったなら、このテストの許可リストも合わせて更新してください。\n"
            + string.Join("\n", unusedAllowlistEntries.Select(e => $"{e.File}: {e.Line}")));
    }

    private static bool IsCommentLine(string trimmed)
        => trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal);

    /// <summary>テスト実行ディレクトリからGraft.slnを上へ辿って探し、"src/Graft"を返す。</summary>
    private static string FindSrcGraftDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Graft.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Graft.slnが見つからず、リポジトリルートを特定できませんでした。");
        }

        var srcGraft = Path.Combine(dir.FullName, "src", "Graft");
        if (!Directory.Exists(srcGraft))
        {
            throw new InvalidOperationException($"src/Graftが見つかりません: {srcGraft}");
        }

        return srcGraft;
    }
}
