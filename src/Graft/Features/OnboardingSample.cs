using System.IO;
using System.Text;

namespace Graft.Features;

/// <summary>
/// 画面上のチュートリアル（<see cref="Graft.Views.ShellWindow"/>のコーチマーク。ShellWindow.
/// Tutorial.cs参照）用に、サンプルプロジェクト（1ファイル）とサンプルパッチを一時フォルダへ
/// 生成する。実データを一切触らずに「登録→解析→差分確認→適用→履歴確認→復元」の流れを
/// 1回体験できるようにするのが目的。初回起動ガイド（<see cref="Graft.Views.OnboardingWindow"/>）
/// の最終画面「使い方を学ぶ」から開始したときも、このクラスが同じ生成処理を担う。
///
/// <see cref="Path.GetTempPath"/>配下（Windowsなら<c>%TEMP%</c>、Linuxなら通常<c>/tmp</c>）にのみ
/// 生成し、利用者のドキュメント等は一切汚さない。<c>tools/New-RevisionTestSample.ps1</c>と似た
/// 構成（適用対象ファイル＋Graft形式のパッチ本文）を参考にしているが、PowerShellには依存せず
/// C#で完結させている（Linuxでも動く必要があるため）。
///
/// SEARCH/REPLACEの対象行はファイル本文とパッチ本文の両方で同じ文字列定数を使い回すことで、
/// 「サンプルなのに一致しない」という事故を防いでいる（マッチングは完全一致が前提のため）。
/// 実際に一致してドライラン・適用まで進むことは<c>OnboardingSampleTests</c>
/// （Graft.Tests、<see cref="Graft.Core.ApplyEngine"/>を通した統合テスト）で確認している。
/// </summary>
public static class OnboardingSample
{
    /// <summary>生成するサンプルファイル名（プロジェクトルート直下）。</summary>
    public const string SampleFileName = "greeting.py";

    // ファイル本文・パッチ本文の両方から参照する「書き換え前後」の行。
    private const string BeforeLine1 = "    # TODO: ここでnameを使ったあいさつ文を組み立てて return してください";
    private const string BeforeLine2 = "    return None";
    private const string AfterLine1 = "    # nameを使ってあいさつ文を組み立てて返す";
    private const string AfterLine2 = "    return f\"こんにちは、{name}さん！Graftへようこそ。\"";

    /// <summary>生成結果。</summary>
    /// <param name="ProjectRoot">生成したサンプルプロジェクトのルート（一時フォルダ配下）。</param>
    /// <param name="PatchText">クリップボードへコピーする、Graft形式のサンプルパッチ本文。</param>
    public sealed record Sample(string ProjectRoot, string PatchText);

    /// <summary>
    /// 一時フォルダへサンプル一式（プロジェクト1件・ファイル1件）を新規生成する。呼び出しのたびに
    /// 新しいフォルダ（GUIDベース）を使うため、複数回試しても前回の適用結果とぶつからない。
    /// </summary>
    public static Sample Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "Graft-サンプル-" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        var fileContent = string.Join("\n", new[]
        {
            "# Graftのサンプルファイルです。自由に書き換えて構いません。",
            "# 「あいさつを表示する」関数の中身が、まだ未実装のままになっています。",
            "",
            "def greet(name):",
            "    \"\"\"名前を受け取ってあいさつ文を返す（未実装）。\"\"\"",
            BeforeLine1,
            BeforeLine2,
            "",
            "",
            "if __name__ == \"__main__\":",
            "    print(greet(\"Graft\"))",
            "",
        });
        // BOM無しUTF-8で書き出す（FileTextIO・SafeFileWriterが既定で扱う形式に合わせる）。
        File.WriteAllText(Path.Combine(root, SampleFileName), fileContent, new UTF8Encoding(false));

        var patchText =
            "<<<< PATCH\n" +
            "summary: サンプル: greet関数であいさつ文を組み立てて返すようにする\n" +
            "type: feat\n" +
            ">>>>\n" +
            "\n" +
            $"<<<< FILE: {SampleFileName}\n" +
            "<<<<<<< SEARCH\n" +
            $"{BeforeLine1}\n" +
            $"{BeforeLine2}\n" +
            "=======\n" +
            $"{AfterLine1}\n" +
            $"{AfterLine2}\n" +
            ">>>>>>> REPLACE\n";

        return new Sample(root, patchText);
    }

    /// <summary>
    /// 生成した一時フォルダを丸ごと削除する。チュートリアル終了時の後片付け
    /// （<see cref="Graft.Views.ShellWindow"/>のチュートリアル制御。詳細はShellWindow.Tutorial.cs
    /// のクラスコメント参照）から呼ぶ。存在しない・削除に失敗した場合は何もしない（あくまで
    /// 体験用の後片付けであり、失敗しても実害は無い。一時フォルダのため、削除しなくてもOSが
    /// 最終的に掃除する）。
    /// </summary>
    public static void Cleanup(string projectRoot)
    {
        try
        {
            if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 画面上のチュートリアル専用: <see cref="Sample.PatchText"/>を一時ファイルへ書き出す。
    /// クリップボード経由（<see cref="Graft.ViewModels.MainViewModel.PasteAndParseCommand"/>）ではなく
    /// ファイルからの解析経路（4.1章、<see cref="Graft.ViewModels.MainViewModel.LoadPatchFromFileAsync"/>）
    /// へそのまま渡すために使う。こうすることで、チュートリアル実行中も利用者のクリップボードの
    /// 中身（実データ）には一切触れずに済む。
    /// </summary>
    public static string WritePatchFile(Sample sample)
    {
        var path = Path.Combine(
            Path.GetTempPath(), "Graft-サンプルパッチ-" + System.Guid.NewGuid().ToString("N")[..8] + ".patch");
        File.WriteAllText(path, sample.PatchText, new UTF8Encoding(false));
        return path;
    }

    /// <summary><see cref="WritePatchFile"/>で生成した一時ファイルを削除する。<see cref="Cleanup"/>と同じ流儀で失敗は無視する。</summary>
    public static void CleanupPatchFile(string patchFilePath)
    {
        try
        {
            if (File.Exists(patchFilePath)) File.Delete(patchFilePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
