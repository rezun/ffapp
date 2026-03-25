$ErrorActionPreference = "Stop"

param(
    [switch]$DryRun,
    [switch]$SkipArchive
)

function Get-PythonPath {
    if ($env:PYTHON_BIN) {
        return $env:PYTHON_BIN
    }

    if ($env:VIRTUAL_ENV) {
        $candidate = Join-Path $env:VIRTUAL_ENV "Scripts\python.exe"
        if (Test-Path $candidate) {
            return $candidate
        }
    }

    $projectVenv = Join-Path $PSScriptRoot "..\.venv\Scripts\python.exe"
    if (Test-Path $projectVenv) {
        return (Resolve-Path $projectVenv).Path
    }

    $command = Get-Command py -ErrorAction SilentlyContinue
    if ($command) {
        return "py -3.13"
    }

    throw "Could not find a usable Python interpreter."
}

function Invoke-Python {
    param(
        [string]$PythonCommand,
        [string[]]$Arguments
    )

    if ($PythonCommand -eq "py -3.13") {
        & py -3.13 @Arguments
        return
    }

    & $PythonCommand @Arguments
}

function Get-PythonVersion {
    param([string]$PythonCommand)

    $code = 'import sys; print(f"{sys.version_info.major}.{sys.version_info.minor}")'
    if ($PythonCommand -eq "py -3.13") {
        return (& py -3.13 -c $code).Trim()
    }

    return (& $PythonCommand -c $code).Trim()
}

if (-not $IsWindows) {
    throw "scripts/build-windows.ps1 must be run on Windows."
}

$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$SpecFile = Join-Path $ProjectRoot "pysidedeploy.spec"
$DistDir = Join-Path $ProjectRoot "dist"
$IconFile = Join-Path $ProjectRoot "assets\icons\windows\app-icon.ico"
$Python = Get-PythonPath
$PythonVersion = Get-PythonVersion -PythonCommand $Python

if ($PythonVersion -ne "3.13") {
    throw "Packaging currently requires Python 3.13. Found $PythonVersion at $Python"
}

$PysideDeploy = if ($Python -eq "py -3.13") {
    Join-Path (Split-Path (& py -3.13 -c "import sys; print(sys.executable)") -Parent) "pyside6-deploy.exe"
} else {
    Join-Path (Split-Path $Python -Parent) "pyside6-deploy.exe"
}

if (-not (Test-Path $PysideDeploy)) {
    $command = Get-Command pyside6-deploy.exe -ErrorAction SilentlyContinue
    if ($command) {
        $PysideDeploy = $command.Path
    } else {
        throw "Could not find pyside6-deploy in the selected Python environment."
    }
}

if (-not (Test-Path $SpecFile)) {
    throw "Missing $SpecFile"
}

if (-not $env:CCACHE_DISABLE) {
    $env:CCACHE_DISABLE = "1"
}

$normalizeScript = @'
from __future__ import annotations

import configparser
import sys
from pathlib import Path

spec_path = Path(sys.argv[1])
python_path = sys.argv[2]
icon_path = Path(sys.argv[3])

spec = configparser.ConfigParser()
spec.read(spec_path)
spec["python"]["python_path"] = python_path
spec["app"]["icon"] = str(icon_path) if icon_path.is_file() else ""

with spec_path.open("w", encoding="utf-8") as handle:
    spec.write(handle)
'@

if ($Python -eq "py -3.13") {
    $ResolvedPython = (& py -3.13 -c "import sys; print(sys.executable)").Trim()
} else {
    $ResolvedPython = $Python
}

$normalizeScriptPath = Join-Path $env:TEMP "ffapp-normalize-spec.py"
Set-Content -Path $normalizeScriptPath -Value $normalizeScript -Encoding UTF8
try {
    Invoke-Python -PythonCommand $Python -Arguments @($normalizeScriptPath, $SpecFile, $ResolvedPython, $IconFile)
} finally {
    if (Test-Path $normalizeScriptPath) {
        Remove-Item -Force $normalizeScriptPath
    }
}

$metaScript = @'
from __future__ import annotations

import configparser
import re
import sys
import tomllib
from pathlib import Path

project_root = Path(sys.argv[1])
pyproject = tomllib.loads((project_root / "pyproject.toml").read_text())
spec = configparser.ConfigParser()
spec.read(project_root / "pysidedeploy.spec")

project_name = pyproject["project"]["name"]
version = pyproject["project"]["version"]
app_title = spec["app"]["title"].strip() or project_name
artifact_name = re.sub(r"[^a-z0-9]+", "-", project_name.lower()).strip("-")

print(project_name)
print(version)
print(app_title)
print(artifact_name)
'@

$metaScriptPath = Join-Path $env:TEMP "ffapp-build-meta.py"
Set-Content -Path $metaScriptPath -Value $metaScript -Encoding UTF8
try {
    $BuildMetaOutput = if ($Python -eq "py -3.13") {
        & py -3.13 $metaScriptPath $ProjectRoot
    } else {
        & $Python $metaScriptPath $ProjectRoot
    }
} finally {
    if (Test-Path $metaScriptPath) {
        Remove-Item -Force $metaScriptPath
    }
}

$BuildMeta = $BuildMetaOutput -split "`r?`n"

$ProjectName = $BuildMeta[0]
$Version = $BuildMeta[1]
$AppTitle = $BuildMeta[2]
$ArtifactName = $BuildMeta[3]

$AppDistDir = Join-Path $DistDir "$AppTitle.dist"
$ArchivePath = Join-Path $DistDir "$ArtifactName-$Version-windows.zip"

New-Item -ItemType Directory -Force -Path $DistDir | Out-Null

Write-Host "Using Python: $ResolvedPython"
Write-Host "Building $ProjectName $Version"

Invoke-Python -PythonCommand $Python -Arguments @((Join-Path $ProjectRoot "scripts\sync_editable_version.py")) | Out-Null

if ($DryRun) {
    & $PysideDeploy -c $SpecFile --dry-run -f
    exit $LASTEXITCODE
}

if (Test-Path $AppDistDir) {
    Remove-Item -Recurse -Force $AppDistDir
}

if (Test-Path $ArchivePath) {
    Remove-Item -Force $ArchivePath
}

& $PysideDeploy -c $SpecFile -f

if (-not (Test-Path $AppDistDir)) {
    throw "Expected standalone bundle was not produced: $AppDistDir"
}

if ($SkipArchive) {
    Write-Host "Standalone bundle created: $AppDistDir"
    exit 0
}

Compress-Archive -Path $AppDistDir -DestinationPath $ArchivePath -Force
Write-Host "Archive created: $ArchivePath"
