using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValheimServerGUI.App.Converters;
using ValheimServerGUI.Game.Network;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools;

namespace ValheimServerGUI.App.ViewModels.Dialogs;

/// <summary>
/// "Can my friends join?": runs <see cref="ConnectionDiagnostics"/> for the window's profile and lists each check with
/// its verdict. The only change it can make is the firewall rule, on the user's click.
/// </summary>
public partial class ConnectionCheckViewModel : ViewModelBase
{
    private readonly ConnectionDiagnostics _diagnostics;
    private readonly IConnectionProbe _probe;
    private readonly IIpAddressProvider _ips;
    private readonly Func<string?, string?, ConnectionDiagnosticInput> _buildInput;
    private ConnectionDiagnosticInput? _lastInput;

    public ConnectionCheckViewModel(
        IConnectionProbe probe,
        IIpAddressProvider ips,
        Func<string?, string?, ConnectionDiagnosticInput> buildInput)
    {
        _probe = probe;
        _diagnostics = new ConnectionDiagnostics(probe);
        _ips = ips;
        _buildInput = buildInput;
    }

    public ObservableCollection<DiagnosticRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CopyCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _summary = Strings.Diagnostic_Running;

    /// <summary>Copies text to the clipboard. Wired by the window.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    private bool CanRun => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    public async Task RunAsync()
    {
        IsBusy = true;
        Summary = Strings.Diagnostic_Running;
        Rows.Clear();
        try
        {
            if (_ips.ExternalIpAddress is null) await _ips.LoadExternalIpAddressAsync();
            if (_ips.InternalIpAddress is null) await _ips.LoadInternalIpAddressAsync();

            _lastInput = _buildInput(_ips.ExternalIpAddress, _ips.InternalIpAddress);
            var results = await _diagnostics.RunAsync(_lastInput);
            foreach (var result in results) Rows.Add(new DiagnosticRowViewModel(result, ApplyFixAsync));

            Summary = results.Any(r => r.Outcome is DiagnosticOutcome.Problem or DiagnosticOutcome.Warning)
                ? Strings.Diagnostic_Summary_Issues
                : !_lastInput.ServerRunning ? Strings.Diagnostic_Summary_Stopped : Strings.Diagnostic_Summary_Ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CopyAsync()
    {
        if (CopyToClipboard is null) return;
        var text = new StringBuilder().AppendLine(Strings.Diagnostic_Title);
        foreach (var row in Rows) text.AppendLine($"[{row.OutcomeMark}] {row.Title}: {row.Detail}");
        await CopyToClipboard(text.ToString());
    }

    private async Task ApplyFixAsync(DiagnosticRowViewModel row)
    {
        if (row.Fix != DiagnosticFix.AddFirewallRule || _lastInput is null) return;

        if (await _probe.AddFirewallRuleAsync(_lastInput.Port, _lastInput.Port + 1))
            await RunAsync();
        else
            row.Detail = Strings.Diagnostic_Fix_Failed;
    }
}

/// <summary>One check's line in the connection check.</summary>
public partial class DiagnosticRowViewModel : ObservableObject
{
    private readonly Func<DiagnosticRowViewModel, Task> _applyFix;

    public DiagnosticRowViewModel(DiagnosticResult result, Func<DiagnosticRowViewModel, Task> applyFix)
    {
        _applyFix = applyFix;
        Title = EnumDisplayConverter.ToText(result.Check);
        _detail = result.Detail;
        Outcome = result.Outcome;
        Fix = result.Fix;
    }

    public string Title { get; }
    public DiagnosticOutcome Outcome { get; }
    public DiagnosticFix Fix { get; }

    [ObservableProperty] private string _detail;

    public bool IsOk => Outcome == DiagnosticOutcome.Ok;
    public bool IsInfo => Outcome == DiagnosticOutcome.Info;
    public bool IsWarning => Outcome == DiagnosticOutcome.Warning;
    public bool IsProblem => Outcome == DiagnosticOutcome.Problem;
    public bool IsSkipped => Outcome == DiagnosticOutcome.Skipped;
    public bool HasFix => Fix != DiagnosticFix.None && OperatingSystem.IsWindows();

    /// <summary>A plain-text verdict for the copied report (not localized: it is a marker, like a log level).</summary>
    public string OutcomeMark => Outcome switch
    {
        DiagnosticOutcome.Ok => "OK",
        DiagnosticOutcome.Info => "i",
        DiagnosticOutcome.Warning => "!",
        DiagnosticOutcome.Problem => "X",
        _ => "-",
    };

    [RelayCommand]
    private Task ApplyFixAsync() => _applyFix(this);
}
