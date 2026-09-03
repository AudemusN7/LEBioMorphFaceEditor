using System.IO;
using System.Windows;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Services;
using MorphFaceEditor.ViewModels;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.LegendaryExplorer.TextureRegistry;

namespace MorphFaceEditor;

public partial class App : Application
{
    private MainWindowViewModel? _viewModel;

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
            MorphRandomisationCatalog.LoadEmbedded());
        var window = new MainWindow(_viewModel);
        MainWindow = window;
        window.Show();
        _ = RecommendTextureDatabaseBuildAsync(textureRegistrySettings, dialogs);
    }

    private static async Task RecommendTextureDatabaseBuildAsync(
        TextureRegistrySettingsViewModel settings,
        IEditorDialogService dialogs)
    {
        try
        {
            await settings.RefreshAsync();
            var needsBuild = settings.Rows.Any(row =>
                Directory.Exists(LegendaryExplorerCoreRuntime.GetCookedPath(row.Game)) &&
                row.Status.State != TextureRegistryState.Ready);
            if (needsBuild)
            {
                dialogs.ShowTextureRegistrySettings();
            }
        }
        catch (Exception exception)
        {
            AppLog.Warning($"Texture database startup check failed: {exception.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        base.OnExit(e);
    }
}
