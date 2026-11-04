namespace CMS_CSharp.Features.Auth;

internal static class PermissionCodes
{
    public const string AccountsView = "accounts.view";
    public const string AccountsCreate = "accounts.create";
    public const string RolesView = "roles.view";
    public const string EventsView = "events.view";
    public const string EventsCreate = "events.create";
    public const string ClaimsView = "claims.view";
    public const string ClaimsEdit = "claims.edit";
    public const string ClaimsDelete = "claims.delete";
    public const string ClaimsExport = "claims.export";
    public const string PromotionsView = "promotions.view";
    public const string PromotionsCreate = "promotions.create";
    public const string PromotionsEdit = "promotions.edit";
    public const string PromotionsDelete = "promotions.delete";
    public const string DevicesView = "devices.view";
    public const string DevicesCreate = "devices.create";
    public const string DevicesEdit = "devices.edit";

    public static readonly IReadOnlyList<string> All =
    [
        AccountsView,
        AccountsCreate,
        RolesView,
        EventsView,
        EventsCreate,
        ClaimsView,
        ClaimsEdit,
        ClaimsDelete,
        ClaimsExport,
        PromotionsView,
        PromotionsCreate,
        PromotionsEdit,
        PromotionsDelete,
        DevicesView,
        DevicesCreate,
        DevicesEdit
    ];
}
