// Decision 0012: compiles C# scripts. A script is a method body (statements, local functions, `return`, `await`) with optional `using`
// directives at the top; `#r`, `#load` and type declarations are refused. It is parsed as a script to find those, then compiled as a regular
// library: the usings are hoisted, and the body becomes the async RunAsync method of a generated class whose properties forward to the
// globals object, so `Args`, `Data` and the rest read as in Roslyn scripting. `#line` directives map every diagnostic and stack frame back to
// the script's own path and lines. Output is deterministic with a portable PDB; each compiled script loads into its own collectible load
// context whose Load returns null, so the host's types are shared, and unloads once its running calls drain. Compiled images are cached in
// memory, and optionally on disk, by the SHA-256 of everything that shapes them (code, path, globals and result types and their assembly
// versions, imports, language version, references, Roslyn's version). Scripts are full trust: a load context is not a sandbox; pin sources
// with TrustedSourceHashes. Globals and result types must be public (generated code lives in another assembly). Single-file apps have no
// assembly paths to reference; such hosts pass ReferencePaths.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace PiSharp.CustomAgents.Scripting;

/// <summary>Options of a <see cref="CSharpScriptHost"/>.</summary>
public sealed record CSharpScriptOptions
{
    /// <summary>Namespaces every script imports. Default: System, System.Collections.Generic, System.Linq, System.Text, System.Text.Json,
    /// System.Threading, System.Threading.Tasks, PiSharp.CustomAgents.Tools and PiSharp.CustomAgents.Workflows.</summary>
    public IReadOnlyList<string> Imports { get; init; } =
    [
        "System", "System.Collections.Generic", "System.Linq", "System.Text", "System.Text.Json", "System.Threading", "System.Threading.Tasks",
        "PiSharp.CustomAgents.Tools", "PiSharp.CustomAgents.Workflows",
    ];
    /// <summary>Assemblies scripts may use, besides the host's (<see cref="UseHostReferences"/>) and those of the globals and result types.</summary>
    public IReadOnlyList<Assembly> Assemblies { get; init; } = [];
    /// <summary>Assembly files scripts may use.</summary>
    public IReadOnlyList<string> ReferencePaths { get; init; } = [];
    /// <summary>Whether scripts may use every assembly the host can load (its trusted platform assemblies); default true.</summary>
    public bool UseHostReferences { get; init; } = true;
    /// <summary>The C# version; default the compiler's latest.</summary>
    public LanguageVersion LanguageVersion { get; init; } = LanguageVersion.Latest;
    /// <summary>When set, only scripts whose SHA-256 (<see cref="CSharpScriptHost.HashOf"/>) is listed compile.</summary>
    public IReadOnlyCollection<string>? TrustedSourceHashes { get; init; }
    /// <summary>A directory to keep compiled images in across processes; default memory only.</summary>
    public string? CacheDirectory { get; init; }
}

/// <summary>One compiler message, at the script's own path and 1-based line and column.</summary>
public sealed record ScriptDiagnostic(string Id, DiagnosticSeverity Severity, string Message, string Path, int Line, int Column)
{
    /// <inheritdoc/>
    public override string ToString() => $"{Path}({Line},{Column}): {Severity.ToString().ToLowerInvariant()} {Id}: {Message}";
}

