using System.Security.Cryptography;
using System.Text;

namespace TradeLedger.Core.Models;

public static class ExecutionFingerprint
{
    public static string Make(Execution e)
    {
        var key = $"{e.Broker}|{e.Account}|{e.ExecutedAt:O}|{e.Action}|{e.Symbol}|{e.Quantity}|{e.Price}|{e.NetAmount}|{e.Fees}";
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes);
    }
}
