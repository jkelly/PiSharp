// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/registry.ts. Installs and subscriptions may come from any
// thread, so the current state and the listener list are guarded by a short lock; listeners run outside it, synchronously, after the new
// state is published.
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable;

/// <summary>Application-owned registries of extensions (upstream <c>createRegistry</c> and <c>BUILTIN_TASKS</c>).</summary>
public static partial class Registry
{
    /// <summary>Built-in task definitions every registry holds; they are not an extension and cannot be removed or replaced (upstream
    /// <c>BUILTIN_TASKS</c>).</summary>
    public static ImmutableArray<DurableTask> BuiltinTasks { get; } = [HarnessTasks.GenerationTask, HarnessTasks.ToolTask, HarnessTasks.CompactionTask];

    /// <summary>Creates an application-owned registry holding only the built-in tasks.</summary>
    public static IRegistry Create() => new RegistryImpl();

    [GeneratedRegex("^[a-z][a-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionKey();

    private const string SectionKeyPattern = "/^[a-z][a-z0-9_-]*$/";

    /// <summary>Unique tool names and section keys within one extension; valid, unreserved section keys.</summary>
    private static void ValidateExtension(Extension extension)
    {
        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in extension.Tools ?? [])
            if (!tools.Add(tool.Name)) throw new InvalidOperationException($"Extension {extension.Name} has two tools named {tool.Name}");
        var sections = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in extension.Sections ?? [])
        {
            var key = section.Key;
            if (!SectionKey().IsMatch(key)) throw new ArgumentException($"Section key {JsonValue.String(key).ToJson()} must match {SectionKeyPattern}");
            if (key == Agents.InstructionsKey) throw new InvalidOperationException($"Section key {key} is reserved for the agent's instructions");
            if (!sections.Add(key)) throw new InvalidOperationException($"Extension {extension.Name} has two sections with key {key}");
        }
    }

    /// <summary>Immutable published registry state.</summary>
    private sealed class RegistryState : IRegistrySnapshot
    {
        private readonly ImmutableArray<Extension> extensions;
        private readonly Dictionary<string, Extension> byName = new(StringComparer.Ordinal);
        private readonly OrderedDictionary<string, DurableTask> tasks = new(StringComparer.Ordinal);

        public RegistryState(ImmutableArray<Extension> extensions)
        {
            this.extensions = extensions;
            foreach (var extension in extensions) byName[extension.Name] = extension;
            foreach (var task in BuiltinTasks) tasks[task.Definition.Name] = task;
            foreach (var extension in extensions)
            {
                foreach (var task in extension.Tasks ?? [])
                {
                    var name = task.Definition.Name;
                    if (tasks.ContainsKey(name)) throw new InvalidOperationException($"Task {name} of extension {extension.Name} is already installed");
                    tasks[name] = task;
                }
            }
        }

        public ImmutableArray<Extension> Extensions => extensions;
        public IReadOnlyList<Extension> Installed() => extensions;
        public Extension? GetExtension(string name) => byName.GetValueOrDefault(name);
        public IReadOnlyList<RegisteredTool> Tools() =>
            [.. extensions.SelectMany(extension => (extension.Tools ?? []).Select(tool => new RegisteredTool(extension, tool)))];
        public IReadOnlyList<RegisteredSection> Sections() =>
            [.. extensions.SelectMany(extension => (extension.Sections ?? []).Select(section => new RegisteredSection(extension, section)))];
        public IReadOnlyList<DurableTask> Tasks() => [.. tasks.Values];
        public DurableTask? GetTask(string name) => tasks.GetValueOrDefault(name);
    }

    private sealed class RegistryImpl : IRegistry
    {
        private readonly Lock gate = new();
        private RegistryState current = new([]);
        private readonly List<Action> listeners = [];

        public IRegistrySnapshot Snapshot()
        {
            lock (gate) return current;
        }

        public IDisposable Subscribe(Action listener)
        {
            ArgumentNullException.ThrowIfNull(listener);
            // A distinct entry per subscription, so two subscriptions of one delegate are independent.
            Action entry = () => listener();
            lock (gate) listeners.Add(entry);
            return new Unsubscriber(() =>
            {
                lock (gate) listeners.Remove(entry);
            });
        }

        public void Install(Extension extension)
        {
            ArgumentNullException.ThrowIfNull(extension);
            ValidateExtension(extension);
            Publish(installed =>
            {
                var index = installed.IndexOf(installed.FirstOrDefault(item => item.Name == extension.Name)!);
                return index < 0 ? installed.Add(extension) : installed.SetItem(index, extension);
            });
        }

        public void Uninstall(Extension extension)
        {
            ArgumentNullException.ThrowIfNull(extension);
            Publish(installed => installed.Any(item => item.Name == extension.Name) ? installed.RemoveAll(item => item.Name == extension.Name) : installed);
        }

        /// <summary>Builds and validates the next state, which throws on a task name collision, then publishes it synchronously.</summary>
        private void Publish(Func<ImmutableArray<Extension>, ImmutableArray<Extension>> next)
        {
            Action[] notify;
            lock (gate)
            {
                var installed = current.Extensions;
                var extensions = next(installed);
                if (extensions == installed) return;
                current = new RegistryState(extensions);
                notify = [.. listeners];
            }
            foreach (var listener in notify) listener();
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        private Action? dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref dispose, null)?.Invoke();
    }
}
