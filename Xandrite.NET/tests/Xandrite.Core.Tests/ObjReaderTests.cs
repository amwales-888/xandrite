using System.Numerics;
using System.Text;
using Xandrite.Geometry;
using Xandrite.Loading;

namespace Xandrite.Core.Tests;

public class ObjReaderTests
{
    private static Mesh Read(string text) => ObjReader.Read(Encoding.UTF8.GetBytes(text));

    private static Vector3 Corner(Mesh mesh, int triangle, int corner) => mesh.Vertices[triangle * 3 + corner].Position;

    // A unit cube: 8 vertices, 6 four-sided faces (so 12 triangles), with the texture and normal parts an exporter writes.
    private const string Cube = """
        # Blender 4.2
        mtllib cube.mtl
        o Cube
        v 0 0 0
        v 1 0 0
        v 1 1 0
        v 0 1 0
        v 0 0 1
        v 1 0 1
        v 1 1 1
        v 0 1 1
        vt 0 0
        vt 1 0
        vt 1 1
        vn 0 0 -1
        usemtl Material
        s off
        f 1/1/1 4/2/1 3/3/1 2/1/1
        f 5/1/1 6/2/1 7/3/1 8/1/1
        f 1//1 2//1 6//1 5//1
        f 2 3 7 6
        f 3/1 4/2 8/3 7/1
        f 4 1 5 8
        """;

    [Fact]
    public void ReadsACubeOfFourSidedFaces_IgnoringTexturesNormalsAndMaterials()
    {
        var mesh = Read(Cube);
        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(Vector3.Zero, mesh.Bounds.Min);
        Assert.Equal(Vector3.One, mesh.Bounds.Max);
        Assert.False(mesh.HasOwnColors);
        Assert.Equal(Look.MaterialColorPacked, mesh.Vertices[0].Color);
    }

    [Fact]
    public void APolygonIsSplitIntoAFanFromItsFirstCorner()
    {
        var mesh = Read("v 0 0 0\nv 1 0 0\nv 2 1 0\nv 1 2 0\nv 0 1 0\nf 1 2 3 4 5\n");
        Assert.Equal(3, mesh.TriangleCount);
        Assert.Equal(new Vector3(0, 0, 0), Corner(mesh, 2, 0));
        Assert.Equal(new Vector3(1, 2, 0), Corner(mesh, 2, 1));
        Assert.Equal(new Vector3(0, 1, 0), Corner(mesh, 2, 2));
    }

    [Fact]
    public void NegativeNumbersCountBackFromTheLatestVertex()
    {
        var mesh = Read("v 0 0 0\nv 1 0 0\nv 0 1 0\nf -3 -2 -1\nv 0 0 5\nf 1 2 -1\n");
        Assert.Equal(2, mesh.TriangleCount);
        Assert.Equal(new Vector3(0, 0, 0), Corner(mesh, 0, 0));
        Assert.Equal(new Vector3(0, 1, 0), Corner(mesh, 0, 2));
        Assert.Equal(new Vector3(0, 0, 5), Corner(mesh, 1, 2));
    }

    [Fact]
    public void CommentsBlankLinesWindowsLineEndsAByteOrderMarkAndContinuedLinesAreHandled()
    {
        string text = "﻿# a comment\r\n\r\nv 0 0 0 # origin\r\nv 1e0 0 0\r\nv 0 \\\r\n 1.5 0\r\ng part\r\nf 1 2 \\\r\n 3\r\n";
        var mesh = Read(text);
        Assert.Equal(1, mesh.TriangleCount);
        Assert.Equal(new Vector3(0, 1.5f, 0), Corner(mesh, 0, 2));
    }

    [Fact]
    public void VertexColoursAreUsed_AndAWeightIsNot()
    {
        var coloured = Read("v 0 0 0 1 0 0\nv 1 0 0 1 0 0\nv 0 1 0 1 0 0\nf 1 2 3\n");
        Assert.True(coloured.HasOwnColors);
        Assert.Equal(ColorSpace.Pack(new Vector3(1, 0, 0)), coloured.Vertices[0].Color);

        var weighted = Read("v 0 0 0 1\nv 1 0 0 1\nv 0 1 0 1\nf 1 2 3\n");
        Assert.False(weighted.HasOwnColors);
    }

    [Fact]
    public void LinesPointsAndFacesWithFewerThanThreeCornersAreNotDrawn()
    {
        var mesh = Read("v 0 0 0\nv 1 0 0\nv 0 1 0\nl 1 2\np 3\nf 1 2\nf 1 2 3\n");
        Assert.Equal(1, mesh.TriangleCount);
    }

    [Fact]
    public void ProblemsSayWhatIsWrongAndWhere()
    {
        Assert.Contains("empty", Assert.Throws<ModelFormatException>(() => Read("")).Message);
        Assert.Contains("no vertices", Assert.Throws<ModelFormatException>(() => Read("# nothing here\n")).Message);
        Assert.Contains("no faces", Assert.Throws<ModelFormatException>(() => Read("v 0 0 0\nv 1 0 0\nl 1 2\n")).Message);
        Assert.Contains("line 3 uses vertex 4", Assert.Throws<ModelFormatException>(() => Read("v 0 0 0\nv 1 0 0\nf 1 2 4\n")).Message);
        Assert.Contains("line 2", Assert.Throws<ModelFormatException>(() => Read("v 0 0 0\nv 1 zero 0\n")).Message);
        Assert.Contains("fewer than three", Assert.Throws<ModelFormatException>(() => Read("v 0 0\n")).Message);
        Assert.Contains("not a vertex number", Assert.Throws<ModelFormatException>(() => Read("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 x\n")).Message);
    }

    [Fact]
    public void TheLoaderReadsObjFiles_WhichAreUsuallyYUp()
    {
        Assert.Contains("obj", ModelLoader.Extensions);
        Assert.Equal(12, ModelLoader.Load(Encoding.UTF8.GetBytes(Cube), ".OBJ").TriangleCount);
        Assert.False(ModelLoader.UsuallyZUp("obj"));
        Assert.True(ModelLoader.UsuallyZUp(".stl"));
        Assert.True(ModelLoader.UsuallyZUp("3mf"));
    }

    [Fact]
    public void ALargeFileIsReadQuickly()
    {
        // A 200 x 200 grid of quads: 40,401 vertices, 80,000 triangles, about 1.6 MB of text.
        var text = new StringBuilder();
        const int n = 200;
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
                text.Append("v ").Append(x).Append(' ').Append(y).Append(" 0.25\n");
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int a = y * (n + 1) + x + 1;
                text.Append("f ").Append(a).Append(' ').Append(a + 1).Append(' ').Append(a + n + 2).Append(' ').Append(a + n + 1).Append('\n');
            }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var mesh = Read(text.ToString());
        Assert.Equal(2 * n * n, mesh.TriangleCount);
        Assert.Equal(new Vector3(n, n, 0.25f), mesh.Bounds.Max);
        Assert.True(clock.ElapsedMilliseconds < 2000, $"took {clock.ElapsedMilliseconds} ms");
    }
}
