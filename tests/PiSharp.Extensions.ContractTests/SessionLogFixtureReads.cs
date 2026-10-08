internal static class SessionLogFixtureReads
{
    internal static async Task<byte[]> ReadAllBytesAsync(string path)
    {
        // A settled operation leaves the session writer open. The inspection reader must share writes.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var bytes = new MemoryStream();
        await file.CopyToAsync(bytes);
        return bytes.ToArray();
    }
}
