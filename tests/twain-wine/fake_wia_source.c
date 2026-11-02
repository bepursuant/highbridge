/*
 * A fake TWAIN 1.9 data source for testing highbridge under Wine. It behaves like the
 * troublesome real-world cases, Windows' WIA-TWAIN layer (wiatwain.ds) and COM-based vendor
 * drivers:
 *
 *   - Capability MSG_GET works from any thread, but MSG_SET/MSG_RESET fail with TWCC_BUMMER unless
 *     they come from the thread that opened the source (a COM object called from the wrong
 *     apartment).
 *   - No MSG_QUERYSUPPORT, no DAT_CALLBACK/DAT_CALLBACK2, no feeder caps (flatbed only).
 *   - After MSG_ENABLEDS it "scans" on a worker thread and then posts its own registered message
 *     to TW_USERINTERFACE.hParent only. With hParent = NULL the message is lost and the
 *     application waits forever. MSG_PROCESSEVENT turns the message into MSG_XFERREADY.
 *   - Native (DIB) and memory (strip) transfers.
 *
 * Wine's twain_32.dll only loads sources named sane.ds or gphoto2.ds, so install it as
 * c:\windows\twain_32\sane.ds. Environment variables:
 *   FAKE_TWAIN_PAGES=n          pages per scan (default 1)
 *   FAKE_TWAIN_NO_XFERREADY=1   never signal a transfer (a driver stuck in its own dialog)
 *   FAKE_TWAIN_MEMORY_ONLY=1    only memory transfers; setting ICAP_XFERMECH=Native fails
 *   FAKE_TWAIN_ANY_THREAD=1     accept sets from any thread
 *   FAKE_TWAIN_REJECT_SETS=1    fail every MSG_SET with TWCC_BUMMER; the scan uses the defaults
 *
 * Build: i686-w64-mingw32-gcc -shared -O2 -o sane.ds fake_wia_source.c -Wl,--kill-at
 */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#pragma pack(push, 2)
typedef unsigned short TW_UINT16;
typedef unsigned long TW_UINT32;
typedef short TW_INT16;
typedef long TW_INT32;
typedef unsigned short TW_BOOL;
typedef char TW_STR32[34];

typedef struct { TW_UINT16 MajorNum, MinorNum, Language, Country; TW_STR32 Info; } TW_VERSION;
typedef struct {
    TW_UINT32 Id; TW_VERSION Version; TW_UINT16 ProtocolMajor, ProtocolMinor; TW_UINT32 SupportedGroups;
    TW_STR32 Manufacturer, ProductFamily, ProductName;
} TW_IDENTITY;
typedef struct { TW_UINT16 Cap, ConType; HANDLE hContainer; } TW_CAPABILITY;
typedef struct { TW_UINT16 ItemType; TW_UINT32 Item; } TW_ONEVALUE;
typedef struct { TW_UINT16 ItemType; TW_UINT32 NumItems, CurrentIndex, DefaultIndex; unsigned char ItemList[1]; } TW_ENUMERATION;
typedef struct { TW_UINT16 ItemType; TW_UINT32 NumItems; unsigned char ItemList[1]; } TW_ARRAY;
typedef struct { TW_INT16 Whole; TW_UINT16 Frac; } TW_FIX32;
typedef struct { TW_BOOL ShowUI, ModalUI; HWND hParent; } TW_USERINTERFACE;
typedef struct { void *pEvent; TW_UINT16 TWMessage; } TW_EVENT;
typedef struct { TW_UINT16 Count; TW_UINT32 EOJ; } TW_PENDINGXFERS;
typedef struct { TW_UINT16 ConditionCode, Data; } TW_STATUS;
typedef struct {
    TW_FIX32 XResolution, YResolution; TW_INT32 ImageWidth, ImageLength; TW_INT16 SamplesPerPixel;
    TW_INT16 BitsPerSample[8]; TW_INT16 BitsPerPixel; TW_BOOL Planar; TW_INT16 PixelType; TW_UINT16 Compression;
} TW_IMAGEINFO;
typedef struct { TW_UINT32 MinBufSize, MaxBufSize, Preferred; } TW_SETUPMEMXFER;
typedef struct { TW_UINT32 Flags, Length; void *TheMem; } TW_MEMORY;
typedef struct {
    TW_UINT16 Compression; TW_UINT32 BytesPerRow, Columns, Rows, XOffset, YOffset, BytesWritten; TW_MEMORY Memory;
} TW_IMAGEMEMXFER;
#pragma pack(pop)

