param(
    [string]$ProjectPath = 'C:\BlitzBuild\project',
    # Default: the Hub editor matching ProjectSettings/ProjectVersion.txt.
    [string]$Unity = '',
    [string]$SourcePath = '\\host.lan\Data\project',
    [string]$OutputPath = '\\host.lan\Data\windows-output',
    [string]$BuildId,
    [string]$Revision
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Windows IL2CPP requires Windows; no Mono fallback.' }
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (!(Test-Path $vswhere)) { throw 'Visual Studio C++ Build Tools are missing' }
$vs = & $vswhere -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'MSVC x64 toolchain is missing' }
if (!(Test-Path "${env:ProgramFiles(x86)}\Windows Kits\10\Include")) { throw 'Windows SDK is missing' }
if (!$BuildId -or !$Revision) { throw 'BuildId and source Revision are required' }
New-Item -ItemType Directory -Force $ProjectPath | Out-Null
& robocopy $SourcePath $ProjectPath /MIR /R:2 /W:2 /XD Library Temp Obj Build Logs UserSettings /NFL /NDL /NJH /NJS
if ($LASTEXITCODE -ge 8) { throw "Project copy failed: $LASTEXITCODE" }
$editor = Select-String -Path (Join-Path $ProjectPath 'ProjectSettings\ProjectVersion.txt') -Pattern '^m_EditorVersion: (.+)$'
$editorVersion = $editor.Matches[0].Groups[1].Value.Trim()
if (!$Unity) { $Unity = "C:\Program Files\Unity\Hub\Editor\$editorVersion\Editor\Unity.exe" }
if (!(Test-Path $Unity)) { throw "Unity $editorVersion is missing: $Unity" }
$settings = Join-Path $ProjectPath 'ProjectSettings\ProjectSettings.asset'
$version = Select-String -Path $settings -Pattern '^  bundleVersion: (.+)$'
$env:BLITZ_RELEASE_VERSION = $version.Matches[0].Groups[1].Value.Trim()
# Version is a source input, not a deployment-specific hardcoded release.
$releaseVersion = $env:BLITZ_RELEASE_VERSION
# The release code (BlitzVersion.Code, also the APK versionCode) is the
# numeric revision: it grows with every alpha, beta and hotfix label, so the
# PE FileVersion stays monotonic (2.0a2f1 / 203 -> 2.0.0.203, 2.0b1 / 205 -> 2.0.0.205).
$code = Select-String -Path $settings -Pattern '^  AndroidBundleVersionCode: (\d+)$'
if (!$code) { throw 'AndroidBundleVersionCode is missing' }
$releaseCode = [int]$code.Matches[0].Groups[1].Value
if ($releaseCode -lt 1 -or $releaseCode -gt 65535) { throw "Release code $releaseCode does not fit a PE version field" }
if ($releaseVersion -match '^(\d+)\.(\d+)[ab]\d+(?:f\d+)?$') {
    $numericVersion = "$($Matches[1]).$($Matches[2]).0.$releaseCode"
} elseif ($releaseVersion -match '^\d+\.\d+\.\d+$') {
    $numericVersion = "$releaseVersion.$releaseCode"
} else { throw 'Unsupported source release version for PE stamping' }
$env:BLITZ_BUILD_ID = $BuildId
$env:BLITZ_REVISION = $Revision
New-Item -ItemType Directory -Force (Join-Path $ProjectPath 'Logs') | Out-Null
$log = Join-Path $ProjectPath 'Logs\windows-il2cpp.log'
$arguments = "-batchmode -nographics -quit -buildTarget Win64 -projectPath `"$ProjectPath`" -logFile `"$log`" -executeMethod BlitzLegacy.EditorTools.BuildPlayerCli.WindowsProductionDebug"
# The host share can drop its SMB session during a long build; take rcedit
# local first and wait for the share to come back before writing results.
$rcedit = Join-Path (Split-Path $ProjectPath) 'tools\rcedit-x64.exe'
New-Item -ItemType Directory -Force (Split-Path $rcedit) | Out-Null
Copy-Item '\\host.lan\Data\rcedit-x64.exe' $rcedit -Force
function Wait-Share {
    for ($i = 0; $i -lt 20; $i++) {
        try { New-Item -ItemType Directory -Force $OutputPath -ErrorAction Stop | Out-Null; return }
        catch { Start-Sleep -Seconds 15 }
    }
    throw "Host share unavailable: $OutputPath"
}
$editor = Start-Process $Unity -ArgumentList $arguments -Wait -PassThru
Wait-Share
Copy-Item $log (Join-Path $OutputPath 'windows-il2cpp.log') -Force
if ($editor.ExitCode -ne 0) { throw "Windows IL2CPP build failed: $($editor.ExitCode); see $log" }
$build = Join-Path $ProjectPath 'Build\Windows-productiondebug'
foreach ($file in @('BlitzLegacy.exe', 'GameAssembly.dll', 'BlitzLegacy_Data\il2cpp_data\Metadata\global-metadata.dat')) {
    if (!(Test-Path (Join-Path $build $file))) { throw "Missing IL2CPP artifact: $file" }
}
if (Test-Path (Join-Path $build 'BlitzLegacy_Data\Managed\BlitzLegacy.Runtime.dll')) { throw 'Refusing a managed runtime package' }
if (!(Select-String -Path $log -Pattern 'build Succeeded backend=IL2CPP development=False defines=BLITZ_DEBUG_TOOLS')) { throw 'ProductionDebug build report verification failed' }
# Unity's loader resource retains the engine version for nonnumeric alpha
# labels. Stamp the application identity without changing engine DLL versions.
if ((Get-FileHash $rcedit -Algorithm SHA256).Hash.ToLowerInvariant() -ne '3e7801db1a5edbec91b49a24a094aad776cb4515488ea5a4ca2289c400eade2a') { throw 'rcedit checksum mismatch' }
$exe = Join-Path $build 'BlitzLegacy.exe'
& $rcedit $exe --set-file-version $numericVersion --set-product-version $numericVersion --set-version-string 'ProductVersion' $env:BLITZ_RELEASE_VERSION --set-version-string 'ProductName' 'Blitz Legacy: Remastered Deluxe Edition' --set-version-string 'FileDescription' 'Blitz Legacy: Remastered Deluxe Edition' --set-version-string 'CompanyName' 'Blitz Legacy'
if ($LASTEXITCODE -ne 0) { throw 'Application PE resource stamping failed' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion -ne $env:BLITZ_RELEASE_VERSION) { throw 'Windows application version mismatch' }
Copy-Item (Join-Path $ProjectPath 'tutorial_mods.txt') $build -Force
Wait-Share
& robocopy $build (Join-Path $OutputPath 'player') /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS
if ($LASTEXITCODE -ge 8) { throw 'Player output copy failed' }
@{version=$env:BLITZ_RELEASE_VERSION;buildId=$BuildId;revision=$Revision;backend='IL2CPP';development=$false;debugTools=$true} | ConvertTo-Json | Set-Content (Join-Path $OutputPath 'build-result.json') -Encoding UTF8
Write-Output 'WINDOWS_IL2CPP_BUILD_OK'
