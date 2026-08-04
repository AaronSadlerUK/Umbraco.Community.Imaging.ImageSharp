# Umbraco.Community.Imaging.ImageSharp

Stops Umbraco sites being OOM-killed in memory-limited containers while browsing the media section.

[![NuGet](https://img.shields.io/nuget/v/Umbraco.Community.Imaging.ImageSharp.svg)](https://www.nuget.org/packages/Umbraco.Community.Imaging.ImageSharp)

> This is a backport of the fix proposed for the CMS in
> [umbraco/Umbraco-CMS#23557](https://github.com/umbraco/Umbraco-CMS/pull/23557)
> (issue [#23556](https://github.com/umbraco/Umbraco-CMS/issues/23556)), for Umbraco 17 sites that
> need it before it ships in the product. **Remove this package once you are on an Umbraco version
> that includes the fix** — see [Migrating off this package](#migrating-off-this-package).

## The problem

If you run Umbraco in a Linux container with a memory limit, browsing the media section grows
memory until the container is killed — `docker inspect` shows `OOMKilled=true` and exit code
**137**. Windows hosting does not behave the same way, and setting `DOTNET_GCHeapHardLimit` and
friends makes no difference, which makes it look like a leak.

It is not a leak. `ImageSharpMiddleware` decodes the source image at **full resolution** before any
processor runs, and it only de-duplicates concurrent requests for the **same** URL. A media grid
renders a screenful of *distinct* thumbnails, so every source decodes in parallel with no ceiling.
Peak memory is:

```
concurrent requests × decoded source size
```

which is unbounded. Measured with `VmHWM` (peak RSS), one request, no concurrency: **a single
300×300 thumbnail of a 4000×3000 JPEG costs +60 MB**. Sixteen concurrent thumbnail requests is
~960 MB, so a 512 MB container dies in seconds.

Two further things stop the memory coming back promptly, both **unmanaged** and therefore outside
anything `DOTNET_GC*` controls:

1. ImageSharp's pool is sized at `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 8` and trims only
   on a gen2 collection, at most 50% per 60 s unless memory load exceeds 90%. Measured: 176 MB still
   held at idle in a 2 GB container, surviving a *forced* gen2 collection.
2. The pool blocks come from `Marshal.AllocHGlobal` (= `malloc`). `malloc_trim(0)` released a
   further 20–45 MB that GC alone did not — glibc retention, which is why Windows differs.

You may also see `docker stats` reporting far more memory than the process actually uses. Cgroup
accounting includes page cache from MediaCache writes — measured 1016 MB cgroup against 441 MB
process RSS. The kernel reclaims that under pressure, so it is not what kills the container, but it
does make monitoring overstate the problem.

## What this package does

It adds two limits, both derived from the memory available to the process (which honours the
container limit):

| Setting | Purpose | Derived default |
|---------|---------|-----------------|
| `MaximumPoolSizeMegabytes` | Caps the unmanaged buffer pool ImageSharp retains between requests | available / 32, clamped to 16–64 MB |
| `MaximumConcurrentProcessing` | Caps how many images are processed at once | (available / 2) / 64 MB, capped at processor count |

The concurrency cap is enforced by a middleware registered **ahead of** `UseImageSharp()` in
Umbraco's pre-pipeline. Requests over the limit **wait**; they are not rejected. Only requests whose
path has a file extension *and* whose query carries a registered ImageSharp processor command are
gated, so an API call that happens to carry a `width` is unaffected.

On a host with no container limit the derived concurrency lands at the processor count, so
non-containerised sites are effectively unthrottled.

## Results

Same load that produced exit 137 — 512 MB limit, 16 concurrent distinct crop/resize requests
against 4000×3000 JPEG sources, every request a cache miss:

| | without the package | with the package |
|---|---|---|
| outcome | **OOMKilled, exit 137** | survives |
| peak RSS | >512 MB | **285 MB** |
| idle RSS after two bursts | — | 189 MB |
| derived settings | — | `poolMB=16 maxConcurrent=3` (available 384 MB, 28 CPUs) |

Behaviour by concurrency without the package, 512 MB limit:

| concurrency | result |
|---|---|
| 1 | stable, 167 MB RSS |
| 8 | survives, peak 453 MB |
| 16 | OOMKilled, exit 137, within 5 s |

Steady state at a 2 GB limit, concurrency 8, idle after two bursts of load:

| | idle RSS | pooled unmanaged |
|---|---|---|
| without | 409 MB | 176 MB (44 handles × 4 MB) |
| with | 251 MB | 16–32 MB |

Cache hits are unaffected: 200 warm-cache requests complete in 3.5 s through a semaphore of 3,
because a cache hit takes milliseconds and never holds the gate.

## Installation

```bash
dotnet add package Umbraco.Community.Imaging.ImageSharp
```

That is all — the package composes itself. There is nothing to add to `Program.cs`.

**Requires Umbraco 17.** Umbraco 18 and later are not supported by this package; the fix is
expected to be in the product by then.

## Configuration

Everything is optional. Defaults are derived at startup and suit most sites.

```json
{
  "Umbraco": {
    "CMS": {
      "Imaging": {
        "Memory": {
          "Enabled": true,
          "MaximumPoolSizeMegabytes": 0,
          "MaximumConcurrentProcessing": 0
        }
      }
    }
  }
}
```

| Setting | Default | Meaning |
|---------|---------|---------|
| `Enabled` | `true` | Set to `false` to leave ImageSharp entirely at its own defaults |
| `MaximumPoolSizeMegabytes` | `0` | `0` derives from available memory; any positive value is used as-is |
| `MaximumConcurrentProcessing` | `0` | `0` derives from available memory and processor count; any positive value is used as-is |

These are deliberately the same configuration keys proposed for the CMS, so nothing has to change
when you migrate off the package.

### Tuning

- **Still being OOM-killed?** Your sources are probably larger than the 64 MB per image the
  derivation assumes. Set `MaximumConcurrentProcessing` explicitly to a lower number, or raise the
  container's memory limit.
- **Thumbnails feel slow on first load?** The gate is only reached on cache misses. Raise
  `MaximumConcurrentProcessing`, and check peak memory afterwards with `docker stats`.
- **Memory still high at rest?** Lower `MaximumPoolSizeMegabytes` (16 is the derived floor). Below
  that, allocations bypass the pool entirely — correct, just a little slower.

## Migrating off this package

Once you are on an Umbraco version that applies these limits itself:

1. Remove the package reference.
2. Leave the `Umbraco:CMS:Imaging:Memory` configuration exactly where it is — the keys are the same.

Leaving the package installed on such a version is harmless but redundant: you would get two
throttles nested inside each other, the inner of which never blocks.

## How this was verified

The measurements above come from a reproduction harness: a minimal ASP.NET Core app configured
identically to `AddUmbracoImageSharp()` (`ClearProviders()`, `WebRootImageProvider`, Umbraco's
`CropWebProcessor`, `ConfigureImageSharpMiddlewareOptions`, `PhysicalFileSystemCache`), running on
`mcr.microsoft.com/dotnet/aspnet:10.0-noble` under cgroup v2 with ImageSharp 3.1.12 and
ImageSharp.Web 3.2.0. Peak memory is read from `VmHWM` rather than sampled, so there is no sampling
race, and the split between managed heap, ImageSharp's unmanaged pool and C-allocator retention is
measured separately.

The package itself was then installed into a clean Umbraco 17.4.2 site and the throttle confirmed to
sit in front of the imaging middleware, by timing four concurrent distinct crops against a single
one:

| `MaximumConcurrentProcessing` | four concurrent crops vs. one | |
|---|---|---|
| `1` | ×3.57, ×3.84 | serialised, as configured |
| `8` | ×1.40, ×1.11 | parallel, as configured |

Had the middleware been registered after `UseImageSharp()` rather than before it, both rows would
read the same.

To reproduce the original problem against a real site:

1. Run Umbraco in Docker with `--memory=512m` on an Ubuntu-based `aspnet` image.
2. Upload a few dozen 4000×3000 JPEGs to the media section.
3. Browse the media section page by page, so each page requests a screenful of distinct thumbnails
   that are not yet in the media cache.
4. Watch `docker stats`.

## Licence

MIT — see [LICENSE](LICENSE).