enum { DG_CONTROL = 1, DG_IMAGE = 2 };
enum {
    DAT_CAPABILITY = 1, DAT_EVENT = 2, DAT_IDENTITY = 3, DAT_PENDINGXFERS = 5, DAT_SETUPMEMXFER = 6,
    DAT_STATUS = 8, DAT_USERINTERFACE = 9, DAT_XFERGROUP = 10,
    DAT_IMAGEINFO = 0x101, DAT_IMAGEMEMXFER = 0x103, DAT_IMAGENATIVEXFER = 0x104
};
enum {
    MSG_GET = 1, MSG_GETCURRENT = 2, MSG_GETDEFAULT = 3, MSG_SET = 6, MSG_RESET = 7, MSG_QUERYSUPPORT = 8,
    MSG_XFERREADY = 0x101, MSG_OPENDS = 0x401, MSG_CLOSEDS = 0x402, MSG_DISABLEDS = 0x501,
    MSG_ENABLEDS = 0x502, MSG_PROCESSEVENT = 0x601, MSG_ENDXFER = 0x701, MSG_RESETXFERS = 0x0007
};
enum { TWRC_SUCCESS, TWRC_FAILURE, TWRC_CHECKSTATUS, TWRC_CANCEL, TWRC_DSEVENT, TWRC_NOTDSEVENT, TWRC_XFERDONE };
enum { TWCC_SUCCESS = 0, TWCC_BUMMER = 1, TWCC_LOWMEMORY = 2, TWCC_BADCAP = 6, TWCC_BADPROTOCOL = 9, TWCC_BADVALUE = 10, TWCC_SEQERROR = 11 };
enum { TWON_ARRAY = 3, TWON_ENUMERATION = 4, TWON_ONEVALUE = 5 };
enum { TWTY_INT16 = 1, TWTY_UINT16 = 4, TWTY_BOOL = 6, TWTY_FIX32 = 7 };
enum {
    CAP_XFERCOUNT = 0x0001, ICAP_PIXELTYPE = 0x0101, ICAP_UNITS = 0x0102, ICAP_XFERMECH = 0x0103,
    CAP_SUPPORTEDCAPS = 0x1005, CAP_INDICATORS = 0x100b, ICAP_XRESOLUTION = 0x1118, ICAP_YRESOLUTION = 0x1119
};

static const TW_UINT16 kSupportedCaps[] = {
    CAP_XFERCOUNT, ICAP_PIXELTYPE, ICAP_UNITS, ICAP_XFERMECH, CAP_SUPPORTEDCAPS, CAP_INDICATORS,
    ICAP_XRESOLUTION, ICAP_YRESOLUTION
};
static const int kResolutions[] = { 75, 100, 150, 200, 300, 600 };

static TW_UINT16 g_cc;
static DWORD g_openThread;
static BOOL g_open, g_enabled;
static UINT g_message;
static HWND g_parent;
static int g_pending, g_pagesPerScan = 1, g_noXferReady, g_memoryOnly, g_anyThread, g_rejectSets;
static TW_UINT16 g_xferMech, g_pixelType = 2;
static int g_dpi = 150, g_xferCount = -1, g_indicators = 1;
static int g_memRow;

static FILE *g_log;
static void logf_(const char *fmt, ...)
{
    if (!g_log) return;
    va_list ap;
    va_start(ap, fmt);
    fprintf(g_log, "[fake-ds thread %lu] ", GetCurrentThreadId());
    vfprintf(g_log, fmt, ap);
    fputc('\n', g_log);
    fflush(g_log);
    va_end(ap);
}

