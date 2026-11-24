<#
.SYNOPSIS
  Start, stop or inspect the whole standalone audio-proctoring application on this machine:
  Redis (Docker) -> .NET backend -> Python AI service -> React frontend.

.EXAMPLE
  .\scripts\run-all.ps1               # same as "start"
  .\scripts\run-all.ps1 start         # check prerequisites, start everything in order, wait for health checks
  .\scripts\run-all.ps1 status        # what is running, health, URLs
  .\scripts\run-all.ps1 stop          # stop the services this script started, then Redis (data is kept)
  .\scripts\run-all.ps1 stop -KeepRedis

.NOTES
  - Works from any current directory; all paths are derived from this script's location.
  - Only processes started by this script are stopped: their PIDs and start times are recorded in
    .run/processes.json, and a PID is only stopped when its start time still matches (no PID-reuse accidents).
    Redis is stopped with "docker compose stop" for this project's compose file only.
  - Logs: .run/logs/<service>.out.log and <service>.err.log (one pair per service, overwritten on each start).
  - Settings: the services keep reading their own .env files. This script sets only what the standalone run
    needs (ports, queue provider, CORS, shared audio folder) for the processes it starts; your shell's
    environment is restored afterwards. Override ports with BACKEND_PORT, AI_PORT, FRONTEND_PORT, REDIS_PORT.
  - Secrets are never printed.
  - Windows PowerShell 5.1 compatible (ASCII only).
#>
param(
    [ValidateSet("start", "stop", "status")] [string] $Action = "start",
    [switch] $KeepRedis,
    [int] $TimeoutSeconds = 240
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$RunDir = Join-Path $Root ".run"
$LogDir = Join-Path $RunDir "logs"
$StateFile = Join-Path $RunDir "processes.json"
$Compose = Join-Path $Root "infra/docker-compose.yml"

function Get-Port([string] $name, [int] $default) {
    $v = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($v)) { return $default } else { return [int] $v }
}
$RedisPort = Get-Port "REDIS_PORT" 6379
$BackendPort = Get-Port "BACKEND_PORT" 5080
$AiPort = Get-Port "AI_PORT" 8090
$FrontendPort = Get-Port "FRONTEND_PORT" 5173

$BackendDir = Join-Path $Root "backend"
$BackendProject = Join-Path $BackendDir "src/Proctoring.Api"
$AiDir = Join-Path $Root "ai-service"
$AiPython = Join-Path $AiDir ".venv/Scripts/python.exe"
$FrontendDir = Join-Path $Root "frontend"
# The backend resolves its relative AUDIO_STORE_PATH (default data/audio) against its project folder; the AI service
# must read the same folder.
$AudioStore = Join-Path $BackendProject "data/audio"

$Urls = [ordered]@{
    redis    = "redis://127.0.0.1:$RedisPort"
    backend  = "http://localhost:$BackendPort  (health: /health/ready)"
    ai       = "http://127.0.0.1:$AiPort  (health: /health/ready)"
    frontend = "http://localhost:$FrontendPort"
}

# ------------------------------------------------------------------ helpers

function Write-Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok([string] $text) { Write-Host "    OK  $text" -ForegroundColor Green }
function Write-Warn2([string] $text) { Write-Host "    !!  $text" -ForegroundColor Yellow }
function Fail([string] $text) { Write-Host "    XX  $text" -ForegroundColor Red; throw "run-all: $text" }

function Read-State {
    if (-not (Test-Path $StateFile)) { return @() }
    $raw = Get-Content $StateFile -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) { return @() }
    # Windows PowerShell 5.1 emits a JSON array as ONE object; enumerate it explicitly.
    $parsed = $raw | ConvertFrom-Json
    return @($parsed | ForEach-Object { $_ })
}

function Save-State($entries) {
    New-Item -ItemType Directory -Force $RunDir | Out-Null
    ConvertTo-Json -InputObject @($entries) -Depth 3 | Out-File $StateFile -Encoding utf8
}

# Returns the live process for a recorded entry, or $null when it has exited or the PID now belongs to someone else.
function Get-RecordedProcess($entry) {
    $p = Get-Process -Id $entry.pid -ErrorAction SilentlyContinue
    if ($null -eq $p) { return $null }
    try {
        if ($p.StartTime.ToUniversalTime().ToString("o") -ne $entry.startTime) { return $null }
    } catch { return $null }
    return $p
}

function Test-PortListening([int] $port) {
    return $null -ne (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue)
}

