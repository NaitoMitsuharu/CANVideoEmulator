# BUS OFF調査（2026-09-03）

## 確認した事実

- プレーヤーログの14:17:39にPCAN_USBBUS1を500 kbit/sで有効化し、14:17:51にドライバーが `the CAN controller is in bus-off state` を返しています。今回のログは実際のBUS OFFです。
- `spresense-SDK`の`feature/gm6020-proto-control`はoriginをfetchし、`751b1f06`から`bd22b4b5`へfast-forwardしました。作業内容の破棄やファームウェアの書き換えはしていません。nuttxはSDKが参照する`ef3093f064`です。
- `sprbox-SDK`と`sprbox-SDKDemo`も同ブランチのoriginをfetchし、最新であることを確認しました。
- Android Demoの通常CANモードの初期設定は1M、CAN EmulatorのStartは共有設定を500kへ変更します。500kの設定値はbaud=500000 / TSEG1=5 / TSEG2=2 / SJW=1、CANポートは0です。
- SDKはこれをCAN制御要求に変換し、Spresenseで `can_uplink -q -p0 set_bit_timing ...` を実行します。`can_uplink`は`can_controller_main`を呼ぶラッパーです。受信開始はCANのライフサイクル処理を通ります。
- MCP2515ドライバーのKconfig既定値は500000 bit/s・発振子8 MHz。`can_controller start`自身は速度を指定しません。実際に書き込まれたファームウェアの設定と基板の発振子は、このPCからは確認できていません。

## 判断と確認手順

**通常CANモードが1Mだった場合、PCの500kと一致しません。** 一方、CAN Emulatorを500kで開始済みなら、その説明だけではBUS OFFの原因を特定できません。終端・配線・電源・発振子設定の確認が必要です。

1. AndroidでCAN Emulatorを開き、停止中の状態から **Start · 500k** を押します。通常CANモードなら設定を500kにします。
2. PCでCAN BUS=0、BITRATE=500000、Transmit to PCAN-USB=ONを確認しPLAYします。修正版はBUS OFF後のPLAYで再初期化します。同じPCANチャンネルを使うPCAN-Viewなどは先に終了してください。
3. AndroidでRX件数またはRaw CANの受信が増えるか確認します。ゲージや車種判定よりも、まずRaw CANが届いているかを見ます。
4. 届かなければ、CAN-H/CAN-L/GND、受信機の電源と通常動作モード、両端120 Ω終端を確認します。PCAN-USBの内蔵終端は標準で有効とは限りません。[PEAKマニュアル](https://www.peak-system.com/produktcd/Pdf/English/PCAN-USB_UserMan_eng.pdf)
5. 発振子が16 MHzの基板に8 MHz前提のファームウェアを使うと、設定表示が500kでも実際のビット速度が変わります。水晶の刻印とビルド設定を照合します。ソフトウェアのGet Bit Timing値だけでは発振子実周波数を証明できません。

IMU受信はSpresense→AndroidのUSB経路が機能している証拠ですが、PCAN→MCP2515のCAN電気接続やACKまでを確認したことにはなりません。今回のPCにはSpresenseのCOMポートが見えていないため、実機のレジスター取得・Androidの受信確認は未実施です。

実機コンソールが使える場合の補助情報は `can_controller -p0 get_bit_timing` と `can_controller -p0 get_status`。後者はCANSTAT/CANCTRL/EFLG/TEC/RECなどを返します。受信開始ログに `Failed to initialize message queue` やCANデバイスopen失敗が出ていないかも確認します。

## アプリで修正した点

- Error PassiveをBUS OFFと誤表示していた判定を分離。
- PCANの状態はビットフラグとして判定し、キュー満杯などと組み合わされたBUS OFFを見落とさないよう変更。[PEAKの状態コード仕様](https://www.peak-system.com/documentation/API/PCAN-Basic.Net/html/cf38e0f8-6667-b57b-71eb-0b7b50e16eb9.htm)
- 送信開始前と送信失敗直後に状態を確認し、利用不能な場合はスケジューラー自身が送信を止めるよう変更。UIの次の更新を待ちません。動画も一時停止します。
- 原因を修正してPLAYを押すと異常チャンネルを解放・再初期化して状態を再確認。失敗中に自動再送を繰り返す動作はしません。
- 通常の`Api.Reset`はキューを消すだけでコントローラーを復旧させないため、復旧用と誤記された未使用メソッドを除去。再初期化によるハードウェアリセットには他クライアントの終了が必要です。[PEAK Reset仕様](https://www.peak-system.com/documentation/API/PCAN-Basic.Net/html/1af8f42e-3b76-125a-4881-2ebcb27032ff.htm)
- 異常時に生の状態コード・チャンネル・速度・シナリオ・バス・API受付数をログへ記録。

これらは異常の表示・停止・復旧の修正です。未確認の物理接続をソフトウェアで修復したという意味ではありません。Android・Spresenseのソースは参照／同期のみです。
