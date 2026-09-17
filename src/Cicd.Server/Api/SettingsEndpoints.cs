using System.Security.Claims;
using Cicd.Contracts.Api;
using Cicd.Core.Settings;

namespace Cicd.Server.Api;

public static class SettingsEndpoints
{
    internal static void MapSettings(RouteGroupBuilder group)
    {
        group.MapGet("/", async Task<IResult> (SettingsService settings, CancellationToken ct) =>
            Results.Ok((await settings.GetAllAsync(ct)).Select(ToDto).ToList()));

        group.MapPut("/", async Task<IResult> (UpdateSettingsRequest request, SettingsService settings, ClaimsPrincipal caller, CancellationToken ct) =>
        {
            if (request.Values is null || request.Values.Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = ["Provide at least one value."] });
            }
            var errors = SettingsService.Validate(request.Values);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = [.. errors] });
            }
            try
            {
                await settings.UpdateAsync(request.Values, caller.Identity?.Name ?? "api-token", ct);
            }
            catch (SettingsValidationException ex)
            {
                // Rejected before any write, including the cross-field rules the per-key Validate above cannot see.
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = [ex.Message] });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
            return Results.NoContent();
        });
    }

    private static SettingDto ToDto(SettingView view) => new(
        view.Definition.Key, view.Definition.Section, view.Definition.DisplayName, view.Definition.Description,
        view.Definition.Kind.ToString(), view.Definition.RestartRequired, view.Value, view.IsSet, view.RestartPending, view.Unreadable);
}
