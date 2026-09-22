using System.Runtime.InteropServices;

namespace author.DataAccess.Interop;

/// <summary>
/// authorcore.dll（C++ 出题核心）的原生 P/Invoke 声明。
/// 仅做封送，不做业务判断；解析与编排在 DataAccess 客户端 / Business 服务层。
/// 跨题并发说明：C++ 端 g_root/g_temp 初始化后只读，g_lastError 为 thread_local，
/// 不同题号使用各自独立的题目目录与 temp 目录，可安全并发调用。
/// </summary>
public static class AuthorCoreInterop
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

// ===== 数据生成器（多生成器模型，均带 name 参数） =====
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ac_gen_set_temp([MarshalAs(UnmanagedType.LPUTF8Str)] string temp_dir);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_list(int id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_create(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string desc);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_get_current(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_set_desc(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string desc);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_save(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string code);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_compile(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_run(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int n);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_list_files(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_get_file(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string file);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_search([MarshalAs(UnmanagedType.LPUTF8Str)] string keyword);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_import_to_problem(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string filename);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_test_compile(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string code);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_test_run(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int n);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_test_files(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_test_file(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string file);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_get_used(int id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ac_gen_set_used(int id, [MarshalAs(UnmanagedType.LPUTF8Str)] string names_json);


    private static string Take(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUTF8(p) ?? ""; }
        finally { ac_free_string(p); }
    }

    public static void Init(string root) => ac_init(root);
    public static string List() => Take(ac_list());
    public static string Create(int id, string? title, string? desc, string? si, string? so)
        => Take(ac_create(id, title, desc, si, so));
    public static string SaveStatement(int id, string? title, string? desc, string? si, string? so)
        => Take(ac_save_statement(id, title, desc, si, so));
    public static string SaveMeta(int id, int timeMs, int memMb, string tagsJson)
        => Take(ac_save_meta(id, timeMs, memMb, tagsJson));
    public static string GetMeta(int id) => Take(ac_get_meta(id));
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

public static void GenSetTemp(string tempDir) => ac_gen_set_temp(tempDir);
    public static string GenList(int id) => Take(ac_gen_list(id));
    public static string GenCreate(int id, string name, string desc) => Take(ac_gen_create(id, name, desc));
    public static string GenGetCurrent(int id, string name) => Take(ac_gen_get_current(id, name));
    public static string GenSetDesc(int id, string name, string desc) => Take(ac_gen_set_desc(id, name, desc));
    public static string GenSave(int id, string name, string code) => Take(ac_gen_save(id, name, code));
    public static string GenCompile(int id, string name) => Take(ac_gen_compile(id, name));
    public static string GenRun(int id, string name, int n) => Take(ac_gen_run(id, name, n));
    public static string GenListFiles(int id, string name) => Take(ac_gen_list_files(id, name));
    public static string GenGetFile(int id, string name, string file) => Take(ac_gen_get_file(id, name, file));
    public static string GenSearch(string keyword) => Take(ac_gen_search(keyword));
    public static string GenImportToProblem(int id, string name, string filename) => Take(ac_gen_import_to_problem(id, name, filename));
    public static string GenTestCompile(int id, string name, string code) => Take(ac_gen_test_compile(id, name, code));
    public static string GenTestRun(int id, string name, int n) => Take(ac_gen_test_run(id, name, n));
    public static string GenTestFiles(int id, string name) => Take(ac_gen_test_files(id, name));
    public static string GenTestFile(int id, string name, string file) => Take(ac_gen_test_file(id, name, file));
    public static string GenGetUsed(int id) => Take(ac_gen_get_used(id));
    public static string GenSetUsed(int id, string namesJson) => Take(ac_gen_set_used(id, namesJson));
}
