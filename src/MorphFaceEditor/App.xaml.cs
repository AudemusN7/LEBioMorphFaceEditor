using System.IO;
using System.Windows;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;
using MorphFaceEditor.Views;

namespace MorphFaceEditor;

public partial class App : Application
{
    private MainWindowViewModel? _viewModel;
    private ThemeViewportBinding? _themeViewport;
    internal ThemeManager? Themes { get; private set; }
    internal bool ClearApplicationDataOnExit { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Information($"Application starting; version {typeof(App).Assembly.GetName().Version}.");
        DispatcherUnhandledException += (_, args) =>
            AppLog.Error("Unhandled WPF dispatcher exception.", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Unhandled application-domain exception.",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
            AppLog.Error("Unobserved task exception.", args.Exception);
        LegendaryExplorerCoreRuntime.Initialize(TaskScheduler.FromCurrentSynchronizationContext());
        Themes = ThemeManager.CreateDefault(Resources);

#if DEBUG
        // Palette authoring is opt-in now that the reviewed themes are shipped.
        var debugPalette = Environment.GetEnvironmentVariable("MFE_THEME_DEBUG") == "1"
            ? DebugThemePalette.CreateDefault(Resources) : null;
#endif

        var sceneFactory = new HeadPreviewSceneFactory();
        var textureRegistryPaths = TextureRegistryPaths.CreateDefault();
        var textureRegistryStore = new TextureRegistryStore(textureRegistryPaths);
        var textureRegistryBuilder = new TextureRegistryBuilder(textureRegistryStore);
        var textureCatalogService = new TextureCatalogService(textureRegistryStore);
        var textureRegistrySettings = new TextureRegistrySettingsViewModel(
            textureRegistryStore, textureRegistryBuilder, textureCatalogService.Invalidate);
        var dialogs = new WpfEditorDialogService(textureRegistrySettings);
        var packageReader = new MorphFacePackageReader();
        var referenceService = new PackageReferenceService(packageReader, textureCatalogService);
        var profiles = MorphFaceProfileRegistry.CreateDefault();
        var targets = new MorphTargetCatalog();
        var packageWriter = new MorphFacePackageWriter();
        var packageContext = new MorphFacePackageContextService();
        var startupPreferences = StartupPreferencesService.CreateDefault();
        _viewModel = new MainWindowViewModel(
            dialogs,
            new MorphFaceCatalogService(profiles),
            new MorphFacePreviewLoadService(sceneFactory, targets, profiles, packageReader),
            sceneFactory,
            new WpfHdrColorDialogService(),
            referenceService,
            packageWriter,
            packageContext,
            new MorphFaceConversionService(profiles, targets, packageContext, textureCatalogService),
            new MorphFaceInterchangeService(),
            new WpfMorphFaceClipboardService(),
            MorphRandomisationCatalog.LoadEmbedded(),
            recentFiles: RecentFileService.CreateDefault(),
            startupPreferences: startupPreferences,
            detachedMeshPreviewLoadService: new DetachedMeshPreviewLoadService(
                sceneFactory,
                materialCatalog: new CustomMaterialTemplateCatalogService(packageReader, textureRegistryStore)));
        textureRegistrySettings.DatabaseChanged += _viewModel.RefreshTextureRegistry;
        _themeViewport = new ThemeViewportBinding(Themes, _viewModel);
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();
#if DEBUG
        if (debugPalette is not null)
        {
            var debugWindow = new ThemeDebugWindow(debugPalette) { Owner = window };
            void RefreshDebug(object? sender, EventArgs args) => debugPalette.Refresh();
            Themes.Changed += RefreshDebug;
            debugWindow.Closed += (_, _) => Themes.Changed -= RefreshDebug;
            debugWindow.Show();
        }
#endif
        _ = Dispatcher.BeginInvoke(() =>
            ShowFirstRunWelcome(window, startupPreferences, dialogs));
    }

    private static void ShowFirstRunWelcome(
        Window owner,
        StartupPreferencesService preferences,
        IEditorDialogService dialogs)
    {
        if (preferences.Current.SuppressWelcome)
        {
            return;
        }

        var welcome = new FirstRunWelcomeWindow { Owner = owner };
        _ = welcome.ShowDialog();
        if (welcome.DontShowAgain)
        {
            preferences.SetSuppressWelcome(true);
        }
        if (welcome.OpenTextureDatabasesRequested)
        {
            dialogs.ShowTextureRegistrySettings();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _themeViewport?.Dispose();
        Themes?.Dispose();
        if (ClearApplicationDataOnExit) AppLog.StopFileWrites();
        _viewModel?.Dispose();
        if (ClearApplicationDataOnExit)
        {
            var failures = ApplicationDataPaths.CreateDefault().Clear();
            if (failures.Count > 0)
                MessageBox.Show("Some application data could not be removed:\n\n" + string.Join("\n", failures),
                    "Application data cleanup incomplete", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        base.OnExit(e);
    }
}
