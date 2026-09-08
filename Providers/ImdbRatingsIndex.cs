using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ImdbRatings.Providers;

/// <summary>
/// Compact on-disk index of IMDb ratings, sized for random-access lookups by the metadata provider.
/// </summary>
/// <remarks>
/// The IMDb ratings flat file is ~30 MB of TSV that costs roughly a second to parse. The provider needs
/// single-key lookups on every scanned item, so the scheduled task writes this index as a byproduct of the
/// parse it already performs and the provider loads it with sequential buffered reads instead.
///
/// Layout (multi-byte values little-endian):
/// <code>
///   magic      8 bytes   "IMDBRIDX"
///   version    4 bytes   uint32
///   count      4 bytes   uint32
///   ids        4 * count uint32, ascending
///   ratings    1 * count byte, averageRating * 10 (10..100)
///   votes      4 * count uint32
/// </code>
/// Ratings are stored as a single byte because IMDb publishes exactly one decimal place; votes are kept
/// verbatim so the minimum-votes threshold stays a runtime setting rather than something baked in at build time.
/// </remarks>
public sealed class ImdbRatingsIndex
{
    /// <summary>
    /// Current on-disk format version. Bump when the layout changes so stale indexes are discarded.
    /// </summary>
    public const uint FormatVersion = 1;

    private const int HeaderSize = 16;
    private const int MaxCount = 20_000_000;
    private const int TransferBufferSize = 128 * 1024;
    private static readonly byte[] MagicBytes = "IMDBRIDX"u8.ToArray();

    private readonly uint[] _ids;
    private readonly byte[] _ratings;
    private readonly uint[] _votes;

    private ImdbRatingsIndex(uint[] ids, byte[] ratings, uint[] votes)
    {
        _ids = ids;
        _ratings = ratings;
        _votes = votes;
    }

    /// <summary>
    /// Gets the number of rated titles held in the index.
    /// </summary>
    public int Count => _ids.Length;

    /// <summary>
    /// Gets the approximate resident size of the index in bytes.
    /// </summary>
    public long ApproximateSizeInBytes => ((long)_ids.Length * sizeof(uint))
        + _ratings.Length
        + ((long)_votes.Length * sizeof(uint));

    /// <summary>
    /// Gets the path the index is written to for a given Jellyfin data directory.
    /// </summary>
    public static string GetIndexPath(string dataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        return Path.Join(dataPath, "imdb-ratings-cache", "title.ratings.idx");
    }

    /// <summary>
    /// Converts an IMDb rating to the single-byte form used on disk.
    /// </summary>
    public static byte EncodeRating(float rating)
    {
        var scaled = (int)MathF.Round(rating * 10f, MidpointRounding.AwayFromZero);
        return (byte)Math.Clamp(scaled, 0, byte.MaxValue);
    }

    /// <summary>
    /// Parses a "tt"-prefixed IMDb ID into its numeric form, rejecting anything wider than a <see cref="uint"/>.
    /// </summary>
    public static bool TryParseImdbId(string? imdbId, out uint numericId)
    {
        numericId = 0;

        if (string.IsNullOrEmpty(imdbId)
            || imdbId.Length < 3
            || imdbId[0] != 't'
            || imdbId[1] != 't')
        {
            return false;
        }

        ulong accumulator = 0;
        for (int i = 2; i < imdbId.Length; i++)
        {
            char c = imdbId[i];
            if (c < '0' || c > '9')
            {
                return false;
            }

            accumulator = (accumulator * 10) + (ulong)(c - '0');
            if (accumulator > uint.MaxValue)
            {
                return false;
            }
        }

        numericId = (uint)accumulator;
        return true;
    }

    /// <summary>
    /// Builds an index from parallel arrays, sorting them into ascending ID order.
    /// </summary>
    /// <remarks>
    /// The source arrays are sorted in place. The IMDb flat file is byte-sorted rather than numerically sorted
    /// (tt10001002 precedes tt9916850), so an explicit sort is required before binary search is valid.
    /// </remarks>
    public static ImdbRatingsIndex CreateSorted(uint[] ids, byte[] ratings, uint[] votes)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(ratings);
        ArgumentNullException.ThrowIfNull(votes);

        if (ids.Length != ratings.Length || ids.Length != votes.Length)
        {
            throw new ArgumentException("Index arrays must all be the same length.", nameof(ids));
        }

        // Sort a packed payload alongside the keys so both value arrays follow the key permutation.
        var payload = new ulong[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            payload[i] = ((ulong)ratings[i] << 32) | votes[i];
        }

        Array.Sort(ids, payload);

        for (int i = 0; i < ids.Length; i++)
        {
            ratings[i] = (byte)(payload[i] >> 32);
            votes[i] = (uint)(payload[i] & 0xFFFFFFFF);
        }

