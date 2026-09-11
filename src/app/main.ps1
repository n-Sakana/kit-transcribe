# transcribe -- local Japanese real-time transcription GUI.
param(
    [string]$Target = "",
    [switch]$Smoke
)
$ErrorActionPreference = "Stop"
$script:SmokeMode = $Smoke.IsPresent
$script:windowTitle = "文字起こし"

function Show-FatalError {
    param([string]$Message)
    if ($script:SmokeMode -or $env:TRANSCRIBE_NOPAUSE -eq "1") {
        [Console]::Error.WriteLine($Message)
        exit 1
    }
    try {
        Add-Type -AssemblyName PresentationFramework -ErrorAction SilentlyContinue
        [void][System.Windows.MessageBox]::Show(
            $Message,
            $script:windowTitle,
            [System.Windows.MessageBoxButton]::OK,
            [System.Windows.MessageBoxImage]::Error
        )
    } catch {
    }
    exit 1
}

function Show-UiError {
    param([string]$Message)
    [void][System.Windows.MessageBox]::Show(
        $Message,
        $script:windowTitle,
        [System.Windows.MessageBoxButton]::OK,
        [System.Windows.MessageBoxImage]::Error
    )
}

function Get-UniqueOutputPath {
    param([string]$Directory)
    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        [void](New-Item -ItemType Directory -Force -Path $Directory)
    }
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $candidate = Join-Path $Directory ("transcribe_" + $stamp + ".txt")
    $suffix = 1
    while (Test-Path -LiteralPath $candidate) {
        $candidate = Join-Path $Directory ("transcribe_" + $stamp + "_" + $suffix + ".txt")
        $suffix++
    }
    return $candidate
}

try {
    $bin = Join-Path $PSScriptRoot "bin"
    $model = Join-Path $PSScriptRoot "model"
    $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $outputDir = Join-Path $root "output"
    $env:PATH = $bin + ";" + $env:PATH

    Add-Type -Path (Join-Path $bin "sherpa-onnx.dll")
    Add-Type -Path (Join-Path $bin "NAudio.dll")
    $engineReferences = @(
        (Join-Path $bin "sherpa-onnx.dll"),
        (Join-Path $bin "NAudio.dll")
    )
    $engineSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "engine.cs"))
    Add-Type -TypeDefinition $engineSource -ReferencedAssemblies $engineReferences
    Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

    $xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="文字起こし" Width="720" Height="540"
        MinWidth="660" MinHeight="400" WindowStartupLocation="CenterScreen">
  <Grid Margin="12">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
      <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>
    <StackPanel Grid.Row="0" Orientation="Horizontal">
      <Button x:Name="StartButton" Content="開始" Width="88" Height="32"/>
      <Button x:Name="CopyButton" Content="コピー" Width="78" Height="32" Margin="8,0,0,0"/>
      <Button x:Name="ClearButton" Content="クリア" Width="78" Height="32" Margin="8,0,0,0"/>
      <Button x:Name="SaveButton" Content="保存" Width="78" Height="32" Margin="8,0,0,0"/>
      <TextBlock Text="感度" VerticalAlignment="Center" Margin="16,0,5,0"/>
      <Slider x:Name="GainSlider" Minimum="0.5" Maximum="4" Value="1"
              TickFrequency="0.5" Width="130" VerticalAlignment="Center"/>
      <ProgressBar x:Name="LevelBar" Minimum="0" Maximum="1" Width="100" Height="15"
                   Margin="10,0,0,0" VerticalAlignment="Center"/>
    </StackPanel>
    <TextBox x:Name="Transcript" Grid.Row="1" Margin="0,12,0,8"
             AcceptsReturn="True" TextWrapping="Wrap" IsReadOnly="True"
             VerticalScrollBarVisibility="Auto" FontSize="16"/>
    <TextBlock x:Name="StatusText" Grid.Row="2" Text="準備完了" Foreground="DimGray"
               TextTrimming="CharacterEllipsis"/>
  </Grid>
