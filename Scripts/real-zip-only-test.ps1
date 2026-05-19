param(
    [string]$RepoRoot = 'C:\Users\18254\Desktop\celeste_replay_mod',
    [string]$CelesteDir = 'D:\Steam\steamapps\common\Celeste',
    [string]$ObsDir = 'D:\obs',
    [bool]$StartObsIfNeeded = $true,
    [bool]$KeepGameOpen = $false,
    [bool]$KeepObsOpen = $true,
    [bool]$RestoreModsOnExit = $true
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Get-PropValue($Object, [string[]]$Names) {
    if ($null -eq $Object) {
        return $null
    }

    foreach ($name in $Names) {
        if ($Object.PSObject.Properties.Name -contains $name) {
            return $Object.$name
        }
    }

    return $null
}

function Wait-Until([scriptblock]$Condition, [string]$Description, [int]$TimeoutSec = 120, [int]$PollMs = 1000) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $result = & $Condition
        if ($result) {
            return $result
        }
        Start-Sleep -Milliseconds $PollMs
    }

    throw "Timed out waiting for: $Description"
}

function Invoke-JsonGet([string]$Url, [int]$TimeoutSec = 15) {
    Invoke-RestMethod -Uri $Url -Method Get -TimeoutSec $TimeoutSec
}

function Invoke-JsonPost([string]$Url, $Body = $null, [int]$TimeoutSec = 30) {
    if ($null -eq $Body) {
        return Invoke-RestMethod -Uri $Url -Method Post -TimeoutSec $TimeoutSec
    }

    $json = $Body | ConvertTo-Json -Depth 8
    return Invoke-RestMethod -Uri $Url -Method Post -ContentType 'application/json' -Body $json -TimeoutSec $TimeoutSec
}

function Invoke-DebugRcText([string]$Url, [int]$TimeoutSec = 10) {
    $output = & curl.exe -sS --max-time $TimeoutSec $Url 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "curl failed for $Url (exit code $LASTEXITCODE)"
    }

    if ($output -is [System.Array]) {
        return ($output -join "`n")
    }

    return [string]$output
}

function Read-Ini([string]$Path) {
    $ini = @{}
    $section = ''
    foreach ($rawLine in Get-Content -LiteralPath $Path -Encoding UTF8) {
        $line = $rawLine.Trim()
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith(';') -or $line.StartsWith('#')) {
            continue
        }

        if ($line.StartsWith('[') -and $line.EndsWith(']')) {
            $section = $line.Substring(1, $line.Length - 2).Trim()
            if (-not $ini.ContainsKey($section)) {
                $ini[$section] = @{}
            }
            continue
        }

        $index = $line.IndexOf('=')
        if ($index -le 0) {
            continue
        }

        $key = $line.Substring(0, $index).Trim()
        $value = $line.Substring($index + 1).Trim()
        if (-not $ini.ContainsKey($section)) {
            $ini[$section] = @{}
        }
        $ini[$section][$key] = $value
    }

    return $ini
}

function Get-IniValue($Ini, [string]$Section, [string]$Key) {
    if ($Ini.ContainsKey($Section) -and $Ini[$Section].ContainsKey($Key)) {
        $value = $Ini[$Section][$Key]
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value
        }
    }

    return $null
}

