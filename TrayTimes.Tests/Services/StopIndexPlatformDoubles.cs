namespace Windows.Storage;

internal sealed class ApplicationData
{
    public static ApplicationData Current { get; } = new();

    public StorageFolder LocalFolder { get; } = new();
}

internal sealed class StorageFolder
{
    public string Path { get; set; } = string.Empty;
}
