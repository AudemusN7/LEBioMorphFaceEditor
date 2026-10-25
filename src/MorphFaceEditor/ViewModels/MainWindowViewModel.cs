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
using MorphFaceEditor.Core.Randomisation;
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
    private readonly CustomMeshPccMaterializer _customMeshPccMaterializer;
    private readonly ICustomMeshBinaryWriter _customMeshBinaryWriter;
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
    private readonly RecentFileService _recentFileService;
    private readonly StartupPreferencesService? _startupPreferences;
    private readonly AsyncRelayCommand _openPackageCommand;
    private readonly AsyncRelayCommand _loadSelectedFaceCommand;
    private readonly AsyncRelayCommand _commitCommand;
    private readonly AsyncRelayCommand _saveCommand;
    private readonly AsyncRelayCommand _saveAsCommand;
    private readonly AsyncRelayCommand _saveMorphToPccCommand;
    private readonly AsyncRelayCommand _closeWorkspaceCommand;
    private readonly RelayCommand _exitCommand;
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
    private readonly AsyncRelayCommand _newPlayerMorphCommand;
    private readonly RelayCommand _cancelNpcImportCommand;
    private readonly AsyncRelayCommand _exportMorphPskCommand;
    private readonly AsyncRelayCommand _exportMorphGltfCommand;
    private readonly AsyncRelayCommand _exportMorphRonCommand;
    private readonly AsyncRelayCommand _assignMorphToActorCommand;
    private readonly AsyncRelayCommand _assignMaterialsToActorCommand;
    private EditorWorkspace? _workspace;
    private MorphFacePackageWorkspace? PackageWorkspace => _workspace?.Package;
    private PcsavHeadMorphWorkspace? CareerWorkspace => _workspace?.Career;
    private ImportedMeshAsset? DetachedMeshSource => _workspace?.Mesh;
    private ImportedFaceFile? SourceFile => _workspace?.ImportedFile;
    private MorphFaceGame? StandaloneGame => IsStandaloneWorkspace ? _workspace!.Game : null;
    private bool IsNpcWorkspace => _workspace is { Kind: EditorWorkspaceKind.Npc };
    private bool CareerDraftPendingInPackage => _workspace?.CareerDraftPendingInPackage == true;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _npcImportCancellation;
    private LoadedMorphFace? _loadedFace;
    private MorphFaceProfile? _activeRegistryProfile;
    private TextureCatalogProfile? _activeRegistryCatalogProfile;
    private string _faceSearchText = string.Empty;
    private BioMorphFaceListItem? _selectedFace;
    private FaceEditorViewModel? _editor;
    private string _status = Loc.Text("loc_Shell_Status_OpenAPackageToBegin");
    private string? _errorMessage;
    private string? _faceDetails;
    private string? _faceDetailsPrefix;
    private bool _faceDetailsHasBakedFallback;
    private string? _loadedFacePath;
    private bool _isBusy;
    private bool _hasPreview;
    private bool _wireframe;
    private bool _extendedSliders;
    private bool _showAttachment = true;
    private HeadPreviewRenderMode _renderMode = HeadPreviewRenderMode.Shaded;
    private HeadPreviewLightingPreset _lightingPreset = HeadPreviewLightingPreset.Studio;
    private bool _postProcessingEnabled = true;
    private int _previewLod;
    private Vector4 _backgroundColor = WpfHdrColorDialogService.DefaultBackgroundColor;
    private bool _hasAuthoredBackground;
    private Vector4 _themeBackgroundColor = WpfHdrColorDialogService.DefaultBackgroundColor;
    private PackageReferenceCatalog _referenceCatalog = new([], []);
    private int _attachmentChangeVersion;
    private CancellationTokenSource? _attachmentPreviewCancellation;
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
        DetachedMeshPreviewLoadService? detachedMeshPreviewLoadService = null,
        RecentFileService? recentFiles = null,
        StartupPreferencesService? startupPreferences = null)
    {
        _dialogs = dialogs;
        _catalogService = catalogService;
        _previewLoadService = previewLoadService;
        _sceneFactory = sceneFactory;
        _colorDialog = colorDialog;
        _referenceService = referenceService;
        _packageWriter = packageWriter;
        _customMeshPccMaterializer = new CustomMeshPccMaterializer();
        _customMeshBinaryWriter = new ImportedSkeletalMeshPackageWriter();
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
        _recentFileService = recentFiles ?? new RecentFileService();
        _startupPreferences = startupPreferences;
        if (startupPreferences?.BackgroundColor is { } savedBackground)
        {
            _hasAuthoredBackground = true;
            _backgroundColor = Vector4.Clamp(savedBackground, Vector4.Zero, Vector4.One);
        }
        _openPackageCommand = new AsyncRelayCommand(OpenPackageAsync, () => !IsBusy);
        _loadSelectedFaceCommand = new AsyncRelayCommand(
            LoadSelectedFaceCommandAsync,
            () => !IsBusy && SelectedFace is not null && WorkspacePackagePath is not null);
        _commitCommand = new AsyncRelayCommand(
            CommitFaceCommandAsync,
            () => !IsBusy && Editor?.IsDirty == true && PackageWorkspace?.CanCommit == true);
        _saveCommand = new AsyncRelayCommand(
            SaveExistingCommandAsync,
            () => !IsBusy && HasWorkspace && (IsDirty || HasMultipleStandaloneImports));
        _saveAsCommand = new AsyncRelayCommand(SaveSourceFileAsAsync, () => !IsBusy && HasWorkspace);
        _saveMorphToPccCommand = new AsyncRelayCommand(
            SaveMorphToPccCommandAsync,
            () => CanTargetPackageFace() ||
                  !IsBusy && IsDetachedMeshWorkspace && SelectedFace is not null &&
                  StandaloneGame is not null && Editor?.CustomMaterials is not null);
        _closeWorkspaceCommand = new AsyncRelayCommand(CloseWorkspaceAsync, () => !IsBusy && HasWorkspace);
        _exitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        _fixMorphCommand = new RelayCommand(FixMorph, () => !IsBusy && Editor?.CanFixMorph == true);
        _editBackgroundColorCommand = new RelayCommand(EditBackgroundColor);
        _dismissErrorCommand = new RelayCommand(() => ErrorMessage = null);
        _textureRegistrySettingsCommand = new RelayCommand(_dialogs.ShowTextureRegistrySettings);
        _cloneMorphCommand = new AsyncRelayCommand(CloneMorphAsync, CanMutatePackageContext);
        _deleteMorphCommand = new AsyncRelayCommand(DeleteMorphAsync,
            () => CanMutatePackageContext() || !IsBusy && CareerWorkspace is not null && SelectedFace is not null);
        _convertMorphCommand = new AsyncRelayCommand(ConvertMorphAsync, CanConvertMorph);
        _copyMorphDataCommand = new AsyncRelayCommand(CopyMorphDataAsync, CanCopyMorphData);
        _pasteMorphDataCommand = new AsyncRelayCommand(PasteMorphDataAsync, CanPasteMorphData);
        _copyMaterialDataCommand = new AsyncRelayCommand(CopyMaterialDataAsync, CanUseMaterialContextMenu);
        _pasteMaterialDataCommand = new AsyncRelayCommand(PasteMaterialDataAsync, CanPasteMaterialData);
        _newPlayerMorphCommand = new AsyncRelayCommand(NewPlayerMorphAsync, () => !IsBusy);
        _importMorphCommand = new AsyncRelayCommand(ImportMorphAsync, () => !IsBusy);
        _cancelNpcImportCommand = new RelayCommand(CancelNpcImport, () => CanCancelNpcImport);
        _exportMorphPskCommand = new AsyncRelayCommand(
            () => ExportMorphMeshAsync(MorphMeshFormat.Psk), CanTargetPackageFace);
        _exportMorphGltfCommand = new AsyncRelayCommand(
            () => ExportMorphMeshAsync(MorphMeshFormat.Gltf), CanTargetPackageFace);
        _exportMorphRonCommand = new AsyncRelayCommand(ExportMorphRonAsync, CanExportMorphRon);
        _exportTseMaterialsCommand = new AsyncRelayCommand(async () => { await ExportMeshMaterialsAsync(true); },
            () => CanUseMeshMaterialFiles() && Editor?.CanExportTseMaterials == true);
        _importTseMaterialsCommand = new AsyncRelayCommand(() => ImportMeshMaterialsAsync(true),
            () => CanUseMeshMaterialFiles() && Editor?.CanImportTseMaterials == true);
        _exportMaterialsCommand = new AsyncRelayCommand(async () =>
        {
            if (CanUseMeshMaterialFiles()) await ExportMeshMaterialsAsync(false);
            else await ExportFaceMaterialsAsync();
        }, CanUseMaterialFiles);
        _importMaterialsCommand = new AsyncRelayCommand(() =>
            CanUseMeshMaterialFiles() ? ImportMeshMaterialsAsync(false) : ImportFaceMaterialsAsync(), CanUseMaterialFiles);
        _assignMorphToActorCommand = new AsyncRelayCommand(AssignMorphToActorAsync, CanMutatePackageContext);
        _assignMaterialsToActorCommand = new AsyncRelayCommand(AssignMaterialsToActorAsync, CanMutatePackageContext);
        RefreshRecentFiles();
        FilteredFaces = CollectionViewSource.GetDefaultView(Faces);
        FilteredFaces.Filter = item =>
            item is BioMorphFaceListItem face && BioMorphFaceSearch.Matches(face, FaceSearchText);
    }

    public event Action<HeadPreviewScene, bool>? PreviewSceneReady;
    public event EventHandler? PreviewCleared;
    public event Action<HeadPreviewDeformationUpdate>? PreviewDeformationReady;
    public event Action<HeadPreviewMaterialUpdate>? PreviewMaterialReady;
    public event EventHandler? PreviewOptionsChanged;
    public event EventHandler? ExitRequested;

    public ObservableCollection<BioMorphFaceListItem> Faces { get; } = [];
    public ObservableCollection<RecentFileItem> RecentFiles { get; } = [];
    public ObservableCollection<PreviewLodChoice> PreviewLods { get; } = [];
    public ICollectionView FilteredFaces { get; }
    public IReadOnlyList<HeadPreviewRenderMode> RenderModes { get; } = Enum.GetValues<HeadPreviewRenderMode>();
    public IReadOnlyList<PreviewLightingChoice> LightingPresets { get; } =
    [
        new(HeadPreviewLightingPreset.Studio, Loc.Text("loc_Shell_Lighting_Studio")),
        new(HeadPreviewLightingPreset.HighContrast, Loc.Text("loc_Shell_Lighting_HighContrast")),
        new(HeadPreviewLightingPreset.WarmInterior, Loc.Text("loc_Shell_Lighting_WarmInterior")),
        new(HeadPreviewLightingPreset.CoolNight, Loc.Text("loc_Shell_Lighting_CoolNight")),
        new(HeadPreviewLightingPreset.Cinematic, Loc.Text("loc_Shell_Lighting_Cinematic"))
    ];
    public ICommand OpenPackageCommand => _openPackageCommand;
    public ICommand LoadSelectedFaceCommand => _loadSelectedFaceCommand;
    public ICommand CommitCommand => _commitCommand;
    public ICommand SaveCommand => _saveCommand;
    public ICommand SaveAsCommand => _saveAsCommand;
    public ICommand SaveMorphToPccCommand => _saveMorphToPccCommand;
    public ICommand CloseWorkspaceCommand => _closeWorkspaceCommand;
    public ICommand ExitCommand => _exitCommand;
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
    public ICommand NewPlayerMorphCommand => _newPlayerMorphCommand;
    public ICommand ImportMorphCommand => _importMorphCommand;
    public ICommand CancelNpcImportCommand => _cancelNpcImportCommand;
    public ICommand ExportMorphPskCommand => _exportMorphPskCommand;
    public ICommand ExportMorphGltfCommand => _exportMorphGltfCommand;
    public ICommand ExportMorphRonCommand => _exportMorphRonCommand;
    public ICommand AssignMorphToActorCommand => _assignMorphToActorCommand;
    public ICommand AssignMaterialsToActorCommand => _assignMaterialsToActorCommand;

    public string? PackagePath => _workspace?.PackagePath;
    public string PackageName => _workspace switch
    {
        null => Loc.Text("loc_Shell_PackageName_NoPackageOpen"),
        { Career: { } career } => Path.GetFileName(career.SourcePath),
        { Kind: EditorWorkspaceKind.DetachedMesh, Game: var game } => Loc.Format("loc_Shell_PackageName_DetachedMeshWorkspace", game),
        { Kind: EditorWorkspaceKind.Player, Game: var game } => Loc.Format("loc_Shell_PackageName_StandalonePlayerWorkspace", game),
        { Kind: EditorWorkspaceKind.Npc, Game: var game } => Loc.Format("loc_Shell_PackageName_StandaloneNpcWorkspace", game),
        _ => Path.GetFileName(PackagePath)!
    };
    public string PackageDisplayName => IsDirty ? $"{PackageName} *" : PackageName;
    public string? WorkspaceGameIcon => _workspace is { Game: not MorphFaceGame.Unsupported }
        ? $"pack://application:,,,/MorphFaceEditor;component/Assets/GameIcons/{_workspace.Game}.png" : null;
    public bool HasWorkspace => _workspace is not null;
    public bool HasRecentFiles => RecentFiles.Count > 0;
    public bool IsDirty => _workspace?.HasPendingChanges == true || Editor?.IsDirty == true;
    public bool CanCommitFace => PackageWorkspace?.CanCommit == true && Editor is not null;
    public bool CanFixMorph => Editor?.CanFixMorph == true;
    public bool IsDetachedMeshWorkspace => _workspace is { Kind: EditorWorkspaceKind.DetachedMesh };
    private string? WorkspacePackagePath => PackageWorkspace?.WorkingPath;
    private bool IsStandaloneWorkspace => _workspace?.IsStandalone == true;
    private bool IsPlayerWorkspace => _workspace is { Kind: EditorWorkspaceKind.Player };

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
                _loadSelectedFaceCommand.RaiseCanExecuteChanged();
                _saveMorphToPccCommand.RaiseCanExecuteChanged();
                _closeWorkspaceCommand.RaiseCanExecuteChanged();
                RaiseFaceContextCanExecuteChanged();
                OnPropertyChanged(nameof(IsMaterialFileWorkspace));
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
        private set
        {
            if (SetProperty(ref _loadedFacePath, value))
                OnPropertyChanged(nameof(IsMaterialFileWorkspace));
        }
    }

    public bool IsBusy
    {
        get => _isBusy || _saveControlLockDepth > 0;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyBusyCommandsChanged();
            }
        }
    }

    private int _saveControlLockDepth;

    private void NotifyBusyCommandsChanged()
    {
        _openPackageCommand.RaiseCanExecuteChanged();
        _loadSelectedFaceCommand.RaiseCanExecuteChanged();
        _commitCommand.RaiseCanExecuteChanged();
        _saveCommand.RaiseCanExecuteChanged();
        _saveAsCommand.RaiseCanExecuteChanged();
        _saveMorphToPccCommand.RaiseCanExecuteChanged();
        _closeWorkspaceCommand.RaiseCanExecuteChanged();
        _fixMorphCommand.RaiseCanExecuteChanged();
        RaiseFaceContextCanExecuteChanged();
    }

    // Refresh/load also set IsBusy. Keep the controls locked until the whole
    // save completes, even when one of those nested stages clears its flag.
    private sealed class SaveControlLock : IDisposable
    {
        private MainWindowViewModel? _owner;

        public SaveControlLock(MainWindowViewModel owner)
        {
            _owner = owner;
            owner._saveControlLockDepth++;
            owner.OnPropertyChanged(nameof(IsBusy));
            owner.NotifyBusyCommandsChanged();
        }

        public void Dispose()
        {
            if (_owner is not { } owner) return;
            _owner = null;
            owner._saveControlLockDepth--;
            owner.OnPropertyChanged(nameof(IsBusy));
            owner.NotifyBusyCommandsChanged();
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

    public bool PostProcessingEnabled
    {
        get => _postProcessingEnabled;
        set
        {
            if (SetProperty(ref _postProcessingEnabled, value))
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
                    ? Loc.Format("loc_Shell_Status_PreviewingLod", value)
                    : Loc.Format("loc_Shell_Status_PreviewingLodMorphDisabled", value);
            }
            catch (Exception exception)
            {
                SetPreviewError(Loc.Format("loc_Shell_Error_LodPreviewFailed", value, UserFacingMessageFormatter.Format(exception)));
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

    internal void ApplyThemeBackgroundColor(Vector4 colour)
    {
        _themeBackgroundColor = colour;
        if (!_hasAuthoredBackground) BackgroundColor = colour;
    }

    public HeadPreviewOptions CreatePreviewOptions() => new(
        RenderMode,
        Wireframe,
        ShowAttachment,
        LightingPreset,
        new Vector3(BackgroundColor.X, BackgroundColor.Y, BackgroundColor.Z),
        PostProcessingEnabled: PostProcessingEnabled);

    private void EditBackgroundColor()
    {
        var before = BackgroundColor;
        var applied = _colorDialog.EditBackground(
            Loc.Text("loc_Shell_Dialog_PreviewBackgroundColour"),
            before,
            _themeBackgroundColor,
            value => BackgroundColor = value);
        var followTheme = applied is not null && (applied.FollowTheme || applied.Colour == _themeBackgroundColor);
        BackgroundColor = applied is null ? (_hasAuthoredBackground ? before : _themeBackgroundColor)
            : followTheme ? _themeBackgroundColor : applied.Colour;
        if (applied is not null)
        {
            _hasAuthoredBackground = !followTheme;
            if (followTheme) _startupPreferences?.FollowThemeBackground();
            else _startupPreferences?.SetBackgroundColor(BackgroundColor);
        }
    }

    private static byte ToColorByte(float value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue);

    public void SetFrameStatus(HeadPreviewFrameInfo frame) =>
        Status = Loc.Format("loc_Shell_Status_PreviewStats", frame.DeviceKind, frame.Width, frame.Height, frame.DrawCalls, frame.RenderTime.TotalMilliseconds);

    public void SetPreviewError(string message)
    {
        ErrorMessage = message;
        Status = Loc.Text("loc_Shell_Status_PreviewUnavailable");
    }

    private void SetEditorError(string message)
    {
        ErrorMessage = message;
        Status = message;
    }

    private async Task OpenPackageAsync()
    {
        var selectedPath = _dialogs.ChooseWorkspaceFile(
            PackagePath is null ? null : Path.GetDirectoryName(PackagePath));
        if (selectedPath is null)
        {
            return;
        }

        if (EditorFileDrop.Classify(selectedPath) == EditorFileDropKind.Unsupported)
        {
            ErrorMessage = Loc.Format("loc_Shell_Error_UnsupportedFileType", Path.GetExtension(selectedPath));
            Status = Loc.Text("loc_Shell_Status_SelectedFileCouldNotOpen");
            return;
        }

        await OpenDroppedFileAsync(selectedPath);
    }

    public bool CanCancelNpcImport => _npcImportCancellation is { IsCancellationRequested: false };

    private void CancelNpcImport()
    {
        _npcImportCancellation?.Cancel();
        _cancelNpcImportCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanCancelNpcImport));
    }

    internal bool CanOpenDroppedFile(string path) =>
        !IsBusy && EditorFileDrop.Classify(path) switch
        {
            EditorFileDropKind.Package => true,
            EditorFileDropKind.CareerSave => true,
            EditorFileDropKind.MorphImport => true,
            _ => false
        };

    internal async Task OpenDroppedFileAsync(string path)
    {
        if (!CanOpenDroppedFile(path))
        {
            return;
        }

        RecordRecentFile(path);

        if (EditorFileDrop.Classify(path) == EditorFileDropKind.CareerSave)
        {
            await OpenCareerSaveAsync(path);
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

    private async Task OpenRecentFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            _recentFileService.Remove(path);
            RefreshRecentFiles();
            ErrorMessage = Loc.Format("loc_Shell_Error_RecentFileMissing", path);
            Status = Loc.Text("loc_Shell_Status_RecentFileCouldNotOpen");
            return;
        }
        await OpenDroppedFileAsync(path);
    }

    private void RecordRecentFile(string path)
    {
        _recentFileService.Add(path);
        RefreshRecentFiles();
    }

    private void RefreshRecentFiles()
    {
        RecentFiles.Clear();
        foreach (var path in _recentFileService.Paths)
        {
            RecentFiles.Add(new RecentFileItem(path, new AsyncRelayCommand(() => OpenRecentFileAsync(path))));
        }
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    private async Task<bool> OpenSourcePackagePathAsync(string selectedPath)
    {
        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Text("loc_Shell_Status_ReadingBioMorphFaceExports");
        try
        {
            var candidate = EditorWorkspace.ForPcc(await Task.Run(() => new MorphFacePackageWorkspace(selectedPath)));
            await PrepareAndPublishWorkspaceAsync(candidate);
            LogWorkspaceCreated("PCC", _workspace!.Game, selectedPath);
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error($"WORKSPACE:OPEN-FAILED FILE:{selectedPath}", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_PackageCouldNotOpen", UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text("loc_Shell_Status_PackageLoadFailed");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static void LogWorkspaceCreated(string type, MorphFaceGame game, string sourcePath, string? seedPath = null, bool append = false) =>
        AppLog.Information($"WORKSPACE:{(append ? "APPEND" : "CREATE")} TYPE:{type} {game} IMPORT:{Path.GetExtension(sourcePath).TrimStart('.').ToUpperInvariant()} " +
            $"SOURCE:\"{sourcePath}\"" + (seedPath is null || seedPath == sourcePath ? "" : $" SEED:\"{seedPath}\""));

    // Mutation callers retain the active owner; this refresh does not roll back their PCC edits.
    private async Task<bool> RefreshWorkspaceAsync(string? preferredFacePath = null, bool clearSearch = false)
    {
        if (_workspace?.Package is null) return false;
        var workspacePath = _workspace.Package.WorkingPath;
        AppLog.Verbose($"WORKSPACE:REFRESH FACE:{preferredFacePath ?? "<first>"} FILE:\"{workspacePath}\"");
        CancelPendingLoad();
        SetEditor(null, null);
        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Text("loc_Shell_Status_ReadingBioMorphFaceExports");
        try
        {
            var prepared = await ReadWorkspaceContentsAsync(_workspace, preferredFacePath);
            PublishWorkspaceContents(prepared, clearSearch);
        }
        catch (Exception exception)
        {
            AppLog.Error($"WORKSPACE:REFRESH-FAILED FILE:{workspacePath}", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_PackageCouldNotOpen", UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text("loc_Shell_Status_PackageLoadFailed");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
        return SelectedFace is null || await LoadSelectedFaceAsync(confirmUnsavedChanges: false);
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
        if (confirmUnsavedChanges && !await StageSavedCareerDraftForReloadAsync()) return false;
        SelectedFace = Faces.FirstOrDefault(face => face.UIndex == requestedFaceUIndex);
        if (SelectedFace is null)
        {
            ErrorMessage = Loc.Format("loc_Shell_Error_FaceMissingFromPcc", requestedFacePath);
            return false;
        }

        CancelPendingLoad();
        SetEditor(null, null);
        _loadCancellation = new CancellationTokenSource();
        var cancellationToken = _loadCancellation.Token;
        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Format("loc_Shell_Status_LoadingFace", SelectedFace.DisplayName);
        AppLog.Verbose($"FACE:LOAD {SelectedFace.InstancedPath} FILE:\"{workspacePath}\"");
        try
        {
            var prepared = await PrepareFaceAsync(_workspace!, _referenceCatalog, SelectedFace, cancellationToken);
            PublishFace(prepared, SelectedFace);
            return true;
        }
        catch (OperationCanceledException)
        {
            Status = Loc.Text("loc_Shell_Status_LoadCancelled");
            return false;
        }
        catch (Exception exception)
        {
            AppLog.Error($"FACE:LOAD-FAILED {requestedFacePath} FILE:{workspacePath}", exception);
            HasPreview = false;
            SetEditor(null, null);
            ErrorMessage = Loc.Format("loc_Shell_Error_FaceCouldNotLoad", UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text("loc_Shell_Status_FaceLoadFailed");
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
            AppLog.Verbose($"REGISTRY:LOAD {profile.Key} {game}");
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var catalog = await _referenceService.ReadTextureCatalogAsync(game, cancellationToken);
            if (cancellationToken.IsCancellationRequested || Editor != editor)
            {
                return;
            }

            if (IsPlayerWorkspace)
            {
                var references = _referenceCatalog.Textures.Concat(_referenceCatalog.SkeletalMeshes).Select(value => value.Identity)
                    .Concat(catalog.Candidates.SelectMany(candidate => candidate.Occurrences.Append(candidate.EffectiveOccurrence)
                        .Select(occurrence => new AssetIdentity(occurrence.PackagePath, candidate.InstancedPath,
                            occurrence.ExportUIndex, "Texture2D"))))
                    .Concat(catalog.AttachmentMeshes.Select(candidate => new AssetIdentity(candidate.EffectiveOccurrence.PackagePath,
                        candidate.CanonicalPath, candidate.EffectiveOccurrence.ExportUIndex, "SkeletalMesh")))
                    .Concat(editor.CreateDraft().MaterialOverrides.Textures.Where(value => value.TextureReference is not null)
                        .Select(value => value.TextureReference!))
                    .Concat(editor.AttachmentMeshes.Select(value => value.Value).OfType<AssetIdentity>()).ToArray();
                var available = await Task.Run(() => _packageContextService.FindAvailablePlayerAssets(game, references,
                    cancellationToken), cancellationToken);
                if (cancellationToken.IsCancellationRequested || Editor != editor) return;
                editor.RestrictPlayerReferences(available);
            }
            editor.UpdateRegistryTextureCandidates(catalog.Candidates, catalogProfile, catalog.IsAvailable);
            editor.UpdateRegistryAttachmentMeshes(catalog.AttachmentMeshes, IsPlayerWorkspace);
            AppLog.Verbose(catalog.IsAvailable
                ? $"REGISTRY:LOADED {profile.Key} COUNT:{catalog.Candidates.Count} SEC:{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F1}"
                : $"REGISTRY:UNAVAILABLE {profile.Key} {game}");
        }
        catch (OperationCanceledException)
        {
            AppLog.Verbose($"REGISTRY:CANCELLED {profile.Key} {game}");
        }
        catch (Exception exception)
        {
            AppLog.Warning($"REGISTRY:LOAD-FAILED {profile.Key} {game} REASON:{exception.Message}");
        }
    }

    public void RefreshTextureRegistry(MorphFaceGame game)
    {
        if (Editor is not { } editor || _loadedFace?.Game != game ||
            _activeRegistryProfile is not { } profile ||
            _activeRegistryCatalogProfile is not { } catalogProfile)
            return;
        _ = LoadTextureRegistryAsync(editor, game, profile, catalogProfile, CancellationToken.None);
    }

    private void SetEditor(FaceEditorViewModel? editor, LoadedMorphFace? loaded)
    {
        if (_workspace is not null) _workspace.CareerDraftPendingInPackage = false;
        _activeRegistryProfile = null;
        _activeRegistryCatalogProfile = null;
        CancelAttachmentPreviewLoad();
        _attachmentChangeVersion++;
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
                attachment.PreviewChanged -= OnAttachmentMeshPreviewChanged;
                attachment.ManualSelectionRequested -= OnManualAttachmentMeshSelectionRequested;
            }
            Editor.Dispose();
        }
        _loadedFace = loaded;
        _preservedOtherMeshAssets = loaded?.OtherMeshes.Skip(1).ToArray() ?? [];
        Editor = editor;
        if (editor is null)
        {
            LoadedFacePath = null;
            FaceDetails = null;
            _faceDetailsPrefix = null;
            _faceDetailsHasBakedFallback = false;
            HasPreview = false;
            PreviewCleared?.Invoke(this, EventArgs.Empty);
        }
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
            foreach (var lod in loaded.BaseHead.Lods
                         .Where(lod => editor is null || editor.AvailableLodIndices.Contains(lod.LodIndex))
                         .OrderBy(lod => lod.LodIndex))
            {
                PreviewLods.Add(new PreviewLodChoice(lod.LodIndex, Loc.Format(
                    editor?.HasMorphGeometryAtLod(lod.LodIndex) == false
                        ? "loc_Shell_PreviewLodStatic"
                        : "loc_Shell_PreviewLod", lod.LodIndex)));
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
                attachment.PreviewChanged += OnAttachmentMeshPreviewChanged;
                attachment.ManualSelectionRequested += OnManualAttachmentMeshSelectionRequested;
            }
        }
        OnDirtyStateChanged();
        _saveMorphToPccCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanFixMorph));
        _fixMorphCommand.RaiseCanExecuteChanged();
    }

    private void OnEditorDirtyStateChanged(object? sender, EventArgs e) => OnDirtyStateChanged();

    private async void OnManualAttachmentMeshSelectionRequested(object? sender, ManualAttachmentSelectionEventArgs e)
    {
        if (sender is not HairMeshEditorViewModel changedSlot || Editor is not { } editor)
        {
            return;
        }
        if (changedSlot.SlotIndex != 0 || e.Selection.Identity is not { } mesh)
        {
            changedSlot.CommitManualSelection(e.Selection, e.Generation);
            return;
        }

        bool IsCurrent() => Editor == editor && changedSlot.IsManualSelectionCurrent(e.Selection, e.Generation);
        var committing = false;

        try
        {
            var prepared = await editor.PrepareManualHairScalpPairAsync(mesh);
            if (!IsCurrent()) return;

            if (prepared?.RequiresCcScalpMaterial == true)
                prepared = prepared with
                {
                    ScalpMaterial = await _referenceService.LoadLe3CcScalpMaterialAsync(prepared.FemaleScalp)
                };

            if (!IsCurrent()) return;
            if (prepared is not null &&
                !_dialogs.ConfirmHairScalpTextureSwitch(mesh.InstancedPath, prepared.RequiresCcScalpMaterial))
                prepared = null;
            if (IsCurrent())
            {
                committing = true;
                editor.CommitManualHairSelection(e.Selection, e.Generation, prepared);
            }
        }
        catch (Exception exception)
        {
            if (Editor != editor || !committing && !IsCurrent()) return;
            // The original workflow retained the chosen mesh when paired preparation failed.
            if (!committing) editor.CommitManualHairSelection(e.Selection, e.Generation, null);
            AppLog.Error($"HAIR:SCALP-PREPARE-FAILED {mesh.InstancedPath}", exception);
            SetEditorError(Loc.Format("loc_Shell_Error_MatchingScalpTexturesCouldNotPrepare", UserFacingMessageFormatter.Format(exception)));
        }
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not FaceEditorViewModel editor) return;
        switch (e.PropertyName)
        {
            case nameof(FaceEditorViewModel.CanExportTseMaterials):
            case nameof(FaceEditorViewModel.CanImportTseMaterials):
                RaiseMaterialFileCanExecuteChanged();
                break;
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
        RaiseMaterialFileCanExecuteChanged();
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanCommitFace));
        OnPropertyChanged(nameof(PackageDisplayName));
        OnPropertyChanged(nameof(CanFixMorph));
        _commitCommand.RaiseCanExecuteChanged();
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
            AppLog.Verbose($"MORPH:REPAIR {LoadedFacePath}");
            editor.FixMorph();
        }
        catch (Exception exception)
        {
            if (editor.HasPendingRepair)
            {
                ReportPostRepairPreviewFailure(editor, exception);
                return;
            }
            AppLog.Error($"MORPH:REPAIR-FAILED {LoadedFacePath}", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_MorphCouldNotRepair", UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text("loc_Shell_Status_MorphRepairFailedFaceUnchanged");
            return;
        }

        var report = editor.Evaluation.OriginalOracleReport;
        SetGeometryDetails(Loc.Format("loc_Shell_FaceDetails_OracleRepaired", report?.MaximumError ?? 0));
        try
        {
            PreviewSceneReady?.Invoke(CreateCurrentPreviewScene(), false);
        }
        catch (Exception exception)
        {
            ReportPostRepairPreviewFailure(editor, exception);
            return;
        }
        Status = Loc.Format("loc_Shell_Status_MorphFixed", SelectedFace?.DisplayName ?? Loc.Text("loc_Shell_Word_Morph"));
        AppLog.Information($"MORPH:REPAIRED {LoadedFacePath} MAX-ERROR:{report?.MaximumError ?? 0:G6}");
    }

    private void ReportPostRepairPreviewFailure(FaceEditorViewModel editor, Exception exception)
    {
        var report = editor.Evaluation.OriginalOracleReport;
        SetGeometryDetails(Loc.Format("loc_Shell_FaceDetails_OracleRepaired", report?.MaximumError ?? 0));
        AppLog.Error(
            $"MORPH:REPAIRED {LoadedFacePath} PREVIEW:FAILED",
            exception);
        ErrorMessage = Loc.Format("loc_Shell_Error_MorphRepairedPreviewCouldNotRefresh", UserFacingMessageFormatter.Format(exception));
        Status = Loc.Text("loc_Shell_Status_MorphRepairedPreviewRefreshFailed");
    }

    private void MarkWorkspaceChanged(string facePath)
    {
        _workspace!.MarkChanged(facePath);
        OnDirtyStateChanged();
    }

    private void OnEditorPreviewChanged(object? sender, EventArgs e)
    {
        if (Editor is not null && _loadedFace is not null)
        {
            if (Editor.UsesLiveDeformationPreview)
            {
                if (Editor.CanEditMorphFeatures && _faceDetailsHasBakedFallback)
                {
                    var report = Editor.Evaluation.OriginalOracleReport;
                    SetGeometryDetails(Loc.Format("loc_Shell_FaceDetails_OracleRepaired", report?.MaximumError ?? 0));
                    _faceDetailsHasBakedFallback = false;
                    Status = Loc.Text("loc_Shell_Status_MorphRepairRestored");
                }
                PreviewDeformationReady?.Invoke(
                    _sceneFactory.CreateUpdate(_loadedFace, Editor.Evaluation, PreviewLod));
            }
            else
            {
                PreviewSceneReady?.Invoke(CreateCurrentPreviewScene(), false);
                if (Editor.CanFixMorph)
                {
                    SetGeometryDetails(Loc.Format("loc_Shell_FaceDetails_BakedFallback",
                        UserFacingMessageFormatter.FormatEditBlockReason(Editor.EditBlock)));
                    _faceDetailsHasBakedFallback = true;
                    Status = Loc.Text("loc_Shell_Status_MorphRepairUndone");
                }
            }
        }
    }

    private void SetGeometryDetails(string geometryDetails)
    {
        FaceDetails = string.IsNullOrEmpty(_faceDetailsPrefix)
            ? geometryDetails
            : Loc.Format("loc_Shell_FaceDetails_WithGeometryDetails", _faceDetailsPrefix, geometryDetails);
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

        CancelAttachmentPreviewLoad();
        var version = ++_attachmentChangeVersion;
        var editor = Editor;
        var loadedFace = _loadedFace;
        var packagePath = WorkspacePackagePath;
        var detachedPreview = IsDetachedMeshWorkspace;
        var references = editor.AttachmentMeshes.Select(mesh => mesh.Value).ToArray();
        try
        {
            var attachments = await LoadAttachmentsAsync(references, loadedFace, packagePath, detachedPreview);

            if (version != _attachmentChangeVersion || Editor != editor || _loadedFace != loadedFace)
            {
                return;
            }

            var source = loadedFace with
            {
                Document = loadedFace.Document with
                {
                    HairMeshReference = references[0],
                    OtherMeshReferences = references.Skip(1)
                        .Concat(loadedFace.Document.OtherMeshReferences.Skip(1))
                        .ToArray()
                }
            };
            var prepared = PrepareAttachmentScene(editor, source, attachments, detachedPreview);
            _suppressMaterialPreview = true;
            if (prepared.Surface is not null) editor.Material.ApplyMaterialSurface(prepared.Surface);
            _loadedFace = prepared.Face;
            PreviewSceneReady?.Invoke(prepared.Scene, false);
            Status = changedSlot.Value is null
                ? Loc.Format("loc_Shell_Status_AttachmentCleared", changedSlot.Label)
                : Loc.Format("loc_Shell_Status_AttachmentChanged", changedSlot.Label, changedSlot.Value.InstancedPath);
        }
        catch (Exception exception)
        {
            if (version == _attachmentChangeVersion)
            {
                AppLog.Error($"ATTACHMENT:CHANGE-FAILED SLOT:{changedSlot.Label}", exception);
                SetEditorError(Loc.Format("loc_Shell_Error_AttachmentMeshCouldNotChange", UserFacingMessageFormatter.Format(exception)));
            }
        }
        finally
        {
            _suppressMaterialPreview = false;
        }
    }

    private async void OnAttachmentMeshPreviewChanged(object? sender, EventArgs e)
    {
        if (Editor is null || _loadedFace is null ||
            sender is not HairMeshEditorViewModel changedSlot ||
            !IsDetachedMeshWorkspace && WorkspacePackagePath is null)
        {
            return;
        }

        CancelAttachmentPreviewLoad();
        var version = ++_attachmentChangeVersion;
        var editor = Editor;
        var loadedFace = _loadedFace;
        var previewSelection = changedSlot.PreviewSelection;
        if (previewSelection is null)
        {
            PreviewSceneReady?.Invoke(CreateCurrentPreviewScene(), false);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _attachmentPreviewCancellation = cancellation;
        var packagePath = WorkspacePackagePath;
        var detachedPreview = IsDetachedMeshWorkspace;
        var references = editor.AttachmentMeshes
            .Select(mesh => ReferenceEquals(mesh, changedSlot)
                ? previewSelection.Identity
                : mesh.Value)
            .ToArray();
        try
        {
            var attachments = await LoadAttachmentsAsync(
                references, loadedFace, packagePath, detachedPreview, cancellation.Token);

            if (version != _attachmentChangeVersion || Editor != editor || _loadedFace != loadedFace ||
                !ReferenceEquals(changedSlot.PreviewSelection, previewSelection))
            {
                return;
            }

            var prepared = PrepareAttachmentScene(editor, loadedFace, attachments, detachedPreview);
            PreviewSceneReady?.Invoke(prepared.Scene, false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (version == _attachmentChangeVersion)
            {
                AppLog.Error($"ATTACHMENT:PREVIEW-FAILED SLOT:{changedSlot.Label}", exception);
                PreviewSceneReady?.Invoke(CreateCurrentPreviewScene(), false);
            }
        }
        finally
        {
            if (ReferenceEquals(_attachmentPreviewCancellation, cancellation))
            {
                _attachmentPreviewCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async Task<LoadedAttachment?[]> LoadAttachmentsAsync(
        IReadOnlyList<AssetIdentity?> references,
        LoadedMorphFace loadedFace,
        string? packagePath,
        bool detachedPreview,
        CancellationToken cancellationToken = default)
    {
        var attachments = new LoadedAttachment?[references.Count];
        for (var index = 0; index < references.Count; index++)
        {
            if (references[index] is not { } reference) continue;
            attachments[index] = detachedPreview ||
                                 !Path.GetFullPath(reference.PackagePath).Equals(
                                     Path.GetFullPath(packagePath!), StringComparison.OrdinalIgnoreCase)
                ? await _referenceService.LoadDetachedAttachmentAsync(
                    reference.PackagePath, reference.InstancedPath, cancellationToken)
                : await _referenceService.LoadAttachmentAsync(
                    packagePath!, loadedFace.Document.Source.InstancedPath,
                    reference.InstancedPath, cancellationToken);
        }
        return attachments;
    }

    private (LoadedMorphFace Face, HeadPreviewScene Scene, PreparedMaterialSurface? Surface) PrepareAttachmentScene(
        FaceEditorViewModel editor,
        LoadedMorphFace source,
        IReadOnlyList<LoadedAttachment?> attachments,
        bool detachedPreview)
    {
        var attachmentMaterials = attachments.OfType<LoadedAttachment>()
            .Select(attachment => attachment.Materials)
            .Append(ResolveMeshMaterials(_preservedOtherMeshAssets, source.Materials))
            .Aggregate(ResolvedHeadMaterialSet.Empty, MergeMaterials);
        var surface = detachedPreview ? null : editor.Material.PrepareAttachmentMaterials(attachmentMaterials);
        var face = source with
        {
            HairMesh = attachments[0]?.Mesh,
            OtherMeshes = attachments.Skip(1).OfType<LoadedAttachment>()
                .Select(attachment => attachment.Mesh).Concat(_preservedOtherMeshAssets).ToArray(),
            Materials = surface?.Materials ?? MergeMaterials(
                editor.Material.Materials,
                ApplyDetachedAttachmentHairColour(attachmentMaterials, editor.Material.Materials))
        };
        // Topology, skinning and the effective materials are validated together.
        // Hover uses this same candidate without publishing its material controls.
        var scene = editor.UsesLiveDeformationPreview
            ? _sceneFactory.CreateEditable(face, editor.Evaluation, PreviewLod)
            : _sceneFactory.Create(face, PreviewLod);
        return (face, scene, surface);
    }

    private void CancelAttachmentPreviewLoad()
    {
        var cancellation = _attachmentPreviewCancellation;
        _attachmentPreviewCancellation = null;
        cancellation?.Cancel();
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
        _ = await SaveSourceFileAsync();

    private async Task CommitFaceCommandAsync() =>
        _ = await FlushEditorToWorkspaceAsync();

    private async Task<bool> FlushEditorToWorkspaceAsync()
    {
        if (Editor is null || _loadedFace is null || WorkspacePackagePath is null)
        {
            return false;
        }
        if (!Editor.IsDirty && !CareerDraftPendingInPackage)
        {
            return true;
        }

        var nativeSaveAlreadyCurrent = CareerDraftPendingInPackage && !IsDirty;
        using var saveLock = new SaveControlLock(this);
        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Text("loc_Shell_Status_UpdatingTemporaryPackageWorkspace");
        try
        {
            AppLog.Verbose($"FACE:FLUSH {LoadedFacePath} FILE:\"{WorkspacePackagePath}\"");
            var draft = Editor.CreateDraft();
            var result = await Task.Run(() => _packageWriter.SaveExisting(draft));
            MarkWorkspaceChanged(result.FaceInstancedPath);
            var reopened = await RefreshWorkspaceAsync(result.FaceInstancedPath);
            if (!reopened)
            {
                ErrorMessage = Loc.Text("loc_Shell_Error_TemporaryEditsRetainedWorkspaceReloadFailed");
                Status = Loc.Text("loc_Shell_Status_WorkspaceUpdatedReloadFailed");
                return false;
            }
            Status = Loc.Format("loc_Shell_Status_TemporaryEditsUpdated", result.FaceInstancedPath);
            if (nativeSaveAlreadyCurrent)
            {
                _workspace!.AcceptSavedChanges();
                OnDirtyStateChanged();
            }
            AppLog.Information($"FACE:UPDATED {result.FaceInstancedPath} VERIFIED");
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error($"FACE:UPDATE-FAILED {LoadedFacePath}", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_TemporaryEditsCouldNotUpdate", UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text("loc_Shell_Status_WorkspaceUpdateFailedSourceUnmodified");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> CommitWorkspaceAsync(bool showConfirmation, string? destination = null)
    {
        if (PackageWorkspace is null || PackagePath is null)
        {
            return false;
        }
        if (!PackageWorkspace.CanCommit)
        {
            ErrorMessage = Loc.Text("loc_Shell_Error_StandaloneCannotOverwritePlayerTemplate");
            Status = Loc.Text("loc_Shell_Status_UseSaveToPccForStandaloneImport");
            return false;
        }
        using var saveLock = new SaveControlLock(this);
        if (Editor?.IsDirty == true && !await FlushEditorToWorkspaceAsync())
        {
            return false;
        }
        if (destination is null && !_workspace!.HasStagedChanges)
        {
            return true;
        }

        IsBusy = true;
        ErrorMessage = null;
        if (destination is null) Status = Loc.Text("loc_Shell_Status_CommittingAndVerifyingPackageChanges");
        try
        {
            await Task.Run(() => PackageWorkspace.Commit(destination));
            _workspace!.AcceptSavedChanges();
            if (destination is null) OnDirtyStateChanged();
            else OnWorkspaceChanged();
            Status = Loc.Format("loc_Shell_Status_PackageSaved", PackageName);
            if (showConfirmation)
            {
                _dialogs.ShowInformation(
                    Loc.Text("loc_Shell_DialogTitle_PackageSaved"),
                    Loc.Format("loc_Shell_Dialog_PackageSaved", PackagePath));
            }
            AppLog.Information($"PCC:SAVED FILE:\"{PackagePath}\" VERIFIED");
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error(destination is null ? $"PCC:SAVE-FAILED FILE:{PackagePath}" : "FILE:SAVE-AS-FAILED", exception);
            ErrorMessage = Loc.Format(destination is null ? "loc_Shell_Error_PackageChangesCouldNotCommit" : "loc_FileSave_Failed",
                UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text(destination is null ? "loc_Shell_Status_SaveFailedTemporaryEditsRetainedSourceUnmodified" : "loc_FileSave_EditsRetained");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task<bool> EnsureCanAbandonEditorChangesAsync() => ConfirmUnsavedChangesAsync(wholeWorkspace: false);
    private Task<bool> EnsureCanAbandonWorkspaceAsync() => ConfirmUnsavedChangesAsync(wholeWorkspace: true);

    private async Task<bool> ConfirmUnsavedChangesAsync(bool wholeWorkspace)
    {
        var hasChanges = wholeWorkspace ? IsDirty : Editor?.IsDirty == true || CareerWorkspace?.HasPendingHeadImport == true;
        if (!hasChanges) return true;
        var subject = wholeWorkspace
            ? IsStandaloneWorkspace ? LoadedFacePath ?? PackageName : PackagePath ?? Loc.Text("loc_Shell_UnsavedChanges_OpenPackage")
            : LoadedFacePath ?? Loc.Text("loc_Shell_UnsavedChanges_LoadedBioMorphFace");
        var scope = CareerWorkspace is not null || CanSaveOriginalRon ? UnsavedChangesScope.SourceFile
            : IsDetachedMeshWorkspace ? UnsavedChangesScope.DetachedMaterials
            : IsStandaloneWorkspace ? UnsavedChangesScope.StandaloneFace
            : wholeWorkspace ? UnsavedChangesScope.Package : UnsavedChangesScope.Face;
        return _dialogs.ConfirmUnsavedChanges(subject, scope) switch
        {
            UnsavedChangesChoice.Save => IsStandaloneWorkspace ? await SaveSourceFileAsync()
                : wholeWorkspace ? await CommitWorkspaceAsync(showConfirmation: false) : await FlushEditorToWorkspaceAsync(),
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

    internal async Task CloseWorkspaceAsync()
    {
        if (!HasWorkspace || !await EnsureCanAbandonWorkspaceAsync())
        {
            return;
        }

        ClearWorkspace();
    }

    private void ClearWorkspace()
    {
        _npcImportCancellation?.Cancel();
        _npcImportCancellation?.Dispose();
        _npcImportCancellation = null;
        ReplaceWorkspace(null);
        _referenceCatalog = new PackageReferenceCatalog([], []);
        _preservedOtherMeshAssets = [];
        _previewCameraFamily = null;
        _loadedSpeciesKey = null;
        SelectedFace = null;
        Faces.Clear();
        FaceSearchText = string.Empty;
        FaceDetails = null;
        _faceDetailsPrefix = null;
        _faceDetailsHasBakedFallback = false;
        LoadedFacePath = null;
        HasPreview = false;
        ErrorMessage = null;
        Status = Loc.Text("loc_Shell_Status_OpenAPackageToBegin");
        OnDirtyStateChanged();
        RaiseFaceContextCanExecuteChanged();
        PreviewCleared?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveMorphToPccCommandAsync()
    {
        if (SelectedFace is not { } source ||
            !IsDetachedMeshWorkspace && !await EnsureContextTargetLoadedAsync(source))
        {
            return;
        }
        _ = await SaveMorphToPccAsync();
    }

    private async Task<bool> SaveMorphToPccAsync()
    {
        if (IsDetachedMeshWorkspace)
        {
            return await SaveDetachedMeshToPccAsync();
        }
        var workspacePath = WorkspacePackagePath;
        if (Editor is null || _loadedFace is null || PackagePath is null || workspacePath is null)
        {
            return false;
        }
        var sourceName = SelectedFace?.ObjectName ??
                         _loadedFace.Document.Source.InstancedPath.Split('.').Last();
        var request = _dialogs.ChooseMorphPackageDestination(
            $"{sourceName}.pcc",
            _workspace!.BrowsePath);
        if (request is null)
        {
            return false;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Text("loc_Shell_Status_ExportingMorphAndDependencies");
        try
        {
            var draft = Editor.CreateDraft();
            AppLog.Verbose($"PCC:EXPORT {LoadedFacePath} FILE:\"{request.DestinationPackagePath}\" NEW:{request.CreateNewPackage}");
            var result = await Task.Run(() => _packageWriter.SaveMorphToPackage(
                draft,
                workspacePath,
                request.DestinationPackagePath,
                request.CreateNewPackage));
            Status = result.Warnings.Count == 0
                ? Loc.Format("loc_Shell_Status_MorphExportedAndVerified", result.FaceInstancedPath)
                : Loc.Format("loc_Shell_Status_MorphExportedAndVerifiedWithWarnings", result.FaceInstancedPath, result.Warnings.Count);
            if (result.Warnings.Count > 0)
            {
                AppLog.Warning(
                    $"PCC:EXPORTED {result.FaceInstancedPath} WARNINGS:{result.Warnings.Count} REASONS:" +
                    string.Join("; ", result.Warnings));
            }
            var warningSummary = result.Warnings.Count == 0
                ? string.Empty
                : Loc.Format("loc_Shell_Dialog_DependencyWarningSummary", result.Warnings.Count);
            _dialogs.ShowInformation(
                Loc.Text("loc_Shell_DialogTitle_MorphPackageSaved"),
                Loc.Format("loc_Shell_Dialog_MorphPackageSaved", result.PackagePath, result.FaceInstancedPath,
                    result.LodCount, result.TextureReferenceCount, warningSummary));
            AppLog.Information($"PCC:EXPORTED {result.FaceInstancedPath} FILE:\"{result.PackagePath}\" VERIFIED");
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error($"PCC:EXPORT-FAILED FACE:{LoadedFacePath} FILE:{PackagePath}", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_MorphCouldNotExport", UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text("loc_Shell_Status_SaveFailedSourcePccUnmodified");
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> SaveDetachedMeshToPccAsync()
    {
        if (Editor is not { CustomMaterials: { } workspace } editor ||
            DetachedMeshSource is null || StandaloneGame is not { } game ||
            SelectedFace is null)
        {
            return false;
        }

        var destination = _dialogs.ChooseMeshPackageDestination(
            $"{SelectedFace.ObjectName}.pcc",
            DetachedMeshSource.SourcePath);
        if (destination is null)
        {
            return false;
        }

        IsBusy = true;
        ErrorMessage = null;
        Status = Loc.Text("loc_Shell_Status_CreatingMeshMaterialsAndDependencies");
        try
        {
            var textureCatalog = await _referenceService.ReadTextureCatalogAsync(game);
            if (!textureCatalog.IsAvailable)
            {
                throw new InvalidOperationException(
                    Loc.Text("loc_Shell_Error_SaveToPccRequiresTextureDatabase"));
            }
            var request = new CustomMeshPccSaveRequest(
                DetachedMeshSource,
                workspace,
                editor.Material.CaptureInterchangeData(),
                game,
                destination,
                SelectedFace.ObjectName,
                _customMeshBinaryWriter,
                textureCatalog.Candidates);
            var result = await Task.Run(() => _customMeshPccMaterializer.Save(request));
            Status = Loc.Format("loc_Shell_Status_CustomMeshSavedAndVerified", result.MeshPath, result.Materials.Count);
            var warningSummary = result.Warnings.Count == 0
                ? string.Empty
                : Loc.Format("loc_Shell_Dialog_DependencyWarningSummary", result.Warnings.Count);
            foreach (var warning in result.Warnings)
            {
                AppLog.Warning(warning);
            }
            _dialogs.ShowInformation(
                Loc.Text("loc_Shell_DialogTitle_MeshPackageSaved"),
                Loc.Format("loc_Shell_Dialog_MeshPackageSaved", result.PackagePath, result.MeshPath,
                    result.Materials.Count, warningSummary));
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error($"PCC:CUSTOM-MESH-EXPORT-FAILED FILE:{DetachedMeshSource.SourcePath}", exception);
            ErrorMessage = Loc.Format("loc_Shell_Error_MeshCouldNotSave", UserFacingMessageFormatter.Format(exception));
            Status = Loc.Text("loc_Shell_Status_SaveFailedSourceMeshAndMaterialEditUnmodified");
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

    private void ReplaceWorkspace(EditorWorkspace? workspace)
    {
        CancelPendingLoad();
        SetEditor(null, null);
        if (CareerWorkspace is not null) CareerWorkspace.ImportStateChanged -= OnEditorDirtyStateChanged;
        if (PackageWorkspace is not null) _referenceService.InvalidatePackage(PackageWorkspace.WorkingPath);
        _workspace?.Dispose();
        _workspace = workspace;
        if (CareerWorkspace is not null) CareerWorkspace.ImportStateChanged += OnEditorDirtyStateChanged;
        OnWorkspaceChanged();
    }

    private void OnWorkspaceChanged()
    {
        OnPropertyChanged(nameof(PackagePath));
        OnPropertyChanged(nameof(PackageName));
        OnPropertyChanged(nameof(WorkspaceGameIcon));
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(IsDetachedMeshWorkspace));
        OnDirtyStateChanged();
        _saveAsCommand.RaiseCanExecuteChanged();
        _closeWorkspaceCommand.RaiseCanExecuteChanged();
        RaiseFaceContextCanExecuteChanged();
    }

    public void Dispose()
    {
        _npcImportCancellation?.Cancel();
        _npcImportCancellation?.Dispose();
        _npcImportCancellation = null;
        ReplaceWorkspace(null);
        _previewLoadService.Dispose();
    }
}

public sealed record PreviewLightingChoice(HeadPreviewLightingPreset Preset, string Name);
public sealed record PreviewLodChoice(int LodIndex, string Name);
public sealed record RecentFileItem(string FilePath, ICommand OpenCommand);
