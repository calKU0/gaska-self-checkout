namespace Gaska.SelfCheckout.Devices.Polcard;

public sealed class PolcardOptions
{
    public const string Section = "Polcard";

    /// <summary>Terminal address (ECR interface) – set per workstation.</summary>
    public string Host { get; set; } = "";
    public int Port { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>Maximum silence during a transaction (the terminal can extend it with pinpad_wait).</summary>
    public int TransactionIdleTimeoutSeconds { get; set; } = 120;

    /// <summary>Response timeout for short operations (hello, last transaction, status).</summary>
    public int ResponseTimeoutSeconds { get; set; } = 30;

    /// <summary>Timeout of the end-of-day operation (transaction upload to the host).</summary>
    public int ReportTimeoutSeconds { get; set; } = 300;

    /// <summary>Sale transaction code (00 – sale; 09 – sale with cashback).</summary>
    public string SaleTransactionType { get; set; } = "00";

    /// <summary>ECR system data sent in ecr_hello_extended (tags 1C10, 1C11, 1C12, 1C19).</summary>
    public string EcrName { get; set; } = "";
    public string EcrVersion { get; set; } = "1.0.0";
    public string EcrModificationDate { get; set; } = "2026/10/07 12:00:00";

    /// <summary>ECR-POS protocol version in NNNmmpp format (4.75.13 → 0047513).</summary>
    public string ProtocolVersion { get; set; } = "0047513";

    /// <summary>End-of-day report type (ecr_report_start): 1 – upload/batch report.</summary>
    public int CloseDayReportType { get; set; } = 1;

    /// <summary>End-of-day additional action: 1 – transaction upload / close batch + configuration download.</summary>
    public int CloseDayAdditionalAction { get; set; } = 1;
}
