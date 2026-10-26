using System.Globalization;

namespace Gaska.SelfCheckout.Web;

public static class Fmt
{
    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    public static string Money(decimal value) => value.ToString("#,##0.00", Pl) + " zł";
    public static string Number(decimal value) => value.ToString("#,##0.##", Pl);
    public static string Qty(decimal value, string unit) => $"{Number(value)} {unit}";
}
