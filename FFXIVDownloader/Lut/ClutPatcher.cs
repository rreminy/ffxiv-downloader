using DotNext.IO;
using FFXIVDownloader.Thaliak;
using FFXIVDownloader.ZiPatch.Config;
using FFXIVDownloader.ZiPatch.Util;
using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FFXIVDownloader.Lut;

public sealed class ClutPatcher : IDisposable
{
    // Akamai restricts the range header size to at most 1034 bytes from my testing,
    // but it doesn't work sometimes, so use a smaller number
    private const int MAX_RANGE_HEADER_SIZE = 1 << 12;
    private const int MIN_RANGE_DISTANCE = 1 << 9;

    public ClutDiff Diff { get; }
    public int Concurrency { get; }
    public Uri BasePatchUrl { get; }

    private PatchClient Client { get; }
    private string[] FileNames { get; }
    private Channel<PatchIntervalData> ApplyQueue { get; }

    // Reference to a ClutDataRef
    private readonly record struct DataReference(int FileNameIdx, int DataRefIdx);

    // Reference to a ClutPatchRef
    private readonly record struct PatchSection(DataReference DataRef);

    // Reference to a ClutDataRef with its corresponding data (if it contains a ClutPatchRef)
    private readonly record struct DataRefOperation(DataReference DataRef, PatchIntervalData? PatchData);

    // Represents a unioned range of ClutPatchRefs with a max distance between them being MIN_RANGE_DISTANCE
    // Used to merge http range requests because Akamai assumes too little distance to be possibly malicious
    private sealed class MergedRange(PatchSection part, ClutPatchRef patchRef)
    {
        public long Offset { get; } = patchRef.Offset;
        public int Size { get; private set; } = patchRef.Size;
        public List<PatchSection> Parts { get; } = [part];

        public bool Add(PatchSection part, ClutPatchRef patchRef)
        {
            if (patchRef.Offset + patchRef.Size + MIN_RANGE_DISTANCE < Offset)
                return false;

            Size = Math.Max(Size, checked((int)(patchRef.Offset + patchRef.Size - Offset)));
            Parts.Add(part);
            return true;
        }
    }

    // Represents a ClutPatchRef with its corresponding data
    // Automatically decompresses data if needed
    private sealed class PatchIntervalData(PatchSection interval, ReadOnlyMemory<byte> fileData)
    {
        public PatchSection Interval { get; } = interval;
        public ReadOnlyMemory<byte> Data { get; } = fileData;

        public static async Task<PatchIntervalData> CreateAsync(PatchSection interval, ClutPatchRef patchRef, ReadOnlyMemory<byte> patchData, ClutDataRef dataRef, CancellationToken token = default)
        {
            if (patchRef.Size != patchData.Length)
                throw new ArgumentException("Invalid patch data size", nameof(patchData));
            if (patchRef.IsCompressed)
            {
                try
                {
                    return new(interval, await DecompressAsync(patchData, token).ConfigureAwait(false));
                }
                catch (Exception)
                {
                    Log.Warn($"CreateAsync DEBUG: First 128/{patchData.Length} bytes of compressed data: {Convert.ToHexString(patchData.Span[..128])}");
                    Log.Warn($"CreateAsync DEBUG: PatchRef: Offset={patchRef.Offset}, Size={patchRef.Size}, IsCompressed={patchRef.IsCompressed}");
                    Log.Warn($"CreateAsync DEBUG: DataRef: Type={dataRef.Type}, Version={dataRef.AppliedVersion}, Offset={dataRef.Offset}, Length={dataRef.Length}");
                    throw;
                }
            }
            return new(interval, patchData);
        }

        private static async ValueTask<ReadOnlyMemory<byte>> DecompressAsync(ReadOnlyMemory<byte> patchData, CancellationToken token = default)
        {
            using var ms = new MemoryStream(patchData.Length * 2);
            using var s = patchData.AsStream();
            using var d = new DeflateStream(s, CompressionMode.Decompress);
            await d.CopyToAsync(ms, token).ConfigureAwait(false);
            return ms.GetBuffer().AsMemory()[..(int)ms.Length];
        }
    }

