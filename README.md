# NotoDo

Notion のデータベースと双方向同期する、デスクトップ常駐の TODO ウィジェットです。
PC のデスクトップに置いた小さなウィジェットと、スマホの Notion アプリの両方から TODO を追加・編集・完了でき、数十秒で相互に反映されます。

- **Windows 版**: C# (WinForms)。.NET Framework 標準機能のみで動作、追加ランタイム不要
- **macOS 版**: Swift (AppKit)。メニューバー常駐 + フローティングウィジェット

## 機能

- ✅ チェックで完了。完了した項目はその日のうちは打ち消し線付きで最下部に表示され、**翌日から自動で非表示**（データは Notion に残る）
- 🔄 Notion と1分間隔で同期（自分の操作は即時プッシュ）。オフラインでもローカルキャッシュで動作
- 📱 スマホの Notion アプリから追加・編集・チェックした内容も自動反映
- 🙈 Notion 側の「デスクトップ非表示」チェックで、項目ごとにウィジェットへの表示/非表示を制御
- 📌 ドラッグで移動、最前面表示切替、タスクトレイ/メニューバーに残件数を表示

## セットアップ

### 1. Notion データベースの準備

Notion で以下のプロパティを持つデータベースを作成します（名前は完全一致で作成してください）:

| プロパティ名 | 種類 | 役割 |
|---|---|---|
| `Name` | タイトル | TODO の本文 |
| `Done` | チェックボックス | 完了フラグ |
| `完了日` | 日付 | 完了した日（自動記録） |
| `デスクトップ非表示` | チェックボックス | ONにするとウィジェットに表示しない |
| `作成日時` | 作成日時 | 並び替え用（任意） |

> 補足: `Done` / `デスクトップ非表示` という名前のチェックボックスが無い場合は、最初に見つかったチェックボックスが完了フラグとして使われます。

### 2. Notion インテグレーションの作成

1. https://www.notion.so/profile/integrations で「新しいインテグレーション」を作成（種類: 内部）
2. シークレット（`ntn_...`）をコピー
3. 作成した TODO データベースのページを開き、右上 **⋯ → 接続** からインテグレーションを追加

### 3. 設定ファイル

`notion-config.example.json` をコピーして `notion-config.json` を作り、トークンとデータベース ID を記入します。

```json
{"Token":"ntn_xxxxxxxxxxxx","DatabaseId":"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"}
```

- DatabaseId はデータベースページ URL の末尾 32 文字（ハイフンあり/なしどちらでも可）
- 置き場所:
  - **Windows**: `NotoDo.exe` と同じフォルダ
  - **macOS**: `~/Library/Application Support/NotoDo/`
- 設定ファイルは自動で再読込されるため、書き換え後の再起動は不要です

## ビルド

### Windows

.NET Framework 付属のコンパイラだけでビルドできます（Visual Studio 不要）:

```bat
%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 ^
  /target:winexe /out:NotoDo.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  windows\NotoDo.cs
```

### macOS

Xcode Command Line Tools（`xcode-select --install`）が必要です:

```sh
cd mac
./build.sh
open NotoDo.app
```

## 操作方法

| 操作 | Windows | macOS |
|---|---|---|
| 追加 | 「＋ 追加」をクリック | 「＋ 追加」をクリック |
| 完了 | チェックボックス | チェックボックス |
| 編集 | 行をダブルクリック | 行をダブルクリック |
| 削除 | 行ホバーで表示される × | 行の × |
| 移動 | ヘッダ/余白をドラッグ | 余白をドラッグ |
| 非表示化 | 行を右クリック → デスクトップで非表示 | Notion 側でチェック |
| メニュー | ウィジェット右クリック / トレイアイコン | メニューバーアイコンを右クリック |
| 表示切替 | トレイアイコンをダブルクリック | メニューバーアイコンをクリック |

## 同期の仕様

- ローカル → Notion: 操作した瞬間にプッシュ
- Notion → ローカル: 60 秒間隔のポーリング
- スマホ側でチェックだけ入れた場合、`完了日` はページの最終編集時刻から自動補完されます
- ウィジェット上の削除は Notion 側ではアーカイブ（ゴミ箱）になります
- Notion 側で削除（アーカイブ）した項目はウィジェットからも消えます

## ライセンス

MIT License