function Get-ObsDefaultRecordingDirectory {
    $appData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
    $obsRoot = Join-Path $appData 'obs-studio'
    $globalIniPath = Join-Path $obsRoot 'global.ini'
    $profileRoot = Join-Path $obsRoot 'basic\profiles'
    $profileCandidates = New-Object System.Collections.Generic.List[string]

    if (Test-Path $globalIniPath) {
        $globalIni = Read-Ini -Path $globalIniPath
        $profileDir = Get-IniValue $globalIni 'Basic' 'ProfileDir'
        if (-not $profileDir) {
            $profileDir = Get-IniValue $globalIni 'Basic' 'Profile'
        }
        if ($profileDir) {
            $profileCandidates.Add((Join-Path $profileRoot $profileDir))
        }
    }

    if (Test-Path $profileRoot) {
        Get-ChildItem -LiteralPath $profileRoot -Directory | ForEach-Object {
            $profileCandidates.Add($_.FullName)
        }
    }

    foreach ($profileDir in $profileCandidates | Select-Object -Unique) {
        $basicIniPath = Join-Path $profileDir 'basic.ini'
        if (-not (Test-Path $basicIniPath)) {
            continue
        }

        $ini = Read-Ini -Path $basicIniPath
        $mode = Get-IniValue $ini 'Output' 'Mode'
        $candidate = $null
        if ($mode -ieq 'Simple') {
            $candidate = Get-IniValue $ini 'SimpleOutput' 'FilePath'
        } elseif ($mode -ieq 'Advanced') {
            $recType = Get-IniValue $ini 'AdvOut' 'RecType'
            if ($recType -ieq 'FFmpeg') {
                $candidate = Get-IniValue $ini 'AdvOut' 'FFFilePath'
            } else {
                $candidate = Get-IniValue $ini 'AdvOut' 'RecFilePath'
                if (-not $candidate) {
                    $candidate = Get-IniValue $ini 'AdvOut' 'FFFilePath'
                }
            }
        }

        if ($candidate) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    return $null
}

function Get-LiveProcessByPath([string]$ExeName, [string]$ExecutablePath) {
    $target = [System.IO.Path]::GetFullPath($ExecutablePath)
    Get-CimInstance Win32_Process -Filter "Name = '$ExeName'" |
        Where-Object { $_.ExecutablePath -and ([System.IO.Path]::GetFullPath($_.ExecutablePath) -eq $target) }
}

function Stop-LiveProcessesByPath([string]$ExeName, [string]$ExecutablePath) {
    foreach ($proc in @(Get-LiveProcessByPath -ExeName $ExeName -ExecutablePath $ExecutablePath)) {
        & taskkill /PID $proc.ProcessId /F | Out-Null
    }

    Wait-Until -Description "$ExeName exit" -TimeoutSec 30 -PollMs 500 -Condition {
        $remaining = @(Get-LiveProcessByPath -ExeName $ExeName -ExecutablePath $ExecutablePath)
        if ($remaining.Count -eq 0) {
            return $true
        }

        return $null
    } | Out-Null
}

function Get-MapFolderName([string]$MapSid) {
    if ([string]::IsNullOrWhiteSpace($MapSid)) {
        return 'UnknownMap'
    }

    $normalized = $MapSid.Trim().Replace('\', '/')
    $segments = @($normalized -split '/' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $leaf = if ($segments.Count -gt 0) { $segments[-1] } else { $normalized }
    foreach ($c in [System.IO.Path]::GetInvalidFileNameChars()) {
        $leaf = $leaf.Replace([string]$c, '_')
    }
    $leaf = $leaf.Trim().TrimEnd('.')
    if ([string]::IsNullOrWhiteSpace($leaf)) {
        return 'UnknownMap'
    }

    return $leaf
}

function Stop-ObsClipPanelProcesses([string]$CelesteDir) {
$helperExe = Join-Path $CelesteDir 'CelesteAutoCutTools\ObsClipPanel\ObsClipPanel.exe'
    if (Test-Path $helperExe) {
        Stop-LiveProcessesByPath -ExeName 'ObsClipPanel.exe' -ExecutablePath $helperExe
    }
}

function Wait-DebugRc([string]$BaseUrl, [int]$TimeoutSec = 240) {
    Wait-Until -Description "DebugRC at $BaseUrl" -TimeoutSec $TimeoutSec -PollMs 2000 -Condition {
        try {
            $status = Get-TasStatus -DebugRcBaseUrl $BaseUrl
            if ($null -ne $status) {
                return $status
            }
        } catch {
        }

        return $null
    }
}

function Wait-PanelSnapshot([string]$BaseUrl, [int]$TimeoutSec = 240) {
    Wait-Until -Description "ObsClipPanel API at $BaseUrl" -TimeoutSec $TimeoutSec -PollMs 2000 -Condition {
        try {
            $snapshot = Invoke-JsonGet -Url "$BaseUrl/api/status" -TimeoutSec 5
            if ($snapshot -and $snapshot.status) {
                return $snapshot
            }
        } catch {
        }

        return $null
    }
}

function Wait-PanelCondition([string]$BaseUrl, [scriptblock]$Condition, [string]$Description, [int]$TimeoutSec = 120, [int]$PollMs = 1000) {
    Wait-Until -Description $Description -TimeoutSec $TimeoutSec -PollMs $PollMs -Condition {
        try {
            $snapshot = Invoke-JsonGet -Url "$BaseUrl/api/status" -TimeoutSec 5
            if ($snapshot -and (& $Condition $snapshot)) {
                return $snapshot
            }
        } catch {
        }

        return $null
    }
}

function Play-Tas([string]$DebugRcBaseUrl, [string]$TasPath) {
    $escaped = [uri]::EscapeDataString($TasPath)
    $response = & curl.exe -sS --max-time 20 "$DebugRcBaseUrl/tas/playtas?filePath=$escaped" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to start TAS via DebugRC: $TasPath"
    }

    if ($response -is [System.Array]) {
        $response = ($response -join "`n")
    }

    if ([string]::IsNullOrWhiteSpace([string]$response) -or ($response -notmatch '\bOK\b')) {
        throw "Unexpected DebugRC playtas response for $TasPath"
    }
}

function Get-TasStatus([string]$DebugRcBaseUrl) {
    $content = Invoke-DebugRcText -Url "$DebugRcBaseUrl/tas/info" -TimeoutSec 10

    if ([string]::IsNullOrWhiteSpace($content)) {
        throw "Empty TAS info response from $DebugRcBaseUrl/tas/info"
    }

    $map = @{}
    foreach ($name in @('Running', 'State', 'CurrentFrame', 'TotalFrames', 'RoomName', 'ChapterTime')) {
        if ($content -match "${name}:\s*([^<\r\n]+)") {
            $map[$name] = $matches[1].Trim()
        }
    }

    [pscustomobject]@{
        Running = if ($map.ContainsKey('Running')) { $map['Running'] -ieq 'True' } else { $false }
        State = if ($map.ContainsKey('State')) { $map['State'] } else { '' }
        CurrentFrame = if ($map.ContainsKey('CurrentFrame')) { [int64]$map['CurrentFrame'] } else { 0 }
        TotalFrames = if ($map.ContainsKey('TotalFrames')) { [int64]$map['TotalFrames'] } else { 0 }
        RoomName = if ($map.ContainsKey('RoomName')) { $map['RoomName'] } else { '' }
        ChapterTime = if ($map.ContainsKey('ChapterTime')) { $map['ChapterTime'] } else { '' }
        Raw = $content
    }
}

function Wait-TasStarted([string]$DebugRcBaseUrl, [int]$MinimumTotalFrames = 1, [int]$TimeoutSec = 120) {
    Wait-Until -Description "TAS started (totalFrames >= $MinimumTotalFrames)" -TimeoutSec $TimeoutSec -PollMs 1000 -Condition {
        try {
            $status = Get-TasStatus -DebugRcBaseUrl $DebugRcBaseUrl
            $state = (Get-PropValue $status @('State', 'state')).ToString()
            $runningValue = Get-PropValue $status @('Running', 'running')
            $running = $false
            if ($runningValue -ne $null) {
                $running = [bool]$runningValue
            }

            $totalFrames = [int64](Get-PropValue $status @('TotalFrames', 'totalFrames'))
            if (($running -or $state -ieq 'Paused' -or $state -ieq 'Running') -and $totalFrames -ge $MinimumTotalFrames) {
                return $status
            }
        } catch {
        }

        return $null
    }
}

function Get-LatestSessionDirectory([string]$SessionsRoot, [datetime]$NotBeforeUtc) {
    if (-not (Test-Path $SessionsRoot)) {
        return $null
    }

    $thresholdLocal = $NotBeforeUtc.ToLocalTime().AddSeconds(-5)
    $candidates = @(
        Get-ChildItem -LiteralPath $SessionsRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $thresholdLocal } |
            Sort-Object LastWriteTime -Descending
    )
    if ($candidates.Count -gt 0) {
        return $candidates[0].FullName
    }

    return $null
}

function Test-SessionHasActiveRecording([string]$SessionDirectory) {
    $obsEventsPath = Join-Path $SessionDirectory 'obs_events.jsonl'
    if (-not (Test-Path $obsEventsPath)) {
        return $false
    }

    foreach ($line in @(Get-Content -LiteralPath $obsEventsPath -Tail 40 -Encoding UTF8)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line -match '"outputActive":true' -and $line -match '"eventType":"record_') {
            return $true
        }
    }

    return $false
}

