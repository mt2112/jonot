<#
.SYNOPSIS
    Runs the QueueFloodIntegrationTests black-box integration test.

.DESCRIPTION
    Starts all required infrastructure (Service Bus emulator, Azurite, Functions host),
    runs the integration test, and tears down background processes on exit.

    Prerequisites:
      - Docker Desktop running
      - Azure Functions Core Tools installed (func)
      - Azurite installed (npm install -g azurite) or available via npx
      - .NET 8 SDK

.EXAMPLE
    .\run-integration-test.ps1
#>

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$funcProjectDir = Join-Path $repoRoot "src\Jonot.Functions"
$emulatorDir = Join-Path $repoRoot "emulator"
$funcLogFile = Join-Path $repoRoot "func-host.log"
$backgroundProcesses = @()

function Cleanup {
    Write-Host "`n--- Cleaning up ---" -ForegroundColor Yellow

    foreach ($proc in $script:backgroundProcesses) {
        if ($proc -and -not $proc.HasExited) {
            Write-Host "  Stopping process: $($proc.ProcessName) (PID $($proc.Id))"
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }

    # Stop Azurite if running as a job
    Get-Job -Name "Azurite" -ErrorAction SilentlyContinue | Stop-Job -ErrorAction SilentlyContinue
    Get-Job -Name "Azurite" -ErrorAction SilentlyContinue | Remove-Job -Force -ErrorAction SilentlyContinue

    Write-Host "  Stopping docker containers..."
    docker compose -f "$emulatorDir\docker-compose.yml" down 2>$null

    Write-Host "--- Cleanup complete ---" -ForegroundColor Green
}

trap { Cleanup; break }

# ── Step 1: Start Service Bus emulator ──────────────────────────────

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "  QUEUE FLOOD INTEGRATION TEST RUNNER" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "[1/5] Starting Service Bus emulator (fresh)..." -ForegroundColor Green
# Tear down any existing containers so the emulator starts with empty queues.
# Old messages from previous failed runs would otherwise jam the session pipeline.
docker compose -f "$emulatorDir\docker-compose.yml" down 2>$null
docker compose -f "$emulatorDir\docker-compose.yml" up -d
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to start Service Bus emulator. Is Docker running?" -ForegroundColor Red
    exit 1
}

# ── Step 2: Start Azurite ───────────────────────────────────────────

Write-Host "[2/5] Starting Azurite (blob/queue/table storage emulator)..." -ForegroundColor Green
$azuriteDataDir = Join-Path $repoRoot "azurite-data"

# Check if Azurite is already running on port 10000
$azuriteAlreadyRunning = $false
try {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect("127.0.0.1", 10000)
    $tcp.Close()
    $azuriteAlreadyRunning = $true
    Write-Host "  Azurite already running on port 10000" -ForegroundColor Gray
} catch {
    # Not running, start it
}

if (-not $azuriteAlreadyRunning) {
    Start-Job -Name "Azurite" -ScriptBlock {
        param($dataDir)
        azurite --location $dataDir --silent 2>&1
    } -ArgumentList $azuriteDataDir | Out-Null

    # Wait for Azurite to be ready
    $azuriteReady = $false
    for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Seconds 1
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $tcp.Connect("127.0.0.1", 10000)
            $tcp.Close()
            $azuriteReady = $true
            break
        } catch {
            # Not ready yet
        }
    }
    if (-not $azuriteReady) {
        Write-Host "ERROR: Azurite did not start on port 10000. Is it installed? (npm install -g azurite)" -ForegroundColor Red
        Cleanup
        exit 1
    }
}
Write-Host "  Azurite ready on ports 10000/10001/10002" -ForegroundColor Gray

# ── Step 3: Wait for Service Bus emulator to be ready ───────────────

Write-Host "[3/5] Waiting for Service Bus emulator to be fully ready..." -ForegroundColor Green
# Port 5672 opens before queues are provisioned from Config.json.
# Check docker logs for the emulator's "Successfully Up" message to know when queues exist.
$sbReady = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 2
    $logs = docker logs servicebus-emulator 2>&1 | Out-String
    if ($logs -match "Emulator Service is Successfully Up") {
        $sbReady = $true
        break
    }
    Write-Host "  Waiting for emulator initialization... ($($i * 2)s)" -ForegroundColor Gray
}
if (-not $sbReady) {
    Write-Host "ERROR: Service Bus emulator did not fully initialize within 120s" -ForegroundColor Red
    Write-Host "--- Emulator logs (last 20 lines) ---" -ForegroundColor Yellow
    docker logs servicebus-emulator --tail 20
    Cleanup
    exit 1
}
Write-Host "  Service Bus emulator fully ready (queues provisioned)" -ForegroundColor Gray

