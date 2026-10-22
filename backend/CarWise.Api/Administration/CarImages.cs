using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CarWise.Api.Infrastructure;
using Microsoft.Extensions.Caching.Memory;

namespace CarWise.Api.Administration;

public sealed class CarImages(IJsonDatabase database) : IDisposable
{
    public const int MaxBytes = 512 * 1024;
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
    public static bool ValidId(string id) => Regex.IsMatch(id, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);

    // Accept raster JPEGs only. Canvas-normalized uploads have no EXIF; strip any
    // additional APP/comment metadata at the server boundary as well.
    public static byte[] Normalize(byte[] bytes)
    {
        if (bytes.Length is < 20 or > MaxBytes || bytes[0] != 255 || bytes[1] != 216 || bytes[^2] != 255 || bytes[^1] != 217)
            throw new ArgumentException("נדרשת תמונת JPEG תקינה, עד 512KB לאחר התאמה.");
        using var output = new MemoryStream();
        output.Write(bytes, 0, 2);
        var position = 2;
        var dimensions = false;
        while (position < bytes.Length - 2)
        {
            var start = position;
            if (bytes[position++] != 255) break;
            while (position < bytes.Length && bytes[position] == 255) position++;
            if (position >= bytes.Length) break;
            var marker = bytes[position++];
            if (position + 2 > bytes.Length || marker is 0 or 216 or 217) break;
            var length = (bytes[position] << 8) + bytes[position + 1];
            if (length < 2 || position + length > bytes.Length - 2) break;
            if (marker is 192 or 194)
            {
                if (length < 8) break;
                var height = (bytes[position + 3] << 8) + bytes[position + 4];
                var width = (bytes[position + 5] << 8) + bytes[position + 6];
                if (width is < 1 or > 1600 || height is < 1 or > 1600 || width * height > 1_920_000)
                    throw new ArgumentException("גודל תמונה מרבי: 1600 פיקסלים בצלע ועד 1.92 מיליון פיקסלים.");
                dimensions = true;
            }
            if (marker == 218 && dimensions)
            {
                output.Write(bytes, start, bytes.Length - start);
                return output.ToArray();
            }
            if (marker is not (>= 224 and <= 239) && marker != 254)
                output.Write(bytes, start, position + length - start);
            position += length;
        }
        throw new ArgumentException("מבנה תמונת JPEG אינו תקין.");
    }

    public async Task<string> SaveAsync(byte[] input, string actor, CancellationToken ct)
    {
        var bytes = Normalize(input);
        var id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await database.UpsertAsync("carwise_rules", new JsonArray(new JsonObject
        {
            ["id"] = "car-image:" + id,
            ["document"] = new JsonObject { ["data"] = Convert.ToBase64String(bytes), ["createdBy"] = actor }
        }), ct);
        cache.Set(id, bytes, new MemoryCacheEntryOptions { Size = bytes.Length, SlidingExpiration = TimeSpan.FromMinutes(20) });
        return id;
    }

    public async Task<byte[]?> ReadAsync(string id, CancellationToken ct)
    {
        if (!ValidId(id)) return null;
        if (cache.TryGetValue<byte[]>(id, out var cached)) return cached;
        var rows = await database.ReadAsync("carwise_rules", new Dictionary<string, string> { ["id"] = "car-image:" + id }, limit: 1, ct: ct);
        if (rows.Count == 0) return null;
        var bytes = Convert.FromBase64String(rows[0]!["document"]!["data"]!.GetValue<string>());
        cache.Set(id, bytes, new MemoryCacheEntryOptions { Size = bytes.Length, SlidingExpiration = TimeSpan.FromMinutes(20) });
        return bytes;
    }

    public void Dispose() => cache.Dispose();
}
