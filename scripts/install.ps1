[CmdletBinding()]
param([string] $ManifestUrl = $env:HYPANEL_MANIFEST_URL)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string] $Message) { throw "HyPanel Agent installer: $Message" }
function Required([string] $Name, [string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { Fail "Missing $Name." }
    return $Value
}
function Property([object] $Object, [string] $Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { Fail "Manifest is missing '$Name'." }
    return $property.Value
}
function Exact-Properties([object] $Object, [string[]] $Names, [string] $Kind) {
    $actual = @($Object.PSObject.Properties | ForEach-Object Name)
    foreach ($name in $Names) { if ($actual -notcontains $name) { Fail "$Kind is missing '$name'." } }
    foreach ($name in $actual) { if ($Names -notcontains $name) { Fail "$Kind contains unexpected field '$name'." } }
}
function Panel-Uri([string] $Value) {
    $uri = $null
    if (-not [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref] $uri) -or $uri.Scheme -notin @('https', 'http')) {
        Fail 'HYPANEL_PANEL_URL must be an absolute http(s) URL.'
    }
    if ($uri.Scheme -eq 'http' -and ($env:HYPANEL_ALLOW_INSECURE_HTTP -ne '1' -or $uri.Host -notin @('localhost', '127.0.0.1', '::1'))) {
        Fail 'HTTP is allowed only for localhost when HYPANEL_ALLOW_INSECURE_HTTP=1.'
    }
    return $uri
}
function Origin([Uri] $Uri) {
    return "{0}://{1}{2}" -f $Uri.Scheme, $Uri.Host, $(if ($Uri.IsDefaultPort) { '' } else { ":$($Uri.Port)" })
}
function Target-Rid {
    if (-not [Environment]::Is64BitOperatingSystem) { Fail '32-bit Windows is not supported.' }
    $processArch = [string]$env:PROCESSOR_ARCHITECTURE
    $runtimeArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    if ($processArch -eq 'ARM64' -or $runtimeArch -eq 'Arm64') { return 'win-arm64' }
    if ($processArch -eq 'AMD64' -or $runtimeArch -eq 'X64') { return 'win-x64' }
    Fail "Unsupported Windows architecture ($processArch/$runtimeArch)."
}
function Restricted-File([string] $Path, [string] $Content) {
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
    $acl = Get-Acl $Path
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) { [void]$acl.RemoveAccessRule($rule) }
    foreach ($identity in @('SYSTEM', 'BUILTIN\Administrators')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'Allow'))
    }
    Set-Acl -Path $Path -AclObject $acl
}
function Safe-Zip([string] $ZipPath, [string] $StagingPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $rootExe = $false
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if ([string]::IsNullOrWhiteSpace($name) -or $name -match '^[\\/]' -or $name -match '^[A-Za-z]:') { Fail 'Archive contains an absolute path.' }
            $normalized = $name.Replace('\', '/')
            $parts = @($normalized -split '/')
            if ($parts | Where-Object { $_ -eq '..' -or $_ -eq '' }) {
                if ($normalized -notmatch '/$' -or ($parts | Where-Object { $_ -eq '' }).Count -gt 1) { Fail 'Archive contains an unsafe path.' }
            }
            if ($normalized -eq 'HyPanel.Agent.exe') { $rootExe = $true }
            if ($normalized -match '(^|/)([^/]+)$' -and $normalized -ne 'HyPanel.Agent.exe' -and $normalized -notmatch '/') {
                Fail 'Archive contains an unexpected root-level file.'
            }
            if ($normalized -match '(^|/)\.(git|ssh|env)(/|$)' -or $normalized -match '(^|/)(bootstrap\.env|credentials\.json)$') {
                Fail 'Archive contains a forbidden layout.'
            }
        }
        if (-not $rootExe) { Fail 'Archive must contain root-level HyPanel.Agent.exe.' }
    } finally { $archive.Dispose() }
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $StagingPath -Force
    if (-not (Test-Path (Join-Path $StagingPath 'HyPanel.Agent.exe') -PathType Leaf)) { Fail 'Archive extraction did not produce HyPanel.Agent.exe.' }
}