    public ClutPatcher(ClutDiff diff, int concurrency, string? basePatchUrl = null)
    {
        Diff = diff;
        Concurrency = concurrency;
        BasePatchUrl = new(
            Diff.BasePatchUrl ??
            basePatchUrl ??
            throw new ArgumentNullException(nameof(basePatchUrl), "No base url exists"),
            UriKind.Absolute);

        Client = new(10);
        FileNames = [.. Diff.AddedFiles.Keys];
        ApplyQueue = Channel.CreateBounded<PatchIntervalData>(new BoundedChannelOptions(Concurrency * 2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
    }

    public async Task ApplyAsync(ZiPatchConfig config, CancellationToken token = default)
    {
        foreach (var removedFile in Diff.RemovedFiles)
            await config.DeleteFile(removedFile).ConfigureAwait(false);

        foreach (var removedFolder in Diff.RemovedFolders)
            await config.DeleteDirectory(removedFolder).ConfigureAwait(false);

        foreach (var addedFolder in Diff.AddedFolders)
            await config.CreateDirectory(addedFolder).ConfigureAwait(false);

        await Parallel.ForEachAsync(
            GetDataRefsAsync(token),
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = Concurrency },
            (operation, token) => ApplyDataRefAsync(config, GetRefName(operation.DataRef), GetRef(operation.DataRef), operation.PatchData?.Data, token)
        ).ConfigureAwait(false);
    }

    private static async ValueTask ApplyDataRefAsync(ZiPatchConfig config, string path, ClutDataRef dataRef, ReadOnlyMemory<byte>? patchData = null, CancellationToken token = default)
    {
        var file = await config.OpenFile(path).ConfigureAwait(false);
        if (dataRef.Type == ClutDataRef.RefType.Patch)
        {
            if (!patchData.HasValue)
                throw new ArgumentNullException(nameof(patchData));

            var patchBuffer = patchData.Value;

            var offset = dataRef.PatchOffset ?? 0;

            ArgumentOutOfRangeException.ThrowIfLessThan(patchBuffer.Length, offset + dataRef.Length, nameof(patchData));
            patchBuffer = patchBuffer.Slice(offset, dataRef.Length);

            await file.WriteAsync(patchBuffer, dataRef.Offset, token).ConfigureAwait(false);
        }
        else if (dataRef.Type == ClutDataRef.RefType.Zero)
            await file.WipeAsync(dataRef.Offset, dataRef.Length, token).ConfigureAwait(false);
        else if (dataRef.Type == ClutDataRef.RefType.EmptyBlock)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(dataRef.Length, 24);
            await file.WriteEmptyFileBlockAt(dataRef.BlockCount!.Value, dataRef.Offset).ConfigureAwait(false);
        }
        else
            throw new InvalidOperationException("Invalid data ref type");
    }

    private async IAsyncEnumerable<DataRefOperation> GetDataRefsAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        IEnumerator<DataReference> nonDownloadDataRefs;
        int nonDownloadDataRefCount;
        Dictionary<PatchSection, List<DataReference>?> downloadDataRefs;
        int downloadDataRefCount;
        {
            var allRefs = FileNames.Index()
                .Select(f => (f.Index, Refs: Enumerable.Range(0, Diff.AddedFiles[f.Item].Count)))
                .SelectMany(f => f.Refs.Select(r => new DataReference(f.Index, r)))
                .ToArray();

            var nonDownloadDataRefsEnum = allRefs
                .Where(f => GetRef(f).Type != ClutDataRef.RefType.Patch);
            nonDownloadDataRefCount = nonDownloadDataRefsEnum.Count();
            nonDownloadDataRefs = nonDownloadDataRefsEnum.GetEnumerator();

            var sectionComparer = EqualityComparer<PatchSection>.Create(
                (a, b) =>
                {
                    var aRef = GetRef(a.DataRef);
                    var bRef = GetRef(b.DataRef);
                    return aRef.AppliedVersion == bRef.AppliedVersion && aRef.Patch == bRef.Patch;
                },
                p =>
                {
                    var patch = GetRef(p.DataRef);
                    return HashCode.Combine(patch.AppliedVersion, patch.Patch!.Value);
                });

            downloadDataRefs = [];
            downloadDataRefCount = 0;
            foreach (var dataRef in allRefs)
            {
                var patch = GetRef(dataRef);
                if (patch.Type != ClutDataRef.RefType.Patch)
                    continue;
                var section = new PatchSection(dataRef);
                if (!downloadDataRefs.TryGetValue(section, out var list))
                    downloadDataRefs[section] = list = [];
                downloadDataRefCount++;
                list!.Add(dataRef);
            }

            foreach (var (section, refs) in downloadDataRefs)
            {
                if (refs!.Count == 1)
                    downloadDataRefs[section] = null;
            }
        }

