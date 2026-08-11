<#
.SYNOPSIS
    Markdownプレビューと構文強調をWindows実機で確認するためのサンプル一式を生成する。

.DESCRIPTION
    次のファイルをデスクトップ配下に生成する。

      project/
        sample.md                    … Markdown記法をできるだけ網羅した確認用ファイル。
                                        各記法に「これは○○の確認です」という説明と、
                                        期待される見た目（正解）を日本語で書いてある。
        linked.md                    … sample.md からの相対リンクで開くための短い別ファイル。
        image.png                    … sample.md からの相対リンクで表示するための小さな画像。
                                        System.Drawing等の外部ライブラリを使わず、PNGの
                                        バイト列（チャンク・zlib圧縮）をこのスクリプト自身で
                                        組み立てて書き出す（New-SimplePng参照）。
        構文ハイライト確認\
          highlight-sample.*         … エディタ本体（Markdownプレビューではなく通常の編集画面）の
                                        構文強調を確認するための短いコードファイル。css/py/js/json/cs/
                                        html/xml/yaml/sql/sh の10種。「1行目が欠ける」不具合の確認も
                                        兼ねるため、どのファイルも1行目から内容が始まる。
      このフォルダについて.txt       … 使い方の要約（このフォルダ単体で迷わないように）。

    生成したら Graft で project フォルダをプロジェクトとして登録し、sample.md を開いて
    プレビュー表示に切り替え、docs\確認手順_Markdownとサンプル.md の手順に沿って確認する。

.PARAMETER OutputPath
    生成先。既定はデスクトップの「Graft検証サンプル_Markdown」。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\New-MarkdownTestSample.ps1
#>

[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Graft検証サンプル_Markdown')
)

$ErrorActionPreference = 'Stop'

# 生成先を絶対パスへ正規化する（相対パス指定・末尾の \ 有無を吸収する。[System.IO.File]に
# 相対パスを渡すと.NETプロセスのカレントディレクトリ（多くの場合C:\WINDOWS\system32）が
# 基準になってしまうため、以降はすべてこの絶対パスから組み立てる）。
$root = [System.IO.Path]::GetFullPath($OutputPath)
$projectRoot = Join-Path $root 'project'
$highlightRoot = Join-Path $projectRoot '構文ハイライト確認'

# 既存の生成物があれば作り直す（前回の生成結果が古いままだと確認の意味が薄れるため）。
if (Test-Path $root) { Remove-Item -Recurse -Force $root }
New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null
New-Item -ItemType Directory -Path $highlightRoot -Force | Out-Null

Write-Host "生成先: $root"

# --------------------------------------------------------------------------
# 文字コード・改行のヘルパー
# --------------------------------------------------------------------------
# 生成する.mdや対象ファイルはUTF-8（BOM無し）・LFで書き出す。
function Write-Utf8NoBom([string]$Path, [string]$Text) {
    $dir = Split-Path -Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Path), $Text, $encoding)
}

# 生成する.ps1は必ずBOM付きで書き出す（このスクリプト自身もBOM付きで保存している）。
# Windows PowerShell 5.1はBOMの無いスクリプトをANSI（日本語環境ではCP932）として読むため、
# UTF-8の日本語が壊れる。単に表示が化けるだけでなく、化けた結果に引用符と同じバイトが
# 現れると構文エラーでスクリプト自体が動かなくなる（実機で発生した実績あり）。
# このスクリプト自体は.md等しか生成しないため実際には使わないが、他のスクリプトとの
# 作法（tools\New-RevisionTestSample.ps1）を揃えるために用意しておく。
function Write-Utf8Bom([string]$Path, [string]$Text) {
    $dir = Split-Path -Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $encoding = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Path), $Text, $encoding)
}

