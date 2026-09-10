namespace HyPanel.Agent.Backends.Infrastructure;

using System.Diagnostics.CodeAnalysis;
/// <summary>Lookup for the statically linked backend providers registered with DI.</summary>
public sealed class BackendProviderRegistry
{
    private readonly Dictionary<string, IBackendProvider> providers;

    public BackendProviderRegistry(IEnumerable<IBackendProvider> providers)
    {
        this.providers = new Dictionary<string, IBackendProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (string.IsNullOrWhiteSpace(provider.BackendType) || !this.providers.TryAdd(provider.BackendType, provider))
            {
                throw new InvalidOperationException($"Backend provider type '{provider.BackendType}' is invalid or registered more than once.");
            }
        }
    }

    public IEnumerable<IBackendProvider> Providers => providers.Values;

    public bool TryGet(string backendType, [NotNullWhen(true)] out IBackendProvider? provider) =>
        providers.TryGetValue(backendType, out provider);

    public IBackendProvider GetRequired(string backendType) =>
        TryGet(backendType, out var provider)
            ? provider
            : throw new KeyNotFoundException($"No provider is registered for backend type '{backendType}'.");
}
