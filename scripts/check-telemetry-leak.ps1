<#
.SYNOPSIS
    Live check that captured request data never reaches Log Analytics or Application Insights
    (PLAN.md section 7, phase 3 definition of done).

.DESCRIPTION
    Sends requests containing a unique secret in the body, headers, query and path to the live
    capture route, waits until the app's own log lines from those requests have been ingested
    (positive control), then searches every table in the workspace for the secret and the token.

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
    $json = az monitor log-analytics query --workspace $Workspace --analytics-query $Query -o json --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw "Log Analytics query failed: $Query" }
    return @($json | ConvertFrom-Json)
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

# Positive control: the app logs one line per capture to stdout, which Container Apps
# ships to ContainerAppConsoleLogs_CL without sampling.
$expected = $RequestsPerToken * $tokens.Count
$controlQuery = @"
ContainerAppConsoleLogs_CL
| where TimeGenerated > datetime($start)
| where Log_s has 'Capture rejected' or Log_s has 'Captured request'
| count
"@
$deadline = (Get-Date).AddMinutes($WaitMinutes)
$seen = 0
while ((Get-Date) -lt $deadline) {
    $seen = [int](Invoke-Kql $workspace $controlQuery)[0].Count
    Write-Host "Control lines ingested: $seen / $expected"
    if ($seen -ge $expected) { break }
    Start-Sleep -Seconds 60
}

# Give Application Insights (a separate ingestion path) a little extra time.
Start-Sleep -Seconds 120

$needles = @($secret) + @($Token | Where-Object { $_ })
$leaks = @()
foreach ($needle in $needles) {
    $leakQuery = "search `"*$needle*`" | where TimeGenerated > datetime($start) | project TimeGenerated, `$table | take 20"
    $leaks += Invoke-Kql $workspace $leakQuery
}

if ($leaks.Count -gt 0) {
    Write-Host 'FAIL: captured data found in telemetry:' -ForegroundColor Red
    $leaks | Format-Table | Out-String | Write-Host
    exit 1
}
if ($seen -lt $expected) {
    Write-Host "INCONCLUSIVE: only $seen of $expected control lines arrived. Run again later." -ForegroundColor Yellow
    exit 2
}
Write-Host "PASS: $seen control lines ingested; the secret and token appear in no table." -ForegroundColor Green
