# Reproduction harness

Everything in this folder exists to answer one question with measurements rather than argument:
**how much memory does Umbraco's image processing actually use, and does this package bound it?**

Every number quoted in the root [README](../README.md), in
[umbraco/Umbraco-CMS#23556](https://github.com/umbraco/Umbraco-CMS/issues/23556) and in the
associated PRs came from here, so anyone can re-run and check them.

## What it is

A minimal ASP.NET Core app configured exactly the way Umbraco's `AddUmbracoImageSharp()` configures
ImageSharp — `ClearProviders()`, `WebRootImageProvider`, Umbraco's own `CropWebProcessor`,
`PhysicalFileSystemCache`, the same cache options — with no CMS, no database and no backoffice. That
isolates the imaging pipeline from everything else so the memory being measured is unambiguously the
image processing.

There are two builds, one per ImageSharp major, matching Umbraco's own two imaging packages:

| Image tag | ImageSharp | Mirrors |
|---|---|---|
| `imaging-harness:imagesharp` | 3.1.12 / Web 3.2.0 | `Umbraco.Cms.Imaging.ImageSharp` |
| `imaging-harness:imagesharp2` | 2.1.13 / Web 2.0.2 | `Umbraco.Cms.Imaging.ImageSharp2` |

`HARNESS_FIX=1` applies the package's limits by compiling **the package's own sources**
(`ImagingMemorySettings.cs` and `ImageProcessingThrottleMiddleware.cs`) directly into the harness. So
a run measures the shipped implementation, not a re-creation of it, and the harness stops compiling
if the package's API changes underneath it.

The two `CropWebProcessor.cs` files are verbatim copies of Umbraco's, one per major. They differ in
two places upstream (a null guard, and `Size` being a property in 3.x and a method in 2.x), which is
why they are not shared.

## Prerequisites

Docker, and a Linux container runtime. The behaviour under investigation is specific to Linux
containers with a memory limit — Windows hosting does not reproduce it.

## Running it

```bash
./build.sh              # publishes both harnesses and builds both images
./generate-media.sh     # fills a volume with 40 JPEGs at 4000x3000 (~9 MB each); run once
```

Then reproduce the crash and confirm the fix:

```bash
# 512 MB limit, 200 requests, 16 concurrent — every request a distinct crop, so every one is a miss
./run.sh baseline imaging-harness:imagesharp 512m 200 16
./run.sh fixed    imaging-harness:imagesharp 512m 200 16 -e HARNESS_FIX=1
```

Substitute `imaging-harness:imagesharp2` for the ImageSharp 2.x equivalent.

To measure what a **single** request costs, with no concurrency and no sampling race (it reads
`VmHWM`, the kernel's own peak-RSS counter):

```bash
./single.sh full-decode imaging-harness:imagesharp "width=300&height=300&mode=crop"
```

### Options

| Variable | Meaning |
|---|---|
| `HARNESS_FIX=1` | Apply the package's pool cap and concurrency limit |
| `HARNESS_POOL_MB` | Override `MaximumPoolSizeMegabytes` (0 = derive from available memory) |
| `HARNESS_MAX_CONCURRENCY` | Override `MaximumConcurrentProcessing` (0 = derive) |
| `IDLE` | Seconds to observe after each burst (default 30) |
| `BURSTS` | Number of load bursts, each with a fresh set of cache misses (default 2) |

### Endpoints

| Endpoint | Purpose |
|---|---|
| `/stats` | Managed heap, ImageSharp's outstanding unmanaged handles, process RSS and peak RSS, cgroup usage |
| `/gc` | Forces a full blocking gen2 collection — what triggers ImageSharp's pool trimming |
| `/malloc-trim` | Calls `malloc_trim(0)`; if RSS drops here but not after `/gc`, the C allocator is holding it, not ImageSharp |

Those three exist to separate the three possible holders of memory. Without that split you can only
see that RSS is high, not who is responsible.

### Reading the output

| Column | Meaning |
|---|---|
| `rssMB` | Process resident memory now |
| `peakRssMB` | `VmHWM` — highest RSS since start, so a peak between samples cannot be missed |
| `cgroupMB` | What the container limit is measured against; includes page cache, so it reads higher than RSS |
| `gcHeapMB` / `gcCommitMB` | Managed heap — note how little of the total this accounts for |
| `isHandles` | ImageSharp's outstanding unmanaged buffers (4 MB each) |
| `gen2` | Gen2 collection count; a large number under load indicates GC pressure, not a leak |

`DEAD` in a row means the container stopped answering — check the `container:` line for
`oomkilled=true`.

## Results

512 MB limit, 16 concurrent distinct crops of 4000×3000 JPEGs:

| | ImageSharp 2 | ImageSharp 3 |
|---|---|---|
| default | **OOMKilled, exit 137** | **OOMKilled, exit 137** |
| `HARNESS_FIX=1` | survives, peak 272 MB | survives, peak 272 MB |

Single request, 300×300 thumbnail of a 4000×3000 JPEG:

| | cost |
|---|---|
| ImageSharp 3 | +59 MB peak RSS |
| ImageSharp 2 | +60 MB peak RSS |

That per-request figure is the whole problem in one number: multiply it by the number of thumbnails
a media grid requests at once and it exceeds any modest container limit. It is not a leak — nothing
grows without bound — it is unbounded *peak* concurrency.

By concurrency, without the fix, at a 512 MB limit:

| concurrency | result |
|---|---|
| 1 | stable, 167 MB |
| 8 | survives, peak 453 MB |
| 16 | OOMKilled within 5 s |

## Cleaning up

```bash
docker volume rm imaging-harness-media imaging-harness-cache imaging-harness-cache-single
```

The media volume is around 380 MB; keeping it saves regenerating the images between runs.
