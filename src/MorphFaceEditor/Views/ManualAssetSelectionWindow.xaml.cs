using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MorphFaceEditor.Core.Domain;
using MorphFaceEditor.Core.Materials;
using MorphFaceEditor.LegendaryExplorer;
using MorphFaceEditor.Infrastructure;
using MorphFaceEditor.Models;
using MorphFaceEditor.Services;

namespace MorphFaceEditor.Views;

public partial class ManualAssetSelectionWindow : Window
{
    private static readonly MaterialParameterDefinition ThumbnailDefinition = new(
        "Preview", "Preview", "Preview", MaterialParameterKind.Texture,
        HeadMaterialFamily.Unknown, TextureRole: TextureRole.Diffuse,
        ColorSpace: TextureColorSpace.Srgb, AlphaPolicy: TextureAlphaPolicy.Ignore);
    private readonly string _packagePath;
    private readonly ManualAssetRow[] _rows;
    private readonly ICollectionView _view;
    private readonly MorphFacePackageReader _thumbnailReader = new();
    private readonly SemaphoreSlim _thumbnailGate = new(1, 1);

    public ManualAssetSelectionWindow(MorphFaceGame game, PackageInventory inventory,
        IReadOnlyList<ManualRegistryAsset> existing)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Closed += OnClosed;
        _packagePath = inventory.PackagePath;
        Heading.Text = $"Add custom assets to {game}";
        PackageLabel.Text = _packagePath;
        _rows = inventory.Entries
            .Where(entry => !entry.IsDefaultObject && entry.ClassName is "Texture2D" or "SkeletalMesh")
            .Select(entry => new ManualAssetRow(entry,
                existing.Any(value => Path.GetFullPath(value.PackagePath).Equals(_packagePath,
                                          StringComparison.OrdinalIgnoreCase) &&
                                      value.InstancedPath.Equals(entry.InstancedPath,
                                          StringComparison.OrdinalIgnoreCase) &&
                                      value.ClassName.Equals(entry.ClassName,
                                          StringComparison.OrdinalIgnoreCase)),
                _packagePath, LoadThumbnailAsync))
            .ToArray();
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = value => value is ManualAssetRow row &&
            (SearchBox.Text.Length == 0 ||
             row.InstancedPath.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase) ||
             row.ClassName.Contains(SearchBox.Text, StringComparison.OrdinalIgnoreCase));
        AssetList.ItemsSource = _view;
    }

    public IReadOnlyList<ManualRegistryAsset> SelectedAssets => _rows
        .Where(row => row.IsSelected && row.CanSelect)
        .Select(row => new ManualRegistryAsset(_packagePath, row.UIndex,
            row.InstancedPath, row.ClassName))
        .ToArray();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private async void OnAssetToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ManualAssetRow row } ||
            row.PreviewAsset is null)
        {
            e.Handled = true;
            return;
        }
        await row.PreviewAsset.EnsureThumbnailAsync();
    }

    private async Task<System.Windows.Media.ImageSource> LoadThumbnailAsync(string texturePath)
    {
        await _thumbnailGate.WaitAsync();
        try
        {
            return await Task.Run(() => TextureThumbnailFactory.Create(
                _thumbnailReader.LoadTexture(_packagePath, texturePath, ThumbnailDefinition)));
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        await _thumbnailGate.WaitAsync();
        try
        {
            _thumbnailReader.Dispose();
        }
        finally
        {
            _thumbnailGate.Release();
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (SelectedAssets.Count == 0)
        {
            ValidationText.Text = "Select at least one new asset.";
            return;
        }
        DialogResult = true;
    }

    private sealed class ManualAssetRow
    {
        public ManualAssetRow(PackageEntrySummary entry, bool alreadyAdded, string packagePath,
            Func<string, Task<System.Windows.Media.ImageSource>> loadThumbnail)
        {
            UIndex = entry.UIndex;
            ClassName = entry.ClassName;
            InstancedPath = entry.InstancedPath;
            CanSelect = !alreadyAdded;
            IsSelected = alreadyAdded;
            if (entry.ClassName == "Texture2D")
            {
                PreviewAsset = new PackageAssetListItem(new AssetIdentity(
                    packagePath, entry.InstancedPath, entry.UIndex, entry.ClassName));
                PreviewAsset.ConfigureThumbnailLoader(() => loadThumbnail(entry.InstancedPath));
            }
        }
        public int UIndex { get; }
        public string ClassName { get; }
        public string InstancedPath { get; }
        public bool CanSelect { get; }
        public bool IsSelected { get; set; }
        public PackageAssetListItem? PreviewAsset { get; }
    }
}
