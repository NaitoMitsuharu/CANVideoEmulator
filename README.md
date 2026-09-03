# CANVideoEmulator

**comma2k19の走行動画を再生し、同じ記録時刻のCANフレームをPCAN-USBから出力するWindowsアプリです。** PCAN-USBがなくても動画と送信スケジューラーをデモ再生できます。

```text
comma2k19 → Scenario Builder → 動画MP4 + バス別CAN + シナリオ情報
                                      ↓
                        Windows / CANVideoEmulator
                            ├─ アプリ内で動画再生
                            └─ PCAN-USB → CAN受信機
```

実車のCAN ID・ペイロードを再生します。操作用の独自CANフレームは追加しません。複数の記録バスは混ぜず、選択した1バスをClassical CANとして送信します。

## 最短セットアップ：別のPCでcloneして使う

対象はWindows 10/11 x64です。初回はインターネット接続が必要です。

1. [Git for Windows](https://git-scm.com/downloads/win)をインストールします。
2. PowerShellで次を実行します。

```powershell
git clone https://github.com/NaitoMitsuharu/CANVideoEmulator.git
cd CANVideoEmulator
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup.ps1
```

`setup.ps1`は.NET 10 SDKを検出し、なければMicrosoftのインストーラーで`.tools/dotnet`に導入します。その後、cloneしたソースをビルドします。成功するとEXEを起動します。ビルドだけ行う場合は`-NoLaunch`を指定してください。生成したEXEは.NETを含むため、**再生先PCに別途.NET Runtimeをインストールする必要はありません**。PCANドライバーは別です。

ビルド済みEXEや大きなデータはGit管理対象外です。更新時も`git pull`後に同じセットアップコマンドを実行してください。公開済みReleaseを取得する既存の`get_player.ps1`は、新名称のReleaseアセットが公開されている場合だけ使えます。

### 別PCへ持ち込むもの・事前確認

| 用途 | 必要なもの |
|---|---|
| 作成済みシナリオの再生 | `bin`フォルダー＋`Scenarios`フォルダー、Windows 10/11 x64 |
| CAN出力 | 上記＋PEAK WindowsドライバーのPCAN-USB／PCAN-Basic API |
| cloneしたソースのビルド | Git、インターネット。.NET SDKはsetup.ps1が不足時に導入 |
| Get Scenariosで取得・変換 | clone一式、Python 3.12 x64、Git、FFmpeg（libx264とffprobeを含む）、インターネット、保存用空き容量 |

`bin`と`Scenarios`を同じ親フォルダーに置けば、作成済みデータの再生にPython・FFmpegは不要です。前のPCの`bin/config.json`は持ち込まず、初回設定をやり直すと古い絶対パスが残りません。通常のWindowsでH.264を再生します。Windows N版は[Media Feature Pack](https://support.microsoft.com/ja-JP/Windows/Experience/Platform-variants/media-feature-pack-for-windows-n)も必要です。アプリと設定を書き込めるユーザーフォルダーへ置いてください。

変換ツールだけ事前点検できます。データの取得や設定変更はしません。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare_scenarios.ps1 -CheckOnly
```

不足があればツール名と導入先を表示します。PythonはPATHだけでなく通常の3.12インストール先とpyランチャーからも検出します。Get Scenariosも**ダウンロード前**にこの点検を行い、数GBを取得してから依存不足に気づく事態を避けます。WinGetが使えるPCでは`winget install -e --id Python.Python.3.12`と`winget install -e --id Gyan.FFmpeg`でも導入できます。Git、Python、FFmpeg、PEAKドライバーは別途インストールが必要で、setup.ps1が自動導入するのは.NET SDKです。

この作業中の変更を別PCのcloneにも反映するには、変更したソース・scripts・assetsをGitへcommit/pushしてからcloneしてください。ローカルの未コミット変更はcloneでは取得できません。

### すぐに動画も試す：公式1分サンプル

シナリオ作成PCに[Python 3.12 x64](https://www.python.org/downloads/windows/)（自動セットアップで検証済みのバージョン）と[FFmpeg](https://ffmpeg.org/download.html)をインストールしてください。`python`、`ffmpeg`、`ffprobe`をPATHで実行できる状態にし、新しくPowerShellを開きます。

```powershell
python --version
ffmpeg -version
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup.ps1 -WithExample
```

これだけで次を行います。

- `.venv`にPythonの変換ツールと依存関係を導入。
- 固定したopendbcリビジョンを`.tools/opendbc`へ取得し、ToyotaのDBCを生成。
- 公式の1分サンプルから必要な10ファイル、**45,240,864バイト（約45 MB）**を取得。
- 動画・CAN・同期時刻を変換し、`Scenarios`へ保存して整合性を検証。

アプリだけ導入済みなら、`powershell -ExecutionPolicy Bypass -File .\scripts\prepare_scenarios.ps1`でもサンプルを作れます。サンプルデータとDBCはコミットを固定しています。Windows用の依存バージョンは`scenario_builder/requirements-windows.txt`に固定し、ビルド済みwheelを導入します。C++コンパイラーやCap’n Protoの個別インストールは不要です。

### PCAN-USBから送信する

[PEAK-SystemのWindowsドライバー](https://www.peak-system.com/quick/DrvSetup)をインストールし、**PCAN-Basic API**も含めてください。動画だけのデモには不要です。

1. PCAN-USBを接続し、`USB Ready`またはチャンネルの検出を確認。
2. 受信機の電源、CAN-H/CAN-L/GND、両端の120 Ω終端、通信速度を確認。
3. ウィンドウ下端にカーソルを寄せ、浮かび上がるシナリオ一覧から選択。
4. CAN BUSとBITRATEを確認。標準パッケージの再生速度は500 kbit/sです。
5. `Transmit to PCAN-USB`は起動時ONです。PLAYを押すと初期化して送信を始めます。動画だけ試す場合はOFFにしてください。

`Connected`はチャンネルの初期化と状態確認ができた意味です。**受信側への到達確認ではありません。** 録画データを送信するベンチ用途を想定しています。

## 初回セットアップ

設定が未完了、または設定したシナリオフォルダーが存在しない場合に表示されます。`config.json`がない場合も対象です。シナリオ、PCAN-USB、CAN設定、完了の4ステップをNext/Backで進めます。画面はスクロール不要で、ステップ間は約200 msのフェード・スライドで遷移します（Windowsでアニメーションを無効にしている場合は即時切り替え）。

`Finish & Open Player`で設定を保存し、そのままメイン画面を開きます。動画とCANは自動再生しません。既存設定を消さずにセットアップを開き直す場合：

```powershell
.\bin\CANVideoEmulator.exe --setup
```

## CAN ID・信号の定義はどこ？

各シナリオの`dbc/signals.json`と同じフォルダー内の`.dbc`です。受信アプリには定義を別途同梱してください。CANで定義ファイルを自動送信する仕組みはありません。ID表、Androidでの読み込み方法、バスごとの注意点は[CAN定義ガイド](docs/CAN_DEFINITIONS.md)を参照してください。

## 操作と画面

| 操作 | 動作 |
|---|---|
| PLAY / PAUSE、Space | 動画とCANスケジューラーを再生／一時停止 |
| Stop | 停止して先頭へ戻す |
| シークバー、左右キー | シーク、10秒移動 |
| Previous / Next | プレイリスト内の移動。停止中は選択のみ、再生中は再生を継続 |
| CAN BUS | 出力する記録バスを1つ選択 |
| Transmit to PCAN-USB | オフなら外部CAN出力なし |
| Get Scenarios | 公式サンプル、または複数チャンクの取得 |
| Reload | 作成済みシナリオを再読み込み |
| 下端へカーソル移動／Scenariosボタン／Ctrl+B | シナリオ一覧を下から表示 |
| 一覧からカーソルを離す／Escape | シナリオ一覧を閉じる |

RECENT CANは表示領域の高さに応じて最新フレームの件数を増減します。スクロールは不要です（最大200件、表示更新15 Hz）。CAN出力の時間制限テストは削除し、再生操作をPLAY/PAUSE/Stopに統一しました。

動画タイトルは映像左上、TIMINGの統計は映像右上に控えめなグレーで重ねます。車種は右側上部に表示します。右側のバス・チャンネル・通信速度は横並びです。

シナリオ一覧はウィンドウ下端にカーソルを寄せると約220 msでせり上がります。カーソルが離れると約400 ms待って閉じ、開閉しても動画サイズは変わりません。ScenariosボタンとCtrl+Bでも開けます。キーボードで一覧を操作中は開いたままになり、Escapeで閉じます。Windowsでアニメーションを無効にしている場合は即時切り替えます。


一覧は小さなサムネイル付きカードを幅に応じて複数列に並べ、縦にスクロールします。タイトル・車種・タグ・時間をコンパクトに表示し、詳細はカーソルを当てると確認できます。シナリオ一覧とGet Scenariosのホイール移動は1目盛り36論理pxに抑えています。


`API Accepted / Demo Frames`はPCAN APIが受け付けた数、またはデモで処理した数です。物理CANで受信された数とは異なります。Pause後も、既にドライバーへ渡した送信キューの排出までゼロ時間になる保証はありません。

BUS OFFについての今回のログ調査とAndroid側の設定確認は[調査メモ](docs/BUS_OFF_INVESTIGATION.md)に記載しています。

## PCAN未接続・途中の抜き差しではどうなる？

| 状況 | アプリの動作・表示 |
|---|---|
| ドライバー/APIなし | Driver Missing。送信をOFFにすると動画のデモ再生が可能 |
| ドライバーあり、USBなし | Not Connected。送信ONのPLAYは開始せず理由を表示。OFFならデモ再生が可能 |
| USBあり、送信チェックなし | USB Ready。外部CANは送信しない |
| USBあり、CANの先が未接続 | 送信前はUSB ReadyやConnectedになり得る。送信するとACK不足などによるBus Errorsや送信キュー詰まりが起き得る |
| 送信中にUSBを抜く | 約2秒周期の検出で一時停止・チャンネル解放・送信チェック解除。検出前の送信エラーはカウンターへ記録 |
| USBを接続／再接続 | 約2秒周期で一覧更新。1台なら自動選択。送信チェックの選択を保持し、**自動再生はしない**。PLAYで初期化して再開 |
| CANケーブルだけを抜く | USB一覧は変わらない。送信中のバス状態・エラーで判断。無通信時に相手の存在は確定できない |
| CANケーブルを戻す | 初期化済みチャンネルの状態表示は約250 ms周期で更新。エラーが解消すれば反映されるが、過去のテスト結果は残る |
| BUS OFF / ERROR PASSIVE | バス異常を表示し再生を一時停止。配線修正後、PLAYでチャンネルを再初期化。別アプリが同じチャンネルを使用している場合は先に終了 |
| チャンネル／BITRATE変更 | チャンネルを解放して一時停止。送信チェックは保持し、PLAYで新設定を適用 |


相手がいないから必ずBUS OFFまで進む、という意味ではありません。Error Passiveで留まることもあり、状態はコントローラーと配線条件によります。送信成功件数だけで到達を判定しないでください。BUS OFFからの復旧は[PEAKのGetStatus説明](https://www.peak-system.com/documentation/API/PCAN-Basic.Net/html/8e9e8746-4599-2d4f-0df4-4c9e68a19376.htm)も参照してください。


## Get Scenariosとデータ量

### Step 1 → Step 2 → Step 3で自動追加

1. **Step 1 — Choose chunks**：Chunk 1–2（Toyota RAV4）、Chunk 3–10（Honda Civic）を複数選択します。一覧は幅が十分なら2列、狭ければ1列になり、縦にスクロールできます。列数を切り替えても選択状態は保持されます。Ctrlキーは不要です。
2. **Step 2 — Download**：`Download & Convert`を押すと、未取得のファイルだけを順番に取得します。途中のファイルはRange対応サーバーなら再開します。
3. **Step 3 — Convert & Add to Player**：取得後、自動でZIP展開、MP4/CAN/DBC作成、検証を行い、シナリオ一覧を更新します。標準ではチャンク内の全区間が対象です。

Step 1は幅が十分なら2列、狭い場合は1列です。サイズ変更でも選択状態を保持し、ホイールの移動量は1目盛り36画面単位です。

Step 3には全体の進捗バーと、現在の入力番号・工程・処理済み件数を表示します。進捗は準備5%、展開15%、変換70%、検証9%の固定配分で、各工程の実測バイト数・区間数・検証件数から計算します。複数入力は均等に配分し、すべての処理が正常終了した時だけ100%になります。これは残り時間の推定ではありません。再利用したファイルやシナリオも処理済みとして数え、中断・エラー時は途中の値を保持します。

**ZIPを取得しただけでは再生一覧は増えません。Step 3の完了まで待ってください。** 既に取得済みの場合は`Convert Selected`でダウンロードを省略して変換できます。`Get 45 MB Sample`も選択チャンクとは独立して同じ自動処理を行い、取得済みなら`Add Sample`になります。

自動変換には、このリポジトリのcloneとPython・FFmpegが必要です。EXEのみをコピーしたPCでは作成済みシナリオを再生でき、変換は作成元PCで行います。処理中は保存先・選択の変更を無効にし、Cancelで中止できます。作成済みの区間と取得済みファイルは保持されます。ログにエラーが出た場合は解消後に再実行してください。

既定の生データ保存先は`data`です。選択した保存先、以前選んだ保存先、シナリオフォルダーと同じ親にある`data`と旧`comma2k19`を確認します。チャンクは各ZIP、45 MBサンプルは必要な10ファイルを確認し、**相対パスとサイズが一致する完了ファイルは通信せず再利用**します。PC全体の検索やハッシュによる破損検出ではありません。

変換時も、同じ走行ルート・区間番号・DBCプロファイルの完成済みパッケージを再利用します。フォルダー名が異なっていても重複追加しません。途中まで作成した不完全なパッケージは再作成します。

手動で最初の3区間だけ作る場合：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\prepare_scenarios.ps1 -InputPath 'D:\comma2k19\Chunk_1.zip' -Count 3
```

`-Count 0`または省略で全区間です。ZIP内の`|`はWindows用に`_`へ置換します。ZIPは全体展開するためZIPと展開先の両方の容量が必要ですが、再実行時はサイズの一致する展開済みファイルを再利用します。

今回の確認では、未変換だった取得済みChunk 1から188区間を追加しました。既存2区間とCivic確認用1区間を合わせ、このPCのScenariosには191区間あります。これらのアセットはGit管理対象外です。

### もっとコンパクトにできる？

- **取得量を減らす**：まず約45 MBの公式サンプルを使う。大きいチャンクは必要な方だけ取得する。
- **再生PCへ運ぶ容量を減らす**：変換済み`Scenarios`だけをコピーする。生ZIP・HEVC・Python・DBC生成ツールは再生PCへ持っていく必要がない。
- **動画を小さくする**：セットアップはCRF 28・最大幅1280でH.264へ変換する。CAN ID・値・時刻・フレーム数は変更しない。

今回の公式サンプルでは、既存動画24.8 MB → コンパクト動画9.2 MB、パッケージ全体約12.2 MBでした。内容と画質設定で容量は変わります。元解像度／従来品質で作る場合は次のように指定します。

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\prepare_scenarios.ps1 -Crf 20 -MaxWidth 0 -Rebuild
```

**アプリには、大きいZIPの中から任意の1分だけを通信量少なく取得する機能は未実装です。** `-Count`は変換する数を制限するもので、ZIPのダウンロード量を減らす設定ではありません。実装候補はHTTP Rangeを使うZIP内ファイル取得、または変換済み小容量パッケージのRelease配布です。

### comma2k19の全データが選択肢に出ている？

はい。取得画面に全10チャンクを表示します。公開データは**2019個の約1分区間、合計33時間超**で、Chunk 1–2がToyota RAV4、3–10がHonda Civicです。

標準のbuild処理は走行ルートの車両IDからRAV4/Civicを判別し、Civicには`honda_civic_touring_2016_can_generated.dbc`と`acura_ilx_2016_nidec.dbc`を使用します。既定バスは主となる車両DBCで選び、レーダーのID数が多いだけでレーダーバスを選ばないようにしています。Civicの1区間で車速・操舵角・車輪速をprocessed_logと照合済みです。全区間・全信号の意味を検証済みという意味ではありません。

| 範囲 | 取得容量の目安 | 用途 |
|---|---:|---|
| 公式サンプルの必要部分 | 45 MB | セットアップ確認、1分の再生 |
| Chunk 1 | 8.73 GB | RAV4の走行記録 |
| Chunk 2 | 9.05 GB | 別のRAV4走行記録 |
| Chunk 3–10 | 各約9.3〜9.9 GB | Civicの走行記録 |
| 全10チャンク | 約94.6 GB | RAV4＋Civic |

動画・生CANに加え、IMU、GNSS、処理済み車速・舵角・車輪速・レーダー、カメラ姿勢・位置・フレーム時刻などがあります。このアプリが利用するのはその一部です。道路・天候を網羅した全車種データ集ではありません。

出典：[comma.ai公式README](https://github.com/commaai/comma2k19)、[公式ミラーのファイル一覧](https://huggingface.co/datasets/commaai/comma2k19/tree/main/raw_data)。

## 設計と開発

| ディレクトリ | 役割 |
|---|---|
| `windows/src/CANVideoEmulator.Core` | 共通再生時計、固定長CANタイムライン、送信スケジューラー |
| `windows/src/CANVideoEmulator.Scenarios` | パッケージ、プレイリスト、再生セッション、取得 |
| `windows/src/CANVideoEmulator.Pcan` | PCAN-Basic、USB検出、状態・診断 |
| `windows/src/CANVideoEmulator.Video` | WPF MediaElementによる動画再生 |
| `windows/src/CANVideoEmulator.Wpf` | 画面、設定、ログ |
| `scenario_builder` | 生ログ抽出、動画変換、DBC生成結果の利用、検証 |
| `android/candecoder` | KotlinのCANデコードと値の失効判定 |
| `android/app` | Compose表示アプリ。ハードウェア入力アダプターは未実装 |
| `scripts` | セットアップ、サンプル取得、変換、Release作成 |
| `assets` | 固定コミットのサンプル取得マニフェスト |

動画とCANは`Stopwatch`由来の共通時計を参照します。動画の開始オフセットは記録フレーム時刻から求め、再生中のずれを補正します。ただしWPF/Windowsのソフトウェア再生なので、フレーム単位やマイクロ秒単位の物理送信同期を保証する構成ではありません。デフォルトの動画補正許容幅は250 msです。

```powershell
# .NET 10 SDKがPATHにある場合。ローカルSDKなら .\.tools\dotnet\dotnet.exe を使う
dotnet test .\windows\CANVideoEmulator.slnx -c Release
.\.venv\Scripts\python.exe -m pip install -e '.\scenario_builder[dev]'
.\.venv\Scripts\python.exe -m pytest .\scenario_builder\tests -q
```

RAV4の多様な区間を選ぶ場合は、まず`python -m scenario_builder analyze --input <展開先> --dbc-dir <DBCフォルダー> --output analysis.json --count 20`で分析し、`build-selected`へそのJSONを渡します。`--max-width`と`--crf`は`build`・`build-selected`両方で利用できます。別の入力を同じ出力先へ追加する場合は異なる`--prefix`を使ってください。プレイリストは出力先の全シナリオから再作成されるため、手編集したプレイリストは事前に退避してください。

Androidは任意の補助プロジェクトです。`android/gradlew.bat :candecoder:test`でデコーダーをテストします（JDKが必要。SDKがない場合は`CANREPLAY_SKIP_ANDROID_APP=1`）。実機入力の`CanSourceFactory.createHardwareSource()`は現在nullを返すため、実際のブリッジとの接続実装は別途必要です。

配布物の作成は`scripts/build_release.ps1`、インストーラーはInno Setupを使います。ファイル形式は[docs/scenario_package.md](docs/scenario_package.md)、依存物は[THIRD_PARTY.md](THIRD_PARTY.md)、解析結果と改善優先順位は[docs/REVIEW.md](docs/REVIEW.md)にまとめています。

## 設定・ログ・既存フォルダー名

`bin/portable.txt`がある場合、設定はEXEと同じ場所の`config.json`、ログは`logs`です。通常インストールでは`%APPDATA%\CANVideoEmulator`を使い、旧`CanReplayPlayer/config.json`を初回読み込み時に引き継ぎます。シナリオの保存先が変わった場合はChange Folderで選び直してください。

既存のローカルフォルダーが`PCANUSBDemo`の場合は、プレーヤー・ターミナル・Codexなど、そのフォルダーを開いているアプリを終了してから、親フォルダーで次を実行できます。

```powershell
powershell -ExecutionPolicy Bypass -File .\PCANUSBDemo\scripts\rename_workspace.ps1
```

改名後は`CANVideoEmulator`を開き直し、必要なら`setup.ps1 -WithExample`を再実行してください。リモートURLは既に`NaitoMitsuharu/CANVideoEmulator`です。
