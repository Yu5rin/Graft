using System.Runtime.CompilerServices;

// Markdownプレビュー機能のテスト用（EditorPane.MarkdownPreview.csのMarkdownLinkDialogs・
// OpenExternalLinkAction参照）。AvaloniaDialogServiceが組み立てる確認ダイアログはヘッドレス
// テストから実際に操作する手段が無いため（DialogKeyboardCoverageTestsのコメント参照）、
// 「確認してから開く」という順序自体をテストで担保するための最小限のinternalな差し替え口を
// UiTestsプロジェクトへ公開する。
[assembly: InternalsVisibleTo("Graft.UiTests")]