# HTTP status code of a GET, or 0 when nothing answers. Never throws.
function Get-HttpStatus([string] $url) {
    try {
        $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5
        return [int] $r.StatusCode
    } catch [System.Net.WebException] {
        if ($_.Exception.Response) { return [int] $_.Exception.Response.StatusCode }
        return 0
    } catch { return 0 }
}

function Get-HttpBody([string] $url) {
    try { return (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5).Content }
    catch [System.Net.WebException] {
        if ($_.Exception.Response) {
            $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
            return $reader.ReadToEnd()
        }
        return $null
    } catch { return $null }
}

function Get-RedisHealth {
    $ErrorActionPreference = "Continue"   # native stderr must not abort (Windows PowerShell 5.1)
    $out = & docker compose -f $Compose ps --format "{{.Service}} {{.Health}}" 2>$null
    foreach ($line in @($out)) {
        if ($line -match "^redis (\S+)") { return $Matches[1] }
    }
    return "not running"
}

function Show-LogTail([string] $name) {
    foreach ($kind in "err", "out") {
        $f = Join-Path $LogDir "$name.$kind.log"
        if ((Test-Path $f) -and (Get-Item $f).Length -gt 0) {
            Write-Host "    --- last lines of .run/logs/$name.$kind.log ---" -ForegroundColor DarkGray
            Get-Content $f -Tail 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        }
    }
}

# Starts a service with its own log files, with extra environment variables applied only for this child.
# The service runs as "cmd.exe /d /s /c <command> > out.log 2> err.log", launched through ShellExecute (Start-Process
# without -Redirect*/-NoNewWindow). ShellExecute does not let the child inherit this script's handles, so piping or
# capturing this script's output (e.g. "run-all.ps1 | Tee-Object") cannot be held open by the long-running services.
# The child still receives this process's environment, including the temporary overrides.
function Start-ServiceProcess([string] $name, [string] $file, [string[]] $arguments, [string] $workDir, [hashtable] $environment) {
    New-Item -ItemType Directory -Force $LogDir | Out-Null
    $out = Join-Path $LogDir "$name.out.log"
    $err = Join-Path $LogDir "$name.err.log"
    $commandLine = "`"$file`" " + ($arguments -join " ") + " > `"$out`" 2> `"$err`""
    $saved = @{}
    foreach ($k in $environment.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k)
        [Environment]::SetEnvironmentVariable($k, $environment[$k])
    }
    try {
        $p = Start-Process -FilePath (Join-Path $env:SystemRoot "System32\cmd.exe") -ArgumentList "/d /s /c `"$commandLine`"" `
            -WorkingDirectory $workDir -WindowStyle Hidden -PassThru
    } finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
    $entry = [pscustomobject]@{
        name = $name; pid = $p.Id; startTime = $p.StartTime.ToUniversalTime().ToString("o")
        startedAt = (Get-Date).ToUniversalTime().ToString("o")
    }
    $state = @(Read-State | Where-Object { $_.name -ne $name }) + $entry
    Save-State $state
    return $p
}

# Waits until $check returns $true; fails early when the process exits.
function Wait-Until([string] $name, $process, [scriptblock] $check, [int] $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $check) { return }
        if ($process -and $process.HasExited) {
            Show-LogTail $name
            Fail "$name exited during startup (exit code $($process.ExitCode)). See .run/logs/$name.*.log"
        }
        Start-Sleep -Milliseconds 1000
    }
    Show-LogTail $name
    Fail "$name did not become healthy within $seconds s. See .run/logs/$name.*.log"
}

function Stop-Recorded([string] $name) {
    $ErrorActionPreference = "Continue"
    $state = @(Read-State)
    $entry = $state | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if ($null -eq $entry) { return }
    $p = Get-RecordedProcess $entry
    if ($null -ne $p) {
        # /T stops the child processes too (dotnet run -> Proctoring.Api.exe, npm -> node/vite, venv launcher -> python).
        & taskkill.exe /PID $entry.pid /T /F 2>&1 | Out-Null
        Write-Ok "$name stopped (pid $($entry.pid))"
    } else {
        Write-Ok "$name was not running"
    }
    Save-State @($state | Where-Object { $_.name -ne $name })
}

# ------------------------------------------------------------------ actions