static int env_int(const char *name, int fallback)
{
    char buf[16];
    DWORD n = GetEnvironmentVariableA(name, buf, sizeof buf);
    return n > 0 && n < sizeof buf ? atoi(buf) : fallback;
}

static TW_UINT16 fail(TW_UINT16 cc) { g_cc = cc; return TWRC_FAILURE; }

static void fill_identity(TW_IDENTITY *id)
{
    TW_UINT32 keep = id->Id;
    memset(id, 0, sizeof *id);
    id->Id = keep;
    id->Version.MajorNum = 1;
    id->Version.MinorNum = 0;
    strcpy(id->Version.Info, "fake WIA layer 1.0");
    id->ProtocolMajor = 1;
    id->ProtocolMinor = 9;
    id->SupportedGroups = DG_CONTROL | DG_IMAGE;
    strcpy(id->Manufacturer, "highbridge test");
    strcpy(id->ProductFamily, "WIA test");
    strcpy(id->ProductName, "WIA-highbridge test scanner");
}

static int width_px(void) { return g_dpi * 2; }
static int height_px(void) { return g_dpi * 3; }
static int bits_per_pixel(void) { return g_pixelType == 0 ? 1 : g_pixelType == 1 ? 8 : 24; }

static unsigned char sample(int x, int y, int channel)
{
    int band = (y * 8) / height_px();
    unsigned char base = (unsigned char)((x * 255) / (width_px() - 1));
    return channel == 0 ? base : channel == 1 ? (unsigned char)(band * 32) : (unsigned char)(255 - base);
}

static int bw_bit(int x, int y) { return ((x / 16) + (y / 16)) & 1; }

/* ---- capability containers ---------------------------------------------------------------- */

static HANDLE one_value(TW_UINT16 type, TW_UINT32 item)
{
    HANDLE h = GlobalAlloc(GHND, sizeof(TW_ONEVALUE));
    TW_ONEVALUE *v = GlobalLock(h);
    v->ItemType = type;
    v->Item = item;
    GlobalUnlock(h);
    return h;
}

static TW_UINT32 fix32(int value) { TW_FIX32 f = { (TW_INT16)value, 0 }; TW_UINT32 u; memcpy(&u, &f, 4); return u; }

static HANDLE enum_u16(const TW_UINT16 *items, int n, int current)
{
    HANDLE h = GlobalAlloc(GHND, sizeof(TW_ENUMERATION) + n * 2);
    TW_ENUMERATION *e = GlobalLock(h);
    e->ItemType = TWTY_UINT16;
    e->NumItems = n;
    e->CurrentIndex = current;
    e->DefaultIndex = 0;
    memcpy(e->ItemList, items, n * 2);
    GlobalUnlock(h);
    return h;
}

static HANDLE enum_resolutions(void)
{
    int n = sizeof kResolutions / sizeof kResolutions[0], current = 0;
    HANDLE h = GlobalAlloc(GHND, sizeof(TW_ENUMERATION) + n * 4);
    TW_ENUMERATION *e = GlobalLock(h);
    e->ItemType = TWTY_FIX32;
    e->NumItems = n;
    for (int i = 0; i < n; i++)
    {
        TW_UINT32 f = fix32(kResolutions[i]);
        memcpy(e->ItemList + i * 4, &f, 4);
        if (kResolutions[i] == g_dpi) current = i;
    }
    e->CurrentIndex = current;
    e->DefaultIndex = 2;
    GlobalUnlock(h);
    return h;
}

static HANDLE array_caps(void)
{
    int n = sizeof kSupportedCaps / sizeof kSupportedCaps[0];
    HANDLE h = GlobalAlloc(GHND, sizeof(TW_ARRAY) + n * 2);
    TW_ARRAY *a = GlobalLock(h);
    a->ItemType = TWTY_UINT16;
    a->NumItems = n;
    memcpy(a->ItemList, kSupportedCaps, n * 2);
    GlobalUnlock(h);
    return h;
}

