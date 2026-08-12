# NotoDo

デスクトップ常駐の小さな TODO ウィジェットです。**Notion のデータベースとの双方向同期（任意）**に対応しており、設定すればスマホの Notion アプリからも TODO を追加・編集・完了でき、数十秒で相互に反映されます。

- **Windows 版**: C# (WinForms)。単体 exe で動作、追加ランタイム不要
- **macOS 版**: Swift (AppKit)。メニューバー常駐 + フローティングウィジェット

## 機能

- ✅ チェックで完了。完了した項目はその日のうちは打ち消し線付きで最下部に表示され、**翌日から自動で非表示**
- 🏠 **Notion 同期は任意**。設定しなければ完全ローカルの TODO ウィジェットとして動作
- 🔄 Notion と1分間隔で同期（自分の操作は即時プッシュ）。オフラインでもローカルキャッシュで動作
- 📱 スマホの Notion アプリから追加・編集・チェックした内容も自動反映
- 🙈 Notion 側の「デスクトップ非表示」チェックで、項目ごとにウィジェットへの表示/非表示を制御
- 📌 ドラッグで移動、最前面表示切替、タスクトレイ/メニューバーに残件数を表示

## インストール

[Releases](https://github.com/wadokon/notodo/releases) からダウンロードするだけで使えます。

### Windows

1. `NotoDo.exe` をダウンロードして好きなフォルダに置き、ダブルクリックで起動
2. 初回に SmartScreen の警告が出た場合は「詳細情報」→「実行」を選択

.NET Framework 4.x（Windows 10/11 標準搭載）で動作します。スタートアップに登録したい場合は `shell:startup` フォルダにショートカットを置いてください。

### macOS

1. `NotoDo-mac.zip` をダウンロードして展開し、`NotoDo.app` をアプリケーションフォルダなどに置く
2. 初回は Gatekeeper にブロックされるため、**右クリック →「開く」**で起動（または `xattr -d com.apple.quarantine NotoDo.app`）

ソースからビルドする場合は `mac/build.sh` を実行します（要 Xcode Command Line Tools）。Windows も `windows/NotoDo.cs` から Visual Studio なしでビルドできます:

```bat
%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /codepage:65001 ^
  /target:winexe /out:NotoDo.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
  /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  windows\NotoDo.cs
```

## Notion 同期のセットアップ（任意）

設定しない場合、TODO は各 PC のローカルにのみ保存されます。同期したい場合のみ以下を行ってください。

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
- ウィジェットのヘッダ右上に同期状態のドット（緑=OK / 黄=同期中 / 赤=エラー）が表示されます。ローカルモードではドットは表示されません

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
