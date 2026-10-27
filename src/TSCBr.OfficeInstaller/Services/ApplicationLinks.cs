namespace TSCBr.OfficeInstaller.Services;

public static class ApplicationLinks
{
    public static Uri Website { get; } = new("https://www.tscbr.com.br/");
    public static Uri LinkedIn { get; } = new("https://www.linkedin.com/in/estev%C3%A3o-miguel-a22057171/");
    public static Uri GitHub { get; } = new("https://github.com/dev-estevao-miguel");
    // Official Microsoft documentation only. Activation requires a valid license (KMS, MAK or Active Directory).
    public static Uri OfficialActivationGuide { get; } = new("https://learn.microsoft.com/office/volume-license-activation/plan-volume-activation-of-office");
}
