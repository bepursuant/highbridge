using System.Text;

namespace Highbridge.Provenance;

/// <summary><c>stamp=top|bottom</c> in highbridge://scan URLs; absent means no stamp.</summary>
internal static class StampUriParameter
{
    public const string Name = "stamp";

    public static bool TryRead(IReadOnlyDictionary<string, string> parameters, out StampOptions? stamp, out string? error)
    {
        stamp = null;
        error = null;
        if (!parameters.TryGetValue(Name, out string? raw))
        {
            return true;
        }

        switch (raw.ToLowerInvariant())
        {
            case "bottom":
                stamp = new StampOptions { Position = StampPosition.Bottom };
                return true;
            case "top":
                stamp = new StampOptions { Position = StampPosition.Top };
                return true;
            default:
                error = $"unknown stamp '{raw}' (use top or bottom)";
                return false;
        }
    }

    public static string? Format(StampOptions? stamp) =>
        stamp is null ? null : stamp.Position == StampPosition.Top ? "top" : "bottom";
}
