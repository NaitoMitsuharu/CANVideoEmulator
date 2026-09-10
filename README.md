# CANVideoEmulator

comma2k19の走行動画を再生しながら、同じ時刻のCANフレームをPCAN-USBから出力するWindowsアプリです。PCAN-USBが無くても動画だけのデモ再生ができます。

## How to Install

対象はWindows 10/11 x64です。

1. [Git for Windows](https://git-scm.com/downloads/win)をインストールする
2. PowerShellで以下を実行する

```powershell
git clone https://github.com/NaitoMitsuharu/CANVideoEmulator.git
cd CANVideoEmulator
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup.ps1
```

`setup.ps1`が.NET 10 SDKを自動導入してビルドし、成功すると自動でアプリが起動します。生成されたEXEに.NETが同梱されているため、別PCへ持ち込む場合も.NET Runtimeのインストールは不要です。

PCAN-USBから実際にCANを送信したい場合は、追加で[PEAKのWindowsドライバー](https://www.peak-system.com/quick/DrvSetup)(PCAN-Basic APIを含む)をインストールしてください。動画だけのデモには不要です。

## How to Use

1. `bin\CANVideoEmulator.exe`を起動する(初回は簡単な初期設定ウィザードが表示されます)
2. ウィンドウ下端にカーソルを近づけるとシナリオ一覧がせり上がるので、再生したいシナリオを選ぶ
3. `PLAY`ボタン(またはSpaceキー)で再生を開始する
4. PCAN-USBへ送信したい場合は`Transmit to PCAN-USB`をONにしてから`PLAY`を押す。動画だけ見たい場合はOFFのままでよい

![プレイヤー画面](docs/images/main-player.png)

## Explain UI

| 部分 | 説明 |
|---|---|
| 中央 | 走行動画。左上にタイトル、右上に再生タイミングの統計を表示 |
| 下部 | PLAY / PAUSE / Stop、シークバー、Previous / Next |
| 右側 | CAN BUSの選択、Transmit to PCAN-USBのON/OFF、Bitrate、接続状態(Driver Missing / Not Connected / USB Ready / Connected など) |
| RECENT CAN | 直近に送受信したCANフレームの一覧 |
| シナリオ一覧 | 画面下端にカーソルを寄せる、`Scenarios`ボタン、または`Ctrl+B`で表示。サムネイル付きカードから選択する |
| Get Scenarios | シナリオを新しく取得・追加するためのウィンドウを開く |

![シナリオ一覧](docs/images/scenario-list.png)

## How to Add Scenarios(シナリオの追加方法)

### アプリから追加する(簡単)

1. メイン画面の`Get Scenarios`ボタンを押す
2. **Step 1 — Choose chunks**：追加したいチャンクを選ぶ(すぐ試したいだけなら`Get 45 MB Sample`で公式の1分サンプルを取得できる)
3. **Step 2 — Download**：`Download & Convert`を押してダウンロードする
4. **Step 3 — Convert & Add to Player**：自動で変換・検証まで行われ、完了するとシナリオ一覧に追加される

![Get Scenariosウィンドウ](docs/images/get-scenarios.png)

この自動変換には、このリポジトリのclone一式に加えてPython 3.12 x64とFFmpegが必要です。EXEだけをコピーしたPCでは、既に作成済みのシナリオを再生することはできますが、新規のシナリオ変換はできません。

### スクリプトから追加する(手動)

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\prepare_scenarios.ps1 -InputPath 'D:\comma2k19\Chunk_1.zip' -Count 3
```

`-Count`で作成する区間数を指定できます(省略時は全区間)。作成されたシナリオは`Scenarios`フォルダーに保存され、次回アプリ起動時に自動で読み込まれます。

---

より詳しい内容(データ量の目安、CAN定義ファイルの場所、開発者向け情報など)は[docs](docs/)以下の各ドキュメントを参照してください。
