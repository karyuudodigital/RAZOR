// =====================================================================================
// Plugin.cs
//
// Purpose:
// This file contains the native interop glue and the managed representation of a
// game plugin used by TESLOR.
//
// Components:
// - `Native` (internal static partial): P/Invoke declarations for the Rust/C native
//   library `esplugin`. Provides functions to obtain JSON metadata for a plugin and
//   to test whether two plugin files overlap (conflict).
// - `PluginHandle` (SafeHandle): A SafeHandle wrapper around the native pointer
//   returned by `Native.GetPluginInfo`. Ensures the native memory is freed via
//   `Native.FreeString` and exposes a safe `AsString()` helper to convert the
//   null-terminated UTF-8 C string into a managed `string`.
// - `Plugin` (DataContract, IDisposable): Managed data model that holds deserialized
//   plugin metadata (serialized fields) and runtime-only state used by the UI and
//   `Game` logic (e.g., `FilePath`, `IsActive`, `Conflicts`). The constructor calls
//   into the native parser to obtain JSON and then deserializes it into managed fields.
//
// - `Game.LoadPlugins` creates `Plugin` instances for each plugin file found in the
//   game's data directory; these instances supply the information shown in the UI
//   (filename, masters, master flags, override records) and are used for writing
//   plugin lists and for conflict checking.
// - The native parser is intentionally kept outside managed code (Rust/C) for
//   robustness and performance. Managed code uses the `PluginHandle` to safely
//   transfer ownership of native memory and to avoid leaks.
//
// - Memory ownership: `GetPluginInfo` returns a pointer to a heap buffer owned by the
//   native side. `PluginHandle` owns that pointer and calls `FreeString` to release it.
//   Using `SafeHandle` gives reliable cleanup even when exceptions occur.
// - Encoding: `PluginHandle.AsString()` decodes the native buffer using UTF-8. The code
//   then creates a MemoryStream using `Encoding.Unicode.GetBytes(...)` to feed the JSON
//   deserializer; in general, JSON is UTF-8 — prefer `Encoding.UTF8` when producing a
//   byte stream for a JSON serializer to avoid encoding surprises.
// - Threading: plugin parsing occurs on background threads (see `Game.LoadPlugins`).
//   The constructor and deserialization are safe off the UI thread because they only
//   populate managed fields; any UI-bound collections are updated via the Dispatcher.
// - IDisposable: `Plugin` holds native resources via `PluginJson`; callers should call
//   `Dispose()` (or use `using`) when the native data is no longer needed.
//
// =====================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;


namespace SimpleLoadOrderOrganizer
{
    /// <summary>
    /// Declares P/Invoke signatures for the native Rust/C library `esplugin`.
    /// - `GetPluginInfo` returns an allocated C string (UTF-8) containing JSON.
    /// - `FreeString` releases memory allocated by the native side.
    /// - `DoesOverlap` returns whether two plugin files overlap (used for conflict checks).
    /// Ownership of returned pointers is the caller's responsibility; use <see cref="PluginHandle"/>.
    /// </summary>
    internal static partial class Native
    {
        private const string DllName = "esplugin";

