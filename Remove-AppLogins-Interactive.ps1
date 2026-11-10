<#
.SYNOPSIS
  Interactive disabling (enabled = 0) of all logins that start with "<name>_" in [AlterianAuth].[AUTH].[LoginIndex]
  on every server of one environment. Rows are NOT deleted.

.DESCRIPTION
  Equivalent of:
    SELECT * FROM [AlterianAuth].[AUTH].[LoginIndex] WHERE login LIKE 'ABISHEKK[_]%'
    UPDATE ... SET enabled = 0 WHERE login = <each matched login> AND enabled <> 0
  You type only the name (ABISHEKK). The script adds the literal underscore and the % itself.

.FLOW
  1. Ask for the name (prefix)
  2. Look up matching rows on ALL servers and show them
  3. Ask you to confirm these are the right rows
  4. Save a full copy (SELECT *) of the rows to a CSV
  5. Ask for a final typed confirmation (DISABLE, or DISABLE PROD for Prod)
  6. Set enabled = 0 on the matched logins that are still enabled (exact login value) on each server
  7. Re-run the lookup on all servers to verify no matching login is still enabled

.PARAMETER PreviewOnly
  Runs steps 1-4 only. Nothing is changed.

.PARAMETER UseSqlLogin
  Prompt for a SQL login. Without it, the current Windows identity is used
  (use "runas /netonly" first if you need a different domain account).

.PARAMETER TrustServerCertificate
  Use if you get certificate / encryption errors with dbatools 2.x.

.EXAMPLES
  .\Remove-AppLogins-Interactive.ps1 -Environment Dev -PreviewOnly
  .\Remove-AppLogins-Interactive.ps1 -Environment Dev
  .\Remove-AppLogins-Interactive.ps1 -Environment Prod -UseSqlLogin
#>
param(
    [Parameter(Mandatory)][ValidateSet('Dev','Test','Prod')] [string]$Environment,
    [switch]$UseSqlLogin,
    [switch]$TrustServerCertificate,
    [switch]$PreviewOnly,
    [string]$ServerFile = (Join-Path $PSScriptRoot 'servers.csv')
)

# ---------------- EDIT THESE ----------------
$table           = '[AlterianAuth].[AUTH].[LoginIndex]'   # Database.Schema.Table
$loginColumn     = 'login'                                # column that holds the login name
$enabledColumn   = 'enabled'                              # column set to 0 to disable a login
$minPrefixLength = 3                                      # safety: refuse very short prefixes
# --------------------------------------------

if (-not (Get-Module -ListAvailable dbatools)) {
    Write-Host "dbatools not found. Installing for current user..."
    Install-Module dbatools -Scope CurrentUser -Force
}
Import-Module dbatools

# Matches rows whose login starts with <prefix>_  (the pattern is passed as a parameter, never concatenated)
$selectQuery  = "SELECT * FROM $table WHERE [$loginColumn] LIKE @Pattern ESCAPE '\' ORDER BY [$loginColumn]"
$disableQuery = "UPDATE $table SET [$enabledColumn] = 0 WHERE [$loginColumn] = @Login AND [$enabledColumn] <> 0; SELECT @@ROWCOUNT AS RowsUpdated;"

$connectArgs = @{}
if ($UseSqlLogin) { $connectArgs.SqlCredential = Get-Credential -Message "SQL login for $Environment" }
# Invoke-DbaQuery has no -TrustServerCertificate; dbatools uses a session config setting instead
if ($TrustServerCertificate) { Set-DbatoolsConfig -FullName sql.connection.trustcert -Value $true }

if (-not (Test-Path $ServerFile)) { Write-Warning "Server file not found: $ServerFile"; return }
$servers = @(Import-Csv $ServerFile | Where-Object Environment -eq $Environment |
    Select-Object @{ n = 'Server'; e = { if ($_.Server) { $_.Server } else { $_.Servers } } }, Environment |
    Where-Object { $_.Server })
if ($servers.Count -eq 0) { Write-Warning "No servers found for $Environment in $ServerFile"; return }

$stamp = Get-Date -Format yyyyMMdd_HHmm
$noise = 'RowError','RowState','Table','ItemArray','HasErrors'   # DataRow properties we don't want to display/export