# --------------------------------------------------------------------------
# PNG生成ヘルパー（System.Drawing等の外部ライブラリを使わず、PowerShellの標準機能
# （System.IO.Compression.DeflateStream）だけでPNGのバイト列を直接組み立てる）。
#
# 【なぜSystem.Drawingを使わないか】System.Drawing.Bitmap等はWindows専用の実装に依存する
# 部分があり、環境（.NET 5.1系/7系、Server Core等）によっては使えないことがある。
# PNGのファイル形式（シグネチャ + チャンク列。各チャンクは 長さ+種別+データ+CRC32）自体は
# 単純なので、CRC32とAdler32（zlibの圧縮データに必要なチェックサム）さえ自前で計算すれば、
# バイト列として直接書き出せる。ピクセルデータの圧縮にはDeflateStream（PowerShell 5.1/7の
# どちらにも存在する標準の.NET機能）を使い、zlibヘッダ（2バイト）とAdler32（4バイト）で
# 前後を挟んでIDATチャンクのデータにする。
#
# 【0xFFFFFFFFのような16進リテラルを直接使わない理由】PowerShellは0xFFFFFFFFのような
# 32bit幅いっぱいの16進リテラルをInt32として解釈し、最上位ビットが立っているため負の値
# （-1）になる。これをそのまま[uint64]へキャストすると「値が大きすぎる/小さすぎる」旨の
# 例外になる（実際にこのスクリプトを書く過程で遭遇した）。10進リテラル（4294967295等）は
# Int32に収まらない場合はInt64として解釈されるため、正の値のまま[uint64]へキャストでき、
# この問題を避けられる。
# --------------------------------------------------------------------------
[uint64]$script:PngMask32 = [uint64]4294967295
[uint64]$script:PngCrc32Poly = [uint64]3988292384

function Get-PngCrc32Table {
    if (-not $script:PngCrc32Table) {
        $table = New-Object 'uint32[]' 256
        for ($n = 0; $n -lt 256; $n++) {
            [uint64]$c = [uint64]$n
            for ($k = 0; $k -lt 8; $k++) {
                if (($c -band 1) -ne 0) {
                    $c = ($script:PngCrc32Poly -bxor ($c -shr 1)) -band $script:PngMask32
                } else {
                    $c = ($c -shr 1) -band $script:PngMask32
                }
            }
            $table[$n] = [uint32]$c
        }
        $script:PngCrc32Table = $table
    }
    return $script:PngCrc32Table
}

function Get-PngCrc32([byte[]]$Bytes) {
    $table = Get-PngCrc32Table
    [uint64]$crc = $script:PngMask32
    foreach ($b in $Bytes) {
        $idx = [int](($crc -bxor [uint64]$b) -band 0xFF)
        $crc = (([uint64]$table[$idx]) -bxor ($crc -shr 8)) -band $script:PngMask32
    }
    return [uint32](($crc -bxor $script:PngMask32) -band $script:PngMask32)
}

function Get-PngAdler32([byte[]]$Bytes) {
    [uint64]$a = 1
    [uint64]$b = 0
    foreach ($byte in $Bytes) {
        $a = ($a + $byte) % 65521
        $b = ($b + $a) % 65521
    }
    return [uint32](($b -shl 16) -bor $a)
}

function Get-PngBigEndianBytes([uint32]$Value) {
    return [byte[]]@(
        [byte](($Value -shr 24) -band 0xFF),
        [byte](($Value -shr 16) -band 0xFF),
        [byte](($Value -shr 8) -band 0xFF),
        [byte]($Value -band 0xFF)
    )
}

function New-PngChunk([string]$Type, [byte[]]$Data) {
    $typeBytes = [System.Text.Encoding]::ASCII.GetBytes($Type)
    $lengthBytes = Get-PngBigEndianBytes ([uint32]$Data.Length)
    $crcInput = [byte[]]($typeBytes + $Data)
    $crcBytes = Get-PngBigEndianBytes (Get-PngCrc32 $crcInput)
    return [byte[]]($lengthBytes + $typeBytes + $Data + $crcBytes)
}

