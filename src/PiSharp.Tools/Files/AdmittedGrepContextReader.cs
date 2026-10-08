namespace PiSharp.Tools.Files;

/// <summary>Per-read host admission of the exact canonical path and byte budget. The callback directly
/// joins its original work before returning or throwing; registration is not a read grant.</summary>
public delegate ValueTask<bool> GrepContextReadAdmission(string canonicalPath, int maximumBytes,
    CancellationToken cancellationToken);

/// <summary>Explicitly borrowed file operations and admission, restricted to a supplied root. No ambient
/// operations, implicit grant, executable acquisition or host-resource disposal. This is path containment,
/// not atomic handle identity or a snapshot shared with the completed search.</summary>
public sealed class AdmittedGrepContextReader : IGrepContextReader
{
    private readonly string _root;
    private readonly IFileOperations _operations;
    private readonly GrepContextReadAdmission _admission;

    public AdmittedGrepContextReader(string admittedRoot, IFileOperations operations, GrepContextReadAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(admission);
        _root = Absolute(admittedRoot);
        _operations = operations;
        _admission = admission;
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(string absolutePath, int maximumBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumBytes is < 1 or > GrepTool.MaximumContextFileBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var path = Absolute(absolutePath);
        if (!GrepTool.Within(_root, path)) throw new UnauthorizedAccessException("Grep context path is outside its admitted root.");
        var root = Absolute(await _operations.CanonicalizeAsync(_root, cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(root, _root, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Grep context root is not the supplied canonical root.");
        var canonical = Absolute(await _operations.CanonicalizeAsync(path, cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        if (!GrepTool.Within(root, canonical)) throw new UnauthorizedAccessException("Grep context link is outside its admitted root.");
        var admitted = await _admission(canonical, maximumBytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!admitted) throw new UnauthorizedAccessException("Grep context read was not admitted.");
        // Admission may await host work. Recheck the exact granted path before acquiring bytes.
        var current = Absolute(await _operations.CanonicalizeAsync(canonical, cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(current, canonical, StringComparison.Ordinal) || !GrepTool.Within(root, current))
            throw new UnauthorizedAccessException("Grep context path changed after read admission.");
        var bytes = await _operations.ReadAsync(canonical, maximumBytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > maximumBytes) throw new FileToolException(FileToolFailure.ResourceLimit);
        return bytes;
    }

    private static string Absolute(string path)
    {
        if (!GrepTool.SearchText(path, 4096) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute bounded grep context path is required.", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
