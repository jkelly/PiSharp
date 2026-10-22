// Decision 0011 (run W2, WU4): the Web Agent's settings as typed sections, each stored as one JSON document in webagent.db (table settings,
// migration 300). A section is a record that names itself and its defaults and checks its own values; a missing or unreadable section
// reads as its defaults. Every save raises Changed, so a running service (the device host's working directory, the approval extension's
// mode) follows the pages without a restart. The M0 sections are Defaults, Tools and Models (SettingsSections.cs); later areas add their own.
namespace PiSharp.WebAgent.Contracts;

/// <summary>A settings section: a record with a stable name, defaults and validation.</summary>
/// <typeparam name="TSelf">The section type itself.</typeparam>
public interface ISettingsSection<TSelf> where TSelf : class, ISettingsSection<TSelf>
{
    /// <summary>The section's key in webagent.db, such as <c>defaults</c>. It never changes once shipped.</summary>
    static abstract string SectionName { get; }

    /// <summary>The section's values when nothing is stored.</summary>
    static abstract TSelf CreateDefault();

    /// <summary>The same values, trimmed and with missing ones filled in; the store saves and returns this.</summary>
    /// <exception cref="ArgumentException">A value is not allowed; the message says which, for the page to show.</exception>
    TSelf Normalize();
}

/// <summary>Typed settings sections in webagent.db. A singleton; safe to call from many threads.</summary>
public interface ISettingsStore
{
    /// <summary>The stored <typeparamref name="T"/> section, or its defaults when none is stored or the stored JSON cannot be read.</summary>
    T Get<T>() where T : class, ISettingsSection<T>;

    /// <summary>Stores <paramref name="value"/> (normalized), raises <see cref="Changed"/> and returns what was stored.</summary>
    /// <exception cref="ArgumentException">A value is not allowed (<see cref="ISettingsSection{TSelf}.Normalize"/>); nothing is stored.</exception>
    Task<T> SaveAsync<T>(T value, CancellationToken cancellationToken = default) where T : class, ISettingsSection<T>;

    /// <summary>Changes the stored section with <paramref name="change"/>, atomically with respect to other saves, and stores the result as
    /// <see cref="SaveAsync{T}"/> does.</summary>
    Task<T> UpdateAsync<T>(Func<T, T> change, CancellationToken cancellationToken = default) where T : class, ISettingsSection<T>;

    /// <summary>Removes the stored section, so it reads as its defaults again, and raises <see cref="Changed"/>.</summary>
    Task<T> ResetAsync<T>(CancellationToken cancellationToken = default) where T : class, ISettingsSection<T>;

    /// <summary>Raised after a section is saved or reset, outside any lock, with the section's new value.</summary>
    event EventHandler<SettingsChangedEventArgs>? Changed;
}

/// <summary>A saved or reset settings section.</summary>
/// <param name="section">The section's <see cref="ISettingsSection{TSelf}.SectionName"/>.</param>
/// <param name="value">The section's new value.</param>
public sealed class SettingsChangedEventArgs(string section, object value) : EventArgs
{
    /// <summary>The section's name.</summary>
    public string Section { get; } = section;

    /// <summary>The section's new value (an instance of the section type).</summary>
    public object Value { get; } = value;
}

/// <summary>Typed change subscriptions.</summary>
public static class SettingsStoreExtensions
{
    /// <summary>Calls <paramref name="changed"/> with each new value of section <typeparamref name="T"/> until the result is disposed.</summary>
    public static IDisposable OnChanged<T>(this ISettingsStore store, Action<T> changed) where T : class, ISettingsSection<T>
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(changed);
        EventHandler<SettingsChangedEventArgs> handler = (_, e) => { if (e.Section == T.SectionName && e.Value is T value) changed(value); };
        store.Changed += handler;
        return new Subscription(() => store.Changed -= handler);
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}
