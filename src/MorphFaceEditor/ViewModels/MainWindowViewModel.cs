using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Editing;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.Core.Services;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Models;
using MorphFaceEditor.Presentation;
using MorphFaceEditor.Rendering;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.ViewModels;

/// <summary>Coordinates package selection, face loading, editor lifetime and verified save workflows.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly RandomisationInclusionState _randomisationInclusionState = new();
    private readonly IEditorDialogService _dialogs;
    private readonly MorphFaceCatalogService _catalogService;
    private readonly MorphFacePreviewLoadService _previewLoadService;
    private readonly HeadPreviewSceneFactory _sceneFactory;
    private readonly IHdrColorDialogService _colorDialog;
    private readonly PackageReferenceService _referenceService;
    private readonly MorphFacePackageWriter _packageWriter;
    private readonly MorphFacePackageContextService _packageContextService;
    private readonly MorphFaceConversionService _conversionService;
    private readonly MorphFaceInterchangeService _interchangeService;
    private readonly IMorphFaceClipboardService _clipboard;
    private readonly ActorAssignmentService _actorAssignmentService;
    private readonly StandalonePlayerMorphImportService _standaloneImportService;
    private readonly StandalonePlayerMeshImportService _standaloneMeshImportService;
    private readonly StandaloneLegacyHeadMorphImportService _standaloneLegacyImportService;
    private readonly DetachedMeshPreviewLoadService _detachedMeshPreviewLoadService;
    private readonly MorphRandomisationCatalog _randomisationCatalog;
    private readonly AsyncRelayCommand _openPackageCommand;
    private readonly AsyncRelayCommand _loadSelectedFaceCommand;
    private readonly AsyncRelayCommand _saveCommand;
    private readonly AsyncRelayCommand _saveMorphToPccCommand;
    private readonly RelayCommand _fixMorphCommand;
    private readonly RelayCommand _editBackgroundColorCommand;
    private readonly RelayCommand _dismissErrorCommand;
    private readonly RelayCommand _textureRegistrySettingsCommand;
    private readonly AsyncRelayCommand _cloneMorphCommand;
    private readonly AsyncRelayCommand _deleteMorphCommand;
    private readonly AsyncRelayCommand _convertMorphCommand;
    private readonly AsyncRelayCommand _copyMorphDataCommand;
    private readonly AsyncRelayCommand _pasteMorphDataCommand;
    private readonly AsyncRelayCommand _copyMaterialDataCommand;
    private readonly AsyncRelayCommand _pasteMaterialDataCommand;
    private readonly AsyncRelayCommand _importMorphCommand;
    private readonly AsyncRelayCommand _exportMorphPskCommand;
    private readonly AsyncRelayCommand _exportMorphGltfCommand;
    private readonly AsyncRelayCommand _exportMorphMd5Command;
    private readonly AsyncRelayCommand _exportMorphRonCommand;
    private readonly AsyncRelayCommand _assignMorphToActorCommand;
    private readonly AsyncRelayCommand _assignMaterialsToActorCommand;
    private MorphFacePackageWorkspace? _packageWorkspace;
    private string? _standaloneImportPath;
    private MorphFaceGame? _standaloneGame;
    private ImportedMeshAsset? _detachedMeshSource;
    private readonly HashSet<string> _fixedBakeFacePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _relativeBakeFacePaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasWorkspaceChanges;
    private CancellationTokenSource? _loadCancellation;
    private LoadedMorphFace? _loadedFace;
    private string? _packagePath;
    private string _faceSearchText = string.Empty;
    private BioMorphFaceListItem? _selectedFace;
    private FaceEditorViewModel? _editor;
    private string _status = "Open a package to begin.";
    private string? _errorMessage;
    private string? _faceDetails;
    private string? _loadedFacePath;
    private bool _isBusy;
    private bool _hasPreview;
    private bool _wireframe;
    private bool _extendedSliders;
    private bool _showAttachment = true;
    private HeadPreviewRenderMode _renderMode = HeadPreviewRenderMode.Shaded;
    private HeadPreviewLightingPreset _lightingPreset = HeadPreviewLightingPreset.Studio;
    private int _previewLod;
    private Vector4 _backgroundColor = new(0.035f, 0.043f, 0.055f, 1);
    private PackageReferenceCatalog _referenceCatalog = new([], []);
    private int _attachmentChangeVersion;
    private bool _suppressMaterialPreview;
    private IReadOnlyList<SkeletalMeshAsset> _preservedOtherMeshAssets = [];
    private string? _previewCameraFamily;
    private string? _loadedSpeciesKey;
    private string? _lastCategoryKey;
    private int _morphRandomisationStrength = 50;
    private int _materialRandomisationStrength = 50;
    private bool _randomiseMorphs = true;
    private bool _randomiseMaterials;
    private bool _cursedMode;

    public MainWindowViewModel(
        IEditorDialogService dialogs,
        MorphFaceCatalogService catalogService,
        MorphFacePreviewLoadService previewLoadService,
        HeadPreviewSceneFactory sceneFactory,
        IHdrColorDialogService colorDialog,
        PackageReferenceService referenceService,
        MorphFacePackageWriter packageWriter,
        MorphFacePackageContextService packageContextService,
        MorphFaceConversionService conversionService,
        MorphFaceInterchangeService interchangeService,
        IMorphFaceClipboardService clipboard,
        MorphRandomisationCatalog? randomisationCatalog = null,
        ActorAssignmentService? actorAssignmentService = null,
        StandalonePlayerMorphImportService? standaloneImportService = null,
        StandalonePlayerMeshImportService? standaloneMeshImportService = null,
        StandaloneLegacyHeadMorphImportService? standaloneLegacyImportService = null,
        DetachedMeshPreviewLoadService? detachedMeshPreviewLoadService = null)
    {
        _dialogs = dialogs;
        _catalogService = catalogService;
        _previewLoadService = previewLoadService;
        _sceneFactory = sceneFactory;
        _colorDialog = colorDialog;
        _referenceService = referenceService;
        _packageWriter = packageWriter;
        _packageContextService = packageContextService;
        _conversionService = conversionService;
        _interchangeService = interchangeService;
        _clipboard = clipboard;
        _actorAssignmentService = actorAssignmentService ?? new ActorAssignmentService();
        _standaloneImportService = standaloneImportService ?? new StandalonePlayerMorphImportService();
        _standaloneMeshImportService = standaloneMeshImportService ??
                                       new StandalonePlayerMeshImportService(interchangeService, packageContextService);
        _standaloneLegacyImportService = standaloneLegacyImportService ?? new StandaloneLegacyHeadMorphImportService();
        _detachedMeshPreviewLoadService = detachedMeshPreviewLoadService ?? new DetachedMeshPreviewLoadService(sceneFactory);
        _randomisationCatalog = randomisationCatalog ?? MorphRandomisationCatalog.Empty;
        _openPackageCommand = new AsyncRelayCommand(OpenPackageAsync, () => !IsBusy);
        _loadSelectedFaceCommand = new AsyncRelayCommand(
            LoadSelectedFaceCommandAsync,
            () => !IsBusy && SelectedFace is not null && WorkspacePackagePath is not null);
        _saveCommand = new AsyncRelayCommand(
            SaveExistingCommandAsync,
            () => !IsBusy && IsDirty && _packageWorkspace?.CanCommit == true);
        _saveMorphToPccCommand = new AsyncRelayCommand(
            SaveMorphToPccCommandAsync,
            () => !IsBusy && Editor is not null && _loadedFace is not null && _packageWorkspace is not null);
        _fixMorphCommand = new RelayCommand(FixMorph, () => !IsBusy && Editor?.CanFixMorph == true);
        _editBackgroundColorCommand = new RelayCommand(EditBackgroundColor);
        _dismissErrorCommand = new RelayCommand(() => ErrorMessage = null);
        _textureRegistrySettingsCommand = new RelayCommand(_dialogs.ShowTextureRegistrySettings);
        _cloneMorphCommand = new AsyncRelayCommand(CloneMorphAsync, CanMutatePackageContext);
        _deleteMorphCommand = new AsyncRelayCommand(DeleteMorphAsync, CanMutatePackageContext);
        _convertMorphCommand = new AsyncRelayCommand(ConvertMorphAsync, CanMutatePackageContext);
        _copyMorphDataCommand = new AsyncRelayCommand(CopyMorphDataAsync, CanUseFaceContextMenu);
        _pasteMorphDataCommand = new AsyncRelayCommand(PasteMorphDataAsync, CanPasteMorphData);
        _copyMaterialDataCommand = new AsyncRelayCommand(CopyMaterialDataAsync, CanUseMaterialContextMenu);
        _pasteMaterialDataCommand = new AsyncRelayCommand(PasteMaterialDataAsync, CanPasteMaterialData);
        _importMorphCommand = new AsyncRelayCommand(ImportMorphAsync, () => !IsBusy);
        _exportMorphPskCommand = new AsyncRelayCommand(
            () => ExportMorphMeshAsync(MorphMeshFormat.Psk), CanUseFaceContextMenu);
        _exportMorphGltfCommand = new AsyncRelayCommand(
            () => ExportMorphMeshAsync(MorphMeshFormat.Gltf), CanUseFaceContextMenu);
        _exportMorphMd5Command = new AsyncRelayCommand(
            () => ExportMorphMeshAsync(MorphMeshFormat.Md5), CanUseFaceContextMenu);
        _exportMorphRonCommand = new AsyncRelayCommand(ExportMorphRonAsync, CanUseFaceContextMenu);
        _assignMorphToActorCommand = new AsyncRelayCommand(AssignMorphToActorAsync, CanMutatePackageContext);
        _assignMaterialsToActorCommand = new AsyncRelayCommand(AssignMaterialsToActorAsync, CanMutatePackageContext);
        FilteredFaces = CollectionViewSource.GetDefaultView(Faces);
        FilteredFaces.Filter = item =>
            item is BioMorphFaceListItem face && BioMorphFaceSearch.Matches(face, FaceSearchText);
    }

    public event Action<HeadPreviewScene, bool>? PreviewSceneReady;
    public event Action<HeadPreviewDeformationUpdate>? PreviewDeformationReady;
    public event Action<HeadPreviewMaterialUpdate>? PreviewMaterialReady;
    public event EventHandler? PreviewOptionsChanged;

    public ObservableCollection<BioMorphFaceListItem> Faces { get; } = [];
    public ObservableCollection<PreviewLodChoice> PreviewLods { get; } = [];
    public ICollectionView FilteredFaces { get; }
    public IReadOnlyList<HeadPreviewRenderMode> RenderModes { get; } = Enum.GetValues<HeadPreviewRenderMode>();
    public IReadOnlyList<PreviewLightingChoice> LightingPresets { get; } =
    [
        new(HeadPreviewLightingPreset.Studio, "Studio"),
        new(HeadPreviewLightingPreset.HighContrast, "High Contrast"),
        new(HeadPreviewLightingPreset.WarmInterior, "Warm Interior"),
        new(HeadPreviewLightingPreset.CoolNight, "Cool Night")
    ];
    public ICommand OpenPackageCommand => _openPackageCommand;
    public ICommand LoadSelectedFaceCommand => _loadSelectedFaceCommand;
    public ICommand SaveCommand => _saveCommand;
    public ICommand SaveMorphToPccCommand => _saveMorphToPccCommand;
    public ICommand FixMorphCommand => _fixMorphCommand;
    public ICommand EditBackgroundColorCommand => _editBackgroundColorCommand;
    public ICommand DismissErrorCommand => _dismissErrorCommand;
    public ICommand TextureRegistrySettingsCommand => _textureRegistrySettingsCommand;
    public ICommand CloneMorphCommand => _cloneMorphCommand;
    public ICommand DeleteMorphCommand => _deleteMorphCommand;
    public ICommand ConvertMorphCommand => _convertMorphCommand;
    public ICommand CopyMorphDataCommand => _copyMorphDataCommand;
    public ICommand PasteMorphDataCommand => _pasteMorphDataCommand;
    public ICommand CopyMaterialDataCommand => _copyMaterialDataCommand;
    public ICommand PasteMaterialDataCommand => _pasteMaterialDataCommand;
    public ICommand ImportMorphCommand => _importMorphCommand;
    public ICommand ExportMorphPskCommand => _exportMorphPskCommand;
    public ICommand ExportMorphGltfCommand => _exportMorphGltfCommand;
    public ICommand ExportMorphMd5Command => _exportMorphMd5Command;
    public ICommand ExportMorphRonCommand => _exportMorphRonCommand;
    public ICommand AssignMorphToActorCommand => _assignMorphToActorCommand;
    public ICommand AssignMaterialsToActorCommand => _assignMaterialsToActorCommand;
    public string ConvertMorphHeader => SelectedFace?.ProfileKey.StartsWith("le3-", StringComparison.OrdinalIgnoreCase) == true
        ? "Convert to LE1/LE2 Morph…"
        : "Convert to LE3 Morph…";

    public string? PackagePath
    {
        get => _packagePath;
        private set
        {
            if (SetProperty(ref _packagePath, value))
            {
                OnPropertyChanged(nameof(PackageName));
                OnPropertyChanged(nameof(PackageDisplayName));
            }
        }
    }

    public string PackageName => _detachedMeshSource is not null
        ? $"{_standaloneGame} Detached Mesh Workspace"
        : _standaloneGame is not null
        ? $"{_standaloneGame} Standalone Player Workspace"
        : PackagePath is null ? "No package open" : Path.GetFileName(PackagePath);
    public string PackageDisplayName => IsDirty ? $"{PackageName} *" : PackageName;
    public bool IsDirty => _hasWorkspaceChanges || Editor?.IsDirty == true;
    public bool CanFixMorph => Editor?.CanFixMorph == true;
    public bool IsDetachedMeshWorkspace => _detachedMeshSource is not null;
    private string? WorkspacePackagePath => _packageWorkspace?.WorkingPath;
    private bool IsStandaloneWorkspace => _standaloneGame is not null;
    private bool IsPlayerWorkspace => _standaloneGame is not null && _packageWorkspace is not null;

    public string FaceSearchText
    {
        get => _faceSearchText;
        set
        {
            if (!SetProperty(ref _faceSearchText, value))
            {
                return;
            }

            FilteredFaces.Refresh();
            if (SelectedFace is not null && !BioMorphFaceSearch.Matches(SelectedFace, value))
            {
                SelectedFace = FilteredFaces.Cast<BioMorphFaceListItem>().FirstOrDefault();
            }
        }
    }

    public BioMorphFaceListItem? SelectedFace
    {
        get => _selectedFace;
        set
        {
            if (SetProperty(ref _selectedFace, value))
            {
                OnPropertyChanged(nameof(ConvertMorphHeader));
                _loadSelectedFaceCommand.RaiseCanExecuteChanged();
                _saveMorphToPccCommand.RaiseCanExecuteChanged();
                RaiseFaceContextCanExecuteChanged();
            }
        }
    }

    public FaceEditorViewModel? Editor
    {
        get => _editor;
        private set => SetProperty(ref _editor, value);
    }

    /// <summary>Keeps the randomiser controls stable while faces and packages are replaced.</summary>
    public int MorphRandomisationStrength
    {
        get => _morphRandomisationStrength;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _morphRandomisationStrength, clamped) && Editor is not null)
            {
                Editor.MorphRandomisationStrength = clamped;
            }
        }
    }

    /// <summary>Keeps the randomiser controls stable while faces and packages are replaced.</summary>
    public int MaterialRandomisationStrength
    {
        get => _materialRandomisationStrength;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (SetProperty(ref _materialRandomisationStrength, clamped) && Editor is not null)
            {
                Editor.MaterialRandomisationStrength = clamped;
            }
        }
    }

    public bool RandomiseMorphs
    {
        get => _randomiseMorphs;
        set
        {
            if (SetProperty(ref _randomiseMorphs, value) && Editor is not null)
            {
                Editor.RandomiseMorphs = value;
            }
        }
    }

    public bool RandomiseMaterials
    {
        get => _randomiseMaterials;
        set
        {
            if (SetProperty(ref _randomiseMaterials, value) && Editor is not null)
            {
                Editor.RandomiseMaterials = value;
            }
        }
    }

    public bool CursedMode
    {
        get => _cursedMode;
        set
        {
            if (SetProperty(ref _cursedMode, value) && Editor is not null)
            {
                Editor.CursedMode = value;
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string? FaceDetails
    {
        get => _faceDetails;
        private set => SetProperty(ref _faceDetails, value);
    }

    public string? LoadedFacePath
    {
        get => _loadedFacePath;
        private set => SetProperty(ref _loadedFacePath, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                _openPackageCommand.RaiseCanExecuteChanged();
                _loadSelectedFaceCommand.RaiseCanExecuteChanged();
                _saveCommand.RaiseCanExecuteChanged();
                _saveMorphToPccCommand.RaiseCanExecuteChanged();
                _fixMorphCommand.RaiseCanExecuteChanged();
                RaiseFaceContextCanExecuteChanged();
            }
        }
    }

    public bool HasPreview
    {
        get => _hasPreview;
        private set => SetProperty(ref _hasPreview, value);
    }

    public HeadPreviewRenderMode RenderMode
    {
        get => _renderMode;
        set
        {
            if (SetProperty(ref _renderMode, value))
            {
                PreviewOptionsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool Wireframe
    {
        get => _wireframe;
        set
        {
            if (SetProperty(ref _wireframe, value))
            {
                PreviewOptionsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool ExtendedSliders
    {
        get => _extendedSliders;
        set
        {
            if (SetProperty(ref _extendedSliders, value))
            {
                _colorDialog.ExtendedSliders = value;
                Editor?.SetExtendedSliders(value);
            }
        }
    }

    public bool ShowAttachment
    {
        get => _showAttachment;
        set
        {
            if (SetProperty(ref _showAttachment, value))
            {
                PreviewOptionsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public HeadPreviewLightingPreset LightingPreset
    {
        get => _lightingPreset;
        set
        {
            if (SetProperty(ref _lightingPreset, value))
            {
                PreviewOptionsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public int PreviewLod
    {
        get => _previewLod;
        set
        {
            if (!SetProperty(ref _previewLod, value) || Editor is null || _loadedFace is null)
            {
                return;
            }
            try
            {
                Editor.SetPreviewLod(value);
                PreviewSceneReady?.Invoke(CreateCurrentPreviewScene(), false);
                Status = Editor.HasMorphGeometryAtLod(value)
                    ? $"Previewing LOD {value}."
                    : $"Previewing LOD {value}; the game has no authored morph-target geometry for this LOD, so morph sliders are disabled.";
            }
            catch (Exception exception)
            {
                SetPreviewError($"LOD {value} could not be previewed: {exception.Message}");
            }
        }
    }

    public Vector4 BackgroundColor
    {
        get => _backgroundColor;
        private set
        {
            value = Vector4.Clamp(value, Vector4.Zero, Vector4.One);
            value.W = 1;
            if (SetProperty(ref _backgroundColor, value))
            {
                OnPropertyChanged(nameof(BackgroundPreviewBrush));
                PreviewOptionsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public Brush BackgroundPreviewBrush => new SolidColorBrush(Color.FromRgb(
        ToColorByte(BackgroundColor.X),
        ToColorByte(BackgroundColor.Y),
        ToColorByte(BackgroundColor.Z)));

    public HeadPreviewOptions CreatePreviewOptions() => new(
        RenderMode,
        Wireframe,
        ShowAttachment,
        LightingPreset,
        new Vector3(BackgroundColor.X, BackgroundColor.Y, BackgroundColor.Z));

    private void EditBackgroundColor()
    {
        var before = BackgroundColor;
        var applied = _colorDialog.EditStandard(
            "Preview background colour",
            before,
            value => BackgroundColor = value);
        BackgroundColor = applied ?? before;
    }

    private static byte ToColorByte(float value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue);

    public void SetFrameStatus(HeadPreviewFrame frame) =>
        Status = $"{frame.DeviceKind} · {frame.Width}×{frame.Height} · {frame.DrawCalls} sections · {frame.RenderTime.TotalMilliseconds:F1} ms";

    public void SetPreviewError(string message)
    {
        ErrorMessage = message;
        Status = "Preview unavailable.";
    }

    private void SetEditorError(string message)
    {
        ErrorMessage = message;
        Status = message;
    }

    private async Task OpenPackageAsync()
    {
        var selectedPath = _dialogs.ChoosePackage(PackagePath is null ? null : Path.GetDirectoryName(PackagePath));
        if (selectedPath is null)
        {
            return;
        }

        if (await EnsureCanAbandonWorkspaceAsync())
        {
            await OpenSourcePackagePathAsync(selectedPath);
        }
    }

    internal bool CanOpenDroppedFile(string path) =>
        !IsBusy && EditorFileDrop.Classify(path) switch
        {
            EditorFileDropKind.Package => true,
            EditorFileDropKind.MorphImport => true,
            _ => false
        };

    internal async Task OpenDroppedFileAsync(string path)
    {
        if (!CanOpenDroppedFile(path))
        {
            return;
        }

        if (EditorFileDrop.Classify(path) == EditorFileDropKind.Package)
        {
            if (await EnsureCanAbandonWorkspaceAsync())
            {
                await OpenSourcePackagePathAsync(path);
            }
            return;
        }

        await ImportMorphAsync(path);
    }

    private async Task<bool> OpenSourcePackagePathAsync(string selectedPath, string? preferredFacePath = null)
    {
        AppLog.Information($"Creating temporary workspace for '{selectedPath}'.");
        MorphFacePackageWorkspace workspace;
        try
        {
            workspace = await Task.Run(() => new MorphFacePackageWorkspace(selectedPath));
        }
        catch (Exception exception)
        {
            AppLog.Error($"Package workspace creation failed for '{selectedPath}'.", exception);
            ErrorMessage = $"Package could not be opened: {exception.Message}";
            Status = "Package load failed.";
            return false;
        }

        CancelPendingLoad();
        SetEditor(null, null);
        DisposePackageWorkspace();
        _packageWorkspace = workspace;
        _standaloneImportPath = null;
        _standaloneGame = null;
        SetDetachedMeshSource(null);
        _fixedBakeFacePaths.Clear();
        _relativeBakeFacePaths.Clear();
        _hasWorkspaceChanges = false;
        PackagePath = workspace.SourcePath;
        OnDirtyStateChanged();
        return await RefreshWorkspaceAsync(preferredFacePath, clearSearch: true);
    }

    private async Task<bool> RefreshWorkspaceAsync(
        string? preferredFacePath = null,
        bool clearSearch = false)
    {
        var workspacePath = WorkspacePackagePath;
        if (workspacePath is null)
        {
            return false;
        }

        AppLog.Information(
            $"Refreshing package workspace '{workspacePath}' (preferred face: {preferredFacePath ?? "<first>"}).");
        CancelPendingLoad();
        SetEditor(null, null);
        _referenceService.InvalidatePackage(workspacePath);
        IsBusy = true;
        ErrorMessage = null;
        HasPreview = false;
        FaceDetails = null;
        LoadedFacePath = null;
        Status = "Reading BioMorphFace exports…";
        var opened = false;
        try
        {
            var catalogTask = _catalogService.ReadAsync(workspacePath);
            var referencesTask = _referenceService.ReadCatalogAsync(workspacePath);
            await Task.WhenAll(catalogTask, referencesTask);
            var catalog = await catalogTask;
            _referenceCatalog = await referencesTask;
            if (clearSearch)
            {
                FaceSearchText = string.Empty;
            }
            Faces.Clear();
            foreach (var face in catalog.Faces)
            {
                Faces.Add(face);
            }
            SelectedFace = preferredFacePath is null
                ? Faces.FirstOrDefault()
                : Faces.FirstOrDefault(face => string.Equals(
                      face.InstancedPath,
                      preferredFacePath,
                      StringComparison.OrdinalIgnoreCase)) ?? Faces.FirstOrDefault();
            Status = Faces.Count == 0
                ? "This package contains no non-default BioMorphFace exports."
                : $"Found {Faces.Count:N0} BioMorphFace export{(Faces.Count == 1 ? string.Empty : "s")}.";
            AppLog.Information($"Package workspace catalogue loaded: '{catalog.PackagePath}', {Faces.Count} face(s).");
            opened = true;
        }
        catch (Exception exception)
        {
            AppLog.Error($"Package workspace refresh failed for '{workspacePath}'.", exception);
            ErrorMessage = $"Package could not be opened: {exception.Message}";
            Status = "Package load failed.";
        }
        finally
        {
            IsBusy = false;
        }

        if (!opened)
        {
            return false;
        }
        if (SelectedFace is not null)
        {
            return await LoadSelectedFaceAsync(confirmUnsavedChanges: false);
        }
        return true;
    }

    private async Task LoadSelectedFaceCommandAsync() =>
        _ = await LoadSelectedFaceAsync(confirmUnsavedChanges: true);

    private async Task<bool> LoadSelectedFaceAsync(bool confirmUnsavedChanges)
    {
        var workspacePath = WorkspacePackagePath;
        if (workspacePath is null || SelectedFace is null)
        {
            return false;
        }
        var requestedFacePath = SelectedFace.InstancedPath;
        var requestedFaceUIndex = SelectedFace.UIndex;
        if (confirmUnsavedChanges && !await EnsureCanAbandonEditorChangesAsync())
        {
            RestoreLoadedFaceSelection();
            return false;
        }
        SelectedFace = Faces.FirstOrDefault(face => face.UIndex == requestedFaceUIndex);
        if (SelectedFace is null)
        {
            ErrorMessage = $"BioMorphFace '{requestedFacePath}' is no longer present in the open PCC.";
            return false;
        }

        CancelPendingLoad();
        SetEditor(null, null);
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;
        IsBusy = true;
        ErrorMessage = null;
        Status = $"Loading {SelectedFace.DisplayName}…";
        AppLog.Information($"Loading face '{SelectedFace.InstancedPath}' from temporary workspace '{workspacePath}'.");
        try
        {
            var result = await _previewLoadService.LoadAsync(
                workspacePath,
                SelectedFace.UIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken,
                _relativeBakeFacePaths.Contains(SelectedFace.InstancedPath)
                    ? MorphFaceGeometryMode.RelativeBake
                    : _fixedBakeFacePaths.Contains(SelectedFace.InstancedPath)
                        ? MorphFaceGeometryMode.FixedBake
                        : null);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var warning in result.Loaded.Warnings)
            {
                AppLog.Warning(warning);
            }
            var textureCatalogProfile = TextureCatalogProfiles.For(result.Profile);
            var topology = result.Loaded.BaseHead.Topology;
            var oracle = result.EditingSession.Evaluation.OriginalOracleReport;
            var materialOverrides = result.Loaded.Document.MaterialOverrides;
            var selectableTextures = IsPlayerWorkspace
                ? PlayerWorkspaceReferencePolicy.SelectPackageAssets(_referenceCatalog.Textures)
                : _referenceCatalog.Textures;
            var selectableMeshes = IsPlayerWorkspace
                ? PlayerWorkspaceReferencePolicy.SelectPackageAssets(_referenceCatalog.SkeletalMeshes)
                : _referenceCatalog.SkeletalMeshes;
            var displayedFinalBoneCount = result.Profile.IgnoresAuthoredGeometry
                ? 0
                : result.Loaded.Document.FinalSkeleton.Count;
            FaceDetails = $"{topology.VertexCount:N0} vertices · {topology.IndexCount / 3:N0} triangles · " +
                          $"{topology.Sections.Count} sections · {displayedFinalBoneCount} final bones · " +
                          $"{materialOverrides.Scalars.Count}/{materialOverrides.Vectors.Count}/{materialOverrides.Textures.Count} material S/V/T · " +
                          (result.EditingSession.GeometryMode == MorphFaceGeometryMode.RelativeBake
                              ? "relative imported bake"
                              : result.EditingSession.GeometryMode == MorphFaceGeometryMode.FixedBake
                                  ? "fixed imported bake"
                              : result.EditingSession.CanEdit
                              ? $"oracle {oracle!.MaximumError:G4} max"
                              : result.Profile.IgnoresAuthoredGeometry
                                  ? "base-head material-only preview"
                              : $"baked fallback · {result.EditingSession.EditBlockReason}");
            LoadedFacePath = result.Loaded.Document.Source.InstancedPath;
            var editor = new FaceEditorViewModel(
                result.EditingSession,
                result.Profile.UiProfile,
                result.MaterialEditingSession,
                _colorDialog,
                _referenceService,
                workspacePath,
                selectableTextures,
                selectableMeshes,
                result.Loaded.Document.HairMeshReference,
                result.Loaded.Document.OtherMeshReferences,
                SetEditorError,
                result.Profile.Key,
                _randomisationCatalog,
                randomisationInclusionState: _randomisationInclusionState,
                registryTextureCandidates: [],
                textureCatalogProfile: textureCatalogProfile,
                isTextureRegistryAvailable: false,
                ignoresAuthoredGeometry: result.Profile.IgnoresAuthoredGeometry);
            var speciesKey = PreviewCameraGrouping.SpeciesForProfile(result.Profile.Key);
            if (string.Equals(_loadedSpeciesKey, speciesKey, StringComparison.OrdinalIgnoreCase))
            {
                editor.SelectCategory(_lastCategoryKey);
            }
            else
            {
                _lastCategoryKey = null;
            }
            _loadedSpeciesKey = speciesKey;
            SetEditor(editor, result.Loaded);
            HasPreview = true;
            var cameraFamily = PreviewCameraGrouping.ForProfile(result.Profile.Key);
            var resetCameraPosition = _previewCameraFamily is not null &&
                                      !string.Equals(_previewCameraFamily, cameraFamily, StringComparison.OrdinalIgnoreCase);
            _previewCameraFamily = cameraFamily;
            PreviewSceneReady?.Invoke(result.Scene, resetCameraPosition);
            Status = result.EditingSession.GeometryMode == MorphFaceGeometryMode.RelativeBake
                ? $"Loaded {SelectedFace.DisplayName} ({result.Profile.DisplayName}); authored geometry preserved with relative morph, bone and material editing ready."
                : result.EditingSession.GeometryMode == MorphFaceGeometryMode.FixedBake
                    ? $"Loaded {SelectedFace.DisplayName} ({result.Profile.DisplayName}); fixed-bake geometry, bone and material editing ready. Morph sliders are preserved but disabled."
                : result.EditingSession.CanEdit
                ? $"Loaded {SelectedFace.DisplayName} ({result.Profile.DisplayName}); live geometry and material editing ready."
                : result.Profile.IgnoresAuthoredGeometry
                    ? $"Loaded {SelectedFace.DisplayName} ({result.Profile.DisplayName}); material editing ready with base-head preview. " +
                      result.EditingSession.EditBlockReason
                : $"Loaded {SelectedFace.DisplayName} ({result.Profile.DisplayName}); material editing ready with baked geometry fallback. " +
                  result.EditingSession.EditBlockReason;
            AppLog.Information(
                $"Face loaded: '{LoadedFacePath}', editable={result.EditingSession.CanEdit}, " +
                $"vertices={topology.VertexCount}, targets={result.EditingSession.Evaluation.Resolution.WeightedTargets.Count}.");
            _ = LoadTextureRegistryAsync(
                editor,
                result.Loaded.Game,
                result.Profile,
                textureCatalogProfile,
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            Status = "Load cancelled.";
            return false;
        }
        catch (Exception exception)
        {
            AppLog.Error($"Face load failed for '{requestedFacePath}' in '{workspacePath}'.", exception);
            HasPreview = false;
            SetEditor(null, null);
            ErrorMessage = $"Face could not be loaded: {exception.Message}";
            Status = "Face load failed.";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadTextureRegistryAsync(
        FaceEditorViewModel editor,
        MorphFaceGame game,
        MorphFaceProfile profile,
        TextureCatalogProfile catalogProfile,
        CancellationToken cancellationToken)
    {
        try
        {
            AppLog.Information($"Texture registry load started for {profile.DisplayName} ({game}).");
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var catalog = await _referenceService.ReadTextureCatalogAsync(game, cancellationToken);
            if (cancellationToken.IsCancellationRequested || Editor != editor)
            {
                return;
            }

            var candidates = IsPlayerWorkspace
                ? PlayerWorkspaceReferencePolicy.SelectRegistryTextures(catalog.Candidates)
                : catalog.Candidates;
            editor.UpdateRegistryTextureCandidates(
                candidates,
                catalogProfile,
                catalog.IsAvailable);
            AppLog.Information(catalog.IsAvailable
                ? $"Texture registry loaded for {profile.DisplayName}: {catalog.Candidates.Count:N0} verified candidates in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F1}s."
                : $"Texture registry is unavailable for {profile.DisplayName}; build the {game} registry in Texture Databases.");
        }
        catch (OperationCanceledException)
        {
            AppLog.Information($"Texture registry load cancelled for {profile.DisplayName} ({game}).");
        }
        catch (Exception exception)
        {
            AppLog.Warning($"Texture registry load failed for {profile.DisplayName} ({game}): {exception.Message}");
        }
    }

    private void SetEditor(FaceEditorViewModel? editor, LoadedMorphFace? loaded)
    {
        if (Editor is not null)
        {
            _lastCategoryKey = Editor.SelectedCategory?.Key;
            Editor.PreviewChanged -= OnEditorPreviewChanged;
            Editor.MaterialPreviewChanged -= OnMaterialPreviewChanged;
            Editor.DirtyStateChanged -= OnEditorDirtyStateChanged;
            Editor.PropertyChanged -= OnEditorPropertyChanged;
            foreach (var attachment in Editor.AttachmentMeshes)
            {
                attachment.SelectionChanged -= OnAttachmentMeshChanged;
            }
            Editor.Dispose();
        }
        _loadedFace = loaded;
        _preservedOtherMeshAssets = loaded?.OtherMeshes.Skip(1).ToArray() ?? [];
        Editor = editor;
        if (Editor is not null)
        {
            Editor.MorphRandomisationStrength = MorphRandomisationStrength;
            Editor.MaterialRandomisationStrength = MaterialRandomisationStrength;
            Editor.RandomiseMorphs = RandomiseMorphs;
            Editor.RandomiseMaterials = RandomiseMaterials;
            Editor.CursedMode = CursedMode;
            if (_randomiseMorphs != Editor.RandomiseMorphs)
            {
                _randomiseMorphs = Editor.RandomiseMorphs;
                OnPropertyChanged(nameof(RandomiseMorphs));
            }
        }
        Editor?.SetExtendedSliders(ExtendedSliders);
        PreviewLods.Clear();
        if (loaded is not null)
        {
            foreach (var lod in loaded.BaseHead.AvailableLods
                         .Where(lod => editor is null || editor.AvailableLodIndices.Contains(lod.LodIndex))
                         .OrderBy(lod => lod.LodIndex))
            {
                var staticSuffix = editor?.HasMorphGeometryAtLod(lod.LodIndex) == false
                    ? " (static)"
                    : string.Empty;
                PreviewLods.Add(new PreviewLodChoice(lod.LodIndex, $"LOD {lod.LodIndex}{staticSuffix}"));
            }
        }
        _previewLod = PreviewLods.FirstOrDefault()?.LodIndex ?? 0;
        Editor?.SetPreviewLod(_previewLod);
        OnPropertyChanged(nameof(PreviewLod));
        if (Editor is not null)
        {
            Editor.PreviewChanged += OnEditorPreviewChanged;
            Editor.MaterialPreviewChanged += OnMaterialPreviewChanged;
            Editor.DirtyStateChanged += OnEditorDirtyStateChanged;
            Editor.PropertyChanged += OnEditorPropertyChanged;
            foreach (var attachment in Editor.AttachmentMeshes)
            {
                attachment.SelectionChanged += OnAttachmentMeshChanged;
            }
        }
        OnDirtyStateChanged();
        _saveMorphToPccCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanFixMorph));
        _fixMorphCommand.RaiseCanExecuteChanged();
    }

    private void OnEditorDirtyStateChanged(object? sender, EventArgs e) => OnDirtyStateChanged();

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not FaceEditorViewModel editor) return;
        switch (e.PropertyName)
        {
            case nameof(FaceEditorViewModel.RandomiseMorphs):
                RandomiseMorphs = editor.RandomiseMorphs;
                break;
            case nameof(FaceEditorViewModel.RandomiseMaterials):
                RandomiseMaterials = editor.RandomiseMaterials;
                break;
            case nameof(FaceEditorViewModel.MorphRandomisationStrength):
                MorphRandomisationStrength = editor.MorphRandomisationStrength;
                break;
            case nameof(FaceEditorViewModel.MaterialRandomisationStrength):
                MaterialRandomisationStrength = editor.MaterialRandomisationStrength;
                break;
            case nameof(FaceEditorViewModel.CursedMode):
                CursedMode = editor.CursedMode;
                break;
            case nameof(FaceEditorViewModel.CanEdit):
            case nameof(FaceEditorViewModel.CanEditMorphFeatures):
            case nameof(FaceEditorViewModel.CanEditBones):
            case nameof(FaceEditorViewModel.CanFixMorph):
                OnPropertyChanged(nameof(CanFixMorph));
                _fixMorphCommand.RaiseCanExecuteChanged();
                break;
        }
    }

    private void OnDirtyStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(PackageDisplayName));
        OnPropertyChanged(nameof(CanFixMorph));
        _saveCommand.RaiseCanExecuteChanged();
        _fixMorphCommand.RaiseCanExecuteChanged();
    }

    private void FixMorph()
    {
        if (Editor is not { CanFixMorph: true } editor || _loadedFace is null)
        {
            return;
        }

        ErrorMessage = null;
        try
        {
            AppLog.Information($"Repairing baked morph geometry for '{LoadedFacePath}'.");
            editor.FixMorph();
        }
        catch (Exception exception)
        {
            if (editor.HasPendingRepair)
            {
                ReportPostRepairPreviewFailure(editor, exception);
                return;
            }
            AppLog.Error($"Morph repair failed for '{LoadedFacePath}'.", exception);
            ErrorMessage = $"The morph could not be repaired: {exception.Message}";
            Status = "Morph repair failed; the face was not changed.";
            return;
        }

        var report = editor.Evaluation.OriginalOracleReport;
        SetGeometryDetails($"oracle repaired · {report?.MaximumError ?? 0:G4} max · unsaved");
        try
        {
            PreviewSceneReady?.Invoke(CreateCurrentPreviewScene(), false);
        }
        catch (Exception exception)
        {
            ReportPostRepairPreviewFailure(editor, exception);
            return;
        }
        Status = $"Fixed {SelectedFace?.DisplayName ?? "morph"}; canonical geometry restored. Save to commit the repair.";
        AppLog.Information(
            $"Baked morph geometry repaired for '{LoadedFacePath}'; " +
            $"post-repair oracle maximum {report?.MaximumError ?? 0:G9}.");
    }

    private void ReportPostRepairPreviewFailure(FaceEditorViewModel editor, Exception exception)
    {
        var report = editor.Evaluation.OriginalOracleReport;
        SetGeometryDetails($"oracle repaired · {report?.MaximumError ?? 0:G4} max · unsaved");
        AppLog.Error(
            $"Baked morph geometry was repaired for '{LoadedFacePath}', but the preview refresh failed.",
            exception);
        ErrorMessage = $"The morph was repaired, but the preview could not be refreshed: {exception.Message}";
        Status = "Morph repaired; unsaved changes retained, but preview refresh failed.";
    }

    private void MarkWorkspaceChanged()
    {
        _hasWorkspaceChanges = true;
        OnDirtyStateChanged();
    }

    private void OnEditorPreviewChanged(object? sender, EventArgs e)
    {
        if (Editor is not null && _loadedFace is not null)
        {
            if (Editor.UsesLiveDeformationPreview)
            {
                if (Editor.CanEditMorphFeatures &&
                    FaceDetails?.Contains("baked fallback", StringComparison.Ordinal) == true)
                {
                    var report = Editor.Evaluation.OriginalOracleReport;
                    SetGeometryDetails($"oracle repaired · {report?.MaximumError ?? 0:G4} max · unsaved");
                    Status = "Morph repair restored; save to commit the repaired geometry.";
                }
                PreviewDeformationReady?.Invoke(
                    _sceneFactory.CreateUpdate(_loadedFace, Editor.Evaluation, PreviewLod));
            }
            else
            {
                PreviewSceneReady?.Invoke(CreateCurrentPreviewScene(), false);
                if (Editor.CanFixMorph)
                {
                    SetGeometryDetails($"baked fallback · {Editor.EditBlockReason}");
                    Status = "Morph repair undone; original baked geometry restored.";
                }
            }
        }
    }

    private void SetGeometryDetails(string geometryDetails)
    {
        var prefix = FaceDetails;
        if (!string.IsNullOrEmpty(prefix))
        {
            var marker = new[] { " · baked fallback", " · oracle repaired", " · oracle " }
                .Select(value => prefix.IndexOf(value, StringComparison.Ordinal))
                .Where(index => index >= 0)
                .DefaultIfEmpty(-1)
                .Min();
            if (marker >= 0)
            {
                prefix = prefix[..marker];
            }
            else if (prefix.StartsWith("baked fallback", StringComparison.Ordinal) ||
                     prefix.StartsWith("oracle ", StringComparison.Ordinal))
            {
                prefix = null;
            }
        }
        FaceDetails = string.IsNullOrEmpty(prefix)
            ? geometryDetails
            : $"{prefix} · {geometryDetails}";
    }

    private void OnMaterialPreviewChanged(object? sender, EventArgs e)
    {
        if (!_suppressMaterialPreview && Editor is not null && _loadedFace is not null)
        {
            var materials = CreatePreviewMaterials(_loadedFace, Editor.Material.Materials);
            PreviewMaterialReady?.Invoke(_sceneFactory.CreateMaterialUpdate(_loadedFace, materials));
        }
    }

    private async void OnAttachmentMeshChanged(object? sender, EventArgs e)
    {
        if (Editor is null || _loadedFace is null ||
            sender is not HairMeshEditorViewModel changedSlot ||
            !IsDetachedMeshWorkspace && WorkspacePackagePath is null)
        {
            return;
        }

        var version = ++_attachmentChangeVersion;
        var editor = Editor;
        var loadedFace = _loadedFace;
        var packagePath = WorkspacePackagePath;
        var detachedPreview = IsDetachedMeshWorkspace;
        var references = editor.AttachmentMeshes.Select(mesh => mesh.Value).ToArray();
        AttachmentMaterialState? materialState = null;
        try
        {
            var attachments = new LoadedAttachment?[references.Length];
            for (var index = 0; index < references.Length; index++)
            {
                if (references[index] is not { } reference)
                {
                    continue;
                }
                attachments[index] = detachedPreview
                    ? await _referenceService.LoadDetachedAttachmentAsync(
                        reference.PackagePath, reference.InstancedPath)
                    : await _referenceService.LoadAttachmentAsync(
                        packagePath!, loadedFace.Document.Source.InstancedPath, reference.InstancedPath);
            }

            if (version != _attachmentChangeVersion || Editor != editor || _loadedFace != loadedFace)
            {
                return;
            }

            var preservedOtherMeshes = _preservedOtherMeshAssets;
            var attachmentMaterials = attachments
                .Where(attachment => attachment is not null)
                .Select(attachment => attachment!.Materials)
                .Append(ResolveMeshMaterials(preservedOtherMeshes, loadedFace.Materials))
                .Aggregate(
                    new ResolvedHeadMaterialSet(
                        new Dictionary<string, ResolvedHeadMaterial>(StringComparer.OrdinalIgnoreCase)),
                    MergeMaterials);
            var otherMeshes = attachments.Skip(1)
                .Where(attachment => attachment is not null)
                .Select(attachment => attachment!.Mesh)
                .Concat(preservedOtherMeshes)
                .ToArray();
            var stagedMaterials = detachedPreview
                ? MergeMaterials(
                    editor.Material.Materials,
                    ApplyDetachedAttachmentHairColour(attachmentMaterials, editor.Material.Materials))
                : MergeMaterials(loadedFace.Materials, attachmentMaterials);
            var stagedLoadedFace = loadedFace with
            {
                HairMesh = attachments[0]?.Mesh,
                OtherMeshes = otherMeshes,
                Materials = stagedMaterials,
                Document = loadedFace.Document with
                {
                    HairMeshReference = references[0],
                    OtherMeshReferences = references.Skip(1)
                        .Concat(loadedFace.Document.OtherMeshReferences.Skip(1))
                        .ToArray()
                }
            };

            // Validate all mesh topology/skinning before touching the live material
            // session. A rejected attachment must leave the old preview intact.
            _ = editor.UsesLiveDeformationPreview
                ? _sceneFactory.CreateEditable(stagedLoadedFace, editor.Evaluation, PreviewLod)
                : _sceneFactory.Create(stagedLoadedFace, PreviewLod);

            _suppressMaterialPreview = true;
            if (!detachedPreview)
            {
                materialState = editor.Material.CaptureAttachmentState();
                editor.Material.ReplaceAttachmentMaterials(
                    attachmentMaterials,
                    attachments.ElementAtOrDefault(changedSlot.SlotIndex)?.Materials);
            }
            var committedLoadedFace = detachedPreview
                ? stagedLoadedFace
                : stagedLoadedFace with { Materials = editor.Material.Materials };
            var committedScene = editor.UsesLiveDeformationPreview
                ? _sceneFactory.CreateEditable(committedLoadedFace, editor.Evaluation, PreviewLod)
                : _sceneFactory.Create(committedLoadedFace, PreviewLod);
            _loadedFace = committedLoadedFace;
            PreviewSceneReady?.Invoke(committedScene, false);
            Status = changedSlot.Value is null
                ? $"{changedSlot.Label} cleared."
                : $"{changedSlot.Label} changed to {changedSlot.Value.InstancedPath}.";
        }
        catch (Exception exception)
        {
            if (version == _attachmentChangeVersion)
            {
                if (materialState is not null)
                {
                    editor.Material.RestoreAttachmentState(materialState);
                }
                AppLog.Error($"Attachment change failed for {changedSlot.Label}.", exception);
                SetEditorError($"Attachment mesh could not be changed: {exception.Message}");
            }
        }
        finally
        {
            _suppressMaterialPreview = false;
        }
    }

    private HeadPreviewScene CreateCurrentPreviewScene()
    {
        var editor = Editor ?? throw new InvalidOperationException("No face is loaded.");
        var loaded = _loadedFace ?? throw new InvalidOperationException("No face is loaded.");
        var previewSource = loaded with { Materials = CreatePreviewMaterials(loaded, editor.Material.Materials) };
        return editor.UsesLiveDeformationPreview
            ? _sceneFactory.CreateEditable(previewSource, editor.Evaluation, PreviewLod)
            : _sceneFactory.Create(previewSource, PreviewLod);
    }

    private ResolvedHeadMaterialSet CreatePreviewMaterials(
        LoadedMorphFace loaded,
        ResolvedHeadMaterialSet editorMaterials)
    {
        if (!IsDetachedMeshWorkspace)
        {
            return editorMaterials;
        }

        IEnumerable<SkeletalMeshAsset> attachmentMeshes = loaded.HairMesh is null
            ? loaded.OtherMeshes
            : Enumerable.Repeat(loaded.HairMesh, 1).Concat(loaded.OtherMeshes);
        var attachmentMaterials = ResolveMeshMaterials(attachmentMeshes, loaded.Materials);
        return MergeMaterials(
            editorMaterials,
            ApplyDetachedAttachmentHairColour(attachmentMaterials, editorMaterials));
    }

    private static ResolvedHeadMaterialSet MergeMaterials(
        ResolvedHeadMaterialSet left,
        ResolvedHeadMaterialSet right)
    {
        var merged = new Dictionary<string, ResolvedHeadMaterial>(left.Materials, StringComparer.OrdinalIgnoreCase);
        foreach (var material in right.Materials)
        {
            merged[material.Key] = material.Value;
        }
        return new ResolvedHeadMaterialSet(merged);
    }

    private static ResolvedHeadMaterialSet ResolveMeshMaterials(
        IEnumerable<SkeletalMeshAsset> meshes,
        ResolvedHeadMaterialSet materials)
    {
        var keys = meshes
            .Where(mesh => mesh.RenderData is not null)
            .SelectMany(mesh => mesh.RenderData!.MaterialSlots)
            .Where(identity => identity is not null)
            .Cast<AssetIdentity>()
            .Select(MaterialIdentityKey.Create)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ResolvedHeadMaterialSet(materials.Materials
            .Where(pair => keys.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
    }

    private static ResolvedHeadMaterialSet ApplyDetachedAttachmentHairColour(
        ResolvedHeadMaterialSet attachmentMaterials,
        ResolvedHeadMaterialSet editorMaterials)
    {
        const string parameter = "HED_Hair_Colour_Vector";
        var selected = editorMaterials.Materials.Values
            .Where(material => material.Supports(parameter, MaterialParameterKind.Vector))
            .Select(material => material.Vectors.TryGetValue(parameter, out var value)
                ? (Found: true, Value: value)
                : (Found: false, Value: default(System.Numerics.Vector4)))
            .FirstOrDefault(value => value.Found);
        if (!selected.Found)
        {
            return attachmentMaterials;
        }

        return new ResolvedHeadMaterialSet(attachmentMaterials.Materials.Values
            .Select(material =>
            {
                if (material.Family is not (HeadMaterialFamily.Hair or HeadMaterialFamily.MaskedHair) ||
                    !material.Supports(parameter, MaterialParameterKind.Vector))
                {
                    return material;
                }
                var vectors = new Dictionary<string, System.Numerics.Vector4>(
                    material.Vectors, StringComparer.OrdinalIgnoreCase)
                {
                    [parameter] = selected.Value
                };
                return material with { Vectors = vectors };
            })
            .ToDictionary(material => material.Key, StringComparer.OrdinalIgnoreCase));
    }

    private async Task SaveExistingCommandAsync() =>
        _ = await CommitWorkspaceAsync(showConfirmation: true);

    private async Task<bool> FlushEditorToWorkspaceAsync()
    {
        if (Editor is null || _loadedFace is null || WorkspacePackagePath is null)
        {
            return false;
        }
        if (!Editor.IsDirty)
        {
            return true;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = "Updating the temporary package workspace…";
        try
        {
            AppLog.Information($"Flushing face '{LoadedFacePath}' to temporary workspace '{WorkspacePackagePath}'.");
            var result = await Task.Run(() => _packageWriter.SaveExisting(Editor.CreateDraft()));
            MarkWorkspaceChanged();
            var reopened = await RefreshWorkspaceAsync(result.FaceInstancedPath);
            if (!reopened)
            {
                ErrorMessage = "Temporary edits were retained, but the refreshed workspace could not be opened.";
                Status = "Workspace updated; reload failed.";
                return false;
            }
            Status = $"Temporary edits updated for {result.FaceInstancedPath}.";
            AppLog.Information($"Temporary face update verified: '{result.FaceInstancedPath}'.");
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error($"Temporary face update failed for '{LoadedFacePath}'.", exception);
            ErrorMessage = $"Temporary edits could not be updated: {exception.Message}";
            Status = "Workspace update failed; the source PCC was not modified.";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> CommitWorkspaceAsync(bool showConfirmation)
    {
        if (_packageWorkspace is null || PackagePath is null)
        {
            return false;
        }
        if (!_packageWorkspace.CanCommit)
        {
            ErrorMessage = "Standalone imports cannot overwrite the installed player template package. Use Save Morph to PCC instead.";
            Status = "Choose Save Morph to PCC for this standalone import.";
            return false;
        }
        if (Editor?.IsDirty == true && !await FlushEditorToWorkspaceAsync())
        {
            return false;
        }
        if (!_hasWorkspaceChanges)
        {
            return true;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = "Committing and verifying package changes…";
        try
        {
            await Task.Run(_packageWorkspace.Commit);
            _hasWorkspaceChanges = false;
            OnDirtyStateChanged();
            Status = $"Saved {PackageName}.";
            if (showConfirmation)
            {
                _dialogs.ShowInformation(
                    "Package saved",
                    $"All temporary BioMorphFace changes were committed successfully.\n\nPackage: {PackagePath}");
            }
            AppLog.Information($"Temporary package workspace committed to '{PackagePath}'.");
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error($"Package workspace commit failed for '{PackagePath}'.", exception);
            ErrorMessage = $"Package changes could not be committed: {exception.Message}";
            Status = "Save failed; temporary edits are still available and the source PCC was not modified.";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> EnsureCanAbandonEditorChangesAsync()
    {
        if (Editor?.IsDirty != true)
        {
            return true;
        }
        return _dialogs.ConfirmUnsavedChanges(
            LoadedFacePath ?? "Loaded BioMorphFace",
            IsStandaloneWorkspace
                ? UnsavedChangesScope.StandaloneFace
                : UnsavedChangesScope.Face) switch
        {
            UnsavedChangesChoice.Save => IsStandaloneWorkspace
                ? await SaveMorphToPccAsync()
                : await FlushEditorToWorkspaceAsync(),
            UnsavedChangesChoice.Discard => true,
            _ => false
        };
    }

    private async Task<bool> EnsureCanAbandonWorkspaceAsync()
    {
        if (!IsDirty)
        {
            return true;
        }
        return _dialogs.ConfirmUnsavedChanges(
            IsStandaloneWorkspace ? LoadedFacePath ?? PackageName : PackagePath ?? "Open package",
            IsStandaloneWorkspace ? UnsavedChangesScope.StandaloneFace : UnsavedChangesScope.Package) switch
        {
            UnsavedChangesChoice.Save => IsStandaloneWorkspace
                ? await SaveMorphToPccAsync()
                : await CommitWorkspaceAsync(showConfirmation: false),
            UnsavedChangesChoice.Discard => true,
            _ => false
        };
    }

    private void RestoreLoadedFaceSelection()
    {
        if (LoadedFacePath is null)
        {
            return;
        }
        SelectedFace = Faces.FirstOrDefault(face => string.Equals(
            face.InstancedPath,
            LoadedFacePath,
            StringComparison.OrdinalIgnoreCase));
    }

    public Task<bool> ConfirmCloseAsync() => EnsureCanAbandonWorkspaceAsync();

    private async Task SaveMorphToPccCommandAsync() => _ = await SaveMorphToPccAsync();

    private async Task<bool> SaveMorphToPccAsync()
    {
        if (Editor is null || _loadedFace is null || PackagePath is null || WorkspacePackagePath is null)
        {
            return false;
        }
        var sourceName = SelectedFace?.ObjectName ??
                         _loadedFace.Document.Source.InstancedPath.Split('.').Last();
        var request = _dialogs.ChooseMorphPackageDestination(
            $"{sourceName}.pcc",
            _standaloneImportPath ?? PackagePath);
        if (request is null)
        {
            return false;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = "Exporting the morph and its dependencies…";
        try
        {
            var draft = Editor.CreateDraft();
            AppLog.Information(
                $"Exporting face '{LoadedFacePath}' to '{request.DestinationPackagePath}' " +
                $"(create new package: {request.CreateNewPackage}).");
            var result = await Task.Run(() => _packageWriter.SaveMorphToPackage(
                draft,
                PackagePath,
                request.DestinationPackagePath,
                request.CreateNewPackage));
            Status = result.Warnings.Count == 0
                ? $"Exported and verified {result.FaceInstancedPath}."
                : $"Exported and verified {result.FaceInstancedPath} with {result.Warnings.Count} dependency warning(s).";
            if (result.Warnings.Count > 0)
            {
                AppLog.Warning(
                    $"Morph package export completed with {result.Warnings.Count} LEC warning(s): " +
                    string.Join("; ", result.Warnings));
            }
            var warningSummary = result.Warnings.Count == 0
                ? string.Empty
                : $"\nDependency warnings: {result.Warnings.Count} (details were written to the application log).";
            _dialogs.ShowInformation(
                "Morph package saved",
                $"The morph and its required package dependencies were exported successfully.\n\n" +
                $"Package: {result.PackagePath}\nFace: {result.FaceInstancedPath}\n" +
                $"Baked LODs: {result.LodCount}\nTexture references: {result.TextureReferenceCount}" + warningSummary);
            AppLog.Information($"Morph package export verified: '{result.FaceInstancedPath}' in '{result.PackagePath}'.");
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error($"Morph package export failed for '{LoadedFacePath}' in '{PackagePath}'.", exception);
            ErrorMessage = $"The morph could not be exported: {exception.Message}";
            Status = "Save failed; the source PCC was not modified.";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CancelPendingLoad()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    private void DisposePackageWorkspace()
    {
        if (_packageWorkspace is null)
        {
            return;
        }
        _referenceService.InvalidatePackage(_packageWorkspace.WorkingPath);
        _packageWorkspace.Dispose();
        _packageWorkspace = null;
    }

    public void Dispose()
    {
        CancelPendingLoad();
        SetEditor(null, null);
        _previewLoadService.Dispose();
        _packageWorkspace?.Dispose();
        _packageWorkspace = null;
    }
}

public sealed record PreviewLightingChoice(HeadPreviewLightingPreset Preset, string Name);
public sealed record PreviewLodChoice(int LodIndex, string Name);
