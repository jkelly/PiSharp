using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PiSharp.Extensions.Runtime.Loading;

/// <summary>One approved supervisor import. This is not a general native dependency resolver or an OS sandbox.</summary>
internal sealed class PluginSystemImportProfile : IDisposable
{
    private const string Profile = "windows-worker-directory-0";
    private const string Artifact = "PiSharp.ExtensionHost.dll";
    private const string Library = "kernel32.dll";
    private readonly object gate = new();
    private readonly string owner;
    private Assembly? attached;
    private nint handle;
    private bool closed;

    private PluginSystemImportProfile(string owner) => this.owner = owner;

    internal static PluginSystemImportProfile? Validate(string owner, ValidatedPublishedPackage package,
        ImmutableArray<PluginSystemImportApproval> approvals)
    {
        if (approvals.IsDefault || approvals.Length > 1) throw Rejected(owner, "system-import-approval");
        if (approvals.IsEmpty)
        {
            // This fixed supervisor artifact has an audited import profile; its use requires a separate grant.
            // Unrelated packages retain the default rejection on native use, without a name-based allowance.
            if (package.Images.TryGetValue("PiSharp.ExtensionHost", out var supervisor) &&
                supervisor.Artifact.RelativePath == Artifact && ContainsNativeImports(supervisor))
                throw Rejected(owner, "system-import-approval-required");
            return null;
        }
        var approval = approvals[0];
        if (approval is null || approval.Profile != Profile || approval.Artifact != Artifact ||
            !package.Images.TryGetValue("PiSharp.ExtensionHost", out var image) || image.Artifact.RelativePath != Artifact ||
            !PublishedPackageValidator.Exact(image.Identity, new AssemblyName("PiSharp.ExtensionHost, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")) ||
            approval.Sha256 != Convert.ToHexStringLower(SHA256.HashData(image.Artifact.Bytes)))
            throw Rejected(owner, "system-import-approval");
        // Inspect held, approved bytes before any entry/module/constructor may execute.
        using var pe = new PEReader(new MemoryStream(image.Artifact.Bytes, writable: false));
        var metadata = pe.GetMetadataReader(); var count = 0;
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0) continue;
                count++;
                if (count > 1 || metadata.GetString(type.Namespace) != "PiSharp.ExtensionHost.Supervision" ||
                    metadata.GetString(type.Name) != "NodeWorkerLaunch" || metadata.GetString(method.Name) != "CreateDirectoryW" ||
                    (method.Attributes & (MethodAttributes.MemberAccessMask | MethodAttributes.Static)) !=
                        (MethodAttributes.Private | MethodAttributes.Static) ||
                    (method.ImplAttributes & MethodImplAttributes.PreserveSig) == 0)
                    throw Rejected(owner, "system-import-metadata");
                var import = method.GetImport();
                var expected = MethodImportAttributes.ExactSpelling | MethodImportAttributes.CharSetUnicode |
                    MethodImportAttributes.SetLastError | MethodImportAttributes.CallingConventionWinApi;
                if (import.Module.IsNil || metadata.GetString(metadata.GetModuleReference(import.Module).Name) != Library ||
                    metadata.GetString(import.Name) != "CreateDirectoryW" || import.Attributes != expected)
                    throw Rejected(owner, "system-import-metadata");
                var signature = metadata.GetBlobReader(method.Signature);
                // Exact static default-callconv bool(string, native int), without generic/modifier/vararg alternatives.
                if (signature.ReadByte() != 0 || signature.ReadCompressedInteger() != 2 || signature.ReadByte() != 0x02 ||
                    signature.ReadByte() != 0x0e || signature.ReadByte() != 0x18 || signature.RemainingBytes != 0)
                    throw Rejected(owner, "system-import-signature");
                var parameters = method.GetParameters().Select(metadata.GetParameter).ToArray();
                var returned = parameters.Where(parameter => parameter.SequenceNumber == 0).ToArray();
                if (returned.Length != 1 || returned[0].GetMarshallingDescriptor().IsNil ||
                    !metadata.GetBlobBytes(returned[0].GetMarshallingDescriptor()).SequenceEqual(new byte[] { 0x02 }) ||
                    parameters.Any(parameter => parameter.SequenceNumber != 0 && !parameter.GetMarshallingDescriptor().IsNil))
                    throw Rejected(owner, "system-import-marshalling");
            }
        }
        if (count != 1) throw Rejected(owner, "system-import-metadata");
        return new(owner);
    }

    private static bool ContainsNativeImports(ManagedSnapshotImage image)
    {
        using var pe = new PEReader(new MemoryStream(image.Artifact.Bytes, writable: false));
        var metadata = pe.GetMetadataReader();
        return metadata.MethodDefinitions.Any(handle =>
            (metadata.GetMethodDefinition(handle).Attributes & MethodAttributes.PinvokeImpl) != 0);
    }

    internal void Attach(Assembly assembly, ManagedSnapshotImage image)
    {
        if (image.Artifact.RelativePath != Artifact) return;
        lock (gate)
        {
            if (closed || attached is not null) throw Rejected(owner, "system-import-attach");
            attached = assembly;
            try { NativeLibrary.SetDllImportResolver(assembly, Resolve); }
            catch { attached = null; throw; }
        }
    }

    private nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        lock (gate)
        {
            // A zero result would fall back to caller-controlled/default search. Never return it.
            if (closed || !ReferenceEquals(assembly, attached) || name != Library)
                throw Rejected(owner, "resolve-system-import");
            if (handle != 0) return handle;
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                throw Rejected(owner, "system-import-platform");
            var absolute = Path.Combine(Environment.SystemDirectory, Library);
            if (!Path.IsPathFullyQualified(absolute)) throw Rejected(owner, "system-import-platform");
            var loaded = NativeLibrary.Load(absolute);
            try { _ = NativeLibrary.GetExport(loaded, "CreateDirectoryW"); }
            catch { NativeLibrary.Free(loaded); throw; }
            handle = loaded;
            return handle;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (closed) return;
            // Caller first joins registrations, plugin disposal and supervised resources. If release fails,
            // retain this handle/context/snapshot through the loader's existing failed-cleanup reservation.
            if (handle != 0) NativeLibrary.Free(handle);
            handle = 0; attached = null; closed = true;
        }
    }
    private static PluginLoadException Rejected(string owner, string operation) =>
        new(PluginLoadFailure.UnsupportedNativeDependency, owner, operation);
}
