<#
.SYNOPSIS
  Start, stop or inspect the local Redis used for the proctoring streams.

.EXAMPLE
  ./scripts/redis-streams.ps1 up        # start Redis (docker compose) and wait until healthy
  ./scripts/redis-streams.ps1 status    # stream lengths, consumer groups, pending entries, dead letters
  ./scripts/redis-streams.ps1 down      # stop Redis (data is kept in the redis-data volume)

  Stream names default to the contract names; override with the same REDIS_* variables the services use.
  The password is read from $env:REDIS_PASSWORD and is never printed.
#>
param([ValidateSet("up", "down", "status")] [string] $Action = "status")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$compose = Join-Path $root "infra/docker-compose.yml"

function Get-Setting([string] $name, [string] $default) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($value)) { return $default } else { return $value }
}

function Invoke-RedisCli([string[]] $arguments) {
    $cli = @("compose", "-f", $compose, "exec", "-T")
    if ($env:REDIS_PASSWORD) { $cli += @("-e", "REDISCLI_AUTH=$env:REDIS_PASSWORD") }
    $cli += @("redis", "redis-cli", "--no-auth-warning") + $arguments
    & docker @cli
}

switch ($Action) {
    "up" {
        docker compose -f $compose up -d --wait
        docker compose -f $compose ps
    }
    "down" {
        docker compose -f $compose stop
    }
    "status" {
        $streams = [ordered]@{
            "events"      = Get-Setting "REDIS_EVENTS_STREAM" "proctoring.audio.events.v1"
            "results"     = Get-Setting "REDIS_RESULTS_STREAM" "proctoring.audio.results.v1"
            "events DLQ"  = Get-Setting "REDIS_DLQ_STREAM" "proctoring.audio.events.dlq.v1"
            "results DLQ" = Get-Setting "REDIS_RESULTS_DLQ_STREAM" "proctoring.audio.results.dlq.v1"
        }
        foreach ($entry in $streams.GetEnumerator()) {
            $length = Invoke-RedisCli @("XLEN", $entry.Value)
            Write-Host ("{0,-12} {1,-36} entries: {2}" -f $entry.Key, $entry.Value, $length)
        }
        foreach ($stream in @($streams["events"], $streams["results"])) {
            Write-Host "`nConsumer groups of ${stream}:"
            Invoke-RedisCli @("XINFO", "GROUPS", $stream)
        }
    }
}