        var downloadTask = GetPatchDataAsync(downloadDataRefs.Keys, token);
        _ = downloadTask.ContinueWith(task => ApplyQueue.Writer.Complete(task.Exception), token);

        var refsNondownload = 0;
        var refsDownload = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (ApplyQueue.Reader.TryRead(out var item))
            {
                if (downloadDataRefs.TryGetValue(item.Interval, out var refs) && refs != null)
                {
                    foreach (var p in refs)
                    {
                        refsDownload++;
                        yield return new(p, item);
                    }
                }
                else
                {
                    refsDownload++;
                    yield return new(item.Interval.DataRef, item);
                }
            }
            else if (nonDownloadDataRefs.MoveNext())
            {
                refsNondownload++;
                yield return new(nonDownloadDataRefs.Current, null);
            }
            else if (!ApplyQueue.Reader.Completion.IsCompleted)
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(5000);
                try
                {
                    await ApplyQueue.Reader.WaitToReadAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {

                }
            }
            else
            {
                await downloadTask.ConfigureAwait(false);
                break;
            }
        }
        if (refsNondownload != nonDownloadDataRefCount)
            throw new InvalidOperationException("Not all non-downloaded refs were processed");
        if (refsDownload != downloadDataRefCount)
            throw new InvalidOperationException("Not all downloaded refs were processed");
    }

    private Task GetPatchDataAsync(IEnumerable<PatchSection> sections, CancellationToken token = default)
    {
        var groupedSections = sections.GroupBy(GetPatchVersion);

        var rangeTasks = new List<Task>();

        return Parallel.ForEachAsync(groupedSections, token, (group, token) =>
            GetPatchesFromVersionAsync(
                group.Key, group,
                (patch, token) => ApplyQueue.Writer.WriteAsync(patch, token),
                token));
    }

    private ValueTask GetPatchesFromVersionAsync(PatchVersion version, IEnumerable<PatchSection> parts, Func<PatchIntervalData, CancellationToken, ValueTask> onRangeRecieved, CancellationToken token = default)
    {
        Log.Info($"Partially downloading {version}");

        List<MergedRange> mergedParts = [];
        foreach (var part in parts.OrderBy(p => GetPatch(p).Offset))
        {
            var last = mergedParts.LastOrDefault();
            var patch = GetPatch(part);
            if (last is null || !last.Add(part, patch))
                mergedParts.Add(new(part, patch));
        }

        var ranges = new List<(List<MergedRange> MergedRanges, RangeHeaderValue Header)>();
        foreach (var range in mergedParts)
        {
            var value = new RangeItemHeaderValue(range.Offset, range.Offset + range.Size - 1);
            var addNew = false;
            if (ranges.Count == 0)
                addNew = true;
            else
            {
                var last = ranges[^1].Header;
                last.Ranges.Add(value);
                if (last.ToString().Length > MAX_RANGE_HEADER_SIZE)
                {
                    last.Ranges.Remove(value);
                    addNew = true;
                }
            }
            if (addNew)
                ranges.Add(([], new()));

            var (mergedRanges, header) = ranges[^1];
            mergedRanges.Add(range);
            header.Ranges.Add(value);
        }

        var ret = Parallel.ForEachAsync(
            ranges, token,
            async (range, token) =>
            {
                var url = new Uri(BasePatchUrl, $"{version}.patch").ToString();
                Log.Info($"Downloading {url} with range {range.Header}");
                await foreach (var (contentRange, stream) in Client.GetPatchRangedAsync(url, version, range.Header, token).WithCancellation(token).ConfigureAwait(false))
                {
                    await foreach (var interval in ProcessStreamIntervalsAsync(contentRange.From!.Value, stream, range.MergedRanges.First(r => r.Offset == contentRange.From!.Value).Parts, token).WithCancellation(token))
                        await onRangeRecieved(interval, token).ConfigureAwait(false);
                }
            }
        );
        return new(ret);
    }

    private async IAsyncEnumerable<PatchIntervalData> ProcessStreamIntervalsAsync(long streamPos, Stream stream, IEnumerable<PatchSection> patches, [EnumeratorCancellation] CancellationToken token = default)
    {
        // Sort intervals by start to process sequentially.
        var sortedIntervals = patches.OrderBy(i => GetPatch(i).Offset).ToList();
        var index = 0;
        var currentPos = streamPos;
        const int SkipBufferSize = 1 << 18;
        var skipBuffer = new byte[SkipBufferSize];

        // Process until all intervals are handled.
        while (index < sortedIntervals.Count)
        {
            // Start a new union segment.
            var unionPatch = GetPatch(sortedIntervals[index]);
            var unionStart = unionPatch.Offset;
            var unionEnd = unionPatch.End;
            List<PatchSection> unionIntervals = [sortedIntervals[index]];
            index++;

            // Merge all intervals that overlap with this union.
            while (index < sortedIntervals.Count)
            {
                var p = GetPatch(sortedIntervals[index]);
                if (p.Offset > unionEnd)
                    break;
                unionIntervals.Add(sortedIntervals[index]);
                unionEnd = Math.Max(unionEnd, p.End);
                index++;
            }

            // Skip bytes until unionStart (if needed).
            var bytesToSkip = unionStart - currentPos;
            while (bytesToSkip > 0)
            {
                var toRead = (int)Math.Min(SkipBufferSize, bytesToSkip);
                var read = await stream.ReadAsync(skipBuffer.AsMemory(0, toRead), token).ConfigureAwait(false);
                if (read == 0)
                {
                    // End of stream reached unexpectedly.
                    yield break;
                }
                bytesToSkip -= read;
                currentPos += read;
            }

            // Read the entire union segment.
            var unionLength = (int)(unionEnd - unionStart);
            var unionBuffer = new byte[unionLength];
            await stream.ReadExactlyAsync(unionBuffer, token).ConfigureAwait(false);
            currentPos += unionLength;

            // Yield each interval from the union by slicing the unionBuffer.
            foreach (var patch in unionIntervals)
            {
                var p = GetPatch(patch);
                var relativeStart = (int)(p.Offset - unionStart);
                var relativeLength = p.Size;
                yield return await PatchIntervalData.CreateAsync(patch, p, unionBuffer.AsMemory(relativeStart, relativeLength), GetRef(patch.DataRef), token).ConfigureAwait(false);
            }
        }
    }

    private PatchVersion GetPatchVersion(PatchSection section) =>
        GetRef(section.DataRef).AppliedVersion;

    private ClutPatchRef GetPatch(PatchSection section) =>
        GetRef(section.DataRef).Patch!.Value;

    private string GetRefName(DataReference reference) =>
        FileNames[reference.FileNameIdx];

    private ClutDataRef GetRef(DataReference reference) =>
        Diff.AddedFiles[GetRefName(reference)][reference.DataRefIdx];

    public void Dispose()
    {
        Client.Dispose();
    }
}
