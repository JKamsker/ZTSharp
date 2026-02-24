[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Network = "0xCAFE2001",
    [int]$ApiPort = 5005,
    [int]$OverlayPort = 28080,
    [int]$ExposeUdpPort = 49001,
    [int]$CallUdpPort = 49002,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$Message) {
    Write-Host ("== " + $Message)
}

function Wait-ForFile([string]$Path, [int]$TimeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            return
        }

        Start-Sleep -Milliseconds 200
    }

    throw "Timed out waiting for file: $Path"
}

function Wait-ForHttpOk([string]$Url, [int]$TimeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -Method Get -TimeoutSec 2
            if ($response.StatusCode -eq 200) {
                return
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 200
    }

    throw "Timed out waiting for HTTP 200 from: $Url"
}

function Get-ZtNodeIdFromState([string]$StateRootPath) {
    $secretPath = Join-Path $StateRootPath "identity.secret"
    $bytes = [System.IO.File]::ReadAllBytes($secretPath)
    if ($bytes.Length -lt 32) {
        throw "Invalid identity secret file: $secretPath"
    }

    $secret = New-Object byte[] 32
    [Array]::Copy($bytes, 0, $secret, 0, 32)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($secret)
    }
    finally {
        $sha.Dispose()
    }

    $value = [System.BitConverter]::ToUInt64($hash, 0)
    $mask = [UInt64]0xFFFFFFFFFF
    $value = $value -band $mask

    return ("0x{0:x10}" -f $value)
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("libzt-dotnet-tunnel-demo-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot | Out-Null

$apiStdOut = Join-Path $tempRoot "demo-api.stdout.log"
$apiStdErr = Join-Path $tempRoot "demo-api.stderr.log"
$exposeStdOut = Join-Path $tempRoot "expose.stdout.log"
$exposeStdErr = Join-Path $tempRoot "expose.stderr.log"
$exposeState = Join-Path $tempRoot "expose-state"
$callState = Join-Path $tempRoot "call-state"

$apiProc = $null
$exposeProc = $null

try {
    if (-not $SkipBuild) {
        Write-Step "Building ($Configuration)..."
        dotnet build -c $Configuration | Out-Host
    }

    Write-Step "Starting demo API (http://127.0.0.1:$ApiPort)..."
    $apiProc = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList @(
            "run", "-c", $Configuration, "--no-build",
            "--project", "samples/ZTSharp.Samples.DemoApi/ZTSharp.Samples.DemoApi.csproj",
            "--", "--port", $ApiPort
        ) `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $apiStdOut `
        -RedirectStandardError $apiStdErr `
        -NoNewWindow `
        -PassThru

    Wait-ForHttpOk -Url "http://127.0.0.1:$ApiPort/healthz" -TimeoutSeconds 20

    Write-Step "Starting overlay expose (UDP $ExposeUdpPort, overlay port $OverlayPort)..."
    New-Item -ItemType Directory -Path $exposeState | Out-Null
    $exposeProc = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList @(
            "run", "-c", $Configuration, "--no-build",
            "--project", "samples/ZTSharp.Cli/ZTSharp.Cli.csproj",
            "--",
            "expose", $ApiPort,
            "--network", $Network,
            "--stack", "overlay",
            "--transport", "osudp",
            "--udp-port", $ExposeUdpPort,
            "--listen", $OverlayPort,
            "--to", "127.0.0.1:$ApiPort",
            "--state", $exposeState
        ) `
        -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $exposeStdOut `
        -RedirectStandardError $exposeStdErr `
        -NoNewWindow `
        -PassThru

    Wait-ForFile -Path (Join-Path $exposeState "identity.secret") -TimeoutSeconds 10
    $nodeId = Get-ZtNodeIdFromState -StateRootPath $exposeState
    Write-Step "Expose node id: $nodeId"

    Write-Step "Calling overlay URL via managed overlay TCP..."
    $callOutput = & dotnet run -c $Configuration --no-build `
        --project samples/ZTSharp.Cli/ZTSharp.Cli.csproj -- `
        call `
        --network $Network `
        --stack overlay `
        --transport osudp `
        --udp-port $CallUdpPort `
        --state $callState `
        --peer "$nodeId@127.0.0.1:$ExposeUdpPort" `
        --url "http://${nodeId}:$OverlayPort/hello" 2>&1

    $callOutput | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Overlay call failed (exit code $LASTEXITCODE). Logs: $tempRoot"
    }

    if (-not ($callOutput -match "HTTP 200 OK")) {
        throw "Expected HTTP 200 OK. Logs: $tempRoot"
    }

    Write-Step "Success. Logs: $tempRoot"
}
finally {
    Write-Step "Stopping background processes..."

    if ($exposeProc -and -not $exposeProc.HasExited) {
        Stop-Process -Id $exposeProc.Id -Force -ErrorAction SilentlyContinue
    }

    if ($apiProc -and -not $apiProc.HasExited) {
        Stop-Process -Id $apiProc.Id -Force -ErrorAction SilentlyContinue
    }
}
