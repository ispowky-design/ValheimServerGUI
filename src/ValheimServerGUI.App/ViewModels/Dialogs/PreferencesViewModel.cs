using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using ValheimServerGUI.Game;
using ValheimServerGUI.Tools;

namespace ValheimServerGUI.App.ViewModels.Dialogs;

/// <summary>
/// Preferences dialog: the six app-wide booleans, the theme, the UI language and the Force Stop offer delay. Start-with-OS is not a stored preference: it
/// is read from and written to the OS registration through <see cref="IStartupManager"/>. The theme is
/// re-applied by the caller on save.
/// </summary>
public partial class PreferencesViewModel : ModalEditViewModel
{
    private readonly IUserPreferencesProvider _userPrefs;
    private readonly IStartupManager _startupManager;

    public PreferencesViewModel(IUserPreferencesProvider userPrefs, IStartupManager startupManager)
    {
        _userPrefs = userPrefs;
        _startupManager = startupManager;
        Load();
    }

    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private bool _startWithOs;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _saveProfileOnStart;
    [ObservableProperty] private bool _writeApplicationLogsToFile;
    [ObservableProperty] private bool _enablePasswordValidation;
    [ObservableProperty] private AppTheme _theme;
    [ObservableProperty] private int _forceStopOfferSeconds;
    [ObservableProperty] private AppLanguage _language;

    public IReadOnlyList<AppTheme> Themes { get; } = new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark, AppTheme.Valheim };

    public IReadOnlyList<AppLanguage> Languages { get; } = new[] { AppLanguage.System, AppLanguage.English, AppLanguage.French };

    /// <summary>Set on save so the caller can re-apply the theme to the running app.</summary>
    public AppTheme SavedTheme { get; private set; }

    // The registration as read on open; Save only touches the OS when the checkbox moved away from it.
    private bool _loadedStartWithOs;

    private void Load() => LoadClean(() =>
    {
        var p = _userPrefs.LoadPreferences();
        CheckForUpdates = p.CheckForUpdates;
        StartWithOs = _loadedStartWithOs = _startupManager.IsStartupEnabled();
        StartMinimized = p.StartMinimized;
        SaveProfileOnStart = p.SaveProfileOnStart;
        WriteApplicationLogsToFile = p.WriteApplicationLogsToFile;
        EnablePasswordValidation = p.EnablePasswordValidation;
        Theme = p.Theme;
        ForceStopOfferSeconds = p.ForceStopOfferSeconds;
        Language = p.Language;
    });

    public override void ApplyDefaults()
    {
        var d = UserPreferences.GetDefault();
        CheckForUpdates = d.CheckForUpdates;
        StartWithOs = false;
        StartMinimized = d.StartMinimized;
        SaveProfileOnStart = d.SaveProfileOnStart;
        WriteApplicationLogsToFile = d.WriteApplicationLogsToFile;
        EnablePasswordValidation = d.EnablePasswordValidation;
        Theme = d.Theme;
        ForceStopOfferSeconds = d.ForceStopOfferSeconds;
        Language = d.Language;
    }

    /// <summary>Flush to the store (OK). Applies a changed startup registration; theme is re-applied by the caller.</summary>
    public void Save()
    {
        var p = _userPrefs.LoadPreferences();
        p.CheckForUpdates = CheckForUpdates;
        p.StartMinimized = StartMinimized;
        p.SaveProfileOnStart = SaveProfileOnStart;
        p.WriteApplicationLogsToFile = WriteApplicationLogsToFile;
        p.EnablePasswordValidation = EnablePasswordValidation;
        p.Theme = Theme;
        p.ForceStopOfferSeconds = ForceStopOfferSeconds;
        p.Language = Language;
        _userPrefs.SavePreferences(p);

        if (StartWithOs != _loadedStartWithOs)
        {
            _startupManager.ApplyStartupSetting(StartWithOs);
            _loadedStartWithOs = StartWithOs;
        }
        SavedTheme = Theme;
    }
}
