# Xandrite.NET

*Every model has a side worth seeing.*

Xandrite for Windows, in .NET 10 and WPF. This is the port of the Electron app
([amwales-888/xandrite](https://github.com/amwales-888/xandrite)) to a Windows-only native application.
There is no browser engine: thumbnails are drawn with Direct3D 11, called directly through COM.

**Status:** the app is usable. It has the library window (folder tree, virtualised thumbnail grid, search, filters,
sorting, ratings, right-click actions, Manage folders), scanning, the thumbnail queue drawing with Direct3D, and a
one-time import of the Electron app's library, the interactive viewer, and models inside .zip, .7z and .rar archives.
It keeps a log file. Not yet done: the installer.

## What is in the solution

| Project | What it does |
|---|---|
| `src/Xandrite` | The WPF app (`Xandrite.exe`): the library window, the viewer, Manage folders, About, and the image loader. Thin: the logic is in `Xandrite.Presentation`. |
| `src/Xandrite.Presentation` | The windows' logic as plain view models (no WPF types). `LibraryViewModel` covers filters, sorting, the tree, tiles in rows, selection, ratings, scanning and thumbnail status. `ViewerViewModel` covers frames, orbiting, Update, Revert, rating and export. Tested on any OS. |
| `src/Xandrite.Core` | Everything that needs no GPU: STL and 3MF readers, camera framing, lighting and material values, the view settings stored in each PNG, the PNG writer. No Windows dependencies, so it builds and tests anywhere. |
| `src/Xandrite.Rendering.Direct3D11` | The renderer. Hand-written COM interop to `d3d11.dll`, `dxgi.dll` and `d3dcompiler_47.dll` (all part of Windows), plus the HLSL shader. No NuGet packages. |
| `src/Xandrite.ThumbTool` | `xandrite-thumb`, a command-line thumbnail maker for trying real files, timing, scripts and CI. |
| `src/Xandrite.Library` | The library. `LibraryDatabase` (a port of `db.js`, same schema), `FolderWalker` (`scanner.js`), `LibraryScanner` (`indexer.js`), `ThumbnailQueue` (`thumbnail-queue.js`), `Downloads`, `ModelSource`, `ElectronImport`, and the archive readers: zip in .NET, and .7z/.rar through 7-Zip's `7z.dll` (`SevenZip/`). SQLite is called through its C API: Windows' own `winsqlite3.dll`. No NuGet packages. |
| `src/Xandrite.LibraryTool` | `xandrite-lib`, which inspects a library (on a copy) or scans folders into a test library. |
| `tests/Xandrite.Core.Tests` | xUnit tests for the readers, framing, view settings and PNG output. |
| `tests/Xandrite.Library.Tests` | The Electron app's explorer, indexer, zip and thumbnail-queue tests, ported, plus compatibility tests against `Fixtures/electron-library.sqlite`, a library written by the Electron app's own `db.js`. |
| `tests/Xandrite.Presentation.Tests` | The library window's behaviour: selection, filters, settings, scanning and thumbnails reaching the tiles. |
| `samples/` | Small test models: a binary STL, an ASCII STL, and a 3MF with colours and a component stored in a separate model file. |
| `vendor/7zip/` | 7-Zip's `7z.dll` (x64) and its licence. It is copied next to every app and test that reads archives. |

## How a thumbnail is made

`ThumbnailGenerator` does what the JS app's `renderer/thumbnail-worker.js` did:

1. **Read** the file into a flat list of triangles (`ModelLoader` → `StlReader` / `ThreeMfReader`). 3MF build-item and
   component transforms are applied here, including components in other model files (Bambu Studio, Orca Slicer,
   PrusaSlicer), and colours from `<basematerials>` and `<m:colorgroup>` are kept.
2. **Frame** it (`ViewMath`, ported from `view.js`): turn Z up, centre the bounding box, and place the camera on the
   default corner view, or on the view the person saved.
3. **Draw** it (`D3D11Renderer`): an off-screen 512 px sRGB image with 4× MSAA. The shader (`Shaders/Standard.hlsl`)
   is a port of three.js's `MeshStandardMaterial` with the same hemisphere light and two directional lights.
   If there is no usable GPU, it falls back to WARP, Microsoft's software Direct3D, which replaces the old SwiftShader fallback.
4. **Write** the PNG with the view settings as JSON in an iTXt chunk named `ModelThumbnailer`, exactly as before.

`Look.cs` holds every value that affects the picture. Change any of them, or the shader, and bump `Look.RenderVersion`
so existing thumbnails are redrawn.

### Compatibility with the Electron app

- The view-settings JSON is the same, field for field. A test compares it with the output of the original `view.js`.
- Camera framing matches `view.js` (tests pin the numbers it produced under Node).
- `RenderVersion` is 2. STL thumbnails come out almost pixel-identical to three.js's (see
  `samples/three-vs-direct3d.png`). The visible change is that 3MF files now keep their colours.
  three.js's 3MF loader couldn't read component files, and the JS app's fallback reader dropped colours.

## Where the app keeps its data

`%APPDATA%\Xandrite.NET`: `library.sqlite`, a `thumbnails` folder (one `<sha256>.png` per model) and a `logs`
folder. Set `XANDRITE_NET_DATA` to use another folder, for example a test library.

## The log file

`logs\xandrite.log` records what the app does, one event per line, in the same format as the Electron app's log:

```
2026-10-31T02:07:07.132+00:00 INFO  hash path=C:\Models\knot.stl sha256=5d0a8bd0640a… size=1008684 ms=3
2026-10-31T02:07:07.148+00:00 WARN  problem path=C:\Models\kit.7z\big.stl message="the file is damaged (checksum does not match)"
2026-10-31T02:07:07.151+00:00 WARN  scan.end root=C:\Models cancelled=false duration_ms=137 found=8 hashed=8 unchanged=0 new=8 …
```

It covers start-up (version, Windows, SQLite, 7-Zip, the graphics adapter of each renderer), the import from the Electron
app, scans (every file hashed, every archive read, every problem, the totals), thumbnails (each one with its read and
draw times, failures with the reason, skips), the viewer (views saved and reverted, ratings), folders added and
removed, downloads, and any crash. Warnings mark scans and runs that had problems. The file rolls over at 10 MB,
keeping five older files (`xandrite.log.1` … `.5`), so it cannot fill a disk. If it cannot be written, the app carries
on without it. Open it from **Preferences ▾ → Show log file**.

**The first start** copies the Electron app's library across, if there is one (`%APPDATA%\xandrite`). The
database is copied before the window opens; the thumbnail images follow in the background (see the status bar).
The Electron app's folder is only read, never written, so both apps can be kept side by side. Its thumbnails were
drawn by three.js, so the queue redraws them with Direct3D one by one; until it reaches each, the old image is shown.
If the app is closed during the image copy, it carries on at the next start.

## Using the library window

It works like the Electron app's window:
- **Folder tree** on the left, with "All files" at the top. Right-click a folder to rescan it, show it in Explorer, or
  remove it from the library.
- **Search** matches part of a path or of a SHA-256. **Filters** narrow by size, age (date modified or date added) and
  rating. **Order by** has name, folder and name, date modified, date added, size and rating; the ⇅ button reverses it.
- **Selecting:** click selects (click again to deselect); Ctrl-click or the round tick adds or removes one;
  Shift-click selects a range; Ctrl+A selects all; Esc clears.
- **Keys:** the arrow keys move the selection; Enter opens the viewer; 1 to 5 rate the selection and 0 clears the rating;
  Ctrl+F goes to the search box; F5 rescans everything.
- **Right-click** a thumbnail: Open, Show in folder, Download… (copies, or extracts from zips, never overwriting),
  Copy path, Copy SHA-256, Rate, Find other copies of this model, Regenerate thumbnail, Revert to automatic view.
- **Status bar:** library counts, scan progress (with Cancel), thumbnail progress (with Pause, Resume and Retry failed),
  and Rescan All.
- **Preferences ▾:** Manage folders… (add several, then they are scanned when the window closes), Search inside
  archives, Thumbnail speed, Open data folder, Show log file, the update options and About Xandrite….
- **Thumbnail speed:** *Full speed* (every worker, normal priority; 7-Zip decompresses on every core), *Balanced* (half
  the workers, below-normal priority, 7-Zip on a quarter of the cores) or *In the background* (one model at a time, lowest
  priority, 7-Zip on one thread). It can be changed while thumbnails are being drawn and takes effect from the next
  model. It is remembered, and changes are logged (`thumbs.speed`). Scanning and the viewer always run at full speed.
- **Models inside archives** are read one archive at a time: every model the queue needs from an archive comes out
  of a single pass, and models are drawn while the rest are still being read (idle workers help). A solid `.7z` used
  to be decompressed from its start once per model; 80 models in a 15 MB solid archive now take about 1 s to read
  instead of 36 s. Each archive is logged as `thumbs.archive`.
- Drop folders on the window to add them.

The grid is a virtualised list of rows, so it stays fast with very large libraries. Thumbnails are decoded on
background threads at the size they are shown, with a small cache.

## The viewer

Double-click a thumbnail (or select it and press Enter, or choose Open) to open the model in the viewer. It is a port
of the Electron app's viewer:
- **Left-drag** turns the model, **right-drag** (or Shift + left-drag) pans, the **wheel** zooms, and **R** resets the
  view. The camera behaves like three.js's OrbitControls: it never rolls or flips over the top.
- **File** shows the name, format, size, triangles, dimensions, SHA-256 and the **rating**, which can be changed
  here for models opened from the library.
- **Update thumbnail** replaces the model's library thumbnail with what you see (512 px) and stores the view, so a
  later redraw uses it. The grid shows the new image straight away. **Revert to automatic view** forgets it.
- **Z up** turns the file's Z axis upwards (on by default, as STL and 3MF files are normally Z-up).
- **Advanced** → **Export as…** saves the view as a PNG at 256 to 2048 px, named after the model by default, and
  shows the view settings stored inside it.
- The viewer is opened from the library. A file dropped onto it can be looked at too, even from outside the library.

Frames are drawn with the same Direct3D 11 renderer as the thumbnails, off screen on a background thread, then
shown in the window. While one frame is drawn, further mouse movement is gathered into a single next frame, so the
view never lags behind the mouse. (If very large models ever feel slow, a direct swap chain can replace this later.)

## The library

`LibraryDatabase` uses the Electron app's schema unchanged, so the .NET app can open an existing
`%APPDATA%\xandrite\library.sqlite` and its `thumbnails` folder as they are. It works the other way too: the
Electron app can read a library written here. Both directions are covered by tests.

- **SQLite** is called directly through its C API (`Sqlite/SqliteNative.cs`). On Windows that is `winsqlite3.dll`,
  part of Windows 10 and 11, so nothing is shipped. If you ever want a newer SQLite, build `sqlite3.dll` from the
  amalgamation and put it next to the exe; it is picked up automatically. The minimum version is 3.24 (from 2018).
- **Scanning** follows `indexer.js`. Unchanged files (same path, size and modified time) are not read again. Moved
  files keep their hash, thumbnail, rating and view. Deleted files are marked missing. Nothing is marked missing
  after a cancelled scan or when a folder can't be read. Modified times are computed exactly as Node.js does, so
  files the Electron app indexed count as unchanged and aren't re-hashed on the first .NET scan.
- **Zip files** are read with `System.IO.Compression`. Entries are matched on size and CRC-32. The Electron app also
  compared entry dates, which .NET reads slightly differently, so dates no longer count. Existing zips are not re-read.
- **.7z and .rar** (RAR 4 and 5) are read with 7-Zip's `7z.dll`, loaded into the app. The Electron app ran
  `7z.exe` as a separate program instead. Solid archives are read in one pass. Split archives (`name.7z.001`, `.002`…)
  and multi-volume RARs (`name.part1.rar`… or `.r00`) are read as one. 7-Zip checks every file's CRC, so a damaged
  model is reported while the others in the same archive are still read. Password-protected archives are reported
  and skipped. Entry names, sizes and CRCs are recorded exactly as the Electron app recorded them, so its archives
  are recognised as unchanged. If `7z.dll` is missing, these archives are reported and their models kept, never
  marked missing.
- Two small improvements over the JS app: scan IDs always increase, even for two scans in the same millisecond, and
  cloud placeholder files (OneDrive) are scanned, while links to folders are still not followed.

Try it on your real library:

```
dotnet run --project src/Xandrite.LibraryTool -- info
dotnet run --project src/Xandrite.LibraryTool -- scan "C:\path\to\models" --db test-library.sqlite
```

`info` reads a temporary copy of `%APPDATA%\xandrite\library.sqlite`, so the Electron app's library is never changed.
`scan` refuses to write into that file.

## What you need to build it

**Recommended: Visual Studio 2026 Community** (free for individuals and open-source work).

In the Visual Studio Installer, select:

- **.NET desktop development** (required). It brings the .NET 10 SDK, the WPF designer, the test runner and the debugger.
- **Desktop development with C++** (optional, for later). Add it when you start bringing in C libraries
  (lib3mf, 7-Zip's `7z.dll` source, anything built from C or C++), or if you want C++/CLI wrappers.

Also worth having:

- **Graphics Tools**, a Windows optional feature (Settings → System → Optional features → Add a feature →
  "Graphics Tools"). It installs the Direct3D debug layer. Then `xandrite-thumb --debug` reports any Direct3D misuse in
  Visual Studio's Output window.
- **RenderDoc** (free), for frame captures if a thumbnail ever looks wrong.
- **Git for Windows**, if you don't use Visual Studio's built-in Git.

Without Visual Studio, the **.NET 10 SDK** alone is enough to build and test from a terminal. VS Code with the
C# Dev Kit extension, or JetBrains Rider, also work.

## Build and run

In Visual Studio: open `Xandrite.slnx`, right-click **Xandrite** → *Set as Startup Project*, press F5.
Use *Test → Run All Tests* for the tests.

From a terminal (in this folder):

```
dotnet build Xandrite.slnx
dotnet test Xandrite.slnx
dotnet run --project src/Xandrite
dotnet run --project src/Xandrite.ThumbTool -- samples -o thumbnails
```

`xandrite-thumb` options: `--size 1024`, `--software` (WARP), `--by-hash` (name files `<sha256>.png` like the
library), `--view some-thumbnail.png` (reuse a stored view), `--no-z-up`, `--debug`.

The GitHub Actions workflow (`.github/workflows/xandrite-net.yml` at the root of the repository) builds, tests, and
draws the samples with WARP on `windows-latest`, then uploads the PNGs as an artifact. It runs only when something
under `Xandrite.NET` changes.

## Installing and updating

Releases are made with [Velopack](https://velopack.io) and published as GitHub releases of this repository.

- **To make a release**, set the version and push a tag that starts with `net-v`:
  ```
  git tag net-v0.4.0
  git push origin net-v0.4.0
  ```
  The `Xandrite.NET release` workflow (`.github/workflows/xandrite-net-release.yml`) builds and tests the app, publishes
  it as a self-contained app, and packs it with `vpk`. It makes a delta from the previous release, then uploads
  `Xandrite.NET-win-Setup.exe`, the update packages and `releases.win.json` to a release called *Xandrite.NET 0.4.0*.
  The version comes from the tag. The Electron app's tags (`v*`) are separate and do not trigger this workflow.
- **Setup.exe** installs for the current user, with no admin prompt, into `%LOCALAPPDATA%\Xandrite.NET`. It adds Start menu and desktop
  shortcuts called *Xandrite* and an entry in *Settings → Apps* for uninstalling. When it finishes, Xandrite asks
  whether to start now (Setup always launches the app, so the app asks on its first run). Uninstalling keeps the library in
  `%APPDATA%\Xandrite.NET`.
- **Updates.** About 10 seconds after start-up, an installed copy checks the latest releases. If it finds a newer
  version, it downloads it in the background (a delta when it can) and the status bar offers **Restart to update**.
  If you don't restart, the update is installed the next time Xandrite starts. *Preferences* has *Check for updates
  automatically*, *Check for updates now* and *About Xandrite…*. Checks, downloads and problems are written to the log
  (`update.*`).
- A copy run from Visual Studio or `dotnet run` is not installed, so it never checks or updates.
- Every CI run also builds an installer (version `0.0.1-ci`) and keeps it as the `installer` artifact, to try without making a release.
- The installer isn't code-signed yet, so Windows SmartScreen warns the first time it is run (*More info → Run anyway*).

## What has been verified, and what has not yet

This was put together on Linux, where NuGet and Windows weren't available:

- `Xandrite.Core`, the renderer library and `xandrite-thumb` compile. The 39 tests pass (run through a stand-in for xUnit).
  On Windows: the tests, `xandrite-thumb`, the WPF preview and GPU rendering all confirmed working.
- `Xandrite.Library` and `xandrite-lib`: on Windows, the tests, `info` and `scan` were confirmed working.
- The library window, thumbnail queue, import and viewer were confirmed working on Windows.
- `.7z`/`.rar`: the reader was tested here against 7-Zip's own Linux build of the same library (same interfaces), with
  real archives made by 7-Zip 23.01 and RAR 7.00 (`tests/Xandrite.Library.Tests/Fixtures/archives`), and then on
  Windows against `7z.dll` by the CI workflow.
- The log file: all 144 tests pass here (46 core, 75 library, 23 view-model). The WPF changes (the Show log file menu,
  start-up and crash logging in `App`) are small and not yet compiled.
- The shader and the exact Direct3D pipeline settings were run under Wine's Direct3D 11 to produce the comparison image.
  The COM vtable slot numbers were taken mechanically from the Windows SDK headers.
- **Not yet run on real Windows:** the C# COM calls themselves, the WPF app (it needs the Windows Desktop SDK to
  compile), and the xUnit packages restoring. The first `dotnet build` and `xandrite-thumb samples --software` on your
  machine are the real test. Please report anything that fails.

## Next steps

1. Windows integration: thumbnails in Explorer itself (a shell thumbnail provider), the NTFS change journal for
   instant rescans.
