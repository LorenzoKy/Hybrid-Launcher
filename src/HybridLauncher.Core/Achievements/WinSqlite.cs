using System.Runtime.InteropServices;
using System.Text;

namespace HybridLauncher.Core.Achievements;

/// <summary>
/// A small read-only reader of SQLite files through Windows' own SQLite (winsqlite3.dll, part of Windows 10 and 11), so no
/// database library ships with the app. Used for the Xbox app's cache.
/// </summary>
public sealed class WinSqlite : IDisposable
{
    const string Dll = "winsqlite3.dll";
    const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
    const int SQLITE_OPEN_READONLY = 0x1, SQLITE_OPEN_URI = 0x40;

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_open_v2(byte[] filename, out nint db, int flags, nint vfs);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_close_v2(nint db);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_prepare_v2(nint db, byte[] sql, int bytes, out nint stmt, nint tail);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_text(nint stmt, int index, byte[] text, int bytes, nint destructor);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_step(nint stmt);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern nint sqlite3_column_text(nint stmt, int col);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_bytes(nint stmt, int col);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_finalize(nint stmt);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_busy_timeout(nint db, int ms);
    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern nint sqlite3_errmsg(nint db);

    nint _db;

    WinSqlite(nint db) => _db = db;

    static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    /// <summary>Opens the file read-only (throws <see cref="IOException"/> when SQLite cannot).</summary>
    public static WinSqlite Open(string path)
    {
        var uri = "file:" + Uri.EscapeDataString(path.Replace('\\', '/')).Replace("%2F", "/").Replace("%3A", ":") + "?mode=ro";
        int rc = sqlite3_open_v2(Z(uri), out var db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, 0);
        if (rc != SQLITE_OK)
        {
            var msg = db != 0 ? Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) : null;
            if (db != 0) sqlite3_close_v2(db);
            throw new IOException($"SQLite open {rc}: {msg}");
        }
        sqlite3_busy_timeout(db, 1500);
        return new WinSqlite(db);
    }

    /// <summary>Every row of a query, <paramref name="columns"/> text columns each; parameters bound as ?1, ?2...</summary>
    public List<string?[]> Query(string sql, int columns, params string[] args)
    {
        var rows = new List<string?[]>();
        if (sqlite3_prepare_v2(_db, Z(sql), -1, out var stmt, 0) != SQLITE_OK)
            throw new IOException("SQLite: " + Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)));
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(args[i]);
                sqlite3_bind_text(stmt, i + 1, bytes, bytes.Length, -1 /* SQLITE_TRANSIENT */);
            }
            while (true)
            {
                int rc = sqlite3_step(stmt);
                if (rc == SQLITE_DONE) break;
                if (rc != SQLITE_ROW) throw new IOException("SQLite step: " + Marshal.PtrToStringUTF8(sqlite3_errmsg(_db)));
                var row = new string?[columns];
                for (int c = 0; c < columns; c++)
                {
                    var p = sqlite3_column_text(stmt, c);
                    row[c] = p == 0 ? null : Marshal.PtrToStringUTF8(p, sqlite3_column_bytes(stmt, c));
                }
                rows.Add(row);
            }
        }
        finally { sqlite3_finalize(stmt); }
        return rows;
    }

    public string? Scalar(string sql, params string[] args) => Query(sql, 1, args) is { Count: > 0 } r ? r[0][0] : null;

    public void Dispose()
    {
        if (_db != 0) sqlite3_close_v2(_db);
        _db = 0;
    }
}
