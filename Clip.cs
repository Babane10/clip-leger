using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
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
        private static readonly string DefaultDestination = Path.Combine(Downloads(), "FileFit");
        private const string VideoFilter = "Vidéos|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v;*.wmv;*.ts;*.mts;*.m2ts;*.mpg;*.mpeg;*.3gp;*.flv";
        private readonly Compressor compressor = new Compressor(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe"));
        private readonly Border dropZone;
        private readonly TextBox limit;
        private readonly ProgressBar gauge;
        private readonly Button start;
        private readonly Button cancel;
        private readonly Button selectFile;
        private readonly Button selectFolder;
        private readonly Button[] presets;
        private readonly RadioButton[] modes;
        private readonly TextBlock state;
        private readonly TextBlock details;
        private readonly TextBlock percent;
        private readonly WrapPanel resultActions;
        private readonly Brush dropBackground;
        private readonly Brush dropActive = new SolidColorBrush(Color.FromRgb(38, 62, 54));
        private string destination = DefaultDestination;
        private string result;
        private MediaFile media;
        private MediaMode mode;
        private string emptyTitle;
        private string formats;
        private string filter;
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

            selectFile = Find<Button>("SelectFile");
            selectFolder = Find<Button>("SelectFolder");
            presets = new[] { Find<Button>("PresetFirst"), Find<Button>("PresetSecond"), Find<Button>("PresetThird") };
            modes = new[] { Find<RadioButton>("ModeVideo"), Find<RadioButton>("ModePhoto"), Find<RadioButton>("ModeAudio"), Find<RadioButton>("ModeExtract") };

            selectFile.Click += async delegate { await ChooseFile(); };
            selectFolder.Click += delegate { ChooseFolder(); };
            foreach (Button preset in presets)
                preset.Click += delegate(object sender, RoutedEventArgs args) { limit.Text = (string)((Button)sender).Tag; };
            foreach (RadioButton tab in modes)
                tab.Checked += OnModeChanged;
            start.Click += async delegate { await Export(); };
            cancel.Click += delegate { CancelOperation(); };
            Find<Button>("OpenFile").Click += delegate { OpenResult(false); };
            Find<Button>("OpenFolder").Click += delegate { OpenResult(true); };

            dropZone.DragOver += OnDragOver;
            dropZone.DragLeave += delegate { dropZone.Background = dropBackground; };
            dropZone.Drop += OnDrop;
            Window.Closing += OnClosing;
            UpdateDestination();
            ConfigureMode();
        }

        private async void OnModeChanged(object sender, RoutedEventArgs args)
        {
            var next = (MediaMode)Array.IndexOf(modes, sender);
            if (operation != null || mode == next)
                return;

            bool switchVideoMode = (mode == MediaMode.Video && next == MediaMode.Extract) ||
                (mode == MediaMode.Extract && next == MediaMode.Video);
            string selected = switchVideoMode && media != null ? media.Path : null;
            mode = next;
            media = null;
            result = null;
            ConfigureMode();
            if (selected != null)
                await LoadMedia(selected);
        }

        private void ConfigureMode()
        {
            bool extracting = mode == MediaMode.Extract;
            bool smallUnits = mode == MediaMode.Photo || mode == MediaMode.Audio;
            string hint;
            if (mode == MediaMode.Photo)
            {
                emptyTitle = "Dépose ta photo ici";
                formats = "JPG, PNG, WebP, BMP, TIFF…";
                filter = "Photos|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.tif;*.tiff";
                hint = "1 Mo = 1 000 Ko. Le résultat est en JPG ou en WebP si l’image est transparente.";
                start.Content = "_Compresser la photo";
            }
            else if (mode == MediaMode.Audio)
            {
                emptyTitle = "Dépose ton fichier audio ici";
                formats = "MP3, WAV, M4A, FLAC, OGG…";
                filter = "Fichiers audio|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma;*.aif;*.aiff";
                hint = "Le débit du MP3 s’adapte à la taille maximale.";
                start.Content = "_Compresser en MP3";
            }
            else
            {
                emptyTitle = "Dépose ta vidéo ici";
                formats = "MP4, MOV, MKV, WebM…";
                filter = VideoFilter;
                hint = extracting ? "La première piste audio est exportée en MP3 à 192 kb/s." : "Le MP4 garde la durée de la vidéo.";
                start.Content = extracting ? "_Extraire le MP3" : "_Compresser en MP4";
            }

            Find<WrapPanel>("SizeControls").Visibility = extracting ? Visibility.Collapsed : Visibility.Visible;
            Find<TextBlock>("OptionsTitle").Text = extracting ? "Format de sortie" : "Taille maximale";
            Find<TextBlock>("OptionsHint").Text = hint;
            Find<TextBlock>("LimitUnit").Text = smallUnits ? "Ko" : "Mo";
            AutomationProperties.SetName(limit, smallUnits ? "Taille maximale en kilooctets" : "Taille maximale en mégaoctets");
            string[] values = smallUnits ? new[] { "1000", "500", "200" } : new[] { "20", "50", "1000" };
            string[] labels = smallUnits ? new[] { "1 Mo", "500 Ko", "200 Ko" } : new[] { "20 Mo", "50 Mo", "1 Go" };
            for (int index = 0; index < presets.Length; index++)
            {
                presets[index].Content = labels[index];
                presets[index].Tag = values[index];
            }
            limit.Text = values[0];
            ShowSource(emptyTitle, formats);
            SetStatus("Sélectionne un fichier pour commencer", "Le résultat sera enregistré sur ton PC.");
            SetSettingsEnabled(true);
        }

        private async Task ChooseFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choisir un fichier",
                Filter = filter + "|Tous les fichiers|*.*",
                CheckFileExists = true, Multiselect = false
            };
            if (dialog.ShowDialog(Window) == true)
                await LoadMedia(dialog.FileName);
        }

        private void ChooseFolder()
        {
            using (var dialog = new Forms.FolderBrowserDialog())
            {
                dialog.Description = "Choisis où enregistrer les fichiers exportés.";
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
            path.Text = string.Equals(destination, DefaultDestination, StringComparison.OrdinalIgnoreCase)
                ? "Téléchargements  ›  FileFit" : destination;
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
                SetStatus("Dépose un seul fichier.", "Tu peux aussi utiliser le bouton Choisir un fichier.", true);
                return;
            }

            await LoadMedia(files[0]);
        }

        private static string[] DropFiles(DragEventArgs args)
        {
            return args.Data.GetData(DataFormats.FileDrop) as string[];
        }

        internal async Task LoadMedia(string path)
        {
            media = null;
            result = null;
            ShowSource(Path.GetFileName(path), "Lecture du fichier…");
            MediaMode selected = mode;
            CancellationToken token = BeginOperation("Lecture du fichier…", "");
            try
            {
                media = await Task.Run(() => compressor.Inspect(path, selected, token), token);
                string summary = Size(media.Bytes);
                if (selected != MediaMode.Photo)
                    summary += "  ·  " + Duration(media.Seconds);
                if (selected != MediaMode.Audio && media.Width > 0 && media.Height > 0)
                    summary += "  ·  " + media.Width + " × " + media.Height;
                ShowSource(Path.GetFileName(media.Path), summary);
                SetStatus("Fichier prêt", selected == MediaMode.Extract ? "Lance l’extraction du MP3." : "Choisis la taille maximale, puis lance la compression.");
            }
            catch (OperationCanceledException)
            {
                ShowSource(emptyTitle, formats);
                SetStatus("Lecture annulée", "Sélectionne un fichier pour recommencer.");
            }
            catch (Exception error)
            {
                ShowSource(emptyTitle, formats);
                SetStatus("Impossible d’ouvrir ce fichier", error.Message, true);
            }
            finally { EndOperation(); }
        }

        private void ShowSource(string name, string metadata)
        {
            TextBlock title = Find<TextBlock>("SourceTitle");
            title.Text = name;
            title.ToolTip = name;
            Find<TextBlock>("SourceDetails").Text = metadata;
        }

        internal async Task Export()
        {
            if (operation != null || media == null)
                return;

            bool extracting = mode == MediaMode.Extract;
            bool smallUnits = mode == MediaMode.Photo || mode == MediaMode.Audio;
            long bytes = 0;
            if (!extracting && !ReadLimit(smallUnits, out bytes))
            {
                string range = smallUnits ? "1 et 1 000 000 Ko." : "0,1 et 1 000 Mo.";
                SetStatus("Vérifie la taille maximale", "Entre une valeur entre " + range, true);
                limit.Focus();
                limit.SelectAll();
                return;
            }

            string folder = destination;
            MediaFile selected = media;
            MediaMode selectedMode = mode;
            result = null;
            var token = BeginOperation("Préparation…", extracting ? "Export en MP3" : "Maximum " + Size(bytes));
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
                result = await Task.Run(() => compressor.Export(selected, selectedMode, bytes, folder, progress, token), token);
                string format = Path.GetExtension(result).Substring(1).ToUpperInvariant();
                SetStatus(format + " prêt", Size(new FileInfo(result).Length) + "  ·  " + Path.GetFileName(result));
                resultActions.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException)
            {
                SetStatus("Export annulé", "Tu peux relancer l’export quand tu veux.");
            }
            catch (UnauthorizedAccessException)
            {
                SetStatus("Impossible d’enregistrer ici", "Choisis un dossier accessible, par exemple Téléchargements.", true);
            }
            catch (Exception error)
            {
                SetStatus("Export impossible", error.Message, true);
            }
            finally { EndOperation(); }
        }

        private bool ReadLimit(bool smallUnits, out long bytes)
        {
            bytes = 0;
            decimal amount;
            if (!decimal.TryParse(limit.Text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
                return false;

            decimal minimum = smallUnits ? 1m : 0.1m;
            decimal maximum = smallUnits ? 1000000m : 1000m;
            if (amount < minimum || amount > maximum)
                return false;

            bytes = (long)(amount * (smallUnits ? 1000m : 1000000m));
            return true;
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
            selectFile.IsEnabled = value;
            selectFolder.IsEnabled = value;
            foreach (Button preset in presets)
                preset.IsEnabled = value;
            foreach (RadioButton tab in modes)
                tab.IsEnabled = value;
            limit.IsEnabled = value;
            start.IsEnabled = value && media != null;
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
                    throw new FileNotFoundException("Le fichier a été déplacé ou supprimé.");
                Process.Start(inExplorer
                    ? new ProcessStartInfo("explorer.exe", "/select,\"" + result + "\"") { UseShellExecute = true }
                    : new ProcessStartInfo(result) { UseShellExecute = true });
            }
            catch (Exception error)
            {
                MessageBox.Show(Window, error.Message, "Impossible d’ouvrir le fichier", MessageBoxButton.OK, MessageBoxImage.Warning);
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
