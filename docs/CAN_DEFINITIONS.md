# CAN IDとデータの定義：受信側でどう読むか

CANVideoEmulatorの責務は、動画に合わせて記録CANをPCAN-USBから出力するところまでです。受信機や受信アプリの種類は問いません。信号名や単位はCANフレーム自身には含まれないため、受信側に対応する定義を別途用意します。

## 定義ファイルの場所

変換済みシナリオの`dbc`フォルダーにあります。現在の小容量サンプルなら次の3ファイルです。

```text
Scenarios/rav4_40_001/dbc/
  signals.json                     受信アプリ用のJSON定義
  toyota_new_mc_pt_generated.dbc    ToyotaのCANメッセージ・信号定義
  toyota_adas.dbc                   運転支援系のCANメッセージ・信号定義
```

`Scenarios/rav4_001/dbc`にも同じ用途の定義があります。フォルダー名はシナリオ生成方法で変わるため、**実際に再生するシナリオに付属したファイル**を使ってください。ファイルはシナリオ生成時に作られ、clone直後のGit管理ファイルには含まれません。

- `.dbc`：opendbc由来の定義。CAN IDとメッセージ名、各信号のビット位置・長さ・符号・エンディアン・倍率・オフセット・単位・列挙値を定義します。
- `signals.json`：BuilderがDBCと記録CANから生成した、既存Kotlinデコーダー用の定義。DBCパーサーをAndroidへ追加せずに読み込めます。
- `scenario.json`：再生するバス、車種、動画、再生通信速度などのパッケージ情報です。CAN信号のビット定義は`signals.json`側です。
- `validation.json`：一部信号をcomma2k19の処理済み値と比較した結果です。全IDの意味が保証されたという意味ではありません。

## 今回のRAV4 / Bus 0の例

これはこのパッケージの定義であり、全車種共通のID表ではありません。

| CAN ID（16進） | 10進 | メッセージ名 | 主な信号 |
|---|---:|---|---|
| `0x025` | 37 | `STEER_ANGLE_SENSOR` | 操舵角、角度の小数成分、操舵速度 |
| `0x0AA` | 170 | `WHEEL_SPEEDS` | 前後左右の車輪速（km/h）と異常フラグ |
| `0x224` | 548 | `BRAKE_MODULE` | ブレーキ操作状態、圧力に関する値 |
| `0x2C1` | 705 | `GAS_PEDAL` | アクセル開度（%）など |
| `0x3BC` | 956 | `GEAR_PACKET` | ギア状態など |

**1つのCAN IDに複数の信号が入っています。** IDからメッセージ定義を選び、ペイロード内のビットを各信号の定義どおりに取り出します。

```text
CAN ID → messages[].can_id
       → start_bit / length / byte_order / signed に従って整数値を抽出
       → 物理値 = 抽出値 × factor + offset
       → unitで単位を確認、valuesで列挙値のラベルを確認
```

例えば`WHEEL_SPEED_FL`の倍率は0.01、オフセットは−67.67、単位はkm/hです。単純に8バイト全体を整数へ変換しても車輪速にはなりません。ビッグエンディアンのビット配置も含め、下記の既存デコーダーを使えます。

このサンプルの`signals.json`には既知の44メッセージがあり、残る61個のIDは`undecoded_can_ids`に列挙されています。未知IDもプレーヤーは記録どおり送信しますが、この定義だけでは意味を解釈できません。受信側では生データとして保持・表示し、推測で信号を割り当てないでください。

## 既存Androidプロジェクトで使う

実装は既にあります。

| ファイル | 役割 |
|---|---|
| `android/candecoder/.../SignalDefinitions.kt` | `SignalDatabase.parse()`でJSONを読み込む |
| `android/candecoder/.../CanDecoder.kt` | ビット抽出、符号処理、倍率・オフセット適用 |
| `android/candecoder/.../VehicleProfile.kt` | どの信号を速度計・操舵角などの表示へ割り当てるか |
| `android/app/.../ViewerViewModel.kt` | APKのassetsからJSONをロードする |
| `android/app/build.gradle.kts` | シナリオのJSONをAPKのassetsへコピーする |

`android/app/build.gradle.kts`は次の順にシナリオの場所を決めます。

1. `android/local.properties`の`scenarioDir`
2. 環境変数`CANREPLAY_SCENARIO_DIR`
3. 既定値`Scenarios/rav4_001`

別のサンプルを指定する場合は、例えばPowerShellで次のようにビルドします。Android SDKとJDKなどのビルド環境は別途必要です。

```powershell
$env:CANREPLAY_SCENARIO_DIR = (Resolve-Path .\Scenarios\rav4_40_001).Path
.\android\gradlew.bat -p .\android :app:assembleDebug
```

`local.properties`に`scenarioDir`が既にある場合はそちらが優先されます。ビルド時にコピーされたJSONは、APK内の`assets/profiles/toyota_rav4_2017/signals.json`に入ります。Debug/Releaseの両方に含まれます。

独自Androidアプリで既存の`candecoder`モジュールを利用する場合の読み込み・デコード例です。

```kotlin
import jp.co.canreplay.candecoder.CanDecoder
import jp.co.canreplay.candecoder.RawCanFrame
import jp.co.canreplay.candecoder.SignalDatabase

val database = context.assets
    .open("profiles/toyota_rav4_2017/signals.json")
    .bufferedReader().use { SignalDatabase.parse(it.readText()) }

// frame: 受信したID、標準/拡張フラグ、ペイロード、単調増加時刻を格納したRawCanFrame
fun decodeReceived(frame: RawCanFrame) {
    val definition = database.message(frame.canId)
        ?.takeIf { it.extended == frame.extended }
        ?: return // 未定義IDは別途Raw CAN表示・保存へ渡す
    val signals = CanDecoder.decode(definition, frame)
    // signalsのsignalName/value/unitなどを自分の画面や処理へ渡す
}
```

定義ファイルはPCAN-USBやCAN経由では自動送信されません。受信アプリへ事前に同梱・コピーしてください。中継機を使用する場合も、意味の解釈は受信アプリ側だけで行えます。

## バス・車種の対応に注意

Get Scenariosの標準変換はCivicにも対応しました。Civicの主DBCは`honda_civic_touring_2016_can_generated.dbc`、レーダーは`acura_ilx_2016_nidec.dbc`です。確認用の`Scenarios/civic_sample_001/dbc/signals.json`はCivic Bus 0の定義です。既存AndroidのToyota用表示プロファイルをそのままCivicへ適用せず、Civicの定義と表示マッピングを受信側へ用意してください。

現在のBuilderが作る`dbc/signals.json`は、そのシナリオの**デフォルトバス1つ分**です。今回のサンプルではJSON先頭の`bus`が0です。プレーヤーでBus 1に変えても、JSONが自動でBus 1用になるわけではありません。

別の車種・別のバスを処理する場合は、その記録に対応するDBCと信号定義を準備します。再生は未知IDを含めて可能ですが、正しくデコードできる範囲とは分けて考えてください。既存Androidのプロファイル選択も受信側で行い、Windowsから独自の車種通知フレームは出力しません。
