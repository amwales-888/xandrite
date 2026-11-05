using System.Runtime.InteropServices;
using Xandrite.Geometry;

namespace Xandrite.Loading;

/// <summary>Turns the bytes of an .stl, .3mf or .obj file into a <see cref="Mesh"/>.</summary>
public static class ModelLoader
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stl", "3mf", "obj" };

    /// <summary>
    /// Which way up a file of this kind usually is, for the automatic view. STL and 3MF are made for printing and are
    /// Z-up. OBJ files are usually Y-up (Blender, ZBrush and Maya export them that way); a model that is the other way
    /// up can be turned in the viewer, which saves the choice for that model.
    /// </summary>
    public static bool UsuallyZUp(string ext) => !ext.TrimStart('.').Equals("obj", StringComparison.OrdinalIgnoreCase);

    /// <param name="data">The whole file.</param>
    /// <param name="ext">"stl", "3mf" or "obj" (a leading dot is allowed, case does not matter).</param>
    public static Mesh Load(ReadOnlyMemory<byte> data, string ext)
    {
        switch (ext.TrimStart('.').ToLowerInvariant())
        {
            case "stl":
                return StlReader.Read(data.Span);
            case "3mf":
                using (var stream = AsStream(data)) return ThreeMfReader.Read(stream);
            case "obj":
                return ObjReader.Read(data.Span);
            default:
                throw new ModelFormatException("Unsupported file type: " + ext);
        }
    }

    public static Mesh LoadFile(string path) => Load(File.ReadAllBytes(path), Path.GetExtension(path));

    private static MemoryStream AsStream(ReadOnlyMemory<byte> data) =>
        MemoryMarshal.TryGetArray(data, out var segment) && segment.Array is not null
            ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(data.ToArray(), writable: false);
}
