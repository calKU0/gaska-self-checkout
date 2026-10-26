namespace Gaska.SelfCheckout.Application.Options;

public sealed class KioskOptions
{
    public const string Section = "Kiosk";

    /// <summary>Workstation id printed on confirmations ("KASA").</summary>
    public string StationId { get; set; } = "SCO1";

    /// <summary>Seconds of inactivity after which the kiosk returns to the start screen.</summary>
    public int IdleTimeoutSeconds { get; set; } = 120;

    /// <summary>Seconds the thank-you screen stays visible after a purchase.</summary>
    public int ThankYouSeconds { get; set; } = 12;

    /// <summary>PIN of the staff service panel.</summary>
    public string ServicePin { get; set; } = "";

    /// <summary>PIN (digits only) protecting the workstation configuration screen.</summary>
    public string AdminPin { get; set; } = "";

    /// <summary>Allow invoices (customer selected by tax id / card).</summary>
    public bool InvoicesEnabled { get; set; } = true;

    /// <summary>Maximum amount of a single transaction (safety limit).</summary>
    public decimal MaxTransactionAmount { get; set; } = 20000m;

    /// <summary>
    /// Seconds between checks of the terminal, printer, XL and database (0 = off). A failed check is repeated after
    /// 15 s; the kiosk stops selling after <see cref="HealthFailuresBeforeStop"/> failures in a row.
    /// </summary>
    public int HealthCheckSeconds { get; set; } = 60;

    public int HealthFailuresBeforeStop { get; set; } = 2;

    /// <summary>Volume of the staff alarm (help call, lost connection) on the kiosk speaker, 0–100 (0 = silent).</summary>
    public int AlarmVolume { get; set; } = 80;
}

public sealed class CheckoutOptions
{
    public const string Section = "Checkout";

    /// <summary>Also print the merchant copy of the card confirmation (by default it is kept electronically).</summary>
    public bool PrintMerchantCopy { get; set; }

    /// <summary>Print a confirmation for declined authorizations.</summary>
    public bool PrintDeclinedSlip { get; set; } = true;

    /// <summary>
    /// Ask the customer whether to print the confirmation of an approved payment (DCC confirmations are always printed –
    /// the customer must get the exchange rate and markup). The merchant copy is kept electronically either way.
    /// </summary>
    public bool AskToPrintConfirmation { get; set; } = true;

    /// <summary>Seconds to wait for the answer; no answer = no printout (the customer has left).</summary>
    public int PrintQuestionSeconds { get; set; } = 30;

    /// <summary>
    /// Prepare the ERP document (header + lines) while the customer pays on the terminal.
    /// After authorization only the payment is added and the document is confirmed.
    /// </summary>
    public bool PrepareDocumentDuringPayment { get; set; } = true;

    /// <summary>How many times to retry creating the ERP document on transient errors (e.g. deadlock).</summary>
    public int ErpRetries { get; set; } = 2;
}

/// <summary>
/// Pick-up ticket printed when goods are taken from a non-primary warehouse. Its layout is fixed
/// (see PickupTicketBuilder); only the state of the map stored in the printer is kept here.
/// </summary>
public sealed class PickupOptions
{
    public const string Section = "Pickup";

    /// <summary>Number of consecutive printer slots the map occupies (set when the map is uploaded; one slot holds max. 640 dots of height).</summary>
    public int MapImageParts { get; set; } = 1;
}