# Escape LIKE wildcards in what the user typed, so the name is treated literally
function ConvertTo-LikeLiteral([string]$s) { $s -replace '([\\%_\[])', '\$1' }

# True when the row's enabled column is non-zero (NULL counts as not enabled, same as the UPDATE's WHERE)
function Test-Enabled($row) {
    $v = $row.$enabledColumn
    ($null -ne $v) -and -not ($v -is [DBNull]) -and ([int]$v -ne 0)
}

function Find-Matches([string]$Server, [string]$Pattern) {
    @(Invoke-DbaQuery -SqlInstance $Server @connectArgs -Query $selectQuery `
        -SqlParameter @{ Pattern = $Pattern } -EnableException)
}

# One status row per server (plus the raw rows)
function Get-StatusAllServers([string]$Pattern) {
    foreach ($s in $servers) {
        try {
            $rows    = @(Find-Matches $s.Server $Pattern)
            $enabled = @($rows | Where-Object { Test-Enabled $_ })
            [pscustomobject]@{
                Server  = $s.Server
                Status  = $(if ($enabled.Count -gt 0) { 'ENABLED' } elseif ($rows.Count -gt 0) { 'ALREADY DISABLED' } else { 'NOT PRESENT' })
                Count   = $rows.Count
                Enabled = $enabled.Count
                Rows    = $rows
                Error   = $null
            }
        } catch {
            [pscustomobject]@{ Server = $s.Server; Status = 'ERROR'; Count = $null; Enabled = $null; Rows = @(); Error = $_.Exception.Message }
        }
    }
}

function Show-Status($status) {
    $status | Select-Object Server, Status, Count, Enabled, Error | Format-Table -AutoSize
    $none = @($status | Where-Object Status -eq 'NOT PRESENT')
    $errs = @($status | Where-Object Status -eq 'ERROR')
    if ($none.Count -gt 0) { Write-Host ("No matches on: " + (($none.Server) -join ', ')) -ForegroundColor Yellow }
    if ($errs.Count -gt 0) { Write-Host ("Could not check (see Error column): " + (($errs.Server) -join ', ')) -ForegroundColor Red }
}

Write-Host "`nEnvironment: $Environment | Servers: $($servers.Count) | Table: $table" -ForegroundColor Cyan
if ($PreviewOnly) { Write-Host "PREVIEW ONLY: nothing will be changed." -ForegroundColor Cyan }

# ---------- Steps 1-3: ask for the name, preview on all servers, confirm ----------
$before = $null
$prefix = $null

while (-not $before) {
    $entered = (Read-Host "`nEnter the name to disable, WITHOUT the underscore, e.g. ABISHEKK (blank to quit)").Trim()
    if (-not $entered) { Write-Host "Cancelled."; return }

    # You typed ABISHEKK_ or ABISHEKK_% by habit? Strip it, the script adds it.
    $entered = $entered -replace '[_%]+$', ''
    if ($entered.Length -lt $minPrefixLength) {
        Write-Host "Name must be at least $minPrefixLength characters." -ForegroundColor Yellow
        continue
    }

    $pattern = (ConvertTo-LikeLiteral $entered) + '\_%'
    Write-Host "`nSearching all servers for: $loginColumn LIKE '$entered[_]%'" -ForegroundColor Cyan
    $status = @(Get-StatusAllServers $pattern)
    Show-Status $status

    $found = @($status | Where-Object { $_.Status -in 'ENABLED', 'ALREADY DISABLED' })
    if ($found.Count -eq 0) {
        Write-Host "No matching rows on any reachable server." -ForegroundColor Yellow
        continue
    }

    foreach ($f in $found) {
        Write-Host "`nMatches on $($f.Server) ($($f.Count) row(s), $($f.Enabled) enabled):" -ForegroundColor Green
        $f.Rows | Select-Object * -ExcludeProperty $noise | Format-Table -AutoSize
    }

    if (-not @($status | Where-Object Status -eq 'ENABLED')) {
        Write-Host "All matching logins are already disabled. Nothing to do." -ForegroundColor Yellow
        continue
    }

    $ok = Read-Host "Are these the correct logins to disable? (Y/N)"
    if ($ok -match '^(y|yes)$') { $before = $status; $prefix = $entered } else { Write-Host "OK, try again." }
}

$toDisable = @($before | Where-Object Status -eq 'ENABLED')
$skipped   = @($before | Where-Object Status -ne 'ENABLED')
$totalRows = ($toDisable | Measure-Object -Property Enabled -Sum).Sum

# ---------- Step 4: save a full copy of the rows before the change ----------
# SELECT * so every column is kept. Contains logins: store securely.
$beforeFile = Join-Path $PSScriptRoot "before_${Environment}_$stamp.csv"
$before | ForEach-Object {
    $srv = $_.Server
    $_.Rows | Select-Object @{n='Server';e={$srv}}, * -ExcludeProperty $noise
} | Export-Csv $beforeFile -NoTypeInformation
Write-Host "`nSaved a full copy of the matching rows to $beforeFile"

if ($PreviewOnly) { Write-Host "Preview only. Nothing was changed."; return }

# ---------- Step 5: final confirmation ----------
Write-Host "`nAbout to set $enabledColumn = 0 on $totalRows row(s) matching '$prefix[_]%' on $($toDisable.Count) server(s) in $Environment." -ForegroundColor Yellow
$errServers = @($skipped | Where-Object Status -eq 'ERROR')
if ($errServers.Count -gt 0) {
    Write-Host "WARNING: $($errServers.Count) server(s) could not be checked and will be SKIPPED: $(($errServers.Server) -join ', ')" -ForegroundColor Red
}
$phrase = if ($Environment -eq 'Prod') { 'DISABLE PROD' } else { 'DISABLE' }
$final  = Read-Host "Type $phrase to proceed (anything else cancels)"
if ($final -cne $phrase) { Write-Host "Cancelled. Nothing was changed."; return }

# ---------- Step 6: disable (by exact login value found in the preview) ----------
$disableLog = foreach ($t in $toDisable) {
    $groups = $t.Rows | Where-Object { Test-Enabled $_ } | Group-Object { $_.$loginColumn }
    foreach ($g in $groups) {
        try {
            $d = Invoke-DbaQuery -SqlInstance $t.Server @connectArgs -Query $disableQuery `
                    -SqlParameter @{ Login = $g.Name } -EnableException
            [pscustomobject]@{ Server=$t.Server; Login=$g.Name; Expected=$g.Count; Disabled=[int]$d.RowsUpdated; Error=$null
                               Time=(Get-Date -Format s); RunBy=$env:USERNAME }
        } catch {
            [pscustomobject]@{ Server=$t.Server; Login=$g.Name; Expected=$g.Count; Disabled=0; Error=$_.Exception.Message
                               Time=(Get-Date -Format s); RunBy=$env:USERNAME }
        }
    }
}
$disableLog | Format-Table -AutoSize
$logFile = Join-Path $PSScriptRoot "disable_log_${Environment}_$stamp.csv"
$disableLog | Export-Csv $logFile -NoTypeInformation
Write-Host "Disable log saved to $logFile"

$mismatch = @($disableLog | Where-Object { $_.Disabled -ne $_.Expected })
if ($mismatch.Count -gt 0) {
    Write-Host "Note: $($mismatch.Count) login(s) updated a different number of rows than expected (see log)." -ForegroundColor Yellow
}

# ---------- Step 7: verify ----------
Write-Host "`nVerifying: checking '$prefix[_]%' again on all servers..." -ForegroundColor Cyan
$after = @(Get-StatusAllServers $pattern)
Show-Status $after

$left = @($after | Where-Object Status -eq 'ENABLED')
$bad  = @($after | Where-Object Status -eq 'ERROR')
if ($left.Count -eq 0 -and $bad.Count -eq 0) {
    Write-Host "`nVERIFIED: no enabled logins matching '$prefix[_]%' remain on any of the $($servers.Count) servers." -ForegroundColor Green
} else {
    if ($left.Count -gt 0) { Write-Host "`nSTILL ENABLED on: $(($left.Server) -join ', ')" -ForegroundColor Red }
    if ($bad.Count  -gt 0) { Write-Host "COULD NOT VERIFY on: $(($bad.Server) -join ', ')" -ForegroundColor Red }
    Write-Host "Check the Error column and the disable log, then rerun for those servers."
}
