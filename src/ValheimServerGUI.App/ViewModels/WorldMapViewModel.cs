using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
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

/// <summary>Where the map's discovered area comes from: a character's own map, or the zones the server generated.</summary>
public sealed class WorldMapExplorationSource(string displayName, WorldMapExploration exploration)
{
    public string DisplayName { get; } = displayName;
    public WorldMapExploration Exploration { get; } = exploration;
    public override string ToString() => DisplayName;
}

/// <summary>A biome swatch in the legend.</summary>
public sealed record WorldMapLegendRow(string Name, IBrush Brush);

/// <summary>
/// World Map tab: draws the selected world from its seed, with the bosses, traders, dungeons and camps its latest save
/// holds. By default only the discovered area shows, as a character's own in-game map has it (or, with no character
/// save at hand, the zones the server has generated); the rest is covered and its markers hidden. Any seed can also be
/// previewed (terrain only). Everything is read from the save files, read-only, so a running server is never
/// disturbed; world generation runs off the UI thread.
/// </summary>
public partial class WorldMapViewModel : ViewModelBase
{
    private const int MaxSearchRows = 200;

    private readonly IApplicationLogger _logger;
    private readonly Func<DirectoryInfo?> _saveFolder;
    private readonly Func<string?> _worldName;
    private readonly Func<IEnumerable<DirectoryInfo>> _characterFolders;
    private long? _worldUid;

    private IReadOnlyList<WorldMapMarker> _allMarkers = Array.Empty<WorldMapMarker>();
    private bool _isActive;
    private bool _isStale = true;
    private int _loadVersion;