static TW_UINT16 cap_get(TW_CAPABILITY *cap, TW_UINT16 msg)
{
    BOOL one = msg != MSG_GET;
    switch (cap->Cap)
    {
    case CAP_SUPPORTEDCAPS:
        cap->ConType = TWON_ARRAY; cap->hContainer = array_caps(); return TWRC_SUCCESS;
    case ICAP_XFERMECH:
    {
        static const TW_UINT16 all[] = { 0, 2 }, memOnly[] = { 2 };
        if (one) { cap->ConType = TWON_ONEVALUE; cap->hContainer = one_value(TWTY_UINT16, g_xferMech); }
        else if (g_memoryOnly) { cap->ConType = TWON_ENUMERATION; cap->hContainer = enum_u16(memOnly, 1, 0); }
        else { cap->ConType = TWON_ENUMERATION; cap->hContainer = enum_u16(all, 2, g_xferMech == 2); }
        return TWRC_SUCCESS;
    }
    case ICAP_PIXELTYPE:
    {
        static const TW_UINT16 all[] = { 0, 1, 2 };
        if (one) { cap->ConType = TWON_ONEVALUE; cap->hContainer = one_value(TWTY_UINT16, g_pixelType); }
        else { cap->ConType = TWON_ENUMERATION; cap->hContainer = enum_u16(all, 3, g_pixelType); }
        return TWRC_SUCCESS;
    }
    case ICAP_UNITS:
        cap->ConType = TWON_ONEVALUE; cap->hContainer = one_value(TWTY_UINT16, 0); return TWRC_SUCCESS;
    case ICAP_XRESOLUTION:
    case ICAP_YRESOLUTION:
        if (one) { cap->ConType = TWON_ONEVALUE; cap->hContainer = one_value(TWTY_FIX32, fix32(g_dpi)); }
        else { cap->ConType = TWON_ENUMERATION; cap->hContainer = enum_resolutions(); }
        return TWRC_SUCCESS;
    case CAP_XFERCOUNT:
        cap->ConType = TWON_ONEVALUE; cap->hContainer = one_value(TWTY_INT16, (TW_UINT32)(TW_UINT16)(TW_INT16)g_xferCount); return TWRC_SUCCESS;
    case CAP_INDICATORS:
        cap->ConType = TWON_ONEVALUE; cap->hContainer = one_value(TWTY_BOOL, g_indicators); return TWRC_SUCCESS;
    }
    return fail(TWCC_BADCAP);
}

static TW_UINT16 cap_set(TW_CAPABILITY *cap)
{
    if (!g_anyThread && GetCurrentThreadId() != g_openThread)
    {
        logf_("MSG_SET cap 0x%04x from the wrong thread (opened on %lu): TWCC_BUMMER", cap->Cap, g_openThread);
        return fail(TWCC_BUMMER);
    }
    if (g_rejectSets)
    {
        logf_("MSG_SET cap 0x%04x rejected (FAKE_TWAIN_REJECT_SETS)", cap->Cap);
        return fail(TWCC_BUMMER);
    }
    if (cap->ConType != TWON_ONEVALUE || !cap->hContainer) return fail(TWCC_BADVALUE);
    TW_ONEVALUE *v = GlobalLock(cap->hContainer);
    TW_UINT32 item = v->Item;
    GlobalUnlock(cap->hContainer);
    switch (cap->Cap)
    {
    case ICAP_XFERMECH:
        if (item != 0 && item != 2) return fail(TWCC_BADVALUE);
        if (g_memoryOnly && item != 2) return fail(TWCC_BADVALUE);
        g_xferMech = (TW_UINT16)item; return TWRC_SUCCESS;
    case ICAP_PIXELTYPE:
        if (item > 2) return fail(TWCC_BADVALUE);
        g_pixelType = (TW_UINT16)item; return TWRC_SUCCESS;
    case ICAP_UNITS:
        return item == 0 ? TWRC_SUCCESS : fail(TWCC_BADVALUE);
    case ICAP_XRESOLUTION:
    case ICAP_YRESOLUTION:
    {
        TW_FIX32 f; memcpy(&f, &item, 4);
        for (unsigned i = 0; i < sizeof kResolutions / sizeof kResolutions[0]; i++)
            if (kResolutions[i] == f.Whole) { g_dpi = f.Whole; return TWRC_SUCCESS; }
        return fail(TWCC_BADVALUE);
    }
    case CAP_XFERCOUNT:
        g_xferCount = (TW_INT16)(TW_UINT16)item; return TWRC_SUCCESS;
    case CAP_INDICATORS:
        g_indicators = item != 0; return TWRC_SUCCESS;
    }
    return fail(TWCC_BADCAP);
}

