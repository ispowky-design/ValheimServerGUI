using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValheimServerGUI.Game;
using ValheimServerGUI.Game.WorldMap;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools.Logging;

namespace ValheimServerGUI.App.ViewModels;

/// <summary>A point the map should centre on. A class, so asking twice for the same point still moves the map.</summary>
public sealed class WorldMapFocus(float x, float z)
{
    public float X { get; } = x;
    public float Z { get; } = z;
}

/// <summary>One row of the map's location list.</summary>
public sealed class WorldMapSearchRow
{
    public WorldMapSearchRow(WorldMapMarker marker, float originX, float originZ)
    {
        Marker = marker;
        Distance = MathF.Sqrt((marker.X - originX) * (marker.X - originX) + (marker.Z - originZ) * (marker.Z - originZ));
        DistanceText = string.Format(Strings.Map_Search_Distance, Distance);
    }

    public WorldMapMarker Marker { get; }
    public string Name => Marker.Name;
    public float Distance { get; }
    public string DistanceText { get; }
}

/// <summary>Where the map's discovered area comes from: the world's cartography tables, or the zones the server generated.</summary>
public sealed class WorldMapExplorationSource(string displayName, WorldMapExploration exploration, bool isGeneratedZones = false)
{
    public string DisplayName { get; } = displayName;
    public WorldMapExploration Exploration { get; } = exploration;
    public bool IsGeneratedZones { get; } = isGeneratedZones;
    public override string ToString() => DisplayName;
}

/// <summary>A biome swatch in the legend.</summary>
public sealed record WorldMapLegendRow(string Name, IBrush Brush);

/// <summary>
/// World Map tab: draws the selected world from its seed, with the bosses, traders, dungeons and camps its latest save
/// holds. By default only the discovered area shows: the map shared on the world's cartography tables (or, before any
/// table has been used, the zones the server has generated); the rest is covered and its markers hidden. Any seed can
/// also be previewed (terrain only). The map as shown can also be published on a web page for players who are not in
/// game. Everything is read from the save files, read-only, so a running server is never disturbed; world generation
/// runs off the UI thread.
/// </summary>
public partial class WorldMapViewModel : ViewModelBase
{
    private const int MaxSearchRows = 200;

    // While the map is published, how often the world is re-read so the page follows the server's saves.
    private static readonly TimeSpan PublishRefreshInterval = TimeSpan.FromMinutes(5);

    // How often the connected players are re-read while the tab shows or the map is published.
    private static readonly TimeSpan PlayersRefreshInterval = TimeSpan.FromSeconds(3);

    private readonly IApplicationLogger _logger;
    private readonly Func<DirectoryInfo?> _saveFolder;
    private readonly Func<string?> _worldName;
    private readonly WorldMapWebServer? _webServer;
    private readonly IUserPreferencesProvider? _userPrefs;
    private readonly Func<string?> _externalIp;
    private readonly Action<string>? _openWebAddress;
    private readonly DispatcherTimer _publishTimer;
    private readonly Func<string?> _serverExePath;
    private readonly DispatcherTimer _playersTimer;
    private IReadOnlyList<LivePlayer> _livePlayers = Array.Empty<LivePlayer>();
    private bool _readingPlayers;
    private bool _pluginJustInstalled;

    private IReadOnlyList<WorldMapMarker> _allMarkers = Array.Empty<WorldMapMarker>();
    private bool _isActive;
    private bool _isStale = true;
    private int _loadVersion;

    // The loaded world the web page may show (null while previewing a seed or with no world), and when it was saved.
    private string? _publishableWorld;
    private DateTime? _savedAt;
    private int _publishRequest;
    private (WorldMapRenderer Renderer, WorldMapExploration? Exploration, WorldMapPicture Picture, byte[] Png)? _publishedPicture;
    private bool _ownsWebServer;
    private bool _applyingPublishState;

