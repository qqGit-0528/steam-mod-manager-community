using static Ck3ModManager.Core.AppText;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Ck3ModManager.Core;

namespace Ck3ModManager.Infrastructure;

/// <summary>只在桥接子进程加载游戏随附的官方库，直接查询本机 Steam。</summary>
internal sealed class NativeSteamClientApi : ISteamClientApi
{
    private readonly IntPtr _library;
    private IntPtr _ugc;
    private int _pipe;
    private bool _initialized;
    private readonly NoArgsVoid _shutdown;
    private readonly NoArgsBool _running;
    private readonly PipeVoid _runFrame;
    private readonly NextCallback _next;
    private readonly PipeVoid _free;
    private readonly ApiCallResult _callResult;
    private readonly ItemState _state;
    private readonly Subscribe _subscribe;
    private readonly Download _download;
    private readonly DownloadInfo _progress;
    private readonly InstallInfo _installed;
    private const int SubscribeCallback = 1313;
    private const int UnsubscribeCallback = 1315;
    private ulong _subscriptionCall;
    private int _subscriptionCallback;

    internal NativeSteamClientApi(string library, uint appId)
    {
        _library = NativeLibrary.Load(Path.GetFullPath(library));
        try
        {
            var init = Bind<NoArgsBool>("SteamAPI_Init");
            _shutdown = Bind<NoArgsVoid>("SteamAPI_Shutdown");
            _running = Bind<NoArgsBool>("SteamAPI_IsSteamRunning");
            _runFrame = Bind<PipeVoid>("SteamAPI_ManualDispatch_RunFrame");
            _next = Bind<NextCallback>("SteamAPI_ManualDispatch_GetNextCallback");
            _free = Bind<PipeVoid>("SteamAPI_ManualDispatch_FreeLastCallback");
            _callResult = Bind<ApiCallResult>("SteamAPI_ManualDispatch_GetAPICallResult");
            _state = Bind<ItemState>("SteamAPI_ISteamUGC_GetItemState");
            _subscribe = Bind<Subscribe>("SteamAPI_ISteamUGC_SubscribeItem");
            _download = Bind<Download>("SteamAPI_ISteamUGC_DownloadItem");
            _progress = Bind<DownloadInfo>("SteamAPI_ISteamUGC_GetItemDownloadInfo");
            _installed = Bind<InstallInfo>("SteamAPI_ISteamUGC_GetItemInstallInfo");
            if (!init()) throw new IOException(T("CouldNotConnectToSteamOpenThe"));
            _initialized = true;
            var utils = Interface("SteamAPI_SteamUtils_v", 10, 8);
            AppId = Bind<UtilsAppId>("SteamAPI_ISteamUtils_GetAppID")(utils);
            if (AppId != appId) throw new InvalidDataException(T("SteamReturnedADifferentGameContextThe"));
            _ugc = Interface("SteamAPI_SteamUGC_v", 25, 12);
            Bind<NoArgsVoid>("SteamAPI_ManualDispatch_Init")();
            _pipe = Bind<NoArgsInt>("SteamAPI_GetHSteamPipe")();
            if (_pipe == 0) throw new IOException(T("SteamClientCommunicationIsNotReady"));
        }
        catch
        {
            if (_initialized) _shutdown!();
            NativeLibrary.Free(_library);
            throw;
        }
    }

    public uint AppId { get; }
    public bool IsRunning => _running();
    public uint GetItemState(ulong item) => _state(_ugc, item);
    public bool SubscribeItem(ulong item) => TrackSubscriptionCall(_subscribe(_ugc, item), SubscribeCallback);
    public bool UnsubscribeItem(ulong item) =>
        TrackSubscriptionCall(Bind<Subscribe>("SteamAPI_ISteamUGC_UnsubscribeItem")(_ugc, item), UnsubscribeCallback);

    private bool TrackSubscriptionCall(ulong call, int callback)
    {
        _subscriptionCall = call;
        _subscriptionCallback = callback;
        return call != 0;
    }
    public bool DownloadItem(ulong item) => _download(_ugc, item, false);

    public SteamClientProgress? GetProgress(ulong item)
    {
        if (!_progress(_ugc, item, out var downloaded, out var total)) return null;
        return new SteamClientProgress(checked((long)downloaded), total > 0 ? checked((long)total) : null);
    }

    public SteamClientInstalled? GetInstalled(ulong item)
    {
        var buffer = new byte[32768];
        if (!_installed(_ugc, item, out var size, buffer, (uint)buffer.Length, out _)) return null;
        var end = Array.IndexOf(buffer, (byte)0);
        if (end <= 0) throw new InvalidDataException(T("SteamDidNotReturnAValidInstallation"));
        return new SteamClientInstalled(Encoding.UTF8.GetString(buffer, 0, end), checked((long)size));
    }

