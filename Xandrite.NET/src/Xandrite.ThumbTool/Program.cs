using System.Diagnostics;
using System.Security.Cryptography;
using Xandrite;
using Xandrite.Loading;
using Xandrite.Rendering;
using Xandrite.Rendering.Direct3D11;
using Xandrite.Thumbnails;

// xandrite-thumb: makes Xandrite thumbnails from the command line.
//
//   xandrite-thumb <file or folder>... [-o <folder>] [--size 512] [--by-hash] [--software] [--debug] [--view view.png]
//
// Folders are searched (with subfolders) for .stl, .3mf and .obj files.

const string Usage = """
    Usage: xandrite-thumb <file or folder>... [options]

      -o, --out <folder>   Where to write the PNG files (default: ./thumbnails)
      --size <pixels>      Image size, 16 to 4096 (default: 512)
      --by-hash            Name each PNG <sha256>.png, as the library does
      --software           Use WARP (software rendering) instead of the GPU
      --debug              Use the Direct3D debug layer if Windows' Graphics Tools are installed
      --view <file.png>    Reuse the view stored inside an existing Xandrite thumbnail
      --no-z-up            Do not turn the file's Z axis upwards (automatic view only)
    """;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("xandrite-thumb draws with Direct3D 11, so it only runs on Windows.");
    return 2;
}

var inputs = new List<string>();
string output = "thumbnails";
int size = Look.ThumbnailSize;
bool byHash = false, software = false, debug = false, zUp = true;
Xandrite.Framing.StoredView? view = null;

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(a + " needs a value.");
    try
    {
        switch (a)
        {
            case "-o" or "--out": output = Next(); break;
            case "--size": size = int.Parse(Next()); break;
            case "--by-hash": byHash = true; break;
            case "--software": software = true; break;
            case "--debug": debug = true; break;
            case "--no-z-up": zUp = false; break;
            case "--view":
                string png = Next();
                string? json = Xandrite.Imaging.PngText.Read(File.ReadAllBytes(png), Look.StateKeyword);
                view = json is null ? null : ViewState.ParseStoredView(json);
                if (view is null) throw new ArgumentException(png + " holds no Xandrite view settings.");
                break;
            case "-h" or "--help" or "/?": Console.WriteLine(Usage); return 0;
            default:
                if (a.StartsWith('-')) throw new ArgumentException("Unknown option " + a);
                inputs.Add(a);
                break;
        }
    }
    catch (Exception e) when (e is ArgumentException or FormatException or IOException)
    {
        Console.Error.WriteLine(e.Message);
        Console.Error.WriteLine(Usage);
        return 2;
    }
}
if (inputs.Count == 0)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var files = new List<string>();
foreach (var input in inputs)
{
    if (Directory.Exists(input))
        files.AddRange(Directory.EnumerateFiles(input, "*.*", SearchOption.AllDirectories)
            .Where(f => ModelLoader.Extensions.Contains(Path.GetExtension(f).TrimStart('.'))));
    else if (File.Exists(input)) files.Add(input);
    else Console.Error.WriteLine("Not found: " + input);
}
Directory.CreateDirectory(output);

D3D11Renderer renderer;
try
{
    renderer = D3D11Renderer.Create(new D3D11RendererOptions(ForceSoftware: software, DebugLayer: debug));
}
catch (RendererException e)
{
    Console.Error.WriteLine(e.Message);
    return 3;
}

using (renderer)
{
    Console.WriteLine($"Renderer: {renderer.AdapterDescription}{(renderer.IsSoftware ? " (software)" : "")}, {renderer.SampleCount}x MSAA");
    var generator = new ThumbnailGenerator(renderer);
    int made = 0, failed = 0;
    var total = Stopwatch.StartNew();

    foreach (var file in files)
    {
        try
        {
            byte[] data = File.ReadAllBytes(file);
            string sha = Convert.ToHexStringLower(SHA256.HashData(data));
            var job = new ThumbnailJob(data, Path.GetExtension(file).TrimStart('.'), Path.GetFileName(file), sha, view, size);
            // The automatic view is always Z-up in the library; --no-z-up is for trying files that are Y-up.
            var result = zUp || view is not null
                ? generator.Generate(job)
                : DrawYUp(generator, job);
            string name = byHash ? sha + ".png" : Path.GetFileNameWithoutExtension(file) + ".png";
            File.WriteAllBytes(Path.Combine(output, name), result.Png);
            made++;
            Console.WriteLine($"{Path.GetFileName(file),-40} {result.Triangles,10:N0} tris  load {Ms(result.LoadTime),6}  render {Ms(result.RenderTime),6}  png {Ms(result.EncodeTime),6}");
        }
        catch (ModelFormatException e)
        {
            failed++;
            Console.WriteLine($"{Path.GetFileName(file),-40} FAILED: {e.Message}");
        }
        catch (RendererException e)
        {
            Console.Error.WriteLine($"The renderer stopped working{(e.DeviceLost ? " (the graphics device was lost)" : "")}: {e.Message}");
            return 3;
        }
    }

    double seconds = total.Elapsed.TotalSeconds;
    Console.WriteLine($"{made} made, {failed} failed in {seconds:0.0} s ({(made / Math.Max(seconds, 0.001)):0.0} per second). Written to {Path.GetFullPath(output)}");
    return failed == 0 ? 0 : 1;
}

static string Ms(TimeSpan t) => $"{t.TotalMilliseconds:0} ms";

static ThumbnailResult DrawYUp(ThumbnailGenerator generator, ThumbnailJob job)
{
    var clock = Stopwatch.StartNew();
    var mesh = ModelLoader.Load(job.Data, job.Format);
    var load = clock.Elapsed;
    var (png, json, render, encode) = generator.Draw(mesh, null, job.SizePx,
        new ThumbnailFileInfo(job.Name, job.Format, job.Data.Length, job.Sha256), defaultZUp: false);
    return new ThumbnailResult(png, mesh.TriangleCount, json, load, render, encode);
}
