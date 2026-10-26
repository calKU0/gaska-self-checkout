using Gaska.SelfCheckout.Application.Abstractions;
using Gaska.SelfCheckout.Infrastructure.Journal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Gaska.SelfCheckout.Tests.Journal;

public sealed class JsonTransactionJournalTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "gaska-journal-" + Guid.NewGuid().ToString("N"));

    private JsonTransactionJournal Create(int retentionDays = 90) =>
        new(Options.Create(new JournalOptions { Folder = _folder, RetentionDays = retentionDays }), NullLogger<JsonTransactionJournal>.Instance);

    [Fact]
    public async Task Records_survive_a_restart()
    {
        var record = new CheckoutRecord { State = CheckoutState.Started, Amount = 21m, CartJson = "[1]" };
        var journal = Create();
        await journal.AddAsync(record);
        record.State = CheckoutState.PaymentApproved;
        record.DocumentNumber = "PA-1/26/PAR";
        await journal.UpdateAsync(record);

        var reloaded = await Create().GetAsync(record.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(CheckoutState.PaymentApproved, reloaded.State);
        Assert.Equal("PA-1/26/PAR", reloaded.DocumentNumber);
        Assert.Equal(21m, reloaded.Amount);
        Assert.Single(await Create().GetRequiringAttentionAsync());
    }

    [Fact]
    public async Task Transaction_interrupted_by_a_restart_requires_attention()
    {
        var record = new CheckoutRecord { State = CheckoutState.Started, Amount = 10m };
        await Create().AddAsync(record); // the process ends during the payment

        var restarted = Create();

        var reloaded = (await restarted.GetAsync(record.Id))!;
        Assert.Equal(CheckoutState.NeedsAttention, reloaded.State);
        Assert.Contains("restart", reloaded.Error);
        Assert.Single(await restarted.GetRequiringAttentionAsync());
        Assert.Equal(CheckoutState.NeedsAttention, (await Create().GetAsync(record.Id))!.State); // stored on disk
    }

    [Fact]
    public async Task Stored_record_is_a_snapshot()
    {
        var record = new CheckoutRecord { State = CheckoutState.Started };
        var journal = Create();
        await journal.AddAsync(record);

        record.State = CheckoutState.Completed; // changed but not saved

        Assert.Equal(CheckoutState.Started, (await journal.GetAsync(record.Id))!.State);
    }

    [Fact]
    public async Task Ecr_counter_persists_and_wraps()
    {
        Assert.Equal(1, await Create().NextEcrTransactionNumberAsync());
        Assert.Equal(2, await Create().NextEcrTransactionNumberAsync());

        await File.WriteAllTextAsync(Path.Combine(_folder, "ecr-counter.txt"), "999999");
        Assert.Equal(1, await Create().NextEcrTransactionNumberAsync());
    }

    [Fact]
    public async Task Old_finished_records_are_removed_but_open_ones_kept()
    {
        var journal = Create();
        var finished = new CheckoutRecord { CreatedAt = DateTime.Today.AddDays(-100), State = CheckoutState.Completed };
        var open = new CheckoutRecord { CreatedAt = DateTime.Today.AddDays(-100), State = CheckoutState.NeedsAttention };
        await journal.AddAsync(finished);
        await journal.AddAsync(open);

        var reloaded = Create(retentionDays: 90);

        Assert.Null(await reloaded.GetAsync(finished.Id));
        Assert.NotNull(await reloaded.GetAsync(open.Id));
    }

    [Fact]
    public async Task Older_finished_records_are_read_from_disk()
    {
        var old = new CheckoutRecord { CreatedAt = DateTime.Today.AddDays(-30), State = CheckoutState.Completed, DocumentNumber = "PA-9/26/PAR" };
        await Create().AddAsync(old);

        var reloaded = Create();

        Assert.DoesNotContain(await reloaded.GetRecentAsync(100), r => r.Id == old.Id); // not kept in memory
        Assert.Equal("PA-9/26/PAR", (await reloaded.GetAsync(old.Id))?.DocumentNumber);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }
}
