using System.Globalization;
using System.Numerics;
using Xandrite.Geometry;

namespace Xandrite.Loading;

/// <summary>
/// Reads Wavefront OBJ files: the vertices ("v") and the faces ("f") that join them. As with STL, faces are shaded
/// from their own geometry, so texture coordinates ("vt"), normals ("vn"), smoothing groups and the separate .mtl
/// material file are not needed and are ignored; objects and groups ("o", "g") all go into the one mesh.
///
///  - A face is a polygon of three or more corners; it is split into triangles as a fan from its first corner,
///    as three.js's OBJLoader does.
///  - A corner is "v", "v/vt", "v//vn" or "v/vt/vn"; only the vertex number is used. Numbers count from 1;
///    a negative number counts back from the last vertex read so far (-1 = the latest).
///  - Vertex colours written after the position ("v x y z r g b", 0 to 1, as MeshLab and others write them) are
///    used, as sRGB. A fourth number alone ("v x y z w") is a weight and is ignored.
///  - A line ending in "\" continues on the next line.
///  - Lines and points ("l", "p") and curves and surfaces are not drawn.
/// </summary>
public static class ObjReader
{
    public static Mesh Read(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) throw new ModelFormatException("The file is empty.");
        if (data.StartsWith("\xEF\xBB\xBF"u8)) data = data[3..];   // a UTF-8 byte-order mark

        // About 30 bytes a vertex line and twice as many faces as vertices in a typical mesh.
        var positions = new List<Vector3>(Math.Max(16, data.Length / 60));
        List<uint>? colors = null;
        var builder = new MeshBuilder(Math.Max(16, data.Length / 60));
        var corners = new List<int>(8);
        int line = 0;
        int pos = 0;

        while (pos < data.Length)
        {
            line++;
            int start = pos;
            int end = data[pos..].IndexOf((byte)'\n');
            end = end < 0 ? data.Length : pos + end;
            pos = end + 1;
            var text = TrimEnd(data[start..end]);

            // A backslash at the end joins the next line on (rare; only then is a copy made).
            if (!text.IsEmpty && text[^1] == (byte)'\\')
            {
                var joined = new List<byte>(text[..^1].ToArray());
                while (pos < data.Length)
                {
                    line++;
                    int next = data[pos..].IndexOf((byte)'\n');
                    next = next < 0 ? data.Length : pos + next;
                    var part = TrimEnd(data[pos..next]);
                    pos = next + 1;
                    joined.Add((byte)' ');
                    if (!part.IsEmpty && part[^1] == (byte)'\\')
                    {
                        joined.AddRange(part[..^1].ToArray());
                        continue;
                    }
                    joined.AddRange(part.ToArray());
                    break;
                }
                ReadLine(joined.ToArray(), line, positions, ref colors, builder, corners);
                continue;
            }
            ReadLine(text, line, positions, ref colors, builder, corners);
        }

        if (builder.TriangleCount == 0)
        {
            throw new ModelFormatException(positions.Count == 0
                ? "This is not a valid OBJ file: it has no vertices."
                : "The file contains no faces, only points or lines, so there is nothing to draw.");
        }
        return builder.Build();
    }

    private static void ReadLine(ReadOnlySpan<byte> text, int line, List<Vector3> positions, ref List<uint>? colors, MeshBuilder builder, List<int> corners)
    {
        int at = 0;
        if (!NextToken(text, ref at, out var keyword) || keyword[0] == (byte)'#') return;

        if (keyword.SequenceEqual("v"u8))
        {
            var p = new Vector3(Number(text, ref at, line), Number(text, ref at, line), Number(text, ref at, line));
            // Three more numbers are a colour; one more is a weight (ignored).
            Span<float> extra = stackalloc float[3];
            int count = 0;
            while (count < 3 && NextToken(text, ref at, out var token) && token[0] != (byte)'#')
                extra[count++] = Parse(token, line, "a vertex");
            if (count == 3)
            {
                if (colors is null)
                {
                    // Vertices read before the first coloured one keep the plain material colour.
                    colors = new List<uint>(positions.Capacity);
                    for (int i = 0; i < positions.Count; i++) colors.Add(Look.MaterialColorPacked);
                }
                colors.Add(ColorSpace.Pack(new Vector3(
                    ColorSpace.SrgbToLinear(Math.Clamp(extra[0], 0f, 1f)),
                    ColorSpace.SrgbToLinear(Math.Clamp(extra[1], 0f, 1f)),
                    ColorSpace.SrgbToLinear(Math.Clamp(extra[2], 0f, 1f)))));
            }
            else
            {
                colors?.Add(Look.MaterialColorPacked);
            }
            positions.Add(p);
        }
        else if (keyword.SequenceEqual("f"u8))
        {
            corners.Clear();
            while (NextToken(text, ref at, out var token))
            {
                if (token[0] == (byte)'#') break;
                corners.Add(Corner(token, positions.Count, line));
            }
            if (corners.Count < 3) return;   // a degenerate face: nothing to draw, as three.js skips it
            builder.HasOwnColors |= colors is not null;
            for (int i = 1; i + 1 < corners.Count; i++)
            {
                int a = corners[0], b = corners[i], c = corners[i + 1];
                if (colors is null)
                    builder.Add(positions[a], positions[b], positions[c], Look.MaterialColorPacked);
                else
                    builder.Add(positions[a], positions[b], positions[c], colors[a], colors[b], colors[c]);
            }
        }
        // Anything else (vt, vn, o, g, s, usemtl, mtllib, l, p, curves) is not needed to draw the shape.
    }

    /// <summary>The vertex number of one face corner ("7", "7/2", "7//3", "-1/..."), as an index into the vertices read so far.</summary>
    private static int Corner(ReadOnlySpan<byte> token, int vertexCount, int line)
    {
        int slash = token.IndexOf((byte)'/');
        var number = slash < 0 ? token : token[..slash];
        if (!int.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) || n == 0)
            throw new ModelFormatException($"This is not a valid OBJ file: a face on line {line:N0} has a corner that is not a vertex number.");
        int index = n > 0 ? n - 1 : vertexCount + n;
        if (index < 0 || index >= vertexCount)
            throw new ModelFormatException($"This is not a valid OBJ file: a face on line {line:N0} uses vertex {n:N0}, but only {vertexCount:N0} vertices come before it.");
        return index;
    }

    private static float Number(ReadOnlySpan<byte> text, ref int at, int line)
    {
        if (!NextToken(text, ref at, out var token) || token[0] == (byte)'#')
            throw new ModelFormatException($"This is not a valid OBJ file: the vertex on line {line:N0} has fewer than three coordinates.");
        return Parse(token, line, "the vertex");
    }

    private static float Parse(ReadOnlySpan<byte> token, int line, string what)
    {
        if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
            throw new ModelFormatException($"This is not a valid OBJ file: {what} on line {line:N0} has a coordinate that is not a number.");
        return value;
    }

    private static bool NextToken(ReadOnlySpan<byte> text, ref int at, out ReadOnlySpan<byte> token)
    {
        while (at < text.Length && IsSpace(text[at])) at++;
        int start = at;
        while (at < text.Length && !IsSpace(text[at])) at++;
        token = text[start..at];
        return !token.IsEmpty;
    }

    private static ReadOnlySpan<byte> TrimEnd(ReadOnlySpan<byte> text)
    {
        int end = text.Length;
        while (end > 0 && IsSpace(text[end - 1])) end--;
        return text[..end];
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or (byte)'\v';
}