/// <summary>A script did not compile.</summary>
public sealed class ScriptCompilationException(IReadOnlyList<ScriptDiagnostic> diagnostics)
    : Exception("The script does not compile:\n" + string.Join("\n", diagnostics))
{
    /// <summary>The errors.</summary>
    public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>A script's hash is not among <see cref="CSharpScriptOptions.TrustedSourceHashes"/>.</summary>
public sealed class ScriptTrustException(string message) : Exception(message);

/// <summary>Compiles C# scripts into collectible assemblies.</summary>
public sealed class CSharpScriptHost
{
    private const string GeneratedNamespace = "PiSharp.CustomAgents.Scripting.Generated";
    private const string GeneratedClass = "Script";
    private static readonly Lazy<CSharpScriptHost> shared = new(() => new CSharpScriptHost());
    private static readonly Lazy<ImmutableArray<string>> hostReferences = new(() =>
        [.. ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)]);
    private static readonly ConcurrentDictionary<string, MetadataReference> references = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Image> images = new(StringComparer.Ordinal);
    private int compilations;

    /// <summary>A host with the default options.</summary>
    public static CSharpScriptHost Shared => shared.Value;

    /// <summary>Creates a host.</summary>
    public CSharpScriptHost(CSharpScriptOptions? options = null) => Options = options ?? new CSharpScriptOptions();

    /// <summary>The options.</summary>
    public CSharpScriptOptions Options { get; }
    /// <summary>How many scripts this host compiled (cache hits excluded).</summary>
    public int Compilations => Volatile.Read(ref compilations);

    /// <summary>The SHA-256 of <paramref name="code"/> (UTF-8), lowercase hex: what <see cref="CSharpScriptOptions.TrustedSourceHashes"/> lists.</summary>
    public static string HashOf(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
    }

    /// <summary>Compiles <paramref name="code"/> to run over <typeparamref name="TGlobals"/> and return <typeparamref name="TResult"/>.</summary>
    /// <param name="code">The script.</param>
    /// <param name="path">The path diagnostics and stack traces name; default <c>script.csx</c>.</param>
    /// <exception cref="ScriptCompilationException">The script has errors.</exception>
    /// <exception cref="ScriptTrustException">The script is not trusted.</exception>
    public CompiledScript<TGlobals, TResult> Compile<TGlobals, TResult>(string code, string? path = null) where TGlobals : class =>
        Compile<TGlobals, TResult>(code, path, null);

    /// <summary><see cref="Compile{TGlobals, TResult}(string, string?)"/> off the calling thread.</summary>
    public Task<CompiledScript<TGlobals, TResult>> CompileAsync<TGlobals, TResult>(string code, string? path = null,
        CancellationToken cancellationToken = default) where TGlobals : class =>
        Task.Run(() => Compile<TGlobals, TResult>(code, path), cancellationToken);

    /// <summary>Compiles a script; <paramref name="fallback"/> is a statement run when the body ends without returning.</summary>
    internal CompiledScript<TGlobals, TResult> Compile<TGlobals, TResult>(string code, string? path, string? fallback) where TGlobals : class
    {
        ArgumentNullException.ThrowIfNull(code);
        path = string.IsNullOrWhiteSpace(path) ? "script.csx" : path.Replace("\"", "", StringComparison.Ordinal);
        if (Options.TrustedSourceHashes is { } trusted && !trusted.Contains(HashOf(code), StringComparer.OrdinalIgnoreCase))
            throw new ScriptTrustException($"The script '{path}' (SHA-256 {HashOf(code)}) is not among the trusted sources.");
        var referenced = References(typeof(TGlobals), typeof(TResult));
        var key = Key(code, path, fallback, typeof(TGlobals), typeof(TResult), referenced);
        var cached = true;
        var image = images.GetOrAdd(key, _ =>
        {
            if (ReadCache(key) is { } stored) return stored;
            cached = false;
            var built = Emit<TGlobals, TResult>(code, path, fallback, key, referenced);
            Interlocked.Increment(ref compilations);
            WriteCache(key, built);
            return built;
        });
        return new CompiledScript<TGlobals, TResult>(key, path, image, cached);
    }

    private Image Emit<TGlobals, TResult>(string code, string path, string? fallback, string key, ImmutableArray<string> referenced)
    {
        var parse = CSharpParseOptions.Default.WithLanguageVersion(Options.LanguageVersion);
        var script = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(code, parse.WithKind(SourceCodeKind.Script), path).GetRoot();
        var refused = new List<ScriptDiagnostic>();
        foreach (var directive in script.GetReferenceDirectives()) refused.Add(At("PSA0001", "#r is not supported; pass assemblies in CSharpScriptOptions.", directive));
        foreach (var directive in script.GetLoadDirectives()) refused.Add(At("PSA0002", "#load is not supported; one script is one file.", directive));
        foreach (var member in script.Members.Where(member => member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseNamespaceDeclarationSyntax))
            refused.Add(At("PSA0003", "Scripts cannot declare types; declare them in the host or a plugin.", member));
        foreach (var alias in script.Externs) refused.Add(At("PSA0004", "extern alias is not supported.", alias));
        if (refused.Count > 0) throw new ScriptCompilationException(refused);
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(Generate<TGlobals, TResult>(code, path, fallback, script), Encoding.UTF8), parse,
            "<generated " + path + ">");
        var compilation = CSharpCompilation.Create("PiSharp.Script." + key[..16], [tree], referenced.Select(Reference),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, deterministic: true,
                nullableContextOptions: NullableContextOptions.Enable));
        using var pe = new MemoryStream();
        using var pdb = new MemoryStream();
        var result = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!result.Success)
            throw new ScriptCompilationException([.. result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(Map)]);
        return new Image(pe.ToArray(), pdb.ToArray());
    }

    /// <summary>The generated library: hoisted usings, then a class whose properties forward to the globals and whose RunAsync is the body.</summary>
    private string Generate<TGlobals, TResult>(string code, string path, string? fallback, CompilationUnitSyntax script)
    {
        var text = new StringBuilder();
        foreach (var import in Options.Imports.Append(typeof(TGlobals).Namespace).OfType<string>().Distinct(StringComparer.Ordinal))
            text.Append("using ").Append(import).Append(";\n");
        var body = code.ToCharArray();
        foreach (var directive in script.Usings)
        {
            text.Append("#line ").Append(directive.GetLocation().GetLineSpan().StartLinePosition.Line + 1).Append(" \"").Append(path).Append("\"\n")
                .Append(directive.ToString()).Append('\n');
            for (var index = directive.Span.Start; index < directive.Span.End; index++) if (body[index] is not ('\r' or '\n')) body[index] = ' ';
        }
        var globals = TypeNames.Of(typeof(TGlobals));
        text.Append("#line default\n#pragma warning disable\nnamespace ").Append(GeneratedNamespace).Append("\n{\n")
            .Append("public sealed class ").Append(GeneratedClass).Append("\n{\n")
            .Append("    private readonly ").Append(globals).Append(" __globals;\n")
            .Append("    public ").Append(GeneratedClass).Append('(').Append(globals).Append(" globals) { __globals = globals; }\n");
        foreach (var property in typeof(TGlobals).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.GetIndexParameters().Length == 0))
        {
            text.Append("    private ").Append(TypeNames.Of(property.PropertyType)).Append(' ').Append(property.Name).Append(" { ");
            if (property.GetMethod?.IsPublic == true) text.Append("get => __globals.").Append(property.Name).Append("; ");
            if (property.SetMethod?.IsPublic == true && !IsInitOnly(property)) text.Append("set => __globals.").Append(property.Name).Append(" = value; ");
            text.Append("}\n");
        }
        text.Append("    public async global::System.Threading.Tasks.Task<").Append(TypeNames.Of(typeof(TResult))).Append("> RunAsync()\n    {\n")
            .Append("#line 1 \"").Append(path).Append("\"\n").Append(body).Append('\n')
            .Append("#line hidden\n        ").Append(fallback ?? "throw new global::System.InvalidOperationException(\"The script ended without returning a result.\");")
            .Append("\n    }\n}\n}\n");
        return text.ToString();
    }

    private static bool IsInitOnly(PropertyInfo property) =>
        property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit));

    /// <summary>Assembly files to reference: the host's, the options', and those the globals and result types need.</summary>
    private ImmutableArray<string> References(params Type[] types)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Options.UseHostReferences) paths.UnionWith(hostReferences.Value);
        foreach (var path in Options.ReferencePaths) paths.Add(Path.GetFullPath(path));
        var assemblies = new HashSet<Assembly>(Options.Assemblies) { typeof(object).Assembly, typeof(CSharpScriptHost).Assembly, typeof(Tools.AgentTool).Assembly };
        foreach (var type in types) Collect(type, assemblies);
        foreach (var assembly in assemblies.Where(assembly => !assembly.IsDynamic && assembly.Location.Length > 0)) paths.Add(assembly.Location);
        return [.. paths.Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static void Collect(Type type, HashSet<Assembly> assemblies)
    {
        if (!assemblies.Add(type.Assembly) && !type.IsGenericType) return;
        if (type.IsGenericType) foreach (var argument in type.GetGenericArguments()) Collect(argument, assemblies);
        if (type.HasElementType) Collect(type.GetElementType()!, assemblies);
    }

    private static MetadataReference Reference(string path) => references.GetOrAdd(path, file => MetadataReference.CreateFromFile(file));

    private string Key(string code, string path, string? fallback, Type globals, Type result, ImmutableArray<string> referenced)
    {
        var text = new StringBuilder()
            .Append("roslyn=").Append(typeof(CSharpCompilation).Assembly.GetName().Version).Append('\n')
            .Append("format=1\n")
            .Append("path=").Append(path).Append('\n')
            .Append("globals=").Append(globals.AssemblyQualifiedName).Append('|').Append(globals.Assembly.ManifestModule.ModuleVersionId).Append('\n')
            .Append("result=").Append(result.AssemblyQualifiedName).Append('|').Append(result.Assembly.ManifestModule.ModuleVersionId).Append('\n')
            .Append("fallback=").Append(fallback).Append('\n')
            .Append("language=").Append(Options.LanguageVersion).Append('\n')
            .Append("imports=").Append(string.Join(';', Options.Imports)).Append('\n')
            .Append("references=").Append(string.Join(';', referenced)).Append('\n')
            .Append("code=").Append(code);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private Image? ReadCache(string key)
    {
        if (Options.CacheDirectory is not { } directory) return null;
        var pe = Path.Combine(directory, key + ".dll");
        var pdb = Path.Combine(directory, key + ".pdb");
        try { return File.Exists(pe) && File.Exists(pdb) ? new Image(File.ReadAllBytes(pe), File.ReadAllBytes(pdb)) : null; }
        catch (IOException) { return null; }
    }

    private void WriteCache(string key, Image image)
    {
        if (Options.CacheDirectory is not { } directory) return;
        try
        {
            Directory.CreateDirectory(directory);
            Write(Path.Combine(directory, key + ".pdb"), image.Pdb);
            Write(Path.Combine(directory, key + ".dll"), image.Pe);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        static void Write(string path, byte[] bytes)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
    }

    private static ScriptDiagnostic At(string id, string message, SyntaxNode node)
    {
        var span = node.GetLocation().GetMappedLineSpan();
        return new ScriptDiagnostic(id, DiagnosticSeverity.Error, message, span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    private static ScriptDiagnostic Map(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetMappedLineSpan();
        return new ScriptDiagnostic(diagnostic.Id, diagnostic.Severity, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
            span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    internal sealed record Image(byte[] Pe, byte[] Pdb);

    internal static string GeneratedType => GeneratedNamespace + "." + GeneratedClass;
}

/// <summary>A compiled script, loaded in its own collectible context. Unload it once it is no longer used; calls still running finish
/// first.</summary>
public sealed class CompiledScript<TGlobals, TResult> : IAsyncDisposable where TGlobals : class
{
    private readonly Lock gate = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ScriptLoadContext? context;
    private ConstructorInfo? constructor;
    private MethodInfo? run;
    private int leases;
    private bool unloading;

    internal CompiledScript(string hash, string path, CSharpScriptHost.Image image, bool fromCache)
    {
        Hash = hash;
        Path = path;
        FromCache = fromCache;
        context = new ScriptLoadContext("PiSharp.Script:" + path + "#" + hash[..12]);
        var assembly = context.LoadFromStream(new MemoryStream(image.Pe), new MemoryStream(image.Pdb));
        var type = assembly.GetType(CSharpScriptHost.GeneratedType, throwOnError: true)!;
        constructor = type.GetConstructor([typeof(TGlobals)])!;
        run = type.GetMethod("RunAsync")!;
        Context = new WeakReference(context);
    }

    /// <summary>The cache key: the SHA-256 of what shaped the compiled image.</summary>
    public string Hash { get; }
    /// <summary>The path diagnostics and stack traces name.</summary>
    public string Path { get; }
    /// <summary>Whether the image came from the cache instead of the compiler.</summary>
    public bool FromCache { get; }
    /// <summary>The load context, weakly (tests check that an unloaded script is collected).</summary>
    internal WeakReference Context { get; }

    /// <summary>Runs the script over <paramref name="globals"/>. Exceptions the script throws propagate as they are.</summary>
    public async Task<TResult> RunAsync(TGlobals globals, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(globals);
        cancellationToken.ThrowIfCancellationRequested();
        ConstructorInfo create;
        MethodInfo method;
        lock (gate)
        {
            if (unloading) throw new ObjectDisposedException(Path, "The script was unloaded.");
            leases++;
            (create, method) = (constructor!, run!);
        }
        try
        {
            Task<TResult> pending;
            try { pending = (Task<TResult>)method.Invoke(create.Invoke([globals]), null)!; }
            catch (TargetInvocationException error) when (error.InnerException is { } inner)
            {
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
            return await pending.ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                leases--;
                if (unloading && leases == 0) drained.TrySetResult();
            }
        }
    }

    /// <summary>Stops new calls, waits up to <paramref name="drain"/> (default 30 s) for running ones, and unloads the context.</summary>
    public async Task UnloadAsync(TimeSpan? drain = null)
    {
        lock (gate)
        {
            if (unloading) return;
            unloading = true;
            if (leases == 0) drained.TrySetResult();
        }
        await Task.WhenAny(drained.Task, Task.Delay(drain ?? TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        ScriptLoadContext? unloaded;
        lock (gate)
        {
            (unloaded, context, constructor, run) = (context, null, null, null);
        }
        unloaded?.Unload();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => new(UnloadAsync());
}

/// <summary>A collectible context for one compiled script; dependencies resolve from the default context, so host types are shared.</summary>
internal sealed class ScriptLoadContext(string name) : AssemblyLoadContext(name, isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName) => null;
}

/// <summary>C# spellings of runtime types for generated code.</summary>
internal static class TypeNames
{
    public static string Of(Type type)
    {
        if (type.IsByRef || type.IsPointer || type.IsGenericParameter) throw new NotSupportedException($"Scripts cannot use type {type}.");
        if (type == typeof(void)) return "void";
        if (type.IsArray) return Of(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (Nullable.GetUnderlyingType(type) is { } underlying) return Of(underlying) + "?";
        if (!IsVisible(type)) throw new NotSupportedException($"Script globals and results must be public; {type} is not.");
        return Named(type, type.IsGenericType ? type.GetGenericArguments() : []);
    }

    private static string Named(Type type, Type[] arguments)
    {
        string prefix;
        var used = 0;
        if (type.DeclaringType is { } declaring)
        {
            used = declaring.IsGenericTypeDefinition ? declaring.GetGenericArguments().Length : 0;
            prefix = Named(used > 0 ? declaring.MakeGenericType(arguments[..used]) : declaring, arguments[..used]) + ".";
        }
        else prefix = "global::" + (string.IsNullOrEmpty(type.Namespace) ? "" : type.Namespace + ".");
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0) name = name[..tick];
        var own = arguments[used..];
        return prefix + name + (own.Length > 0 ? "<" + string.Join(", ", own.Select(Of)) + ">" : "");
    }

    private static bool IsVisible(Type type) =>
        (type.IsPublic || type.IsNestedPublic && IsVisible(type.DeclaringType!)) && (!type.IsGenericType || type.GetGenericArguments().All(argument => argument.IsGenericParameter || IsVisible(argument)));
}
