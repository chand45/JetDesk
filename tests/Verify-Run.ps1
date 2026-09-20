[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [ValidateSet('completed', 'limit_reached', 'stopped', 'needs_attention', 'error')]
    [string]$ExpectedStatus = 'completed',
    [int]$ExpectedMaximum = 50,
    [string]$FixtureEventsPath,
    [string]$ExpectedTrack = ('Sweater Weather ' + [char]0x2014 + ' The Neighbourhood'),
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$checks = [System.Collections.Generic.List[string]]::new()

function Assert-Condition([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw "Verification failed: $Description" }
    $checks.Add($Description)
}

$resultRaw = Get-Content -LiteralPath $ResultPath -Raw
$result = $resultRaw | ConvertFrom-Json
Assert-Condition ($result.Status -eq $ExpectedStatus) "Result status is $ExpectedStatus."
Assert-Condition ($result.Operations -ge 0 -and $result.Operations -le $ExpectedMaximum) "Operation count is between 0 and $ExpectedMaximum."
Assert-Condition (Test-Path -LiteralPath $result.LogPath -PathType Leaf) 'Result points to an existing trace.'

$traceRaw = Get-Content -LiteralPath $result.LogPath -Raw
$trace = @(Get-Content -LiteralPath $result.LogPath | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
Assert-Condition ($trace.Count -ge 2) 'Trace contains start and terminal evidence.'
Assert-Condition ($trace[0].kind -eq 'start') 'Trace begins with run configuration.'
Assert-Condition ($trace[-1].kind -eq 'result') 'Trace ends with a result.'
Assert-Condition ($trace[-1].data.Status -eq $result.Status -and $trace[-1].data.Operations -eq $result.Operations) 'Result file agrees with terminal trace record.'
Assert-Condition ($trace[0].data.MaxOperations -eq $ExpectedMaximum) 'Actual run used the expected operation maximum.'

$observations = @($trace | Where-Object kind -eq 'observation')
$decisions = @($trace | Where-Object kind -eq 'decision')
$actions = @($trace | Where-Object { $_.kind -in @('action', 'action_error', 'action_cancelled') })
$verifications = @($trace | Where-Object kind -eq 'verification')
Assert-Condition ($actions.Count -le $result.Operations) 'Recorded input attempts do not exceed counted operations.'
Assert-Condition (@($actions | Where-Object { $_.data.Step -gt $ExpectedMaximum }).Count -eq 0) 'No input attempt exceeds the configured operation limit.'

$previousActionStep = 0
$latestObservation = -1
$latestDecision = -1
for ($index = 0; $index -lt $trace.Count; $index++) {
    $record = $trace[$index]
    if ($record.kind -eq 'observation') { $latestObservation = $index }
    if ($record.kind -eq 'decision') {
        Assert-Condition ($latestObservation -ge 0 -and $latestObservation -gt $latestDecision) "Decision at trace record $index follows a newly observed screen."
        $latestDecision = $index
    }
    if ($record.kind -in @('action', 'action_error', 'action_cancelled')) {
        Assert-Condition ($latestDecision -gt $latestObservation) "Input at trace record $index follows a Jev decision."
        Assert-Condition ($record.data.Operation -eq $trace[$latestDecision].data.Operation) "Input at trace record $index matches Jev's chosen operation."
        Assert-Condition ($record.data.Step -gt $previousActionStep) "Input step $($record.data.Step) advances monotonically."
        $previousActionStep = $record.data.Step
    }
}

if ($ExpectedStatus -eq 'completed') {
    Assert-Condition ($observations.Count -gt 0 -and $verifications.Count -gt 0) 'Completion includes observations and an explicit verification.'
    Assert-Condition ([bool]$verifications[-1].data.Achieved) 'Final Jev verification positively confirms completion.'
    $verificationIndex = -1
    $lastInputIndex = -1
    for ($index = 0; $index -lt $trace.Count; $index++) {
        if ($trace[$index].kind -eq 'verification') { $verificationIndex = $index }
        if ($trace[$index].kind -in @('action', 'action_error', 'action_cancelled')) { $lastInputIndex = $index }
    }
    $freshObservationFound = $false
    for ($index = $lastInputIndex + 1; $index -lt $verificationIndex; $index++) {
        if ($trace[$index].kind -eq 'observation') { $freshObservationFound = $true }
    }
    Assert-Condition $freshObservationFound 'Completion verification used a screen observed after the last attempted input.'
}
if ($ExpectedStatus -eq 'limit_reached') {
    Assert-Condition ($result.Operations -eq $ExpectedMaximum) 'Limit result stops at exactly the configured maximum.'
    Assert-Condition ($verifications.Count -gt 0 -and -not [bool]$verifications[-1].data.Achieved) 'Limit result follows a negative fresh-screen Jev verification.'
}

# Test the real secret values without displaying them or passing them through child-process arguments.
$keys = @([EnvironmentVariableTarget]::Process, [EnvironmentVariableTarget]::User, [EnvironmentVariableTarget]::Machine) |
    ForEach-Object { [Environment]::GetEnvironmentVariable('JEV_KEY', $_) } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_.Length -ge 8 } |
    Select-Object -Unique
foreach ($secret in $keys) {
    Assert-Condition (-not $traceRaw.Contains($secret) -and -not $resultRaw.Contains($secret)) 'Trace and result omit the configured API key.'
}

$fixtureSummary = $null
if ($FixtureEventsPath) {
    $events = @(Get-Content -LiteralPath $FixtureEventsPath | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    $searchIndex = -1
    $selectIndex = -1
    $playIndex = -1
    for ($index = 0; $index -lt $events.Count; $index++) {
        $event = $events[$index]
        if ($event.operation -eq 'search' -and $event.details.resultCount -gt 0) { $searchIndex = $index }
        if ($event.operation -eq 'select_track' -and $event.details.track -eq $ExpectedTrack) { $selectIndex = $index }
        if ($event.operation -eq 'play' -and $event.details.track -eq $ExpectedTrack) { $playIndex = $index }
    }
    Assert-Condition ($searchIndex -ge 0 -and $selectIndex -gt $searchIndex -and $playIndex -gt $selectIndex) 'Independent native fixture recorded successful search, correct selection, and playback in order.'
    $ticks = @($events | Where-Object { $_.operation -eq 'playback_tick' -and $_.details.track -eq $ExpectedTrack })
    Assert-Condition ($ticks.Count -gt 0 -and $ticks[-1].details.elapsedSeconds -gt 0) 'Independent native fixture timer advanced after playback.'
    $observedPlayback = @($observations | Where-Object {
        @($_.data.Controls | Where-Object { $_.Name -eq "Now playing: $ExpectedTrack" -or $_.Value -eq "Now playing: $ExpectedTrack" }).Count -gt 0
    })
    Assert-Condition ($observedPlayback.Count -gt 0) 'Runner observed the fixture playback state through UI Automation.'
    $fixtureSummary = [ordered]@{ track = $ExpectedTrack; eventCount = $events.Count; elapsedSeconds = $ticks[-1].details.elapsedSeconds; simulatedPlayback = $true }
}

$report = [ordered]@{
    verifiedAt = [DateTimeOffset]::UtcNow.ToString('o')
    status = 'passed'
    resultPath = [IO.Path]::GetFullPath($ResultPath)
    tracePath = [IO.Path]::GetFullPath($result.LogPath)
    runnerStatus = $result.Status
    operations = $result.Operations
    observations = $observations.Count
    decisions = $decisions.Count
    fixture = $fixtureSummary
    checks = $checks.ToArray()
}
$json = $report | ConvertTo-Json -Depth 8
if ($ReportPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath), $json) }
$json
