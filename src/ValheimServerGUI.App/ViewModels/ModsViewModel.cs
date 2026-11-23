using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using ValheimServerGUI.App.Converters;
using ValheimServerGUI.Game.Mods;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools;
using ValheimServerGUI.Tools.Logging;

namespace ValheimServerGUI.App.ViewModels;

/// <summary>The text that explains a <see cref="ModSafetyResult"/>.</summary>
public static class ModSafetyText
{
    public static string Reason(ModSafetyResult result) => result.Reason switch
    {
        ModSafetyReason.ServerSideTag => Strings.Mods_Reason_ServerSideTag,
        ModSafetyReason.Loader => Strings.Mods_Reason_Loader,
        ModSafetyReason.ClientSideTag => Strings.Mods_Reason_ClientSideTag,
        ModSafetyReason.NoServerSideTag => Strings.Mods_Reason_NoServerSideTag,
        ModSafetyReason.DescriptionMentionsClients => string.Format(Strings.Mods_Reason_DescriptionMentionsClients, result.Detail),
        ModSafetyReason.Modpack => Strings.Mods_Reason_Modpack,
        ModSafetyReason.Deprecated => Strings.Mods_Reason_Deprecated,
        ModSafetyReason.DependencyMissing => string.Format(Strings.Mods_Reason_DependencyMissing, result.Detail),
        ModSafetyReason.DependencyClientRequired => string.Format(Strings.Mods_Reason_DependencyClientRequired, result.Detail),
        _ => result.Reason.ToString(),
    };
}

/// <summary>A catalog that never reaches Thunderstore: what a view-model built without one (in tests) gets.</summary>
internal sealed class UnavailableThunderstoreCatalog : IThunderstoreCatalog
{
    public static readonly UnavailableThunderstoreCatalog Instance = new();

    public Task<ThunderstoreCatalogSnapshot> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
        => Task.FromException<ThunderstoreCatalogSnapshot>(new IOException("No Thunderstore catalog is configured."));

    public Task<byte[]> DownloadAsync(ThunderstorePackage package, CancellationToken cancellationToken)
        => Task.FromException<byte[]>(new IOException("No Thunderstore catalog is configured."));
}

/// <summary>One Thunderstore package in the catalog list, with its server-only verdict.</summary>
public sealed class CatalogModRowViewModel
{
    public CatalogModRowViewModel(ThunderstorePackage package, ModSafetyResult safety, bool isInstalled)
    {
        Package = package;
        Safety = safety;
        IsInstalled = isInstalled;
        VerdictText = EnumDisplayConverter.ToText(safety.Verdict);
        ReasonText = ModSafetyText.Reason(safety);
        BylineText = string.Format(Strings.Mods_Details_Byline, package.Owner, package.Latest.VersionNumber, package.TotalDownloads);
        var dependencies = package.Latest.Dependencies
            .Select(ThunderstoreDependency.Parse).Where(d => d is not null).Select(d => d!.Value.FullName).ToList();
        DependenciesText = dependencies.Count == 0
            ? Strings.Mods_Details_NoDependencies
            : string.Format(Strings.Mods_Details_Dependencies, string.Join(", ", dependencies));
        CategoriesText = string.Format(Strings.Mods_Details_Categories, string.Join(", ", package.Categories));
    }

    public ThunderstorePackage Package { get; }
    public ModSafetyResult Safety { get; }
    public bool IsAllowed => Safety.IsAllowed;
    public bool IsInstalled { get; }
    public string Name => Package.DisplayName;
    public string Owner => Package.Owner;
    public string Version => Package.Latest.VersionNumber;
    public long Downloads => Package.TotalDownloads;
    public string DownloadsText => Package.TotalDownloads.ToString("N0");
    public string Description => Package.Latest.Description;
    public string VerdictText { get; }
    public string ReasonText { get; }
    public string BylineText { get; }
    public string DependenciesText { get; }
    public string CategoriesText { get; }
    public double RowOpacity => IsAllowed ? 1.0 : 0.55;
}

