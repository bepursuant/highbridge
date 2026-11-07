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
    private const uint ProtocolRaw = 0x10000;
    private const uint LeaveCard = 0;
    private const uint StateChanged = 0x2;
    private const uint StateUnaware = 0;
    private const uint StateUnavailable = 0x8;
    private const uint StateEmpty = 0x10;
    private const uint StatePresent = 0x20;
    private const uint StateMute = 0x200;
    private const uint NoReaders = 0x8010002E;
    private const uint Timeout = 0x8010000A;
    private const uint ProtoMismatch = 0x8010000F;
    private const uint InvalidParameter = 0x80100004;
    private const uint SharingViolation = 0x8010000B;
    private const uint ResetCard = 0x80100068;
    private const uint RemovedCard = 0x80100069;
    private const uint Unresponsive = 0x80100066;

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

    /// <summary>
    /// Blocks until a card is present in one of <paramref name="readers"/>, <paramref name="waitMs"/>
    /// elapses, or the token fires. Returns the reader that should be connected.
    /// </summary>
    internal static (string Reader, byte[] Atr) WaitForCard(
        IntPtr context,
        IReadOnlyList<string> readers,
        CardInterface iface,
        string? requestedId,
        int waitMs,
        CancellationToken cancellationToken)
    {
        if (readers.Count == 0)
        {
            throw CardReaderException.NoReaders();
        }

        var names = new IntPtr[readers.Count];
        try
        {
            var states = new ScardReaderState[readers.Count];
            for (int i = 0; i < readers.Count; i++)
            {
                names[i] = Marshal.StringToHGlobalAnsi(readers[i]);
                states[i] = new ScardReaderState
                {
                    Reader = names[i],
                    CurrentState = StateUnaware,
                    Atr = new byte[36],
                };
            }

            Check(SCardGetStatusChange(context, 0, states, (uint)states.Length));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var limit = TimeSpan.FromMilliseconds(Math.Max(0, waitMs));
            bool sawMute = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var present = new List<string>();
                int unavailable = 0;
                for (int i = 0; i < states.Length; i++)
                {
                    uint state = states[i].EventState;
                    if ((state & StateUnavailable) != 0)
                    {
                        unavailable++;
                        continue;
                    }

                    if ((state & StateMute) != 0 && Present(state))
                    {
                        sawMute = true;
                        continue;
                    }

                    if (Present(state))
                    {
                        present.Add(readers[i]);
                    }
                }

                if (present.Count > 0)
                {
                    string chosen = CardSlots.ChoosePresent(present, iface, requestedId);
                    int index = readers.ToList().FindIndex(name => name == chosen);
                    return (chosen, CopyAtr(states[index]));
                }

                if (unavailable == states.Length)
                {
                    throw CardReaderException.ReadFailed();
                }

                if (clock.Elapsed >= limit)
                {
                    throw sawMute ? CardReaderException.CardUnresponsive() : CardReaderException.NoCard();
                }

                for (int i = 0; i < states.Length; i++)
                {
                    // CHANGED must not be fed back in. Contactless drivers treat it as a parameter error.
                    states[i].CurrentState = states[i].EventState & ~StateChanged;
                }

                double remaining = (limit - clock.Elapsed).TotalMilliseconds;
                uint slice = (uint)Math.Clamp((int)remaining, 1, 250);
                int rc = SCardGetStatusChange(context, slice, states, (uint)states.Length);
                if (Code(rc) == Timeout)
                {
                    continue;
                }

                Check(rc);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw CardReaderException.PcscUnavailable(ex);
        }
        finally
        {
            foreach (IntPtr name in names)
            {
                if (name != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(name);
                }
            }
        }
    }

    internal static (IntPtr Card, uint Protocol) Connect(IntPtr context, string reader)
    {
        try
        {
            // Contact is T=0. Contactless is usually T=1, and some drivers reject the combined request
            // or only speak the raw protocol. Try the ordinary pair first, then each protocol alone.
            uint[] attempts = [ProtocolT0 | ProtocolT1, ProtocolT1, ProtocolT0, ProtocolRaw];
            int last = 0;
            foreach (uint protocols in attempts)
            {
                int rc = SCardConnect(context, reader, ShareShared, protocols, out IntPtr card, out uint protocol);
                if (rc == 0)
                {
                    if (protocol is ProtocolT0 or ProtocolT1 or ProtocolRaw)
                    {
                        return (card, protocol);
                    }

                    SCardDisconnect(card, LeaveCard);
                    last = rc;
                    continue;
                }

                last = rc;
                if (!RetryableConnect(Code(rc)))
                {
                    throw Map(rc);
                }
            }

            throw Map(last);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw CardReaderException.PcscUnavailable(ex);
        }
    }

    /// <summary>
    /// Starts an exclusive transaction. Some contactless drivers refuse it; the read can still proceed.
    /// </summary>
    internal static bool TryBegin(IntPtr card)
    {
        try
        {
            int rc = SCardBeginTransaction(card);
            if (rc == 0)
            {
                return true;
            }

            if (Code(rc) == SharingViolation)
            {
                return false;
            }

            throw Map(rc);
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
    internal static byte[] Transmit(IntPtr card, ref uint protocol, byte[] apdu)
    {
        byte[] command = apdu;
        var payload = new List<byte>();
        for (int hop = 0; hop < 8; hop++)
        {
            byte[] raw = TransmitRaw(card, ref protocol, command);
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

    private static byte[] TransmitRaw(IntPtr card, ref uint protocol, byte[] apdu)
    {
        int rc = TransmitOnce(card, protocol, apdu, out byte[] received);
        if (rc == 0)
        {
            return received;
        }

        // Contactless readers often reset the card on the first command. The command was not
        // accepted, so reconnect once and send it again.
        if (Code(rc) is ResetCard or RemovedCard)
        {
            int again = SCardReconnect(card, ShareShared, ProtocolT0 | ProtocolT1 | ProtocolRaw, LeaveCard, out uint next);
            if (again == 0 && next is ProtocolT0 or ProtocolT1 or ProtocolRaw)
            {
                protocol = next;
                rc = TransmitOnce(card, protocol, apdu, out received);
                if (rc == 0)
                {
                    return received;
                }
            }
        }

        throw Map(rc);
    }

    private static int TransmitOnce(IntPtr card, uint protocol, byte[] apdu, out byte[] received)
    {
        received = [];
        IntPtr pci = ProtocolPci(protocol);
        IntPtr owned = IntPtr.Zero;
        try
        {
            if (pci == IntPtr.Zero)
            {
                int size = Marshal.SizeOf<ScardIoRequest>();
                owned = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(new ScardIoRequest { Protocol = protocol, PciLength = (uint)size }, owned, false);
                pci = owned;
            }

            var recv = new byte[258];
            uint length = (uint)recv.Length;
            int rc = SCardTransmit(card, pci, apdu, (uint)apdu.Length, IntPtr.Zero, recv, ref length);
            if (rc != 0)
            {
                return rc;
            }

            if (length < 2 || length > recv.Length)
            {
                throw CardReaderException.ReadFailed();
            }

            received = recv[..(int)length];
            return 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw CardReaderException.PcscUnavailable(ex);
        }
        finally
        {
            if (owned != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(owned);
            }
        }
    }

    /// <summary>
    /// Contactless T=1 drivers reject a caller-built PCI and return a Windows SCard error.
    /// The exported globals are the structures those drivers accept.
    /// </summary>
    private static IntPtr ProtocolPci(uint protocol)
    {
        string export = protocol switch
        {
            ProtocolT1 => "g_rgSCardT1Pci",
            ProtocolRaw => "g_rgSCardRawPci",
            _ => "g_rgSCardT0Pci",
        };
        IntPtr module = GetModuleHandle("winscard.dll");
        if (module == IntPtr.Zero)
        {
            module = LoadLibrary("winscard.dll");
        }

        return module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, export);
    }

    private static void Check(int rc)
    {
        if (rc != 0)
        {
            throw Map(rc);
        }
    }

    private static bool RetryableConnect(uint code) =>
        code is ProtoMismatch or InvalidParameter or Unresponsive or 87;

    private static byte[] CopyAtr(ScardReaderState state)
    {
        if (state.Atr is not { Length: > 0 })
        {
            return [];
        }

        int atrLength = (int)Math.Min(state.AtrLength, (uint)state.Atr.Length);
        if (atrLength <= 0)
        {
            return [];
        }

        var atr = new byte[atrLength];
        Array.Copy(state.Atr, atr, atrLength);
        return atr;
    }

    private static CardReaderException Map(int rc)
    {
        CardReaderException ex = Code(rc) switch
        {
            NoReaders => CardReaderException.NoReaders(),
            0x80100009 => CardReaderException.ReaderNotFound(),
            0x8010000C => CardReaderException.NoCard(),
            RemovedCard or ResetCard or 0x8010002F => CardReaderException.CardRemoved(),
            Unresponsive or 0x80100067 => CardReaderException.CardUnresponsive(),
            Timeout => new CardReaderException("read_timeout", "The card reader did not finish the read. Check the reader, then try again."),
            0x8010001D or 0x8010001E => CardReaderException.PcscUnavailable(),
            _ => CardReaderException.ReadFailed(),
        };
        ex.PcscCode = Code(rc);
        return ex;
    }

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

    [DllImport("winscard.dll", EntryPoint = "SCardReconnect")]
    private static extern int SCardReconnect(IntPtr card, uint shareMode, uint protocols, uint initialization, out uint activeProtocol);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleA", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandle(string name);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryA", CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string name);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
