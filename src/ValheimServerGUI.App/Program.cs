using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using ValheimServerGUI.App.Infrastructure;
using ValheimServerGUI.Game;
using ValheimServerGUI.Tools;

namespace ValheimServerGUI.App;

internal static class Program
{
    // Avalonia + SynchronizationContext-reliant code must not run before AppMain; keep Main minimal.
    [STAThread]
    public static void Main(string[] args)
    {
        ConfigureCulture();
        WaitForUpdatedProcess(ref args);

        // Single-instance, multi-window: a second launch forwards its args to the primary and exits.
        var singleInstance = new SingleInstanceManager();
        if (!singleInstance.TryAcquire())
        {
            singleInstance.ForwardArgs(args);
            singleInstance.Dispose();
            return;
        }

        var services = ServiceConfiguration.BuildServiceProvider(args, singleInstance);
        BuildAvaloniaApp(services).StartWithClassicDesktopLifetime(args);
    }

    // A portable update restarts the new exe while the old process is still exiting. Wait for it to be gone, or this
    // launch would find it holding the single-instance lock and hand its arguments to a process about to exit.
    private static void WaitForUpdatedProcess(ref string[] args)
    {
        if (AppUpdater.TakeAfterUpdateArgs(ref args) is not { } pid) return;

        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    // Restart-to-switch localization: the UI culture is resolved ONCE here, before Avalonia builds any
    // control, so every resource lookup ({x:Static loc:Strings.*} / Strings.Key) binds against a single
    // culture for the process lifetime. VSG_LANG wins when set (QA: VSG_LANG=qps-ploc shows resource keys
    // in place of text); then the Language preference (File > Preferences); otherwise the OS UI culture
    // stands, and a culture without a satellite falls back to English. Only CurrentUICulture is set:
    // CurrentCulture (number/date formatting) is left alone.
    private static void ConfigureCulture()
    {
        var name = Environment.GetEnvironmentVariable("VSG_LANG");
        if (string.IsNullOrWhiteSpace(name))
        {
            IValheimPathResolver paths = OperatingSystem.IsWindows() ? new WindowsValheimPathResolver() : new LinuxValheimPathResolver();
            name = AppLanguages.ReadCultureName(paths.UserPrefsFilePath);
        }
        if (string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            var culture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // Unknown culture name: keep the OS default rather than crashing.
        }
    }

    // Composition root: the running app is constructed with the fully-built provider (see App).
    public static AppBuilder BuildAvaloniaApp(IServiceProvider services)
        => AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // Parameterless overload for the Avalonia visual designer only. The designer never runs the real
    // DI graph, so it gets an App that lazily builds a default provider (see App.Services).
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