function Wait-RecordingStart([string]$BaseUrl, [string]$SessionsRoot, [datetime]$NotBeforeUtc, [int]$TimeoutSec = 60) {
    Wait-Until -Description 'recording became active' -TimeoutSec $TimeoutSec -PollMs 1000 -Condition {
        try {
            $snapshot = Invoke-JsonGet -Url "$BaseUrl/api/status" -TimeoutSec 5
            if ($snapshot -and $snapshot.status.recordingActive -and -not [string]::IsNullOrWhiteSpace($snapshot.status.outputPath)) {
                return [pscustomobject]@{
                    Source = 'api'
                    Snapshot = $snapshot
                    SessionId = $snapshot.status.currentSessionId
                    SessionDirectory = $snapshot.status.paths.sessionDirectory
                    OutputPath = $snapshot.status.outputPath
                    FinalOutputPath = $snapshot.status.paths.finalOutputPath
                }
            }
        } catch {
        }

        $latestSessionDirectory = Get-LatestSessionDirectory -SessionsRoot $SessionsRoot -NotBeforeUtc $NotBeforeUtc
        if ($latestSessionDirectory -and (Test-SessionHasActiveRecording -SessionDirectory $latestSessionDirectory)) {
            return [pscustomobject]@{
                Source = 'session-tail'
                Snapshot = $null
                SessionId = Split-Path -Leaf $latestSessionDirectory
                SessionDirectory = $latestSessionDirectory
                OutputPath = $null
                FinalOutputPath = $null
            }
        }

        return $null
    }
}

function Test-SessionShowsRecordingStopped([string]$SessionDirectory) {
    if (-not (Test-Path $SessionDirectory)) {
        return $false
    }

    $manifestPath = Join-Path $SessionDirectory 'session_manifest.json'
    if (Test-Path $manifestPath) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
            if (@($manifest.recordings).Count -gt 0) {
                return $true
            }
        } catch {
        }
    }

    $obsEventsPath = Join-Path $SessionDirectory 'obs_events.jsonl'
    if (-not (Test-Path $obsEventsPath)) {
        return $false
    }

    foreach ($line in @(Get-Content -LiteralPath $obsEventsPath -Tail 40 -Encoding UTF8)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line -match '"outputActive":false' -and ($line -match 'OBS_WEBSOCKET_OUTPUT_STOPPING' -or $line -match 'OBS_WEBSOCKET_OUTPUT_STOPPED' -or $line -match '"eventType":"record_status_sample"')) {
            return $true
        }
    }

    return $false
}

function Wait-RecordingStop([string]$BaseUrl, [string]$SessionDirectory, [int]$TimeoutSec = 120) {
    Wait-Until -Description 'recording stopped' -TimeoutSec $TimeoutSec -PollMs 1000 -Condition {
        try {
            $snapshot = Invoke-JsonGet -Url "$BaseUrl/api/status" -TimeoutSec 5
            if ($snapshot -and -not $snapshot.status.recordingActive) {
                return [pscustomobject]@{
                    Source = 'api'
                    Snapshot = $snapshot
                }
            }
        } catch {
        }

        if (Test-SessionShowsRecordingStopped -SessionDirectory $SessionDirectory) {
            return [pscustomobject]@{
                Source = 'session-tail'
                Snapshot = $null
            }
        }

        return $null
    }
}

function Wait-TasCompletion([string]$DebugRcBaseUrl, [int]$TimeoutSec = 900) {
    $sawTasRunning = $false
    Wait-Until -Description 'TAS completion (running -> stopped via /tas/info)' -TimeoutSec $TimeoutSec -PollMs 1000 -Condition {
        try {
            $status = Get-TasStatus -DebugRcBaseUrl $DebugRcBaseUrl
            $state = Get-PropValue $status @('State', 'state')
            $currentFrame = Get-PropValue $status @('CurrentFrame', 'currentFrame')
            $totalFrames = Get-PropValue $status @('TotalFrames', 'totalFrames')
            $running = $false
            $runningValue = Get-PropValue $status @('Running', 'running')
            if ($runningValue -ne $null) {
                $running = [bool]$runningValue
            }

            if ($running -or ([int64]$currentFrame -gt 0) -or ($state -and $state.ToString() -ieq 'Running')) {
                $sawTasRunning = $true
            }

            if ($sawTasRunning -and $state -and $state.ToString() -ieq 'Paused' -and [int64]$totalFrames -gt 0 -and [int64]$currentFrame -eq [int64]$totalFrames) {
                return $status
            }

            if ($sawTasRunning -and -not $running -and [int64]$totalFrames -gt 0) {
                return $status
            }
        } catch {
        }

        return $null
    }
}

