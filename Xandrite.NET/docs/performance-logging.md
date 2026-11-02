# Finding out why a scan or a thumbnail run is slow

Xandrite measures where scans and thumbnail runs spend their time and writes it to the log
(**Preferences (the gear) → Show log file**, or `%APPDATA%\Xandrite.NET\logs\xandrite.log`). Each event is one line of
`name=value` fields. These are the lines to look at, and what they tell you.

Times added up over several workers can be larger than the wall-clock time; `parallel` says how many workers were
busy on average. A rate shown as `-` means the time was too short to measure, which is good news.

## While a scan runs: `scan.progress` (every 30 seconds)

```
INFO  scan.progress root=\\server\models elapsed_s=1800 found=40210 hashed=40102 unchanged=0 archives=2210 folders=640
      in_flight=4 read_mb=48210 read_mb_s=31.5 models_s=20.3 db_ms=4210 stalls=2
```

| Field | Meaning |
|---|---|
| `read_mb_s` | How fast files came off the disk or network **in the last 30 s**. On 1 GbE the ceiling is about 110. |
| `models_s` | Models hashed or confirmed unchanged in the last 30 s. |
| `in_flight` | Files or archives being worked on now (at most the scan's concurrency, 4). |
| `stalls` | Single reads that took more than 2 seconds so far (each one is also logged, see below). |

A steady `read_mb_s` near the network's limit means the network is the limit. A low `read_mb_s` with `in_flight=4`
means the workers are busy with something else: see `scan.metrics`.

## When an archive has been read: `archive`

```
INFO  archive path=\\server\models\Kits\big.7z.001 models=120 unchanged=0 nested=0 parts=4 size_mb=1840.2 read_mb=1840.2
      unpacked_mb=4210.7 ms=61230 read_ms=17010 read_mb_s=108.2 models_ms=6120 other_ms=38100 open_ms=12 list_ms=850
```

| Field | Meaning |
|---|---|
| `size_mb` / `read_mb` | The archive's size, and how much of it was actually read. |
| `unpacked_mb` | The size of the models taken out of it. |
| `read_ms`, `read_mb_s` | Time waiting for the disk or network, and the speed while reading. |
| `models_ms` | Time spent hashing and storing the models once they were unpacked. |
| `other_ms` | The rest: mostly **decompressing** (7-Zip). |
| `open_ms` | Opening the archive's files (one per part). |
| `list_ms` | Listing the folder to find a split archive's parts. Large values mean a big folder on a slow share. |

So for each archive you can see whether reading, decompressing or hashing took the time.

## At the end of a scan: `scan.metrics`

| Field | Meaning | If it is large |
|---|---|---|
| `wall_ms` | How long the scan took. | |
| `folders`, `list_ms`, `listed` | Folder listings, the time they took, and the entries they returned. | Many small folders on a slow share. |
| `stats`, `stat_ms` | Extra lookups of a file's size and date (one per loose model and per archive part). Each is a network round trip on a share. | Many files on a high-latency share. |
| `files_read`, `files_mb`, `files_ms`, `files_read_ms` | Loose models read and hashed; total time, and the part spent waiting for the disk or network. | `files_read_ms` close to `files_ms`: I/O-bound. |
| `archives_read`, `archives_unchanged` | Archives opened, and archives skipped because they had not changed. | A rescan should show `archives_read` near 0. |
| `archives_ms`, `archive_read_ms`, `archive_read_mb`, `archive_read_calls` | Time in archives; time waiting for their bytes; how much was read, in how many reads. | `archive_read_ms` much smaller than `archives_ms`: decompressing (CPU) is the limit. |
| `archive_opens`, `archive_open_ms`, `archive_lists`, `archive_list_ms` | Opening archive files, and listing folders for split archives. | `archive_list_ms` large: folders with many split archives on a share. |
| `unpacked_mb`, `models_ms` | Models taken out of archives; time hashing and storing them. | |
| `read_mb_s` | Average read speed while reading. | Well below the network's limit: latency or the server. |
| `db_calls`, `db_ms` | Database work, including waiting for the lock. | Close to `wall_ms`: the database is the limit. |
| `wait_ms` | Time the folder walk waited for a free worker. | Large: the workers are the limit, not finding files. |
| `busy_ms`, `parallel` | Workers' time added up, and divided by `wall_ms`. | `parallel` near 4: all workers busy. Near 1: one slow thing at a time. |
| `stalls` | Reads slower than 2 seconds. | Any at all: the share or the server is struggling. |

### Slow things, as they happen: `scan.slow`

```
WARN  scan.slow what=list path=\\server\models\Kits ms=4120 entries=18210
WARN  scan.slow what=read path=\\server\models\Kits\big.7z.003 ms=2810 bytes=65536
```

`what=list` is a folder that took more than 2 s to list; `what=read` is a single read that took more than 2 s.

## Thumbnails

Each `thumb.ok` line now has `load_ms` (reading the STL or 3MF), `render_ms` (drawing) and `encode_ms` (making the
PNG), as well as `read_ms`. Every minute, `thumbs.progress` gives `per_min` (thumbnails a minute, lately) and
`read_mb_s`. `thumbs.end` totals `read_ms`, `hash_ms`, `load_ms`, `render_ms`, `encode_ms` and `write_ms`, so the
largest of them is where the run's time went. Slow single reads are logged as `thumbs.slow what=read`.
`thumbs.archive` has `extract_ms`: how long the archive took to read, including waiting for its models to be drawn.

## Reading them together

- **Network-bound:** `read_mb_s` near the link's limit in `scan.progress`, `read_ms` the biggest part of each archive.
  A faster link, or running Xandrite on the machine that holds the files, is the only cure.
- **Latency-bound:** `read_mb_s` low, but many `archive_read_calls`, `stats` or `folders`, each costing a round trip.
  Fewer, larger reads help; this is something Xandrite can improve.
- **CPU-bound:** `other_ms` (decompressing) or `models_ms` larger than `read_ms`, and `parallel` near 4. More workers
  would help; this is something Xandrite can change.
- **Database-bound:** `db_ms` close to `wall_ms`.

## Developer logging

For tracking down problems with files, archives or threads, Xandrite has extra logging that is only offered when it
is started with `--developer`:

```
"%LOCALAPPDATA%\Xandrite.NET\Xandrite.exe" --developer
dotnet run --project src/Xandrite -- --developer           (from a build)
```

**Preferences (the gear) → Developer** then has five switches, all off at first. They are remembered, but only put
in force when the app is started with `--developer` again, so they never run unnoticed. Everything goes to the normal
log file; `developer.options` at start-up records which are on.

| Switch | What it logs |
|---|---|
| **Log archives opened and closed** | `archive.open path=… kind=zip\|7z\|rar parts=…` and `archive.close … ms=…`: one pair per archive, a split `.7z.001…` or multi-volume `.part1.rar…` set counting as one (`parts=` says how many files). |
| **Log archive reads, seeks and writes** | Very detailed. Every call on an archive's files: `archive.read path=… at=… asked=… got=… us=…` (where in the file, bytes asked for and received, microseconds) and `archive.seek path=… from=… to=… origin=begin\|current\|end offset=…`, on each part of a split set and each RAR volume. For 7-Zip, each item it goes through: `archive.item … index=… entry=… wanted=true\|false` (an unwanted item in a solid block is still decompressed), `archive.write … index=… bytes=… total=…` for each piece of decompressed content handed over, and `archive.item.end … result=… bytes=… ms=…`. For zip, `archive.entry path=… entry=… bytes=… reads=… ms=…` for each entry read. |
| **Log files opened and closed** | `file.open path=… access=read\|write purpose=… open_ms=…` and `file.close … read=… written=… open_ms=…` (bytes moved while it was open, and how long), `file.open_failed … message=…`. `purpose` says why: `hash`, `zip`, `archive`, `archive part`, `rar volume`, `model`, `thumbnail`, `tile image`, `viewer`, `export`, `download`, `import`. The log file itself is not included. |
| **Log threads started and ended** | `work.start name=… tid=… pool=true` and `work.end name=… ms=… tid=… start_tid=…` for each piece of background work, plus `pool.thread.start` / `pool.thread.stop` / `pool.thread.retire os_tid=… active=…` as .NET's thread pool adds and retires the threads that run it. |
| **Thread id on every log line** | `tid=…` at the end of every line: the managed id of the thread that wrote it. |

Work runs on the .NET thread pool, and asynchronous work can move to another pool thread at each `await`, so a
`work.end` can have a different `tid` from its `work.start` (`start_tid` gives the first). The named work is:
`scan`, `scan.progress`, `scan.hash`, `scan.archive`, `thumbs.run`, `thumbs.worker.N`, `thumbs.archive_read`,
`7z.scan`, `7z.read`, `7z.read_many`, `viewer.load`, `viewer.frame`, `viewer.thumbnail`, `viewer.save_view`,
`tile.decode` and `import.thumbnails`.

These logs grow quickly (one line or two per file and per thumbnail); the log keeps about 60 MB, so turn them off
again when you are done.
