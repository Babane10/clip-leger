# Clip Léger

Clip Léger est une petite appli Windows pour compresser une vidéo en MP4 avant de l’envoyer sur Discord. L’appli vérifie la taille réelle du MP4 avant de l’exporter.

## Installer sur Windows

1. Récupère `Clip-Leger-v1.0.0-Windows-x64.zip` dans **Releases**, puis extrais le ZIP.
2. Lance `Installer-FFmpeg.cmd` une seule fois pour télécharger le moteur vidéo officiel et vérifier sa somme SHA-256. Tu peux aussi placer un `ffmpeg.exe` récent avec `libx264` à côté de `ClipLeger.exe`.
3. Ouvre `ClipLeger.exe`, glisse une vidéo, choisis une taille maximale et clique sur **Compresser en MP4**.

Le dossier de sortie par défaut est **Téléchargements → Discord**. Tu peux le changer dans l’appli. Le fichier d’origine est conservé et les vidéos sont traitées localement. Une connexion est nécessaire pour télécharger FFmpeg lors de la première installation.

## Compiler

Le projet utilise C# et WPF sur .NET Framework 4.8 sous Windows x64. Depuis un terminal PowerShell ouvert dans le dossier du projet, exécute `./Build.ps1`. L’exécutable est créé dans `dist/`. Place `ffmpeg.exe` dans ce dossier pour compresser une vidéo.

FFmpeg est un programme séparé fourni par [Gyan](https://www.gyan.dev/ffmpeg/builds/). Les liens vers son code source et les conditions de distribution figurent sur cette page et sur le [site FFmpeg](https://ffmpeg.org/legal.html).
