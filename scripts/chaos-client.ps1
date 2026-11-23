param(
    [string]$BaseUrl = "http://localhost:8081",
    [int]$Iterations = 60,
    [int]$DelayMs = 500
)

$ErrorActionPreference = "Stop"

$email = "chaos-tester@ddia.dev"
$password = "ChaosTest123!"

Write-Host "=== DDIA Chapter 9: Live Chaos Steady-State Client ===" -ForegroundColor Cyan
Write-Host "Target Gateway: $BaseUrl"

# 1. Register (ignore if user already exists)
try {
    $regBody = @{ email = $email; password = $password; firstName = "Chaos"; lastName = "Tester" } | ConvertTo-Json
    Invoke-RestMethod -Uri "$BaseUrl/api/Auth/register" -Method Post -ContentType "application/json" -Body $regBody | Out-Null
    Write-Host "[AUTH] Registered user $email" -ForegroundColor Green
} catch {
    Write-Host "[AUTH] User already exists (continuing to login)..." -ForegroundColor DarkGray
}

# 2. Login to get JWT
$loginBody = @{ email = $email; password = $password } | ConvertTo-Json
$loginResp = Invoke-RestMethod -Uri "$BaseUrl/api/Auth/login" -Method Post -ContentType "application/json" -Body $loginBody
$token = $loginResp.data.token
$headers = @{ Authorization = "Bearer $token" }
Write-Host "[AUTH] Logged in successfully." -ForegroundColor Green

# 3. Create 1 Issue to track across faults
$createBody = @{ title = "Chaos Probe Issue"; description = "Tracking version durability"; priority = 1 } | ConvertTo-Json
$createResp = Invoke-RestMethod -Uri "$BaseUrl/api/Issue" -Method Post -Headers $headers -ContentType "application/json" -Body $createBody
$issueId = $createResp.data
$expectedVersion = 1
$highestSeenVersion = 1

Write-Host "[SETUP] Created Issue $issueId at Version 1`n" -ForegroundColor Green
Write-Host "Starting steady-state read/write loop ($Iterations iterations, ${DelayMs}ms interval)..."
Write-Host "Press Ctrl+C anytime to stop.`n"

$okWrites = 0
$failedWrites = 0
$okReads = 0
$failedReads = 0
$invariantViolations = 0

for ($i = 1; $i -le $Iterations; $i++) {
    $ts = (Get-Date).ToString("HH:mm:ss.fff")
    $writeStatus = "ERR"
    $readStatus = "ERR"
    $readVersion = $null

    # --- WRITE (Optimistic Concurrency with ExpectedVersion) ---
    try {
        $updateBody = @{
            id = $issueId
            title = "Chaos Probe #$i"
            description = "Tick $i at $ts"
            priority = 2
            expectedVersion = $expectedVersion
        } | ConvertTo-Json

        $putResp = Invoke-RestMethod -Uri "$BaseUrl/api/Issue/$issueId" -Method Put -Headers $headers -ContentType "application/json" -Body $updateBody -TimeoutSec 5
        $expectedVersion++
        $okWrites++
        $writeStatus = "200 OK (v->$expectedVersion)"
    } catch {
        $failedWrites++
        $code = $_.Exception.Response.StatusCode.value__
        $writeStatus = if ($code) { "HTTP $code" } else { "NET ERR" }
    }

    # --- READ (Monotonic Read with X-Min-Version) ---
    try {
        $readHeaders = @{
            Authorization = "Bearer $token"
            "X-Min-Version" = "$highestSeenVersion"
        }
        $getResp = Invoke-RestMethod -Uri "$BaseUrl/api/Issue/$issueId" -Method Get -Headers $readHeaders -TimeoutSec 5
        $readVersion = [int]$getResp.data.version
        $okReads++
        $readStatus = "200 OK (v=$readVersion)"

        if ($readVersion -lt $highestSeenVersion) {
            $invariantViolations++
            Write-Host "[$ts] #$i INVARIANT VIOLATION: Monotonic Read went backwards! Saw v=$readVersion < highest=$highestSeenVersion" -ForegroundColor Red
        } else {
            $highestSeenVersion = $readVersion
        }
    } catch {
        $failedReads++
        $code = $_.Exception.Response.StatusCode.value__
        $readStatus = if ($code) { "HTTP $code" } else { "NET ERR" }
    }

    $color = if ($writeStatus -like "200*" -and $readStatus -like "200*") { "Gray" } else { "Yellow" }
    Write-Host ("[$ts] #{0,-3} | WRITE: {1,-18} | READ: {2,-15}" -f $i, $writeStatus, $readStatus) -ForegroundColor $color

    Start-Sleep -Milliseconds $DelayMs
}

Write-Host "`n=== Steady-State / Chaos Summary ===" -ForegroundColor Cyan
Write-Host "Writes: $okWrites OK / $failedWrites Failed"
Write-Host "Reads:  $okReads OK / $failedReads Failed"
Write-Host "Invariant Violations (Monotonic Read): $invariantViolations" -ForegroundColor $(if ($invariantViolations -eq 0) { "Green" } else { "Red" })
