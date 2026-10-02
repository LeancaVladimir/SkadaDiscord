# Сборка архива релиза для GitHub (его скачивает автообновление программы).
# Запуск: powershell -ExecutionPolicy Bypass -File src\release.ps1 -Exe <путь к SkadaDiscord.exe> -Out <папка> [-Notes <файл .md>]
# Результат: <папка>\SkadaDiscord-<версия>.zip и <папка>\release-notes.md (с строкой SHA256 для проверки).
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Notes
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent   # папка игры (там Interface\ и SkadaDiscord\)
$addon = Join-Path $root 'Interface\AddOns\SkadaDiscord'
$prog = Join-Path $root 'SkadaDiscord'
$version = (Get-Item $Exe).VersionInfo.FileVersion
if (-not $version) { throw "у $Exe нет версии" }
$version = ([version]$version).ToString(3)

New-Item -ItemType Directory -Force $Out | Out-Null
$zipPath = Join-Path $Out "SkadaDiscord-$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }

# файлы архива: путь внутри архива (через /) -> файл на диске
$files = [ordered]@{
    'SkadaDiscord/SkadaDiscord.exe'     = $Exe
    'SkadaDiscord/README.txt'           = Join-Path $prog 'README.txt'
    'SkadaDiscord/config.example.json'  = Join-Path $prog 'config.example.json'
    'SkadaDiscord - README.txt'         = Join-Path $prog 'README.txt'
}
foreach ($f in Get-ChildItem $addon -File | Where-Object { $_.Extension -in '.lua', '.toc' }) {
    $files["Interface/AddOns/SkadaDiscord/$($f.Name)"] = $f.FullName
}
# Config.lua в архиве - всегда пустой (у пользователя он может содержать перенос настроек со ссылками)
$cleanConfig = Join-Path $Out 'Config.lua'
[IO.File]::WriteAllText($cleanConfig, "-- SkadaDiscord: через этот файл программа один раз передаёт аддону свои старые настройки.`n-- Сейчас он пуст: каналы, боссы и окна отчёта настраиваются в игре (/sd → «Настройки»).`n", (New-Object Text.UTF8Encoding $false))
$files['Interface/AddOns/SkadaDiscord/Config.lua'] = $cleanConfig

# проверка: ни одной ссылки-вебхука в архиве
foreach ($p in $files.Values) {
    if ($p -notmatch '\.exe$' -and (Select-String -Path $p -Pattern 'discord(app)?\.com/api/webhooks/\d{6,}/[\w-]{20,}' -Quiet)) { throw "в $p есть ссылка на вебхук!" }
}

# zip с путями через / (CreateFromDirectory в старых .NET пишет \)
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    foreach ($kv in $files.GetEnumerator()) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $kv.Value, $kv.Key, 'Optimal')
    }
} finally { $zip.Dispose() }

$sha = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
$body = if ($Notes -and (Test-Path $Notes)) { [IO.File]::ReadAllText($Notes, [Text.Encoding]::UTF8).TrimEnd() } else { "## SkadaDiscord $version" }
$body += "`n`n---`nУстановка: распакуйте архив в папку с игрой (где Wow.exe). Если программа уже стоит — она обновится сама.`n`nSHA256: $sha`n"
[IO.File]::WriteAllText((Join-Path $Out 'release-notes.md'), $body, (New-Object Text.UTF8Encoding $false))
Write-Host "Готово: $zipPath"
Write-Host "Версия: v$version"
Write-Host "SHA256: $sha"
