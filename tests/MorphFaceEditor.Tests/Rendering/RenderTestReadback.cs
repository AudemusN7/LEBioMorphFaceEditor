using System.Reflection;
using System.Runtime.InteropServices;
using MorphFaceEditor.Rendering;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using D3DDevice = SharpDX.Direct3D11.Device;
using D3DMapFlags = SharpDX.Direct3D11.MapFlags;

namespace MorphFaceEditor.Tests;

internal static class RenderTestReadback
{
    internal static Texture2D GetTexture(HeadPreviewRenderer renderer, string surfaceName)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var rendererType = typeof(HeadPreviewRenderer);
        object? surface;
        if (surfaceName is "GradingLut" or "ShadowMap")
        {
            surface = rendererType.GetField(surfaceName == "GradingLut" ? "_gradingLut" : "_shadowMap", privateInstance)
                ?.GetValue(renderer);
        }
        else
        {
            var targets = rendererType.GetField("_targets", privateInstance)?.GetValue(renderer);
            surface = targets?.GetType().GetProperty(surfaceName)?.GetValue(targets);
        }
        return surface?.GetType().GetProperty("Texture")?.GetValue(surface) as Texture2D
            ?? throw new InvalidOperationException($"Renderer surface '{surfaceName}' has no texture.");
    }

    internal static float[] ReadFloatTarget(HeadPreviewRenderer renderer, string surfaceName) =>
        ReadFloatTexture(renderer, surfaceName, Format.R16G16B16A16_Float).Values;

    internal static FloatTarget ReadFloatTexture(HeadPreviewRenderer renderer, string surfaceName, Format expectedFormat)
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        var rendererType = typeof(HeadPreviewRenderer);
        var source = GetTexture(renderer, surfaceName);
        var device = (D3DDevice?)rendererType.GetField("_device", privateInstance)?.GetValue(renderer)
            ?? throw new InvalidOperationException("Renderer D3D device was not found.");
        var context = (DeviceContext?)rendererType.GetField("_context", privateInstance)?.GetValue(renderer)
            ?? throw new InvalidOperationException("Renderer D3D context was not found.");
        var description = source.Description;
        if (description.Format != expectedFormat || description.SampleDescription.Count != 1)
        {
            throw new InvalidOperationException(
                $"Renderer target '{surfaceName}' must be resolved {expectedFormat}; found {description.Format} " +
                $"with {description.SampleDescription.Count} samples.");
        }

        var channelCount = expectedFormat switch
        {
            Format.R16G16_Float => 2,
            Format.R16G16B16A16_Float or Format.R32G32B32A32_Float => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(expectedFormat))
        };
        var bytesPerChannel = expectedFormat == Format.R32G32B32A32_Float ? 4 : 2;
        var stagingDescription = new Texture2DDescription
        {
            Width = description.Width,
            Height = description.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = expectedFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CpuAccessFlags = CpuAccessFlags.Read,
            OptionFlags = ResourceOptionFlags.None
        };
        using var staging = new Texture2D(device, stagingDescription);
        context.CopyResource(source, staging);
        var mapped = context.MapSubresource(staging, 0, MapMode.Read, D3DMapFlags.None);
        try
        {
            var values = new float[description.Width * description.Height * channelCount];
            var bytesPerPixel = bytesPerChannel * channelCount;
            for (var y = 0; y < description.Height; y++)
            {
                var row = mapped.DataPointer + y * mapped.RowPitch;
                for (var x = 0; x < description.Width; x++)
                for (var channel = 0; channel < channelCount; channel++)
                {
                    var address = row + x * bytesPerPixel + channel * bytesPerChannel;
                    var value = bytesPerChannel == 4
                        ? BitConverter.Int32BitsToSingle(Marshal.ReadInt32(address))
                        : (float)BitConverter.UInt16BitsToHalf(unchecked((ushort)Marshal.ReadInt16(address)));
                    values[(y * description.Width + x) * channelCount + channel] = value;
                }
            }
            return new FloatTarget(description.Width, description.Height, channelCount, values);
        }
        finally
        {
            context.UnmapSubresource(staging, 0);
        }
    }

    internal sealed record FloatTarget(int Width, int Height, int Channels, float[] Values);
}
