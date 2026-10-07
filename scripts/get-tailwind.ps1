# Downloads the standalone Tailwind CSS CLI used to compile src/Notify.Web/Styles/app.css.
# The binary is git-ignored; run this once per machine (or after cloning).
#   powershell -ExecutionPolicy Bypass -File scripts/get-tailwind.ps1
$ErrorActionPreference = "Stop"
$version = "v3.4.17"
$target = Join-Path $PSScriptRoot "tailwindcss.exe"
$url = "https://github.com/tailwindlabs/tailwindcss/releases/download/$version/tailwindcss-windows-x64.exe"

if (Test-Path $target) {
    Write-Host "Tailwind CLI already present at $target"
    exit 0
}

Write-Host "Downloading Tailwind CSS $version standalone CLI..."
Invoke-WebRequest -Uri $url -OutFile $target -UseBasicParsing
Write-Host "Saved to $target"
& $target --help | Select-Object -First 2
