using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using ImmichSplash.Core;

namespace ImmichSplash.App.Services;

/// <summary>
/// Windows shell bits that need COM: the taskbar jump list (right-click the taskbar button) and shortcut files
/// (the "Send to" menu). Uses source-generated COM, since the app is trimmed without built-in COM support.
/// </summary>
public static partial class WindowsShell
{
    /// <summary>One jump list entry: runs this app with <see cref="Arguments"/>.</summary>
    public sealed record JumpTask(string Title, string Arguments, string? Description = null);

    /// <summary>Sets the "Tasks" part of the taskbar jump list. Does nothing outside Windows.</summary>
    public static void SetJumpListTasks(IReadOnlyList<JumpTask> tasks)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
            return;
        try
        {
            var list = Create<ICustomDestinationList>(ClsidDestinationList, typeof(ICustomDestinationList).GUID);
            list.SetAppID(DesktopNotifications.WindowsAppId);
            var objectArray = typeof(IObjectArray).GUID;
            list.BeginList(out _, ref objectArray, out _);
            var collection = Create<IObjectCollection>(ClsidEnumerableObjectCollection, typeof(IObjectCollection).GUID);
            foreach (var task in tasks)
                collection.AddObject(MakeLink(exe, task.Arguments, task.Title, task.Description));
            list.AddUserTasks((IObjectArray)collection);
            list.CommitList();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not set the taskbar jump list", ex);
        }
    }

    /// <summary>Writes a shortcut (.lnk) that runs this app with <paramref name="arguments"/>.</summary>
    public static bool CreateShortcut(string path, string arguments, string description)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
            return false;
        try
        {
            var link = MakeLink(exe, arguments, null, description);
            ((IPersistFile)link).Save(path, true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not create the shortcut {path}", ex);
            return false;
        }
    }

    static IShellLinkW MakeLink(string exe, string arguments, string? title, string? description)
    {
        var link = Create<IShellLinkW>(ClsidShellLink, typeof(IShellLinkW).GUID);
        link.SetPath(exe);
        link.SetArguments(arguments);
        link.SetIconLocation(exe, 0);
        if (description is not null)
            link.SetDescription(description);
        if (title is not null)
        {
            // Jump list entries show the shortcut's title property.
            var store = (IPropertyStore)link;
            var key = new PropertyKey { FormatId = new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), PropertyId = 2 };
            var value = new PropVariant { Type = 31 /* VT_LPWSTR */, Pointer = Marshal.StringToCoTaskMemUni(title) };
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Pointer);
            }
        }
        return link;
    }

    static readonly StrategyBasedComWrappers Wrappers = new();

    static T Create<T>(Guid clsid, Guid iid)
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out var pointer));
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr pointer);

    static readonly Guid ClsidDestinationList = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
    static readonly Guid ClsidEnumerableObjectCollection = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
    static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
    internal partial interface ICustomDestinationList
    {
        void SetAppID(string appId);
        void BeginList(out uint minSlots, ref Guid iid, out IntPtr removed);
        void AppendCategory(string category, IObjectArray items);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray items);
        void CommitList();
        void GetRemovedDestinations(ref Guid iid, out IntPtr removed);
        void DeleteList(string appId);
        void AbortList();
    }

    [GeneratedComInterface]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    internal partial interface IObjectArray
    {
        void GetCount(out uint count);
        void GetAt(uint index, ref Guid iid, out IntPtr item);
    }

    [GeneratedComInterface]
    [Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
    internal partial interface IObjectCollection : IObjectArray
    {
        void AddObject(IShellLinkW item);
        void AddFromArray(IObjectArray items);
        void RemoveObjectAt(uint index);
        void Clear();
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath(IntPtr file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(IntPtr name, int max);
        void SetDescription(string name);
        void GetWorkingDirectory(IntPtr dir, int max);
        void SetWorkingDirectory(string dir);
        void GetArguments(IntPtr args, int max);
        void SetArguments(string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation(IntPtr path, int max, out int index);
        void SetIconLocation(string path, int index);
        void SetRelativePath(string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath(string file);
    }

    [GeneratedComInterface]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load(string file, uint mode);
        void Save(string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted(string file);
        void GetCurFile(out IntPtr file);
    }
}