function Wait-TasIdle([string]$DebugRcBaseUrl, [int]$TimeoutSec = 120) {
    Wait-Until -Description 'TAS idle after completion' -TimeoutSec $TimeoutSec -PollMs 1000 -Condition {
        try {
            $status = Get-TasStatus -DebugRcBaseUrl $DebugRcBaseUrl
            $state = (Get-PropValue $status @('State', 'state')).ToString()
            $currentFrame = [int64](Get-PropValue $status @('CurrentFrame', 'currentFrame'))
            $totalFrames = [int64](Get-PropValue $status @('TotalFrames', 'totalFrames'))
            $runningValue = Get-PropValue $status @('Running', 'running')
            $running = $false
            if ($runningValue -ne $null) {
                $running = [bool]$runningValue
            }

            if (-not $running -and $totalFrames -gt 0) {
                return $status
            }

            if (($state -ieq 'Disabled' -or $state -ieq 'Paused') -and $totalFrames -gt 0 -and ($currentFrame -eq 0 -or $currentFrame -eq $totalFrames)) {
                return $status
            }
        } catch {
        }

        return $null
    }
}

function Get-RoomEventsSince([string]$Path, [datetime]$AfterUtc, [int]$TailCount = 400) {
    if (-not (Test-Path $Path)) {
        return @()
    }

    $events = New-Object System.Collections.Generic.List[object]
    foreach ($line in @(Get-Content -LiteralPath $Path -Tail $TailCount -Encoding UTF8)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        try {
            $event = $line | ConvertFrom-Json -ErrorAction Stop
            $utcText = Get-PropValue $event @('utc', 'Utc')
            if (-not $utcText) {
                continue
            }

            $eventUtc = [DateTimeOffset]::Parse($utcText).UtcDateTime
            if ($eventUtc -gt $AfterUtc) {
                [void]$events.Add($event)
            }
        } catch {
        }
    }

    return $events.ToArray()
}

function Wait-RoomEventSessionStart([string]$Path, [datetime]$AfterUtc, [int]$TimeoutSec = 120) {
    Wait-Until -Description 'room event activity after TAS start' -TimeoutSec $TimeoutSec -PollMs 1000 -Condition {
        $events = @(Get-RoomEventsSince -Path $Path -AfterUtc $AfterUtc)
        if ($events.Count -eq 0) {
            return $null
        }

        $activity = @(
            $events | Where-Object {
                (Get-PropValue $_ @('eventType', 'EventType')) -in @('session_start', 'load_level', 'room_enter', 'transition', 'level_complete', 'session_end', 'exit')
            }
        )
        if ($activity.Count -eq 0) {
            return $null
        }

        $sessionId = Get-PropValue $activity[0] @('sessionId', 'SessionId')
        [pscustomobject]@{
            SessionId = $sessionId
            Event = $activity[0]
            Events = $activity
        }
    }
}

function Wait-RoomEventSessionComplete([string]$Path, [datetime]$AfterUtc, [string]$SessionId = '', [int]$TimeoutSec = 1800) {
    Wait-Until -Description 'room event completion after TAS start' -TimeoutSec $TimeoutSec -PollMs 1000 -Condition {
        $events = @(Get-RoomEventsSince -Path $Path -AfterUtc $AfterUtc)
        if ($SessionId) {
            $events = @(
                $events | Where-Object {
                    (Get-PropValue $_ @('sessionId', 'SessionId')) -eq $SessionId
                }
            )
        }

        if ($events.Count -eq 0) {
            return $null
        }

        $completed = @(
            $events | Where-Object {
                $eventType = Get-PropValue $_ @('eventType', 'EventType')
                if ($eventType -in @('level_complete', 'session_end')) {
                    return $true
                }

                if ($eventType -eq 'exit') {
                    $notes = Get-PropValue $_ @('notes', 'Notes')
                    $mode = Get-PropValue $notes @('mode', 'Mode')
                    return $mode -eq 'Completed'
                }

                return $false
            }
        )

        if ($completed.Count -eq 0) {
            return $null
        }

        [pscustomobject]@{
            SessionId = if ($SessionId) { $SessionId } else { Get-PropValue $completed[-1] @('sessionId', 'SessionId') }
            Event = $completed[-1]
            Events = $events
        }
    }
}

function Wait-File([string]$Path, [string]$Description, [int]$TimeoutSec = 180) {
    Wait-Until -Description $Description -TimeoutSec $TimeoutSec -PollMs 1000 -Condition {
        if (Test-Path $Path) {
            return $Path
        }
        return $null
    }
}

function Wait-AssemblyExecution([string]$AssemblyReportPath, [int]$TimeoutSec = 600) {
    Wait-Until -Description 'assembly execution completion' -TimeoutSec $TimeoutSec -PollMs 1000 -Condition {
        if (-not (Test-Path $AssemblyReportPath)) {
            return $null
        }

        try {
            $report = Get-Content -LiteralPath $AssemblyReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($report.executed) {
                return $report
            }
        } catch {
        }

        return $null
    }
}

function Test-RecentFile([string]$Path, [datetime]$NotBeforeUtc) {
    if (-not (Test-Path $Path)) {
        return $false
    }

    try {
        return (Get-Item -LiteralPath $Path).LastWriteTimeUtc -ge $NotBeforeUtc.AddSeconds(-5)
    } catch {
        return $false
    }
}

function Get-ExecutedAssemblyReport([string]$AssemblyReportPath) {
    if (-not (Test-Path $AssemblyReportPath)) {
        return $null
    }

    try {
        $report = Get-Content -LiteralPath $AssemblyReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($report.executed) {
            return [pscustomobject]@{
                Report = $report
                ReportPath = $AssemblyReportPath
                FinalOutputPath = $report.finalOutputPath
                PrecisionMode = $report.precisionMode
                Source = 'session-report'
            }
        }
    } catch {
    }

    return $null
}

