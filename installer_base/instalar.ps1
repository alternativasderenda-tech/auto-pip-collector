# ============================================================
#  Auto-Pip Collector - Instalador
#  Acha o Fallout Shelter, instala o mod + o app, e libera tudo.
# ============================================================
$ErrorActionPreference = 'Stop'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# auto-eleva (precisa de admin p/ escrever na pasta do jogo)
if (-not (Test-Admin)) {
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","`"$PSCommandPath`""
    exit
}

Add-Type -AssemblyName System.Windows.Forms
function Msg($t, $titulo = "Auto-Pip Collector") { [System.Windows.Forms.MessageBox]::Show($t, $titulo) | Out-Null }

function Find-Game {
    $guesses = @(
        "C:\Program Files (x86)\Steam\steamapps\common\Fallout Shelter",
        "C:\Program Files\Steam\steamapps\common\Fallout Shelter",
        "D:\Steam\steamapps\common\Fallout Shelter",
        "D:\SteamLibrary\steamapps\common\Fallout Shelter",
        "E:\SteamLibrary\steamapps\common\Fallout Shelter",
        "E:\Steam\steamapps\common\Fallout Shelter"
    )
    foreach ($g in $guesses) { if (Test-Path (Join-Path $g "FalloutShelter.exe")) { return $g } }
    $vdf = "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        foreach ($line in Get-Content $vdf) {
            if ($line -match '"path"\s+"([^"]+)"') {
                $p = $matches[1] -replace '\\\\', '\'
                $cand = Join-Path $p "steamapps\common\Fallout Shelter"
                if (Test-Path (Join-Path $cand "FalloutShelter.exe")) { return $cand }
            }
        }
    }
    return $null
}

try {
    $src = Join-Path $PSScriptRoot "arquivos_jogo"
    if (-not (Test-Path $src)) { Msg "Pasta 'arquivos_jogo' nao encontrada ao lado do instalador."; exit }

    # jogo aberto? pede pra fechar
    if (Get-Process FalloutShelter -ErrorAction SilentlyContinue) {
        Msg "Feche o Fallout Shelter antes de instalar, depois rode o instalador de novo."
        exit
    }

    $game = Find-Game
    if (-not $game) {
        Msg "Nao encontrei o Fallout Shelter automaticamente. Na proxima janela, ache e selecione o arquivo FalloutShelter.exe (dentro da pasta do jogo na Steam)."
        $ofd = New-Object System.Windows.Forms.OpenFileDialog
        $ofd.Filter = "FalloutShelter.exe|FalloutShelter.exe"
        $ofd.Title = "Selecione o FalloutShelter.exe"
        if ($ofd.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { exit }
        $game = Split-Path $ofd.FileName
    }

    # 1) copia BepInEx + mod + configs pra pasta do jogo
    Copy-Item (Join-Path $src "*") $game -Recurse -Force

    # 2) libera escrita na pasta de config (Users = SID S-1-5-32-545, independe do idioma)
    icacls (Join-Path $game "BepInEx\config") /grant "*S-1-5-32-545:(OI)(CI)M" /T | Out-Null

    # 3) instala o app num lugar do usuario + atalho na area de trabalho
    $appDir = Join-Path $env:LOCALAPPDATA "AutoPipCollector"
    New-Item -ItemType Directory -Force $appDir | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "AutoPipCollector.exe") $appDir -Force
    $appExe = Join-Path $appDir "AutoPipCollector.exe"

    $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) "Auto-Pip Collector.lnk"
    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($lnk)
    $sc.TargetPath = $appExe
    $sc.IconLocation = $appExe
    $sc.WorkingDirectory = $appDir
    $sc.Description = "Auto-Pip Collector - coletor do Fallout Shelter"
    $sc.Save()

    Msg ("Instalado com sucesso!`n`nJogo: " + $game + "`n`nComo usar:`n1) Abra o Fallout Shelter pela Steam`n2) Abra o atalho 'Auto-Pip Collector' na area de trabalho`n`nPronto - ele coleta sozinho!") "Concluido"
}
catch {
    Msg ("Deu um erro na instalacao:`n`n" + $_.Exception.Message) "Erro"
}
