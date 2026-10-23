using System.IO;
using MorphFaceEditor.Core.Materials;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;

namespace MorphFaceEditor.Rendering;

/// <summary>Owns uploaded material images and their colour-space views; borrows the preview device/context.</summary>
internal sealed class HeadPreviewTextureCache(Device device, DeviceContext context) : IDisposable
{
    private const long MaximumTextureBytes = 512L * 1024 * 1024;
    private readonly Dictionary<string, GpuTexture> _textures = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _activeKeys = new(StringComparer.OrdinalIgnoreCase);
    private long _useSerial;

    public int UploadCount { get; private set; }
    public long ByteCount { get; private set; }

    public void SetActiveKeys(HashSet<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        // The renderer transfers its freshly derived case-insensitive key set.
        _activeKeys = keys;
    }

    public ShaderResourceView? GetView(HeadPreviewTexture? texture)
    {
        if (texture is null) return null;
        if (FindView(texture.Key, texture.ColorSpace) is { } cached) return cached;

        var mips = GetValidMips(texture);
        var subresources = mips.Select(mip => new Subresource(
            mip.Rgba8, checked(mip.Width * 4))).ToArray();
        return Upload(texture.Key, texture.ColorSpace, texture.Width, texture.Height,
            mips.Count, cube: false, subresources);
    }

    public ShaderResourceView? GetCubeView(HeadPreviewCubeTexture? texture)
    {
        if (texture is null) return null;
        if (FindView(texture.Key, texture.ColorSpace) is { } cached) return cached;
        if (texture.Rgba8Faces.Count != 6)
        {
            throw new InvalidDataException($"Cube texture '{texture.Key}' must contain exactly six faces.");
        }

        var mips = GetValidCubeMips(texture);
        // D3D subresources are face-major, with each face's authored mips together.
        var subresources = Enumerable.Range(0, 6).SelectMany(face => mips.Select(mip =>
            new Subresource(mip.Rgba8Faces[face], checked(mip.Size * 4)))).ToArray();
        return Upload(texture.Key, texture.ColorSpace, texture.Size, texture.Size,
            mips.Count, cube: true, subresources);
    }

    private ShaderResourceView? FindView(string key, TextureColorSpace colorSpace)
    {
        if (!_textures.TryGetValue(key, out var texture)) return null;
        texture.LastUsed = ++_useSerial;
        return texture.GetView(device, colorSpace);
    }

