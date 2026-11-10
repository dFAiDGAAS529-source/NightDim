param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '1.1.2',

    [string] $CompilerPath,

    [string] $PublishDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $projectRoot 'artifacts'

if (!$PublishDirectory) {
    $PublishDirectory = Join-Path $artifacts 'NightDim-win-x64'
}
$payload = (Resolve-Path -LiteralPath $PublishDirectory).Path
foreach ($file in @('NightDim.exe', 'NightDim.dll', 'NightDim.deps.json', 'NightDim.runtimeconfig.json', 'coreclr.dll', 'PresentationFramework.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $payload $file) -PathType Leaf)) {
        throw "The self-contained win-x64 publication is missing $file. Publish NightDim before building the installer."
    }
}
$dependencies = Get-Content -Raw -LiteralPath (Join-Path $payload 'NightDim.deps.json') | ConvertFrom-Json
if ($dependencies.runtimeTarget.name -notlike '*/win-x64') {
    throw 'Publish the payload with the win-x64 runtime identifier.'
}
$productVersion = (Get-Item -LiteralPath (Join-Path $payload 'NightDim.exe')).VersionInfo.ProductVersion
if (($productVersion -split '\+', 2)[0] -ne $Version) {
    throw "Published NightDim.exe is version $productVersion, but the requested installer is $Version."
}

if (!$CompilerPath) {
    $compilerCommand = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue
    $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
    $candidates = @(
        (Join-Path $artifacts 'installer-tools\InnoSetup\ISCC.exe')
        $compilerCommand.Source
        "$programFilesX86\Inno Setup 6\ISCC.exe"
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $CompilerPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
        Select-Object -First 1
}
if (!$CompilerPath) {
    throw 'Inno Setup 6.7 or later is required. Install it from https://jrsoftware.org/isdl.php or pass -CompilerPath.'
}
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$icon = (Resolve-Path -LiteralPath (Join-Path $projectRoot 'src\NightDim\Assets\NightDim.ico')).Path
$arguments = @(
    "/DNightDimVersion=$Version"
    "/DNightDimPayload=$payload"
    "/DNightDimOutput=$artifacts"
    "/DNightDimIcon=$icon"
    (Join-Path $PSScriptRoot 'NightDim.iss')
)

& $compiler @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$setup = Join-Path $artifacts "NightDim-Setup-$Version-win-x64.exe"
Get-FileHash -LiteralPath $setup -Algorithm SHA256
