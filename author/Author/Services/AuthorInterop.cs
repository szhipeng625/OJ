using System.Runtime.InteropServices;

namespace author.Services;

/// <summary>
/// P/Invoke 调用出题核心 authorcore.dll（C++）。
/// </summary>
public static class AuthorInterop
{
    private const string Dll = "authorcore.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ac_init([MarshalAs(UnmanagedType.LPUTF8Str)] string problems_dir);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_list();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_create(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string? title, [MarshalAs(UnmanagedType.LPUTF8Str)] string? desc, [MarshalAs(UnmanagedType.LPUTF8Str)] string? sampleIn, [MarshalAs(UnmanagedType.LPUTF8Str)] string? sampleOut);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_save_statement(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string? title, [MarshalAs(UnmanagedType.LPUTF8Str)] string? desc, [MarshalAs(UnmanagedType.LPUTF8Str)] string? sampleIn, [MarshalAs(UnmanagedType.LPUTF8Str)] string? sampleOut);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_save_meta(int id, int time_ms, int mem_mb, [MarshalAs(UnmanagedType.LPUTF8Str)] string tags_json);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_get_meta(int id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_get_history(int id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_compile([MarshalAs(UnmanagedType.LPUTF8Str)] string src_file, [MarshalAs(UnmanagedType.LPUTF8Str)] string exe_file);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_outputs(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string std_exe);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_validate(int id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_publish(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string target_root);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_contest_create(int cid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string desc,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string start_time,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string end_time,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string problems_json);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_contest_list();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_contest_get(int cid);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_contest_publish(int cid, [MarshalAs(UnmanagedType.LPUTF8Str)] string target_root);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_last_error();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ac_free_string(IntPtr s);

    // ===== 数据生成器 =====
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ac_gen_set_temp([MarshalAs(UnmanagedType.LPUTF8Str)] string temp_dir);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_get_current(int id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_save(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string code);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_list_versions(int id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_get_version(int id, int version);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_compile(int id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_run(int id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_list_files(int id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_get_file(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_search([MarshalAs(UnmanagedType.LPUTF8Str)] string keyword);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_import_to_problem(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    private static string Take(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUTF8(p) ?? ""; }
        finally { ac_free_string(p); }
    }

    public static void Init(string root) => ac_init(root);
    public static string List() => Take(ac_list());
    public static string Create(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string? title, [MarshalAs(UnmanagedType.LPUTF8Str)] string? desc, string? si, string? so)
        => Take(ac_create(id, title, desc, si, so));
    public static string SaveStatement(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string? title, [MarshalAs(UnmanagedType.LPUTF8Str)] string? desc, string? si, string? so)
        => Take(ac_save_statement(id, title, desc, si, so));
    public static string SaveMeta(int id, int timeMs, int memMb, string tagsJson)
        => Take(ac_save_meta(id, timeMs, memMb, tagsJson));
    public static string GetMeta(int id) => Take(ac_get_meta(id));
    public static string GetHistory(int id) => Take(ac_get_history(id));
    public static string Compile(string src, string exe) => Take(ac_compile(src, exe));
    public static string GenOutputs(int id, string stdExe) => Take(ac_gen_outputs(id, stdExe));
    public static string Validate(int id) => Take(ac_validate(id));
    public static string Publish(int id, string target) => Take(ac_publish(id, target));
    public static string ContestCreate(int cid, string name, string desc, string start, string end, string problemsJson)
        => Take(ac_contest_create(cid, name, desc, start, end, problemsJson));
    public static string ContestList() => Take(ac_contest_list());
    public static string ContestGet(int cid) => Take(ac_contest_get(cid));
    public static string ContestPublish(int cid, string target) => Take(ac_contest_publish(cid, target));
    public static string LastError() => Take(ac_last_error());

    // ===== 数据生成器 =====
    public static void GenSetTemp(string tempDir) => ac_gen_set_temp(tempDir);
    public static string GenGetCurrent(int id) => Take(ac_gen_get_current(id));
    public static string GenSave(int id, string code) => Take(ac_gen_save(id, code));
    public static string GenListVersions(int id) => Take(ac_gen_list_versions(id));
    public static string GenGetVersion(int id, int version) => Take(ac_gen_get_version(id, version));
    public static string GenCompile(int id) => Take(ac_gen_compile(id));
    public static string GenRun(int id) => Take(ac_gen_run(id));
    public static string GenListFiles(int id) => Take(ac_gen_list_files(id));
    public static string GenGetFile(int id, string name) => Take(ac_gen_get_file(id, name));
    public static string GenSearch(string keyword) => Take(ac_gen_search(keyword));
    public static string GenImportToProblem(int id, string name) => Take(ac_gen_import_to_problem(id, name));
}
