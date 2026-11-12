# Creates a GitHub release and uploads its files. Called by release.cmd.
# Sign-in: GitHub CLI (gh) if it is installed and signed in, otherwise the GitHub sign-in that Git for Windows keeps
# (Git Credential Manager; the same one github-push.cmd uses). The token is only passed to api.github.com.
param(
    [Parameter(Mandatory = $true)][string]$Repo,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Branch,
    [Parameter(Mandatory = $true)][string]$NotesFile,
    # Files to attach, separated by ';' (powershell -File cannot pass arrays).
    [Parameter(Mandatory = $true)][string]$Files,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Get-Token {
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        $t = (& gh auth token 2>$null)
        if ($LASTEXITCODE -eq 0 -and $t) { return $t.Trim() }
    }
    $out = "protocol=https`nhost=github.com`n`n" | & git credential fill 2>$null
    $line = $out | Where-Object { $_ -like 'password=*' } | Select-Object -First 1
    if (-not $line) { throw 'No GitHub sign-in. Run github-push.cmd once (it asks to sign in to GitHub in the browser).' }
    return $line.Substring(9).Trim()
}

$headers = @{
    Authorization          = 'Bearer ' + (Get-Token)
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'PcToolkit-release'
}
$api = "https://api.github.com/repos/$Repo"
$tag = "v$Version"

try {
    Invoke-RestMethod -Uri "$api/releases/tags/$tag" -Headers $headers | Out-Null
    Write-Host "Release $tag already exists on github.com/$Repo. Raise Version in Directory.Build.props."
    exit 2
}
catch {
    if ($_.Exception.Response -eq $null -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
}
if ($CheckOnly) { exit 0 }

$body = @{
    tag_name         = $tag
    target_commitish = $Branch
    name             = "PC Toolkit $Version"
    body             = (Get-Content -LiteralPath $NotesFile -Raw -Encoding UTF8)
    draft            = $false
    prerelease       = $false
} | ConvertTo-Json
$release = Invoke-RestMethod -Method Post -Uri "$api/releases" -Headers $headers `
    -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))

$upload = $release.upload_url -replace '\{\?name,label\}$', ''
foreach ($file in ($Files -split ';' | Where-Object { $_ })) {
    $name = [IO.Path]::GetFileName($file)
    Write-Host "Uploading $name ..."
    Invoke-RestMethod -Method Post -Uri ($upload + '?name=' + [Uri]::EscapeDataString($name)) -Headers $headers `
        -ContentType 'application/octet-stream' -InFile $file -TimeoutSec 3600 | Out-Null
}
Write-Host "Done: $($release.html_url)"
