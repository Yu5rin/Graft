namespace Graft.Core;

/// <summary>
/// Markdownプレビュー機能の性能ガード（利用者指示の追加要件6）。
///
/// 【なぜ必要か】
/// MarkdownをAvalonia標準コントロール（<see cref="Graft.Views.ManualMarkdownRenderer"/>）へ
/// 丸ごと展開する方式は、見出し・段落・表・コードブロックの1個1個が独立したUI要素
/// （<c>SelectableTextBlock</c>・<c>Border</c>・<c>Grid</c>等）になるため、巨大なファイルでは
/// 「行数に比例したUI要素をまとめて生成する」コストがそのまま重くなる
/// （AvaloniaEditの仮想化＝可視行のみ処理、とは根本的に方式が異なる）。
///
/// 【判定方法】
/// パース前に分かる安価な指標（文字数・行数）だけで判定する。実際にMarkdownをパースして
/// ブロック数を数えるには結局パースが必要になり、「重い処理を避けるための判定」自体が
/// 重くなってしまうため、パースを経ない指標にしている。
///
/// 【しきい値の根拠（実測）】
/// <see cref="Graft.Views.ManualMarkdownRenderer.Render"/>を直接呼び、見出し＋段落を交互に
/// 並べたMarkdown（1ペアで2行・2ブロック）のレンダリング時間をヘッドレス環境で実測した
/// （ウォームアップ1回の後に計測。UIツリーへの追加・レイアウトは含まない、パース＋
/// コントロール構築のみの時間）。
///
/// <code>
/// ブロック数    文字数      経過時間
///   2,000       68,780件    125.0ms
///   4,000      139,780件    283.2ms
///  10,000      352,780件    414.0ms
///  20,000      707,780件    701.1ms
///  40,000    1,437,780件  1,472.5ms
/// </code>
///
/// おおむね文字数に比例し、1,000文字あたり約1ms（707,780文字で701ms）で線形に収まっている
/// （2乗的な劣化は見られない）。ただし実際の画面表示ではこの後にAvalonia側のレイアウト・
/// 初回描画のコストが追加で乗るため、「プレビューに切り替えた瞬間にUIが固まる」体感を避ける
/// ぶんの余裕を見て、実測値から離れた小さめの値をしきい値とした。
///
/// 具体的なしきい値:
/// - 文字数: <see cref="CharLimit"/>（15万文字）。実測の比例関係から見積もると純粋な構築時間で
///   約150ms程度で収まる規模。
/// - 行数: <see cref="LineLimit"/>（8,000行）。1行あたり平均文字数が短い（表・箇条書き中心）
///   ファイルは文字数のしきい値だけでは検出できないため、ブロック数のおおまかな上限として
///   行数も別途チェックする（1ブロックが複数行にまたがる場合もあるため、実際のブロック数は
///   これより少なくなるのが普通で、安全側の上限として機能する）。10,000行で414msという実測
///   データに対し、余裕を持たせて8,000行を上限とした。
///
/// 上記の実測を再現する回帰テスト（線形性が崩れていないことの確認）は
/// tests/Graft.UiTests/MarkdownPreviewPerformanceTests.cs にある。
///
/// どちらか一方でも超えたら、プレビューを諦めて編集モードで開く
/// （<see cref="Graft.ViewModels.EditorTabViewModel"/>のコンストラクタ参照）。
/// </summary>
public static class MarkdownPreviewSizeGuard
{
    /// <summary>プレビュー対象とする文字数の上限。超えると編集モードで開く。</summary>
    public const int CharLimit = 150_000;

    /// <summary>プレビュー対象とする行数の上限。超えると編集モードで開く。</summary>
    public const int LineLimit = 8_000;

    /// <summary>
    /// プレビューが利用できない場合、利用者へ表示する理由（日本語）を返す。
    /// 上限内であれば null（プレビュー可能）を返す。
    /// </summary>
    public static string? EvaluateUnavailableReason(int textLength, int lineCount)
    {
        if (textLength > CharLimit)
        {
            return $"ファイルが大きいため（約{textLength:N0}文字）、プレビューを表示せず編集モードで開いています。";
        }

        if (lineCount > LineLimit)
        {
            return $"ファイルが大きいため（{lineCount:N0}行）、プレビューを表示せず編集モードで開いています。";
        }

        return null;
    }
}
