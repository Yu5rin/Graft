# 第三者の著作物について（THIRD-PARTY NOTICES）

Graft 本体は MIT License（[`LICENSE`](./LICENSE)）で公開している。このファイルは、それとは別の条件で
リポジトリと配布物に含めている第三者の著作物と、その出典・条件をまとめたものである。
依存ライブラリ（NuGet パッケージ）のライセンスは README の「使用ライブラリのライセンス」と、
実行ファイルのバージョン情報画面（ライセンスの全文を埋め込み）にある。

## Office の customUI の XML スキーマ（XSD）

リボン（customUI）の XML を、ブックへ書く前に検査する（`src/Graft/Core/Vba/VbaRibbonXmlValidator.cs`。
仕様書_ブックの情報と依頼の観点 16.8）ために、Microsoft の公開仕様の付録にある XML スキーマの全文を同梱している。
実行ファイルには埋め込みリソースとして入る。

| ファイル | 形式（名前空間） | 出典 | 版 | 仕様書の中の箇所 |
|---|---|---|---|---|
| `src/Graft/Assets/CustomUI/customUI.xsd` | `http://schemas.microsoft.com/office/2006/01/customui`（`customUI.xml`） | [MS-CUSTOMUI]: Custom UI XML Markup Specification — <https://learn.microsoft.com/en-us/openspecs/office_standards/ms-customui/> | Protocol Revision 10.1（2/18/2025） | Appendix B: Full XML Schemas |
| `src/Graft/Assets/CustomUI/customUI14.xsd` | `http://schemas.microsoft.com/office/2009/07/customui`（`customUI14.xml`） | [MS-CUSTOMUI2]: Custom UI XML Markup Version 2 Specification — <https://learn.microsoft.com/en-us/openspecs/office_standards/ms-customui2/> | Protocol Revision 8.0（8/20/2024） | Appendix A: Full XML Schemas |

- 取得したのは、上の各ページに掲げられた版の文書（.docx）である。
  - `https://officeprotocoldocs-f5hpbjgea6b8gneq.b02.azurefd.net/files/MS-CUSTOMUI/%5bMS-CUSTOMUI%5d-250218.docx`
    （SHA-256: `738a9ea710c4c3aa464d5d171c9986a5d17db8e459a773bedafb3a02b58b4a00`）
  - `https://officeprotocoldocs-f5hpbjgea6b8gneq.b02.azurefd.net/files/MS-CUSTOMUI2/%5bMS-CUSTOMUI2%5d-240820.docx`
    （SHA-256: `773741cc9d0f99ec533cc67446f6fd7006eb4548f6cfa5cbca5d07bc9e332b7a`）
- スキーマの本文（`xsd:schema` 要素）は、文書の付録から 1 文字も変えずに写した。変えたのは、各ファイルの先頭に
  XML 宣言（`encoding="utf-8"`）と、出典・版・箇所を書いたコメントを足したことだけである。
- 2 つの文書は、Microsoft の Open Specifications Promise の対象一覧（[MS-DEVCENTLP]、Revised February 24, 2023）にも
  載っている（一覧の版と、ここで使った文書の版の対応は確かめていない）。

### 再配布の根拠（Intellectual Property Rights Notice の Copyrights 項・原文）

両文書の冒頭の「Intellectual Property Rights Notice for Open Specifications Documentation」の Copyrights 項は、
次のとおりである（両文書で同じ文面）。

> Copyrights. This documentation is covered by Microsoft copyrights. Regardless of any other terms that are contained in the terms of use for the Microsoft website that hosts this documentation, you can make copies of it in order to develop implementations of the technologies that are described in this documentation and can distribute portions of it in your implementations that use these technologies or in your documentation as necessary to properly document the implementation. You can also distribute in your implementation, with or without modification, any schemas, IDLs, or code samples that are included in the documentation. This permission also applies to any documents that are referenced in the Open Specifications documentation.

Graft はこの「You can also distribute in your implementation, with or without modification, any schemas, IDLs,
or code samples that are included in the documentation.」に基づいて、スキーマを実装（Graft）に含めて配布している。
この許諾は Graft の MIT License とは別のもので、MIT License はこの 2 つの XSD には及ばない。

### 商標の注意

同じ通知の Trademarks 項のとおり、この通知は商標のライセンスを与えない。

> Trademarks. The names of companies and products contained in this documentation might be covered by trademarks or similar intellectual property rights. This notice does not grant any licenses under those rights. For a list of Microsoft trademarks, visit www.microsoft.com/trademarks.

Microsoft、Excel、Office は、Microsoft グループの企業の商標である。Graft は Microsoft とは関係が無く、
Microsoft による承認・後援を受けたものではない。Graft がこれらの名前を使うのは、連携する相手の製品を指し示すためだけである。

### 特許について

同じ通知の Patents 項は、文書が特許のライセンスを与えないこと、文書によっては Microsoft Open Specifications Promise
または Microsoft Community Promise の対象になりうることを述べている。扱いは原文（各文書の Patents 項と [MS-DEVCENTLP]）を
参照のこと。このファイルは、特許について何らかの判断を示すものではない。
