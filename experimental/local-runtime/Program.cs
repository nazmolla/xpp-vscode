// XppLocalTestRunner: runs X++ SysTest classes in-process against a local database, the way
// SysTestConsole's LocalRunner does, but with settings from the app config / ECA_ConfigurationFile
// instead of the Visual Studio development configuration.
// Usage: XppLocalTestRunner.exe /test:Class1,Class2 [/xml:result.xml] [/partition:initial] [/diag]
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Reflection;
using Dynamics.AX.Application;
using Microsoft.Dynamics.AX.AosConfig;
using Microsoft.Dynamics.Ax.MSIL;
using Microsoft.Dynamics.Ax.Xpp;

internal static class Program
{
    private sealed class TraceListener : System.Diagnostics.Tracing.EventListener
    {
        private readonly StreamWriter writer = new StreamWriter(@"C:\XppLocalRuntime\runner.events.txt", false) { AutoFlush = true };

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)(-1));
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
        {
            try
            {
                string payload = e.Payload == null ? "" : string.Join(" | ", e.Payload.Select(p => p?.ToString()));
                if (payload.Length > 400) payload = payload.Substring(0, 400);
                lock (writer) writer.WriteLine(e.EventSource.Name + "/" + e.EventName + " [" + e.Level + "] " + payload);
            }
            catch { }
        }
    }

    private static TraceListener traceListener;

    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveFromPackages;
        if (args.Any(a => a.Equals("/trace", StringComparison.OrdinalIgnoreCase)))
            traceListener = new TraceListener();
        // /schema:Table1,Table2: create only the kernel tables and the named tables, without starting the kernel.
        if (args.Length > 0 && args[0].StartsWith("/schema", StringComparison.OrdinalIgnoreCase))
            return SchemaSync.Run(args);
        // /exec:<tool.exe> args...: run another D365 tool (e.g. SyncEngine.exe) with the same assembly resolution.
        if (args.Length > 0 && args[0].StartsWith("/exec:", StringComparison.OrdinalIgnoreCase))
        {
            var entry = Assembly.LoadFrom(args[0].Substring(6)).EntryPoint;
            object result = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : new object[] { args.Skip(1).ToArray() });
            return result is int code ? code : 0;
        }
        if (args.Any(a => a.Equals("/diag", StringComparison.OrdinalIgnoreCase)))
        {
            // The kernel fails fast on some managed errors; log them before the process dies.
            var seen = new HashSet<string>();
            bool inHandler = false;
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                if (inHandler) return;
                inHandler = true;
                try
                {
                    string key = e.Exception.GetType().FullName + ": " + e.Exception.Message.Replace("\r", " ").Replace("\n", " ");
                    if (!seen.Add(key)) return;
                    Console.Out.WriteLine("FIRSTCHANCE " + key);
                    Console.Out.WriteLine("    at " + string.Join("\n    at ", new System.Diagnostics.StackTrace(1, false).GetFrames()
                        .Select(f => f.GetMethod()).Where(m => m != null).Take(45).Select(m => m.DeclaringType?.FullName + "." + m.Name)));
                    Console.Out.Flush();
                }
                finally { inHandler = false; }
            };
        }
        return Run(args);
    }

    // A UDE package download has no AOS webroot\bin; runtime dependencies (System.ValueTuple, ...) live in
    // bin\ModelUtilDlls, package bin folders and Plugins instead.
    private static Dictionary<string, List<string>> assemblyIndex;

    private static Assembly ResolveFromPackages(object sender, ResolveEventArgs e)
    {
        var name = new AssemblyName(e.Name);
        if (assemblyIndex == null)
        {
            assemblyIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string root = ConfigurationManager.AppSettings["Common.BinDir"] ?? Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            var folders = new List<string> { Path.Combine(root, "bin"), Path.Combine(root, "bin", "ModelUtilDlls") };
            folders.AddRange(Directory.GetDirectories(root).Select(d => Path.Combine(d, "bin")));
            if (Directory.Exists(Path.Combine(root, "Plugins"))) folders.AddRange(Directory.GetDirectories(Path.Combine(root, "Plugins")));
            foreach (string folder in folders.Where(Directory.Exists))
                foreach (string file in Directory.EnumerateFiles(folder, "*.dll"))
                {
                    string key = Path.GetFileNameWithoutExtension(file);
                    if (!assemblyIndex.TryGetValue(key, out var list)) assemblyIndex[key] = list = new List<string>();
                    list.Add(file);
                }
        }
        if (!assemblyIndex.TryGetValue(name.Name, out var candidates)) return null;
        string best = null;
        Version bestVersion = null;
        foreach (string file in candidates)
        {
            Version version;
            try { version = AssemblyName.GetAssemblyName(file).Version; } catch { continue; }
            if (bestVersion == null || version > bestVersion) { best = file; bestVersion = version; }
        }
        if (best == null || (name.Version != null && bestVersion < name.Version)) return null;
        return Assembly.LoadFrom(best);
    }

    private static int Run(string[] args)
    {
        var options = args.Where(a => a.StartsWith("/")).Select(a => a.Substring(1).Split(new[] { ':' }, 2))
            .ToDictionary(p => p[0].ToLowerInvariant(), p => p.Length > 1 ? p[1] : "true");
        string tests = Get(options, "test");
        string output = Get(options, "xml") ?? Path.Combine(Path.GetTempPath(), "XppLocalTestResult.xml");
        string partition = Get(options, "partition") ?? "initial";
        bool diag = options.ContainsKey("diag");

        if (string.IsNullOrEmpty(tests))
        {
            Console.Error.WriteLine("Pass /test:Class1,Class2");
            return 2;
        }

        SubscribeToInitializationErrors();

        Step("Config.Init", diag, () => Config.Init());
        // The AOS host registers its AppDomain; run-as sessions (XppSession.Start) are created through it.
        typeof(Microsoft.Dynamics.Ax.MSIL.Interop).Assembly.GetType("Microsoft.Dynamics.Ax.Services.AOSAppDomainHelper", true)
            .GetProperty("AOSAppDomain", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .SetValue(null, AppDomain.CurrentDomain);
        Step("ReflectionCallHelper.Start", diag, () => ReflectionCallHelper.Start());

        IntPtr session = IntPtr.Zero;
        Step("CreateTmpSession", diag, () => session = Interop.CreateTmpSession());
        if (session == IntPtr.Zero)
        {
            Console.Error.WriteLine("Unable to create a temporary session.");
            return 3;
        }
        try
        {
            Step("CreateManagedApplicationObject", diag, () => Interop.CreateManagedApplicationObject(session));
            Step("CallSessionStartupCallback", diag, () => Interop.CallSessionStartupCallback(session));
            if (options.ContainsKey("probe"))
            {
                Try("DataArea::exist('dat')", () => typeof(DataArea).GetMethods()
                    .First(m => string.Equals(m.Name, "exist", StringComparison.OrdinalIgnoreCase) && m.IsStatic && m.GetParameters().Length == 1)
                    .Invoke(null, new object[] { "dat" }));
                Try("QueryRun SysClientPerf/SysLastValue", () =>
                {
                    Type Find(string name) => AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).First(t => t.FullName == name);
                    object Call(object target, string name, params object[] a) => target.GetType().GetMethods()
                        .First(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == a.Length).Invoke(target, a);
                    string result = "";
                    foreach (int tableId in new[] { 13642, 65528 })
                    {
                        object query = Activator.CreateInstance(Find("Dynamics.AX.Application.Query"));
                        Call(query, "addDataSource", tableId);
                        var queryRunType = Find("Dynamics.AX.Application.QueryRun");
                        object queryRun = queryRunType.GetConstructors().First(c => c.GetParameters().Length == 1).Invoke(new[] { query });
                        result += " table " + tableId + " next=" + Call(queryRun, "next");
                    }
                    return result;
                });
                foreach (int tableId in new[] { 13642, 65528 })
                {
                    Try("DictTable " + tableId, () =>
                    {
                        object dict = new DictTable(tableId);
                        object Call(object target, string name, params object[] a) => target.GetType().GetMethods()
                            .First(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase) && m.GetParameters().Length == a.Length).Invoke(target, a);
                        object key = Call(dict, "configurationKeyId");
                        return "name=" + Call(dict, "name") + " enabled=" + Call(dict, "enabled") + " configKey=" + key;
                    });
                }
            }
            string xml = null;
            Step("RunTests", diag, () =>
            {
                using (options.ContainsKey("nosession") ? null : XppSession.StartInPartition(partition))
                {
                    var list = new SysTestExecutionList();
                    list.groupName("XppLocalTestRunner");
                    var classes = new List(Types.String);
                    foreach (string name in tests.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0))
                        classes.addEnd(name);
                    list.testClasses(classes);

                    var settings = new SysTestRunnerSettings();
                    settings.createPartition(false);
                    xml = new SysTestService().runTests(list, settings).resultXml();
                }
            });
            System.IO.File.WriteAllText(output, xml ?? "");
            Console.WriteLine("RESULT " + output);
            Console.WriteLine(xml);
            return 0;
        }
        finally
        {
            Interop.DestroyTmpSession(session);
        }
    }

    // The kernel reports why startup failed through AOS.ExceptionTriggeredDuringInitialization and then exits.
    private static void SubscribeToInitializationErrors()
    {
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        // Every infolog message (info/warning/error) the kernel adds, including the specific reason behind generic errors.
        var infolog = typeof(Microsoft.Dynamics.Ax.MSIL.Interop).GetEvent("OnInfologMessageAdding", flags);
        if (infolog != null)
            AddPrinter(infolog, "INFOLOG");
        var kernelMessages = typeof(Microsoft.Dynamics.Ax.MSIL.Interop).GetEvent("OnKernelMessageAdding", flags);
        if (kernelMessages != null)
            AddPrinter(kernelMessages, "KERNEL");
        var evt = typeof(Microsoft.Dynamics.AX.AOS).GetEvent("ExceptionTriggeredDuringInitialization", flags);
        if (evt == null) return;
        AddPrinter(evt, null);
    }

    private static void AddPrinter(EventInfo evt, string prefix)
    {
        var invoke = evt.EventHandlerType.GetMethod("Invoke");
        var parameters = invoke.GetParameters().Select(p => System.Linq.Expressions.Expression.Parameter(p.ParameterType)).ToArray();
        var method = prefix == null
            ? typeof(Program).GetMethod(nameof(OnInitializationError), BindingFlags.NonPublic | BindingFlags.Static)
            : typeof(Program).GetMethod(nameof(OnInfolog), BindingFlags.NonPublic | BindingFlags.Static);
        var body = System.Linq.Expressions.Expression.Call(method,
            System.Linq.Expressions.Expression.NewArrayInit(typeof(object), parameters.Select(p => System.Linq.Expressions.Expression.Convert(p, typeof(object)))));
        var handler = System.Linq.Expressions.Expression.Lambda(evt.EventHandlerType, body, parameters).Compile();
        evt.GetAddMethod(true).Invoke(null, new object[] { handler });
    }

    private static void OnInfolog(object[] args)
    {
        Console.Out.WriteLine("INFOLOG " + string.Join(" | ", args.Select(a => a?.ToString())));
        Console.Out.Flush();
    }

    private static void OnInitializationError(object[] args)
    {
        foreach (object arg in args.Where(a => a != null))
        {
            var type = arg.GetType();
            object message = type.GetProperty("UserFriendlyErrorMessage")?.GetValue(arg);
            object exception = type.GetProperty("Exception")?.GetValue(arg);
            Console.Out.WriteLine("INITERROR " + (message ?? arg) + (exception != null ? "\n" + exception : ""));
        }
        Console.Out.Flush();
    }

    private static string Get(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out string value) ? value : null;

    private static void Step(string name, bool diag, Action action)
    {
        Console.WriteLine("STEP " + name);
        try
        {
            action();
        }
        catch (System.Exception ex)
        {
            Console.WriteLine("FAILED " + name + ": " + ex);
            Diagnose();
            Environment.Exit(1);
        }
        if (diag) Diagnose();
    }

    private static void Diagnose()
    {
        Try("Common.BinDir setting", () => ConfigurationManager.AppSettings["Common.BinDir"]);
        Try("PackagesBinDirectory", () => MetadataSupport.PackagesBinDirectory);
        Try("XppAssemblies", () => string.Join(", ", ReflectionCallHelper.XppAssemblies.Select(a => a.GetName().Name).Take(15))
            + " (" + ReflectionCallHelper.XppAssemblies.Count() + ")");
        Try("Accessor modules", () =>
        {
            var accessor = AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes)
                .FirstOrDefault(t => t.Name == "Accessor" && t.GetProperty("MetadataProvider", BindingFlags.Public | BindingFlags.Static) != null);
            if (accessor == null) return "Accessor type not found";
            object provider = accessor.GetProperty("MetadataProvider", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            if (provider == null) return accessor.FullName + ".MetadataProvider is null";
            object manifest = provider.GetType().GetProperty("ModelManifest").GetValue(provider);
            var modules = ((System.Collections.IEnumerable)manifest.GetType().GetMethod("ListModules", Type.EmptyTypes).Invoke(manifest, null)).Cast<object>().ToList();
            return accessor.FullName + ": " + modules.Count + " modules, provider " + provider.GetType().FullName;
        });
        Try("ScaleUnitInterop type", () => ReflectionCallHelper.getType("Dynamics.AX.Application.ScaleUnitInterop")?.AssemblyQualifiedName ?? "null");
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        catch { return System.Linq.Enumerable.Empty<Type>(); }
    }

    private static void Try(string label, Func<object> read)
    {
        try { Console.WriteLine("  " + label + ": " + read()); }
        catch (System.Exception ex) { Console.WriteLine("  " + label + ": <" + ex.GetType().Name + ": " + ex.Message + ">"); }
    }
}