    public WorldMapViewModel(IApplicationLogger logger, Func<DirectoryInfo?> saveFolder, Func<string?> worldName,
        Func<IEnumerable<DirectoryInfo>>? characterFolders = null)
    {
        _logger = logger;
        _saveFolder = saveFolder;
        _worldName = worldName;
        _characterFolders = characterFolders ?? (() => Array.Empty<DirectoryInfo>());

        Legend = WorldMapMarkers.Legend()
            .Select(e => new WorldMapLegendRow(e.Name, new SolidColorBrush(Color.FromRgb(e.R, e.G, e.B))))
            .ToList();
        SearchResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(SearchEmptyText));
    }

    public IReadOnlyList<WorldMapLegendRow> Legend { get; }

    /// <summary>The world generator the map draws; null while there is nothing to draw.</summary>
    [ObservableProperty] private WorldMapRenderer? _renderer;

    /// <summary>The markers of the categories that are switched on.</summary>
    [ObservableProperty] private IReadOnlyList<WorldMapMarker> _markers = Array.Empty<WorldMapMarker>();

    /// <summary>The discovered area the map shows when <see cref="ShowFog"/> is on; null when nothing is known.</summary>
    [ObservableProperty] private WorldMapExploration? _exploration;

    /// <summary>The places the discovered area can come from: each character that has a map of this world, then the server's zones.</summary>
    public ObservableCollection<WorldMapExplorationSource> ExplorationSources { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExploration))]
    private WorldMapExplorationSource? _selectedExplorationSource;

    public bool HasExploration => SelectedExplorationSource is not null;

    /// <summary>A character save picked by hand (e.g. one kept on another drive or in Steam Cloud).</summary>
    [ObservableProperty] private string? _characterFile;

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

    partial void OnCharacterFileChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || _worldUid is not { } uid) return;
        _ = AddCharacterFileAsync(new FileInfo(value), uid);
    }
    partial void OnSearchTextChanged(string? value) => RefreshSearch();

    partial void OnSelectedResultChanged(WorldMapSearchRow? value)
    {
        if (value is not null) Focus = new WorldMapFocus(value.Marker.X, value.Marker.Z);
    }

    /// <summary>Called by the view when the tab becomes visible/hidden: the world is read from disk only while shown.</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (active && _isStale) _ = LoadSelectedWorldAsync();
    }

    /// <summary>The window's world or save folder changed; re-read now if the tab is showing, else when it next shows.</summary>
    public void OnSelectionChanged()
    {
        _isStale = true;
        if (_isActive) _ = LoadSelectedWorldAsync();
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
        _worldUid = null;
        SetExplorationSources(Array.Empty<WorldMapExplorationSource>());
        SetSaveData(null);
        await LoadTerrainAsync(version, ValheimHash.SeedFromName(seedName), worldGenVersion: 2);
        if (version == _loadVersion) StatusMessage = Strings.Map_Status_Preview;
    }

    /// <summary>Reads the selected world's seed and latest save, then draws it.</summary>
    public async Task LoadSelectedWorldAsync()
    {
        _isStale = false;
        var version = ++_loadVersion;
        _worldUid = null;
        SetExplorationSources(Array.Empty<WorldMapExplorationSource>());

        var world = _worldName();
        var folder = string.IsNullOrWhiteSpace(world) ? null : _saveFolder();
        if (folder is null || world is null)
        {
            WorldText = Strings.Map_World_None;
            SeedText = null;
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
        IReadOnlyList<CharacterWorldMap> characterMaps = Array.Empty<CharacterWorldMap>();
        var characterFolders = _characterFolders().ToList();
        IsBusy = true;
        try
        {
            (metadata, save, saveError, savedAt, characterMaps) = await Task.Run(() =>
            {
                var meta = WorldMetadataReader.ReadFromWorldFolder(worldFolder);
                var maps = meta.Uid is { } uid ? ReadCharacterMaps(characterFolders, uid) : Array.Empty<CharacterWorldMap>();
                try
                {
                    var data = WorldMapSaveReader.ReadFromWorldFolder(worldFolder);
                    var time = WorldMetadataReader.FindLatestCompleteSave(worldFolder)?.LastWriteTime;
                    return (meta, data, (string?)null, time, maps);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    return (meta, (WorldMapSaveData?)null, e.Message, (DateTime?)null, maps);
                }
            });
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (version != _loadVersion) return;
            _logger.Warning("Could not read the save of world {world} for the map: {message}", world, e.Message);
            IsBusy = false;
            SeedText = null;
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
            SetSaveData(null);
            Renderer = null;
            StatusMessage = Strings.Map_Status_NoSave;
            return;
        }

        SeedText = metadata.SeedName;
        _worldUid = metadata.Uid;
        var sources = characterMaps
            .Select(m => new WorldMapExplorationSource(string.Format(Strings.Map_Exploration_Character, m.CharacterName), m.Exploration))
            .ToList();
        if (save is not null)
        {
            sources.Add(new WorldMapExplorationSource(Strings.Map_Exploration_ServerZones,
                WorldMapExploration.FromGeneratedZones(save.GeneratedZones)));
        }
        SetSaveData(save);
        SetExplorationSources(sources);
        if (saveError is not null)
        {
            _logger.Warning("Could not read the locations of world {world} for the map: {message}", world, saveError);
        }

        await LoadTerrainAsync(version, seed, metadata.WorldGenVersion ?? 2);
        if (version != _loadVersion) return;

        StatusMessage = saveError is not null ? string.Format(Strings.Map_Status_ReadFailed, saveError)
            : save is null || savedAt is null ? Strings.Map_Status_TerrainOnly
            : string.Format(Strings.Map_Status_World, _allMarkers.Count, savedAt.Value);
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

    private void SetExplorationSources(IReadOnlyList<WorldMapExplorationSource> sources)
    {
        ExplorationSources.Clear();
        foreach (var source in sources) ExplorationSources.Add(source);
        SelectedExplorationSource = sources.FirstOrDefault();
    }

    /// <summary>Reads, off the UI thread, the map each character save in the given folders has of the world.</summary>
    private IReadOnlyList<CharacterWorldMap> ReadCharacterMaps(IEnumerable<DirectoryInfo> folders, long worldUid)
    {
        var maps = new List<CharacterWorldMap>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            IEnumerable<FileInfo> files;
            try
            {
                files = CharacterMapReader.FindCharacterFiles(folder).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!seen.Add(file.FullName)) continue;
                try
                {
                    if (CharacterMapReader.ReadWorldMap(file, worldUid) is { } map) maps.Add(map);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    _logger.Warning("Could not read the map in character save {path}: {message}", file.FullName, e.Message);
                }
            }
        }
        return maps;
    }

    private async Task AddCharacterFileAsync(FileInfo file, long worldUid)
    {
        CharacterWorldMap? map;
        try
        {
            map = await Task.Run(() => CharacterMapReader.ReadWorldMap(file, worldUid));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            StatusMessage = string.Format(Strings.Map_Status_CharacterFailed, e.Message);
            return;
        }

        if (_worldUid != worldUid) return;
        if (map is null)
        {
            StatusMessage = Strings.Map_Status_CharacterNoWorld;
            return;
        }

        var source = new WorldMapExplorationSource(string.Format(Strings.Map_Exploration_Character, map.CharacterName), map.Exploration);
        ExplorationSources.Insert(0, source);
        SelectedExplorationSource = source;
    }

    private void ApplyFilters()
    {
        Markers = _allMarkers.Where(m => IsShown(m.Kind) && IsDiscovered(m)).ToList();
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
}