    private ShaderResourceView Upload(
        string key,
        TextureColorSpace colorSpace,
        int width,
        int height,
        int mipCount,
        bool cube,
        IReadOnlyList<Subresource> subresources)
    {
        var streams = new List<DataStream>(subresources.Count);
        Texture2D? resource = null;
        GpuTexture? texture = null;
        var published = false;
        try
        {
            var rectangles = new DataRectangle[subresources.Count];
            for (var index = 0; index < subresources.Count; index++)
            {
                var subresource = subresources[index];
                var stream = new DataStream(subresource.Pixels.Length, true, true);
                streams.Add(stream);
                stream.Write(subresource.Pixels, 0, subresource.Pixels.Length);
                stream.Position = 0;
                rectangles[index] = new DataRectangle(stream.DataPointer, subresource.RowPitch);
            }

            resource = new Texture2D(device, new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = mipCount,
                ArraySize = cube ? 6 : 1,
                Format = Format.R8G8B8A8_Typeless,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = cube ? ResourceOptionFlags.TextureCube : ResourceOptionFlags.None
            }, rectangles);
            var byteCount = subresources.Sum(subresource => (long)subresource.Pixels.Length);
            texture = new GpuTexture(resource, byteCount, ++_useSerial);
            var view = texture.GetView(device, colorSpace);
            TrimToBudget(byteCount);
            _textures.Add(key, texture);
            ByteCount += byteCount;
            UploadCount++;
            published = true;
            return view;
        }
        finally
        {
            if (!published)
            {
                if (texture is not null) texture.Dispose();
                else resource?.Dispose();
            }
            foreach (var stream in streams) stream.Dispose();
        }
    }

    public void TrimToBudget() => TrimToBudget(0);

    private void TrimToBudget(long incomingBytes)
    {
        while (ByteCount + incomingBytes > MaximumTextureBytes && _textures.Count > 0)
        {
            KeyValuePair<string, GpuTexture>? oldest = null;
            foreach (var entry in _textures)
            {
                if (!_activeKeys.Contains(entry.Key) &&
                    (oldest is null || entry.Value.LastUsed < oldest.Value.Value.LastUsed)) oldest = entry;
            }
            // Active sources may exceed the budget; never discard the current scene's images.
            if (oldest is null) break;
            Remove(oldest.Value.Key);
        }
    }

    public void PruneInactive() => Remove(_textures.Keys.Where(key => !_activeKeys.Contains(key)).ToArray());

    public void Clear()
    {
        Remove(_textures.Keys.ToArray());
        _activeKeys.Clear();
    }

    private void Remove(params string[] keys)
    {
        if (keys.Length == 0) return;
        // Context bindings retain native references independently of our view wrappers.
        context.PixelShader.SetShaderResources(0, new ShaderResourceView?[10]);
        context.PixelShader.SetShaderResources(15, new ShaderResourceView?[2]);
        foreach (var key in keys)
        {
            if (!_textures.Remove(key, out var texture)) continue;
            ByteCount -= texture.ByteCount;
            texture.Dispose();
        }
    }

    public void Dispose() => Clear();

    private static IReadOnlyList<DecodedTextureMip> GetValidMips(HeadPreviewTexture texture)
    {
        var candidates = texture.Mips is { Count: > 0 }
            ? texture.Mips
            : [new DecodedTextureMip(texture.Width, texture.Height, texture.Rgba8)];
        var valid = new List<DecodedTextureMip>(candidates.Count);
        for (var level = 0; level < candidates.Count; level++)
        {
            var mip = candidates[level];
            if (mip.Width != Math.Max(1, texture.Width >> level) ||
                mip.Height != Math.Max(1, texture.Height >> level) ||
                mip.Rgba8.Length != checked(mip.Width * mip.Height * 4))
            {
                break;
            }
            valid.Add(mip);
        }
        return valid.Count > 0 ? valid : [new DecodedTextureMip(texture.Width, texture.Height, texture.Rgba8)];
    }

    private static IReadOnlyList<DecodedTextureCubeMip> GetValidCubeMips(HeadPreviewCubeTexture texture)
    {
        var candidates = texture.Mips is { Count: > 0 }
            ? texture.Mips
            : [new DecodedTextureCubeMip(texture.Size, texture.Rgba8Faces)];
        var valid = new List<DecodedTextureCubeMip>(candidates.Count);
        for (var level = 0; level < candidates.Count; level++)
        {
            var mip = candidates[level];
            var expectedSize = Math.Max(1, texture.Size >> level);
            if (mip.Size != expectedSize || mip.Rgba8Faces.Count != 6 ||
                mip.Rgba8Faces.Any(face => face.Length != checked(expectedSize * expectedSize * 4)))
            {
                break;
            }
            valid.Add(mip);
        }
        return valid.Count > 0 ? valid : [new DecodedTextureCubeMip(texture.Size, texture.Rgba8Faces)];
    }

    private readonly record struct Subresource(byte[] Pixels, int RowPitch);

    private sealed class GpuTexture(Texture2D resource, long byteCount, long lastUsed) : IDisposable
    {
        private readonly Dictionary<TextureColorSpace, ShaderResourceView> _views = [];
        public long ByteCount { get; } = byteCount;
        public long LastUsed { get; set; } = lastUsed;

        public ShaderResourceView GetView(Device device, TextureColorSpace colorSpace)
        {
            if (_views.TryGetValue(colorSpace, out var existing)) return existing;
            var textureDescription = resource.Description;
            var description = new ShaderResourceViewDescription
            {
                Format = colorSpace == TextureColorSpace.Srgb
                    ? Format.R8G8B8A8_UNorm_SRgb
                    : Format.R8G8B8A8_UNorm
            };
            if ((textureDescription.OptionFlags & ResourceOptionFlags.TextureCube) != 0)
            {
                description.Dimension = ShaderResourceViewDimension.TextureCube;
                description.TextureCube = new ShaderResourceViewDescription.TextureCubeResource
                {
                    MostDetailedMip = 0,
                    MipLevels = textureDescription.MipLevels
                };
            }
            else
            {
                description.Dimension = ShaderResourceViewDimension.Texture2D;
                description.Texture2D = new ShaderResourceViewDescription.Texture2DResource
                {
                    MostDetailedMip = 0,
                    MipLevels = textureDescription.MipLevels
                };
            }
            var view = new ShaderResourceView(device, resource, description);
            try
            {
                _views.Add(colorSpace, view);
                return view;
            }
            catch
            {
                view.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            foreach (var view in _views.Values) view.Dispose();
            resource.Dispose();
        }
    }
}
