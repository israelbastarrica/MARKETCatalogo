# Genera wwwroot/buildinfo.txt con el último commit (hash + fecha + asunto). Lo llama un Target de MSBuild
# en cada build, así el build SIEMPRE sabe de qué commit salió.
#
# Para qué: sin esto no hay forma de saber qué versión está viva en el server. El 10/08/2026 perdimos
# veinte minutos —y borramos la carpeta del sitio al vacío— comparando títulos de páginas a ojo para
# adivinar si el deploy había entrado. El endpoint /version lo responde en un segundo, y el propio
# workflow lo usa para verificar que lo que quedó arriba es el commit que acaba de compilar.
#
# Se hace en PowerShell (no MSBuild/cmd) para evitar el lío de escaping de '%' y de encoding.
$ErrorActionPreference = 'SilentlyContinue'
# git escribe UTF-8; sin esto PowerShell lo decodifica con el codepage de consola y rompe los acentos.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
$info = & git -C $PSScriptRoot -c i18n.logOutputEncoding=UTF-8 log -1 --date=format-local:"%Y-%m-%d %H:%M" --pretty=format:"%h%x09%ad%x09%s" 2>$null
if (-not $info) { $info = '' }
# Si hay cambios sin commitear, el build no refleja del todo el commit → lo aviso.
$dirty = & git -C $PSScriptRoot status --porcelain 2>$null
if ($info -and $dirty) { $info = "$info  (+ cambios sin commitear)" }
$out = Join-Path $PSScriptRoot 'wwwroot\buildinfo.txt'
[System.IO.File]::WriteAllText($out, [string]$info, (New-Object System.Text.UTF8Encoding($false)))
