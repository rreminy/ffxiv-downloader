using FFXIVDownloader.Thaliak;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FFXIVDownloader.Lut;

[DebuggerDisplay("{DebuggerDisplay,nq}")]
public struct ClutDataRef : IEquatable<ClutDataRef>
{
    public enum RefType : byte
    {
        Patch = 0,
        Zero = 1,
        EmptyBlock = 2,
        _FullPatch = 0xFF,
    }

    public RefType Type { get; init; }
    public PatchVersion AppliedVersion { get; init; }
    public long Offset { get; set; }
    public int Length { get; set; }
    public int? BlockCount { get; init; }
    public int? PatchOffset { get; init; }
    public ClutPatchRef? Patch { get; init; }

    public readonly long End => Offset + Length;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private readonly string DebuggerDisplay => $"{Offset:X8} - {End:X8} ({Type})";

    public ClutDataRef()
    {
        Type = RefType.Zero;
        Offset = 0;
        Length = 0;
    }

    public ClutDataRef(BinaryReader reader, ReadOnlySpan<PatchVersion> patchMap, ref long patchOffset)
    {
        var type = (RefType)reader.ReadByte();
        if (type == RefType._FullPatch)
            Type = RefType.Patch;
        else
            Type = type;

        AppliedVersion = patchMap[reader.Read7BitEncodedInt()];
        if (Type == RefType.EmptyBlock)
            BlockCount = reader.ReadInt32();
        if (Type == RefType.Patch)
            Patch = new(reader, ref patchOffset);
        if (type == RefType.Patch)
            PatchOffset = reader.Read7BitEncodedInt();
    }

    public void ReadOffset(BinaryReader reader, ref long lastOffset)
    {
        Offset = reader.Read7BitEncodedInt64() + lastOffset;
        lastOffset = Offset;
    }

    public void ReadLength(BinaryReader reader)
    {
        Length = reader.Read7BitEncodedInt();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void Write(BinaryWriter writer, FrozenDictionary<PatchVersion, int> patchMap, ref long patchOffset)
    {
        var type = Type;
        if (Type == RefType.Patch && (PatchOffset ?? 0) == 0)
            type = RefType._FullPatch;

        writer.Write((byte)type);
        writer.Write7BitEncodedInt(patchMap[AppliedVersion]);
        if (Type == RefType.EmptyBlock)
            writer.Write(BlockCount!.Value);
        if (Type == RefType.Patch)
            Patch!.Value.Write(writer, ref patchOffset);
        if (type == RefType.Patch)
            writer.Write7BitEncodedInt(PatchOffset ?? 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteOffset(BinaryWriter writer, ref long lastOffset)
    {
        writer.Write7BitEncodedInt64(Offset - lastOffset);
        lastOffset = Offset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly void WriteLength(BinaryWriter writer)
    {
        writer.Write7BitEncodedInt(Length);
    }

    public readonly bool Equals(ClutDataRef other)
    {
        if (Type != other.Type)
            return false;
        if (Offset != other.Offset)
            return false;
        if (Length != other.Length)
            return false;
        if (BlockCount != other.BlockCount)
            return false;
        if (PatchOffset != other.PatchOffset)
            return false;
        if (Patch != other.Patch)
            return false;
        return true;
    }

    public override readonly bool Equals(object? obj) => obj is ClutDataRef other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Type, Offset, Length, BlockCount, PatchOffset, Patch);

    public static bool operator ==(ClutDataRef left, ClutDataRef right) => left.Equals(right);

    public static bool operator !=(ClutDataRef left, ClutDataRef right) => !left.Equals(right);

    public static ClutDataRef FromRawPatchData(PatchVersion version, long patchOffset, long fileOffset, int length)
    {
        return new ClutDataRef
        {
            Type = RefType.Patch,
            AppliedVersion = version,
            Offset = fileOffset,
            Length = length,
            PatchOffset = 0,
            Patch = new ClutPatchRef
            {
                Offset = patchOffset,
                Size = length,
                IsCompressed = false
            }
        };
    }

    public static ClutDataRef FromZeros(PatchVersion version, long fileOffset, int length)
    {
        return new ClutDataRef
        {
            Type = RefType.Zero,
            AppliedVersion = version,
            Offset = fileOffset,
            Length = length,
            Patch = null
        };
    }

    public static (ClutDataRef, ClutDataRef?) FromEmpty(PatchVersion version, long fileOffset, int length)
    {
        if ((length & 0x7F) != 0)
            throw new ArgumentException("Length must be a multiple of 128", nameof(length));
        var empty = new ClutDataRef
        {
            Type = RefType.EmptyBlock,
            AppliedVersion = version,
            Offset = fileOffset,
            Length = 24,
            BlockCount = length >> 7,
            Patch = null
        };
        ClutDataRef? zero = null;
        if (length != 0)
        {
            zero = new ClutDataRef
            {
                Type = RefType.Zero,
                AppliedVersion = version,
                Offset = fileOffset + 24,
                Length = length - 24,
                Patch = null
            };
        }
        return (empty, zero);
    }

    public static ClutDataRef FromCompressedPatchData(PatchVersion version, long patchOffset, long fileOffset, int compressedLength, int length)
    {
        return new ClutDataRef
        {
            Type = RefType.Patch,
            AppliedVersion = version,
            Offset = fileOffset,
            Length = length,
            PatchOffset = 0,
            Patch = new ClutPatchRef
            {
                Offset = patchOffset,
                Size = compressedLength,
                IsCompressed = true
            }
        };
    }

    // fileOffset is the offset to write to the file
    // patchFileOffset is the offset of the uncompressed data in the patch
    public static ClutDataRef FromSplitPatchData(PatchVersion version, ClutPatchRef patch, long fileOffset, int patchOffset, int length)
    {
        return new ClutDataRef
        {
            Type = RefType.Patch,
            AppliedVersion = version,
            Offset = fileOffset,
            Length = length,
            PatchOffset = patchOffset,
            Patch = patch
        };
    }

    public static ClutDataRef FromSliceInterval(ref readonly ClutDataRef source, long fileOffsetStart, long fileOffsetEnd) =>
        FromSlice(in source, fileOffsetStart, checked((int)(fileOffsetEnd - fileOffsetStart)));

    public static ClutDataRef FromSlice(ref readonly ClutDataRef source, long fileOffset, int length)
    {
        // Validate slice bounds
        if (fileOffset < source.Offset || fileOffset + length > source.End)
            throw new ArgumentException($"Slice bounds ({fileOffset}-{fileOffset + length}) are outside source bounds ({source.Offset}-{source.End})");

        return source.Type switch
        {
            RefType.Zero => FromZeros(source.AppliedVersion, fileOffset, length),
            RefType.EmptyBlock => throw new InvalidOperationException("Cannot slice an EmptyBlock"),
            RefType.Patch => FromSplitPatchData(source.AppliedVersion, source.Patch!.Value, fileOffset, checked((int)(fileOffset - source.Offset + source.PatchOffset!.Value)), length),
            _ => throw new InvalidOperationException("Unknown RefType"),
        };
    }
}
