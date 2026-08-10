using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Features;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 実機フリーズ調査（利用者報告「エクスプローラで何個もコードを開くと固まる」）の回帰テスト。
///
/// <see cref="GitGutterProvider"/>はタブを開くたびに<see cref="GitIntegration"/>経由でgitを
/// 子プロセスとして起動する。調査の結果、次の2点が判明した。
/// 1. 従来は起動したgitプロセスにタイムアウトが一切無く、呼び出し元がキャンセルしても
///    （<c>CancellationTokenSource.Cancel()</c>）、それは.NET側の「待つのをやめる」だけで、
///    既に起動済みのOSプロセス自体は<c>Kill</c>されないまま生き続けていた。ハングした
///    gitプロセス（企業のWindows環境ではgitのラッパーがライセンスサーバ等への通信で
///    ハングする事例が知られている）は回収されずに積み上がり得る。
/// 2. <c>Process.Start</c>自体はC#の非同期メソッドの仕様により、最初のawaitに到達するまで
///    呼び出し元のスレッド（多くの場合UIスレッド）上で同期的に実行される。Windows実機では
///    ウイルス対策ソフトの新規プロセス作成フックにより<c>CreateProcess</c>自体が数秒〜
///    数十秒ブロックすることが知られており、これがUIスレッド上で起きると
///    アプリ全体が完全に無応答になる。
///
/// このテストは(1)を検証する: タイムアウトを超えて応答しない子プロセス（ハングを模した
/// シェルスクリプト）が、設定したタイムアウトで確実に打ち切られ（待ち時間が青天井にならない）、
/// かつ実際にOSプロセスとして後始末（Kill）されること（プロセスが積み上がらないこと）を、
/// 実際の子プロセスで確認する。
///
/// (2)（UIスレッドを塞がないこと）は、Windows実機のウイルス対策ソフトによる<c>CreateProcess</c>
/// フックというOS・環境固有の事情に起因するため、Linux上のヘッドレス環境では
/// 決定的に再現・検証する手段が無い（<c>Process.Start</c>自体の所要時間はLinuxでは常に
/// 数ms程度で、遅延を模擬してもそれは子プロセスの実行時間が延びるだけであり、親プロセス側の
/// <c>Process.Start</c>の戻りの早さには影響しない）。この点はコード（<see cref="GitIntegration"/>の
/// <c>RunGitAsync</c>が<c>Task.Run</c>で全体をスレッドプールへ逃がしている）で担保し、
/// Windows実機での確認が必要な旨をコメントに残すに留める。
///
/// テストの入口は<see cref="GitIntegration.RunForTestAsync"/>（<c>internal</c>、
/// <c>BuildProcessStartInfo</c>と同じ「テストから直接検証できるようにする」方針）を使う。
/// 実際に起動するのは"git"という名前の実行可能ファイルのため、テスト用の一時ディレクトリを
/// PATHの先頭へ加えて偽の（ハングする）"git"へ差し替える。PATH環境変数はプロセス全体で共有される
/// 状態のため、実gitへ実際にコマンドを投げる<see cref="GitIntegrationTests"/>と同時に実行される
/// と汚染し合う恐れがあり、<see cref="GitProcessCollection"/>で同一コレクションに束ね、
/// xUnitが両者を並行実行しないようにしている。
/// </summary>
[Collection(GitProcessCollection.Name)]
public class GitProcessTimeoutTests
{
    [Fact(DisplayName = "ハングしたgitプロセスはタイムアウトで打ち切られ、待ち時間が青天井にならない")]
    public async Task ハングしたgitプロセスはタイムアウトで打ち切られる()
    {
        using var ws = new TempWorkspace();
        using var fakeGit = new FakeHangingGit();

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fakeGit.PrependToPath(originalPath));
        try
        {
            var timeout = TimeSpan.FromMilliseconds(300);
            var sw = Stopwatch.StartNew();
            var (started, exitCode, output) = await GitIntegration
                .RunForTestAsync(ws.RootPath, new[] { "rev-parse", "--is-inside-work-tree" }, timeout)
                .ConfigureAwait(true);
            sw.Stop();

            // 偽のgit（30秒スリープ）が実際に起動されたことを確認したうえで、タイムアウト
            // （300ms）にほぼ即した時間で打ち切られること（30秒待たされないこと）を確認する。
            // 共有ランナーの遅さを考慮し、タイムアウトの十分な倍数（10倍＝3秒）を上限とする。
            fakeGit.WasStarted.Should().BeTrue("偽のgitが実際に子プロセスとして起動している必要がある");
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3),
                $"タイムアウト（{timeout.TotalMilliseconds}ms）を大きく超えて（実測{sw.ElapsedMilliseconds}ms）"
                + "待たされている。ハングしたgitプロセスがUIスレッドの応答性を奪い続ける不具合の再発");

            started.Should().BeFalse("タイムアウトした呼び出しは失敗として扱われるはず");
            exitCode.Should().Be(-1);
            output.Should().Contain("タイムアウト");

            // 打ち切り後、偽のgitプロセス自体（子プロセス）も後始末（Kill）されていること。
            // 待たずに済ませたが実プロセスは裏で生き続けている、という状態では
            // 「多数のファイルを開くとgitプロセスが積み上がる」問題の再発になる。
            (await fakeGit.WaitUntilProcessExitedAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true))
                .Should().BeTrue("タイムアウト後、ハングしたgitプロセスはKillされ後始末されているはず"
                    + "（後始末されない場合、多数のファイルを開くとgitプロセスが積み上がる）");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact(DisplayName = "正常に完了するgitはタイムアウトの影響を受けず、通常どおり結果を返す")]
    public async Task 正常なgitはタイムアウトの影響を受けない()
    {
        using var ws = new TempWorkspace();

        // 実gitに対し、タイムアウトを十分大きく取った状態での通常経路の健全性を確認する
        // （タイムアウト機構の追加が正常系を壊していないことの確認）。
        var (started, exitCode, _) = await GitIntegration
            .RunForTestAsync(ws.RootPath, new[] { "rev-parse", "--is-inside-work-tree" }, TimeSpan.FromSeconds(10))
            .ConfigureAwait(true);

        started.Should().BeTrue("実gitは正常に起動・終了するはず");
        exitCode.Should().NotBe(-1);
        // ws.RootPathはgit initしていないため、"not a git repository"で非0終了するのが正しい。
        exitCode.Should().NotBe(0, "git initしていないディレクトリはリポジトリ外と判定されるはず");
    }

    /// <summary>
    /// "git"という名前で、起動されたことの検知・PIDの記録・応答しない（ハングする）挙動だけを行う
    /// 実行可能なシェルスクリプトを一時ディレクトリへ用意する。実gitへは一切委譲しない
    /// （このテストは「ハングしたら打ち切って後始末する」ことだけを検証すればよく、実際の
    /// git出力の中身は不要なため）。
    /// </summary>
    private sealed class FakeHangingGit : IDisposable
    {
        private readonly string _binDir;
        private readonly string _pidFilePath;

        public FakeHangingGit()
        {
            _binDir = Path.Combine(Path.GetTempPath(), "graft-fake-git-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_binDir);
            _pidFilePath = Path.Combine(_binDir, "git.pid");

            var scriptPath = Path.Combine(_binDir, "git");
            File.WriteAllText(scriptPath,
                "#!/bin/sh\necho $$ > \"" + _pidFilePath + "\"\nexec sleep 30\n");
            MakeExecutable(scriptPath);
        }

        /// <summary>既存のPATHの先頭へこのディレクトリを加えた新しいPATH文字列を返す。</summary>
        public string PrependToPath(string? originalPath)
            => _binDir + Path.PathSeparator + originalPath;

        /// <summary>偽のgitスクリプトが実際に起動され、PIDを記録できたかどうか。</summary>
        public bool WasStarted => File.Exists(_pidFilePath);

        /// <summary>
        /// 記録済みのPIDのプロセスが、指定時間内に本当に終了（Killされて後始末）されたかどうかを
        /// ポーリングして確認する。
        /// </summary>
        public async Task<bool> WaitUntilProcessExitedAsync(TimeSpan timeout)
        {
            if (!File.Exists(_pidFilePath)) return false;
            var pidText = (await File.ReadAllTextAsync(_pidFilePath).ConfigureAwait(false)).Trim();
            if (!int.TryParse(pidText, out var pid)) return false;

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!IsProcessAlive(pid)) return true;
                await Task.Delay(50).ConfigureAwait(false);
            }
            return !IsProcessAlive(pid);
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false; // 既に存在しない（終了済み）。
            }
        }

        private static void MakeExecutable(string path)
        {
            if (OperatingSystem.IsWindows()) return; // Windowsでは拡張子解決のため本テスト自体が対象外。
            using var chmod = Process.Start(new ProcessStartInfo("chmod", $"+x \"{path}\"")
            {
                UseShellExecute = false,
            });
            chmod?.WaitForExit();
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_binDir)) Directory.Delete(_binDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

/// <summary>
/// PATH環境変数（プロセス全体の共有状態）を書き換えるテストと、実gitへ実際にコマンドを送る
/// テストが並行実行されて汚染し合わないよう、xUnitのコレクションで束ねて直列化する
/// （同一コレクションに属するテストクラス同士は、xUnitの既定の挙動として並行実行されない）。
/// </summary>
[CollectionDefinition(Name)]
public sealed class GitProcessCollection
{
    public const string Name = "GitProcess (PATH環境変数を書き換えるため直列実行)";
}
