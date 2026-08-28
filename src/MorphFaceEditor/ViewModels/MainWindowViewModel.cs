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
    private readonly MorphRandomisationCatalog _randomisationCatalog;
    private readonly AsyncRelayCommand _openPackageCommand;
    private readonly AsyncRelayCommand _loadSelectedFaceCommand;
    private readonly AsyncRelayCommand _saveCommand;
    private readonly AsyncRelayCommand _saveMorphToPccCommand;
    private readonly RelayCommand _editBackgroundColorCommand;
    private readonly RelayCommand _dismissErrorCommand;
    private readonly RelayCommand _objectDatabaseSettingsCommand;
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
    private MorphFacePackageWorkspace? _packageWorkspace;
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
        MorphRandomisationCatalog? randomisationCatalog = null)
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
        _randomisationCatalog = randomisationCatalog ?? MorphRandomisationCatalog.Empty;
        _openPackageCommand = new AsyncRelayCommand(OpenPackageAsync, () => !IsBusy);
        _loadSelectedFaceCommand = new AsyncRelayCommand(
            LoadSelectedFaceCommandAsync,
            () => !IsBusy && SelectedFace is not null && WorkspacePackagePath is not null);
        _saveCommand = new AsyncRelayCommand(
            SaveExistingCommandAsync,
            () => !IsBusy && IsDirty && _packageWorkspace is not null);
        _saveMorphToPccCommand = new AsyncRelayCommand(
            SaveMorphToPccAsync,
            () => !IsBusy && Editor is not null && _loadedFace is not null && _packageWorkspace is not null);
        _editBackgroundColorCommand = new RelayCommand(EditBackgroundColor);
        _dismissErrorCommand = new RelayCommand(() => ErrorMessage = null);
        _objectDatabaseSettingsCommand = new RelayCommand(_dialogs.ShowObjectDatabaseSettings);
        _cloneMorphCommand = new AsyncRelayCommand(CloneMorphAsync, CanUseFaceContextMenu);
        _deleteMorphCommand = new AsyncRelayCommand(DeleteMorphAsync, CanUseFaceContextMenu);
        _convertMorphCommand = new AsyncRelayCommand(ConvertMorphAsync, CanUseFaceContextMenu);
        _copyMorphDataCommand = new AsyncRelayCommand(CopyMorphDataAsync, CanUseFaceContextMenu);
        _pasteMorphDataCommand = new AsyncRelayCommand(PasteMorphDataAsync, CanPasteMorphData);
        _copyMaterialDataCommand = new AsyncRelayCommand(CopyMaterialDataAsync, CanUseFaceContextMenu);
        _pasteMaterialDataCommand = new AsyncRelayCommand(PasteMaterialDataAsync, CanPasteMaterialData);
        _importMorphCommand = new AsyncRelayCommand(ImportMorphAsync, CanUseFaceContextMenu);
        _exportMorphPskCommand = new AsyncRelayCommand(
            () => ExportMorphMeshAsync(MorphMeshFormat.Psk), CanUseFaceContextMenu);
        _exportMorphGltfCommand = new AsyncRelayCommand(
            () => ExportMorphMeshAsync(MorphMeshFormat.Gltf), CanUseFaceContextMenu);
        _exportMorphMd5Command = new AsyncRelayCommand(
            () => ExportMorphMeshAsync(MorphMeshFormat.Md5), CanUseFaceContextMenu);
        _exportMorphRonCommand = new AsyncRelayCommand(ExportMorphRonAsync, CanUseFaceContextMenu);
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
    public ICommand EditBackgroundColorCommand => _editBackgroundColorCommand;
    public ICommand DismissErrorCommand => _dismissErrorCommand;
    public ICommand ObjectDatabaseSettingsCommand => _objectDatabaseSettingsCommand;
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

    public string PackageName => PackagePath is null ? "No package open" : Path.GetFileName(PackagePath);
    public string PackageDisplayName => IsDirty ? $"{PackageName} *" : PackageName;
    public bool IsDirty => _hasWorkspaceChanges || Editor?.IsDirty == true;
    private string? WorkspacePackagePath => _packageWorkspace?.WorkingPath;

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
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var warning in result.Loaded.Warnings)
            {
                AppLog.Warning(warning);
            }
            var textureCatalogProfile = TextureCatalogProfiles.For(result.Profile);
            var topology = result.Loaded.BaseHead.Topology;
            var oracle = result.EditingSession.Evaluation.OriginalOracleReport;
            var materialOverrides = result.Loaded.Document.MaterialOverrides;
            FaceDetails = $"{topology.VertexCount:N0} vertices · {topology.IndexCount / 3:N0} triangles · " +
                          $"{topology.Sections.Count} sections · {result.Loaded.Document.FinalSkeleton.Count} final bones · " +
                          $"{materialOverrides.Scalars.Count}/{materialOverrides.Vectors.Count}/{materialOverrides.Textures.Count} material S/V/T · " +
                          (result.EditingSession.CanEdit
                              ? $"oracle {oracle!.MaximumError:G4} max"
                              : $"baked fallback · {result.EditingSession.EditBlockReason}");
            LoadedFacePath = result.Loaded.Document.Source.InstancedPath;
            var editor = new FaceEditorViewModel(
                result.EditingSession,
                result.Profile.UiProfile,
                result.MaterialEditingSession,
                _colorDialog,
                _referenceService,
                workspacePath,
                _referenceCatalog.Textures,
                _referenceCatalog.SkeletalMeshes,
                result.Loaded.Document.HairMeshReference,
                result.Loaded.Document.OtherMeshReferences,
                SetEditorError,
                result.Profile.Key,
                _randomisationCatalog,
                randomisationInclusionState: _randomisationInclusionState,
                registryTextureCandidates: [],
                textureCatalogProfile: textureCatalogProfile,
                isTextureRegistryAvailable: false);
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
            Status = result.EditingSession.CanEdit
                ? $"Loaded {SelectedFace.DisplayName} ({result.Profile.DisplayName}); live geometry and material editing ready."
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

            editor.UpdateRegistryTextureCandidates(
                catalog.Candidates,
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
        }
        Editor?.SetExtendedSliders(ExtendedSliders);
        PreviewLods.Clear();
        if (loaded is not null)
        {
            foreach (var lod in loaded.BaseHead.AvailableLods
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
        }
    }

    private void OnDirtyStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(PackageDisplayName));
        _saveCommand.RaiseCanExecuteChanged();
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
            PreviewDeformationReady?.Invoke(_sceneFactory.CreateUpdate(_loadedFace, Editor.Evaluation, PreviewLod));
        }
    }

    private void OnMaterialPreviewChanged(object? sender, EventArgs e)
    {
        if (!_suppressMaterialPreview && Editor is not null && _loadedFace is not null)
        {
            PreviewMaterialReady?.Invoke(_sceneFactory.CreateMaterialUpdate(_loadedFace, Editor.Material.Materials));
        }
    }

    private async void OnAttachmentMeshChanged(object? sender, EventArgs e)
    {
        if (Editor is null || _loadedFace is null || WorkspacePackagePath is not { } workspacePath ||
            sender is not HairMeshEditorViewModel changedSlot)
        {
            return;
        }

        var version = ++_attachmentChangeVersion;
        var editor = Editor;
        var loadedFace = _loadedFace;
        var packagePath = workspacePath;
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
                attachments[index] = await _referenceService.LoadAttachmentAsync(
                    packagePath,
                    loadedFace.Document.Source.InstancedPath,
                    reference.InstancedPath);
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
            var stagedLoadedFace = loadedFace with
            {
                HairMesh = attachments[0]?.Mesh,
                OtherMeshes = otherMeshes,
                Materials = MergeMaterials(loadedFace.Materials, attachmentMaterials),
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
            _ = editor.CanEdit
                ? _sceneFactory.CreateEditable(stagedLoadedFace, editor.Evaluation, PreviewLod)
                : _sceneFactory.Create(stagedLoadedFace, PreviewLod);

            materialState = editor.Material.CaptureAttachmentState();
            _suppressMaterialPreview = true;
            editor.Material.ReplaceAttachmentMaterials(
                attachmentMaterials,
                attachments.ElementAtOrDefault(changedSlot.SlotIndex)?.Materials);
            var committedLoadedFace = stagedLoadedFace with { Materials = editor.Material.Materials };
            var committedScene = editor.CanEdit
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
        var previewSource = loaded with { Materials = editor.Material.Materials };
        return editor.CanEdit
            ? _sceneFactory.CreateEditable(previewSource, editor.Evaluation, PreviewLod)
            : _sceneFactory.Create(previewSource, PreviewLod);
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

    private async Task SaveExistingCommandAsync() =>
        _ = await CommitWorkspaceAsync(showConfirmation: true);

    private async Task<bool> FlushEditorToWorkspaceAsync()
    {
        if (Editor is null || _loadedFace is null || WorkspacePackagePath is null)
        {
            return false;
        }
        if (Editor.Material.HasExternalRegistrySelections)
        {
            ErrorMessage = "This face uses an installed texture-registry selection. Saving it will be enabled once the path-preserving texture materialisation stage is complete.";
            Status = "External texture selection is preview-only for now.";
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
        if (Editor?.Material.HasExternalRegistrySelections == true)
        {
            ErrorMessage = "This face uses an installed texture-registry selection. Saving it will be enabled once the path-preserving texture materialisation stage is complete.";
            Status = "External texture selection is preview-only for now.";
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
            UnsavedChangesScope.Face) switch
        {
            UnsavedChangesChoice.Save => await FlushEditorToWorkspaceAsync(),
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
        return _dialogs.ConfirmUnsavedChanges(PackagePath ?? "Open package") switch
        {
            UnsavedChangesChoice.Save => await CommitWorkspaceAsync(showConfirmation: false),
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

    private async Task SaveMorphToPccAsync()
    {
        if (Editor is null || _loadedFace is null || PackagePath is null || WorkspacePackagePath is null)
        {
            return;
        }
        if (Editor.Material.HasExternalRegistrySelections)
        {
            ErrorMessage = "This face uses an installed texture-registry selection. Export is disabled until path-preserving texture materialisation is complete.";
            Status = "External texture selection is preview-only for now.";
            return;
        }
        var sourceName = SelectedFace?.DisplayName ?? _loadedFace.Document.Source.InstancedPath.Split('.').Last();
        var request = _dialogs.ChooseMorphPackageDestination($"{sourceName}.pcc", PackagePath);
        if (request is null)
        {
            return;
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
        }
        catch (Exception exception)
        {
            AppLog.Error($"Morph package export failed for '{LoadedFacePath}' in '{PackagePath}'.", exception);
            ErrorMessage = $"The morph could not be exported: {exception.Message}";
            Status = "Save failed; the source PCC was not modified.";
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