function Invoke-Status {
    Write-Step "Status"
    $state = @(Read-State)
    $health = @{
        redis    = { Get-RedisHealth }
        backend  = { "HTTP " + (Get-HttpStatus "http://localhost:$BackendPort/health/ready") }
        ai       = { "HTTP " + (Get-HttpStatus "http://127.0.0.1:$AiPort/health/ready") }
        frontend = { "HTTP " + (Get-HttpStatus "http://localhost:$FrontendPort/") }
    }
    foreach ($name in $Urls.Keys) {
        $entry = $state | Where-Object { $_.name -eq $name } | Select-Object -First 1
        $proc = if ($name -eq "redis") { "docker" } elseif ($entry -and (Get-RecordedProcess $entry)) { "pid $($entry.pid)" } else { "not started by run-all" }
        "{0,-9} {1,-24} {2,-14} {3}" -f $name, $proc, (& $health[$name]), $Urls[$name] | Write-Host
    }
}

function Invoke-Stop {
    $ErrorActionPreference = "Continue"
    Write-Step "Stopping services started by run-all"
    foreach ($name in "frontend", "ai", "backend") { Stop-Recorded $name }
    if ($KeepRedis) {
        Write-Ok "Redis left running (-KeepRedis)"
    } else {
        & docker compose -f $Compose stop redis 2>&1 | Out-Null
        Write-Ok "Redis stopped (docker compose stop; data kept in the redis-data volume)"
    }
}

function Test-Prerequisites {
    $ErrorActionPreference = "Continue"
    Write-Step "Checking prerequisites"
    $problems = @()
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { $problems += "Docker is not installed (needed for Redis)." }
    else {
        & docker info --format "{{.ServerVersion}}" 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { $problems += "Docker is installed but the engine is not running - start Docker Desktop." }
        else { Write-Ok "Docker engine running" }
    }
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) { $problems += ".NET SDK not found (needs .NET 9 SDK)." }
    else {
        $sdks = & dotnet --list-sdks 2>$null
        if (-not ($sdks | Where-Object { $_ -match "^9\." })) { $problems += ".NET 9 SDK not found (dotnet --list-sdks)." }
        else { Write-Ok ".NET SDK $((& dotnet --version).Trim())" }
    }
    $node = Get-Command node -ErrorAction SilentlyContinue
    if (-not $node) { $problems += "Node.js not found (needs Node 20+)." }
    else {
        $nv = (& node --version).Trim()
        if ([int]($nv.TrimStart("v").Split(".")[0]) -lt 20) { $problems += "Node $nv is too old (needs 20+)." } else { Write-Ok "Node $nv" }
    }
    if (-not (Get-Command npm.cmd -ErrorAction SilentlyContinue)) { $problems += "npm not found." }
    if (-not (Test-Path $AiPython)) { $problems += "AI service virtual environment missing: create it as described in ai-service/README.md (ai-service/.venv)." }
    else { Write-Ok "AI service venv: $((& $AiPython --version 2>&1).ToString().Trim())" }
    if (-not (Test-Path (Join-Path $FrontendDir "node_modules"))) { $problems += "Frontend dependencies missing: run 'npm install' in frontend/." }
    else { Write-Ok "Frontend dependencies installed" }
    if (-not (Test-Path (Join-Path $BackendProject "Proctoring.Api.csproj"))) { $problems += "Backend project not found at backend/src/Proctoring.Api." }

    foreach ($pair in @(@("backend", $BackendPort), @("ai", $AiPort), @("frontend", $FrontendPort))) {
        $entry = @(Read-State) | Where-Object { $_.name -eq $pair[0] } | Select-Object -First 1
        $ours = $entry -and (Get-RecordedProcess $entry)
        if ((Test-PortListening $pair[1]) -and -not $ours) {
            $problems += "Port $($pair[1]) ($($pair[0])) is already in use by another process. Stop it, or set $($pair[0].ToUpper())_PORT."
        }
    }

    # Configuration notes (values are never printed).
    $rootEnv = Join-Path $Root ".env"
    if ((Test-Path $rootEnv) -and (Select-String -Path $rootEnv -Pattern "^\s*QUEUE_PROVIDER\s*=\s*local-directory" -Quiet)) {
        Write-Warn2 "Root .env sets QUEUE_PROVIDER=local-directory; this script runs the backend with QUEUE_PROVIDER=redis-streams."
    }
    if (-not (Test-Path (Join-Path $FrontendDir ".env"))) { Write-Ok "frontend/.env not present - frontend defaults apply (API http://localhost:5080)" }
    if (-not (Test-Path (Join-Path $AiDir ".env"))) { Write-Ok "ai-service/.env not present - AI defaults apply (transcription off)" }

    if ($problems.Count -gt 0) {
        foreach ($p in $problems) { Write-Host "    XX  $p" -ForegroundColor Red }
        throw "run-all: prerequisites missing (see above)"
    }
}

