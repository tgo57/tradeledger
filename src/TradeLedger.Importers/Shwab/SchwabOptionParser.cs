using System.Globalization;
using System.Text.RegularExpressions;

namespace TradeLedger.Importers.Schwab;

public enum OptionRight { Call, Put }

public sealed record ParsedOptionContract(
    string Underlying,
    DateOnly Expiration,
    decimal Strike,
    OptionRight Right
);

public static class SchwabOptionParser
{
    // Example: "SPXW 02/10/2026 6880.00 P"
    private static readonly Regex Rx = new(
        @"^\s*(?<und>[A-Z0-9\.]+)\s+(?<exp>\d{2}/\d{2}/\d{4})\s+(?<strike>\d+(\.\d+)?)\s+(?<right>[CP])\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool TryParse(string? symbol, out ParsedOptionContract? contract)
    {
        contract = null;
        if (string.IsNullOrWhiteSpace(symbol)) return false;

        var m = Rx.Match(symbol.Trim());
        if (!m.Success) return false;

        var und = m.Groups["und"].Value.ToUpperInvariant();

        if (!DateOnly.TryParseExact(
                m.Groups["exp"].Value,
                "MM/dd/yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var exp))
            return false;

        if (!decimal.TryParse(
                m.Groups["strike"].Value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var strike))
            return false;

        var rightChar = m.Groups["right"].Value.ToUpperInvariant();
        var right = rightChar == "P" ? OptionRight.Put : OptionRight.Call;

        contract = new ParsedOptionContract(und, exp, strike, right);
        return true;
    }
}