/// <summary>One mod the app installed on the server.</summary>
public sealed class InstalledModRowViewModel
{
    public InstalledModRowViewModel(InstalledMod mod, ThunderstorePackage? latest, ModSafetyResult? safety)
    {
        Mod = mod;
        Latest = latest;
        var dash = mod.FullName.IndexOf('-');
        Owner = dash > 0 ? mod.FullName[..dash] : "";
        Name = (dash > 0 ? mod.FullName[(dash + 1)..] : mod.FullName).Replace('_', ' ');
        HasUpdate = latest is not null && latest.Latest.VersionNumber != mod.Version;
        var state = mod.IsLoader ? Strings.Mods_State_Loader
            : !mod.Enabled ? Strings.Mods_State_Disabled
            : mod.AsDependency ? Strings.Mods_State_Dependency
            : Strings.Mods_State_Enabled;
        StateText = HasUpdate ? string.Format(Strings.Mods_State_UpdateAvailable, state, latest!.Latest.VersionNumber) : state;
        WarningText = safety is { IsAllowed: false } ? string.Format(Strings.Mods_NowRefused, ModSafetyText.Reason(safety)) : null;
    }

    public InstalledMod Mod { get; }
    public ThunderstorePackage? Latest { get; }
    public string FullName => Mod.FullName;
    public string Name { get; }
    public string Owner { get; }
    public string Version => Mod.Version;
    public bool IsEnabled => Mod.Enabled;
    public bool IsLoader => Mod.IsLoader;
    public bool HasUpdate { get; }
    public string StateText { get; }

    /// <summary>Set when Thunderstore no longer lists the mod as server-only (it was retagged since it was installed).</summary>
    public string? WarningText { get; }
    public bool HasWarning => WarningText is not null;
}

/// <summary>
/// Mods tab: Thunderstore mods for the selected profile's dedicated server, <b>server-only mods only</b>
/// (<see cref="ModSafety"/>). Lists what the app installed (update, disable, remove) and the catalog (search,
/// install with dependencies, BepInExPack when missing). Refused mods can be shown, greyed with the reason, but
/// never installed. Nothing changes while a server runs from the folder. File and network work runs off the UI
/// thread.
/// </summary>
public partial class ModsViewModel : ViewModelBase
{
    /// <summary>Most catalog rows shown at once (most downloaded first).</summary>
    public const int MaxResults = 300;

    private readonly IThunderstoreCatalog _catalog;
    private readonly ServerModManager _manager;
    private readonly IShellLauncher _shell;
    private readonly IApplicationLogger _logger;
    private readonly Func<string?> _serverExePath;
    private readonly Func<string, bool> _folderInUse;

    private ThunderstoreCatalogSnapshot? _snapshot;
    private Dictionary<string, ModSafetyResult> _safety = new(StringComparer.OrdinalIgnoreCase);
    private bool _isActive;
    private bool _catalogRequested;

    public ModsViewModel(
        IThunderstoreCatalog catalog,
        ServerModManager manager,
        IShellLauncher shell,
        IApplicationLogger logger,
        Func<string?> serverExePath,
        Func<string, bool> folderInUse)
    {
        _catalog = catalog;
        _manager = manager;
        _shell = shell;
        _logger = logger;
        _serverExePath = serverExePath;
        _folderInUse = folderInUse;
    }

    /// <summary>Asks a yes/no question (title, body); true = yes. Wired by the window; treated as "no" if unset.</summary>
    public Func<string, string, Task<bool>>? ConfirmPrompt { get; set; }

    /// <summary>Surfaces a failed or refused operation. Wired by the window.</summary>
    public Action<string>? ErrorReported { get; set; }

    public ObservableCollection<InstalledModRowViewModel> Installed { get; } = new();

