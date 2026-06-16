namespace Ccusage.Api.Auth;

/// <summary>
/// Endpoint filter that re-checks the database on every request: the user must exist,
/// be enabled, and hold at least one of the required permissions. Pair with
/// <c>.RequireAuthorization()</c> (the JWT proves identity; this enforces authorization).
/// </summary>
public sealed class PermissionFilter : IEndpointFilter
{
    private readonly string[] _permissions;

    /// <param name="permissions">
    /// The accepted permission keys. The request is allowed if the user holds ANY of them
    /// (a single key behaves as a plain "require this permission").
    /// </param>
    public PermissionFilter(params string[] permissions) => _permissions = permissions;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var accessor = context.HttpContext.RequestServices.GetRequiredService<CurrentUserAccessor>();
        var user = await accessor.GetUserAsync(context.HttpContext.RequestAborted);

        if (user is null || !user.IsEnabled)
            return Results.Json(new { message = "Your account is not provisioned or has been disabled." },
                statusCode: StatusCodes.Status403Forbidden);

        if (!_permissions.Any(user.Permissions.Contains))
            return Results.Json(new { message = $"You don't have permission: {string.Join(" or ", _permissions)}" },
                statusCode: StatusCodes.Status403Forbidden);

        return await next(context);
    }
}

public static class PermissionFilterExtensions
{
    /// <summary>Require a specific permission (re-checked against the DB each request).</summary>
    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission) =>
        builder.AddEndpointFilter(new PermissionFilter(permission));

    public static RouteGroupBuilder RequirePermission(this RouteGroupBuilder builder, string permission) =>
        builder.AddEndpointFilter(new PermissionFilter(permission));

    /// <summary>Require ANY one of the given permissions (re-checked against the DB each request).</summary>
    public static RouteHandlerBuilder RequireAnyPermission(this RouteHandlerBuilder builder, params string[] permissions) =>
        builder.AddEndpointFilter(new PermissionFilter(permissions));

    public static RouteGroupBuilder RequireAnyPermission(this RouteGroupBuilder builder, params string[] permissions) =>
        builder.AddEndpointFilter(new PermissionFilter(permissions));
}
