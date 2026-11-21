@echo off
setlocal
pushd "%~dp0" || exit /b 1
if exist "ffmpeg.exe" (
    echo FFmpeg est deja disponible. Tu peux lancer ClipLeger.exe.
    pause
    popd
    exit /b 0
)

set "CLIP_FFMPEG_URL=https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.7z"
set "CLIP_SETUP_DIR=%TEMP%\ClipLeger-FFmpeg-%RANDOM%-%RANDOM%"
mkdir "%CLIP_SETUP_DIR%" || goto :error
echo Telechargement de FFmpeg depuis gyan.dev...
curl.exe --proto "=https" --proto-redir "=https" --fail --show-error --silent --location --connect-timeout 15 --max-time 300 --output "%CLIP_SETUP_DIR%\archive.7z" "%CLIP_FFMPEG_URL%" || goto :cleanup_error
curl.exe --proto "=https" --proto-redir "=https" --fail --show-error --silent --location --connect-timeout 15 --max-time 30 --output "%CLIP_SETUP_DIR%\archive.sha256" "%CLIP_FFMPEG_URL%.sha256" || goto :cleanup_error

echo Verification du fichier telecharge...
powershell.exe -NoProfile -NonInteractive -Command "$ErrorActionPreference='Stop'; $d=$env:CLIP_SETUP_DIR; $checksum=[regex]::Match([IO.File]::ReadAllText([IO.Path]::Combine($d,'archive.sha256')), '[0-9a-fA-F]{64}').Value; $archive=[IO.File]::ReadAllBytes([IO.Path]::Combine($d,'archive.7z')); $actual=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($archive)).Replace('-',''); if ($actual -ne $checksum) { exit 1 }" || goto :cleanup_error
tar.exe -xf "%CLIP_SETUP_DIR%\archive.7z" -C "%CLIP_SETUP_DIR%" --include "*/bin/ffmpeg.exe" --strip-components 2 || goto :cleanup_error
if not exist "%CLIP_SETUP_DIR%\ffmpeg.exe" goto :cleanup_error
if not exist "ffmpeg.exe" copy /-Y "%CLIP_SETUP_DIR%\ffmpeg.exe" "ffmpeg.exe" <nul >nul || goto :cleanup_error
call :cleanup

echo FFmpeg est pret. Tu peux lancer ClipLeger.exe.
pause
popd
exit /b 0

:cleanup_error
call :cleanup
:error
echo Installation impossible. Consulte le README pour installer FFmpeg manuellement.
pause
popd
exit /b 1

:cleanup
del /q "%CLIP_SETUP_DIR%\archive.7z" "%CLIP_SETUP_DIR%\archive.sha256" "%CLIP_SETUP_DIR%\ffmpeg.exe" 2>nul
rmdir "%CLIP_SETUP_DIR%" 2>nul
exit /b 0
