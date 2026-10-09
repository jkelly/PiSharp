namespace PiSharp.Cli.Extensions.Pi;

/// <summary>An extension entry point the package manager resolved (or a <c>-e</c> path), with its scope (<c>user</c>, <c>project</c>
/// or <c>temporary</c>) and source (<c>local</c>, <c>auto</c>, <c>cli</c>, a package source).</summary>
internal sealed record PiExtensionSource(string Path, string Scope, string Source);
