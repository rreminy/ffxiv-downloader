using DotMake.CommandLine;
using FFXIVDownloader.Lut;
using FFXIVDownloader.Thaliak;
using FFXIVDownloader.ZiPatch;
using FFXIVDownloader.ZiPatch.Util;

namespace FFXIVDownloader.Command;

[CliCommand(Description = "Create a LUT file from a patch url. Provide a slug or a list of urls.", Parent = typeof(MainCommand))]
public class LutCommand
{
    public required MainCommand Parent { get; set; }

    [CliOption(Required = true, Description = "The slug of the repository.")]
    public required string Slug { get; set; }

    [CliOption(Required = false, Description = "The version to download from the slug. If blank, the latest version will be used.")]
    public string? Version { get; set; }

    [CliOption(Required = false, Arity = CliArgumentArity.OneOrMore, Description = "The url (or file paths) of the patch files.")]
    public string[]? Urls { get; set; }

    [CliOption(Required = false, Description = "Degree of parallelism to use when downloading patches.")]
    public int Parallelism { get; set; } = Environment.ProcessorCount;

    [CliOption(Required = false, Description = "The output directory to write the LUTs to. If omitted, the current directory will be used.")]
    public string OutputPath { get; set; } = Directory.GetCurrentDirectory();

    [CliOption(Required = false, Description = "The compression method to use for the LUT files.")]
    public CompressType Compression { get; set; } = CompressType.Brotli;

    [CliOption(Required = false, Description = "Force overwrite existing LUT files.")]
    public bool Force { get; set; }

    public async Task RunAsync()
    {
        var token = Parent.Init();

        OutputPath = Directory.CreateDirectory(OutputPath).FullName;
        Log.Info($"Output Path: {OutputPath}");

        var chain = await GetChainAsync(Urls, Slug, Version, token).ConfigureAwait(false);

        if (!Force)
        {
            for (var i = chain.Count - 1; i >= 0; --i)
            {
                var (ver, patch) = chain[i];
                if (File.Exists(Path.Join(OutputPath, $"{ver:P}.lut")))
                {
                    Log.Info($"Skipping patch {ver}");
                    chain.RemoveAt(i);
                }
            }
        }

        Log.Info($"Total Size: {(chain.Any(p => p.Patch.Size == 0) ? ">" : string.Empty)}{chain.Sum(p => p.Patch.Size) / (double)(1 << 30):0.00} GiB");

        using var patchClient = new PatchClient(10);

        await Parallel.ForEachAsync(chain, new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Parallelism
        }, async (item, token) =>
        {
            var (gameVer, patch) = item;
            var patchVer = patch.Version;

            Log.Info($"Downloading patch {patchVer}");
            Log.Verbose($"  URL: {patch.Url}");
            if (patch.Size != 0)
                Log.Verbose($"  Size: {patch.Size / (double)(1 << 20):0.00} MiB");

            using var httpStream = await patchClient.GetPatchAsync(patch.Url, patchVer, token).ConfigureAwait(false);
            using var bufferedStream = new BufferedStream(httpStream, 1 << 20);
            using var patchStream = new PositionedStream(bufferedStream);

            var lutFile = new LutFile
            {
                Header = new LutHeader
                {
                    Compression = Compression,
                    Version = patchVer,
                    Repository = Slug
                }
            };

            using (var file = new ZiPatchFile(patchStream))
            {
                await foreach (var chunk in file.GetChunksAsync(token).WithCancellation(token).ConfigureAwait(false))
                {
                    Log.Debug($"Chunk {chunk}");
                    lutFile.Chunks.Add(new(chunk));
                }
            }

            var fileName = $"{patchVer}.lut";
            var outPath = Path.Join(OutputPath, fileName);
            Log.Debug($"Writing to {fileName}");

            long fileSize;
            using (var lutStream = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                using var writer = new BinaryWriter(lutStream);
                lutFile.Write(writer);
                fileSize = lutStream.Length;
            }

            // using (var lutStream2 = File.OpenRead(Path.Join(OutputPath, fileName)))
            // {
            //     using var reader = new BinaryReader(lutStream2);
            //     var lutFile2 = new LutFile(reader);
            //     Log.DebugObject(lutFile2.Chunks);
            // }

            Log.Verbose($"Finished {patchVer} ({fileSize / (double)(1 << 10):0.00} KiB)");
        }).ConfigureAwait(false);
    }

    public static async Task<List<(GameVersion Version, Patch Patch)>> GetChainAsync(string[]? urls, string? slug, string? gameVersion, CancellationToken token)
    {
        if (urls != null && urls.Length > 0)
        {
            return [.. urls.Select(url =>
            {
                var version = new GameVersion(Path.GetFileNameWithoutExtension(url));
                Log.Warn($"Patch version {version} may not line up with the game version.");
                var patch = new Patch
                {
                    Url = url,
                    Size = File.Exists(url) ? new FileInfo(url).Length : 0
                };
                return (version, patch);
            })];
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(slug);

        using var thaliak = new ThaliakClient();

        var meta = await thaliak.GetRepositoryMetadataAsync(slug, token).ConfigureAwait(false);
        Log.Verbose($"Repository:");
        Log.Verbose($"  Slug: {slug}");
        Log.Verbose($"  Name: {meta.Name}");
        Log.Verbose($"  Description: {meta.Description}");
        Log.Verbose($"  Latest Version: {meta.LatestVersion?.VersionString}");

        var parsedVersion = gameVersion != null ? new GameVersion(gameVersion) : meta.LatestVersion!.VersionString;
        Log.Info($"Using version {parsedVersion}");

        Log.Verbose($"Downloading patch chain");
        var chain = await thaliak.GetPatchChainAsync(slug, parsedVersion, token).ConfigureAwait(false);

        return chain;
    }
}
