using TradeLedger.Importers.Schwab;
//using TradeLedger.Importers.Schwab.OptionParsers; // adjust if needed

namespace TradeLedger.Importers.Tastytrade;

public sealed class TastytradeOptionParser
{
    // TODO: Implement when you confirm tastytrade symbol format
    public static bool TryParse(string symbol, out ParsedOptionContract? contract)
    {
        contract = null;
        return false;
    }
}