    public WorldMapViewModel(IApplicationLogger logger, Func<DirectoryInfo?> saveFolder, Func<string?> worldName,
        WorldMapWebServer? webServer = null, IUserPreferencesProvider? userPrefs = null, Func<string?>? externalIp = null,
        Action<string>? openWebAddress = null, Func<string?>? serverExePath = null)
    {
        _serverExePath = serverExePath ?? (() => null);
        _playersTimer = new DispatcherTimer { Interval = PlayersRefreshInterval };
        _playersTimer.Tick += (_, _) => _ = RefreshPlayersAsync();
        _logger = logger;
        _saveFolder = saveFolder;
        _worldName = worldName;
        _webServer = webServer;
        _userPrefs = userPrefs;
        _externalIp = externalIp ?? (() => null);
        _openWebAddress = openWebAddress;
        _publishTimer = new DispatcherTimer { Interval = PublishRefreshInterval };
        _publishTimer.Tick += (_, _) =>
        {
            if (!IsBusy) _ = LoadSelectedWorldAsync();
        };

        Legend = WorldMapMarkers.Legend()
            .Select(e => new WorldMapLegendRow(e.Name, new SolidColorBrush(Color.FromRgb(e.R, e.G, e.B))))
            .ToList();
        SearchResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SearchEmptyText));

        if (userPrefs?.LoadPreferences() is { } prefs)
        {
            _applyingPublishState = true;
            PublishPort = prefs.MapWebPort;
            IsPublishing = prefs.PublishMapOnWeb && webServer is { IsRunning: false };
            _applyingPublishState = false;
            if (IsPublishing) StartPublishing();
        }
    }

    public IReadOnlyList<WorldMapLegendRow> Legend { get; }

    /// <summary>The world generator the map draws; null while there is nothing to draw.</summary>
    [ObservableProperty] private WorldMapRenderer? _renderer;

    /// <summary>The markers of the categories that are switched on.</summary>
    [ObservableProperty] private IReadOnlyList<WorldMapMarker> _markers = Array.Empty<WorldMapMarker>();

    /// <summary>The discovered area the map shows when <see cref="ShowFog"/> is on; null when nothing is known.</summary>
    [ObservableProperty] private WorldMapExploration? _exploration;

    /// <summary>The places the discovered area can come from: the cartography tables, then the server's zones.</summary>
    public ObservableCollection<WorldMapExplorationSource> ExplorationSources { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExploration))]
    private WorldMapExplorationSource? _selectedExplorationSource;

    public bool HasExploration => SelectedExplorationSource is not null;

    [ObservableProperty] private WorldMapFocus? _focus;

    [ObservableProperty] private string _worldText = Strings.Map_World_None;
    [ObservableProperty] private string? _seedText;
    [ObservableProperty] private string? _statusMessage = Strings.Map_Status_NoWorld;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand), nameof(RefreshCommand))]
    private bool _isBusy;

    /// <summary>Whether the map fills the tab (the side panel is hidden).</summary>
    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    private string? _previewSeed;

    [ObservableProperty] private bool _showSpawn = true;
    [ObservableProperty] private bool _showBosses = true;
    [ObservableProperty] private bool _showTraders = true;
    [ObservableProperty] private bool _showDungeons;
    [ObservableProperty] private bool _showCamps;
    /// <summary>Show only the discovered area (on by default): the rest of the world is covered and its markers hidden.</summary>
    [ObservableProperty] private bool _showFog = true;

    [ObservableProperty] private string? _searchText;

    [ObservableProperty] private WorldMapSearchRow? _selectedResult;

    public ObservableCollection<WorldMapSearchRow> SearchResults { get; } = new();

    public string? SearchEmptyText => SearchResults.Count > 0 || _allMarkers.Count == 0 ? null : Strings.Map_Search_EmptyText;

    /// <summary>Whether the map, as shown here, is served on a web page for players who are not in game.</summary>
    [ObservableProperty] private bool _isPublishing;

    /// <summary>The TCP port the web page is served on.</summary>
    [ObservableProperty] private int _publishPort = CoreConstants.DefaultMapWebPort;

    /// <summary>The page's address for players on the internet (needs the port forwarded); null while not published.</summary>
    [ObservableProperty] private string? _publicLink;

    /// <summary>The page's address on this computer, to check it; null while not published.</summary>
    [ObservableProperty] private string? _localLink;

    public bool CanPublish => _webServer is not null;

    /// <summary>The connected players the map draws (empty while <see cref="ShowPlayers"/> is off).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayers))]
    private IReadOnlyList<LivePlayer> _players = Array.Empty<LivePlayer>();

    public bool HasPlayers => Players.Count > 0;

    [ObservableProperty] private bool _showPlayers = true;

    [ObservableProperty] private LivePlayer? _selectedPlayer;

    /// <summary>What the players panel says: how many are connected, or what is missing to see them.</summary>
    [ObservableProperty] private string? _playersStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallPlugin))]
    [NotifyCanExecuteChangedFor(nameof(InstallPluginCommand))]
    private ServerPluginState _pluginState = ServerPluginState.NoServer;

    public bool CanInstallPlugin => PluginState is ServerPluginState.NotInstalled or ServerPluginState.Outdated;

    partial void OnShowPlayersChanged(bool value) => Players = value ? _livePlayers : Array.Empty<LivePlayer>();

    partial void OnSelectedPlayerChanged(LivePlayer? value)
    {
        if (value is not null) Focus = new WorldMapFocus(value.X, value.Z);
    }

    partial void OnShowSpawnChanged(bool value) => ApplyFilters();
    partial void OnShowBossesChanged(bool value) => ApplyFilters();
    partial void OnShowTradersChanged(bool value) => ApplyFilters();
    partial void OnShowDungeonsChanged(bool value) => ApplyFilters();
    partial void OnShowCampsChanged(bool value) => ApplyFilters();

    partial void OnShowFogChanged(bool value)
    {
        ApplyFilters();
        RefreshSearch();
    }

    partial void OnSelectedExplorationSourceChanged(WorldMapExplorationSource? value)
    {
        Exploration = value?.Exploration;
        ApplyFilters();
        RefreshSearch();
    }

    partial void OnSearchTextChanged(string? value) => RefreshSearch();

    partial void OnSelectedResultChanged(WorldMapSearchRow? value)
    {
        if (value is not null) Focus = new WorldMapFocus(value.Marker.X, value.Marker.Z);
    }

    partial void OnRendererChanged(WorldMapRenderer? value) => SchedulePublish();

    partial void OnIsPublishingChanged(bool value)
    {
        if (_applyingPublishState) return;
        if (value) StartPublishing();
        else StopPublishing();
        SavePublishPreferences();
    }

    partial void OnPublishPortChanged(int value)
    {
        if (_applyingPublishState) return;
        if (IsPublishing) StartPublishing();
        SavePublishPreferences();
    }

    /// <summary>Called by the view when the tab becomes visible/hidden: the world is read from disk only while shown (or published).</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (active && _isStale) _ = LoadSelectedWorldAsync();
        UpdatePlayersTimer();
    }

    /// <summary>The window's world or save folder changed; re-read now if the tab is showing or the map published, else when it next shows.</summary>
    public void OnSelectionChanged()
    {
        _isStale = true;
        if (_isActive || IsPublishing) _ = LoadSelectedWorldAsync();
    }

    private bool CanRefresh => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task Refresh() => LoadSelectedWorldAsync();

    private bool CanPreview => !IsBusy && !string.IsNullOrWhiteSpace(PreviewSeed);

    [RelayCommand(CanExecute = nameof(CanPreview))]
    private async Task Preview()
    {
        var seedName = PreviewSeed?.Trim();
        if (string.IsNullOrEmpty(seedName))
        {
            StatusMessage = Strings.Map_Status_InvalidSeed;
            return;
        }

        var version = ++_loadVersion;
        WorldText = Strings.Map_World_None;
        SeedText = seedName;
        _publishableWorld = null;
        SetExploration(null, null);
        SetSaveData(null);
        await LoadTerrainAsync(version, ValheimHash.SeedFromName(seedName), worldGenVersion: 2);
        if (version == _loadVersion) StatusMessage = Strings.Map_Status_Preview;
    }

    [RelayCommand]
    private void OpenPublicLink()
    {
        if (PublicLink is { } link) _openWebAddress?.Invoke(link);
    }

    [RelayCommand]
    private void OpenLocalLink()
    {
        if (LocalLink is { } link) _openWebAddress?.Invoke(link);
    }

    /// <summary>Reads the selected world's seed and latest save, then draws it.</summary>
    public async Task LoadSelectedWorldAsync()
    {
        _isStale = false;
        var version = ++_loadVersion;
        _publishableWorld = null;

        var world = _worldName();
        var folder = string.IsNullOrWhiteSpace(world) ? null : _saveFolder();
        if (folder is null || world is null)
        {
            WorldText = Strings.Map_World_None;
            SeedText = null;
            SetExploration(null, null);
            SetSaveData(null);
            Renderer = null;
            StatusMessage = Strings.Map_Status_NoWorld;
            return;
        }

        WorldText = world;
        var worldFolder = folder.GetLocalWorldFolder(world);

        WorldMetadata metadata;
        WorldMapSaveData? save = null;
        string? saveError = null;
        DateTime? savedAt = null;
        SharedWorldMap? sharedMap = null;
        IsBusy = true;
        try
        {
            (metadata, save, saveError, savedAt, sharedMap) = await Task.Run(() =>
            {
                var meta = WorldMetadataReader.ReadFromWorldFolder(worldFolder);
                var tables = ReadSharedMap(worldFolder, world);
                try
                {
                    var data = WorldMapSaveReader.ReadFromWorldFolder(worldFolder);
                    var time = WorldMetadataReader.FindLatestCompleteSave(worldFolder)?.LastWriteTime;
                    return (meta, data, (string?)null, time, tables);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    return (meta, (WorldMapSaveData?)null, e.Message, (DateTime?)null, tables);
                }
            });
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (version != _loadVersion) return;
            _logger.Warning("Could not read the save of world {world} for the map: {message}", world, e.Message);
            IsBusy = false;
            SeedText = null;
            SetExploration(null, null);
            SetSaveData(null);
            Renderer = null;
            StatusMessage = string.Format(Strings.Map_Status_ReadFailed, e.Message);
            return;
        }

        if (version != _loadVersion) return;
        IsBusy = false;

        if (metadata.Seed is not { } seed)
        {
            SeedText = null;
            SetExploration(null, null);
            SetSaveData(null);
            Renderer = null;
            StatusMessage = Strings.Map_Status_NoSave;
            return;
        }

        SeedText = metadata.SeedName;
        _publishableWorld = world;
        _savedAt = savedAt;
        SetSaveData(save);
        SetExploration(sharedMap, save is null ? null : WorldMapExploration.FromGeneratedZones(save.GeneratedZones));
        if (saveError is not null)
        {
            _logger.Warning("Could not read the locations of world {world} for the map: {message}", world, saveError);
        }

        await LoadTerrainAsync(version, seed, metadata.WorldGenVersion ?? 2);
        if (version != _loadVersion) return;

        StatusMessage = saveError is not null ? string.Format(Strings.Map_Status_ReadFailed, saveError)
            : save is null || savedAt is null ? Strings.Map_Status_TerrainOnly
            : string.Format(Strings.Map_Status_World, _allMarkers.Count, savedAt.Value);
        SchedulePublish();
    }

    private async Task LoadTerrainAsync(int version, int seed, int worldGenVersion)
    {
        if (Renderer?.Seed == seed) return;

        IsBusy = true;
        StatusMessage = Strings.Map_Status_Generating;
        Renderer = null;
        try
        {
            var renderer = await Task.Run(() => new WorldMapRenderer(seed, worldGenVersion));
            if (version == _loadVersion) Renderer = renderer;
        }
        finally
        {
            if (version == _loadVersion) IsBusy = false;
        }
    }

    private void SetSaveData(WorldMapSaveData? save)
    {
        _allMarkers = save is null ? Array.Empty<WorldMapMarker>() : WorldMapMarkers.FromSave(save);
        ApplyFilters();
        RefreshSearch();
    }

    /// <summary>
    /// Lists the sources (the cartography tables, then the server's zones as a fallback) and selects the tables, or
    /// the zones when there are no tables yet. A reload keeps the zones selected if they were.
    /// </summary>
    private void SetExploration(SharedWorldMap? sharedMap, WorldMapExploration? generatedZones)
    {
        var keepZones = SelectedExplorationSource?.IsGeneratedZones == true;

        var sources = new List<WorldMapExplorationSource>();
        if (sharedMap is { } shared)
        {
            var name = shared.TableCount == 1
                ? Strings.Map_Exploration_Tables_One
                : string.Format(Strings.Map_Exploration_Tables_Other, shared.TableCount);
            sources.Add(new WorldMapExplorationSource(name, shared.Exploration));
        }
        if (generatedZones is { } zones)
        {
            sources.Add(new WorldMapExplorationSource(Strings.Map_Exploration_ServerZones, zones, isGeneratedZones: true));
        }

        ExplorationSources.Clear();
        foreach (var source in sources) ExplorationSources.Add(source);
        SelectedExplorationSource = (keepZones ? sources.FirstOrDefault(s => s.IsGeneratedZones) : null) ?? sources.FirstOrDefault();
    }

    /// <summary>Reads, off the UI thread, what the world's cartography tables hold; null when there are none.</summary>
    private SharedWorldMap? ReadSharedMap(DirectoryInfo worldFolder, string world)
    {
        try
        {
            return SharedMapReader.ReadFromWorldFolder(worldFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Warning("Could not read the cartography tables of world {world}: {message}", world, e.Message);
            return null;
        }
    }

    private void ApplyFilters()
    {
        Markers = _allMarkers.Where(m => IsShown(m.Kind) && IsDiscovered(m)).ToList();
        SchedulePublish();
    }

    /// <summary>With the fog on, a place nobody has discovered stays hidden, on the map and in the list.</summary>
    private bool IsDiscovered(WorldMapMarker marker)
        => !ShowFog || Exploration is not { } exploration || exploration.IsExplored(marker.X, marker.Z);

    private bool IsShown(WorldLocationKind kind) => kind switch
    {
        WorldLocationKind.Spawn => ShowSpawn,
        WorldLocationKind.Boss => ShowBosses,
        WorldLocationKind.Trader => ShowTraders,
        WorldLocationKind.Dungeon => ShowDungeons,
        WorldLocationKind.Camp => ShowCamps,
        _ => false,
    };

    /// <summary>
    /// Lists the locations matching the search text, nearest the spawn first. With no text, lists the spawn, bosses
    /// and traders: the places people look for.
    /// </summary>
    private void RefreshSearch()
    {
        var spawn = _allMarkers.FirstOrDefault(m => m.Kind == WorldLocationKind.Spawn);
        var (originX, originZ) = spawn is null ? (0f, 0f) : (spawn.X, spawn.Z);
        var text = SearchText?.Trim();

        IEnumerable<WorldMapMarker> matches = string.IsNullOrEmpty(text)
            ? _allMarkers.Where(m => m.Kind is WorldLocationKind.Spawn or WorldLocationKind.Boss or WorldLocationKind.Trader)
            : _allMarkers.Where(m => m.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        matches = matches.Where(IsDiscovered);

        SearchResults.Clear();
        foreach (var row in matches
                     .Select(m => new WorldMapSearchRow(m, originX, originZ))
                     .OrderBy(r => r.Marker.Kind)
                     .ThenBy(r => r.Distance)
                     .Take(MaxSearchRows))
        {
            SearchResults.Add(row);
        }
    }

    private void StartPublishing()
    {
        if (_webServer is null) return;
        try
        {
            _webServer.Start(PublishPort);
        }
        catch (Exception e) when (e is SocketException or ArgumentOutOfRangeException)
        {
            _logger.Warning("Could not publish the world map on port {port}: {message}", PublishPort, e.Message);
            StatusMessage = string.Format(Strings.Map_Status_PublishFailed, PublishPort, e.Message);
            _applyingPublishState = true;
            IsPublishing = false;
            _applyingPublishState = false;
            StopPublishing();
            return;
        }

        _ownsWebServer = true;
        UpdatePlayersTimer();
        LocalLink = string.Format(CultureInfo.InvariantCulture, "http://localhost:{0}/", PublishPort);
        UpdatePublicLink();
        _publishTimer.Start();
        if (_publishableWorld is null) OnSelectionChanged();
        else SchedulePublish();
    }

    private void StopPublishing()
    {
        _publishTimer.Stop();
        UpdatePlayersTimer();
        if (_ownsWebServer) _webServer?.Stop();
        _ownsWebServer = false;
        PublicLink = null;
        LocalLink = null;
    }

    private void UpdatePublicLink()
    {
        var ip = _externalIp();
        PublicLink = string.IsNullOrWhiteSpace(ip)
            ? null
            : string.Format(CultureInfo.InvariantCulture, ip.Contains(':') ? "http://[{0}]:{1}/" : "http://{0}:{1}/", ip, PublishPort);
    }

    private void SavePublishPreferences()
    {
        if (_userPrefs is null) return;
        var prefs = _userPrefs.LoadPreferences();
        if (prefs.PublishMapOnWeb == IsPublishing && prefs.MapWebPort == PublishPort) return;
        prefs.PublishMapOnWeb = IsPublishing;
        prefs.MapWebPort = PublishPort;
        _userPrefs.SavePreferences(prefs);
    }

    /// <summary>
    /// Sends the map, as shown here, to the web page: the picture is redrawn off the UI thread only when the world or
    /// its discovered area changed, and the page itself is rebuilt for the current places.
    /// </summary>
    private void SchedulePublish()
    {
        if (!IsPublishing || _webServer is null || IsDisposed) return;
        if (Renderer is not { } renderer || _publishableWorld is not { } world) return;
        _ = PublishAsync(++_publishRequest, renderer, world, ShowFog ? Exploration : null, Markers);
    }

    private async Task PublishAsync(int request, WorldMapRenderer renderer, string world, WorldMapExploration? exploration,
        IReadOnlyList<WorldMapMarker> markers)
    {
        // Let a burst of changes (a reload sets several properties in a row) settle into one publish.
        await Task.Yield();
        if (request != _publishRequest) return;

        WorldMapPicture picture;
        byte[] png;
        if (_publishedPicture is { } cached && cached.Renderer == renderer && cached.Exploration == exploration)
        {
            (picture, png) = (cached.Picture, cached.Png);
        }
        else
        {
            try
            {
                (picture, png) = await Task.Run(() =>
                {
                    var drawn = WorldMapImage.Render(renderer, exploration);
                    return (drawn, PngEncoder.Encode(drawn.Width, drawn.Height, drawn.Pixels));
                });
            }
            catch (Exception e) when (e is OutOfMemoryException or InvalidOperationException)
            {
                _logger.Warning("Could not draw the world map for the web page: {message}", e.Message);
                return;
            }
            if (request != _publishRequest || IsDisposed) return;
            _publishedPicture = (renderer, exploration, picture, png);
        }

        var signature = string.Join('\n', markers.Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.Kind}:{m.X}:{m.Z}")));
        var version = Convert.ToHexString(SHA256.HashData(png.Concat(Encoding.UTF8.GetBytes(world + signature)).ToArray()))[..16];
        var html = WorldMapWebPage.Build(world, picture, markers, _savedAt ?? DateTime.Now, version);
        _webServer!.Publish(new WorldMapWebContent(html, png, version));
        UpdatePublicLink();
    }

    [RelayCommand(CanExecute = nameof(CanInstallPlugin))]
    private async Task InstallPlugin()
    {
        var exe = _serverExePath();
        if (string.IsNullOrWhiteSpace(exe)) return;
        try
        {
            await Task.Run(() => ServerPluginInstaller.Install(exe));
            _logger.Information("Installed the player positions plugin at {path}", ServerPluginInstaller.PluginPath(exe));
            _pluginJustInstalled = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.Warning("Could not install the player positions plugin: {message}", e.Message);
            PlayersStatus = string.Format(Strings.Map_Players_InstallFailed, e.Message);
            return;
        }
        await RefreshPlayersAsync();
    }

    private void UpdatePlayersTimer()
    {
        if (IsDisposed) return;
        if (_isActive || IsPublishing)
        {
            if (!_playersTimer.IsEnabled)
            {
                _playersTimer.Start();
                _ = RefreshPlayersAsync();
            }
        }
        else
        {
            _playersTimer.Stop();
        }
    }

    /// <summary>
    /// Re-reads, off the UI thread, whether the server can report its players and where they are. Players count only
    /// while the server is writing (a stopped server leaves its last file behind) and only for the world on show.
    /// </summary>
    public async Task RefreshPlayersAsync()
    {
        if (_readingPlayers || IsDisposed) return;
        _readingPlayers = true;
        try
        {
            var exe = _serverExePath();
            var (state, snapshot, error) = await Task.Run(() =>
            {
                var current = ServerPluginInstaller.Check(exe);
                if (exe is null || current is not (ServerPluginState.Installed or ServerPluginState.Outdated))
                {
                    return (current, (LivePlayerSnapshot?)null, (string?)null);
                }
                try
                {
                    return (current, LivePlayerReader.Read(LivePlayerReader.FilePath(exe)), (string?)null);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    return (current, null, e.Message);
                }
            });
            if (IsDisposed) return;

            PluginState = state;
            var live = snapshot is not null && snapshot.IsCurrent(DateTime.UtcNow)
                       && (snapshot.World is null || _publishableWorld is null || string.Equals(snapshot.World, _publishableWorld, StringComparison.Ordinal))
                ? snapshot.Players
                : Array.Empty<LivePlayer>();
            if (snapshot is not null && snapshot.IsCurrent(DateTime.UtcNow)) _pluginJustInstalled = false;

            _livePlayers = live;
            Players = ShowPlayers ? live : Array.Empty<LivePlayer>();
            PlayersStatus = error is not null ? string.Format(Strings.Map_Players_Status_ReadFailed, error) : state switch
            {
                ServerPluginState.NoServer => Strings.Map_Players_Status_NoServer,
                ServerPluginState.NoBepInEx => Strings.Map_Players_Status_NoBepInEx,
                ServerPluginState.NotInstalled => Strings.Map_Players_Status_NotInstalled,
                _ when _pluginJustInstalled => Strings.Map_Players_Status_Installed,
                _ when snapshot is null || !snapshot.IsCurrent(DateTime.UtcNow) => Strings.Map_Players_Status_Waiting,
                _ when live.Count == 0 => Strings.Map_Players_Status_None,
                _ when live.Count == 1 => Strings.Map_Players_Status_One,
                _ => string.Format(Strings.Map_Players_Status_Other, live.Count),
            };
            if (state == ServerPluginState.Outdated) PlayersStatus += " " + Strings.Map_Players_Status_Outdated;

            // The web page only shows the players who chose to be visible on the in-game map.
            if (IsPublishing) _webServer?.PublishPlayers(live.Where(p => p.IsPublic));
        }
        finally
        {
            _readingPlayers = false;
        }
    }

    protected override void DisposeCore()
    {
        _playersTimer.Stop();
        _publishTimer.Stop();
        if (_ownsWebServer) _webServer?.Stop();
    }
}
