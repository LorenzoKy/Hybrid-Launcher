using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using HybridLauncher.Core;
using HybridLauncher.Core.Detection;

namespace HybridLauncher.Helpers;

/// <summary>
/// The taskbar's jump list (right-click on Hybrid Launcher's taskbar button): a "Recent games" list, each one started through
/// Hybrid Launcher like a desktop shortcut (<c>--launch &lt;id&gt;</c>: Smart HDR, play time and Stop work). The app has no
/// package identity, so WinRT's JumpList is not available; the shell's ICustomDestinationList is used through source-generated
/// COM (built-in COM interop is off in the trimmed app). Written off the UI thread; a failure only leaves the old list.
/// </summary>
public static partial class TaskbarJumpList
{
    const int MaxGames = 8;
    static int _busy;

    /// <summary>Rewrites the list from the games played last (at start and after each launch).</summary>
    public static void Update()
    {
        var self = Environment.ProcessPath;
        // Never from test instances, unless a test asks for it (HL_JUMPLIST=1; the repo build has its own taskbar identity).
        if (self is null || (Environment.GetEnvironmentVariable("HL_DATA") is { Length: > 0 } && Environment.GetEnvironmentVariable("HL_JUMPLIST") != "1")) return;
        var games = AppState.Games
            .Where(g => !g.NotInstalled && AppState.Settings.Games.TryGetValue(g.Id, out var s) && s.LastPlayed is not null)
            .OrderByDescending(g => AppState.Settings.Games[g.Id].LastPlayed)
            .Take(MaxGames)
            .Select(g => (g.Id, g.Name, Icon: GameScanner.EffectiveExe(g, AppState.Settings.For(g.Id)) is { } exe && File.Exists(exe) ? exe : self))
            .ToList();
        string category = L.T("Recent games"), play = L.T("Play {0} with Hybrid Launcher");
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try { Write(self, games, category, play); }
            catch (Exception ex) { Log.Warn($"Jump list: {ex.Message}"); }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    static unsafe void Write(string self, List<(string Id, string Name, string Icon)> games, string category, string playFormat)
    {
        var list = Create<ICustomDestinationList>(ClsidDestinationList);
        var iidArray = typeof(IObjectArray).GUID;
        list.BeginList(out _, in iidArray, out var removedPtr);
        if (removedPtr != 0) Marshal.Release(removedPtr);
        if (games.Count == 0)
        {
            list.CommitList();
            return;
        }
        var collection = Create<IObjectCollection>(ClsidObjectCollection);
        foreach (var (id, name, icon) in games)
        {
            var link = Create<IShellLinkW>(ClsidShellLink);
            link.SetPath(self);
            link.SetArguments($"{Shortcuts.LaunchSwitch} \"{id}\"");
            link.SetWorkingDirectory(Path.GetDirectoryName(self)!);
            link.SetIconLocation(icon, 0);
            link.SetDescription(string.Format(playFormat, name));
            // The text shown in the list is the link's Title property.
            var store = (IPropertyStore)link;
            var key = TitleKey;
            var value = new PropVariant { vt = 31, p = Marshal.StringToCoTaskMemUni(name) };
            try { store.SetValue(in key, in value); store.Commit(); }
            finally { Marshal.FreeCoTaskMem(value.p); }
            var unknown = (nint)ComInterfaceMarshaller<IShellLinkW>.ConvertToUnmanaged(link);
            try { collection.AddObject(unknown); }
            finally { Marshal.Release(unknown); }
        }
        list.AppendCategory(category, (IObjectArray)collection);
        list.CommitList();
        Log.Info($"Jump list: {games.Count} recent games");
    }

    static readonly StrategyBasedComWrappers Wrappers = new();
    static readonly Guid ClsidDestinationList = new("77f10cf0-3db5-4966-b520-b7c54fd35ed6");
    static readonly Guid ClsidObjectCollection = new("2d3468c1-36a7-43b6-ac24-d3f02fd9607a");
    static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    static readonly PropertyKey TitleKey = new() { fmtid = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), pid = 2 };

    static T Create<T>(Guid clsid)
    {
        var iid = typeof(T).GUID;
        int hr = CoCreateInstance(in clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, in iid, out var ptr);
        Marshal.ThrowExceptionForHR(hr);
        try { return (T)Wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None); }
        finally { Marshal.Release(ptr); }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint ppv);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant { public ushort vt; public ushort r1, r2, r3; public nint p; public nint p2; }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("6332debf-87b5-4670-90c0-5e57b408a49e")]
    internal partial interface ICustomDestinationList
    {
        void SetAppID(string id);
        void BeginList(out uint minSlots, in Guid riid, out nint removed);
        void AppendCategory(string category, IObjectArray items);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray tasks);
        void CommitList();
        void GetRemovedDestinations(in Guid riid, out nint removed);
        void DeleteList(string id);
        void AbortList();
    }

    [GeneratedComInterface, Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9")]
    internal partial interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, in Guid riid, out nint item);
    }

    [GeneratedComInterface, Guid("5632b1a4-e38a-400a-928a-d4cd63230295")]
    internal partial interface IObjectCollection : IObjectArray
    {
        void AddObject(nint unknown);
        void AddFromArray(IObjectArray source);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16), Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(nint file, int max, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription(nint name, int max);
        void SetDescription(string name);
        void GetWorkingDirectory(nint dir, int max);
        void SetWorkingDirectory(string dir);
        void GetArguments(nint args, int max);
        void SetArguments(string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation(nint path, int max, out int index);
        void SetIconLocation(string path, int index);
        void SetRelativePath(string path, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath(string file);
    }

    [GeneratedComInterface, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    internal partial interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropVariant value);
        void SetValue(in PropertyKey key, in PropVariant value);
        void Commit();
    }
}
