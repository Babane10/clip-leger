# FileFit

FileFit compresse et convertit des fichiers sous Windows x64. L’appli vérifie la taille réelle avant d’exporter un fichier soumis à une limite.

## Installer sur Windows

1. Télécharge `FileFit-v1.1.0-Windows-x64.zip` depuis la [page des versions](https://github.com/Babane10/clip-leger/releases/latest), puis extrais le ZIP.
2. Lance `Installer-FFmpeg.cmd` une fois pour télécharger le moteur et vérifier sa somme SHA-256. Tu peux aussi placer un `ffmpeg.exe` récent à côté de `FileFit.exe`.
3. Ouvre `FileFit.exe`, choisis un mode en haut, puis dépose un fichier.

- **Vidéo** compresse en MP4 sous une taille en Mo choisie.
- **Photo** réduit une image en JPG ou en WebP si elle est transparente. Tu peux choisir 1 Mo, 500 Ko, 200 Ko ou saisir une taille en Ko.
- **Audio** compresse un fichier audio en MP3 sous une taille choisie en Ko. Le débit s’adapte à la durée et à la limite.
- **Vidéo → MP3** extrait la première piste audio d’une vidéo en MP3 à 192 kb/s.

Les exports sont enregistrés dans **Téléchargements → FileFit** par défaut. Ce dossier peut être changé dans l’application. Le traitement se fait sur ton PC et le fichier original est conservé.

## Compiler

Le projet utilise C# et WPF sur .NET Framework 4.8 sous Windows x64. Depuis un terminal PowerShell ouvert dans le dossier du projet, exécute `./Build.ps1`. L’exécutable est créé dans `dist/`. Place `ffmpeg.exe` dans ce dossier pour compresser une vidéo.

FFmpeg est un programme séparé fourni par [Gyan](https://www.gyan.dev/ffmpeg/builds/). Les liens vers son code source et les conditions de distribution figurent sur cette page et sur le [site FFmpeg](https://ffmpeg.org/legal.html).
