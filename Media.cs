using System;
using System.IO;
using System.Threading;

namespace ClipLeger
{
    sealed partial class Compressor
    {
        private static readonly int[] Mp3Rates = { 320, 256, 224, 192, 160, 128, 112, 96, 80, 64, 56, 48, 40, 32, 24, 16, 8 };

        private string ExportPhoto(MediaFile photo, long limit, string folder, string job, IProgress<CompressionProgress> progress, CancellationToken token)
        {
            if (Fits(photo.Path, limit) && (photo.Codec == "mjpeg" || photo.Codec == "webp"))
            {
                string originalFormat = photo.Codec == "mjpeg" ? ".jpg" : ".webp";
                return Publish(photo.Path, folder, photo.Path, "Photo", originalFormat, progress, token);
            }

            string extension = photo.Transparent ? ".webp" : ".jpg";
            string temporary = Path.Combine(job, "image" + extension);
            double scale = Math.Min(1, 12000.0 / Math.Max(1, Math.Max(photo.Width, photo.Height)));
            for (int attempt = 0; attempt < 9; attempt++)
            {
                Report(progress, 5 + attempt * 10, attempt == 0 ? "Compression de la photo…" : "Ajustement de la taille…");
                string factor = scale.ToString("0.############", Invariant);
                string filter = "scale='max(1,round(iw*" + factor + "))':'max(1,round(ih*" + factor + "))':flags=lanczos,setsar=1";
                string encoding = photo.Transparent
                    ? " -c:v libwebp -pix_fmt yuva420p -quality " + (attempt == 0 ? "86" : "75")
                    : " -c:v mjpeg -pix_fmt yuvj420p -q:v " + (attempt == 0 ? "3" : "5") + " -f image2 -update 1";
                Run(Input(photo.Path) + " -loglevel error -xerror -map 0:V:0 -frames:v 1 -an -map_metadata -1 -filter_threads 2 -vf " + Quote(filter) +
                    encoding + " -threads 4 -y " + Quote(temporary), token, null, "Impossible de compresser cette photo.");
                if (Fits(temporary, limit))
                    return Publish(temporary, folder, photo.Path, "Photo", extension, progress, token);

                // Try a lower quality at the same resolution before resizing.
                if (attempt > 0)
                {
                    long actual = new FileInfo(temporary).Length;
                    scale *= Math.Max(0.18, Math.Min(0.90, 0.96 * Math.Pow((double)limit / actual, 0.38)));
                }
            }

            throw new InvalidDataException("La photo dépasse encore la limite. Augmente la taille maximale.");
        }

        private string ExportAudio(MediaFile file, MediaMode mode, long limit, string folder, string job, IProgress<CompressionProgress> progress, CancellationToken token)
        {
            bool extract = mode == MediaMode.Extract;
            string temporary = Path.Combine(job, "audio.mp3");
            string input = ProgressInput(file.Path) + " -map 0:a:0 -vn -sn -dn -map_metadata -1 -map_chapters -1";
            string label = extract ? "Extraction du son…" : "Compression du son…";
            Action<double> onTime = seconds => ReportTime(progress, seconds, file.Seconds, 3, 97, label);

            if (extract)
            {
                EncodeMp3(input, temporary, 192, file.Mono, token, onTime);
                if (Fits(temporary, long.MaxValue))
                    return Publish(temporary, folder, file.Path, "Audio extrait", ".mp3", progress, token);
                throw new InvalidDataException("Aucun son n’a été extrait de cette vidéo.");
            }

            if (file.Codec == "mp3" && Fits(file.Path, limit))
            {
                Run(input + " -c:a copy -y " + Quote(temporary), token, onTime, "Impossible de préparer le MP3.");
                if (Fits(temporary, limit))
                    return Publish(temporary, folder, file.Path, "Audio", ".mp3", progress, token);
            }

            double maximumRate = (limit * 0.985 - 384) * 8 / file.Seconds / 1000;
            double sourceRate = (file.Bytes - 384) * 8 / file.Seconds / 1000;
            maximumRate = Math.Min(maximumRate, Math.Max(8, sourceRate));
            foreach (int rate in Mp3Rates)
            {
                if (rate > maximumRate)
                    continue;
                Report(progress, 3, label);
                EncodeMp3(input, temporary, rate, file.Mono, token, onTime);
                if (Fits(temporary, limit))
                    return Publish(temporary, folder, file.Path, "Audio", ".mp3", progress, token);
            }

            throw new InvalidDataException("La limite est trop basse pour la durée de ce fichier en MP3. Augmente la taille maximale.");
        }

        private void EncodeMp3(string input, string result, int rate, bool mono, CancellationToken token, Action<double> onTime)
        {
            string sampleRate = rate < 64 ? "24000" : "44100";
            string channels = mono || rate < 64 ? "1" : "2";
            Run(input + " -c:a libmp3lame -ar " + sampleRate + " -ac " + channels + " -b:a " + Number(rate) + "k -y " + Quote(result),
                token, onTime, "Impossible de créer le MP3.");
        }
    }
}
