using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Octokit;
using SharpCompress.Archives;
using BadBuilder.Configuration;

namespace BadBuilder;

public partial class MainWindow : Window
{
    private int _currentStep = 1;
    private bool _isFinished = false;
    private readonly BuilderConfig _config = new(ArtifactCatalog.Homebrew);

    private readonly HttpClient _http = new HttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private record DriveItem(DiskInfo Disk, string Display);
    private record OptionItem<T>(T Value, string Display);

    public MainWindow()
    {
        InitializeComponent();

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

        LoadExploits();
        LoadBootstraps();
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnMinimizeClicked(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnCreditsClicked(object sender, RoutedEventArgs e)
    {
        var credits = new CreditsWindow { Owner = this };
        credits.ShowDialog();
    }

    private void OnTosCheckChanged(object sender, RoutedEventArgs e)
    {
        NextButton.IsEnabled = TosCheckBox.IsChecked == true;
    }

    private void OnDriveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveComboBox.SelectedValue is DiskInfo disk)
        {
            _config.TargetDisk = disk;
            DriveWarning.Visibility = Visibility.Visible;
            NextButton.IsEnabled = true;
        }
    }

    private void LoadDrives()
    {
        DriveComboBox.Items.Clear();
        try
        {
            var drives = Services.Disks.DiskService.EnumerateDisks()
                .Where(d => d.Type == DiskDriveType.Removable || d.Size < 128L * 1024 * 1024 * 1024)
                .ToList();

            if (drives.Count == 0)
            {
                MessageBox.Show("No suitable drives found. Make sure a USB drive is connected.", "No Drives",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            foreach (var d in drives)
            {
                double gb = d.Size / 1024d / 1024d / 1024d;
                DriveComboBox.Items.Add(new DriveItem(d, $"{d.Name}  ({gb:0.00} GB)  [{d.Type}]"));
            }
            DriveComboBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not enumerate drives:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void LoadExploits()
    {
        ExploitComboBox.Items.Clear();
        foreach (var kv in ArtifactCatalog.Exploits)
        {
            ExploitComboBox.Items.Add(new OptionItem<ExploitOption>(kv.Key, $"{kv.Value.DisplayName} — {kv.Value.Description}"));
        }
        ExploitComboBox.SelectedIndex = 2;
    }

    private void LoadBootstraps()
    {
        BootstrapComboBox.Items.Clear();
        foreach (var kv in ArtifactCatalog.Bootstraps)
        {
            BootstrapComboBox.Items.Add(new OptionItem<BootstrapOption>(kv.Key, $"{kv.Value.DisplayName} — {kv.Value.Description}"));
        }
        BootstrapComboBox.SelectedIndex = 0;
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (_currentStep <= 1) return;
        ShowStep(_currentStep - 1);
    }

    private async void OnNextClicked(object sender, RoutedEventArgs e)
    {
        if (_isFinished)
        {
            Close();
            return;
        }

        switch (_currentStep)
        {
            case 1:
                ShowStep(2);
                LoadDrives();
                NextButton.IsEnabled = DriveComboBox.SelectedItem != null;
                break;

            case 2:
                if (_config.TargetDisk is null)
                {
                    MessageBox.Show("Please select a target drive.", "Missing Drive", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                ShowStep(3);
                NextButton.IsEnabled = true;
                break;

            case 3:
                if (ExploitComboBox.SelectedValue is ExploitOption exp)
                    _config.SelectedExploit = exp;
                ShowStep(4);
                break;

            case 4:
                if (BootstrapComboBox.SelectedValue is BootstrapOption boot)
                    _config.SelectedBootstrap = boot;
                ShowStep(5);
                break;

            case 5:
                _config.FirmwareUpdateEnabled = FirmwareUpdateCheckBox.IsChecked == true;

                _config.Homebrew.Clear();
                bool auroraHomebrewEnabled = (FindName("HomebrewAuroraCheckBox") as CheckBox)?.IsChecked == true;
                bool xexMenuEnabled = (FindName("HomebrewXeXMenuCheckBox") as CheckBox)?.IsChecked == true;
                bool nandFlasherEnabled = (FindName("HomebrewNandFlasherCheckBox") as CheckBox)?.IsChecked == true;
                bool xm360Enabled = (FindName("HomebrewXM360CheckBox") as CheckBox)?.IsChecked == true;

                foreach (var entry in ArtifactCatalog.Homebrew)
                {
                    bool include = entry.Artifact.ID switch
                    {
                        "homebrew-aurora" => auroraHomebrewEnabled,
                        "homebrew-xexmenu" => xexMenuEnabled,
                        "homebrew-simple360nandflasher" => nandFlasherEnabled,
                        "homebrew-xm360" => xm360Enabled,
                        _ => false
                    };
                    if (include)
                        _config.Homebrew.Add(entry);
                }

                ShowStep(6);
                NextButton.IsEnabled = false;
                CancelButton.Visibility = Visibility.Collapsed;
                await RunInstallAsync();
                break;
        }
    }

    private void ShowStep(int step)
    {
        Step1_Agreement.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2_Drive.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3_Exploit.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4_Bootstrap.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;
        Step5_Options.Visibility = step == 5 ? Visibility.Visible : Visibility.Collapsed;
        Step6_Progress.Visibility = step == 6 ? Visibility.Visible : Visibility.Collapsed;

        _currentStep = step;

        BackButton.Visibility = step > 1 && step < 6 ? Visibility.Visible : Visibility.Collapsed;

        NextButton.Content = step switch
        {
            5 => "Build USB",
            6 => "Finish",
            _ => "Next"
        };
    }

    private async Task RunInstallAsync()
    {
        try
        {
            StatusTitle.Text = "Formatting drive...";
            StatusSubtext.Text = "This may take a moment";
            MainProgressBar.Value = 5;

            if (OperatingSystem.IsWindows())
            {
                _config.MountPoint = Services.Disks.DiskService.FormatFAT32(_config.TargetDisk!);
            }
            else
            {
                MessageBox.Show("Automatic formatting is only supported on Windows.\nPlease format the drive as FAT32 manually.",
                    "Manual Format Required", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(_config.MountPoint))
                throw new InvalidOperationException("Drive was not remounted after format.");

            StatusTitle.Text = "Downloading artifacts...";
            StatusSubtext.Text = "Resolving releases";
            MainProgressBar.Value = 10;

            var artifacts = ArtifactCatalog.GetSelectedArtifacts(_config);

            string workRoot = Path.Combine(Path.GetTempPath(), "BadBuilder", Guid.NewGuid().ToString("N"));
            string downloadRoot = Path.Combine(workRoot, "Downloads");
            string stagingRoot = Path.Combine(workRoot, "Staging");
            Directory.CreateDirectory(downloadRoot);
            Directory.CreateDirectory(stagingRoot);

            var github = new GitHubClient(new ProductHeaderValue("BadBuilder"));
            int total = Math.Max(artifacts.Count, 1);
            int done = 0;

            var downloaded = new List<(ArtifactDefinition Artifact, string ArchivePath)>();

            foreach (var art in artifacts)
            {
                StatusSubtext.Text = $"Downloading {art.DisplayName}...";
                string archivePath = await DownloadArtifactAsync(art, downloadRoot, github);
                downloaded.Add((art, archivePath));
                done++;
                MainProgressBar.Value = 10 + (done * 40.0 / total);
            }

            StatusTitle.Text = "Extracting files...";
            MainProgressBar.Value = 55;

            var staged = new List<(ArtifactDefinition Artifact, string StagingPath)>();
            done = 0;

            foreach (var (art, archivePath) in downloaded)
            {
                StatusSubtext.Text = $"Extracting {art.DisplayName}...";
                string extractPath = Path.Combine(stagingRoot, art.ID);
                ExtractArchive(archivePath, extractPath);
                staged.Add((art, extractPath));
                done++;
                MainProgressBar.Value = 55 + (done * 20.0 / total);
            }

            StatusTitle.Text = "Installing files...";
            StatusSubtext.Text = "Copying to USB";
            MainProgressBar.Value = 78;

            foreach (var (art, stagingPath) in staged)
            {
                StatusSubtext.Text = $"Installing {art.DisplayName}...";
                await ApplyOperationsAsync(art, stagingPath, _config.MountPoint!);
            }

            await File.WriteAllTextAsync(Path.Combine(_config.MountPoint!, "name.txt"), "USB Storage Device");
            await File.WriteAllTextAsync(Path.Combine(_config.MountPoint!, "info.txt"),
                "This drive was created with AVeryBadBuilder\n\n" + _config.ToString());

            MainProgressBar.Value = 100;
            StatusTitle.Text = "USB Ready!";
            StatusSubtext.Text = $"Drive prepared at {_config.MountPoint}";

            if (FindResource("SuccessAnimationStoryboard") is Storyboard sb)
                sb.Begin();

            _isFinished = true;
            NextButton.Content = "Finish";
            NextButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            string message = ex.Message;
            if (ex.InnerException != null)
                message += "\n\n" + ex.InnerException.Message;

            MessageBox.Show($"Install failed:\n\n{message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);

            ShowStep(5);
            NextButton.IsEnabled = true;
            BackButton.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Visible;
        }
    }

    private async Task<string> DownloadArtifactAsync(ArtifactDefinition art, string downloadRoot, GitHubClient github)
    {
        string artifactDir = Path.Combine(downloadRoot, art.ID);
        Directory.CreateDirectory(artifactDir);

        if (art.LocalArchivePath is not null && File.Exists(art.LocalArchivePath))
            return art.LocalArchivePath;

        if (art.Source is null)
            throw new InvalidOperationException($"No source configured for {art.DisplayName}.");

        string url;
        string fileName;

        if (art.Source is DirectSource direct)
        {
            url = direct.URL;
            fileName = Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = art.ID + ".zip";
        }
        else if (art.Source is GitHubReleaseSource gh)
        {
            Release release;

            if (!string.IsNullOrWhiteSpace(gh.ReleaseTag))
                release = await github.Repository.Release.Get(gh.Owner, gh.Repo, gh.ReleaseTag);
            else
            {
                try
                {
                    release = await github.Repository.Release.GetLatest(gh.Owner, gh.Repo);
                }
                catch
                {
                    var all = await github.Repository.Release.GetAll(gh.Owner, gh.Repo);
                    if (all.Count == 0)
                        throw new InvalidOperationException($"No releases found for {gh.Owner}/{gh.Repo}");
                    release = all[0];
                }
            }

            ReleaseAsset? asset = gh.AssetName is null
                ? release.Assets.FirstOrDefault(a =>
                    !a.Name.Contains("sha256", StringComparison.OrdinalIgnoreCase) &&
                    !a.Name.Contains("checksum", StringComparison.OrdinalIgnoreCase))
                : release.Assets.FirstOrDefault(a =>
                    string.Equals(a.Name, gh.AssetName, StringComparison.OrdinalIgnoreCase));

            if (asset is null)
                throw new InvalidOperationException($"No downloadable asset found for {art.DisplayName} in {gh.Owner}/{gh.Repo}");

            url = asset.BrowserDownloadUrl;
            fileName = asset.Name;
        }
        else
        {
            throw new InvalidOperationException($"Unsupported source type for {art.DisplayName}.");
        }

        string target = Path.Combine(artifactDir, fileName);

        if (File.Exists(target) && new FileInfo(target).Length > 0)
            return target;

        StatusSubtext.Text = $"Downloading {fileName}...";

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(target, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, 81920, true);
        await input.CopyToAsync(output);

        return target;
    }
    private static void ExtractArchive(string archivePath, string destination)
    {
        if (Directory.Exists(destination))
            Directory.Delete(destination, true);

        Directory.CreateDirectory(destination);

        using var archive = ArchiveFactory.OpenArchive(archivePath);

        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
        {
            string relative = (entry.Key ?? "")
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            if (string.IsNullOrWhiteSpace(relative)) continue;

            string dest = Path.GetFullPath(Path.Combine(destination, relative));
            string fullDestRoot = Path.GetFullPath(destination);

            if (!dest.StartsWith(fullDestRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !dest.Equals(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            string? dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            using var entryStream = entry.OpenEntryStream();
            using var outFile = File.Create(dest);
            entryStream.CopyTo(outFile);
        }
    }

    private static async Task ApplyOperationsAsync(ArtifactDefinition art, string stagingPath, string mountPoint)
    {
        if (art.Operations is null || art.Operations.Count == 0)
        {
            CopyDirectory(stagingPath, mountPoint);
            return;
        }

        foreach (var op in art.Operations)
        {
            switch (op.Kind)
            {
                case InstallOperationKind.CopyDirectory:
                    {
                        string source = ResolvePath(stagingPath, op.SourcePath ?? ".");

                        if (source.Contains("<SUBFOLDER>", StringComparison.OrdinalIgnoreCase))
                        {
                            string parent = source.Replace("<SUBFOLDER>", "", StringComparison.OrdinalIgnoreCase).TrimEnd('\\', '/');
                            var first = Directory.GetDirectories(parent).FirstOrDefault()
                                ?? throw new InvalidOperationException($"Could not resolve <SUBFOLDER> in {parent}");
                            source = first;
                        }

                        string dest = ResolvePath(mountPoint, op.DestinationPath);
                        CopyDirectory(source, dest);
                        break;
                    }

                case InstallOperationKind.CopyFile:
                    {
                        string source = ResolvePath(stagingPath, op.SourcePath ?? throw new InvalidOperationException("CopyFile needs SourcePath"));
                        string dest = ResolvePath(mountPoint, op.DestinationPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.Copy(source, dest, true);
                        break;
                    }

                case InstallOperationKind.WriteFile:
                    {
                        string dest = ResolvePath(mountPoint, op.DestinationPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        await File.WriteAllTextAsync(dest, op.Contents ?? "");
                        break;
                    }

                case InstallOperationKind.RenameFile:
                    {
                        string source = ResolvePath(mountPoint, op.SourcePath ?? throw new InvalidOperationException("RenameFile needs SourcePath"));
                        string dest = ResolvePath(mountPoint, op.DestinationPath);
                        if (File.Exists(source))
                            File.Move(source, dest, true);
                        break;
                    }
            }
        }
    }

    private static string ResolvePath(string root, string relative)
    {
        string current = Path.GetFullPath(root);

        foreach (string part in relative.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;

            if (part.Equals("<SUBFOLDER>", StringComparison.OrdinalIgnoreCase))
            {
                current = Directory.GetDirectories(current).FirstOrDefault()
                    ?? throw new InvalidOperationException($"Could not resolve <SUBFOLDER> inside {current}");
            }
            else
            {
                current = Path.Combine(current, part);
            }
        }

        return Path.GetFullPath(current);
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Staged directory not found: {source}");

        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(source, file);
            string destFile = Path.Combine(destination, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, true);
        }
    }
}