# 64x64の市松模様（Graftの配色に寄せた青と白）のPNGを生成する。
function New-SimplePng {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$Width = 64,
        [int]$Height = 64
    )

    $colorA = [byte[]]@(0x2B, 0x6C, 0xB0)  # 青系
    $colorB = [byte[]]@(0xFF, 0xFF, 0xFF)  # 白

    # 各行の先頭にPNGのフィルタタイプバイト（0=フィルタなし）を置き、続けてRGB各1バイトを並べる。
    $raw = New-Object System.Collections.Generic.List[byte]
    for ($y = 0; $y -lt $Height; $y++) {
        [void]$raw.Add([byte]0)
        for ($x = 0; $x -lt $Width; $x++) {
            $isColorA = ((([int]($x / 8)) + ([int]($y / 8))) % 2) -eq 0
            if ($isColorA) { $raw.AddRange($colorA) } else { $raw.AddRange($colorB) }
        }
    }
    $rawBytes = $raw.ToArray()

    $ms = New-Object System.IO.MemoryStream
    $deflate = New-Object System.IO.Compression.DeflateStream($ms, [System.IO.Compression.CompressionMode]::Compress, $true)
    $deflate.Write($rawBytes, 0, $rawBytes.Length)
    $deflate.Close()
    $deflateBytes = $ms.ToArray()
    $ms.Dispose()

    # zlib形式 = 2バイトのヘッダ + DeflateStreamが作った生のdeflateデータ + Adler32（4バイト、
    # ビッグエンディアン）。PNGのIDATチャンクはこのzlib形式のバイト列をそのまま格納する。
    $adler = Get-PngAdler32 $rawBytes
    $zlibBytes = [byte[]]([byte[]]@(0x78, 0x01) + $deflateBytes + (Get-PngBigEndianBytes $adler))

    $signature = [byte[]]@(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    $ihdrData = [byte[]]((Get-PngBigEndianBytes ([uint32]$Width)) + (Get-PngBigEndianBytes ([uint32]$Height)) + [byte[]]@(8, 2, 0, 0, 0))
    $ihdr = New-PngChunk 'IHDR' $ihdrData
    $idat = New-PngChunk 'IDAT' $zlibBytes
    $iend = New-PngChunk 'IEND' ([byte[]]@())

    $pngBytes = [byte[]]($signature + $ihdr + $idat + $iend)
    $dir = Split-Path -Path $Path -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Path), $pngBytes)
}

Write-Host ''
Write-Host '=== 1. sample.md / linked.md を生成 ==='

# --------------------------------------------------------------------------
# sample.md 本体
# --------------------------------------------------------------------------
# 1つの段落は1つの物理行にしてある（このレンダラは同じ段落内の複数行を区切り文字なしで
# そのまま連結するため、行を分けて書くと単語同士がくっついて表示される。docs\取扱説明書.md
# と同じ書き方に揃えている）。
$sampleMd = @'
# Markdown記法とシンタックスハイライトの確認サンプル

このファイルはGraftのMarkdownプレビューを確認するためのサンプルです。よく使われるMarkdown記法をできるだけ網羅し、各記法の直前または直後に「これは○○の確認です」という説明と、期待される見た目（正解）を日本語で書いてあります。プレビュー表示に切り替えて、各項目が説明どおりに見えるかどうかを確認してください。

## 目次

