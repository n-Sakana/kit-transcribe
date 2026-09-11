# Transcribe

Windows x64 / Windows PowerShell 5.1 用の独立したローカル日本語文字起こしツールです。Tool Rack Host、Python、クラウドの音声認識 API は不要です。

## モデルと操作

| 用途 | モデル | 実行タイミング |
|---|---|---|
| リアルタイム | Whisper small（多言語版、ONNX int8） | 「録音開始」後、発話区間ごとに表示 |
| 清書 | Whisper large-v3-turbo（ONNX int8。配布ファイル名は `turbo-*`） | 録音を停止し、残りの処理が終わってから「清書を実行」 |

どちらも日本語 (`ja`) の文字起こし (`transcribe`) として CPU で動作します。リアルタイムは真のストリーミングモデルではなく、VAD による区切り（無音、または最大約6秒）ごとに small で推論します。清書は保存済み音声全体を最大約25秒の発話区間で再認識します。文章の要約や LLM による校正ではありません。

モデル検証・読み込み、録音、推論は UI スレッドから分離しています。small と large-v3-turbo は同時に保持せず、各処理の終了時に解放します。性能・表示遅延は CPU、発話長、空きメモリに依存し、常に実時間より速いことを保証するものではありません。未処理音声の秒数を画面に表示します。

## 初回準備

このフォルダを、ユーザーが書き込める固定の場所へ展開してください。

1. `setup-models.cmd` を実行して両モデルを取得します（合計約1.4 GB）。インターネット接続が必要なのはこの取得時だけです。リアルタイム用だけ先に取得する場合は `setup-models.cmd small`、清書用を追加する場合は `setup-models.cmd turbo` を実行します。
2. 右クリックメニューも使う場合は `install.bat` を実行します。現在のユーザーの HKCU にだけ登録します。
3. `transcribe.vbs` または右クリックメニューの `Transcribe` から起動します。診断用のコンソール付き起動は `transcribe.cmd` です。

取得元は sherpa-onnx の公式ドキュメントで案内されている ONNX 配布リポジトリです。リビジョンを固定し、モデルの SHA-256 とトークン表の形式・件数を検証します。ダウンロード途中のファイルをモデルとして使いません。破損時も同じセットアップコマンドで再取得できます。大きな Whisper モデルは Git に同梱しません。旧 Zipformer の同梱ファイルは残っていますが使用・結合しません。

Windows 11 の右クリック登録は従来形式です。新しいメニューに出ない場合は「その他のオプションを表示」から開いてください。Windows Script Host がポリシー等で無効の場合は `transcribe.cmd` を使ってください。32-bit ホストからのランチャー起動も 64-bit Windows PowerShell へ誘導します。PowerShell 7 (`pwsh`) は対象外です。

## 録音と清書

「録音開始」で small の検証・読み込みを行い、画面が「録音中」に変わった時点からマイクを取得します。読み込み中はまだ録音していません。「録音停止」後は最終発話まで処理します。録音中・停止処理中・清書中は別の処理を開始できません。

録音停止後の「清書を実行」で large-v3-turbo を読み込み、音声を再認識します。リアルタイムと清書は別タブです。清書が失敗・中止した場合は、既存の文字起こしと保存済み音声を保持します。清書は手動で再実行できます。「処理を中止」は現在のモデル読み込み／音声区間の推論が戻った後に反映され、ネイティブ推論を強制終了しません。中止した録音のリアルタイム結果は途中までになる場合がありますが、収録済みの音声は清書に使えます。

「コピー」「クリア」「保存」は表示中のタブが対象です。録音を新しく開始する際は、既存表示を消してよいか確認します。次の録音が実際に開始するまでは表示を消しません。ウィンドウを閉じると処理の中止を要求し、UI を固めずにリソース解放を待ちます。

## 保存先とプライバシー

**この版から音声自体も保存します。** 清書に必要なため、16 kHz / mono / PCM16 の WAV を `output/recordings/recording_<日時>_<一意ID>.wav` に逐次保存します。感度調整後の音声です。認識が遅れても録音音声をメモリキューから捨てず、ディスクに蓄積します。空き容量が足りない場合はエラーを表示して録音を止めます。単一録音の上限は24時間です。

音声は終了・クリア・アンインストール時にも自動削除しません。不要な録音は利用者が削除してください。音声認識中に録音や文字起こしを外部送信する処理はありません。音声は約115 MB/時で、ディスク空き容量と機密情報の保管に注意してください。

「保存」で書き出す文字起こしは `output/transcribe_<日時>_<一意ID>.txt`（UTF-8 BOM付き）です。文字起こしテキストは自動保存されないため、終了／新規録音前に必要なタブを保存してください。保存先は右クリックした場所やカレントディレクトリに依存しません。

起動・実行エラーは `%LOCALAPPDATA%\pub-transcribe\logs\transcribe_yyyyMMdd.log` に記録します（取得できなければ一時フォルダ配下）。ログに音声や認識テキストは記録しません。DLL が欠損・破損している場合は元の配布ファイルを復元してください。

## 検証

Windows x64 の Windows PowerShell 5.1 で実行します。

```powershell
# 構文、依存ファイル、背景メニュー定義
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test\check.ps1
# WPF と C# の起動確認（モデルの読み込み・マイク録音なし）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test\check.ps1 -Runtime
# 偽の認識器を使う回帰テスト（モデル取得・マイクなし）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test\engine.ps1
# トークン表の末尾マーカーとファイルロックの回帰テスト
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test\token-validation.ps1
# 同梱ネイティブ DLL と Silero VAD の実行確認（マイクなし）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test\native-vad.ps1
# モデル取得後、両モデルのロード・無音入力でのネイティブ推論を確認
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test\model-runtime.ps1
# 任意の16kHz/mono/PCM16のWAVで両モデルの認識を確認
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test\model-runtime.ps1 -Wav .\sample.wav
```

GitHub Actions の `Windows checks` は構文・WPF・回帰テスト・ネイティブ VAD を実行します。`Whisper model smoke` はモデル関連の変更時に約1.4 GBの重みを取得し、両モデルの読み込みと無音入力でのネイティブ推論を確認します。無音入力テストは初期化・推論経路の確認であり、日本語の認識精度を保証するものではありません。実マイク録音は CI では行いません。実機ではマイク権限、録音開始／停止、長い連続発話、音声と文字の対応、清書、清書再試行、コピー／保存、録音中の終了を確認してください。`test/check.ps1 -Registry` は本番登録とは別の一意なテストキーで右クリック登録の再登録・解除を検証します。

`SHA256SUMS.txt` は Git に保存したバイト列のハッシュです。Windows のチェックアウト時の CRLF 変換後のファイルとは、テキストファイルのハッシュが異なる場合があります。ダウンロードする Whisper 重みのハッシュは `src/app/model/SOURCE.md` とセットアップスクリプトで管理します。

## 削除と移動

`uninstall.bat` はこのコピーが登録した背景メニューだけを解除し、文字起こし・音声・モデル・他ツールの登録を削除しません。配置を変えたら移動先で `install.bat` を再実行してください。古いコピーの解除は、新しいコピーの登録を残します。

元の Tool Rack を残す場合、その Transcribe は別登録として残ります。独立版は元リポジトリや既存データを自動変更せず、ホットキーも移植しません。ライセンスと取得元は `src/app/THIRD-PARTY-NOTICES.md`、`src/app/model/SOURCE.md`、`src/app/licenses/` を参照してください。