/* ---- scanning ------------------------------------------------------------------------------ */

static DWORD WINAPI scan_thread(LPVOID arg)
{
    HWND parent = (HWND)arg;
    Sleep(500);
    if (g_noXferReady) { logf_("scanned, but never signalling (FAKE_TWAIN_NO_XFERREADY)"); return 0; }
    /* PostMessage(NULL) posts to this thread's own queue, which nobody reads: the message is lost. */
    BOOL ok = PostMessageA(parent, g_message, 0, 0);
    logf_("posted transfer-ready to hParent=%p (%s)", (void *)parent, ok ? "ok" : "failed");
    return 0;
}

static HANDLE make_dib(void)
{
    int w = width_px(), h = height_px(), bpp = bits_per_pixel();
    int colors = bpp == 1 ? 2 : bpp == 8 ? 256 : 0;
    int stride = ((w * bpp + 31) / 32) * 4;
    HANDLE hdib = GlobalAlloc(GHND, sizeof(BITMAPINFOHEADER) + colors * 4 + stride * h);
    BITMAPINFOHEADER *bi = GlobalLock(hdib);
    bi->biSize = sizeof *bi;
    bi->biWidth = w;
    bi->biHeight = h;
    bi->biPlanes = 1;
    bi->biBitCount = (WORD)bpp;
    bi->biXPelsPerMeter = bi->biYPelsPerMeter = (LONG)(g_dpi / 0.0254);
    bi->biClrUsed = colors;
    RGBQUAD *palette = (RGBQUAD *)(bi + 1);
    for (int i = 0; i < colors; i++)
    {
        BYTE g = (BYTE)(colors == 2 ? i * 255 : i);
        palette[i].rgbRed = palette[i].rgbGreen = palette[i].rgbBlue = g;
    }
    unsigned char *bits = (unsigned char *)(palette + colors);
    for (int y = 0; y < h; y++)
    {
        unsigned char *row = bits + (h - 1 - y) * stride; /* bottom-up */
        for (int x = 0; x < w; x++)
        {
            if (bpp == 1) { if (bw_bit(x, y)) row[x / 8] |= (unsigned char)(0x80 >> (x % 8)); }
            else if (bpp == 8) row[x] = sample(x, y, 0);
            else { row[x * 3] = sample(x, y, 2); row[x * 3 + 1] = sample(x, y, 1); row[x * 3 + 2] = sample(x, y, 0); }
        }
    }
    GlobalUnlock(hdib);
    return hdib;
}

static int mem_stride(void) { return ((width_px() * bits_per_pixel() + 31) / 32) * 4; }

