using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using NTwain;
using NTwain.Data;
using Highbridge.Scanning;
using ColorMode = Highbridge.Scanning.ColorMode;

namespace Highbridge.Twain;

/// <summary>What the scan will actually use after negotiation.</summary>
internal sealed record NegotiatedScan(XferMech Mechanism, bool ZeroIsBlack);

/// <summary>
/// Applies <see cref="ScanOptions"/> to an open source (TWAIN state 4). Nothing here is fatal: a
/// capability the source lacks or rejects keeps the source's own value, and the scan goes ahead.
/// Many sources, the WIA-TWAIN layer in particular, support only a few capabilities.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class CapabilityNegotiator(DataSource source, TwainSession session, ILogger logger)
{
    private static readonly XferMech[] HandledMechanisms = [XferMech.Native, XferMech.Memory, XferMech.File, XferMech.MemFile];

    public NegotiatedScan Apply(ScanOptions options)
    {
        var caps = source.Capabilities;
        LogSupportedCaps(caps);

        // Transfer mechanism first: the allowed values of the others can depend on it.
        TrySet(caps.ICapXferMech, CapabilityId.ICapXferMech, XferMech.Native, UInt16(XferMech.Native));
        var mechanism = Current(caps.ICapXferMech) ?? XferMech.Native;
        if (!HandledMechanisms.Contains(mechanism))
        {
            logger.LogWarning("The source reports transfer mechanism {Mechanism}; highbridge will treat it as a native transfer", mechanism);
            mechanism = XferMech.Native;
        }

        ApplyPixelType(caps, options.ColorMode);
        ApplyResolution(caps, options.Dpi);
        ApplyPaperSource(caps, options.Source);
        ApplyDuplex(caps, options.Duplex);

        if (options.EffectivePageLimit() is int pages)
        {
            TrySet(caps.CapXferCount, CapabilityId.CapXferCount, pages, new TWOneValue { Item = (uint)pages, ItemType = ItemType.Int16 });
        }

        if (!options.ShowDriverUi)
        {
            // Many drivers otherwise pop up a progress window on every scan, and some wait for a click.
            TrySet(caps.CapIndicators, CapabilityId.CapIndicators, BoolType.False, Bool(false));
        }

        bool zeroIsBlack = (caps.ICapPixelFlavor.IsSupported ? Current(caps.ICapPixelFlavor) : null) != PixelFlavor.Vanilla;
        logger.LogInformation(
            "Settings in effect: transfer {Mechanism}, pixel type {PixelType}, resolution {XRes} x {YRes}, units {Units}, feeder {Feeder}, duplex {Duplex}, pixel flavor {Flavor}",
            mechanism,
            Describe(caps.ICapPixelType),
            Describe(caps.ICapXResolution),
            Describe(caps.ICapYResolution),
            Describe(caps.ICapUnits),
            Describe(caps.CapFeederEnabled),
            Describe(caps.CapDuplexEnabled),
            zeroIsBlack ? "chocolate (0 = black)" : "vanilla (0 = white)");
        return new NegotiatedScan(mechanism, zeroIsBlack);
    }

    private void LogSupportedCaps(ICapabilities caps)
    {
        try
        {
            var ids = caps.CapSupportedCaps.IsSupported ? caps.CapSupportedCaps.GetValues().ToList() : [];
            logger.LogInformation("The source lists {Count} capabilities: {Caps}", ids.Count, ids.Count > 0 ? string.Join(", ", ids) : "(CAP_SUPPORTEDCAPS unsupported or empty)");
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Could not read CAP_SUPPORTEDCAPS");
        }
    }

    private void ApplyPixelType(ICapabilities caps, ColorMode mode)
    {
        PixelType[] preference = mode switch
        {
            ColorMode.BlackWhite => [PixelType.BlackWhite, PixelType.Gray, PixelType.RGB],
            ColorMode.Gray => [PixelType.Gray, PixelType.RGB, PixelType.BlackWhite],
            _ => [PixelType.RGB, PixelType.Gray, PixelType.BlackWhite],
        };
        var allowed = Values(caps.ICapPixelType);
        var chosen = allowed.Count == 0 ? preference[0] : preference.FirstOrDefault(allowed.Contains, preference[0]);
        if (chosen != preference[0])
        {
            logger.LogInformation("The source offers pixel types {Allowed}, not {Wanted}; asking for {Chosen}", string.Join(", ", allowed), preference[0], chosen);
        }

        if (TrySet(caps.ICapPixelType, CapabilityId.ICapPixelType, chosen, UInt16(chosen)) && chosen == PixelType.BlackWhite && caps.ICapBitDepth.IsSupported)
        {
            TrySet(caps.ICapBitDepth, CapabilityId.ICapBitDepth, 1, new TWOneValue { Item = 1, ItemType = ItemType.UInt16 });
        }
    }

    private void ApplyResolution(ICapabilities caps, int dpi)
    {
        // Resolutions are in ICAP_UNITS, so make sure those are inches first.
        if (caps.ICapUnits.IsSupported)
        {
            TrySet(caps.ICapUnits, CapabilityId.ICapUnits, Unit.Inches, UInt16(Unit.Inches));
        }

        SetResolution(caps.ICapXResolution, CapabilityId.ICapXResolution, dpi);
        SetResolution(caps.ICapYResolution, CapabilityId.ICapYResolution, dpi);
    }

    private void SetResolution(ICapWrapper<TWFix32> cap, CapabilityId id, int dpi)
    {
        if (!cap.IsSupported)
        {
            logger.LogInformation("{Cap}: not supported by the source; keeping its resolution", id);
            return;
        }

        double target = dpi;
        try
        {
            var raw = cap.GetValuesRaw();
            target = raw.ContainerType switch
            {
                ContainerType.Range => NearestValue.FromRange(ToDouble(raw.RangeMinValue), ToDouble(raw.RangeMaxValue), ToDouble(raw.RangeStepSize), dpi),
                ContainerType.Enum or ContainerType.Array => NearestValue.FromList(raw.CollectionValues.Select(ToDouble), dpi) ?? dpi,
                ContainerType.OneValue => ToDouble(raw.OneValue),
                _ => dpi,
            };
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "{Cap}: could not read the allowed values; asking for {Dpi}", id, dpi);
        }

        if (target != dpi)
        {
            logger.LogInformation("{Cap}: {Dpi} dpi is not offered; using the nearest, {Target}", id, dpi, target);
        }

        TWFix32 value = target;
        TrySet(cap, id, value, value.ToOneValue());
    }

    private void ApplyPaperSource(ICapabilities caps, PaperSource wanted)
    {
        var feeder = caps.CapFeederEnabled;
        bool hasFeeder = feeder.IsSupported;
        switch (wanted)
        {
            case PaperSource.Feeder when !hasFeeder:
                logger.LogWarning("The page asked for the document feeder, but the source has no CAP_FEEDERENABLED (a flatbed-only scanner, or a driver that hides the feeder); scanning from its default paper source");
                break;
            case PaperSource.Feeder:
                if (TrySet(feeder, CapabilityId.CapFeederEnabled, BoolType.True, Bool(true)))
                {
                    if (caps.CapAutoFeed.IsSupported)
                    {
                        TrySet(caps.CapAutoFeed, CapabilityId.CapAutoFeed, BoolType.True, Bool(true));
                    }

                    if (caps.CapFeederLoaded.IsSupported && Current(caps.CapFeederLoaded) == BoolType.False)
                    {
                        logger.LogWarning("CAP_FEEDERLOADED says there is no paper in the feeder");
                    }
                }

                break;
            case PaperSource.Flatbed when hasFeeder:
                TrySet(feeder, CapabilityId.CapFeederEnabled, BoolType.False, Bool(false));
                break;
            case PaperSource.Flatbed:
                logger.LogInformation("Flatbed requested; the source has no feeder control, nothing to set");
                break;
            default:
                logger.LogInformation("Paper source: the source's default (feeder enabled: {Feeder})", hasFeeder ? Describe(feeder) : "no feeder control");
                break;
        }
    }

    /// <summary>
    /// Enables the feeder when this scan uses it. Null when the scan is not a feeder scan.
    /// False when CAP_FEEDERLOADED says the feeder is empty. True when paper is loaded, or when
    /// the source cannot report that capability.
    /// </summary>
    internal bool? OneSheetFeeder(ScanOptions options)
    {
        var feeder = source.Capabilities.CapFeederEnabled;
        bool hasFeeder = feeder.IsSupported;
        switch (options.Source)
        {
            case PaperSource.Flatbed:
                return null;
            case PaperSource.Feeder when !hasFeeder:
                logger.LogWarning("One sheet was requested, but the source has no CAP_FEEDERENABLED; this scan will not wait for paper");
                return null;
            case PaperSource.Feeder:
                bool enabled = TrySet(feeder, CapabilityId.CapFeederEnabled, BoolType.True, Bool(true));
                if (!enabled && Current(feeder) != BoolType.True)
                {
                    logger.LogWarning("Could not enable the feeder for a one-sheet scan; this scan will not wait for paper");
                    return null;
                }

                if (source.Capabilities.CapAutoFeed.IsSupported)
                {
                    TrySet(source.Capabilities.CapAutoFeed, CapabilityId.CapAutoFeed, BoolType.True, Bool(true));
                }

                return FeederIsLoaded();
            default:
                if (!hasFeeder || Current(feeder) != BoolType.True)
                {
                    return null;
                }

                return FeederIsLoaded();
        }
    }

    /// <summary>True when the feeder has paper, or when the source cannot say.</summary>
    internal bool FeederIsLoaded()
    {
        var loaded = source.Capabilities.CapFeederLoaded;
        if (!loaded.IsSupported)
        {
            return true;
        }

        // Unknown reads as "don't wait forever". False is the only empty answer.
        return Current(loaded) != BoolType.False;
    }

    private void ApplyDuplex(ICapabilities caps, bool duplex)
    {
        var enabled = caps.CapDuplexEnabled;
        if (duplex)
        {
            var kind = caps.CapDuplex.IsSupported ? Current(caps.CapDuplex) : null;
            if (!enabled.IsSupported || kind is null or Duplex.None)
            {
                logger.LogWarning("Duplex requested, but the source cannot scan both sides (CAP_DUPLEX {Kind}, CAP_DUPLEXENABLED {Enabled})", kind?.ToString() ?? "unsupported", enabled.IsSupported ? "supported" : "unsupported");
                return;
            }

            TrySet(enabled, CapabilityId.CapDuplexEnabled, BoolType.True, Bool(true));
        }
        else if (enabled.IsSupported && Current(enabled) == BoolType.True)
        {
            TrySet(enabled, CapabilityId.CapDuplexEnabled, BoolType.False, Bool(false));
        }
    }

    /// <summary>Sets one capability (MSG_SET with a TW_ONEVALUE). Returns false, after logging why, if it did not take.</summary>
    private bool TrySet<T>(IReadOnlyCapWrapper<T> cap, CapabilityId id, T value, TWOneValue raw)
    {
        try
        {
            if (!cap.IsSupported)
            {
                logger.LogInformation("{Cap}: not supported by the source; keeping its default", id);
                return false;
            }

            string before = Describe(cap);
            if (cap.CanGetCurrent && EqualityComparer<T>.Default.Equals(cap.GetCurrent(), value))
            {
                logger.LogInformation("{Cap}: already {Value}", id, value);
                return true;
            }

            if (!cap.CanSet)
            {
                logger.LogInformation("{Cap}: the source does not allow setting it; keeping {Current}", id, before);
                return false;
            }

            ReturnCode rc;
            using (var twCap = new TWCapability(id, raw))
            {
                rc = source.DGControl.Capability.Set(twCap);
            }

            if (rc is ReturnCode.Success or ReturnCode.CheckStatus)
            {
                logger.LogInformation("{Cap}: {Before} -> asked for {Value}, {ReturnCode}; now {After}", id, before, value, rc, Describe(cap));
                return true;
            }

            logger.LogWarning(
                "{Cap}: the source rejected {Value}: {ReturnCode}, {Status}, TWAIN state {State}, thread {Thread}; keeping {Before}",
                id, value, rc, TwainDiagnostics.SourceStatus(source), session.State, Environment.CurrentManagedThreadId, before);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Cap}: setting {Value} failed; keeping the source's value", id, value);
            return false;
        }
    }

    private T? Current<T>(IReadOnlyCapWrapper<T> cap)
        where T : struct
    {
        try
        {
            return cap.IsSupported && cap.CanGetCurrent ? cap.GetCurrent() : null;
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Could not read the current value of a capability");
            return null;
        }
    }

    private static List<T> Values<T>(IReadOnlyCapWrapper<T> cap)
    {
        try
        {
            return cap.IsSupported && cap.CanGet ? cap.GetValues().ToList() : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string Describe<T>(IReadOnlyCapWrapper<T> cap)
    {
        try
        {
            if (!cap.IsSupported)
            {
                return "unsupported";
            }

            return cap.CanGetCurrent ? Convert.ToString(cap.GetCurrent(), CultureInfo.InvariantCulture) ?? "null" : "unreadable";
        }
        catch (Exception ex)
        {
            return $"unreadable ({ex.GetType().Name})";
        }
    }

    private static double ToDouble(object? value) => value switch
    {
        TWFix32 fix => fix,
        null => 0,
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
    };

    private static TWOneValue UInt16<TEnum>(TEnum value)
        where TEnum : struct, Enum => new() { Item = Convert.ToUInt32(value, CultureInfo.InvariantCulture), ItemType = ItemType.UInt16 };

    private static TWOneValue Bool(bool value) => new() { Item = value ? 1u : 0u, ItemType = ItemType.Bool };
}