function Invoke-Start {
    Test-Prerequisites
    New-Item -ItemType Directory -Force $LogDir | Out-Null
    New-Item -ItemType Directory -Force $AudioStore | Out-Null

    # 1. Redis
    Write-Step "Starting Redis (docker compose, service 'redis')"
    $savedRedisPort = $env:REDIS_PORT
    $env:REDIS_PORT = "$RedisPort"   # used by infra/docker-compose.yml for the published port
    $ErrorActionPreference = "Continue"   # docker compose reports progress on stderr
    try {
        & docker compose -f $Compose up -d redis *> (Join-Path $LogDir "redis.out.log")
        $composeExit = $LASTEXITCODE
    } finally { $env:REDIS_PORT = $savedRedisPort; $ErrorActionPreference = "Stop" }
    if ($composeExit -ne 0) { Show-LogTail "redis"; Fail "docker compose could not start Redis (exit $composeExit)" }
    Wait-Until "redis" $null { (Get-RedisHealth) -eq "healthy" } 60
    Write-Ok "Redis healthy - $($Urls.redis)"

    # 2. Backend
    Write-Step "Starting backend (dotnet run; the first start compiles, which can take a minute)"
    $backendEnv = @{
        ASPNETCORE_ENVIRONMENT = "Development"
        ASPNETCORE_URLS        = "http://localhost:$BackendPort"
        QUEUE_PROVIDER         = "redis-streams"
        REDIS_CONNECTION       = "localhost:$RedisPort"
        CORS_ORIGINS           = "http://localhost:$FrontendPort"
        LOG_FORMAT             = "text"
    }
    $backend = Start-ServiceProcess "backend" (Get-Command dotnet).Source @("run", "--project", "`"$BackendProject`"", "--no-launch-profile") $BackendDir $backendEnv
    Wait-Until "backend" $backend { (Get-HttpStatus "http://localhost:$BackendPort/health/ready") -eq 200 } $TimeoutSeconds
    Write-Ok "Backend ready - $($Urls.backend)"

    # 3. AI service
    Write-Step "Starting AI service (uvicorn; loads the Silero VAD model)"
    $aiEnv = @{
        REDIS_CONNECTION     = "localhost:$RedisPort"
        AUDIO_STORE_PATH     = $AudioStore
        OPENBLAS_NUM_THREADS = "4"
    }
    $ai = Start-ServiceProcess "ai" $AiPython @("-m", "uvicorn", "app.main:app", "--host", "127.0.0.1", "--port", "$AiPort") $AiDir $aiEnv
    Wait-Until "ai" $ai { (Get-HttpStatus "http://127.0.0.1:$AiPort/health/ready") -eq 200 } $TimeoutSeconds
    Write-Ok "AI service ready - $($Urls.ai)"

    # 4. Frontend
    Write-Step "Starting frontend (vite dev server)"
    $feEnv = @{}
    if ($BackendPort -ne 5080) { $feEnv["VITE_API_BASE_URL"] = "http://localhost:$BackendPort" }
    $frontend = Start-ServiceProcess "frontend" (Get-Command npm.cmd).Source @("run", "dev", "--", "--port", "$FrontendPort", "--strictPort") $FrontendDir $feEnv
    Wait-Until "frontend" $frontend { (Get-HttpStatus "http://localhost:$FrontendPort/") -eq 200 } 120
    Write-Ok "Frontend ready - $($Urls.frontend)"

    Write-Host ""
    Invoke-Status
    Write-Host ""
    Write-Host "Open $($Urls.frontend) in Chrome or Edge. Logs: .run/logs/. Stop with: .\scripts\run-all.ps1 stop" -ForegroundColor Cyan
}

switch ($Action) {
    "start" {
        try { Invoke-Start }
        catch {
            Write-Host ""
            Write-Host "Startup failed: $($_.Exception.Message)" -ForegroundColor Red
            $running = @(Read-State | Where-Object { Get-RecordedProcess $_ } | ForEach-Object { $_.name })
            if ($running.Count -gt 0) {
                Write-Host "Still running from this start: $($running -join ', '). Stop them with: .\scripts\run-all.ps1 stop" -ForegroundColor Yellow
            } else {
                Write-Host "No application service was started." -ForegroundColor Yellow
            }
            exit 1
        }
    }
    "stop" { Invoke-Stop }
    "status" { Invoke-Status }
}