    public ObservableCollection<CatalogModRowViewModel> Results { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand), nameof(RemoveCommand), nameof(ToggleEnabledCommand))]
    [NotifyPropertyChangedFor(nameof(ToggleEnabledText))]
    private InstalledModRowViewModel? _selectedInstalled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(OpenPageCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedResult))]
    private CatalogModRowViewModel? _selectedResult;

    public bool HasSelectedResult => SelectedResult is not null;

    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private bool _showRefused;

    /// <summary>The selected profile's server folder, or null when it has no server.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UpdateCommand), nameof(UpdateAllCommand), nameof(RemoveCommand), nameof(ToggleEnabledCommand), nameof(OpenFolderCommand))]
    private string? _serverFolder;

    [ObservableProperty] private string _serverFolderText = Strings.Mods_Server_None;
    [ObservableProperty] private string? _bepInExText;
    [ObservableProperty] private string? _unmanagedText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UpdateCommand), nameof(UpdateAllCommand), nameof(RemoveCommand), nameof(ToggleEnabledCommand))]
    private bool _isFolderInUse;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UpdateCommand), nameof(UpdateAllCommand), nameof(RemoveCommand), nameof(ToggleEnabledCommand), nameof(RefreshCatalogCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _isCatalogLoading;
    [ObservableProperty] private string? _catalogStatus;
    [ObservableProperty] private string? _resultsNote;
    [ObservableProperty] private string? _statusMessage;

    public string? InstalledEmptyText => Installed.Count > 0 ? null : ServerFolder is null ? Strings.Mods_Server_None : Strings.Mods_Installed_Empty;

    public string? ResultsEmptyText => Results.Count > 0 || IsCatalogLoading ? null
        : _snapshot is null ? Strings.Mods_Catalog_NotLoaded : Strings.Mods_Catalog_Empty;

    public string ToggleEnabledText => SelectedInstalled is { IsEnabled: false } ? Strings.Mods_Enable : Strings.Mods_Disable;

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    partial void OnShowRefusedChanged(bool value) => ApplyFilter();

    partial void OnIsCatalogLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ResultsEmptyText));
        RefreshCatalogCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Called when the tab becomes visible/hidden: the server folder is read, and the catalog loaded, only while shown.</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (!active) return;
        RefreshInstalled();
        if (!_catalogRequested)
        {
            _catalogRequested = true;
            _ = LoadCatalogAsync(forceRefresh: false);
        }
    }

    /// <summary>The window's profile changed; re-read the server folder if the tab is showing.</summary>
    public void OnSelectionChanged()
    {
        if (_isActive) RefreshInstalled();
    }

    /// <summary>A server started or stopped: re-check whether this folder may be changed.</summary>
    public void RefreshFolderInUse()
    {
        IsFolderInUse = ServerFolder is not null && _folderInUse(ServerFolder);
    }

    /// <summary>Re-reads the server folder: BepInEx, the mods the app installed, and the plugins it did not.</summary>
    public void RefreshInstalled()
    {
        var exe = _serverExePath();
        var folder = string.IsNullOrWhiteSpace(exe) || !File.Exists(exe) ? null : Path.GetDirectoryName(Path.GetFullPath(exe));
        ServerFolder = folder;
        ServerFolderText = folder ?? (string.IsNullOrWhiteSpace(exe) ? Strings.Mods_Server_None : exe);
        RefreshFolderInUse();

        var previous = SelectedInstalled?.FullName;
        Installed.Clear();
        SelectedInstalled = null;
        BepInExText = null;
        UnmanagedText = null;
        if (folder is not null)
        {
            try
            {
                BepInExText = ServerModManager.HasBepInEx(folder) ? Strings.Mods_BepInEx_Installed : Strings.Mods_BepInEx_Missing;
                foreach (var mod in _manager.GetInstalled(folder).OrderBy(m => m.IsLoader ? 0 : 1).ThenBy(m => m.FullName, StringComparer.OrdinalIgnoreCase))
                {
                    var latest = _snapshot?.Find(mod.FullName);
                    var row = new InstalledModRowViewModel(mod, latest, latest is null ? null : SafetyOf(latest));
                    Installed.Add(row);
                    if (row.FullName == previous) SelectedInstalled = row;
                }
                var unmanaged = _manager.GetUnmanagedPlugins(folder);
                if (unmanaged.Count > 0) UnmanagedText = string.Format(Strings.Mods_Unmanaged, string.Join(", ", unmanaged));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.Warning(e, "Could not read the mods of {folder}: {message}", folder, e.Message);
            }
        }
        OnPropertyChanged(nameof(InstalledEmptyText));
        UpdateAllCommand.NotifyCanExecuteChanged();
        ApplyFilter();
    }

    /// <summary>Loads the catalog (from Thunderstore or the saved copy) and works out every package's verdict.</summary>
    public async Task LoadCatalogAsync(bool forceRefresh)
    {
        IsCatalogLoading = true;
        CatalogStatus = Strings.Mods_Catalog_Loading;
        try
        {
            var snapshot = await _catalog.GetAsync(forceRefresh, CancellationToken.None);
            var safety = await Task.Run(() => snapshot.Packages.ToDictionary(p => p.FullName, snapshot.Classify, StringComparer.OrdinalIgnoreCase));
            if (IsDisposed) return;
            _snapshot = snapshot;
            _safety = safety;
            var allowed = safety.Values.Count(s => s.Verdict == ModSafetyVerdict.ServerOnly);
            CatalogStatus = snapshot.IsStale
                ? string.Format(Strings.Mods_Catalog_Stale, snapshot.FetchedAtUtc.ToLocalTime())
                : string.Format(Strings.Mods_Catalog_Status, allowed, snapshot.Packages.Count, snapshot.FetchedAtUtc.ToLocalTime());
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or TaskCanceledException or UnauthorizedAccessException)
        {
            _logger.Warning("Could not read the Thunderstore catalog: {message}", e.Message);
            CatalogStatus = string.Format(Strings.Mods_Catalog_Failed, e.Message);
        }
        finally
        {
            IsCatalogLoading = false;
        }
        if (IsDisposed) return;
        RefreshInstalled();
    }

    private ModSafetyResult SafetyOf(ThunderstorePackage package)
        => _safety.TryGetValue(package.FullName, out var known) ? known : _snapshot!.Classify(package);

    /// <summary>Rebuilds the catalog list from the search text and the "show refused" choice.</summary>
    private void ApplyFilter()
    {
        var previous = SelectedResult?.Package.FullName;
        Results.Clear();
        ResultsNote = null;
        if (_snapshot is not null)
        {
            var installed = new HashSet<string>(Installed.Select(i => i.FullName), StringComparer.OrdinalIgnoreCase);
            var terms = (SearchText ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var matches = _snapshot.Packages
                .Where(p => !p.HasNsfwContent)
                .Where(p => ShowRefused || SafetyOf(p).IsAllowed)
                .Where(p => terms.All(t => p.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                                           || p.Owner.Contains(t, StringComparison.OrdinalIgnoreCase)
                                           || p.DisplayName.Contains(t, StringComparison.OrdinalIgnoreCase)
                                           || p.Latest.Description.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(p => p.TotalDownloads)
                .ToList();

            foreach (var package in matches.Take(MaxResults))
            {
                var row = new CatalogModRowViewModel(package, SafetyOf(package), installed.Contains(package.FullName));
                Results.Add(row);
                if (package.FullName == previous) SelectedResult = row;
            }
            if (matches.Count > MaxResults) ResultsNote = string.Format(Strings.Mods_Results_Truncated, MaxResults);
        }
        if (SelectedResult is not null && !Results.Contains(SelectedResult)) SelectedResult = null;
        OnPropertyChanged(nameof(ResultsEmptyText));
    }

    private bool CanChange => ServerFolder is not null && !IsFolderInUse && !IsBusy;

    private bool CanInstall => CanChange && SelectedResult is { IsAllowed: true } && _snapshot is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        var row = SelectedResult!;
        await InstallOrUpdateAsync(row.Package, confirm: true, Strings.Mods_Status_Installed);
    }

    private bool CanUpdate => CanChange && SelectedInstalled is { HasUpdate: true } && _snapshot is not null;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task Update()
        => InstallOrUpdateAsync(SelectedInstalled!.Latest!, confirm: false, Strings.Mods_Status_Updated);

    private bool CanUpdateAll => CanChange && _snapshot is not null && Installed.Any(i => i.HasUpdate);

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAll()
    {
        // Mods the user picked; their dependencies come along with them. Then whatever is left (dependencies only).
        var targets = Installed.Where(i => i.HasUpdate).OrderBy(i => i.Mod.AsDependency).Select(i => i.Latest!).ToList();
        var updated = 0;
        foreach (var package in targets)
        {
            if (Installed.FirstOrDefault(i => i.FullName == package.FullName) is not { HasUpdate: true }) continue;
            if (!await InstallOrUpdateAsync(package, confirm: false, Strings.Mods_Status_Updated)) break;
            updated++;
        }
        if (updated > 0 && !Installed.Any(i => i.HasUpdate)) StatusMessage = Strings.Mods_Status_UpToDate;
    }

    /// <summary>Plans, (asks,) downloads and installs one package with what it needs. False when refused, cancelled or failed.</summary>
    private async Task<bool> InstallOrUpdateAsync(ThunderstorePackage package, bool confirm, string doneFormat)
    {
        var folder = ServerFolder;
        var snapshot = _snapshot;
        if (folder is null || snapshot is null) return false;

        // A server may have started since the buttons were drawn; check at the moment it matters.
        RefreshFolderInUse();
        if (IsFolderInUse)
        {
            ErrorReported?.Invoke(Strings.Mods_InUse);
            return false;
        }

        var plan = ServerModManager.Plan(package, snapshot, _manager.GetInstalled(folder), ServerModManager.HasBepInEx(folder));
        if (!plan.IsAllowed)
        {
            ErrorReported?.Invoke(string.Format(Strings.Mods_Refused, package.DisplayName, ModSafetyText.Reason(plan.Safety)));
            return false;
        }

        if (confirm)
        {
            var message = string.Format(Strings.Prompt_InstallMod_Message, package.DisplayName, folder);
            var others = plan.Packages.Where(p => p.FullName != package.FullName).Select(p => p.DisplayName).ToList();
            if (others.Count > 0) message += "\n\n" + string.Format(Strings.Prompt_InstallMod_Also, string.Join(", ", others));
            if (ConfirmPrompt is null || !await ConfirmPrompt(Strings.Prompt_InstallMod_Title, message)) return false;
        }

        var ok = await RunAsync(async () =>
        {
            var progress = new Progress<string>(name => StatusMessage = string.Format(Strings.Mods_Status_Downloading, name));
            await _manager.InstallAsync(folder, plan, snapshot, progress, CancellationToken.None);
            return string.Format(doneFormat, package.DisplayName);
        });
        return ok;
    }

    private bool CanRemove => CanChange && SelectedInstalled is { IsLoader: false };

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task Remove()
    {
        var row = SelectedInstalled!;
        var folder = ServerFolder!;
        if (ConfirmPrompt is null || !await ConfirmPrompt(Strings.Prompt_RemoveMod_Title, string.Format(Strings.Prompt_RemoveMod_Message, row.Name))) return;
        RefreshFolderInUse();
        if (IsFolderInUse)
        {
            ErrorReported?.Invoke(Strings.Mods_InUse);
            return;
        }
        await RunAsync(() => Task.Run(() =>
        {
            _manager.Uninstall(folder, row.FullName);
            return string.Format(Strings.Mods_Status_Removed, row.Name);
        }));
    }

    private bool CanToggleEnabled => CanChange && SelectedInstalled is { IsLoader: false };

    [RelayCommand(CanExecute = nameof(CanToggleEnabled))]
    private async Task ToggleEnabled()
    {
        var row = SelectedInstalled!;
        var folder = ServerFolder!;
        RefreshFolderInUse();
        if (IsFolderInUse)
        {
            ErrorReported?.Invoke(Strings.Mods_InUse);
            return;
        }
        var enable = !row.IsEnabled;
        await RunAsync(() => Task.Run(() =>
        {
            _manager.SetEnabled(folder, row.FullName, enable);
            return string.Format(enable ? Strings.Mods_Status_Enabled : Strings.Mods_Status_Disabled, row.Name);
        }));
    }

    private bool CanOpenPage => SelectedResult?.Package.PackageUrl is not null;

    [RelayCommand(CanExecute = nameof(CanOpenPage))]
    private void OpenPage()
    {
        if (SelectedResult?.Package.PackageUrl is { } url && ThunderstoreCatalog.IsThunderstoreUrl(url)) _shell.OpenWebAddress(url);
    }

    private bool CanOpenFolder => ServerFolder is not null;

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        if (ServerFolder is null) return;
        var plugins = ServerModManager.PluginsFolder(ServerFolder);
        try
        {
            _shell.OpenDirectory(Directory.Exists(plugins) ? plugins : ServerFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ErrorReported?.Invoke(e.Message);
        }
    }

    private bool CanRefreshCatalog => !IsBusy && !IsCatalogLoading;

    [RelayCommand(CanExecute = nameof(CanRefreshCatalog))]
    private Task RefreshCatalog() => LoadCatalogAsync(forceRefresh: true);

    /// <summary>Runs one change to the server folder, then reports its outcome and re-reads the folder.</summary>
    private async Task<bool> RunAsync(Func<Task<string>> operation)
    {
        IsBusy = true;
        StatusMessage = Strings.Mods_Status_Working;
        var ok = false;
        try
        {
            StatusMessage = await operation();
            ok = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                                      or HttpRequestException or TaskCanceledException)
        {
            _logger.Error(e, "Mod operation on {folder} failed: {message}", ServerFolder, e.Message);
            StatusMessage = null;
            ErrorReported?.Invoke(e.Message);
        }
        finally
        {
            IsBusy = false;
        }
        RefreshInstalled();
        return ok;
    }
}
