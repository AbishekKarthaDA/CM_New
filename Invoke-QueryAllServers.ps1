<#
.SYNOPSIS
  Run one read-only query on every server (and optionally every user database) of one or more environments
  and return the combined rows.

.DESCRIPTION
  Servers come from the same servers.csv as Remove-AppLogins-Interactive.ps1 (Server/Servers, Environment columns).
  Every returned row gets Server, Environment and Database columns in front, so results can be compared
  in one table. Results are also saved to a CSV next to the script.

  Only queries that start with SELECT or WITH are allowed, so this script cannot change data.

.PARAMETER Environment
  One or more of Dev, Test, Prod.

.PARAMETER Query
  The SQL to run.

.PARAMETER QueryFile
  Path to a .sql file to run instead of -Query.

.PARAMETER Database
  Run in this one database on every server (default: the login's default database, usually master).

.PARAMETER AllDatabases
  Run in every online user database on each server (system databases are always skipped).

.PARAMETER ExcludeDatabase
  With -AllDatabases: database names to skip (not case-sensitive).

.PARAMETER RequireObject
  With -AllDatabases: only run in databases where this object exists, e.g. AL.FolderIndex.
  Databases without it are listed as SKIPPED, not as errors.

.PARAMETER UseSqlLogin
  Prompt for a SQL login. Without it, the current Windows identity is used.

.PARAMETER TrustServerCertificate
  Use if you get "certificate chain was issued by an authority that is not trusted".

.PARAMETER QueryTimeout
  Seconds to wait for each query (default 120).

.EXAMPLES
  .\Invoke-QueryAllServers.ps1 -Environment Prod,Dev -TrustServerCertificate -AllDatabases `
      -ExcludeDatabase alchemy,AlchemyStore,almain,AlterianAuth -RequireObject AL.FolderIndex `
      -Query "SELECT * FROM [AL].[FolderIndex] WHERE folderType = 1"

  .\Invoke-QueryAllServers.ps1 -Environment Prod -TrustServerCertificate -Database AlterianAuth `
      -Query "SELECT * FROM [AUTH].[LoginIndex] WHERE login LIKE 'LAKNOX[_]%'"
#>
param(
    [Parameter(Mandatory)][ValidateSet('Dev','Test','Prod')] [string[]]$Environment,
    [Parameter(Mandatory, ParameterSetName = 'Inline')] [string]$Query,
    [Parameter(Mandatory, ParameterSetName = 'File')]   [string]$QueryFile,
    [string]$Database,
    [switch]$AllDatabases,
    [string[]]$ExcludeDatabase = @(),
    [string]$RequireObject,
    [switch]$UseSqlLogin,
    [switch]$TrustServerCertificate,
    [int]$QueryTimeout = 120,
    [string]$ServerFile = (Join-Path $PSScriptRoot 'servers.csv')
)

if ($AllDatabases -and $Database) { Write-Warning "Use either -Database or -AllDatabases, not both."; return }

if (-not (Get-Module -ListAvailable dbatools)) {
    Write-Host "dbatools not found. Installing for current user..."
    Install-Module dbatools -Scope CurrentUser -Force
}
Import-Module dbatools

if ($QueryFile) {
    if (-not (Test-Path $QueryFile)) { Write-Warning "Query file not found: $QueryFile"; return }
    $Query = Get-Content $QueryFile -Raw
}

# Safety: read-only queries only (strip leading comments/whitespace before checking)
$firstWord = ($Query -replace '(?s)^\s*((--[^\n]*\n)|(/\*.*?\*/)|\s)*', '') -split '\s+' | Select-Object -First 1
if ($firstWord -notin 'SELECT', 'WITH') { Write-Warning "Only SELECT / WITH queries are allowed. Query starts with: $firstWord"; return }
if ($Query -match '(?i)\b(INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|TRUNCATE|CREATE|EXEC|EXECUTE|GRANT|REVOKE)\b') {
    Write-Warning "Query contains a data/schema-changing keyword ($($Matches[1])). This script is read-only; not running it."
    return
}

$connectArgs = @{ QueryTimeout = $QueryTimeout }
if ($UseSqlLogin) { $connectArgs.SqlCredential = Get-Credential -Message "SQL login for $($Environment -join ', ')" }
# Invoke-DbaQuery has no -TrustServerCertificate; dbatools uses a session config setting instead
if ($TrustServerCertificate) { Set-DbatoolsConfig -FullName sql.connection.trustcert -Value $true }

if (-not (Test-Path $ServerFile)) { Write-Warning "Server file not found: $ServerFile"; return }
$servers = @(Import-Csv $ServerFile | Where-Object { $_.Environment -in $Environment } |
    Select-Object @{ n = 'Server'; e = { if ($_.Server) { $_.Server } else { $_.Servers } } }, Environment |
    Where-Object { $_.Server })
if ($servers.Count -eq 0) { Write-Warning "No servers found for $($Environment -join ', ') in $ServerFile"; return }

$noise = 'RowError','RowState','Table','ItemArray','HasErrors'   # DataRow properties we don't want to display/export
$stamp = Get-Date -Format yyyyMMdd_HHmm

# Online user databases this login can open (database_id > 4 skips master, tempdb, model, msdb)
$listDbQuery = "SELECT name FROM sys.databases WHERE database_id > 4 AND state_desc = 'ONLINE' AND HAS_DBACCESS(name) = 1 ORDER BY name"

# Which databases to run in on one server; $null means "connection default"
function Get-TargetDatabases([string]$Server) {
    if ($Database) { return , @($Database) }
    if (-not $AllDatabases) { return , @($null) }
    $names = @(Invoke-DbaQuery -SqlInstance $Server @connectArgs -Query $listDbQuery -EnableException | ForEach-Object name)
    , @($names | Where-Object { $_ -notin $ExcludeDatabase })   # -notin is not case-sensitive
}

Write-Host "`nEnvironment: $($Environment -join ', ') | Servers: $($servers.Count)" -ForegroundColor Cyan
if ($AllDatabases) {
    Write-Host "All user databases$(if ($ExcludeDatabase) { ', except: ' + ($ExcludeDatabase -join ', ') })$(if ($RequireObject) { " | only where $RequireObject exists" })" -ForegroundColor Cyan
}

$summary = [System.Collections.Generic.List[object]]::new()
$results = foreach ($s in $servers) {
    $srv = $s.Server; $envName = $s.Environment
    try { $dbs = Get-TargetDatabases $srv }
    catch {
        Write-Host "  $srv : ERROR listing databases" -ForegroundColor Red
        $summary.Add([pscustomobject]@{ Server = $srv; Environment = $envName; Database = $null; Status = 'ERROR'; Rows = $null; Error = $_.Exception.Message })
        continue
    }
    if ($dbs.Count -eq 0) { Write-Host "  $srv : no databases to check" -ForegroundColor Yellow }

    foreach ($db in $dbs) {
        $dbArgs = @{}
        if ($db) { $dbArgs.Database = $db }
        $label = if ($db) { "$srv / $db" } else { $srv }
        Write-Host "  $label ..." -NoNewline
        try {
            if ($RequireObject) {
                $obj = Invoke-DbaQuery -SqlInstance $srv @connectArgs @dbArgs -Query "SELECT OBJECT_ID(@Obj) AS id" `
                        -SqlParameter @{ Obj = $RequireObject } -EnableException
                if ($obj.id -is [DBNull]) {
                    Write-Host " skipped (no $RequireObject)" -ForegroundColor DarkGray
                    $summary.Add([pscustomobject]@{ Server = $srv; Environment = $envName; Database = $db; Status = 'SKIPPED'; Rows = $null; Error = "No $RequireObject" })
                    continue
                }
            }
            $rows = @(Invoke-DbaQuery -SqlInstance $srv @connectArgs @dbArgs -Query $Query -EnableException)
            Write-Host " $($rows.Count) row(s)" -ForegroundColor Green
            $summary.Add([pscustomobject]@{ Server = $srv; Environment = $envName; Database = $db; Status = 'OK'; Rows = $rows.Count; Error = $null })
            $rows | Select-Object @{ n = 'Server'; e = { $srv } }, @{ n = 'Environment'; e = { $envName } },
                                  @{ n = 'Database'; e = { $db } }, * -ExcludeProperty $noise
        } catch {
            Write-Host " ERROR" -ForegroundColor Red
            $summary.Add([pscustomobject]@{ Server = $srv; Environment = $envName; Database = $db; Status = 'ERROR'; Rows = $null; Error = $_.Exception.Message })
        }
    }
}

Write-Host "`nSummary:" -ForegroundColor Cyan
$summary | Format-Table Server, Environment, Database, Status, Rows, Error -AutoSize

if (@($results).Count -gt 0) {
    Write-Host "Results:" -ForegroundColor Cyan
    $results | Format-Table -AutoSize
    $outFile = Join-Path $PSScriptRoot "query_results_$($Environment -join '_')_$stamp.csv"
    $results | Export-Csv $outFile -NoTypeInformation
    Write-Host "Saved $(@($results).Count) row(s) to $outFile"
} else {
    Write-Host "No rows returned from any reachable server/database." -ForegroundColor Yellow
}

$errs = @($summary | Where-Object Status -eq 'ERROR')
if ($errs.Count -gt 0) {
    Write-Host "Could not query: $(($errs | ForEach-Object { if ($_.Database) { "$($_.Server)/$($_.Database)" } else { $_.Server } }) -join ', ') (see Error column)" -ForegroundColor Red
}
