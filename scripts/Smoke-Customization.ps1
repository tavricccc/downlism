# Uses an isolated profile and generated local data. Never deletes or overwrites real downloads.
param([string]$Executable, [string]$OutputDirectory, [int]$Port = 18991)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Executable) { $Executable = Join-Path $root 'src/Downlism.App/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/Downlism.App.exe' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts/customization-smoke/' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
if (Get-Process Downlism.App -ErrorAction SilentlyContinue) { throw 'Close the running Downlism instance before this test.' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$profile = Join-Path $OutputDirectory 'profile'
$downloads = Join-Path $OutputDirectory 'downloads'
New-Item -ItemType Directory -Force $profile,$downloads | Out-Null
@{ DownloadFolder=$downloads; CloseToTray=$false; KeepProgressWindow=$false; Theme='System' } | ConvertTo-Json | Set-Content (Join-Path $profile 'settings.json') -Encoding utf8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$tree = [System.Windows.Automation.TreeScope]::Descendants
$ae = [System.Windows.Automation.AutomationElement]
function Find-Name($Parent, [string]$Name, [string]$Kind = '') {
 $conditions = [Collections.Generic.List[System.Windows.Automation.Condition]]::new()
 $conditions.Add([System.Windows.Automation.PropertyCondition]::new($ae::NameProperty, $Name))
 if ($Kind) { $conditions.Add([System.Windows.Automation.PropertyCondition]::new($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::$Kind)) }
 $condition = if ($conditions.Count -eq 1) { $conditions[0] } else { [System.Windows.Automation.AndCondition]::new($conditions.ToArray()) }
 $found = $Parent.FindFirst($tree, $condition)
 if (!$found) { throw "Control not found: $Name [$Kind]" }
 return $found
}
function Find-Id($Parent, [string]$Id) {
 $found = $null
 for ($i=0; $i -lt 30 -and !$found; $i++) {
  $found = $Parent.FindFirst($tree, [System.Windows.Automation.PropertyCondition]::new($ae::AutomationIdProperty, $Id))
  if (!$found) { Start-Sleep -Milliseconds 100 }
 }
 if (!$found) { throw "Control not found: #$Id" }; return $found
}
function Invoke-Control($Element) { $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 400 }
function Set-Value($Element, [string]$Value) { $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value) }
function Select-Control($Element) { $Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300 }
function Window([string]$Title) {
 for ($i=0; $i -lt 40; $i++) {
  if ($process.HasExited) { throw "App exited with $($process.ExitCode)" }
  $condition = [System.Windows.Automation.AndCondition]::new(
   [System.Windows.Automation.PropertyCondition]::new($ae::ProcessIdProperty, $process.Id),
   [System.Windows.Automation.PropertyCondition]::new($ae::NameProperty, $Title))
  $found = $ae::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
  if ($found) { return $found }; Start-Sleep -Milliseconds 250
 }
 throw "Window not found: $Title"
}
function Shot([string]$Name, [string]$Title='Downlism') {
 & (Join-Path $PSScriptRoot 'Capture-Window.ps1') -ProcessId $process.Id -Title $Title -OutputPath (Join-Path $OutputDirectory ($Name+'.png'))
}
$server = Start-Process pwsh -ArgumentList @('-NoProfile','-File',(Join-Path $PSScriptRoot 'Serve-TestFile.ps1'),'-Port',$Port,'-SizeMegabytes',8,'-KilobytesPerSecond',128) -PassThru -WindowStyle Hidden
$start = [Diagnostics.ProcessStartInfo]::new($Executable)
$start.WorkingDirectory = Split-Path $Executable
$start.UseShellExecute = $false
$start.Environment['DOWNLISM_DATA_DIRECTORY'] = $profile
$process = [Diagnostics.Process]::Start($start)
try {
 $main = Window 'Downlism'; Start-Sleep -Seconds 2
 Shot '01-main-light'
 $previousClipboard = Get-Clipboard -Raw
 try {
  Set-Clipboard -Value "http://127.0.0.1:$Port/sample.bin"
  Invoke-Control (Find-Name $main '貼上網址' 'Button')
  $prompt = Window '新增下載'
  Shot '01a-download-prompt' '新增下載'
  Invoke-Control (Find-Name $prompt '這次下載的設定' 'Button')
  [void](Find-Id $prompt 'JobConnections')
  [void](Find-Id $prompt 'ExpectedHash')
  Shot '01b-download-options' '新增下載'
  $prompt.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 } finally { Set-Clipboard -Value $(if ($null -eq $previousClipboard) { '' } else { $previousClipboard }) }
 Invoke-Control (Find-Name $main '設定' 'Button')
 $settings = Window 'Downlism 設定'
 $actualFolder = (Find-Id $settings 'Folder').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
 if ($actualFolder -ne $downloads) { throw 'Profile isolation is not active. Rebuild with -p:Platform=x64 before running this script.' }
 Shot '02-settings-downloads' 'Downlism 設定'
 $number = Find-Id $settings 'Connections'
 $edit = $number.FindFirst($tree, [System.Windows.Automation.PropertyCondition]::new($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit))
 Set-Value $edit '12'
 Select-Control (Find-Name $settings '分類規則' 'TabItem')
 Set-Value (Find-Id $settings 'Rules') '測試=bin'
 Select-Control (Find-Name $settings '介面與行為' 'TabItem')
 $theme = Find-Id $settings 'ThemeChoice'
 $theme.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
 Start-Sleep -Milliseconds 300
 Select-Control (Find-Name $ae::RootElement '深色' 'ListItem')
 Shot '03-settings-behavior' 'Downlism 設定'
 Invoke-Control (Find-Name $settings '儲存' 'Button')
 Start-Sleep -Milliseconds 500
 $saved = Get-Content (Join-Path $profile 'settings.json') -Raw | ConvertFrom-Json
 if ($saved.Connections -ne 12 -or $saved.CategoryRules -ne '測試=bin' -or $saved.Theme -ne 'Dark') { throw 'Settings did not round-trip through the real UI.' }
 Shot '04-main-dark'
 Invoke-Control (Find-Name $main '確定' 'Button')
 Invoke-Control (Find-Name $main '批次新增' 'Button')
 Set-Value (Find-Name $main '批次網址' 'Edit') "http://127.0.0.1:$Port/sample.bin`nhttp://127.0.0.1:$Port/sample.bin"
 Invoke-Control (Find-Name $main '加入清單' 'Button')
 Shot '05-paused-batch'
 Invoke-Control (Find-Name $main '確定' 'Button')
 Invoke-Control (Find-Name $main '全部繼續' 'Button')
 Start-Sleep -Seconds 1
 Shot '06-downloading'
 $bar = Find-Name $main '下載進度' 'ProgressBar'
 $range = $bar.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current
 if ($range.Maximum -ne 1 -or $range.Value -le 0 -or $range.Value -ge 1) { throw 'Expected native determinate progress between 0 and 1.' }
 $speed = Find-Name $main '下載速度' 'Text'
 if (!$speed.Current.HelpText.Contains('即時速度：') -or $speed.Current.HelpText.Contains('即時速度：—')) { throw 'Live speed was not populated.' }
 Invoke-Control (Find-Name $main '全部暫停' 'Button')
 Start-Sleep -Seconds 1
 Shot '07-paused'
 Invoke-Control (Find-Name $main '全部繼續' 'Button')
 $file = Join-Path $downloads '測試/sample.bin'
 for ($i=0; $i -lt 90 -and !(Test-Path $file); $i++) { Start-Sleep -Milliseconds 500 }
 if (!(Test-Path $file)) { throw 'Download did not finish.' }
 $expected = [byte[]]::new(8MB)
 for ($index=0; $index -lt $expected.Length; $index+=997) { $expected[$index] = [byte]($index % 251) }
 $expectedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($expected))
 $actual = (Get-FileHash $file -Algorithm SHA256).Hash
 if ($actual -ne $expectedHash) { throw 'Downloaded content does not match local origin.' }
 Set-Value (Find-Id $main 'SearchBox') 'no-such-file'
 Start-Sleep -Milliseconds 300
 [void](Find-Name $main '沒有符合條件的下載' 'Text')
 Set-Value (Find-Id $main 'SearchBox') ''
 Start-Sleep -Seconds 3
 Shot '08-completed'
 $completeBar = Find-Name $main '下載進度' 'ProgressBar'
 if ($completeBar.Current.IsOffscreen -or $completeBar.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne 1) { throw 'Completed download must retain a visible full progress bar.' }
 $speed = Find-Name $main '下載速度' 'Text'
 if (!$speed.Current.HelpText.Contains('平均速度：') -or $speed.Current.HelpText.Contains('平均速度：—')) { throw 'Completed row lost its average speed.' }
 $averageDetails = $speed.Current.HelpText
 $process.Refresh()
 $metrics = [ordered]@{ ProcessId=$process.Id; WorkingSetMiB=[math]::Round($process.WorkingSet64/1MB,1); PrivateMiB=[math]::Round($process.PrivateMemorySize64/1MB,1); DownloadSha256=$actual; Profile=$profile; Result='passed' }
 $metrics | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'result.json') -Encoding utf8
 $metrics | ConvertTo-Json
 $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 if (!$process.WaitForExit(10000)) { throw 'Close-to-exit preference did not exit the app.' }
 $process = [Diagnostics.Process]::Start($start)
 $restored = Window 'Downlism'
 Start-Sleep -Seconds 2
 [void](Find-Name $restored 'sample.bin' 'Text')
 $completeBar = Find-Name $restored '下載進度' 'ProgressBar'
 if ($completeBar.Current.IsOffscreen -or $completeBar.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne 1) { throw 'Restored completed download must retain its full progress bar.' }
 $speed = Find-Name $restored '下載速度' 'Text'
 if ($speed.Current.HelpText -ne $averageDetails) { throw 'Average speed did not survive restart.' }
 $restored.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
 if (!$process.WaitForExit(10000)) { throw 'Restored app did not exit.' }
 Write-Output 'Restart restored the completed download successfully.'
} finally {
 if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
 if (!$server.HasExited) { $server.Kill(); $server.WaitForExit() }
}