        return new ImdbRatingsIndex(ids, ratings, votes);
    }

    /// <summary>
    /// Looks up a rating by IMDb ID, honouring the supplied minimum-votes threshold.
    /// </summary>
    public bool TryGetRating(string? imdbId, int minimumVotes, out float rating, out int votes)
    {
        rating = 0;
        votes = 0;

        if (!TryParseImdbId(imdbId, out var numericId))
        {
            return false;
        }

        int position = Array.BinarySearch(_ids, numericId);
        if (position < 0)
        {
            return false;
        }

        var candidateVotes = _votes[position];
        if (candidateVotes < (uint)Math.Max(minimumVotes, 0))
        {
            return false;
        }

        rating = _ratings[position] / 10f;
        votes = (int)Math.Min(candidateVotes, int.MaxValue);
        return true;
    }

    /// <summary>
    /// Writes the index to disk, replacing any existing file atomically.
    /// </summary>
    public async Task WriteAsync(string indexPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);

        var directory = Path.GetDirectoryName(indexPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = indexPath + ".tmp";

        try
        {
            var header = new byte[HeaderSize];
            MagicBytes.CopyTo(header, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), FormatVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), (uint)_ids.Length);

            var stream = new FileStream(
                tempPath,
                new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.Create,
                    Options = FileOptions.Asynchronous,
                    PreallocationSize = GetPayloadSize(_ids.Length),
                    Share = FileShare.None
                });

            await using (stream.ConfigureAwait(false))
            {
                byte[] transferBuffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
                try
                {
                    await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                    await WriteArrayAsync(stream, _ids, transferBuffer, cancellationToken).ConfigureAwait(false);
                    await stream.WriteAsync(_ratings, cancellationToken).ConfigureAwait(false);
                    await WriteArrayAsync(stream, _votes, transferBuffer, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(transferBuffer);
                }
            }

            File.Move(tempPath, indexPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Loads an index from disk, returning <see langword="null"/> when the file is absent, truncated,
    /// or written by a different format version.
    /// </summary>
    public static async Task<ImdbRatingsIndex?> TryLoadAsync(string indexPath, CancellationToken cancellationToken)
    {
        return await TryLoadAsync(indexPath, cancellationToken, afterOpen: null).ConfigureAwait(false);
    }

    internal static async Task<ImdbRatingsIndex?> TryLoadAsync(
        string indexPath,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? afterOpen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);

        FileStream stream;
        try
        {
            stream = new FileStream(
                indexPath,
                new FileStreamOptions
                {
                    Access = FileAccess.Read,
                    Mode = FileMode.Open,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    // Replacement is atomic only if readers permit the old directory entry to be renamed/deleted
                    // on Windows while this handle is still consuming it.
                    Share = FileShare.Read | FileShare.Delete
                });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            if (afterOpen is not null)
            {
                await afterOpen(cancellationToken).ConfigureAwait(false);
            }

            // Validate the header before allocating anything sized by the file, so a corrupt or bloated
            // index is rejected rather than turned into a multi-hundred-megabyte allocation.
            if (stream.Length < HeaderSize || stream.Length > GetPayloadSizeUnchecked(MaxCount))
            {
                return null;
            }

            var header = new byte[HeaderSize];
            if (!await TryReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false)
                || !header.AsSpan(0, MagicBytes.Length).SequenceEqual(MagicBytes)
                || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4)) != FormatVersion)
            {
                return null;
            }

            var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
            if (count > MaxCount || stream.Length != GetPayloadSizeUnchecked((long)count))
            {
                return null;
            }

            var ids = new uint[count];
            var ratings = new byte[count];
            var votes = new uint[count];

            byte[] transferBuffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
            try
            {
                if (!await TryReadArrayAsync(stream, ids, transferBuffer, cancellationToken).ConfigureAwait(false)
                    || !await TryReadExactlyAsync(stream, ratings, cancellationToken).ConfigureAwait(false)
                    || !await TryReadArrayAsync(stream, votes, transferBuffer, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(transferBuffer);
            }

            return new ImdbRatingsIndex(ids, ratings, votes);
        }
    }

    private static async Task WriteArrayAsync(
        Stream stream,
        Array source,
        byte[] transferBuffer,
        CancellationToken cancellationToken)
    {
        int sourceOffset = 0;
        int bytesRemaining = Buffer.ByteLength(source);
        while (bytesRemaining > 0)
        {
            int bytesToWrite = Math.Min(bytesRemaining, transferBuffer.Length);
            Buffer.BlockCopy(source, sourceOffset, transferBuffer, 0, bytesToWrite);
            await stream.WriteAsync(transferBuffer.AsMemory(0, bytesToWrite), cancellationToken).ConfigureAwait(false);
            sourceOffset += bytesToWrite;
            bytesRemaining -= bytesToWrite;
        }
    }

    private static async Task<bool> TryReadArrayAsync(
        Stream stream,
        Array destination,
        byte[] transferBuffer,
        CancellationToken cancellationToken)
    {
        int destinationOffset = 0;
        int bytesRemaining = Buffer.ByteLength(destination);
        while (bytesRemaining > 0)
        {
            int bytesToRead = Math.Min(bytesRemaining, transferBuffer.Length);
            if (!await TryReadExactlyAsync(
                    stream,
                    transferBuffer.AsMemory(0, bytesToRead),
                    cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            Buffer.BlockCopy(transferBuffer, 0, destination, destinationOffset, bytesToRead);
            destinationOffset += bytesToRead;
            bytesRemaining -= bytesToRead;
        }

        return true;
    }

    private static async Task<bool> TryReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = await stream.ReadAsync(destination[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            totalRead += read;
        }

        return true;
    }

    private static long GetPayloadSizeUnchecked(long count)
    {
        return HeaderSize + (count * sizeof(uint)) + count + (count * sizeof(uint));
    }

    private static int GetPayloadSize(int count)
    {
        return (int)GetPayloadSizeUnchecked(count);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup of the temp file.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup of the temp file.
        }
    }
}