static TW_UINT16 memory_strip(TW_IMAGEMEMXFER *x)
{
    int w = width_px(), h = height_px(), bpp = bits_per_pixel(), stride = mem_stride();
    if (!x->Memory.TheMem || x->Memory.Length < (TW_UINT32)stride) return fail(TWCC_BADVALUE);
    int rows = (int)(x->Memory.Length / stride);
    if (rows > 64) rows = 64;
    if (rows > h - g_memRow) rows = h - g_memRow;
    unsigned char *dst = (x->Memory.Flags & 16) ? GlobalLock(x->Memory.TheMem) : x->Memory.TheMem;
    memset(dst, 0, rows * stride);
    for (int r = 0; r < rows; r++)
    {
        int y = g_memRow + r;
        unsigned char *row = dst + r * stride; /* top-down, RGB order, padded rows */
        for (int px = 0; px < w; px++)
        {
            if (bpp == 1) { if (bw_bit(px, y)) row[px / 8] |= (unsigned char)(0x80 >> (px % 8)); }
            else if (bpp == 8) row[px] = sample(px, y, 0);
            else { row[px * 3] = sample(px, y, 0); row[px * 3 + 1] = sample(px, y, 1); row[px * 3 + 2] = sample(px, y, 2); }
        }
    }
    if (x->Memory.Flags & 16) GlobalUnlock(x->Memory.TheMem);
    x->Compression = 0;
    x->BytesPerRow = stride;
    x->Columns = w;
    x->Rows = rows;
    x->XOffset = 0;
    x->YOffset = g_memRow;
    x->BytesWritten = rows * stride;
    g_memRow += rows;
    return g_memRow >= h ? TWRC_XFERDONE : TWRC_SUCCESS;
}

/* ---- entry point --------------------------------------------------------------------------- */

