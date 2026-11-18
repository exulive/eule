# Builds the Eule installer with Velopack.
#   .\pack.ps1 -Version 0.2.0
# Output: .\releases\Eule-win-Setup.exe (+ the update packages Velopack needs).
# The installer is framework-dependent and installs the .NET 8 desktop runtime itself when it is missing.
# Requires: dotnet tool install -g vpk --version <same version as the Velopack package>
param(
    [string]$Version = "",
    [string]$Channel = "win"
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content src/Eule.App/Eule.App.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
Write-Host "Eule $Version paketleniyor..."

dotnet test --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Testler başarısız" }

$publish = Join-Path $PSScriptRoot "publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish src/Eule.App -c Release -r win-x64 --self-contained false -p:Version=$Version -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Yayınlama başarısız" }

vpk pack `
    --packId Eule `
    --packVersion $Version `
    --packDir $publish `
    --mainExe Eule.exe `
    --packTitle Eule `
    --packAuthors "Eule" `
    --icon src/Eule.App/Assets/eule.ico `
    --framework net8.0-x64-desktop `
    --runtime win-x64 `
    --channel $Channel `
    --outputDir releases
if ($LASTEXITCODE -ne 0) { throw "Paketleme başarısız" }

Get-ChildItem releases | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