function Wait-AssemblyEvidence(
    [string]$AssemblyReportPath,
    [string]$ExpectedFinalOutputPath,
    [string]$FinalOutputPathHint,
    [datetime]$NotBeforeUtc,
    [int]$TimeoutSec = 600
) {
    Wait-Until -Description 'assembly evidence (report or final output)' -TimeoutSec $TimeoutSec -PollMs 1000 -Condition {
        $executedReport = Get-ExecutedAssemblyReport -AssemblyReportPath $AssemblyReportPath
        if ($executedReport) {
            return $executedReport
        }

        foreach ($candidate in @($ExpectedFinalOutputPath, $FinalOutputPathHint) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique) {
            if (Test-RecentFile -Path $candidate -NotBeforeUtc $NotBeforeUtc) {
                return [pscustomobject]@{
                    Report = $null
                    ReportPath = $null
                    FinalOutputPath = $candidate
                    PrecisionMode = 'unknown (final-output fallback)'
                    Source = 'final-output-fallback'
                }
            }
        }

        return $null
    }
}

function Ensure-ObsRunning([string]$ObsDir, [bool]$StartObsIfNeeded) {
    $obsExe = Join-Path $ObsDir 'bin\64bit\obs64.exe'
    if (-not (Test-Path $obsExe)) {
        $detected = Get-ChildItem -LiteralPath $ObsDir -Recurse -Filter 'obs64.exe' -ErrorAction SilentlyContinue |
            Sort-Object FullName |
            Select-Object -First 1 -ExpandProperty FullName
        if ($detected) {
            $obsExe = $detected
        }
    }

    $proc = Get-Process -Name 'obs64' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) {
        return @{ Started = $false; Process = $proc }
    }

    if (-not $StartObsIfNeeded) {
        throw 'OBS Studio is not running, and StartObsIfNeeded is false.'
    }
    if (-not (Test-Path $obsExe)) {
        throw "OBS executable not found: $obsExe"
    }

    Write-Step 'Start OBS Studio'
    $started = Start-Process -FilePath $obsExe -PassThru
    Start-Sleep -Seconds 8
    return @{ Started = $true; Process = $started }
}

$dotnet = Join-Path $RepoRoot '.dotnet\dotnet.exe'
$modProject = Join-Path $RepoRoot 'CelesteReplay\CelesteAutoCut\CelesteAutoCut.csproj'
$sidecarProject = Join-Path $RepoRoot 'CelesteReplay\ObsClipSidecar\ObsClipSidecar.csproj'
$zipPath = Join-Path $RepoRoot 'CelesteReplay\CelesteAutoCut.zip'
$scriptsRoot = Join-Path $RepoRoot 'CelesteReplay\Scripts'
$testRoot = Join-Path $RepoRoot '.omx\real-zip-only-test'
$modsDir = Join-Path $CelesteDir 'Mods'
$cacheDir = Join-Path $modsDir 'Cache'
$modZipInstallPath = Join-Path $modsDir 'CelesteAutoCut.zip'
$modFolderInstallPath = Join-Path $modsDir 'CelesteAutoCut'
$modZipDuplicatePattern = 'CelesteAutoCut-*.zip'
$helperDir = Join-Path $CelesteDir 'CelesteAutoCutTools\ObsClipPanel'
$helperExe = Join-Path $helperDir 'ObsClipPanel.exe'
$replayRoot = Join-Path $CelesteDir 'CelesteAutoCutReplays'
$roomEventsPath = Join-Path $replayRoot 'room_events.jsonl'
$obsAutoRoot = Join-Path $replayRoot 'obs_auto'
$sessionsRoot = Join-Path $obsAutoRoot 'sessions'
$debugRcBaseUrl = 'http://localhost:32270'
$panelBaseUrl = 'http://127.0.0.1:38500'
$tasPath = Join-Path $RepoRoot 'CelesteTAS-master\CelesteTAS-master\1A.tas'
$bootstrapTasPath = Join-Path $testRoot 'bootstrap-to-level.tas'
$blacklistPath = Join-Path $modsDir 'blacklist.txt'
$cleanupRoot = Join-Path $testRoot 'backups'
$celesteExe = Join-Path $CelesteDir 'Celeste.exe'
$startedGame = $null
$startedObs = $null
$restoreFolderBackup = $null
$restoreZipBackup = $null
$originalBlacklist = $null
$blacklistBackupPath = Join-Path $cleanupRoot 'blacklist.txt.backup'
$temporaryBlacklistMarker = 'Temporary Codex test blacklist for CelesteReplay-only boot validation.'
$hadModFolderBefore = $false
$hadModZipBefore = $false
$finalSummary = $null
$transcriptStarted = $false
$transcriptPath = Join-Path $testRoot 'real-zip-only-test-transcript.txt'

New-Item -ItemType Directory -Force -Path $testRoot, $cleanupRoot | Out-Null

