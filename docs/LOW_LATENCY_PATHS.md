# HomePodの別音声経路調査（Issue #2）

この文書と実験CLIは、受信機が受理するSETUP宣言を比較するためのものです。
新しい低遅延経路の発見、別経路での出音、20〜50 msの総遅延はまだ確認していません。
既存のGUIアプリの送信経路とALAC encoderは変更しません。

## ソースから確認できたこと

| 観察 | 根拠 | ここからは判断できないこと |
| --- | --- | --- |
| 現行Win2HomePodはtype=96 / ct=2 / audioFormat=0x40000 / spf=352 / 44.1 kHz / PTP | vendorのRaopSession.cs | HomePodが持つ全経路 |
| pyatvのAirPlay v2実装はtype=96 / ct=1（Raw PCM）/ audioFormat=0x800 / spf=352 / 44.1 kHz / NTPを宣言する | [固定コミットの実装](https://github.com/postlund/pyatv/blob/b277a4c8222ecdcbaab8a24e3e713ca44765adb4/pyatv/protocols/raop/protocols/airplayv2.py) | LPCMでHomePodのrender pipelineや遅延が変わるか |
| GET /infoにはfeatures・model・sourceVersion等がある | [airplay-specのGET /info](https://github.com/openairplay/airplay-spec/blob/00063da0e7f16c20fbfb5d29331334da1a89590a/src/audio/rtsp_requests/get_info.md) | feature bitだけで隠れた低遅延経路を特定できるか |
| NTP/ALACの外部実装ではarrivalToRenderLatencyMs=70という観測が報告されている | [JITTER.md](https://github.com/iakacer/windows-airplay-homepod/blob/master/JITTER.md) | 全機種・全OS・PTP・他経路に共通する70 msの固定下限か |

資料と既存enumには一部feature bitの呼称・位置に差があります。
比較データは符号を含む元の値と64-bit hex、TXTの下位/上位wordを保持し、
名前付きflagだけから未確認のcapabilityを推定しません。
AAC-ELD / Opusや別stream typeは、ここではIDや対応audioFormatを推測してプリセット化しません。
送信元の観測または受信機応答を得た値を使います。

## ビルドとオフライン確認

```powershell
./build-probe.ps1
./probe-app/Win2HomePod.Probe.exe --help
./probe-app/Win2HomePod.Probe.exe plan --output .state/plan-alac.json
./probe-app/Win2HomePod.Probe.exe plan --codec 1 --audio-format 0x800 --timing ntp --output .state/plan-pcm.json
```

`plan`・`import-info`・`compare`はネットワークを使わず、Linuxの.NET 10でも実行できます。
`plan`はSETUPの宣言だけを作ります。音声encoderを構成するものではありません。
`--latency-min` / `--latency-max`は**sample frames**です。6615 frames @ 44100 Hz = 150 msです。
msをそのまま渡さないでください。`--codec`はSETUPの`ct`を表す数値で、十進または`0x`の十六進を指定します。
`--audio-format`もcodec・sample rate等と整合する観測値を指定します。
妥当性の検証は値の範囲と構文までで、受信機の対応を保証しません。
既存ファイルは上書きしないので、各実験では別の出力名を使います。

## 実機でのcapability保存とSETUP probe

Windowsと受信機を同じLANへ接続し、以下の`<receiver IPv4>`を実際のアドレスへ置き換えます。
CLIのライブ操作はWindowsのみです。

```powershell
./probe-app/Win2HomePod.Probe.exe capture --host '<receiver IPv4>' --output .state/capabilities.json
./probe-app/Win2HomePod.Probe.exe probe --host '<receiver IPv4>' --output .state/probe-alac.json
./probe-app/Win2HomePod.Probe.exe probe --host '<receiver IPv4>' --codec 1 --audio-format 0x800 --timing ntp --output .state/probe-pcm-ntp.json
```

- `capture`: GET /infoを読み、既定2秒間のmDNSで同じIPv4のTXTを照合します。pairingやSETUPは行いません。
- `probe`: 既存のGET /info → transient pair-setup → session SETUP → RECORD → PTP時はSETPEERS → stream SETUPを使い、直後にTEARDOWNします。
- `--stream-type` / `--codec` / `--audio-format` / `--spf` / `--sample-rate` / `--audio-mode` / `--latency-min` / `--latency-max` / `--timing`で宣言を変えられます。
- audio pump・WASAPI取得・RTP音声送信・Windows出力切替は始めません。受信機のセッションは一時的に確保するため、通常再生を停止してから実行します。
- `--port`は既定7000、`--timeout-seconds`は既定45秒です。TEARDOWN・終了待ちは別に最大数秒かかります。
- mDNSが使えない場合は`MdnsStatus`で欠落を表します。`--discover-ms 0`でmDNSを省略できます。
- PIN等が必要な受信機はpairing段階で失敗として記録します。今回のCLIは既存の認証制御を回避せず、保存済みcredentialの読み書きもしません。
- 終了コードは0=宣言生成/capture成功/SETUP完了、1=ライブ試験のreject・timeout・失敗、2=入力・ファイル等のエラーです。

JSONには要求パラメータ、応答順序、RTSP status、失敗段階、capability、受信機latencyの生値、TEARDOWN結果を残します。
name・IP・device ID・公開鍵/共有鍵・生のpairing payload・バイナリTXTは保存対象から外します。
TXTはcapabilityに使うキーだけを保存します。モデル・OS等の情報は残るため、共有前に内容を確認してください。
受信機latencyは名前と生値のまま比較し、単位が不明な値をmsへ変換しません。
`StreamSetupAccepted=true`でも、応答plistの解析失敗や後続のport不足は`Outcome`で区別します。
`AudibleOutput`・安定性・複数台の結果は`not-tested`、`MeasuredOutputLatencyMs`はnullです。

## Apple senderとの比較

macOS / iOSから、Music・システム音声・動画・ミラーリング等のケースを個別に記録します。
RTSP順序、SETUP plist、type/ct/audioMode/spf/sr/audioFormat、latency fields、RTP payload type、
PTP/NTP、sync mappingを並べて、ひとつずつ差を比較します。
ペアリング後の制御通信は暗号化されるため、pcapだけでSETUP内容が読めるとは限りません。
復号済みのGET /info plistを入手した場合、以下で比較用データへ取り込めます。
CLIはpcapの復号やApple端末からの自動収集は行いません。

```powershell
./probe-app/Win2HomePod.Probe.exe import-info --input info.bplist --txt-json txt.json --output .state/imported.json
./probe-app/Win2HomePod.Probe.exe compare --left .state/capabilities.json --right .state/imported.json --output .state/comparison.json
```

TXTファイルは`{"airplay":{"features":"0x200,0x200"},"raop":{"cn":"0,1,2"}}`形式です。
`compare`は要求・capability・応答・結果の差分を並べます。遅延の優劣は判定しません。

## 実験マトリクスと残る作業

| ケース | 変更 | 判定 |
| --- | --- | --- |
| 現行基準 | 96 / ALAC / 0x40000 / 352 / 44100 / PTP | 実機2台でSETUP 200、arrivalToRenderLatencyMs=69 |
| PCM・NTP | ct=1 / audioFormat=0x800 / NTP（pyatvを比較元にする） | 実機2台でSETUP 200、arrivalToRenderLatencyMs=70。音声送信は未実装 |
| PCM・PTP | 上記からtimingだけ変更 | 実機2台でSETUP 200、arrivalToRenderLatencyMs=69。音声送信は未実装 |
| 小packet | 基準からspf=176または88だけ変更 | 両方とも実機2台でstream SETUP 400。対応音声送信は未実装 |
| その他のtype/mode/codec | Apple sender等で観測した値 | 観測値未取得 |

probeの200 OKは出音や低遅延の証拠ではありません。次段階では受理された宣言に対応するencoder・
packetization・syncを実装し、連続再生の再送/損失、安定性、stereo pair/multi-roomと外部の音響測定を行います。
この時点では「別経路が速い」「70 msが固定下限」のどちらも結論にしません。
CIは設定・plist・redaction・応答分類・ローカルの模擬RTSP受信機とCLIのオフライン動作を検証します。
実HomePodの認証と全SETUP成功シーケンスはCIで再現していません。

2026-10-18の実機結果と試験範囲は[REAL_DEVICE_TESTS.md](REAL_DEVICE_TESTS.md)を参照してください。
