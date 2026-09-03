# 展示・再生PCへの導入

ソースから導入する場合は、[READMEの最短セットアップ](README.md#最短セットアップ別のpcでcloneして使う)を参照してください。

## 変換済みのアプリを別PCへコピーする

1. 作成元PCで`scripts/setup.ps1 -WithExample`を実行します。
2. 配布先フォルダーへ`bin/CANVideoEmulator.exe`と`bin/portable.txt`をコピーします。
3. その横に、作成済みの`Scenarios`フォルダーをコピーします。

```text
配布先/
  CANVideoEmulator.exe
  portable.txt
  Scenarios/
    playlist.json
    rav4_.../
      scenario.json
      video.mp4
      can/
      dbc/
```

このEXEは.NETを含みます。再生先にはPython・FFmpeg・opendbc・生のcomma2k19 ZIPは不要です。設定を引き継ぐ場合、`config.json`内の絶対パスが作成元PCを指していないか確認してください。見つからない場合は起動後にChange Folderでシナリオの保存先を選び直します。

## 実CAN出力を使う場合

[PEAKのWindowsドライバー](https://www.peak-system.com/quick/DrvSetup)をインストールし、PCAN-Basic APIも導入します。USBを接続すると約2秒で検出され、1台の場合は自動選択されます。検出だけでは送信しません。

受信機の電源、配線、終端、通信速度を確認し、`Transmit to PCAN-USB`は起動時ONです。PLAYで初期化して送信します。未接続で動画だけ試す場合はOFFにしてください。

`USB Ready`は検出済み、`Connected`は初期化済みを示します。どちらも受信側への到達証明ではありません。必要に応じて独立した受信機で到達を確認します。[HARDWARE_TEST.md](HARDWARE_TEST.md)を参照してください。

## トラブル時

- USBの抜き差しは約2秒で自動反映。RefreshとTest Connectionボタンは削除しました。
- 抜去すると一時停止し、チャンネルを解放します。送信チェックは保持し、再接続後はPLAYで再初期化します。
- USBの先のCANケーブルが未接続でも初期化に成功する場合があります。送信時のBus Errorsと受信側カウンターを確認します。
- BUS OFFなら配線・速度・終端を修正後、PLAYで再初期化します。他アプリがチャンネルを使っている場合は終了してください。
- シナリオがない場合はGet Scenariosでサンプルまたはチャンクを選びます。clone上ではStep 2の取得後にStep 3の変換・一覧更新が自動実行されます。Python・FFmpegが必要です。取得済みZIPはConvert Selectedで変換できます。
- ログはportableモードではEXE横の`logs`、通常は`%APPDATA%/CANVideoEmulator/logs`です。Diagnosticsで詳細を確認できます。

変換前の依存チェック: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\prepare_scenarios.ps1 -CheckOnly`。Get Scenariosからも取得前に実行します。詳細は[README](README.md)の「別PCへ持ち込むもの・事前確認」を参照してください。
