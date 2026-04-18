<#
.SYNOPSIS
    Live check that captured request data never reaches Log Analytics or Application Insights
    (PLAN.md section 7, phase 3 definition of done).

.DESCRIPTION
    Sends requests containing a unique secret in the body, headers, query and path to the live
    capture route. Waits for two positive controls: the app's log lines for those requests in
    the container logs, and a marker sent to an ordinary page in Application Insights. Then
    checks that the search finds known text, and searches every table for the secret and token.

    PASS          the control lines arrived and the secret was found nowhere
    FAIL          the secret or the token was found (the matching rows are printed)
    INCONCLUSIVE  the control lines did not arrive in time; run again later

    Requires the Azure CLI, logged in to the subscription (az login).

.EXAMPLE
    ./scripts/check-telemetry-leak.ps1 -BaseUrl https://ca-webhook-inspector.<id>.norwayeast.azurecontainerapps.io -Token <capture token>
#>
param(
    [Parameter(Mandatory)] [string] $BaseUrl,
    # Capture token of one of your endpoints. Without it only the unknown-token path is checked.
    [string] $Token,
    [string] $ResourceGroup = 'rg-webhook-inspector',
    [string] $WorkspaceName = 'log-webhook-inspector',
    [int] $RequestsPerToken = 10,
    [int] $WaitMinutes = 20
)

$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')

function Invoke-Kql([string] $Workspace, [string] $Query) {
    # The query goes through a file: quotes inside it do not survive az.cmd on Windows.
    $file = New-TemporaryFile
    try {
        Set-Content -Path $file -Value $Query -NoNewline -Encoding ascii
        $lines = az monitor log-analytics query --workspace $Workspace --analytics-query "@$file" -o json --only-show-errors
        if ($LASTEXITCODE -ne 0) { throw "Log Analytics query failed: $Query" }
    }
    finally {
        Remove-Item $file -ErrorAction SilentlyContinue
    }
    # az prints JSON over several lines, and Windows PowerShell does not unroll a parsed
    # JSON array by itself, so join first and unroll explicitly.
    $parsed = ($lines -join "`n") | ConvertFrom-Json
    return @($parsed | ForEach-Object { $_ })
}

# Every table in the workspace, any column, substring match.
function Find-InWorkspace([string] $Workspace, [string] $Text, [string] $Since) {
    return Invoke-Kql $Workspace "union * | where TimeGenerated > datetime($Since) | where * contains '$Text' | project TimeGenerated, Type | take 20"
}

$workspace = az monitor log-analytics workspace show -g $ResourceGroup -n $WorkspaceName --query customerId -o tsv --only-show-errors
if (-not $workspace) { throw "Workspace $WorkspaceName not found in $ResourceGroup." }

$secret = 'leakcheck' + [guid]::NewGuid().ToString('N')
$alphabet = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'
$unknownToken = -join (1..22 | ForEach-Object { $alphabet[(Get-Random -Maximum $alphabet.Length)] })
$tokens = @($unknownToken) + @($Token | Where-Object { $_ })
$start = (Get-Date).ToUniversalTime().AddMinutes(-1).ToString('o')

Write-Host "Secret: $secret"
Write-Host "Sending $RequestsPerToken requests to each of $($tokens.Count) token(s)..."
$body = Join-Path ([IO.Path]::GetTempPath()) "leakcheck-$secret.json"
Set-Content -Path $body -Value "{`"secret`":`"$secret-body`"}" -NoNewline
try {
    foreach ($t in $tokens) {
        for ($i = 0; $i -lt $RequestsPerToken; $i++) {
            $code = curl.exe -s -o NUL -w '%{http_code}' -X POST `
                -H 'Content-Type: application/json' `
                -H "X-Secret: $secret-header" `
                -H "Authorization: Bearer $secret-auth" `
                -A "agent-$secret" `
                --data-binary "@$body" `
                "$BaseUrl/h/$t/$secret-path?key=$secret-query"
            Write-Host "  $($t.Substring(0, 4))...: $code"
        }
    }
}
finally {
    Remove-Item $body -ErrorAction SilentlyContinue
}

# Positive control for Application Insights: requests to an ordinary page are traced with
# their path (only /h/* is redacted). 16 requests at 25 % sampling are all dropped with a
# probability of about 1 %. Finding this marker proves App Insights data has arrived.
$marker = 'leakcontrol' + [guid]::NewGuid().ToString('N')
for ($i = 0; $i -lt 16; $i++) {
    curl.exe -s -o NUL "$BaseUrl/$marker" | Out-Null
}

# Positive control for container logs: the app logs one line per capture to stdout, which
# Container Apps ships to ContainerAppConsoleLogs_CL without sampling.
$expected = $RequestsPerToken * $tokens.Count
$controlQuery = @"
ContainerAppConsoleLogs_CL
| where TimeGenerated > datetime($start)
| where Log_s has 'Capture rejected' or Log_s has 'Captured request'
| summarize lines = count()
"@
$deadline = (Get-Date).AddMinutes($WaitMinutes)
$seen = 0
$traced = 0
while ((Get-Date) -lt $deadline) {
    $seen = [int](Invoke-Kql $workspace $controlQuery)[0].lines
    $traced = (Find-InWorkspace $workspace $marker $start).Count
    Write-Host "Control: $seen / $expected log lines, App Insights marker found: $($traced -gt 0)"
    if ($seen -ge $expected -and $traced -gt 0) { break }
    Start-Sleep -Seconds 60
}

# Self-check: the same search must find text that is known to be in the logs, or a
# broken query would report "no leaks" for the wrong reason.
if ((Find-InWorkspace $workspace 'Captured request' $start).Count -eq 0) {
    Write-Host 'INCONCLUSIVE: the search did not find the known control text, so a clean result would mean nothing.' -ForegroundColor Yellow
    exit 2
}

$needles = @($secret) + @($Token | Where-Object { $_ })
$leaks = @()
foreach ($needle in $needles) {
    $leaks += Find-InWorkspace $workspace $needle $start
}

if ($leaks.Count -gt 0) {
    Write-Host 'FAIL: captured data found in telemetry:' -ForegroundColor Red
    $leaks | Format-Table | Out-String | Write-Host
    exit 1
}
if ($seen -lt $expected -or $traced -eq 0) {
    Write-Host "INCONCLUSIVE: control data incomplete ($seen of $expected log lines, App Insights marker found: $($traced -gt 0)). Run again later." -ForegroundColor Yellow
    exit 2
}
Write-Host "PASS: both controls arrived ($seen log lines, App Insights marker found); the secret and token appear in no table." -ForegroundColor Green
