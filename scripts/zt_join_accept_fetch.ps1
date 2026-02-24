[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Org = "cmlpb7m960006np01jgwg4dr7",
    [string]$Network = "9ad07d01093a69e3",
    [string]$Url = "http://10.121.15.99:5380/",
    [int]$UdpPort = 0,
    [string]$HttpMode = "os",
    [string]$StateRootPath = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$Message) {
    Write-Host ("== " + $Message)
}

function Get-ZtNodeIdFromState([string]$StateRoot) {
    $secretPath = Join-Path $StateRoot "identity.secret"
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

if ([string]::IsNullOrWhiteSpace($StateRootPath)) {
    if (-not $env:LOCALAPPDATA) {
        throw "LOCALAPPDATA is not set. Please pass -StateRootPath explicitly."
    }

    $StateRootPath = Join-Path $env:LOCALAPPDATA ("libzt-dotnet\\state-" + $Network)
}

New-Item -ItemType Directory -Path $StateRootPath -Force | Out-Null

Write-Step "Checking ztnet auth..."
ztnet auth test | Out-Host

if (-not $SkipBuild) {
    Write-Step "Building ($Configuration)..."
    dotnet build -c $Configuration | Out-Host
}

Write-Step "Joining network $Network (state: $StateRootPath)..."
& dotnet run -c $Configuration --no-build `
    --project samples/ZTSharp.Cli/ZTSharp.Cli.csproj -- `
    join `
    --once `
    --network $Network `
    --stack overlay `
    --transport osudp `
    --udp-port $UdpPort `
    --state $StateRootPath | Out-Host

$nodeId = Get-ZtNodeIdFromState -StateRoot $StateRootPath
Write-Step "NodeId: $nodeId"
$nodeIdForZtnet = $nodeId
if ($nodeIdForZtnet.StartsWith("0x")) {
    $nodeIdForZtnet = $nodeIdForZtnet.Substring(2)
}

Write-Step "Adding + authorizing member in ZTNet (org: $Org)..."
ztnet --org $Org --yes network member add $Network $nodeIdForZtnet | Out-Host
ztnet --org $Org --yes network member authorize $Network $nodeIdForZtnet | Out-Host

Write-Step "Fetching $Url (call --http $HttpMode)..."
& dotnet run -c $Configuration --no-build `
    --project samples/ZTSharp.Cli/ZTSharp.Cli.csproj -- `
    call `
    --http $HttpMode `
    --network $Network `
    --stack overlay `
    --transport osudp `
    --udp-port 0 `
    --state $StateRootPath `
    --url $Url | Out-Host
