namespace RuleVault.Storage;

public sealed class StorageFormatException : Exception
{
    public StorageFormatException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
