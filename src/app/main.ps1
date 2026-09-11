# Windows PowerShell 5.1 / WPF. All capture and inference work runs on a C# worker.
param([string]$Target = '', [switch]$Smoke)
$ErrorActionPreference = 'Stop'
$script:closing = $false
$script:allowClose = $false
$script:seenSession = 0
$script:previousState = ''
$script:logPath = ''
$script:refinementCompleted = $false

function Write-TranscribeLog([string]$Message) {
    try {
        $base = [Environment]::GetFolderPath('LocalApplicationData')
        if (-not $base) { $base = [IO.Path]::GetTempPath() }
        $directory = Join-Path $base 'pub-transcribe\logs'
        [void][IO.Directory]::CreateDirectory($directory)
        $script:logPath = Join-Path $directory ('transcribe_' + (Get-Date -Format 'yyyyMMdd') + '.log')
        Add-Content -LiteralPath $script:logPath -Encoding UTF8 -Value ((Get-Date -Format o) + "`r`n" + $Message)
    } catch { [Console]::Error.WriteLine($Message) }
}
function Show-UiError([string]$Message) {
    Write-TranscribeLog $Message
    $script:refinementCompleted = $false
    $errorText.Text = "エラー: $Message`r`nログ: $script:logPath"
    $errorText.Visibility = 'Visible'
}
function Get-SelectedTextBox {
    if ($tabs.SelectedIndex -eq 1) { return $cleanTranscript }
    return $transcript
}
function Get-UniqueOutputPath([string]$Directory) {
    [void][IO.Directory]::CreateDirectory($Directory)
    return Join-Path $Directory ('transcribe_' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '_' + [Guid]::NewGuid().ToString('N') + '.txt')
}