</Window>
"@

    $window = [System.Windows.Markup.XamlReader]::Parse($xaml)
    $startButton = $window.FindName("StartButton")
    $copyButton = $window.FindName("CopyButton")
    $clearButton = $window.FindName("ClearButton")
    $saveButton = $window.FindName("SaveButton")
    $gainSlider = $window.FindName("GainSlider")
    $levelBar = $window.FindName("LevelBar")
    $transcript = $window.FindName("Transcript")
    $statusText = $window.FindName("StatusText")

    $engine = New-Object TranscriberEngine -ArgumentList $model
    $engine.Gain = [single]$gainSlider.Value
    $script:uiRunning = $false
    $script:stopping = $false
    $script:closeError = $null

    $timer = New-Object System.Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromMilliseconds(100)
    $timer.Add_Tick({
        [string]$nextText = ""
        while ($engine.TryGetText([ref]$nextText)) {
            $transcript.AppendText($nextText + "`r`n")
            $transcript.ScrollToEnd()
            $nextText = ""
        }

        $errors = New-Object System.Collections.Generic.List[string]
        [string]$nextError = ""
        while ($engine.TryGetError([ref]$nextError)) {
            $errors.Add($nextError)
            $nextError = ""
        }
        if ($errors.Count -gt 0) {
            $message = $errors -join "`r`n"
            $statusText.Text = "エラー: " + $message
            Show-UiError $message
        }

        $levelBar.Value = [double]$engine.LatestLevel
        if ($script:uiRunning -and -not $engine.IsRunning) {
            $script:uiRunning = $false
            $script:stopping = $false
            $startButton.IsEnabled = $true
            $startButton.Content = "開始"
            $statusText.Text = "停止しました"
        }
    })

    $startButton.Add_Click({
        if (-not $script:uiRunning) {
            try {
                $engine.Gain = [single]$gainSlider.Value
                $engine.Start()
                $script:uiRunning = $true
                $script:stopping = $false
                $startButton.Content = "停止"
                $statusText.Text = "録音中"
            } catch {
                Show-UiError $_.Exception.Message
                $statusText.Text = "開始できませんでした"
            }
            return
        }
        if (-not $script:stopping) {
            try {
                $script:stopping = $true
                $startButton.IsEnabled = $false
                $startButton.Content = "停止中..."
                $statusText.Text = "最後の発話を処理しています"
                $engine.Stop()
            } catch {
                $script:stopping = $false
                $startButton.IsEnabled = $true
                Show-UiError $_.Exception.Message
            }
        }
    })

    $gainSlider.Add_ValueChanged({
        if ($null -ne $engine) {
            $engine.Gain = [single]$gainSlider.Value
        }
    })

    $copyButton.Add_Click({
        try {
            if ([string]::IsNullOrWhiteSpace($transcript.Text)) {
                $statusText.Text = "コピーする文字がありません"
                return
            }
            Set-Clipboard -Value $transcript.Text
            $statusText.Text = "クリップボードへコピーしました"
        } catch {
            Show-UiError ("コピーに失敗しました: " + $_.Exception.Message)
        }
    })

    $clearButton.Add_Click({
        $transcript.Clear()
        $statusText.Text = "クリアしました"
    })

    $saveButton.Add_Click({
        try {
            if ([string]::IsNullOrWhiteSpace($transcript.Text)) {
                $statusText.Text = "保存する文字がありません"
                return
            }
            $path = Get-UniqueOutputPath $outputDir
            $utf8Bom = New-Object System.Text.UTF8Encoding -ArgumentList $true
            [IO.File]::WriteAllText($path, $transcript.Text, $utf8Bom)
            $statusText.Text = "保存しました: " + $path
        } catch {
            Show-UiError ("保存に失敗しました: " + $_.Exception.Message)
        }
    })

    $window.Add_Closing({
        $timer.Stop()
        try {
            $engine.Dispose()
        } catch {
            $script:closeError = $_.Exception.Message
        }
    })

    if ($Smoke) {
        $transcript.Text = "smoke"
        $clearButton.RaiseEvent((New-Object System.Windows.RoutedEventArgs -ArgumentList ([System.Windows.Controls.Button]::ClickEvent)))
        if ($transcript.Text -ne "") {
            throw "Clear button did not empty the transcript."
        }
        Write-Output "CLEAR_OK"
        $timer.Start()
        $timer.Stop()
        $engine.Dispose()
        Write-Output "SMOKE_OK"
        exit 0
    }

    $timer.Start()
    [void]$window.ShowDialog()
    if ($null -ne $script:closeError) {
        throw $script:closeError
    }
} catch {
    Show-FatalError ("文字起こしを開始できませんでした。`r`n" + $_.Exception.Message)
}
