# Run idf.py with the ESP-IDF v5.5.5 environment from C:\Espressif, e.g.
#   .\idf.ps1 build
#   .\idf.ps1 -p COM5 flash monitor
#   .\idf.ps1 menuconfig
param([Parameter(ValueFromRemainingArguments = $true)] [string[]] $Args)

$env:IDF_TOOLS_PATH = 'C:\Espressif'
$env:IDF_PATH = 'C:\Espressif\frameworks\esp-idf-v5.5.5'
$env:IDF_PYTHON_ENV_PATH = 'C:\Espressif\python_env\idf5.5_py3.11_env'
$env:PATH = "$env:IDF_PYTHON_ENV_PATH\Scripts;C:\Espressif\tools\idf-git\2.44.0\cmd;" + $env:PATH
Remove-Item Env:MSYSTEM -ErrorAction SilentlyContinue
Remove-Item Env:PYTHONPATH -ErrorAction SilentlyContinue
Remove-Item Env:PYTHONHOME -ErrorAction SilentlyContinue
. "$env:IDF_PATH\export.ps1" | Out-Null
Set-Location $PSScriptRoot
& python "$env:IDF_PATH\tools\idf.py" @Args
exit $LASTEXITCODE
