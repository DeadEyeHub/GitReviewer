namespace GitReviewer.Models;

public sealed class ModelProfile
{
    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyEnvironment { get; set; } = string.Empty;

    public ModelProfile Clone() => (ModelProfile)MemberwiseClone();
}

public sealed class ModelsConfiguration
{
    public string ActiveProfile { get; set; } = string.Empty;
    public List<ModelProfile> Profiles { get; } = [];
}