try {
    Start-Transcript -LiteralPath $transcriptPath -Force | Out-Null
    $transcriptStarted = $true

    Write-Step 'Run ObsClipSidecar self-test for multi-recording edge coverage'
    & $dotnet run --project $sidecarProject -- self-test
    if ($LASTEXITCODE -ne 0) {
        throw 'ObsClipSidecar self-test failed.'
    }

    Write-Step 'Publish release zip'
    $env:DOTNET_CLI_HOME = (Join-Path $RepoRoot '.dotnet_home')
    & $dotnet publish $modProject -c Release
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed.'
    }
    if (-not (Test-Path $zipPath)) {
        throw "Published zip not found: $zipPath"
    }

    Write-Step 'Prepare Mods install state'
    Stop-LiveProcessesByPath -ExeName 'Celeste.exe' -ExecutablePath $celesteExe
    Stop-ObsClipPanelProcesses -CelesteDir $CelesteDir
    Start-Sleep -Seconds 2
    if (Test-Path $blacklistPath) {
        $currentBlacklist = Get-Content -LiteralPath $blacklistPath -Raw -Encoding UTF8
        if ($currentBlacklist -match [Regex]::Escape($temporaryBlacklistMarker) -and (Test-Path $blacklistBackupPath)) {
            Write-Step 'Restore stale blacklist backup from previous interrupted run'
            $currentBlacklist = Get-Content -LiteralPath $blacklistBackupPath -Raw -Encoding UTF8
            [System.IO.File]::WriteAllText($blacklistPath, $currentBlacklist, [System.Text.UTF8Encoding]::new($false))
        }

        $originalBlacklist = $currentBlacklist
        [System.IO.File]::WriteAllText($blacklistBackupPath, $originalBlacklist, [System.Text.UTF8Encoding]::new($false))
        $filtered = ($originalBlacklist -split "`r?`n") | Where-Object {
            $_ -notin @('CelesteAutoCut.zip', 'CelesteReplay.zip', 'CelesteTAS.zip', 'SpeedrunTool.zip')
        }
        [System.IO.File]::WriteAllText($blacklistPath, (($filtered -join [Environment]::NewLine).TrimEnd() + [Environment]::NewLine), [System.Text.UTF8Encoding]::new($false))
    }

    $hadModFolderBefore = Test-Path $modFolderInstallPath
    $hadModZipBefore = Test-Path $modZipInstallPath

    if (Test-Path $modFolderInstallPath) {
        $restoreFolderBackup = Join-Path $cleanupRoot 'CelesteAutoCut-folder-backup'
        Remove-Item -LiteralPath $restoreFolderBackup -Recurse -Force -ErrorAction SilentlyContinue
        Move-Item -LiteralPath $modFolderInstallPath -Destination $restoreFolderBackup
    }

    if (Test-Path $modZipInstallPath) {
        $restoreZipBackup = Join-Path $cleanupRoot 'CelesteAutoCut.zip.backup'
        Copy-Item -LiteralPath $modZipInstallPath -Destination $restoreZipBackup -Force
    }

    Get-ChildItem -LiteralPath $modsDir -Filter $modZipDuplicatePattern -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue

    Copy-Item -LiteralPath $zipPath -Destination $modZipInstallPath -Force
    Get-ChildItem -LiteralPath $cacheDir -Filter 'CelesteReplay*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $cacheDir -Filter 'CelesteAutoCut*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $cacheDir -Filter 'CelesteTAS*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $cacheDir -Filter 'SpeedrunTool*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Write-Step 'Clean helper/session state'
    Stop-ObsClipPanelProcesses -CelesteDir $CelesteDir
    Remove-Item -LiteralPath $helperDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $sessionsRoot -Recurse -Force -ErrorAction SilentlyContinue

    $obsDefaultRecordingDir = Get-ObsDefaultRecordingDirectory
    if ($obsDefaultRecordingDir) {
        New-Item -ItemType Directory -Force -Path $obsDefaultRecordingDir | Out-Null
        $legacyStaleFinal = Join-Path $obsDefaultRecordingDir 'final_useful_run.mp4'
        Remove-Item -LiteralPath $legacyStaleFinal -Force -ErrorAction SilentlyContinue
    }

    $obsState = Ensure-ObsRunning -ObsDir $ObsDir -StartObsIfNeeded $StartObsIfNeeded
    if ($obsState.Started) {
        $startedObs = $obsState.Process
    }

    Write-Step 'Restart Celeste'
    Stop-LiveProcessesByPath -ExeName 'Celeste.exe' -ExecutablePath $celesteExe
    $startedGame = Start-Process -FilePath $celesteExe -PassThru

    Write-Step 'Wait for DebugRC'
    Wait-DebugRc -BaseUrl $debugRcBaseUrl | Out-Null

    Write-Step 'Wait for helper API and connect OBS'
    $snapshot = Wait-PanelSnapshot -BaseUrl $panelBaseUrl -TimeoutSec 240
    try {
        Invoke-JsonPost -Url "$panelBaseUrl/api/obs/stop-record" | Out-Null
    } catch {
    }
    Start-Sleep -Seconds 3
    try {
        $snapshot = Invoke-JsonGet -Url "$panelBaseUrl/api/status" -TimeoutSec 5
    } catch {
    }

    $snapshot = Wait-Until -Description 'OBS websocket connected' -TimeoutSec 360 -PollMs 3000 -Condition {
        try {
            $current = Invoke-JsonGet -Url "$panelBaseUrl/api/status" -TimeoutSec 5
            if ($current) {
                $connected = [bool](Get-PropValue $current.status @('obsConnected', 'ObsConnected'))
                $identified = [bool](Get-PropValue $current.status @('obsIdentified', 'ObsIdentified'))
                if ($connected -and $identified) {
                    return $current
                }
            }
        } catch {
        }

        try {
            Invoke-JsonPost -Url "$panelBaseUrl/api/obs/connect" -TimeoutSec 10 | Out-Null
        } catch {
        }

        return $null
    }

    if ($snapshot.status.paths.finalOutputPath) {
        Remove-Item -LiteralPath $snapshot.status.paths.finalOutputPath -Force -ErrorAction SilentlyContinue
    }

    $preSessionId = $snapshot.status.currentSessionId

    Write-Step 'Bootstrap into a level'
    @"
console load 1
 240
