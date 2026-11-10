using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NightDim.Core;

public sealed record GammaJournalEntry(GammaRamp Baseline, GammaRamp? Applied, GammaRamp? Candidate)
{
    public bool Owns(GammaRamp ramp) => Applied?.Matches(ramp) == true || Candidate?.Matches(ramp) == true;
}

public interface IGammaJournal
{
    bool TryRead(string id, out GammaJournalEntry? entry);
    bool TrySave(string id, GammaJournalEntry entry);
    bool TryRemove(string id);
}

public sealed class FileGammaJournal(string directory) : IGammaJournal
{
    private sealed record DiskEntry(string DisplayId, ushort[] Baseline, ushort[]? Applied, ushort[]? Candidate);

    public bool TryRead(string id, out GammaJournalEntry? entry)
    {
        entry = null;
        try
        {
            using FileStream file = File.OpenRead(GetPath(id));
            DiskEntry? saved = JsonSerializer.Deserialize<DiskEntry>(file);
            if (saved?.DisplayId != id || saved.Baseline?.Length != GammaRamp.Size ||
                (saved.Applied is null && saved.Candidate is null) ||
                (saved.Applied is not null && saved.Applied.Length != GammaRamp.Size) ||
                (saved.Candidate is not null && saved.Candidate.Length != GammaRamp.Size))
                return false;

            var baseline = new GammaRamp(saved.Baseline);
            if (!baseline.IsRestorable())
                return false;

            entry = new GammaJournalEntry(baseline,
                saved.Applied is null ? null : new GammaRamp(saved.Applied),
                saved.Candidate is null ? null : new GammaRamp(saved.Candidate));
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public bool TrySave(string id, GammaJournalEntry entry) => AtomicJsonFile.TryWrite(GetPath(id),
        new DiskEntry(id, entry.Baseline.Values.ToArray(), entry.Applied?.Values.ToArray(), entry.Candidate?.Values.ToArray()));

    public bool TryRemove(string id) => AtomicJsonFile.TryDelete(GetPath(id));

    private string GetPath(string id) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");
}

public sealed class MemoryGammaJournal : IGammaJournal
{
    private readonly Dictionary<string, GammaJournalEntry> _entries = new(StringComparer.Ordinal);

    public bool TryRead(string id, out GammaJournalEntry? entry)
    {
        _entries.TryGetValue(id, out entry);
        return true;
    }

    public bool TrySave(string id, GammaJournalEntry entry)
    {
        _entries[id] = entry;
        return true;
    }

    public bool TryRemove(string id)
    {
        _entries.Remove(id);
        return true;
    }
}
