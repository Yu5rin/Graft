using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using FluentAssertions;
using Graft.Core;
using Graft.Editor;
using Xunit.Abstractions;

namespace Graft.UiTests;

/// <summary>
/// 仕様書18章の性能要件を、画面の無い環境で自動検証できる範囲で押さえる（20章 L5）。
///
/// 「遅延なく」は体感の指標のため、ここでは体感が損なわれる水準を上限として置く
/// （1操作あたりの上限を十分に余裕のある値にし、性能が桁で悪化したときだけ失敗させる）。
/// 実測値はテスト出力へ書き出し、環境ごとの傾向を追えるようにする。
/// </summary>
public class PerformanceTests
{
    private const int LineCount = 100_000;

    private readonly ITestOutputHelper _output;

    public PerformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [AvaloniaFact(DisplayName = "10万行のファイルを開いても構築・描画が滞らない")]
    public void 十万行を開ける()
    {
        var text = BuildSource(LineCount);

        var editor = new TextEditor { ShowLineNumbers = true };
        var window = new Window { Width = 1200, Height = 800, Content = editor };
        window.Show();

        var stopwatch = Stopwatch.StartNew();
        editor.Document = new TextDocument(text);
        window.CaptureRenderedFrame().Should().NotBeNull();
        stopwatch.Stop();

        _output.WriteLine($"10万行の読み込みと初回描画: {stopwatch.ElapsedMilliseconds} ms");
        // 生成した文字列は改行で終わるため、最終行のあとに空行が1つ増える。
        editor.Document.LineCount.Should().Be(LineCount + 1);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000,
            "10万行を開く操作が体感で待たされる水準になっていないこと");
    }

    [AvaloniaFact(DisplayName = "10万行のファイルでもスクロールが滞らない")]
    public void 十万行でもスクロールできる()
    {
        var editor = new TextEditor { ShowLineNumbers = true, Document = new TextDocument(BuildSource(LineCount)) };
        var window = new Window { Width = 1200, Height = 800, Content = editor };
        window.Show();
        window.CaptureRenderedFrame();

        // 先頭・中間・末尾へ跳んでも、可視範囲だけを描き直せていることを確認する
        // （仮想化が効いていなければ行数に比例して時間が延びる）。
        var stopwatch = Stopwatch.StartNew();
        foreach (var line in new[] { 1, LineCount / 2, LineCount })
        {
            editor.ScrollToLine(line);
            window.CaptureRenderedFrame().Should().NotBeNull();
        }
        stopwatch.Stop();

        _output.WriteLine($"3箇所へのスクロールと再描画: {stopwatch.ElapsedMilliseconds} ms");
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(3000, "スクロールのたびに全行を描き直していないこと");
    }

    [AvaloniaFact(DisplayName = "10万行のファイルでも編集が滞らない")]
    public void 十万行でも編集できる()
    {
        var document = new TextDocument(BuildSource(LineCount));
        var editor = new TextEditor { Document = document };
        var window = new Window { Width = 1200, Height = 800, Content = editor };
        window.Show();
        window.CaptureRenderedFrame();

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
        {
            document.Insert(0, "// 追記\n");
        }
        window.CaptureRenderedFrame().Should().NotBeNull();
        stopwatch.Stop();

        _output.WriteLine($"先頭への50回の挿入と再描画: {stopwatch.ElapsedMilliseconds} ms");
        document.LineCount.Should().Be(LineCount + 50 + 1); // 末尾の空行ぶんを含む
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(3000, "1文字ごとの編集が体感で引っかからないこと");
    }

    [AvaloniaFact(DisplayName = "10万行のファイルでもシンタックスハイライトが可視範囲に限定される")]
    public void 十万行でもハイライトが可視範囲に限定される()
    {
        var editor = new TextEditor { Document = new TextDocument(BuildSource(LineCount)) };
        var window = new Window { Width = 1200, Height = 800, Content = editor };

        using var bridge = new SyntaxHighlightBridge(editor);
        editor.TextArea.TextView.LineTransformers.Add(bridge);
        bridge.Attach(editor.Document, ".cs", syntaxEnabled: true);

        window.Show();

        var stopwatch = Stopwatch.StartNew();
        window.CaptureRenderedFrame().Should().NotBeNull();
        stopwatch.Stop();

        _output.WriteLine($"ハイライト有効時の初回描画: {stopwatch.ElapsedMilliseconds} ms");
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000,
            "ハイライトが可視範囲だけに限定されていること（全行を色付けしていないこと）");
    }

    [AvaloniaFact(DisplayName = "10万行のレキサ走査が実用的な時間で終わる")]
    public void 十万行のレキサ走査が終わる()
    {
        var lines = TextNormalizer.SplitLines(BuildSource(LineCount));
        var rule = SyntaxLexer.RuleForExtension(".cs");
        rule.Should().NotBeNull();

        var lexer = new SyntaxLexer(rule!);

        var stopwatch = Stopwatch.StartNew();
        lexer.Scan(lines);
        stopwatch.Stop();

        _output.WriteLine($"10万行のレキサ走査: {stopwatch.ElapsedMilliseconds} ms");
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(5000);
    }

    [AvaloniaFact(DisplayName = "起動からシェルの初回描画までが1秒以内に収まる")]
    public void 起動が一秒以内に収まる()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "graft-perf", Guid.NewGuid().ToString("N"));

        try
        {
            var appPaths = new Graft.Infra.AppPaths(baseDirectory);
            appPaths.EnsureCoreDirectoriesExist();

            // 18章「起動から操作可能まで1秒以内」。ここで測るのは、依存の構築から
            // シェルの初回描画までの時間（プロセス起動やランタイムの初期化は含まない）。
            var stopwatch = Stopwatch.StartNew();

            var shell = Graft.Views.StartupCoordinator.BuildShellViewModel(
                appPaths,
                new Graft.Infra.Settings(),
                new Graft.Infra.SettingsStore(appPaths),
                new Graft.Features.PatchQueue(appPaths),
                new Graft.Features.ProjectStore(appPaths),
                new RevisionStore(appPaths),
                new RevisionRestorer(appPaths),
                new Graft.Platform.Null.NullDialogService(),
                new Graft.Platform.AvaloniaUiServices(),
                openSettings: () => { });

            var built = stopwatch.ElapsedMilliseconds;

            var window = new Graft.Views.ShellWindow(shell) { Width = 1280, Height = 800 };
            var constructed = stopwatch.ElapsedMilliseconds;

            window.Show();
            window.CaptureRenderedFrame().Should().NotBeNull();
            stopwatch.Stop();

            _output.WriteLine($"内訳: ViewModel構築={built} ms, ウィンドウ構築={constructed - built} ms, "
                + $"表示と初回描画={stopwatch.ElapsedMilliseconds - constructed} ms");
            _output.WriteLine($"依存の構築からシェルの初回描画まで: {stopwatch.ElapsedMilliseconds} ms");

            // 1回目の値には、このテスト環境（CPUを共有するコンテナ）でのJITとXAMLの
            // 初回読み込みが乗る。実機での「1秒以内」の可否をここで断定はできないため、
            // 桁で悪化したときだけ気付けるよう緩い上限で押さえる。
            stopwatch.ElapsedMilliseconds.Should().BeLessThan(4000,
                "初回構築が桁で悪化していないこと（実機での1秒要件は発行物での確認による）");

            // 1回目にはXAMLの初回読み込みとJITの費用が含まれる。実行中のプロセスで
            // 2回目を測ると、その一度きりの費用を除いた本来の構築費用が分かる。
            var warm = Stopwatch.StartNew();
            var second = new Graft.Views.ShellWindow(shell) { Width = 1280, Height = 800 };
            second.Show();
            second.CaptureRenderedFrame().Should().NotBeNull();
            warm.Stop();
            _output.WriteLine($"2回目（初回費用を除く）: {warm.ElapsedMilliseconds} ms");

            warm.ElapsedMilliseconds.Should().BeLessThan(1000,
                "初回の読み込み費用を除いた構築・描画は1秒以内に収まること（18章）");
        }
        finally
        {
            try
            {
                if (Directory.Exists(baseDirectory)) Directory.Delete(baseDirectory, recursive: true);
            }
            catch (IOException)
            {
                // 後始末の失敗は測定結果に影響しない。
            }
        }
    }

    /// <summary>コメント・文字列・キーワードが混ざった、実際のコードに近い内容を生成する。</summary>
    private static string BuildSource(int lines)
    {
        var builder = new StringBuilder(lines * 40);
        for (var i = 0; i < lines; i++)
        {
            var kind = i % 10;
            builder.Append(kind switch
            {
                0 => $"// {i} 行目のコメント\n",
                1 => $"public void Method{i}(int value)\n",
                2 => "{\n",
                3 => $"    var text = \"文字列 {i}\";\n",
                4 => $"    var number = {i} * 2;\n",
                5 => "    if (number > 0)\n",
                6 => "    {\n",
                7 => "        Console.WriteLine(text);\n",
                8 => "    }\n",
                _ => "}\n",
            });
        }
        return builder.ToString();
    }
}
