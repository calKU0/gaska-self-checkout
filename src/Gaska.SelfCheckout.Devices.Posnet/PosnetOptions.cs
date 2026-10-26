namespace Gaska.SelfCheckout.Devices.Posnet;

public sealed class PosnetOptions
{
    public const string Section = "Posnet";

    public string PortName { get; set; } = "";
    public int BaudRate { get; set; } = 9600;

    public int ResponseTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// The COM port is shared with the ERP fiscal printing – it is opened only for a job and retried while busy.
    /// </summary>
    public int PortBusyRetries { get; set; } = 20;
    public int PortBusyDelayMs { get; set; } = 500;

    /// <summary>
    /// Code page of the printer PC interface – it must match what the ERP sends for receipts (Comarch ERP XL: Mazovia).
    /// Auto reads it from the printer.
    /// </summary>
    public PosnetCodePage CodePage { get; set; } = PosnetCodePage.Auto;

    /// <summary>Line width; 0 = read from the printer (prnwidthget).</summary>
    public int LineWidth { get; set; }

    /// <summary>Non-fiscal form number – 200 (super form, free lines).</summary>
    public int FormNumber { get; set; } = 200;

    /// <summary>QR code module size in dots.</summary>
    public int QrModuleSize { get; set; } = 6;

    /// <summary>Super form headers: 29 – card payment confirmation, 34 – authorization declined, 33 – reversal, 84 – non-fiscal document.</summary>
    public int HeaderCardPayment { get; set; } = 29;
    public int HeaderCardDeclined { get; set; } = 34;
    public int HeaderCardReversal { get; set; } = 33;
    public int HeaderReport { get; set; } = 84;
    public int HeaderPickupTicket { get; set; } = 84;
    public int HeaderOther { get; set; } = 84;
}