    public IReadOnlyList<SteamClientResult> RunFrame()
    {
        var results = new List<SteamClientResult>();
        _runFrame(_pipe);
        // CallbackMsg_t 的前四个字段偏移在 4/8 字节 packing 下相同；留足最大结构空间。
        var message = Marshal.AllocHGlobal(32);
        try
        {
            for (var count = 0; count < 512 && _next(_pipe, message); count++)
            {
                try
                {
                    var type = Marshal.ReadInt32(message, 4);
                    if (type == 704) throw new IOException(T("SteamIsClosingAndDownloadTrackingEnded"));
                    if (type == 117) throw new IOException(T("CommunicationWithSteamFailedReopenTheClient"));
                    if (type is not (3406 or 703)) continue;
                    var pointer = Marshal.ReadIntPtr(message, 8);
                    var length = Marshal.ReadInt32(message, 8 + IntPtr.Size);
                    if (pointer == IntPtr.Zero || length is < 0 or > 65536) throw new InvalidDataException(T("TheSteamCallbackSizeIsInvalid"));
                    var data = new byte[length]; Marshal.Copy(pointer, data, 0, length);
                    if (type == 3406) results.Add(DecodeDownload(data));
                    else if (type == 703 && length == 16 && _subscriptionCall != 0)
                    {
                        var handle = BinaryPrimitives.ReadUInt64LittleEndian(data);
                        if (handle != _subscriptionCall) continue;
                        var expected = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
                        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
                        if (expected != _subscriptionCallback || size is not (12 or 16)) throw new InvalidDataException(T("TheSteamSubscriptionCallbackFormatIsUnsupported"));
                        var payload = Marshal.AllocHGlobal((int)size);
                        try
                        {
                            if (!_callResult(_pipe, handle, payload, (int)size, expected, out var failed) || failed)
                                throw new IOException(T("SteamDidNotConfirmTheSubscriptionChange"));
                            var bytes = new byte[size]; Marshal.Copy(payload, bytes, 0, (int)size);
                            var result = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                            var id = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(size == 12 ? 4 : 8));
                            results.Add(new SteamClientResult(expected == UnsubscribeCallback ? "unsubscription" : "subscription", AppId, id, result));
                            _subscriptionCall = 0;
                        }
                        finally { Marshal.FreeHGlobal(payload); }
                    }
                }
                finally { _free(_pipe); }
            }
        }
        finally { Marshal.FreeHGlobal(message); }
        return results;
    }

    internal static SteamClientResult DecodeDownload(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is not (16 or 24)) throw new InvalidDataException(T("TheSteamDownloadCallbackFormatIsUnsupported"));
        var wide = bytes.Length == 24;
        return new SteamClientResult("download", BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[(wide ? 8 : 4)..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[(wide ? 16 : 12)..]));
    }

    private T Bind<T>(string name) where T : Delegate => NativeLibrary.TryGetExport(_library, name, out var pointer)
        ? Marshal.GetDelegateForFunctionPointer<T>(pointer) : throw new NotSupportedException(AppText.T("TheGameSSteamLibraryIsMissing", name));
    private IntPtr Interface(string prefix, int newest, int oldest)
    {
        for (var version = newest; version >= oldest; version--)
            if (NativeLibrary.TryGetExport(_library, prefix + version.ToString("D3"), out var address))
            {
                var result = Marshal.GetDelegateForFunctionPointer<NoArgsPointer>(address)();
                if (result != IntPtr.Zero) return result;
            }
        throw new NotSupportedException(T("TheSteamLibraryVersionShippedWithThe"));
    }

    public void Dispose()
    {
        if (!_initialized) return;
        _initialized = false;
        _shutdown(); NativeLibrary.Free(_library);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NoArgsVoid();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool NoArgsBool();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgsInt();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NoArgsPointer();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PipeVoid(int pipe);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint UtilsAppId(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint ItemState(IntPtr self, ulong item);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong Subscribe(IntPtr self, ulong item);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool Download(IntPtr self, ulong item, [MarshalAs(UnmanagedType.I1)] bool highPriority);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool DownloadInfo(IntPtr self, ulong item, out ulong bytes, out ulong total);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool InstallInfo(IntPtr self, ulong item, out ulong size, [Out] byte[] folder, uint capacity, out uint timestamp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool NextCallback(int pipe, IntPtr message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool ApiCallResult(int pipe, ulong call, IntPtr payload, int size, int expected, [MarshalAs(UnmanagedType.I1)] out bool failed);
}
