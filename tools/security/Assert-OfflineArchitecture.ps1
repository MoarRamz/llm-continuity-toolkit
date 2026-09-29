$ErrorActionPreference = 'Stop'

$sourceRoots = @(
    'src/LLMContinuityToolkit.Core',
    'src/LLMContinuityToolkit.App'
)

$forbidden = [ordered]@{
    'HTTP client'             = '\bHttpClient\b|System\.Net\.Http'
    'legacy web client'       = '\bWebRequest\b|\bWebClient\b'
    'TCP/UDP networking'      = '\bTcpClient\b|\bUdpClient\b'
    'raw sockets'             = 'System\.Net\.Sockets|\bSocket\b'
    'DNS lookup'              = '\bDns\s*\.'
    'WebSocket'               = '\bWebSocket\b'
    'Windows Registry'        = 'Microsoft\.Win32\.Registry|\bRegistryKey\b'
    'Windows Service API'     = '\bServiceController\b|System\.ServiceProcess'
    'startup persistence'     = 'SpecialFolder\.Startup|CurrentVersion\\Run'
    'PowerShell execution'    = 'powershell(?:\.exe)?'
    'cmd.exe execution'       = 'cmd\.exe'
    'VBScript execution'      = 'wscript\.exe|cscript\.exe|\.vbs["'']'
    'dynamic assembly load'   = 'Assembly\.Load(?:File|From)?\s*\('
    'native library load'     = '\bNativeLibrary\.Load\s*\('
}

$files = foreach ($root in $sourceRoots) {
    if (Test-Path $root) {
        Get-ChildItem $root -Recurse -File -Filter '*.cs'
    }
}

$violations = @()
foreach ($file in $files) {
    $content = Get-Content $file.FullName -Raw
    foreach ($item in $forbidden.GetEnumerator()) {
        if ($content -match $item.Value) {
            $violations += "$($file.FullName): forbidden $($item.Key) capability matched pattern '$($item.Value)'"
        }
    }

    if ($content -match '\bProcess\.Start\s*\(') {
        $relative = [IO.Path]::GetRelativePath((Get-Location).Path, $file.FullName).Replace('\', '/')
        $allowedFile = 'src/LLMContinuityToolkit.App/ExportSuccessWindow.xaml.cs'
        $allowedExplorerLaunch = 'Process\.Start\s*\(\s*new\s+ProcessStartInfo\s*\(\s*"explorer\.exe"'
        if ($relative -ne $allowedFile -or $content -notmatch $allowedExplorerLaunch) {
            $violations += "$($file.FullName): child-process launch is not the allowlisted Explorer Open Folder action."
        }
    }
}

$manifest = 'src/LLMContinuityToolkit.App/app.manifest'
if (-not (Test-Path $manifest)) {
    $violations += 'Windows application manifest is missing.'
}
else {
    $manifestText = Get-Content $manifest -Raw
    if ($manifestText -notmatch 'requestedExecutionLevel\s+level="asInvoker"') {
        $violations += 'Windows manifest must explicitly request asInvoker.'
    }
    if ($manifestText -match 'requireAdministrator|highestAvailable') {
        $violations += 'Windows manifest must not request elevation.'
    }
}

if ($violations.Count -gt 0) {
    Write-Host 'Security architecture guard FAILED:' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
    throw 'Application architecture violated the local-only/no-persistence security contract.'
}

Write-Host "Security architecture guard passed across $($files.Count) C# source files."
Write-Host 'No networking, Registry persistence, Windows service, startup persistence, script-shell execution, or dynamic load APIs were detected.'
Write-Host 'Child-process launch is restricted to the explicit Explorer Open Folder action.'
Write-Host 'Windows manifest remains asInvoker.'
