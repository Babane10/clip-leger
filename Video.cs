using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClipLeger
{
    sealed class Video
    {
        public string Path;
        public long Bytes;
        public double Seconds;
        public double FramesPerSecond;
        public int Width;
        public int Height;
        public bool HasAudio;
        public bool Mp4Compatible;
    }

    sealed class CompressionProgress
    {
        public readonly int Percent;
        public readonly string Label;

        public CompressionProgress(int percent, string label)
        {
            Percent = percent;
            Label = label;
        }
    }

    sealed class Compressor
    {
        private const int MinimumVideoRate = 30;
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        private readonly string engine;

        public Compressor(string enginePath)
        {
            engine = enginePath;
        }

        public Video Inspect(string path, CancellationToken token)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
                throw new InvalidDataException("Le fichier est introuvable ou vide.");

            string input = Input(path);
            string header = Run(input + " -map 0:V:0 -c copy -frames:v 1 -f null NUL", token, null, "Impossible de lire cette vidéo.");
            int end = header.IndexOf("Stream mapping:", StringComparison.Ordinal);
            if (end >= 0)
                header = header.Substring(0, end);

            string[] streams = header.Split('\n').Where(line => line.TrimStart().StartsWith("Stream #0:", StringComparison.Ordinal)).ToArray();
            string picture = streams.FirstOrDefault(line => line.Contains("Video:") && !line.Contains("(attached pic)"));
            if (picture == null)
                throw new InvalidDataException("Ce fichier ne contient pas de vidéo lisible.");

            string audio = streams.FirstOrDefault(line => line.Contains("Audio:"));
            var video = new Video { Path = file.FullName, Bytes = file.Length, HasAudio = audio != null };
            Match clock = Regex.Match(header, @"Duration:\s*(\d+):([0-5]\d):([0-5]\d(?:\.\d+)?)");
            if (clock.Success)
                video.Seconds = Parse(clock.Groups[1].Value) * 3600 + Parse(clock.Groups[2].Value) * 60 + Parse(clock.Groups[3].Value);

            Match size = Regex.Match(picture, @",\s*(\d{1,5})x(\d{1,5})(?=\s|\[|,)");
            if (size.Success)
            {
                video.Width = int.Parse(size.Groups[1].Value, Invariant);
                video.Height = int.Parse(size.Groups[2].Value, Invariant);
            }

            Match frames = Regex.Match(picture, @"([\d.]+) fps");
            if (frames.Success)
                video.FramesPerSecond = Parse(frames.Groups[1].Value);

            video.Mp4Compatible = Regex.IsMatch(picture, @"Video:\s*h264\b") &&
                Regex.IsMatch(picture, @"[,\s]yuv(?:j)?420p(?:[,\s(]|$)") &&
                (audio == null || Regex.IsMatch(audio, @"Audio:\s*aac\b"));

            if (video.Seconds <= 0)
            {
                double elapsed = 0;
                Run(input + " -loglevel error -progress pipe:1 -nostats -map 0:V:0 -map 0:a:0? -c copy -f null NUL", token,
                    seconds => elapsed = Math.Max(elapsed, seconds), "Impossible de calculer la durée de cette vidéo.");
                video.Seconds = elapsed;
            }

            if (video.Seconds <= 0 || double.IsInfinity(video.Seconds))
                throw new InvalidDataException("Impossible de calculer la durée de cette vidéo.");

            return video;
        }

        public string Export(Video video, long limit, string folder, IProgress<CompressionProgress> progress, CancellationToken token)
        {
            if (!File.Exists(video.Path))
                throw new FileNotFoundException("La vidéo d’origine a été déplacée ou supprimée.");

            Directory.CreateDirectory(folder);
            string job = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ClipLeger-" + Guid.NewGuid().ToString("N"))).FullName;
            string temporary = Path.Combine(job, "clip.mp4");
            string stats = Quote(Path.Combine(job, "analyse"));
            string input = Input(video.Path) + " -loglevel warning -progress pipe:1 -stats_period 0.25 -nostats";

            try
            {
                if (video.Mp4Compatible && new FileInfo(video.Path).Length < limit)
                {
                    Report(progress, 2, "Préparation du MP4…");
                    Run(input + " -map 0:V:0 -map 0:a:0? -c copy -movflags +faststart -y " + Quote(temporary), token,
                        seconds => ReportTime(progress, seconds, video.Seconds, 2, 95, "Préparation du MP4…"),
                        "Impossible de préparer ce MP4.");
                    if (Fits(temporary, limit))
                        return Publish(temporary, folder, video.Path, progress, token);
                }

                // Keep room for container overhead and allow the file-size check to catch rate-control variation.
                long budget = Math.Min(limit, Math.Max(video.Bytes, 1000000));
                double total = (budget * 0.975 - Math.Max(12000, video.Seconds * 550)) * 8 / video.Seconds / 1000;
                if (total < MinimumVideoRate + (video.HasAudio ? 24 : 0))
                    throw new InvalidDataException("La limite est trop basse pour cette durée. Choisis une taille plus grande.");

                int audioRate = video.HasAudio ? (int)Math.Max(24, Math.Min(128, total * 0.17)) : 0;
                int videoRate = (int)Math.Min(40000, Math.Floor(total - audioRate));

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    string first = attempt == 0 ? "Analyse de la vidéo…" : "Ajustement à la taille maximale…";
                    string second = "Compression de la vidéo…";
                    Report(progress, 0, first);
                    string encoding = BuildEncoding(video, videoRate);
                    Run(input + encoding + " -an -pass 1 -passlogfile " + stats + " -f null NUL", token,
                        seconds => ReportTime(progress, seconds, video.Seconds, 0, 40, first), "L’analyse de la vidéo a échoué.");

                    Report(progress, 40, second);
                    string audio = video.HasAudio ? " -map 0:a:0? -c:a aac -ac " + (audioRate < 48 ? "1" : "2") +
                        " -b:a " + Number(audioRate) + "k" : " -an";
                    Run(input + encoding + audio + " -pass 2 -passlogfile " + stats + " -movflags +faststart -y " + Quote(temporary), token,
                        seconds => ReportTime(progress, seconds, video.Seconds, 40, 97, second), "La compression a échoué.");

                    if (Fits(temporary, limit))
                        return Publish(temporary, folder, video.Path, progress, token);

                    double correction = limit * 0.90 / new FileInfo(temporary).Length;
                    videoRate = (int)Math.Floor(videoRate * correction);
                    audioRate = video.HasAudio ? Math.Max(24, (int)Math.Floor(audioRate * correction)) : 0;
                    if (videoRate < MinimumVideoRate)
                        break;
                }

                throw new InvalidDataException("Cette vidéo dépasse encore la limite. Essaie une taille maximale plus grande.");
            }
            finally
            {
                foreach (string file in Directory.GetFiles(job))
                    TryDelete(file);
                try { Directory.Delete(job); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string BuildEncoding(Video video, int rate)
        {
            int shortSide;
            int fps;
            if (rate >= 5500) { shortSide = 1080; fps = 60; }
            else if (rate >= 2600) { shortSide = 1080; fps = 30; }
            else if (rate >= 1200) { shortSide = 720; fps = 30; }
            else if (rate >= 600) { shortSide = 540; fps = 30; }
            else if (rate >= 320) { shortSide = 480; fps = 24; }
            else if (rate >= 165) { shortSide = 360; fps = 24; }
            else if (rate >= 80) { shortSide = 270; fps = 20; }
            else { shortSide = 180; fps = 15; }

            string shortEdge = Number(shortSide);
            string longEdge = Number((int)Math.Ceiling(shortSide * 16.0 / 9));
            string filter = "scale=w='max(2,min(iw,if(gte(iw,ih)," + longEdge + "," + shortEdge + ")))':" +
                "h='max(2,min(ih,if(gte(iw,ih)," + shortEdge + "," + longEdge + ")))':" +
                "force_original_aspect_ratio=decrease:force_divisible_by=2:reset_sar=1:flags=bicubic";
            if (video.FramesPerSecond > fps || video.FramesPerSecond <= 0)
                filter += ",fps=fps=" + Number(fps) + ":eof_action=pass";

            return " -map 0:V:0 -sn -dn -map_metadata -1 -map_chapters -1 -filter_threads 2 -vf " + Quote(filter) +
                " -c:v libx264 -preset veryfast -threads 4 -pix_fmt yuv420p -b:v " + Number(rate) + "k";
        }

        private static bool Fits(string path, long limit)
        {
            long bytes = new FileInfo(path).Length;
            return bytes > 0 && bytes < limit;
        }

        private static string Publish(string temporary, string folder, string source, IProgress<CompressionProgress> progress, CancellationToken token)
        {
            Report(progress, 98, "Enregistrement du MP4…");
            string draft = Path.Combine(folder, ".clip-" + Guid.NewGuid().ToString("N") + ".part");
            string stem = Path.GetFileNameWithoutExtension(source);
            if (stem.Length > 90)
                stem = stem.Substring(0, 90).TrimEnd();

            try
            {
                using (var from = File.OpenRead(temporary))
                using (var to = new FileStream(draft, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[1024 * 1024];
                    int read;
                    while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        to.Write(buffer, 0, read);
                    }
                }

                for (int index = 1; ; index++)
                {
                    token.ThrowIfCancellationRequested();
                    string suffix = index == 1 ? "" : " (" + Number(index) + ")";
                    string destination = Path.Combine(folder, stem + " - Discord" + suffix + ".mp4");
                    try
                    {
                        File.Move(draft, destination);
                        return destination;
                    }
                    catch (IOException)
                    {
                        if (!File.Exists(destination) && !Directory.Exists(destination))
                            throw;
                    }
                }
            }
            finally { TryDelete(draft); }
        }

        private string Run(string arguments, CancellationToken token, Action<double> onTime, string error)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(engine))
                throw new FileNotFoundException("Place ffmpeg.exe dans le dossier de l’application. Si tu as téléchargé le ZIP, lance Installer-FFmpeg.cmd.");

            var info = new ProcessStartInfo(engine, "-hide_banner -nostdin " + arguments)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                RedirectStandardOutput = true, StandardErrorEncoding = Encoding.UTF8
            };

            using (var process = new Process { StartInfo = info })
            {
                var diagnostics = new StringBuilder();
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    if (args.Data != null && diagnostics.Length < 32768)
                        diagnostics.AppendLine(args.Data);
                };
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
                {
                    long microseconds;
                    if (onTime != null && args.Data != null && args.Data.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                        long.TryParse(args.Data.Substring(12), NumberStyles.Integer, Invariant, out microseconds))
                        onTime(microseconds / 1000000.0);
                };

                process.Start();
                using (token.Register(() => Stop(process)))
                {
                    try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }

                    process.BeginErrorReadLine();
                    process.BeginOutputReadLine();
                    process.WaitForExit();
                    token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0)
                        throw new InvalidDataException(error + Environment.NewLine + Tail(diagnostics.ToString()));
                    return diagnostics.ToString();
                }
            }
        }

        private static string Input(string path)
        {
            return " -protocol_whitelist file -threads 4 -i " + Quote(path);
        }

        private static string Quote(string argument)
        {
            // Callers provide an existing filename or a filter, neither with a trailing backslash.
            return "\"" + argument.Replace("\"", "\\\"") + "\"";
        }

        private static string Tail(string text)
        {
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string last = string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Length - 2)));
            return last.Length > 350 ? last.Substring(last.Length - 350) : last;
        }

        private static void Stop(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static double Parse(string value) { return double.Parse(value, Invariant); }
        private static string Number(int value) { return value.ToString(Invariant); }

        private static void Report(IProgress<CompressionProgress> progress, int percent, string label)
        {
            if (progress != null)
                progress.Report(new CompressionProgress(percent, label));
        }

        private static void ReportTime(IProgress<CompressionProgress> progress, double time, double duration, int start, int end, string label)
        {
            int percent = start + (int)((end - start) * Math.Max(0, Math.Min(1, time / duration)));
            Report(progress, percent, label);
        }
    }
}
