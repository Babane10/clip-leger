using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ClipLeger
{
    static class Program
    {
        [STAThread]
        private static void Main()
        {
            var application = new Application();
            application.Run(new ClipWindow().Window);
        }
    }

    sealed class ClipWindow
    {
        private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
        private readonly Compressor compressor = new Compressor(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"));
        private readonly Border dropZone;
        private readonly TextBox limit;
        private readonly ProgressBar gauge;
        private readonly Button start;
        private readonly Button cancel;
        private readonly Button[] settings;
        private readonly TextBlock state;
        private readonly TextBlock details;
        private readonly TextBlock percent;
        private readonly WrapPanel resultActions;
        private readonly Brush dropBackground;
        private readonly Brush dropActive = new SolidColorBrush(Color.FromRgb(38, 62, 54));
        private string destination = Path.Combine(Downloads(), "Discord");
        private string result;
        private Video video;
        private CancellationTokenSource operation;
        private bool closeWhenIdle;

        public Window Window { get; private set; }

        public ClipWindow()
        {
            using (Stream markup = typeof(Program).Assembly.GetManifestResourceStream("Clip.xaml"))
                Window = (Window)XamlReader.Load(markup);

            dropZone = Find<Border>("DropZone");
            dropBackground = dropZone.Background;
            limit = Find<TextBox>("Limit");
            gauge = Find<ProgressBar>("Gauge");
            start = Find<Button>("Start");
            cancel = Find<Button>("Cancel");
            state = Find<TextBlock>("State");
            details = Find<TextBlock>("StateDetails");
            percent = Find<TextBlock>("Percent");
            resultActions = Find<WrapPanel>("ResultActions");

            Button selectVideo = Find<Button>("SelectVideo");
            Button selectFolder = Find<Button>("SelectFolder");
            Button free = Find<Button>("Preset20");
            Button basic = Find<Button>("Preset50");
            Button nitro = Find<Button>("Preset1000");
            settings = new[] { selectVideo, selectFolder, free, basic, nitro };

            selectVideo.Click += delegate { ChooseVideo(); };
            selectFolder.Click += delegate { ChooseFolder(); };
            free.Click += delegate { limit.Text = "20"; };
            basic.Click += delegate { limit.Text = "50"; };
            nitro.Click += delegate { limit.Text = "1000"; };
            start.Click += async delegate { await Export(); };
            cancel.Click += delegate { CancelOperation(); };
            Find<Button>("OpenVideo").Click += delegate { OpenResult(false); };
            Find<Button>("OpenFolder").Click += delegate { OpenResult(true); };

            dropZone.DragOver += OnDragOver;
            dropZone.DragLeave += delegate { dropZone.Background = dropBackground; };
            dropZone.Drop += OnDrop;
            Window.Closing += OnClosing;
            UpdateDestination();
        }

        private async void ChooseVideo()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choisir une vidéo",
                Filter = "Vidéos|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v;*.wmv;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.3gp;*.flv|Tous les fichiers|*.*",
                CheckFileExists = true, Multiselect = false
            };
            if (dialog.ShowDialog(Window) == true)
                await LoadVideo(dialog.FileName);
        }

        private void ChooseFolder()
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "Choisis où enregistrer les MP4 compressés.";
                dialog.SelectedPath = Directory.Exists(destination) ? destination : Downloads();
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog(new WindowOwner(Window)) == Forms.DialogResult.OK)
                {
                    destination = dialog.SelectedPath;
                    UpdateDestination();
                }
            }
        }

        private void UpdateDestination()
        {
            TextBlock path = Find<TextBlock>("Destination");
            path.Text = string.Equals(destination, Path.Combine(Downloads(), "Discord"), StringComparison.OrdinalIgnoreCase)
                ? "Téléchargements  ›  Discord" : destination;
            path.ToolTip = destination;
        }

        private void OnDragOver(object sender, DragEventArgs args)
        {
            string[] files = DropFiles(args);
            bool valid = operation == null && files != null && files.Length == 1 && File.Exists(files[0]);
            args.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
            dropZone.Background = valid ? dropActive : dropBackground;
            args.Handled = true;
        }

        private async void OnDrop(object sender, DragEventArgs args)
        {
            dropZone.Background = dropBackground;
            args.Handled = true;
            if (operation != null)
                return;

            string[] files = DropFiles(args);
            if (files == null || files.Length != 1 || !File.Exists(files[0]))
            {
                SetStatus("Dépose un seul fichier vidéo.", "Tu peux aussi utiliser le bouton Choisir une vidéo.", true);
                return;
            }

            await LoadVideo(files[0]);
        }

        private static string[] DropFiles(DragEventArgs args)
        {
            return args.Data.GetData(DataFormats.FileDrop) as string[];
        }

        internal async Task LoadVideo(string path)
        {
            video = null;
            result = null;
            ShowSource(Path.GetFileName(path), "Lecture du fichier…");
            CancellationToken token = BeginOperation("Lecture de la vidéo…", "");
            try
            {
                video = await Task.Run(() => compressor.Inspect(path, token), token);
                string summary = Size(video.Bytes) + "  ·  " + Duration(video.Seconds);
                if (video.Width > 0 && video.Height > 0)
                    summary += "  ·  " + video.Width + " × " + video.Height;
                ShowSource(Path.GetFileName(video.Path), summary);
                SetStatus("Vidéo prête à compresser", "Choisis la taille maximale, puis lance la compression.");
            }
            catch (OperationCanceledException)
            {
                ShowSource("Dépose ta vidéo ici", "MP4, MOV, MKV, WebM…");
                SetStatus("Lecture annulée", "Sélectionne une vidéo pour recommencer.");
            }
            catch (Exception error)
            {
                ShowSource("Dépose ta vidéo ici", "MP4, MOV, MKV, WebM…");
                SetStatus("Impossible d’ouvrir la vidéo", error.Message, true);
            }
            finally { EndOperation(); }
        }

        private void ShowSource(string name, string metadata)
        {
            TextBlock title = Find<TextBlock>("VideoTitle");
            title.Text = name;
            title.ToolTip = name;
            Find<TextBlock>("VideoDetails").Text = metadata;
        }

        internal async Task Export()
        {
            if (operation != null || video == null)
                return;

            decimal megabytes;
            if (!decimal.TryParse(limit.Text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out megabytes)
                || megabytes < 0.1m || megabytes > 1000m)
            {
                SetStatus("Vérifie la taille maximale", "Entre une valeur entre 0,1 et 1 000 Mo.", true);
                limit.Focus();
                limit.SelectAll();
                return;
            }

            long bytes = (long)(megabytes * 1000000m);
            string folder = destination;
            Video selected = video;
            result = null;
            var token = BeginOperation("Préparation…", "Maximum " + megabytes.ToString("0.######", French) + " Mo");
            gauge.Visibility = Visibility.Visible;
            percent.Text = "0 %";
            var progress = new Progress<CompressionProgress>(update =>
            {
                if (operation == null || token.IsCancellationRequested)
                    return;
                state.Text = update.Label;
                percent.Text = update.Percent + " %";
                gauge.Value = update.Percent;
            });

            try
            {
                result = await Task.Run(() => compressor.Export(selected, bytes, folder, progress, token), token);
                SetStatus("MP4 prêt à envoyer", Size(new FileInfo(result).Length) + "  ·  " + Path.GetFileName(result));
                resultActions.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException)
            {
                SetStatus("Compression annulée", "Tu peux relancer la compression quand tu veux.");
            }
            catch (UnauthorizedAccessException)
            {
                SetStatus("Impossible d’enregistrer ici", "Choisis un dossier accessible, par exemple Téléchargements.", true);
            }
            catch (Exception error)
            {
                SetStatus("Compression impossible", error.Message, true);
            }
            finally { EndOperation(); }
        }

        private CancellationToken BeginOperation(string title, string description)
        {
            operation = new CancellationTokenSource();
            SetStatus(title, description);
            cancel.Visibility = Visibility.Visible;
            cancel.IsEnabled = true;
            SetSettingsEnabled(false);
            return operation.Token;
        }

        private void EndOperation()
        {
            operation.Dispose();
            operation = null;
            cancel.Visibility = Visibility.Collapsed;
            SetSettingsEnabled(true);
            if (closeWhenIdle)
                Window.Close();
        }

        private void SetSettingsEnabled(bool value)
        {
            foreach (Button button in settings)
                button.IsEnabled = value;
            limit.IsEnabled = value;
            start.IsEnabled = value && video != null;
        }

        private void CancelOperation()
        {
            if (operation == null)
                return;
            cancel.IsEnabled = false;
            state.Text = "Annulation…";
            operation.Cancel();
        }

        private void OnClosing(object sender, CancelEventArgs args)
        {
            if (operation == null)
                return;
            args.Cancel = true;
            closeWhenIdle = true;
            CancelOperation();
        }

        private void SetStatus(string label, string description, bool error = false)
        {
            state.Text = label;
            state.Foreground = error ? new SolidColorBrush(Color.FromRgb(255, 184, 160)) : Window.Foreground;
            details.Text = description;
            percent.Text = "";
            gauge.Value = 0;
            gauge.Visibility = Visibility.Collapsed;
            resultActions.Visibility = Visibility.Collapsed;
        }

        private void OpenResult(bool inExplorer)
        {
            try
            {
                if (!File.Exists(result))
                    throw new FileNotFoundException("Le MP4 a été déplacé ou supprimé.");
                Process.Start(inExplorer
                    ? new ProcessStartInfo("explorer.exe", "/select,\"" + result + "\"") { UseShellExecute = true }
                    : new ProcessStartInfo(result) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                MessageBox.Show(Window, error.Message, "Impossible d’ouvrir le MP4", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private T Find<T>(string name) where T : class { return (T)Window.FindName(name); }

        private static string Size(long bytes)
        {
            return bytes < 1000000 ? (bytes / 1000.0).ToString("0.#", French) + " Ko"
                : (bytes / 1000000.0).ToString("0.##", French) + " Mo";
        }

        private static string Duration(double seconds)
        {
            var time = TimeSpan.FromSeconds(seconds);
            if (time.TotalHours >= 1)
                return ((int)time.TotalHours) + " h " + time.Minutes.ToString("00") + " min";
            if (time.TotalMinutes >= 1)
                return time.Minutes + " min " + time.Seconds.ToString("00") + " s";
            return seconds.ToString("0.#", French) + " s";
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

        private static string Downloads()
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            IntPtr path;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out path) == 0 && path != IntPtr.Zero)
            {
                try { return Marshal.PtrToStringUni(path); }
                finally { Marshal.FreeCoTaskMem(path); }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        private sealed class WindowOwner : Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }

            public WindowOwner(Window owner)
            {
                Handle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            }
        }
    }
}
