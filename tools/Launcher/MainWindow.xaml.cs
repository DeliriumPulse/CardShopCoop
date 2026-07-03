using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CardShopCoopLauncher
{
    public partial class MainWindow : Window
    {
        private string _gamePath;
        private string _dllPath;
        private bool _ready;

        private static readonly Brush DotBlue = new SolidColorBrush(Color.FromRgb(0x54, 0xa0, 0xff));
        private static readonly Brush DotGreen = new SolidColorBrush(Color.FromRgb(0x2e, 0xc4, 0x84));
        private static readonly Brush DotYellow = new SolidColorBrush(Color.FromRgb(0xff, 0xd5, 0x4a));
        private static readonly Brush DotRed = new SolidColorBrush(Color.FromRgb(0xe8, 0x5d, 0x5d));

        public MainWindow()
        {
            InitializeComponent();
            LauncherVer.Text = "launcher " + typeof(MainWindow).Assembly.GetName().Version.ToString(2);
            Loaded += async (_, __) => await RunFlow();
        }

        // ---------------- the flow ----------------

        private async Task RunFlow()
        {
            try
            {
                Status(DotBlue, "finding your game…", busy: true);
                _gamePath = Updater.AutoLocateGame();
                if (_gamePath == null)
                {
                    Status(DotYellow, "couldn't find the game - click \"change game folder\" below", busy: false);
                    return;
                }
                string pluginDir = Path.Combine(_gamePath, "BepInEx", "plugins");
                _dllPath = Path.Combine(pluginDir, "CardShopCoop.dll");
                if (!Directory.Exists(pluginDir))
                {
                    Status(DotRed, "BepInEx isn't installed yet - install BepInEx 5 (x64), run the game once, then run this again", busy: false);
                    return;
                }

                var installed = Updater.InstalledVersion(_dllPath);
                InstalledChip.Text = installed != null ? "installed: v" + installed : "installed: none";

                Status(DotBlue, "checking for updates…", busy: true);
                var latest = await Updater.FetchLatestRelease();
                LatestChip.Text = "latest: " + latest.Tag;
                NotesTitle.Text = "what's new in " + latest.Tag;
                NotesText.Text = string.IsNullOrWhiteSpace(latest.Notes)
                    ? "(no release notes)" : latest.Notes.Trim();

                if (installed == null || latest.Ver > installed)
                {
                    while (Updater.GameIsRunning())
                    {
                        Status(DotYellow, "close the game so the mod can be updated - waiting…", busy: true);
                        await Task.Delay(2500);
                    }
                    Status(DotBlue, installed == null
                        ? "installing CardShopCoop " + latest.Tag + "…"
                        : "updating v" + installed + " → " + latest.Tag + "…", busy: true);
                    Progress.IsIndeterminate = false;
                    var prog = new Progress<double>(p => Progress.Value = p);
                    await Updater.InstallDll(latest.ZipUrl, _dllPath, prog);
                    InstalledChip.Text = "installed: " + latest.Tag;
                    Status(DotGreen, "up to date - ready to play", busy: false);
                }
                else
                {
                    Status(DotGreen, "up to date - ready to play", busy: false);
                }

                _ready = true;
                PlayBtn.IsEnabled = true;
            }
            catch (UnauthorizedAccessException)
            {
                Status(DotRed, "no permission to write to the game folder - try running the launcher as administrator once", busy: false);
            }
            catch (Exception e)
            {
                Status(DotRed, "update check failed: " + e.Message + " - you can still play your installed version", busy: false);
                if (File.Exists(_dllPath ?? "")) { _ready = true; PlayBtn.IsEnabled = true; }
            }
        }

        private void Status(Brush dot, string text, bool busy)
        {
            StatusDot.Fill = dot;
            StatusText.Text = text;
            Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy) { Progress.IsIndeterminate = true; }
        }

        // ---------------- interactions ----------------

        private void OnPlay(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            Updater.LaunchGame();
            Status(DotGreen, "launching through Steam - see you in the shop!", busy: false);
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            t.Tick += (_, __) => Close();
            t.Start();
        }

        private void OnChangeFolder(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Pick the game executable (Card Shop Simulator.exe)",
                Filter = "Card Shop Simulator|Card Shop Simulator.exe|Programs|*.exe",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() == true)
            {
                Updater.SaveGamePath(Path.GetDirectoryName(dlg.FileName));
                _ready = false;
                PlayBtn.IsEnabled = false;
                _ = RunFlow();
            }
        }

        private void OnOpenGitHub(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo("https://github.com/" + Updater.Repo) { UseShellExecute = true });

        private void OnDragWindow(object sender, MouseButtonEventArgs e) { try { DragMove(); } catch { } }
        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void OnClose(object sender, RoutedEventArgs e) => Close();
    }
}