        // GetPluginInfo: returns an allocated C string we must free manually
        [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr GetPluginInfo(string path, int game);

        // FreeString: frees memory allocated by Rust
        [LibraryImport(DllName)]
        public static partial void FreeString(IntPtr ptr);

        // DoesOverlap: returns bool
        [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool DoesOverlap(int game, string pluginOne, string pluginTwo);
    }


    /// <summary>
    /// SafeHandle wrapper for a native, null-terminated UTF-8 string returned by `Native.GetPluginInfo`.
    /// - Guarantees `Native.FreeString` is called in <see cref="ReleaseHandle"/>.
    /// - Provides <see cref="AsString"/> to safely read the managed string.
    /// </summary>
    internal class PluginHandle : SafeHandle
    {
        // Default constructor for ownership
        public PluginHandle() : base(IntPtr.Zero, true) { }

        // New constructor to wrap an existing pointer
        public PluginHandle(IntPtr handle) : base(IntPtr.Zero, true)
        {
            SetHandle(handle); // protected call is allowed inside derived class
        }

        public override bool IsInvalid { get { return this.handle == IntPtr.Zero; } }

        /// <summary>
        /// Reads a null-terminated UTF-8 C string from the native pointer and returns a managed string.
        /// </summary>
        public string AsString()
        {
            int len = 0;
            while (Marshal.ReadByte(handle, len) != 0) { ++len; }
            byte[] buffer = new byte[len];
            Marshal.Copy(handle, buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer);
        }

        /// <summary>
        /// Release native memory via the native FreeString function.
        /// SafeHandle ensures this is called exactly once.
        /// </summary>
        protected override bool ReleaseHandle()
        {
            if (!this.IsInvalid) { Native.FreeString(handle); }

            return true;
        }
    }


    /// <summary>
    /// Represents plugin metadata returned from native parsing and runtime state used by the UI.
    /// Fields marked with [DataMember] are serialized into the application's config; runtime-only
    /// fields (FilePath, IsActive, Conflicts, DateModified) are populated at runtime.
    /// 
    /// Construction flow:
    /// - Call native parser via `Native.GetPluginInfo` to receive a JSON string pointer.
    /// - Wrap pointer in <see cref="PluginHandle"/>, convert to managed string, and deserialize JSON
    ///   into a temporary Plugin instance.
    /// - Copy required properties out of the temporary instance and release the native buffer.
    /// - If parsing fails in a non-recoverable way, `invalid` is set and callers skip the plugin.
    /// </summary>
    [DataContract]
    public class Plugin : IDisposable
    {

        [DataMember(Name = "overriderecords")]
        public int OverrideRecords { get; set; }


        [DataMember(Name = "ismaster")]
        public bool IsMaster { get; set; }


        [DataMember(Name = "islightmaster")]
        public bool IsLight { get; set; }


        [DataMember(Name = "masters")]
        public List<string>? Masters { get; set; }


        [DataMember(Name = "filename")]
        public string? PluginFilename { get; set; }

        // Runtime: full file path on disk (not serialized)
        public string? FilePath { get; set; }


        // Runtime: whether plugin is enabled (persisted separately via game config files)
        public bool IsActive { get; set; }

        // Native JSON handle - must be disposed to free native memory
        private readonly PluginHandle PluginJson;


        // Runtime/UI helpers
        public string? MastersString { get; set; }

        public DateTime DateModified { get; set; }
            

        public string? Conflicts { get; set; }
        

        // Flag set when parsing fails or the plugin JSON is missing required fields
        public bool invalid = false;

        /// <summary>
        /// Construct a Plugin by calling the native parser and deserializing JSON into managed fields.
        /// This constructor performs native interop and JSON deserialization and is intended to run
        /// on a background thread (see Game.LoadPlugins).
        /// </summary>
        /// 

        //DEFAULT CONSTRCUTOR FOR TESTING PURPOSES ONLY

        public Plugin()
        {
            this.Masters = [];
            this.IsMaster = true;
            this.IsLight = false;
            this.OverrideRecords = 400;
            this.PluginFilename = "testing";
        }

        public Plugin(string path, Int32 game) {


            PluginJson = new PluginHandle(Native.GetPluginInfo(path, game));


            using var memoryStream = new MemoryStream(Encoding.Unicode.GetBytes(PluginJson.AsString()));
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(Plugin));

                var obj = serializer.ReadObject(memoryStream);
                if (obj is Plugin temp &&
                    temp.Masters != null &&
                    temp.PluginFilename != null){
                    this.Masters = temp.Masters;
                    this.IsMaster = temp.IsMaster;
                    this.IsLight = temp.IsLight;
                    this.OverrideRecords = temp.OverrideRecords;
                    this.PluginFilename = temp.PluginFilename;
                }
                else{
                    throw new InvalidOperationException("Deserialized Plugin is missing required properties.");
                }


            }
            catch (Exception ex) {

                if (ex is InvalidOperationException) {
                    this.invalid = true;
                }
                else { MessageBox.Show(ex.Message); }
                    
            
            
            }

        }

        

        /// <summary>
        /// Dispose the native handle. After disposing, the plugin retains managed state (filename,
        /// masters, counts) but native resources are released. Callers should dispose once native
        /// ownership is no longer required.
        /// </summary>
        public void Dispose() { PluginJson.Dispose(); GC.SuppressFinalize(this); }


      


    }
}