try {
    $panel = Panel-Uri (Required 'HYPANEL_PANEL_URL' $env:HYPANEL_PANEL_URL)
    $token = Required 'HYPANEL_ENROLLMENT_TOKEN' $env:HYPANEL_ENROLLMENT_TOKEN
    $rid = Target-Rid
    $frozenRids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'linux-musl-x64', 'linux-musl-arm64', 'osx-x64', 'osx-arm64')
    if ([string]::IsNullOrWhiteSpace($ManifestUrl)) { $ManifestUrl = [Uri]::new($panel, 'api/agent/manifest.json').AbsoluteUri }
    $manifestUri = $null
    if (-not [Uri]::TryCreate($ManifestUrl, [UriKind]::Absolute, [ref] $manifestUri) -or (Origin $manifestUri) -ne (Origin $panel)) { Fail 'Manifest URL must be same-origin.' }
    $manifest = Invoke-RestMethod -Uri $manifestUri.AbsoluteUri -Method Get -UseBasicParsing
    Exact-Properties $manifest @('schemaVersion', 'version', 'publishedAt', 'assets') 'Manifest'
    if ([int](Property $manifest 'schemaVersion') -ne 1) { Fail 'Manifest schemaVersion must be 1.' }
    $version = [string](Property $manifest 'version')
    if ($version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') { Fail 'Manifest version is invalid.' }
    $publishedAt = $null
    if (-not [DateTimeOffset]::TryParse([string](Property $manifest 'publishedAt'), [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref] $publishedAt)) { Fail 'Manifest publishedAt is invalid.' }
    $assets = @(Property $manifest 'assets')
    if ($assets.Count -ne 8) { Fail 'Manifest must contain exactly eight assets.' }
    $seenRids = @{}
    $seenNames = @{}
    foreach ($asset in $assets) {
        Exact-Properties $asset @('rid', 'fileName', 'sha256', 'size') 'Asset'
        $assetRid = [string](Property $asset 'rid')
        if ($frozenRids -notcontains $assetRid -or $seenRids.ContainsKey($assetRid)) { Fail 'Manifest contains an invalid or duplicate RID.' }
        $seenRids[$assetRid] = $true
        $fileName = [string](Property $asset 'fileName')
        if ([IO.Path]::GetFileName($fileName) -ne $fileName -or $fileName -match '[\\/]' -or $fileName -notmatch ('^hypanel-agent-' + [Regex]::Escape($version) + '-(win-x64|win-arm64|linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64|osx-x64|osx-arm64)\.zip$')) { Fail 'Manifest asset fileName is invalid.' }
        if ($seenNames.ContainsKey($fileName)) { Fail 'Manifest asset basenames must be unique.' }
        $seenNames[$fileName] = $true
        $hash = [string](Property $asset 'sha256')
        if ($hash -cnotmatch '^[0-9a-f]{64}$') { Fail 'Manifest sha256 must be lowercase hexadecimal.' }
        $assetSize = [Int64](Property $asset 'size')
        if ($assetSize -lt 1) { Fail 'Manifest asset size must be positive.' }
    }
    foreach ($requiredRid in $frozenRids) { if (-not $seenRids.ContainsKey($requiredRid)) { Fail "Manifest is missing RID '$requiredRid'." } }
    $selected = @($assets | Where-Object { [string](Property $_ 'rid') -eq $rid })[0]
    $fileName = [string](Property $selected 'fileName')
    $assetUri = [Uri]::new((Origin $panel) + '/api/releases/v1/assets/' + [Uri]::EscapeDataString($fileName))

    $overrideRoot = $env:HYPANEL_INSTALL_ROOT
    $stagingMode = $env:STAGING_INSTALL -eq '1'
    $dryRoot = -not [string]::IsNullOrWhiteSpace($overrideRoot)
    $root = if ($dryRoot) { [IO.Path]::GetFullPath($overrideRoot) } else { Join-Path $env:ProgramFiles 'HyPanel' }
    $agentDir = Join-Path $root 'Agent'
    $dataDir = if ($dryRoot) { Join-Path $root 'data' } else { Join-Path $env:ProgramData 'HyPanel\Agent' }
    $download = Join-Path ([IO.Path]::GetTempPath()) ("HyPanel.Agent.{0}.zip" -f [Guid]::NewGuid())
    $staging = Join-Path ([IO.Path]::GetTempPath()) ("HyPanel.Agent.{0}.staging" -f [Guid]::NewGuid())
    $serviceExists = $false
    New-Item -ItemType Directory -Force -Path $root, $dataDir | Out-Null
    try {
        Invoke-WebRequest -Uri $assetUri.AbsoluteUri -OutFile $download -UseBasicParsing
        if ((Get-Item $download).Length -ne [Int64](Property $selected 'size')) { Fail 'Downloaded archive size does not match manifest.' }
        if ((Get-FileHash $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne [string](Property $selected 'sha256')) { Fail 'Downloaded archive hash does not match manifest.' }
        New-Item -ItemType Directory -Force -Path $staging | Out-Null
        Safe-Zip $download $staging
        if ($stagingMode) { exit 0 }
        $serviceExists = (-not $dryRoot) -and ((sc.exe query HyPanelAgent 2>$null) -match 'SERVICE_NAME')
        if ($serviceExists) { sc.exe stop HyPanelAgent | Out-Null; Start-Sleep -Seconds 1 }
        $backupDir = "$agentDir.bak"
        if (Test-Path $backupDir) { Remove-Item $backupDir -Recurse -Force }
        if (Test-Path $agentDir) { Move-Item $agentDir $backupDir }
        Move-Item $staging $agentDir
        if (-not $dryRoot) {
            $bootstrap = Join-Path $dataDir 'bootstrap.env'
            Restricted-File $bootstrap "HYPANEL_PANEL_URL=$($panel.AbsoluteUri)`r`nHYPANEL_ENROLLMENT_TOKEN=$token`r`n"
            $serviceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\HyPanelAgent'
            New-Item -Path $serviceKey -Force | Out-Null
            New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Value @("HYPANEL_PANEL_URL=$($panel.AbsoluteUri)", "HYPANEL_ENROLLMENT_TOKEN=$token") -Force | Out-Null
            if (-not $serviceExists) { sc.exe create HyPanelAgent binPath= "`"$(Join-Path $agentDir 'HyPanel.Agent.exe')`"" start= auto | Out-Null }
            sc.exe config HyPanelAgent binPath= "`"$(Join-Path $agentDir 'HyPanel.Agent.exe')`"" start= auto | Out-Null
            sc.exe start HyPanelAgent | Out-Null
            $credentials = Join-Path $dataDir 'credentials.json'
            $deadline = (Get-Date).AddSeconds(30)
            while (-not (Test-Path $credentials) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
            if (-not (Test-Path $credentials)) { Fail 'Agent did not complete enrollment within 30 seconds.' }
            Restricted-File $bootstrap "HYPANEL_DATA_DIR=$dataDir`r`n"
            New-ItemProperty -Path $serviceKey -Name Environment -PropertyType MultiString -Value @("HYPANEL_DATA_DIR=$dataDir") -Force | Out-Null
        }
    } catch {
        if (Test-Path "$agentDir.bak") {
            if (Test-Path $agentDir) { Remove-Item $agentDir -Recurse -Force }
            Move-Item "$agentDir.bak" $agentDir
        }
        if (-not $dryRoot -and $serviceExists) { sc.exe start HyPanelAgent | Out-Null }
        throw
    } finally { Remove-Item $download, $staging -Recurse -Force -ErrorAction SilentlyContinue }
} catch { Write-Error $_.Exception.Message; exit 1 }
