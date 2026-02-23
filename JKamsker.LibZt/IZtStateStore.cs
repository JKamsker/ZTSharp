using System.Collections.Generic;
using System.Threading.Tasks;

namespace JKamsker.LibZt;

/// <summary>
/// Storage abstraction for node identity, network definitions, peers, and roots.
/// </summary>
public interface IZtStateStore
{
    /// <summary>Returns true when a logical key exists.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Reads raw bytes for a logical key or returns null if missing.</summary>
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Writes raw bytes for a logical key.</summary>
    Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken = default);

    /// <summary>Deletes a logical key and returns true when something was deleted.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Enumerates known keys under an optional prefix.</summary>
    Task<IReadOnlyList<string>> ListAsync(string prefix = "", CancellationToken cancellationToken = default);

    /// <summary>Flushes pending durable state.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