"@ | Set-Content -LiteralPath $bootstrapTasPath -Encoding UTF8
    Play-Tas -DebugRcBaseUrl $debugRcBaseUrl -TasPath $bootstrapTasPath
    Wait-TasCompletion -DebugRcBaseUrl $debugRcBaseUrl -TimeoutSec 240 | Out-Null

    Write-Step 'Start recording through helper API'
    $recordStartRequestedUtc = (Get-Date).ToUniversalTime()
    Invoke-JsonPost -Url "$panelBaseUrl/api/obs/start-record" | Out-Null
    $recordingStart = Wait-RecordingStart -BaseUrl $panelBaseUrl -SessionsRoot $sessionsRoot -NotBeforeUtc $recordStartRequestedUtc -TimeoutSec 90
    $snapshot = $recordingStart.Snapshot
    $recordingSessionId = $recordingStart.SessionId
    $recordingSessionDir = $recordingStart.SessionDirectory
    $sessionFinalOutputPathHint = $recordingStart.FinalOutputPath

    if ($snapshot -eq $null) {
        Start-Sleep -Seconds 2
        try {
            $snapshot = Invoke-JsonGet -Url "$panelBaseUrl/api/status" -TimeoutSec 5
            if (-not $sessionFinalOutputPathHint) {
                $sessionFinalOutputPathHint = $snapshot.status.paths.finalOutputPath
            }
        } catch {
        }
    }

    Write-Host "Recording session: $recordingSessionId (source=$($recordingStart.Source))" -ForegroundColor Yellow
    Write-Host "Session dir: $recordingSessionDir" -ForegroundColor Yellow
    Start-Sleep -Seconds 1

    Write-Step 'Play 1A TAS'
    $mainTasStartUtc = (Get-Date).ToUniversalTime()
    Play-Tas -DebugRcBaseUrl $debugRcBaseUrl -TasPath $tasPath
    $tasStarted = Wait-TasStarted -DebugRcBaseUrl $debugRcBaseUrl -MinimumTotalFrames 1000 -TimeoutSec 60
    Write-Host "Detected 1A TAS start: frame $((Get-PropValue $tasStarted @('CurrentFrame', 'currentFrame')))/$((Get-PropValue $tasStarted @('TotalFrames', 'totalFrames'))), state=$((Get-PropValue $tasStarted @('State', 'state')))" -ForegroundColor Yellow
    $tasRoomSession = Wait-RoomEventSessionStart -Path $roomEventsPath -AfterUtc $mainTasStartUtc -TimeoutSec 120
    if ($tasRoomSession.SessionId) {
        Write-Host "Detected room event session: $($tasRoomSession.SessionId)" -ForegroundColor Yellow
    }
    Wait-RoomEventSessionComplete -Path $roomEventsPath -AfterUtc $mainTasStartUtc -SessionId $tasRoomSession.SessionId -TimeoutSec 1800 | Out-Null
    Start-Sleep -Seconds 2
    $tasCompletion = Wait-TasIdle -DebugRcBaseUrl $debugRcBaseUrl -TimeoutSec 120
    $tasState = Get-PropValue $tasCompletion @('State', 'state')
    $tasCurrentFrame = [int64](Get-PropValue $tasCompletion @('CurrentFrame', 'currentFrame'))
    $tasTotalFrames = [int64](Get-PropValue $tasCompletion @('TotalFrames', 'totalFrames'))

    Write-Step 'Stop recording'
    Invoke-JsonPost -Url "$panelBaseUrl/api/obs/stop-record" | Out-Null
    Wait-RecordingStop -BaseUrl $panelBaseUrl -SessionDirectory $recordingSessionDir -TimeoutSec 120 | Out-Null

    Write-Step 'Wait for captured session assembly artifacts'
    $clipIntervalsPath = Join-Path $recordingSessionDir 'clip_intervals.json'
    $manifestPath = Join-Path $recordingSessionDir 'session_manifest.json'
    $assemblyReportPath = Join-Path $recordingSessionDir 'assembly\assembly_report.json'
    Wait-File -Path $clipIntervalsPath -Description 'clip_intervals.json' -TimeoutSec 240 | Out-Null
    Wait-File -Path $manifestPath -Description 'session_manifest.json' -TimeoutSec 240 | Out-Null

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $clipDoc = Get-Content -LiteralPath $clipIntervalsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $recordingFiles = @($manifest.recordings | ForEach-Object { $_.files } | ForEach-Object { $_ })
    $firstRecordingPath = $null
    if ($recordingFiles.Count -gt 0) {
        $firstRecordingPath = $recordingFiles[0].path
    }
    $expectedFinalOutputFileName = $null
    if ($firstRecordingPath) {
        $expectedFinalOutputFileName = ([System.IO.Path]::GetFileNameWithoutExtension($firstRecordingPath)) + '.mp4'
    } elseif (@($manifest.recordings).Count -gt 0 -and $manifest.recordings[0].startUtc) {
        $expectedFinalOutputFileName = ([DateTimeOffset]::Parse([string]$manifest.recordings[0].startUtc).ToLocalTime().ToString('yyyy-MM-dd HH-mm-ss')) + '.mp4'
    }
    $primaryMapSid = $null
    if (@($clipDoc.clips).Count -gt 0) {
        $primaryMapSid = Get-PropValue $clipDoc.clips[0] @('mapSid', 'MapSid')
    }
    $mapFolderName = Get-MapFolderName -MapSid $primaryMapSid

    $expectedOutputDirRoot = if ($firstRecordingPath) {
        Split-Path -Parent $firstRecordingPath
    } elseif ($obsDefaultRecordingDir) {
        $obsDefaultRecordingDir
    } else {
        Split-Path -Parent $sessionFinalOutputPathHint
    }
    $expectedOutputDir = if ($expectedOutputDirRoot) {
        Join-Path $expectedOutputDirRoot $mapFolderName
    } else {
        $null
    }
    $expectedFinalOutputPath = if ($expectedOutputDir -and $expectedFinalOutputFileName) {
        Join-Path $expectedOutputDir $expectedFinalOutputFileName
    } else {
        $sessionFinalOutputPathHint
    }

    $assemblyReport = $null
    if (Test-Path $assemblyReportPath) {
        try {
            $assemblyReport = Get-Content -LiteralPath $assemblyReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
        } catch {
        }
    }

    if (@($clipDoc.clips).Count -eq 0) {
        if (-not $assemblyReport) {
            throw "Auto-assemble did not produce any valid clips. validClips=0; invalidClips=$(@($clipDoc.invalidClips).Count); assembly report missing at '$assemblyReportPath'."
        }
        $warningText = @($assemblyReport.warnings) -join ', '
        $invalidReasons = @($clipDoc.invalidClips | ForEach-Object { @($_.reasons) } | ForEach-Object { $_ }) | Select-Object -Unique
        $invalidReasonText = ($invalidReasons -join ', ')
        throw "Auto-assemble did not produce any valid clips. executed=$($assemblyReport.executed); validClips=$(@($clipDoc.clips).Count); invalidClips=$(@($clipDoc.invalidClips).Count); warnings=[$warningText]; reasons=[$invalidReasonText]"
    }
    $assemblyEvidence = Wait-AssemblyEvidence -AssemblyReportPath $assemblyReportPath -ExpectedFinalOutputPath $expectedFinalOutputPath -FinalOutputPathHint $sessionFinalOutputPathHint -NotBeforeUtc $recordStartRequestedUtc -TimeoutSec 600
    if ($assemblyEvidence.Report) {
        $assemblyReport = $assemblyEvidence.Report
    } elseif (-not $assemblyReport) {
        $assemblyReport = [pscustomobject]@{
            schemaVersion = 1
            precisionMode = 'unknown (final-output fallback)'
            dryRun = $false
            fastPreviewCopy = $false
            executed = $true
            ffmpegPath = ''
            finalOutputPath = $assemblyEvidence.FinalOutputPath
            warnings = @('assembly_report.json was not observed at the original session path; accepted recent final output as completion evidence.')
        }
    }

    $finalOutputPath = if ($assemblyEvidence.FinalOutputPath) { $assemblyEvidence.FinalOutputPath } else { $assemblyReport.finalOutputPath }
    if (-not $finalOutputPath) {
        $finalOutputPath = $sessionFinalOutputPathHint
    }
    if (-not $finalOutputPath) {
        throw 'Could not determine final output path from assembly evidence, assembly report, or helper snapshot.'
    }
    Wait-File -Path $finalOutputPath -Description 'final output video' -TimeoutSec 60 | Out-Null

    $helperExtracted = Test-Path $helperExe
    $actualOutputDir = Split-Path -Parent $finalOutputPath
    if ($expectedOutputDir) {
        $expectedFull = [System.IO.Path]::GetFullPath($expectedOutputDir)
        $actualFull = [System.IO.Path]::GetFullPath($actualOutputDir)
        if ($expectedFull -ne $actualFull) {
            throw "Final output directory mismatch. Expected '$expectedFull', got '$actualFull'."
        }
    }

    if ($expectedFinalOutputFileName) {
        $actualFinalOutputFileName = Split-Path -Leaf $finalOutputPath
        $expectedStem = [System.IO.Path]::GetFileNameWithoutExtension($expectedFinalOutputFileName)
        $actualStem = [System.IO.Path]::GetFileNameWithoutExtension($actualFinalOutputFileName)
        if ($actualFinalOutputFileName -ne $expectedFinalOutputFileName -and -not $actualStem.StartsWith($expectedStem, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Final output filename mismatch. Expected '$expectedFinalOutputFileName' (or a collision-safe variant starting with '$expectedStem'), got '$actualFinalOutputFileName'."
        }
    }

    $finalSummary = [pscustomobject]@{
        sessionId = $recordingSessionId
        sessionDir = $recordingSessionDir
        finalOutputPath = $finalOutputPath
        expectedFinalOutputFileName = $expectedFinalOutputFileName
        finalOutputDirectory = $actualOutputDir
        expectedOutputDirectory = $expectedOutputDir
        sourceRecordingPath = $firstRecordingPath
        clipCount = @($clipDoc.clips).Count
        invalidClipCount = @($clipDoc.invalidClips).Count
        precisionMode = $assemblyReport.precisionMode
        assemblyEvidenceSource = $assemblyEvidence.Source
        mapFolderName = $mapFolderName
        helperExtracted = $helperExtracted
        helperExe = $helperExe
        tasState = $tasState
        tasCurrentFrame = $tasCurrentFrame
        tasTotalFrames = $tasTotalFrames
        obsDefaultRecordingDirectory = $obsDefaultRecordingDir
    }

    Write-Step 'Real test passed'
    $finalSummary | ConvertTo-Json -Depth 8
}
finally {
    if ($originalBlacklist -ne $null) {
        [System.IO.File]::WriteAllText($blacklistPath, $originalBlacklist, [System.Text.UTF8Encoding]::new($false))
    }

    if ($restoreFolderBackup -and (Test-Path $restoreFolderBackup)) {
        if (Test-Path $modFolderInstallPath) {
            Remove-Item -LiteralPath $modFolderInstallPath -Recurse -Force -ErrorAction SilentlyContinue
        }
        Move-Item -LiteralPath $restoreFolderBackup -Destination $modFolderInstallPath
    }

    if ($restoreZipBackup -and (Test-Path $restoreZipBackup)) {
        Copy-Item -LiteralPath $restoreZipBackup -Destination $modZipInstallPath -Force
    }
    elseif ((-not $hadModZipBefore) -and (($hadModFolderBefore) -or $RestoreModsOnExit) -and (Test-Path $modZipInstallPath)) {
        Remove-Item -LiteralPath $modZipInstallPath -Force -ErrorAction SilentlyContinue
    }

    if ((Test-Path $blacklistBackupPath) -and $RestoreModsOnExit) {
        Remove-Item -LiteralPath $blacklistBackupPath -Force -ErrorAction SilentlyContinue
    }

    if (-not $KeepGameOpen -and $startedGame) {
        Stop-Process -Id $startedGame.Id -Force -ErrorAction SilentlyContinue
    }

    if (-not $KeepObsOpen -and $startedObs) {
        Stop-Process -Id $startedObs.Id -Force -ErrorAction SilentlyContinue
    }

    if ($transcriptStarted) {
        Stop-Transcript | Out-Null
    }
}


