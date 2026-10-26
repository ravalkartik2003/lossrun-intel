using Azure.Core;
using Azure.Identity;

namespace LossRun.Ingestion;

public static class AzureCredentials
{
    /// <summary>
    /// DefaultAzureCredential (keyless). Off Azure (no IDENTITY_ENDPOINT/MSI_ENDPOINT, e.g. a developer laptop) the
    /// managed-identity probe is excluded: with Azure.Identity 1.21 a failed IMDS probe ends the chain instead of
    /// falling through to the Azure CLI / Visual Studio credentials. In Functions/App Service the identity endpoint
    /// variables exist and managed identity is used as usual.
    /// </summary>
    public static TokenCredential Default()
    {
        var onAzure = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT") is not null || Environment.GetEnvironmentVariable("MSI_ENDPOINT") is not null;
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeManagedIdentityCredential = !onAzure });
    }
}
