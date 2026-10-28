using MorphFaceEditor.Core.Services;
using MorphFaceEditor.Core.Randomisation;
using MorphFaceEditor.Core.Domain;
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
    private IdleMemoryCleanup? _idleMemoryCleanup;
    internal ThemeManager? Themes { get; private set; }
    internal bool ClearApplicationDataOnExit { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.StartSession(e.Args, typeof(App).Assembly.GetName().Version);
        foreach (var failure in MorphFacePackageWorkspace.CleanupOrphanedFiles())
            AppLog.Warning($"WORKSPACE:CLEANUP {failure}");
        Loc.Initialize(Resources, ApplicationPreferencesStore.CreateDefault().Current.Language);
        DispatcherUnhandledException += (_, args) =>
            AppLog.Error("APP:DISPATCHER-EXCEPTION", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("APP:DOMAIN-EXCEPTION",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
            AppLog.Error("APP:TASK-EXCEPTION", args.Exception);
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
        var dialogs = new WpfEditorDialogService(textureRegistrySettings, textureCatalogService);
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
        var window = new MainWindow(_viewModel, ApplicationPreferencesStore.CreateDefault());
        MainWindow = window;
        window.Show();
        _idleMemoryCleanup = new IdleMemoryCleanup(window, _viewModel, referenceService);
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
        if (preferences.SuppressWelcome)
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
        _idleMemoryCleanup?.Dispose();
        _themeViewport?.Dispose();
        Themes?.Dispose();
        if (ClearApplicationDataOnExit) AppLog.StopFileWrites();
        _viewModel?.Dispose();
        if (ClearApplicationDataOnExit)
        {
            var failures = ApplicationDataPaths.CreateDefault().Clear();
            if (failures.Count > 0)
                MessageBox.Show(Loc.Format("loc_App_CleanupFailed", string.Join("\n", failures)),
                    Loc.Text("loc_App_CleanupIncomplete"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        base.OnExit(e);
    }
}
