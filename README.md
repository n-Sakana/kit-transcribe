# Transcribe

Tool Rack から切り離した、Windows x64 / Windows PowerShell 5.1 用の独立したローカル日本語文字起こしツールです。このフォルダだけで動作し、Tool Rack Host、共通ランチャー、他のリポジトリは不要です。

## インストールと起動

固定の場所へ展開して `install.bat` を実行します。現在のユーザーの HKCU にだけ登録します。エクスプローラの空白またはデスクトップの空白を右クリックし、`Transcribe` を選ぶとコンソールを表示せず既存の WPF 画面を開きます。録音は画面の「開始」を押してから始まります。ファイル・フォルダ選択時のメニューには登録しません。

Windows 11 では従来形式のメニューへの登録です。新しいメニューに見えない場合は「その他のオプションを表示」から開きます。新しい簡略メニューへの COM 拡張ではありません。

直接起動は `transcribe.vbs`。コンソール付きの診断起動は `transcribe.cmd` です。Windows Script Host が管理ポリシー等で使えない環境では `transcribe.cmd` で起動してください。

## データと依存ファイル

本体、認識エンジン、DLL、分割モデル、モデルの説明、第三者ライセンスは `src/app/` にすべて同梱しています。初回のモデル結合・ハッシュ検証も既存実装を維持しています。音声認識は従来どおりローカルで実行します。

保存先はこのリポジトリ内の `output/transcribe_<日時>[_n].txt` です。右クリックした場所や PowerShell のカレントディレクトリには依存しません。音声そのものは保存しません。第三者ライセンスは `src/app/THIRD-PARTY-NOTICES.md` と `src/app/licenses/` を参照してください。

## 削除と移動

`uninstall.bat` はこのコピーが登録した Transcribe の背景メニューだけを解除します。保存した文字起こし、モデル、他ツールの登録は削除しません。配置を変えたら移動先で `install.bat` を再実行してください。古いコピーの解除処理は、新しいコピーの登録を残します。

## 検証

`powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\test\check.ps1` は構文・依存・メニュー定義を検証します。`-Registry` は一意のテスト用キーで登録・再登録・解除を確認します。`-Runtime` は既存の `-Smoke` で DLL・モデル・WPF の初期化を確認します。いずれもマイク録音は開始しません。

実機ではエクスプローラとデスクトップの空白、非表示コンソール起動、録音開始・停止、コピー・クリア・保存、終了を確認してください。

元の Tool Rack を残す場合、その中の Transcribe は別登録として残ります。独立版の確認後に `tool/transcribe` をバックアップして取り除き、`bindings.json` から Transcribe を呼ぶ binding を削除し、Tool Rack の `install.bat` を再実行してください。Tool Rack のグローバルホットキーは独立版へ自動移植しません。独立版は元リポジトリや既存の文字起こしを自動変更しません。