try {
    . (Join-Path $PSScriptRoot 'bootstrap.ps1')
    Initialize-TranscribeEngine
    Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
    $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $outputDir = Join-Path $root 'output'
    $model = Join-Path $PSScriptRoot 'model'
    $engine = New-Object TranscriberEngine -ArgumentList $model, (Join-Path $outputDir 'recordings')
    $xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="文字起こし — Whisper" Width="850" Height="620"
        MinWidth="740" MinHeight="460" WindowStartupLocation="CenterScreen">
  <Grid Margin="14">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/><RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>
    <WrapPanel Grid.Row="0">
      <Button x:Name="StartButton" Content="録音開始" MinWidth="100" Height="34" Margin="0,0,8,4"/>
      <Button x:Name="RefineButton" Content="清書を実行" MinWidth="110" Height="34" Margin="0,0,8,4" IsEnabled="False"/>
      <Button x:Name="CancelButton" Content="処理を中止" Width="100" Height="34" Margin="0,0,16,4" IsEnabled="False"/>
      <Button x:Name="CopyButton" Content="コピー" Width="72" Height="34" Margin="0,0,8,4"/>
      <Button x:Name="ClearButton" Content="クリア" Width="72" Height="34" Margin="0,0,8,4"/>
      <Button x:Name="SaveButton" Content="保存" Width="72" Height="34" Margin="0,0,8,4"/>
    </WrapPanel>
    <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="0,6,0,10">
      <TextBlock Text="マイク感度" VerticalAlignment="Center"/>
      <Slider x:Name="GainSlider" Minimum="0.5" Maximum="4" Value="1" Width="160" Margin="10,0,16,0"/>
      <ProgressBar x:Name="LevelBar" Minimum="0" Maximum="1" Width="130" Height="14"/>
      <TextBlock Text="リアルタイム: small / 清書: large-v3-turbo" Margin="16,0,0,0" VerticalAlignment="Center" Foreground="DimGray"/>
    </StackPanel>
    <TabControl x:Name="Tabs" Grid.Row="2">
      <TabItem Header="リアルタイム">
        <TextBox x:Name="Transcript" AcceptsReturn="True" TextWrapping="Wrap" IsReadOnly="True"
                 VerticalScrollBarVisibility="Auto" FontSize="16" BorderThickness="0" Padding="8"/>
      </TabItem>
      <TabItem Header="清書">
        <TextBox x:Name="CleanTranscript" AcceptsReturn="True" TextWrapping="Wrap" IsReadOnly="True"
                 VerticalScrollBarVisibility="Auto" FontSize="16" BorderThickness="0" Padding="8"/>
      </TabItem>
    </TabControl>
    <TextBlock x:Name="ErrorText" Grid.Row="3" Foreground="Firebrick" TextWrapping="Wrap" MaxHeight="100" Margin="0,8,0,0" Visibility="Collapsed"/>
    <TextBlock x:Name="StatusText" Grid.Row="4" Text="準備完了。モデル未取得の場合は setup-models.cmd を実行してください。" Margin="0,8,0,0" TextWrapping="Wrap"/>
    <TextBlock Grid.Row="5" Text="音声は output/recordings にローカル保存します。清書は録音停止後に手動実行します。"
               FontSize="12" Foreground="DimGray" Margin="0,6,0,0" TextWrapping="Wrap"/>
  </Grid>
</Window>
'@
    $window = [System.Windows.Markup.XamlReader]::Parse($xaml)
    $startButton = $window.FindName('StartButton'); $refineButton = $window.FindName('RefineButton')
    $cancelButton = $window.FindName('CancelButton'); $copyButton = $window.FindName('CopyButton')
    $clearButton = $window.FindName('ClearButton'); $saveButton = $window.FindName('SaveButton')
    $gainSlider = $window.FindName('GainSlider'); $levelBar = $window.FindName('LevelBar')
    $transcript = $window.FindName('Transcript'); $cleanTranscript = $window.FindName('CleanTranscript')
    $tabs = $window.FindName('Tabs'); $statusText = $window.FindName('StatusText'); $errorText = $window.FindName('ErrorText')

    $timer = New-Object System.Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromMilliseconds(100)
    $timer.Add_Tick({
        try {
            if ($engine.SessionVersion -ne $script:seenSession) {
                $script:seenSession = $engine.SessionVersion
                $transcript.Clear(); $cleanTranscript.Clear(); $tabs.SelectedIndex = 0
            }
            [string]$text = ''
            while ($engine.TryGetText([ref]$text)) { $transcript.AppendText($text + "`r`n"); $text = '' }
            $transcript.ScrollToEnd()
            while ($engine.TryGetRefinedText([ref]$text)) {
                $cleanTranscript.Text = $text; $tabs.SelectedIndex = 1; $script:refinementCompleted = $true
                $statusText.Text = '清書が完了しました'; $text = ''
            }
            while ($engine.TryGetError([ref]$text)) { Show-UiError $text; $text = '' }
            $busy = $engine.IsBusy; $state = $engine.State
            $startButton.IsEnabled = (-not $script:closing) -and ((-not $busy) -or $state -eq 'Recording' -or $state -eq 'LoadingSmall')
            $startButton.Content = if ($busy -and $state -in @('LoadingSmall', 'Recording', 'Stopping')) { '録音停止' } else { '録音開始' }
            $refineButton.IsEnabled = (-not $script:closing) -and (-not $busy) -and $engine.HasRecording
            $cancelButton.IsEnabled = $busy -and (-not $script:closing) -and (-not $engine.IsCancellationRequested)
            $clearButton.IsEnabled = (-not $busy) -and (-not $script:closing)
            $gainSlider.IsEnabled = (-not $script:closing) -and $state -notin @('LoadingTurbo', 'Refining')
            $levelBar.Value = [double]$engine.LatestLevel
            if ($script:closing) {
                $statusText.Text = '終了処理中。現在のモデル処理が終わるまで音声を保持します。'
                if (-not $busy) { $engine.Dispose(); $script:allowClose = $true; $timer.Stop(); $window.Close() }
                return
            }
            switch ($state) {
                'LoadingSmall' { $statusText.Text = 'Whisper small を検証・読み込み中（まだ録音していません）...' }
                'Recording' { $statusText.Text = '録音中 — 未処理音声: {0:N1} 秒' -f $engine.BacklogSeconds }
                'Stopping' { $statusText.Text = '録音停止処理中 — 残りの音声を処理しています。中止しても録音は残ります。' }
                'LoadingTurbo' { $statusText.Text = 'Whisper large-v3-turbo を検証・読み込み中...' }
                'Refining' { $statusText.Text = '清書中: {0:P0}（中止は現在の音声区間の処理後に反映）' -f $engine.Progress }
                'Error' { $statusText.Text = '処理に失敗しました。エラー欄とログを確認してください。保存済み音声は保持しています。' }
                'Idle' {
                    if ($script:previousState -ne 'Idle' -and $script:previousState -ne '') {
                        $statusText.Text = if ($script:refinementCompleted) { '清書が完了しました。リアルタイム結果は別タブに保持しています。' } elseif ($engine.HasRecording) { '停止しました。清書を実行できます。音声: ' + $engine.LastRecordingPath } else { '準備完了' }
                    }
                }
            }
            $script:previousState = $state
        } catch { Show-UiError $_.Exception.ToString() }
    })
    $startButton.Add_Click({
        try {
            if ($engine.IsBusy) { $engine.Stop(); $startButton.IsEnabled = $false; return }
            if ($transcript.Text -or $cleanTranscript.Text) {
                $answer = [System.Windows.MessageBox]::Show('新しい録音では表示をクリアします。必要な文字起こしは保存済みですか？ 音声ファイルは削除しません。', '新しい録音', 'YesNo', 'Question')
                if ($answer -ne 'Yes') { return }
            }
            $engine.Gain = [single]$gainSlider.Value
            $engine.Start()
            $errorText.Visibility = 'Collapsed'; $script:refinementCompleted = $false
            $refineButton.IsEnabled = $false; $clearButton.IsEnabled = $false
        } catch { Show-UiError $_.Exception.ToString() }
    })
    $refineButton.Add_Click({
        try {
            $engine.StartRefinement()
            $errorText.Visibility = 'Collapsed'; $script:refinementCompleted = $false
            $refineButton.IsEnabled = $false; $startButton.IsEnabled = $false; $clearButton.IsEnabled = $false
        } catch { Show-UiError $_.Exception.ToString() }
    })
    $cancelButton.Add_Click({
        try { $engine.Cancel(); $cancelButton.IsEnabled = $false; $statusText.Text = '処理の中止を要求しました。録音と既存の結果は残ります。' }
        catch { Show-UiError $_.Exception.ToString() }
    })
    $gainSlider.Add_ValueChanged({ try { $engine.Gain = [single]$gainSlider.Value } catch { Show-UiError $_.Exception.ToString() } })
    $copyButton.Add_Click({
        try { $box = Get-SelectedTextBox; if ($box.Text) { Set-Clipboard -Value $box.Text; $statusText.Text = '表示中のタブをコピーしました' } }
        catch { Show-UiError $_.Exception.ToString() }
    })
    $clearButton.Add_Click({
        if (-not $engine.IsBusy) { (Get-SelectedTextBox).Clear(); $statusText.Text = '表示中のタブをクリアしました（録音は保持）' }
    })
    $saveButton.Add_Click({
        try {
            $box = Get-SelectedTextBox
            if (-not $box.Text) { $statusText.Text = '保存する文字がありません'; return }
            $path = Get-UniqueOutputPath $outputDir
            [IO.File]::WriteAllText($path, $box.Text, (New-Object System.Text.UTF8Encoding -ArgumentList $true))
            $statusText.Text = '保存しました: ' + $path
        } catch { Show-UiError $_.Exception.ToString() }
    })
    $window.Add_Closing({
        param($sender, $eventArgs)
        if ($script:allowClose) { return }
        try {
            if ($engine.IsBusy) {
                $eventArgs.Cancel = $true
                $script:closing = $true
                $engine.Cancel()
                return
            }
            $engine.Dispose(); $timer.Stop()
        } catch { $eventArgs.Cancel = $true; Show-UiError $_.Exception.ToString() }
    })
    if ($Smoke) {
        if ($refineButton.IsEnabled) { throw 'Refinement must be disabled without a recording.' }
        if ($engine.IsBusy) { throw 'Opening the UI must not load models or start recording.' }
        $transcript.Text = 'smoke'
        $clearButton.RaiseEvent((New-Object System.Windows.RoutedEventArgs -ArgumentList ([System.Windows.Controls.Button]::ClickEvent)))
        if ($transcript.Text -ne '') { throw 'Clear did not empty the active transcript.' }
        $engine.Dispose()
        Write-Output 'CLEAR_OK'; Write-Output 'SMOKE_OK'
        exit 0
    }
    $timer.Start()
    [void]$window.ShowDialog()
} catch {
    $message = "文字起こしを開始できませんでした。`r`n" + $_.Exception.ToString()
    Write-TranscribeLog $message
    if ($Smoke -or $env:TRANSCRIBE_NOPAUSE -eq '1') { [Console]::Error.WriteLine($message); exit 1 }
    try {
        Add-Type -AssemblyName PresentationFramework
        [void][System.Windows.MessageBox]::Show($message + "`r`nログ: $script:logPath", '文字起こし', 'OK', 'Error')
    } catch { [Console]::Error.WriteLine($message) }
    exit 1
}
