namespace BrimDeck.Core;

public interface IProviderSecrets
{
    string Read(Guid instanceId);
    void Write(Guid instanceId, string secret);
    void Delete(Guid instanceId);
}

public sealed record ProviderTestResult(ProviderSnapshot Snapshot, double Seconds, IReadOnlyList<string> Logs);
