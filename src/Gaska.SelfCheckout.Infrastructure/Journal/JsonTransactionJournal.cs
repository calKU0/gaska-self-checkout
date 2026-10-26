using System.Collections.Concurrent;
using System.Text.Json;
using Gaska.SelfCheckout.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Infrastructure.Journal;

public sealed class JournalOptions
{
    public const string Section = "Journal";

    /// <summary>Folder of the journal files (relative to the application folder).</summary>
    public string Folder { get; set; } = "data/journal";

    /// <summary>Days after which finished transactions are deleted (open / needing attention ones are kept).</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Days of finished transactions kept in memory for the service panel; older ones are read from disk on demand.</summary>
    public int MemoryDays { get; set; } = 7;
}

/// <summary>
/// Local, durable kiosk transaction journal: one JSON file per purchase attempt (data/journal/yyyy-MM-dd/{id}.json),
/// written atomically. It is the source of truth after a failure (payment taken, document state unknown) and feeds
/// the service panel; recent and open records are also kept in memory.
/// </summary>
public sealed class JsonTransactionJournal : ITransactionJournal
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly CheckoutState[] OpenStates =
        [CheckoutState.Started, CheckoutState.PaymentApproved, CheckoutState.DocumentCreated, CheckoutState.NeedsAttention];

    private readonly string _folder;
    private readonly string _counterFile;
    private readonly ConcurrentDictionary<Guid, CheckoutRecord> _records = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ILogger<JsonTransactionJournal> _logger;

    public JsonTransactionJournal(IOptions<JournalOptions> options, ILogger<JsonTransactionJournal> logger)
    {
        _logger = logger;
        _folder = Path.GetFullPath(options.Value.Folder, AppContext.BaseDirectory);
        _counterFile = Path.Combine(_folder, "ecr-counter.txt");
        Directory.CreateDirectory(_folder);
        Load(options.Value.RetentionDays, options.Value.MemoryDays);
    }

    public Task AddAsync(CheckoutRecord record, CancellationToken ct = default) => SaveAsync(record, ct);

    public Task UpdateAsync(CheckoutRecord record, CancellationToken ct = default) => SaveAsync(record, ct);

    public async Task<CheckoutRecord?> GetAsync(Guid id, CancellationToken ct = default)
    {
        if (_records.TryGetValue(id, out var record))
            return Copy(record);

        // older finished transaction – only on disk
        var file = Directory.EnumerateFiles(_folder, $"{id}.json", SearchOption.AllDirectories).FirstOrDefault();
        return file is null ? null : JsonSerializer.Deserialize<CheckoutRecord>(await File.ReadAllTextAsync(file, ct), Json);
    }

    public Task<IReadOnlyList<CheckoutRecord>> GetRecentAsync(int count, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CheckoutRecord>>(_records.Values.OrderByDescending(r => r.CreatedAt).Take(count).Select(Copy).ToList());

    public Task<IReadOnlyList<CheckoutRecord>> GetRequiringAttentionAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CheckoutRecord>>(_records.Values
            .Where(r => r.State is CheckoutState.NeedsAttention or CheckoutState.PaymentApproved or CheckoutState.DocumentCreated)
            .OrderByDescending(r => r.CreatedAt).Take(100).Select(Copy).ToList());

    public async Task<int> NextEcrTransactionNumberAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            var current = File.Exists(_counterFile) && int.TryParse(await File.ReadAllTextAsync(_counterFile, ct), out var v) ? v : 0;
            var next = current >= 999999 ? 1 : current + 1;
            await WriteAtomicAsync(_counterFile, next.ToString(), ct);
            return next;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task SaveAsync(CheckoutRecord record, CancellationToken ct)
    {
        var copy = Copy(record);
        await _writeGate.WaitAsync(ct);
        try
        {
            await WriteAtomicAsync(FilePath(copy), JsonSerializer.Serialize(copy, Json), ct);
            _records[copy.Id] = copy;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private string FilePath(CheckoutRecord record) =>
        Path.Combine(_folder, record.CreatedAt.ToString("yyyy-MM-dd"), $"{record.Id}.json");

    /// <summary>Writes to a temporary file and replaces the target – a crash never leaves a half-written record.</summary>
    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(stream))
            await writer.WriteAsync(content.AsMemory(), ct);
        File.Move(temp, path, overwrite: true);
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private void Load(int retentionDays, int memoryDays)
    {
        var cutoff = DateTime.Today.AddDays(-Math.Max(1, retentionDays));
        var memoryCutoff = DateTime.Today.AddDays(-Math.Max(1, memoryDays));
        foreach (var day in Directory.EnumerateDirectories(_folder))
        {
            var dated = DateTime.TryParseExact(Path.GetFileName(day), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date);
            var expired = dated && date < cutoff;
            var inMemory = !dated || date >= memoryCutoff;
            foreach (var file in Directory.EnumerateFiles(day, "*.json"))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<CheckoutRecord>(File.ReadAllText(file), Json);
                    if (record is null)
                        continue;
                    if (record.State == CheckoutState.Started)
                    {
                        // the process ended during the payment (crash, power loss) – the card may have been charged
                        record.State = CheckoutState.NeedsAttention;
                        record.Error = (string.IsNullOrEmpty(record.Error) ? "" : record.Error + " | ") +
                                       "Interrupted by an application restart – check the terminal's last transaction and the document in XL.";
                        record.UpdatedAt = DateTime.Now;
                        WriteAtomic(file, JsonSerializer.Serialize(record, Json));
                        _logger.LogWarning("Transaction {Id} was interrupted by a restart – marked as requiring attention", record.Id);
                    }

                    var open = OpenStates.Contains(record.State);
                    if (expired && !open)
                    {
                        File.Delete(file);
                        continue;
                    }

                    if (inMemory || open)
                        _records[record.Id] = record;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Journal file {File} could not be read", file);
                }
            }

            if (expired && !Directory.EnumerateFileSystemEntries(day).Any())
                Directory.Delete(day);
        }

        _logger.LogInformation("Transaction journal: {Count} records in {Folder}", _records.Count, _folder);
    }

    // callers keep mutating their record object – the journal stores its own snapshot
    private static CheckoutRecord Copy(CheckoutRecord record) =>
        JsonSerializer.Deserialize<CheckoutRecord>(JsonSerializer.Serialize(record, Json), Json)!;
}
