namespace AIClient.Infrastructure.Providers;

/// <summary>
/// Base URLs for each provider, bound from configuration.
/// </summary>
/// <remarks>
/// Section 9 asks that the endpoint and payload format be changeable without touching the
/// UI. The format lives in the provider class; the endpoint lives here, so pointing NVIDIA
/// at a self-hosted NIM container or an on-prem deployment is a configuration edit.
/// </remarks>
public sealed class ProviderEndpointOptions
{
    public const string SectionName = "Providers";

    /// <summary>NVIDIA's hosted NIM API. Overridable for self-hosted NIM.</summary>
    public string Nvidia { get; set; } = "https://integrate.api.nvidia.com/v1";

    /// <summary>Request timeout for catalogue and non-streaming calls.</summary>
    public int RequestTimeoutSeconds { get; set; } = 100;

    /// <summary>
    /// Timeout for a streaming chat call, which must outlast a slow model rather than a slow
    /// network. Long-form answers from a reasoning model routinely run past two minutes.
    /// </summary>
    public int StreamTimeoutSeconds { get; set; } = 600;
}
