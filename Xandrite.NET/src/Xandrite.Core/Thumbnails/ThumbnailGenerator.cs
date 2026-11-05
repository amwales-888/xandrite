using System.Diagnostics;
using Xandrite.Framing;
using Xandrite.Geometry;
using Xandrite.Imaging;
using Xandrite.Loading;
using Xandrite.Rendering;

namespace Xandrite.Thumbnails;

/// <summary>One model to draw. Mirrors the job the JS app's thumbnail queue sent to its hidden render window.</summary>
/// <param name="Data">The whole model file.</param>
/// <param name="Format">"stl", "3mf" or "obj".</param>
/// <param name="Name">File name, recorded in the PNG.</param>
/// <param name="Sha256">Content hash, recorded in the PNG. May be empty when drawing a file outside the library.</param>
/// <param name="View">A view the person saved, or null for the automatic view.</param>
public sealed record ThumbnailJob(ReadOnlyMemory<byte> Data, string Format, string Name, string Sha256 = "", StoredView? View = null, int SizePx = Look.ThumbnailSize);

public sealed record ThumbnailResult(byte[] Png, int Triangles, string ViewStateJson, TimeSpan LoadTime, TimeSpan RenderTime, TimeSpan EncodeTime);

/// <summary>
/// Makes a thumbnail: reads the model, frames it, draws it and returns a PNG with the view settings inside.
/// This is the .NET counterpart of the JS app's renderer/thumbnail-worker.js.
/// </summary>
public sealed class ThumbnailGenerator(IModelRenderer renderer)
{
    /// <exception cref="ModelFormatException">The model cannot be read. Mark it as failed.</exception>
    /// <exception cref="RendererException">The renderer failed. Pause and try again with a new renderer.</exception>
    public ThumbnailResult Generate(ThumbnailJob job)
    {
        var clock = Stopwatch.StartNew();
        Mesh mesh = ModelLoader.Load(job.Data, job.Format);
        var loadTime = clock.Elapsed;

        var (png, json, renderTime, encodeTime) = Draw(mesh, job.View, job.SizePx,
            new ThumbnailFileInfo(job.Name, job.Format.TrimStart('.'), job.Data.Length, job.Sha256), ModelLoader.UsuallyZUp(job.Format));
        return new ThumbnailResult(png, mesh.TriangleCount, json, loadTime, renderTime, encodeTime);
    }

    /// <summary>Draws an already loaded mesh. Useful when the caller wants to keep the mesh, as the preview app does.</summary>
    /// <param name="defaultZUp">Orientation for the automatic view (a stored view carries its own).</param>
    public (byte[] Png, string ViewStateJson, TimeSpan RenderTime, TimeSpan EncodeTime) Draw(Mesh mesh, StoredView? view, int sizePx, ThumbnailFileInfo? file, bool defaultZUp = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sizePx, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sizePx, 4096);

        // A view the person set up by hand is used when there is one; otherwise the automatic default view.
        bool zUp = view?.ZUp ?? defaultZUp;
        var frame = ViewMath.OrientAndCentre(mesh.Bounds, zUp);
        var camera = view is null ? ViewMath.DefaultCamera(frame.Radius, 1f) : ViewMath.StoredCamera(view, frame.Radius, 1f);

        var clock = Stopwatch.StartNew();
        RenderedImage image;
        using (var gpuMesh = renderer.Upload(mesh))
        {
            image = renderer.Render(gpuMesh, new RenderRequest(frame.Transform, camera, sizePx, sizePx));
        }
        var renderTime = clock.Elapsed;

        clock.Restart();
        string json = ViewState.Build(camera, zUp, sizePx, auto: view is null, file);
        byte[] png = PngEncoder.EncodeRgb(image.Rgba, image.Width, image.Height, image.Width * 4,
            [new KeyValuePair<string, string>(Look.StateKeyword, json)]);
        return (png, json, renderTime, clock.Elapsed);
    }
}