- [見出しの確認](#見出しの確認)
- [段落と強調の確認](#段落と強調の確認)
- [入り組んだ書き方の確認](#入り組んだ書き方の確認)
- [箇条書きとチェックリストの確認](#箇条書きとチェックリストの確認)
- [引用の確認](#引用の確認)
- [表の確認](#表の確認)
- [水平線の確認](#水平線の確認)
- [リンクの確認](#リンクの確認)
- [画像の確認](#画像の確認)
- [脚注の確認](#脚注の確認)
- [コードブロックの確認](#コードブロックの確認)

この目次の各項目は文書内アンカーへのリンクです。クリックすると対応する見出しへジャンプするのが正しい動きです。

## 見出しの確認

これは見出し（`#`から`######`まで6段階）の確認です。数字が増えるほど文字が小さく・控えめになり、レベル1が最も大きく目立ち、レベル6が最も小さく地味になるのが正しい見た目です。全レベルが視覚的に見分けられるかどうかを確認してください。以下にレベル1からレベル6までの見出しを実際に並べます。

# サンプル見出しレベル1

## サンプル見出しレベル2

### サンプル見出しレベル3

#### サンプル見出しレベル4

##### サンプル見出しレベル5

###### サンプル見出しレベル6

期待される見た目: 上から順に文字が小さくなり、6段階すべてが見分けられること。

## 段落と強調の確認

これは段落・太字・斜体・打ち消し線・インラインコード・エスケープの確認です。それぞれ1つの説明文の中に例を含める形にしてあります。

**太字の確認**: これは**太字になるはずの部分**を含む文章です。太字の部分だけがひときわ濃く強調されて見えるのが正しい見た目です。

**斜体の確認（アスタリスク記法）**: これは*斜体になるはずの部分*を含む文章です。斜体の部分だけ文字が傾いて見えるのが正しい見た目です。

**斜体の確認（アンダースコア記法）**: これは_斜体になるはずの部分（アンダースコア記法）_を含む文章です。アスタリスク記法と同じく文字が傾いて見えるのが正しい見た目です。

**打ち消し線の確認**: これは~~打ち消し線になるはずの部分~~を含む文章です。該当部分に横線が引かれて見えるのが正しい見た目です。

**インラインコードの確認**: これは`インラインコードになるはずの部分`を含む文章です。該当部分が等幅フォント・背景色付きで区別できるのが正しい見た目です。

**エスケープの確認**: これは \* と \_ と \` を書いた文章です。いずれも記号としてそのまま1文字ずつ表示され、太字・斜体・インラインコードとして解釈されないのが正しい見た目です。

## 入り組んだ書き方の確認

これは装飾記号が入れ子になっていたり、紛らわしい書き方をしていたりする場合の確認です。実装によっては誤動作しやすい箇所です。

**太字の中に斜体がある場合の確認**: これは**太字の中に*斜体*が入っている**という文章です。全体が太字になり、そのうち「斜体」の部分だけがさらに斜体にもなっているのが正しい見た目です。

**インラインコードの中の記号がそのまま表示されることの確認**: これは`コード内の**記号**は装飾されない`という文章です。バッククォートの中の`**`はインラインコードの一部としてそのまま表示され、太字として解釈されないのが正しい見た目です。

**太字を含むリンクテキストの確認**: これは[**太字**のリンクテキストです](https://example.com)というリンクです。リンク全体がクリック可能になり、かつリンクテキストの中の太字表現も反映されるのが正しい見た目です。

**アスタリスクが装飾記号として誤認されないことの確認**: これは 2 * 3 = 6 という数式を含む文章です。前後にスペースを挟んだ単独の`*`は掛け算記号として扱われ、斜体として誤変換されないのが正しい見た目です。

## 箇条書きとチェックリストの確認

これは箇条書き（入れ子2〜3階層）・番号付きリスト・両者の混在・GitHub形式のチェックリストの確認です。

### 箇条書き（入れ子）の確認

期待される見た目: 階層が深くなるほど右にインデントされ、階層ごとに視覚的な段差がつくこと。

- 果物
  - りんご
    - 青森県産
    - 長野県産
  - みかん
    - 温州みかん
- 野菜
  - にんじん
  - じゃがいも

### 番号付きリストの確認

期待される見た目: 「1.」「2.」のように連番が振られること。

1. 最初の手順です
2. 次の手順です
3. 最後の手順です

### 箇条書きと番号付きリストが混在する確認

期待される見た目: 種類が切り替わったことが分かるように、それぞれ別のリストとして表示されること。

- 準備するもの（箇条書き）
- 材料をそろえる

1. 手順1（番号付きリスト）
2. 手順2

### チェックリストの確認

期待される見た目: 四角いチェックボックスが表示され、チェック済み項目にはレ点、未チェック項目には何も入らないこと。入れ子のチェックリストも階層が分かるように表示されること。

- [x] チェック済みの項目です
- [ ] 未チェックの項目です
  - [x] 入れ子のチェック済み項目です
  - [ ] 入れ子の未チェック項目です

## 引用の確認

これは引用（`>`）・入れ子の引用（`>>`）・引用の中の箇条書きとコードブロックの確認です。

> これは1階層目の引用です。左側に縦線が入り、本文と区別できる見た目になるのが正しい表示です。
>
> > これは2階層目（入れ子）の引用です。1階層目よりもさらに右にインデントされ、縦線が2本になるのが正しい表示です。

> 引用の中に箇条書きとコードブロックを含めた場合の確認です。
>
> - 引用の中の箇条書き項目1
> - 引用の中の箇条書き項目2
>
> ```text
> 引用の中のコードブロックです
> ```

期待される見た目: 引用符（`>`）そのものは表示されず、左側の縦線とインデントで引用であることが分かること。入れ子・箇条書き・コードブロックもそれぞれ正しく認識されること。

## 表の確認

これは表（見出し行・区切り行・複数列）の確認です。

| 列1（左寄せ想定） | 列2 | 列3 |
| --- | --- | --- |
| 1行目A | 1行目B | 1行目C |
| 2行目A | 2行目B | 2行目C |
| **太字セル** | `コードセル` | 通常セル |

期待される見た目: 見出し行が本文と区別できる見た目（太字や背景色など）になり、罫線で区切られた表として表示されること。セル内の太字・インラインコードも段落中と同じように装飾されること。

## 水平線の確認

これは水平線（`---`）の確認です。この段落の直後に区切り線が1本入ります。

---

期待される見た目: 上下の文章の間に、横幅いっぱいの区切り線が1本表示されること。

## リンクの確認

これは3種類のリンク（文書内アンカー・同じフォルダの別のMarkdownファイルへの相対リンク・外部リンク）の確認です。

- [文書内アンカーの確認: 「見出しの確認」章へ移動](#見出しの確認)
- [相対リンクの確認: 同じフォルダのlinked.mdを開く](linked.md)
- [外部リンクの確認: example.comを開く](https://example.com)

期待される見た目: いずれのリンクもクリックできる見た目（下線や色分けなど）になっていること。実際にクリックすると、1つ目は本文中の「見出しの確認」章へジャンプし、2つ目はこの`sample.md`と同じフォルダの`linked.md`が開き、3つ目は確認ダイアログを経て既定のブラウザでexample.comが開くのが正しい動きです。

## 画像の確認

これは画像（同じフォルダに置いた画像への相対リンク、および外部の画像URL）の確認です。

同じフォルダの画像（相対リンク）:

![同じフォルダに置いた確認用画像（相対リンク）](image.png)

外部の画像（URL直接指定、実在しないURLのため読み込みには失敗します）:

![外部の確認用画像（example.com経由、読み込み失敗が正常）](https://example.com/graft-sample-image.png)

期待される見た目: 1つ目は実際の画像（青と白の市松模様のPNG）がそのまま本文中に表示されること。2つ目はURLが実在しないため、画像が読み込めなかったことを示す表示（代替テキストが見える等）になっていて構いません。いずれも「!」や「[...]」のような生のMarkdown記号がそのまま見えている状態は正しくありません。

## 脚注の確認

これは脚注記法の確認です。本文中に注釈を付けたい場合に使います。本文中に脚注の参照を1つ置きます[^1]。

期待される見た目: 本文中の`[^1]`の部分が小さな上付き番号（またはそれに準じた見た目）になり、クリックするとページ下部の脚注定義へジャンプすること。脚注定義側の行頭にも対応する番号が表示されること。

[^1]: これが脚注の本文です。脚注番号をクリックすると本文の参照位置へ戻れるのが理想的な見た目です。

## コードブロックの確認

これはコードブロック（言語指定あり・なし）の確認です。対応言語ではキーワード・文字列・コメントなどに色が付き、未対応の言語や言語指定なしの場合は等幅フォントのみで色が付かないのが正しい見た目です。

### Python（言語指定: python）

```python
def greet(name: str) -> str:
    # これはコメントです
    message = f"こんにちは、{name}さん"
    return message


class Sample:
    """docstringのテストです"""
    value = 42
```

### C#（言語指定: csharp）

```csharp
namespace GraftSample
{
    // これはコメントです
    public class Sample
    {
        public string Message => "こんにちは、Graftのサンプルです";
        public int Value { get; set; } = 42;
    }
}
```

### JavaScript（言語指定: javascript）

```javascript
// これはコメントです
function greet(name) {
    const message = `こんにちは、${name}さん`;
    return message;
}

class Sample {
    constructor() {
        this.value = 42;
    }
}
```

### TypeScript（言語指定: typescript）

```typescript
// これはコメントです
interface Greeting {
    message: string;
}

function greet(name: string): Greeting {
    return { message: `こんにちは、${name}さん` };
}
```

### CSS（言語指定: css）

```css
/* これはコメントです */
.sample-class {
    color: #336699;
    font-size: 14px;
    background-color: rgba(255, 255, 255, 0.8);
}
```

### JSON（言語指定: json）

```json
{
    "name": "Graftサンプル",
    "value": 42,
    "enabled": true,
    "items": ["a", "b", "c"]
}
```

### HTML（言語指定: html）

```html
<!-- これはコメントです -->
<html>
    <body>
        <p>こんにちは、Graftのサンプルです</p>
    </body>
</html>
```

### XML（言語指定: xml）

```xml
<!-- これはコメントです -->
<root>
    <item id="1">こんにちは</item>
</root>
```

### YAML（言語指定: yaml）

```yaml
# これはコメントです
name: Graftサンプル
value: 42
items:
  - a
  - b
```

### SQL（言語指定: sql）

```sql
-- これはコメントです
SELECT name, value FROM sample_table WHERE enabled = 1;
```

### シェルスクリプト（言語指定: bash）

```bash
#!/bin/bash
# これはコメントです
echo "こんにちは、Graftの構文強調確認サンプルです"
```

### 言語指定なし

```
これは言語指定の無いコードブロックです。
色が一切付かず、等幅フォントのままなのが正しい見た目です。
```

以上でMarkdown記法の確認サンプルは終わりです。
'@
Write-Utf8NoBom (Join-Path $projectRoot 'sample.md') $sampleMd
Write-Host '生成: project\sample.md'

# --------------------------------------------------------------------------
# linked.md（sample.mdからの相対リンク先）
# --------------------------------------------------------------------------
$linkedMd = @'
# リンク確認用の別ファイル

これは`sample.md`から相対リンクで開くための確認用ファイルです。このファイルが正しく開けたら、相対リンクの確認は成功です。

[sample.mdへ戻る](sample.md)
'@
Write-Utf8NoBom (Join-Path $projectRoot 'linked.md') $linkedMd
Write-Host '生成: project\linked.md'

# --------------------------------------------------------------------------
# 画像（sample.mdからの相対リンク先）
# --------------------------------------------------------------------------
Write-Host ''
Write-Host '=== 2. image.png を生成 ==='
try {
    New-SimplePng -Path (Join-Path $projectRoot 'image.png') -Width 64 -Height 64
    Write-Host '生成: project\image.png（64x64、青と白の市松模様、PowerShellのみで生成）'
} catch {
    Write-Warning "画像の生成に失敗しました（$($_.Exception.Message)）。sample.md内の画像表示の確認だけスキップしてください。他のファイルの生成は続行します。"
}

# --------------------------------------------------------------------------
# 構文強調確認用のコードファイル（1行目から内容が始まる。「1行目が欠ける」不具合の
# 確認も兼ねる）
# --------------------------------------------------------------------------
Write-Host ''
Write-Host '=== 3. 構文強調確認用のコードファイルを生成 ==='

$highlightFiles = @(
    @{
        Name = 'highlight-sample.css'
        Text = @'
/* CSS構文強調の確認用サンプルです。1行目から内容が始まっています。 */
.header {
    color: #333333;
    background-color: #f0f0f0;
    font-size: 16px;
}

#main-content a:hover {
    text-decoration: underline;
}
'@
    },
    @{
        Name = 'highlight-sample.py'
        Text = @'
# Python構文強調の確認用サンプルです。1行目から内容が始まっています。
def add(a, b):
    """2つの数を足すだけの関数です"""
    return a + b


class Calculator:
    def __init__(self):
        self.total = 0

    def add(self, value):
        self.total += value
        return self.total
'@
    },
    @{
        Name = 'highlight-sample.js'
        Text = @'
// JavaScript構文強調の確認用サンプルです。1行目から内容が始まっています。
function add(a, b) {
    return a + b;
}

class Calculator {
    constructor() {
        this.total = 0;
    }

    add(value) {
        this.total += value;
        return this.total;
    }
}
'@
    },
    @{
        Name = 'highlight-sample.json'
        Text = @'
{
    "name": "構文強調確認サンプル",
    "description": "JSON構文強調の確認用サンプルです。1行目から内容が始まっています。",
    "count": 3,
    "tags": ["json", "sample", "graft"]
}
'@
    },
    @{
        Name = 'highlight-sample.cs'
        Text = @'
// C#構文強調の確認用サンプルです。1行目から内容が始まっています。
namespace GraftHighlightSample
{
    public class Calculator
    {
        private int _total;

        public int Add(int a, int b) => a + b;

        public int Total => _total;
    }
}
'@
    },
    @{
        Name = 'highlight-sample.html'
        Text = @'
<!-- HTML構文強調の確認用サンプルです。1行目から内容が始まっています。 -->
<!DOCTYPE html>
<html>
<head><title>サンプル</title></head>
<body>
    <p>構文強調の確認用です。</p>
</body>
</html>
'@
    },
    @{
        Name = 'highlight-sample.xml'
        Text = @'
<!-- XML構文強調の確認用サンプルです。1行目から内容が始まっています。 -->
<config>
    <setting name="theme" value="dark" />
    <setting name="language" value="ja" />
</config>
'@
    },
    @{
        Name = 'highlight-sample.yaml'
        Text = @'
# YAML構文強調の確認用サンプルです。1行目から内容が始まっています。
name: 構文強調確認サンプル
version: 1
tags:
  - yaml
  - sample
'@
    },
    @{
        Name = 'highlight-sample.sql'
        Text = @'
-- SQL構文強調の確認用サンプルです。1行目から内容が始まっています。
SELECT id, name FROM users WHERE active = 1 ORDER BY name;
'@
    },
    @{
        Name = 'highlight-sample.sh'
        Text = @'
#!/bin/bash
# シェルスクリプト構文強調の確認用サンプルです。1行目から内容が始まっています。
echo "こんにちは、Graftの構文強調確認サンプルです"
'@
    }
)

foreach ($f in $highlightFiles) {
    Write-Utf8NoBom (Join-Path $highlightRoot $f.Name) $f.Text
    Write-Host ('生成: project\構文ハイライト確認\' + $f.Name)
}

# --------------------------------------------------------------------------
# フォルダ全体の説明ファイル
# --------------------------------------------------------------------------
Write-Host ''
Write-Host '=== 4. フォルダの説明ファイルを生成 ==='
$readmeText = @"
このフォルダについて
====================

これはdocs\確認手順_Markdownとサンプル.mdに沿ってGraftのMarkdownプレビューと構文強調を
確認するための、自動生成された検証用データです（tools\New-MarkdownTestSample.ps1が
生成しました）。

- project\ … Graftに「プロジェクトを追加」で登録してください。
- project\sample.md … メインの確認用ファイルです。Graftで開き、プレビュー表示に
  切り替えて確認してください。
- project\linked.md、project\image.png … sample.mdからの相対リンク確認用です。
- project\構文ハイライト確認\ … エディタ本体の構文強調（プレビューではなく通常の
  編集画面の色付け）を確認するための短いコードファイルです。

生成日時: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
生成元:   $PSCommandPath

手順の詳細はdocs\確認手順_Markdownとサンプル.mdを参照してください。
"@
Write-Utf8NoBom (Join-Path $root 'このフォルダについて.txt') $readmeText
Write-Host '生成: このフォルダについて.txt'

Write-Host ''
Write-Host '=== 生成完了 ==='
Write-Host "出力先: $root"
Write-Host ''
Write-Host '--- 使い方 ---'
Write-Host "1. Graftで $projectRoot をプロジェクトとして登録する"
Write-Host '2. sample.md をダブルクリックで開き、プレビュー表示に切り替える'
Write-Host '3. docs\確認手順_Markdownとサンプル.md の手順に沿って、各記法の見た目を確認する'
Write-Host '4. project\構文ハイライト確認\ 内の各ファイルを開き、通常の編集画面での構文強調も確認する'