# ── Step 4: Build and start Functions host ──────────────────────────

Write-Host "[4/5] Building and starting Functions host..." -ForegroundColor Green

# Kill any existing process on port 7071 to avoid "port unavailable" errors
$existingConn = Get-NetTCPConnection -LocalPort 7071 -ErrorAction SilentlyContinue
if ($existingConn) {
    $existingPid = $existingConn.OwningProcess | Select-Object -First 1
    Write-Host "  Killing existing process on port 7071 (PID $existingPid)..." -ForegroundColor Yellow
    Stop-Process -Id $existingPid -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# Pre-build the test project (includes Functions project as dependency) so dotnet test can use --no-build
dotnet build "$repoRoot\tests\Jonot.Functions.Tests\Jonot.Functions.Tests.csproj" -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Project build failed" -ForegroundColor Red
    Cleanup
    exit 1
}

# Resolve func.cmd full path so Start-Process works reliably
$funcCmd = Get-Command func -ErrorAction SilentlyContinue
if (-not $funcCmd) {
    Write-Host "ERROR: 'func' (Azure Functions Core Tools) not found in PATH" -ForegroundColor Red
    Cleanup
    exit 1
}
$funcPath = $funcCmd.Source

# Start func as a real child process (not a PowerShell job) with output redirected to a log file
Write-Host "  Functions host log: $funcLogFile" -ForegroundColor Gray
$funcProcess = Start-Process -FilePath $funcPath `
    -ArgumentList "start" `
    -WorkingDirectory $funcProjectDir `
    -RedirectStandardOutput $funcLogFile `
    -RedirectStandardError "$funcLogFile.err" `
    -NoNewWindow `
    -PassThru
$backgroundProcesses += $funcProcess

# Wait for Functions host to be ready by checking the log for the startup message
$funcReady = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 2

    if ($funcProcess.HasExited) {
        Write-Host "ERROR: Functions host exited unexpectedly (exit code: $($funcProcess.ExitCode))" -ForegroundColor Red
        Write-Host "--- Functions host output ---" -ForegroundColor Yellow
        if (Test-Path $funcLogFile) { Get-Content $funcLogFile -Tail 30 }
        if (Test-Path "$funcLogFile.err") { Get-Content "$funcLogFile.err" -Tail 10 }
        Cleanup
        exit 1
    }

    if (Test-Path $funcLogFile) {
        $logContent = Get-Content $funcLogFile -Raw -ErrorAction SilentlyContinue
        if ($logContent -match "Host lock lease acquired|Worker process started and initialized|Functions:") {
            $funcReady = $true
            break
        }
    }

    Write-Host "  Waiting for Functions host... ($($i * 2)s)" -ForegroundColor Gray
}

if (-not $funcReady) {
    Write-Host "WARNING: Functions host startup not confirmed after 60s, continuing..." -ForegroundColor Yellow
    if (Test-Path $funcLogFile) {
        Write-Host "--- Functions host output (last 15 lines) ---" -ForegroundColor Yellow
        Get-Content $funcLogFile -Tail 15
    }
}
else {
    Write-Host "  Functions host started (PID $($funcProcess.Id))" -ForegroundColor Gray
}

# Give Functions host a moment to register all triggers
Start-Sleep -Seconds 5

# ── Step 5: Run the integration test ────────────────────────────────

Write-Host "[5/5] Running integration test..." -ForegroundColor Green
Write-Host ""

dotnet test "$repoRoot\tests\Jonot.Functions.Tests\Jonot.Functions.Tests.csproj" `
    --no-build `
    --filter "FullyQualifiedName~QueueFloodIntegrationTests" `
    --logger "console;verbosity=detailed" `
    -- RunConfiguration.TestSessionTimeout=300000

$testExitCode = $LASTEXITCODE

Write-Host ""
if ($testExitCode -eq 0) {
    Write-Host "================================================================" -ForegroundColor Green
    Write-Host "  TEST PASSED" -ForegroundColor Green
    Write-Host "================================================================" -ForegroundColor Green
}
else {
    Write-Host "================================================================" -ForegroundColor Red
    Write-Host "  TEST FAILED (exit code: $testExitCode)" -ForegroundColor Red
    Write-Host "================================================================" -ForegroundColor Red

    # Show Functions host output for troubleshooting
    Write-Host "`n--- Functions host output (last 50 lines) ---" -ForegroundColor Yellow
    if (Test-Path $funcLogFile) { Get-Content $funcLogFile -Tail 50 }
    if (Test-Path "$funcLogFile.err") {
        Write-Host "`n--- Functions host errors ---" -ForegroundColor Yellow
        Get-Content "$funcLogFile.err" -Tail 20
    }
}

# ── Cleanup ─────────────────────────────────────────────────────────

Cleanup
exit $testExitCode
