namespace HyPanel.Agent;

using System.Text.Json;

public sealed class AgentCredentialStore(AgentEnrollmentOptions options, ILogger<AgentCredentialStore> logger)
{
    private const string CredentialsFileName = "credentials.json";

    public string CredentialsPath => Path.Combine(options.DataDirectory, CredentialsFileName);

    public async Task<AgentCredentials?> TryLoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(CredentialsPath))
        {
            return null;
        }

        var credentialBytes = await File.ReadAllBytesAsync(CredentialsPath, cancellationToken);
        var credentials = JsonSerializer.Deserialize(
            credentialBytes,
            AgentJsonSerializerContext.Default.AgentCredentials);

        return credentials ?? throw new InvalidOperationException("The Agent credential file is invalid.");
    }

    public async Task SaveAsync(AgentCredentials credentials, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(options.DataDirectory);

        var temporaryPath = Path.Combine(
            options.DataDirectory,
            $".{CredentialsFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            var credentialBytes = JsonSerializer.SerializeToUtf8Bytes(
                credentials,
                AgentJsonSerializerContext.Default.AgentCredentials);

            await File.WriteAllBytesAsync(temporaryPath, credentialBytes, cancellationToken);
            SetOwnerOnlyPermissions(temporaryPath, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, CredentialsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void SetOwnerOnlyPermissions(string path, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            logger.LogWarning("Could not restrict Agent credential file permissions to the owner.");
        }
    }
}