__declspec(dllexport) TW_UINT16 WINAPI DS_Entry(TW_IDENTITY *origin, TW_UINT32 dg, TW_UINT16 dat, TW_UINT16 msg, void *data)
{
    (void)origin;
    if (dat == DAT_STATUS)
    {
        TW_STATUS *s = data;
        s->ConditionCode = g_cc;
        s->Data = 0;
        g_cc = TWCC_SUCCESS;
        return TWRC_SUCCESS;
    }
    if (dat != DAT_EVENT) g_cc = TWCC_SUCCESS;
    if (dat != DAT_EVENT && dat != DAT_IDENTITY)
        logf_("DS_Entry dg=%lu dat=0x%04x msg=0x%04x", dg, dat, msg);

    if (dg == DG_CONTROL && dat == DAT_IDENTITY)
    {
        if (msg == MSG_GET) { fill_identity(data); return TWRC_SUCCESS; }
        if (msg == MSG_OPENDS)
        {
            if (g_open) return fail(TWCC_SEQERROR);
            if (!g_log)
            {
                char path[MAX_PATH];
                if (GetEnvironmentVariableA("FAKE_TWAIN_LOG", path, sizeof path)) g_log = fopen(path, "a");
            }
            g_pagesPerScan = env_int("FAKE_TWAIN_PAGES", 1);
            g_noXferReady = env_int("FAKE_TWAIN_NO_XFERREADY", 0);
            g_memoryOnly = env_int("FAKE_TWAIN_MEMORY_ONLY", 0);
            g_anyThread = env_int("FAKE_TWAIN_ANY_THREAD", 0);
            g_rejectSets = env_int("FAKE_TWAIN_REJECT_SETS", 0);
            g_xferMech = g_memoryOnly ? 2 : 0;
            g_pixelType = 2;
            g_dpi = 150;
            g_xferCount = -1;
            g_message = RegisterWindowMessageA("highbridge fake TWAIN source");
            g_openThread = GetCurrentThreadId();
            g_open = TRUE;
            fill_identity(data);
            logf_("MSG_OPENDS");
            return TWRC_SUCCESS;
        }
        if (msg == MSG_CLOSEDS) { logf_("MSG_CLOSEDS"); g_open = g_enabled = FALSE; return TWRC_SUCCESS; }
        return fail(TWCC_BADPROTOCOL);
    }
    if (!g_open) return fail(TWCC_SEQERROR);

    if (dg == DG_CONTROL && dat == DAT_CAPABILITY)
    {
        TW_CAPABILITY *cap = data;
        switch (msg)
        {
        case MSG_GET: case MSG_GETCURRENT: case MSG_GETDEFAULT: return cap_get(cap, msg);
        case MSG_SET:
        {
            if (g_enabled) return fail(TWCC_SEQERROR);
            TW_UINT16 rc = cap_set(cap);
            logf_("MSG_SET cap 0x%04x -> rc %u cc %u", cap->Cap, rc, g_cc);
            return rc;
        }
        case MSG_RESET:
            if (!g_anyThread && GetCurrentThreadId() != g_openThread) return fail(TWCC_BUMMER);
            return cap_get(cap, MSG_GETCURRENT);
        }
        return fail(TWCC_BADPROTOCOL); /* no MSG_QUERYSUPPORT, like TWAIN 1.x sources */
    }
    if (dg == DG_CONTROL && dat == DAT_USERINTERFACE)
    {
        TW_USERINTERFACE *ui = data;
        if (msg == MSG_ENABLEDS)
        {
            g_enabled = TRUE;
            g_parent = ui->hParent;
            g_pending = g_pagesPerScan;
            logf_("MSG_ENABLEDS ShowUI=%u hParent=%p", ui->ShowUI, (void *)ui->hParent);
            CloseHandle(CreateThread(NULL, 0, scan_thread, (LPVOID)ui->hParent, 0, NULL));
            return TWRC_SUCCESS;
        }
        if (msg == MSG_DISABLEDS) { g_enabled = FALSE; return TWRC_SUCCESS; }
        return fail(TWCC_BADPROTOCOL);
    }
    if (dg == DG_CONTROL && dat == DAT_EVENT && msg == MSG_PROCESSEVENT)
    {
        TW_EVENT *ev = data;
        MSG *m = ev->pEvent;
        ev->TWMessage = 0;
        if (g_enabled && m && m->message == g_message)
        {
            logf_("MSG_PROCESSEVENT: transfer ready");
            ev->TWMessage = MSG_XFERREADY;
            return TWRC_DSEVENT;
        }
        return TWRC_NOTDSEVENT;
    }
    if (dg == DG_CONTROL && dat == DAT_XFERGROUP && msg == MSG_GET)
    {
        *(TW_UINT32 *)data = DG_IMAGE;
        return TWRC_SUCCESS;
    }
    if (dg == DG_CONTROL && dat == DAT_PENDINGXFERS)
    {
        TW_PENDINGXFERS *p = data;
        if (msg == MSG_ENDXFER && g_pending > 0) g_pending--;
        else if (msg == MSG_RESETXFERS) g_pending = 0;
        g_memRow = 0;
        p->Count = (TW_UINT16)g_pending;
        p->EOJ = 0;
        return TWRC_SUCCESS;
    }
    if (dg == DG_CONTROL && dat == DAT_SETUPMEMXFER && msg == MSG_GET)
    {
        TW_SETUPMEMXFER *s = data;
        s->MinBufSize = mem_stride();
        s->MaxBufSize = mem_stride() * 64;
        s->Preferred = mem_stride() * 16;
        return TWRC_SUCCESS;
    }
    if (dg == DG_IMAGE && dat == DAT_IMAGEINFO && msg == MSG_GET)
    {
        TW_IMAGEINFO *ii = data;
        memset(ii, 0, sizeof *ii);
        ii->XResolution.Whole = ii->YResolution.Whole = (TW_INT16)g_dpi;
        ii->ImageWidth = width_px();
        ii->ImageLength = height_px();
        ii->SamplesPerPixel = g_pixelType == 2 ? 3 : 1;
        for (int i = 0; i < ii->SamplesPerPixel; i++) ii->BitsPerSample[i] = g_pixelType == 0 ? 1 : 8;
        ii->BitsPerPixel = (TW_INT16)bits_per_pixel();
        ii->PixelType = (TW_INT16)g_pixelType;
        return TWRC_SUCCESS;
    }
    if (dg == DG_IMAGE && dat == DAT_IMAGENATIVEXFER && msg == MSG_GET)
    {
        if (g_xferMech != 0) return fail(TWCC_SEQERROR);
        *(HANDLE *)data = make_dib();
        logf_("native transfer done (%dx%d, %d bpp)", width_px(), height_px(), bits_per_pixel());
        return TWRC_XFERDONE;
    }
    if (dg == DG_IMAGE && dat == DAT_IMAGEMEMXFER && msg == MSG_GET)
    {
        if (g_xferMech != 2) return fail(TWCC_SEQERROR);
        return memory_strip(data);
    }
    return fail(TWCC_BADPROTOCOL);
}
