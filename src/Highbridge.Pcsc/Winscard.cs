using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Highbridge.Cards;

namespace Highbridge.Pcsc;

/// <summary>
/// P/Invoke to the system winscard.dll. The tray app is win-x86, so pointer-sized fields follow
/// the process. This assembly is compiled on Linux CI and is not loaded there; nothing here runs
/// unless a Windows host calls it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Winscard
{
    private const uint ScopeUser = 0;
    private const uint ShareShared = 2;
    private const uint ProtocolT0 = 1;
    private const uint ProtocolT1 = 2;
    private const uint LeaveCard = 0;
    private const uint StateUnaware = 0;
    private const uint StateEmpty = 0x10;
    private const uint StatePresent = 0x20;
    private const uint StateMute = 0x200;
    private const uint StateUnavailable = 0x8;
    private const uint NoReaders = 0x8010002E;
    private const uint Timeout = 0x8010000A;

    internal static IntPtr Establish()
    {
        try
        {
            int rc = SCardEstablishContext(ScopeUser, IntPtr.Zero, IntPtr.Zero, out IntPtr context);
            if (rc != 0)
            {
                throw CardReaderException.PcscUnavailable();
            }

            return context;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw CardReaderException.PcscUnavailable(ex);
        }
    }

    internal static void Release(IntPtr context)
    {
        if (context != IntPtr.Zero)
        {
            try
            {
                SCardReleaseContext(context);
            }
            catch (DllNotFoundException)
            {
            }
        }
    }

    internal static IReadOnlyList<string> ListReaders(IntPtr context)
    {
        try
        {
            uint length = 0;
            int rc = SCardListReaders(context, null, null, ref length);
            if (Code(rc) == NoReaders)
            {
                return [];
            }

            if (rc != 0)
            {
                throw Map(rc);
            }

            if (length <= 1)
            {
                return [];
            }

            var buffer = new byte[length];
            rc = SCardListReaders(context, null, buffer, ref length);
            if (Code(rc) == NoReaders)
            {
                return [];
            }

            if (rc != 0)
            {
                throw Map(rc);
            }

            return SplitReaders(buffer, length);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw CardReaderException.PcscUnavailable(ex);
        }
    }

    /// <summary>Blocks until a card is present, <paramref name="waitMs"/> elapses, or the token fires.</summary>
    internal static byte[] WaitForCard(IntPtr context, string reader, int waitMs, CancellationToken cancellationToken)
    {
        IntPtr name = Marshal.StringToHGlobalAnsi(reader);
        try
        {
            var states = new[]
            {
                new ScardReaderState
                {
                    Reader = name,
                    CurrentState = StateUnaware,
                    Atr = new byte[36],
                },
            };
            Check(SCardGetStatusChange(context, 0, states, 1));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var limit = TimeSpan.FromMilliseconds(Math.Max(0, waitMs));
            while (!Present(states[0].EventState))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((states[0].EventState & StateMute) != 0)
                {
                    throw CardReaderException.CardUnresponsive();
                }

                if ((states[0].EventState & StateUnavailable) != 0)
                {
                    throw CardReaderException.ReadFailed();
                }

                if (clock.Elapsed >= limit)
                {
                    throw CardReaderException.NoCard();
                }

                states[0].CurrentState = states[0].EventState;
                double remaining = (limit - clock.Elapsed).TotalMilliseconds;
                uint slice = (uint)Math.Clamp((int)remaining, 1, 250);
                int rc = SCardGetStatusChange(context, slice, states, 1);
                if (Code(rc) == Timeout)
                {
                    continue;
                }

                Check(rc);
            }

            int atrLength = (int)Math.Min(states[0].AtrLength, (uint)states[0].Atr.Length);
            var atr = new byte[atrLength];
            Array.Copy(states[0].Atr, atr, atrLength);
            return atr;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw CardReaderException.PcscUnavailable(ex);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    internal static (IntPtr Card, uint Protocol) Connect(IntPtr context, string reader)
    {
        try
        {
            int rc = SCardConnect(context, reader, ShareShared, ProtocolT0 | ProtocolT1, out IntPtr card, out uint protocol);
            if (rc != 0)
            {
                throw Map(rc);
            }

            if (protocol is not (ProtocolT0 or ProtocolT1))
            {
                SCardDisconnect(card, LeaveCard);
                throw CardReaderException.ReadFailed();
            }

            return (card, protocol);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw CardReaderException.PcscUnavailable(ex);
        }
    }

    internal static void Begin(IntPtr card)
    {
        try
        {
            Check(SCardBeginTransaction(card));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw CardReaderException.PcscUnavailable(ex);
        }
    }

    internal static void End(IntPtr card)
    {
        try
        {
            SCardEndTransaction(card, LeaveCard);
        }
        catch (DllNotFoundException)
        {
        }
    }

    internal static void Disconnect(IntPtr card)
    {
        if (card != IntPtr.Zero)
        {
            try
            {
                SCardDisconnect(card, LeaveCard);
            }
            catch (DllNotFoundException)
            {
            }
        }
    }

    /// <summary>Sends one command and follows 61xx GET RESPONSE and 6Cxx length retries. Status word stays on the end.</summary>
    internal static byte[] Transmit(IntPtr card, uint protocol, byte[] apdu)
    {
        byte[] command = apdu;
        var payload = new List<byte>();
        for (int hop = 0; hop < 8; hop++)
        {
            byte[] raw = TransmitRaw(card, protocol, command);
            if (!Emv.TrySplit(raw, out byte[] body, out ushort status))
            {
                throw CardReaderException.ReadFailed();
            }

            if ((status & 0xFF00) == 0x6100)
            {
                payload.AddRange(body);
                command = Emv.GetResponse((byte)(status & 0xFF));
                continue;
            }

            if ((status & 0xFF00) == 0x6C00)
            {
                command = Emv.WithLe(command, (byte)(status & 0xFF));
                continue;
            }

            payload.AddRange(body);
            payload.Add((byte)(status >> 8));
            payload.Add((byte)status);
            return payload.ToArray();
        }

        throw CardReaderException.ReadFailed();
    }

    private static byte[] TransmitRaw(IntPtr card, uint protocol, byte[] apdu)
    {
        int size = Marshal.SizeOf<ScardIoRequest>();
        IntPtr pci = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(new ScardIoRequest { Protocol = protocol, PciLength = (uint)size }, pci, false);
            var recv = new byte[258];
            uint length = (uint)recv.Length;
            int rc = SCardTransmit(card, pci, apdu, (uint)apdu.Length, IntPtr.Zero, recv, ref length);
            if (rc != 0)
            {
                throw Map(rc);
            }

            if (length < 2 || length > recv.Length)
            {
                throw CardReaderException.ReadFailed();
            }

            return recv[..(int)length];
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw CardReaderException.PcscUnavailable(ex);
        }
        finally
        {
            Marshal.FreeHGlobal(pci);
        }
    }

    private static void Check(int rc)
    {
        if (rc != 0)
        {
            throw Map(rc);
        }
    }

    private static CardReaderException Map(int rc) => Code(rc) switch
    {
        NoReaders => CardReaderException.NoReaders(),
        0x80100009 => CardReaderException.ReaderNotFound(),
        0x8010000C => CardReaderException.NoCard(),
        0x80100069 or 0x80100068 => CardReaderException.CardRemoved(),
        0x80100066 or 0x80100067 => CardReaderException.CardUnresponsive(),
        Timeout => new CardReaderException("read_timeout", "The card reader did not finish the read. Check the reader, then try again."),
        0x8010001D or 0x8010001E => CardReaderException.PcscUnavailable(),
        _ => CardReaderException.ReadFailed(),
    };

    private static uint Code(int rc) => unchecked((uint)rc);

    private static bool Present(uint state) => (state & StatePresent) != 0 && (state & StateEmpty) == 0;

    private static List<string> SplitReaders(byte[] buffer, uint length)
    {
        var names = new List<string>();
        int start = 0;
        int end = (int)Math.Min(length, (uint)buffer.Length);
        for (int i = 0; i < end; i++)
        {
            if (buffer[i] != 0)
            {
                continue;
            }

            if (i == start)
            {
                break;
            }

            names.Add(Encoding.Latin1.GetString(buffer, start, i - start));
            start = i + 1;
        }

        return names;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScardIoRequest
    {
        public uint Protocol;
        public uint PciLength;
    }

    /// <summary>Matches winscard SCARD_READERSTATE. IntPtr fields follow the process bitness.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ScardReaderState
    {
        public IntPtr Reader;
        public IntPtr UserData;
        public uint CurrentState;
        public uint EventState;
        public uint AtrLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)]
        public byte[] Atr;
    }

    [DllImport("winscard.dll", EntryPoint = "SCardEstablishContext")]
    private static extern int SCardEstablishContext(uint scope, IntPtr reserved1, IntPtr reserved2, out IntPtr context);

    [DllImport("winscard.dll", EntryPoint = "SCardReleaseContext")]
    private static extern int SCardReleaseContext(IntPtr context);

    [DllImport("winscard.dll", EntryPoint = "SCardListReadersA", CharSet = CharSet.Ansi)]
    private static extern int SCardListReaders(IntPtr context, string? groups, byte[]? readers, ref uint readersLength);

    [DllImport("winscard.dll", EntryPoint = "SCardGetStatusChangeA")]
    private static extern int SCardGetStatusChange(IntPtr context, uint timeout, [In, Out] ScardReaderState[] states, uint count);

    [DllImport("winscard.dll", EntryPoint = "SCardConnectA", CharSet = CharSet.Ansi)]
    private static extern int SCardConnect(IntPtr context, string reader, uint shareMode, uint protocols, out IntPtr card, out uint activeProtocol);

    [DllImport("winscard.dll", EntryPoint = "SCardDisconnect")]
    private static extern int SCardDisconnect(IntPtr card, uint disposition);

    [DllImport("winscard.dll", EntryPoint = "SCardBeginTransaction")]
    private static extern int SCardBeginTransaction(IntPtr card);

    [DllImport("winscard.dll", EntryPoint = "SCardEndTransaction")]
    private static extern int SCardEndTransaction(IntPtr card, uint disposition);

    [DllImport("winscard.dll", EntryPoint = "SCardTransmit")]
    private static extern int SCardTransmit(
        IntPtr card,
        IntPtr sendPci,
        byte[] sendBuffer,
        uint sendLength,
        IntPtr recvPci,
        byte[] recvBuffer,
        ref uint recvLength);
}
